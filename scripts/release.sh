#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# release.sh — merge current branch to main, bump version, publish to NuGet
#
# Usage:
#   ./scripts/release.sh <version>
#
# Example:
#   ./scripts/release.sh 1.1.0
#
# Required env vars:
#   NUGET_API_KEY — NuGet.org API key
#
# What it does:
#   1. Validates inputs and prerequisites
#   2. Runs dotnet test — nothing ships if tests are red
#   3. Bumps <Version> in ContextManager.Mcp.csproj
#   4. Commits the version bump on the current branch
#   5. Merges current branch into main (fast-forward when possible)
#   6. Tags the merge commit as v<version>
#   7. Pushes main + the tag
#   8. Packs and pushes to NuGet.org
# ---------------------------------------------------------------------------

CSPROJ="src/ContextManager.Mcp/ContextManager.Mcp.csproj"
NUGET_SOURCE="https://api.nuget.org/v3/index.json"

# ── helpers ──────────────────────────────────────────────────────────────────
red()   { echo -e "\033[0;31m$*\033[0m"; }
green() { echo -e "\033[0;32m$*\033[0m"; }
bold()  { echo -e "\033[1m$*\033[0m"; }

die() { red "ERROR: $*" >&2; exit 1; }

# ── 1. validate inputs ───────────────────────────────────────────────────────
[[ $# -eq 1 ]] || die "Usage: $0 <version>  (e.g. 1.1.0)"

VERSION="$1"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Version must be semver: X.Y.Z"

[[ -n "${NUGET_API_KEY:-}" ]] || die "NUGET_API_KEY env var is not set"

command -v dotnet >/dev/null || die "dotnet not found — install .NET 8 SDK"
command -v git    >/dev/null || die "git not found"

# ── 2. must be in repo root ──────────────────────────────────────────────────
[[ -f "$CSPROJ" ]] || die "Run this script from the repository root"

CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
[[ "$CURRENT_BRANCH" != "main" ]] || die "Already on main — run this from your feature/dev branch"

bold "Releasing v${VERSION} from branch '${CURRENT_BRANCH}' → main"

# check working tree is clean (version bump aside)
[[ -z "$(git status --porcelain)" ]] || die "Working tree is dirty — commit or stash changes first"

# ── 3. run tests ─────────────────────────────────────────────────────────────
bold "\n▶ Running tests..."
dotnet test --no-restore -c Release --logger "console;verbosity=minimal" \
  || die "Tests failed — fix them before releasing"
green "✓ All tests passed"

# ── 4. bump version in csproj ────────────────────────────────────────────────
bold "\n▶ Bumping version to ${VERSION}..."
sed -i "s|<Version>.*</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
grep "<Version>" "$CSPROJ"  # confirm

git add "$CSPROJ"
git commit -m "chore(release): bump version to ${VERSION}"
green "✓ Version bumped and committed"

# ── 5. merge to main ─────────────────────────────────────────────────────────
bold "\n▶ Merging '${CURRENT_BRANCH}' into main..."
git checkout main
git pull --ff-only origin main  || die "main is not up-to-date with origin — pull first"
git merge --no-ff "$CURRENT_BRANCH" -m "chore(release): merge ${CURRENT_BRANCH} into main for v${VERSION}"
green "✓ Merged"

# ── 6. tag ───────────────────────────────────────────────────────────────────
TAG="v${VERSION}"
git tag -a "$TAG" -m "Release ${TAG}"
green "✓ Tagged ${TAG}"

# ── 7. push main + tag ───────────────────────────────────────────────────────
bold "\n▶ Pushing main and tag ${TAG}..."
git push origin main
git push origin "$TAG"
green "✓ Pushed"

# ── 8. pack and publish ──────────────────────────────────────────────────────
bold "\n▶ Packing..."
dotnet pack "$CSPROJ" -c Release --no-build -o ./nupkgs

NUPKG=$(ls ./nupkgs/ContextManager.${VERSION}.nupkg 2>/dev/null || ls ./nupkgs/ContextManager.*.nupkg | head -1)
[[ -f "$NUPKG" ]] || die "nupkg not found after dotnet pack"

bold "\n▶ Publishing ${NUPKG} to NuGet.org..."
dotnet nuget push "$NUPKG" \
  --api-key "$NUGET_API_KEY" \
  --source "$NUGET_SOURCE" \
  --skip-duplicate

green "\n✓ Published ContextManager ${VERSION} to NuGet.org"
bold "  Install with: dotnet tool install -g ContextManager"
bold "  Update with:  dotnet tool update -g ContextManager"

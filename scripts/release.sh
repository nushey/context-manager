#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# release.sh — publish a release: <branch> → main → NuGet.org → back to <branch>
#
# Usage:
#   ./scripts/release.sh <version>
#
# Example:
#   ./scripts/release.sh 1.2.0
#
# Required env vars:
#   NUGET_API_KEY — NuGet.org API key
#
# Design: every validation that can fail runs BEFORE anything mutates state.
# The only irreversible step (nuget push) happens after the package is built
# and verified, and before any git ref reaches origin — so a failed publish
# leaves nothing public to clean up.
#
# Phases:
#   A. Preflight  — inputs, tooling, remote reachability, tag/version/branch state
#   B. Verify     — restore, build, test on the release branch
#   C. Bump       — write <Version>, commit on the release branch
#   D. Merge      — main fast-forwarded to origin, then --no-ff merge of the branch
#   E. Package    — rebuild the MERGED tree, test it, pack, verify the exact nupkg
#   F. Tag        — annotated v<version> on the merge commit
#   G. Publish    — dotnet nuget push (point of no return)
#   H. Propagate  — push main + tag, fast-forward the branch onto main, push it
# ---------------------------------------------------------------------------

CSPROJ="src/ContextManager.Mcp/ContextManager.Mcp.csproj"
PACKAGE_ID="ContextManager"
NUGET_SOURCE="https://api.nuget.org/v3/index.json"
REMOTE="origin"
PACK_DIR="./nupkgs"

# ── helpers ──────────────────────────────────────────────────────────────────
red()   { echo -e "\033[0;31m$*\033[0m"; }
green() { echo -e "\033[0;32m$*\033[0m"; }
bold()  { echo -e "\033[1m$*\033[0m"; }

die() { red "ERROR: $*" >&2; exit 1; }

# Rollback state — updated as the script advances so the trap knows how far it got.
BRANCH_START_SHA=""
MAIN_START_SHA=""
TAG=""
PUBLISHED=0
PUSHED=0
RELEASE_BRANCH=""

cleanup() {
  local code=$?
  [[ $code -eq 0 ]] && return 0

  red "\n✗ Release aborted (exit ${code})."

  if [[ $PUSHED -eq 1 ]]; then
    red "Refs already reached ${REMOTE}. Nothing rolled back — finish by hand:"
    red "  git checkout ${RELEASE_BRANCH} && git merge --ff-only main && git push ${REMOTE} ${RELEASE_BRANCH}"
    return 0
  fi

  if [[ $PUBLISHED -eq 1 ]]; then
    red "${PACKAGE_ID} ${VERSION} IS PUBLISHED on NuGet.org but git refs were not pushed."
    red "Do NOT reuse this version. Push the local refs instead:"
    red "  git push ${REMOTE} main && git push ${REMOTE} ${TAG}"
    red "  git checkout ${RELEASE_BRANCH} && git merge --ff-only main && git push ${REMOTE} ${RELEASE_BRANCH}"
    return 0
  fi

  if [[ -z "$BRANCH_START_SHA" ]]; then
    red "Failed during preflight — no state was modified."
    return 0
  fi

  bold "Nothing was published or pushed — rolling back local state..."
  [[ -n "$TAG" ]] && git tag -d "$TAG" >/dev/null 2>&1 || true
  git merge --abort >/dev/null 2>&1 || true
  git checkout --quiet "$RELEASE_BRANCH" >/dev/null 2>&1 || true
  [[ -n "$MAIN_START_SHA" ]]   && git branch --quiet -f main "$MAIN_START_SHA" >/dev/null 2>&1 || true
  [[ -n "$BRANCH_START_SHA" ]] && git reset --hard --quiet "$BRANCH_START_SHA" >/dev/null 2>&1 || true
  green "✓ Local state restored to ${RELEASE_BRANCH}@${BRANCH_START_SHA:0:8}"
}
trap cleanup EXIT

# ═══ A. PREFLIGHT — no mutations past this block ══════════════════════════════
bold "▶ Preflight checks..."

[[ $# -eq 1 ]] || die "Usage: $0 <version>  (e.g. 1.2.0)"
VERSION="$1"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "Version must be semver: X.Y.Z"

[[ -n "${NUGET_API_KEY:-}" ]] || die "NUGET_API_KEY env var is not set"

command -v dotnet >/dev/null || die "dotnet not found — install the .NET 10 SDK"
command -v git    >/dev/null || die "git not found"

[[ -f "$CSPROJ" ]] || die "Run this script from the repository root ($CSPROJ not found)"

RELEASE_BRANCH=$(git rev-parse --abbrev-ref HEAD)
[[ "$RELEASE_BRANCH" != "HEAD" ]] || die "Detached HEAD — check out the release branch first"
[[ "$RELEASE_BRANCH" != "main" ]] || die "Already on main — run this from your dev/feature branch"

[[ -z "$(git status --porcelain --untracked-files=no)" ]] \
  || die "Working tree is dirty — commit or stash tracked changes first"

CURRENT_VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$CSPROJ" | head -1)
[[ -n "$CURRENT_VERSION" ]] || die "No <Version> element found in $CSPROJ"
[[ "$CURRENT_VERSION" != "$VERSION" ]] \
  || die "$CSPROJ already declares ${VERSION} — pick a new version"

# Highest version wins: refuse to go backwards.
HIGHEST=$(printf '%s\n%s\n' "$CURRENT_VERSION" "$VERSION" | sort -V | tail -1)
[[ "$HIGHEST" == "$VERSION" ]] \
  || die "Version ${VERSION} is older than the current ${CURRENT_VERSION}"

TAG="v${VERSION}"
if git rev-parse --verify --quiet "refs/tags/${TAG}" >/dev/null; then
  die "Tag ${TAG} already exists locally — delete it or pick a new version"
fi

# Remote reachability + credentials, and remote tag collision, in one round trip.
bold "  Contacting ${REMOTE}..."
git ls-remote --exit-code "$REMOTE" >/dev/null 2>&1 \
  || die "Cannot reach ${REMOTE} — check network and credentials"
[[ -z "$(git ls-remote --tags "$REMOTE" "refs/tags/${TAG}")" ]] \
  || die "Tag ${TAG} already exists on ${REMOTE} — that version was already released"

git fetch --quiet --prune "$REMOTE" "main" "$RELEASE_BRANCH" 2>/dev/null \
  || git fetch --quiet --prune "$REMOTE" \
  || die "git fetch from ${REMOTE} failed"

git rev-parse --verify --quiet "refs/remotes/${REMOTE}/main" >/dev/null \
  || die "${REMOTE}/main does not exist"

# The release branch must contain everything on its own remote counterpart.
if git rev-parse --verify --quiet "refs/remotes/${REMOTE}/${RELEASE_BRANCH}" >/dev/null; then
  git merge-base --is-ancestor "${REMOTE}/${RELEASE_BRANCH}" HEAD \
    || die "${RELEASE_BRANCH} is behind ${REMOTE}/${RELEASE_BRANCH} — pull and rerun"
fi

# main must be fast-forwardable to its remote, and the branch must be ahead of main.
if git rev-parse --verify --quiet refs/heads/main >/dev/null; then
  git merge-base --is-ancestor main "${REMOTE}/main" \
    || die "Local main has commits not on ${REMOTE}/main — reconcile main first"
fi
git merge-base --is-ancestor "${REMOTE}/main" HEAD \
  || die "${RELEASE_BRANCH} does not contain ${REMOTE}/main — merge main into ${RELEASE_BRANCH} first"

BRANCH_START_SHA=$(git rev-parse HEAD)
MAIN_START_SHA=$(git rev-parse --verify --quiet refs/heads/main || true)

green "✓ Preflight passed — releasing ${TAG} from '${RELEASE_BRANCH}' (was ${CURRENT_VERSION})"

# ═══ B. VERIFY ════════════════════════════════════════════════════════════════
bold "\n▶ Restoring and testing '${RELEASE_BRANCH}'..."
dotnet restore || die "dotnet restore failed"
dotnet test -c Release --logger "console;verbosity=minimal" \
  || die "Tests failed on ${RELEASE_BRANCH} — fix them before releasing"
green "✓ Tests passed"

# ═══ C. BUMP ══════════════════════════════════════════════════════════════════
bold "\n▶ Bumping version ${CURRENT_VERSION} → ${VERSION}..."
sed -i "s|<Version>${CURRENT_VERSION}</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
grep -q "<Version>${VERSION}</Version>" "$CSPROJ" || die "Version bump did not apply to $CSPROJ"

git add "$CSPROJ"
git commit --quiet -m "chore(release): bump version to ${VERSION}"
green "✓ Bumped and committed on ${RELEASE_BRANCH}"

# ═══ D. MERGE ═════════════════════════════════════════════════════════════════
bold "\n▶ Merging '${RELEASE_BRANCH}' into main..."
git checkout --quiet main
git merge --ff-only "${REMOTE}/main" || die "main could not fast-forward to ${REMOTE}/main"
git merge --no-ff "$RELEASE_BRANCH" \
  -m "chore(release): merge ${RELEASE_BRANCH} into main for v${VERSION}" \
  || die "Merge into main failed"
green "✓ main merged"

# ═══ E. PACKAGE — rebuild and retest the MERGED tree, never the pre-merge one ══
bold "\n▶ Rebuilding and testing the merged tree..."
dotnet build -c Release || die "Release build of the merged tree failed"
dotnet test -c Release --no-build --logger "console;verbosity=minimal" \
  || die "Tests failed on the merged tree — main brought in a regression"
green "✓ Merged tree is green"

bold "\n▶ Packing ${PACKAGE_ID} ${VERSION}..."
NUPKG="${PACK_DIR}/${PACKAGE_ID}.${VERSION}.nupkg"
rm -f "$NUPKG"
dotnet pack "$CSPROJ" -c Release --no-build -o "$PACK_DIR" || die "dotnet pack failed"
# Strict: only the exact expected artifact ships. Never fall back to a stale nupkg.
[[ -f "$NUPKG" ]] || die "Expected package $NUPKG was not produced by dotnet pack"
green "✓ Packed $NUPKG"

# ═══ F. TAG ═══════════════════════════════════════════════════════════════════
git tag -a "$TAG" -m "Release ${TAG}"
green "✓ Tagged ${TAG}"

# ═══ G. PUBLISH — point of no return ══════════════════════════════════════════
bold "\n▶ Publishing ${NUPKG} to NuGet.org..."
dotnet nuget push "$NUPKG" \
  --api-key "$NUGET_API_KEY" \
  --source "$NUGET_SOURCE" \
  --skip-duplicate \
  || die "NuGet push failed — nothing was pushed to ${REMOTE}, local state will be rolled back"
PUBLISHED=1
green "✓ Published ${PACKAGE_ID} ${VERSION}"

# ═══ H. PROPAGATE — main, tag, and the release branch all end up in sync ══════
bold "\n▶ Pushing main and ${TAG}..."
git push "$REMOTE" main || die "Failed to push main"
git push "$REMOTE" "$TAG" || die "Failed to push ${TAG}"
PUSHED=1

bold "\n▶ Syncing '${RELEASE_BRANCH}' with main..."
git checkout --quiet "$RELEASE_BRANCH"
git merge --ff-only main || die "${RELEASE_BRANCH} could not fast-forward onto main"
git push "$REMOTE" "$RELEASE_BRANCH" || die "Failed to push ${RELEASE_BRANCH}"
green "✓ ${RELEASE_BRANCH} and main are identical and both pushed"

green "\n✓ Release ${TAG} complete"
bold "  Branch:  ${RELEASE_BRANCH} == main == ${TAG}"
bold "  Install: dotnet tool install -g ${PACKAGE_ID}"
bold "  Update:  dotnet tool update -g ${PACKAGE_ID}"

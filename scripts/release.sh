#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# release.sh — prepares release/vX.Y.Z and hands it to CI
#
# Usage:
#   ./scripts/release.sh patch|minor|major
#
# Run from a feature/* or fix/* branch.
#
# The script only touches the source branch and release/vX.Y.Z on origin.
# .github/workflows/promote.yml then merges release/vX.Y.Z into main, tests the
# merge, pushes main and the tag, syncs dev onto main and deletes the release
# branch. The tag triggers .github/workflows/release.yml, which publishes to
# NuGet.org and the MCP Registry.
#
# Phases:
#   A. Preflight     — inputs, tooling, remote, tag/version/branch state
#   B. Push source   — push the feature/fix branch
#   C. Release       — release/vX.Y.Z from origin/dev, --no-ff merge of the
#                      source branch, bump <Version> and server.json, commit
#   D. Hand off      — push release/vX.Y.Z, delete it locally, back on the source branch
# ---------------------------------------------------------------------------

CSPROJ="src/ContextManager.Mcp/ContextManager.Mcp.csproj"
SERVER_JSON="server.json"
REMOTE="origin"

red()   { echo -e "\033[0;31m$*\033[0m"; }
green() { echo -e "\033[0;32m$*\033[0m"; }
bold()  { echo -e "\033[1m$*\033[0m"; }

die() { red "ERROR: $*" >&2; exit 1; }

merge_subject() {
  local subject="chore(release): merge $1 into $2"
  if (( ${#subject} > 72 )); then
    subject="chore(release): merge branch into $2"
  fi
  printf '%s' "$subject"
}

SOURCE_BRANCH=""
RELEASE_BRANCH=""
RELEASE_CREATED=0

cleanup() {
  local code=$?
  [[ $code -eq 0 ]] && return 0

  red "\n✗ Release aborted (exit ${code})."
  [[ $RELEASE_CREATED -eq 1 ]] || return 0

  git merge --abort >/dev/null 2>&1 || true
  git checkout --quiet --force "$SOURCE_BRANCH" >/dev/null 2>&1 || true
  git branch --quiet -D "$RELEASE_BRANCH" >/dev/null 2>&1 || true
  red "Local ${RELEASE_BRANCH} removed; back on ${SOURCE_BRANCH}."
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

bold "▶ Preflight checks..."

[[ $# -eq 1 ]] || die "Usage: $0 patch|minor|major"
BUMP="$1"
[[ "$BUMP" =~ ^(patch|minor|major)$ ]] || die "Bump must be one of: patch, minor, major"

command -v git >/dev/null || die "git not found"

[[ -f "$CSPROJ" ]] || die "Run this script from the repository root ($CSPROJ not found)"

SOURCE_BRANCH=$(git rev-parse --abbrev-ref HEAD)
[[ "$SOURCE_BRANCH" == feature/* || "$SOURCE_BRANCH" == fix/* ]] \
  || die "Run this from a feature/* or fix/* branch (HEAD is '${SOURCE_BRANCH}')"

[[ -z "$(git status --porcelain --untracked-files=no)" ]] \
  || die "Working tree is dirty — commit or stash tracked changes first"

CURRENT_VERSION=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$CSPROJ" | head -1)
[[ "$CURRENT_VERSION" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)$ ]] \
  || die "<Version> in $CSPROJ is '${CURRENT_VERSION}', expected X.Y.Z"
MAJOR="${BASH_REMATCH[1]}"
MINOR="${BASH_REMATCH[2]}"
PATCH="${BASH_REMATCH[3]}"
case "$BUMP" in
  major) VERSION="$((MAJOR + 1)).0.0" ;;
  minor) VERSION="${MAJOR}.$((MINOR + 1)).0" ;;
  patch) VERSION="${MAJOR}.${MINOR}.$((PATCH + 1))" ;;
esac

TAG="v${VERSION}"
RELEASE_BRANCH="release/${TAG}"
if git rev-parse --verify --quiet "refs/tags/${TAG}" >/dev/null; then
  die "Tag ${TAG} already exists locally — delete it or pick another bump"
fi
if git rev-parse --verify --quiet "refs/heads/${RELEASE_BRANCH}" >/dev/null; then
  die "Local branch ${RELEASE_BRANCH} already exists — delete it first"
fi

bold "  Contacting ${REMOTE}..."
git ls-remote --exit-code "$REMOTE" >/dev/null 2>&1 \
  || die "Cannot reach ${REMOTE} — check network and credentials"
[[ -z "$(git ls-remote --tags "$REMOTE" "refs/tags/${TAG}")" ]] \
  || die "Tag ${TAG} already exists on ${REMOTE} — that version was already released"

git fetch --quiet --prune "$REMOTE" || die "git fetch from ${REMOTE} failed"

git rev-parse --verify --quiet "refs/remotes/${REMOTE}/dev" >/dev/null || die "${REMOTE}/dev does not exist"

if git rev-parse --verify --quiet "refs/remotes/${REMOTE}/${SOURCE_BRANCH}" >/dev/null; then
  git merge-base --is-ancestor "${REMOTE}/${SOURCE_BRANCH}" HEAD \
    || die "${SOURCE_BRANCH} is behind ${REMOTE}/${SOURCE_BRANCH} — pull and rerun"
fi

green "✓ Preflight passed — preparing ${TAG} (${BUMP} from ${CURRENT_VERSION}) from '${SOURCE_BRANCH}'"

bold "\n▶ Pushing '${SOURCE_BRANCH}'..."
git push "$REMOTE" "$SOURCE_BRANCH" || die "Failed to push ${SOURCE_BRANCH}"
green "✓ ${SOURCE_BRANCH} pushed"

bold "\n▶ Creating ${RELEASE_BRANCH} from ${REMOTE}/dev and merging '${SOURCE_BRANCH}'..."
git checkout --quiet --no-track -b "$RELEASE_BRANCH" "${REMOTE}/dev"
RELEASE_CREATED=1
git merge --no-ff "$SOURCE_BRANCH" \
  -m "$(merge_subject "$SOURCE_BRANCH" dev)" \
  -m "Release ${TAG} from ${SOURCE_BRANCH}" \
  || die "Merging ${SOURCE_BRANCH} into dev failed — resolve it on ${SOURCE_BRANCH} and rerun"

bold "\n▶ Bumping ${CURRENT_VERSION} → ${VERSION}..."
sed -i "s|<Version>${CURRENT_VERSION}</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
grep -q "<Version>${VERSION}</Version>" "$CSPROJ" || die "Version bump did not apply to $CSPROJ"
sed -i "s|\"version\": \"${CURRENT_VERSION}\"|\"version\": \"${VERSION}\"|" "$SERVER_JSON"
[[ $(grep -c "\"version\": \"${VERSION}\"" "$SERVER_JSON") -eq 2 ]] || die "Version bump did not apply to $SERVER_JSON"
git add "$CSPROJ" "$SERVER_JSON"
git commit --quiet -m "chore(release): bump version to ${VERSION}"
green "✓ Bumped and committed on ${RELEASE_BRANCH}"

bold "\n▶ Pushing ${RELEASE_BRANCH}..."
git push --force "$REMOTE" "$RELEASE_BRANCH" || die "Failed to push ${RELEASE_BRANCH}"

git checkout --quiet "$SOURCE_BRANCH"
git branch --quiet -D "$RELEASE_BRANCH"
RELEASE_CREATED=0

green "\n✓ ${RELEASE_BRANCH} handed off to CI"
bold "  CI merges it into main, tags ${TAG}, syncs dev and publishes."
bold "  Follow it: gh run list --workflow promote.yml --branch ${RELEASE_BRANCH}"

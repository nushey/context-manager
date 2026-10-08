#!/usr/bin/env bash
set -euo pipefail

# ---------------------------------------------------------------------------
# release.sh — git-flow release: <feature|fix> → dev → release/vX.Y.Z → main
#              → NuGet.org → dev synced onto main
#
# Usage:
#   ./scripts/release.sh patch|minor|major
#
# Run from a feature/* or fix/* branch. Required env vars:
#   NUGET_API_KEY — NuGet.org API key
#
# Only the source branch reaches origin before the NuGet push; main, the tag
# and dev are pushed after it. A failure before publishing restores every local
# ref; a failure after it prints the remaining steps.
#
# Phases:
#   A. Preflight     — inputs, tooling, remote, tag/version/branch state
#   B. Push source   — push the feature/fix branch
#   C. Integrate     — merge it --no-ff into dev (local), test
#   D. Release       — release/vX.Y.Z from dev, bump <Version>, commit
#   E. Merge main    — main fast-forwarded to origin, --no-ff merge of release
#   F. Package       — rebuild and test the merged tree, pack, verify nupkg
#   G. Tag           — annotated vX.Y.Z on the merge commit
#   H. Publish       — dotnet nuget push (point of no return)
#   I. Propagate     — push main + tag, fast-forward dev onto main, push dev,
#                      delete release/vX.Y.Z, return to the source branch
# ---------------------------------------------------------------------------

CSPROJ="src/ContextManager.Mcp/ContextManager.Mcp.csproj"
PACKAGE_ID="ContextManager"
NUGET_SOURCE="https://api.nuget.org/v3/index.json"
REMOTE="origin"
PACK_DIR="./nupkgs"

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

STAGE="preflight"
SOURCE_BRANCH=""
RELEASE_BRANCH=""
RELEASE_CREATED=0
DEV_START_SHA=""
DEV_EXISTED=0
MAIN_START_SHA=""
MAIN_EXISTED=0
VERSION=""
TAG=""

print_dev_steps() {
  red "  git checkout dev && git merge --ff-only main && git push ${REMOTE} dev"
  red "    (if ${REMOTE}/dev moved: git merge ${REMOTE}/dev on dev first, then merge main)"
  red "  git checkout ${SOURCE_BRANCH} && git branch -D ${RELEASE_BRANCH}"
}

restore_ref() {
  local name="$1" start="$2" existed="$3"
  if [[ $existed -eq 1 ]]; then
    git branch --quiet -f "$name" "$start" >/dev/null 2>&1 || true
  else
    git branch --quiet -D "$name" >/dev/null 2>&1 || true
  fi
}

cleanup() {
  local code=$?
  [[ $code -eq 0 ]] && return 0

  red "\n✗ Release aborted (exit ${code})."

  case "$STAGE" in
    preflight)
      red "Failed during preflight — no state was modified."
      return 0
      ;;
    published)
      red "${PACKAGE_ID} ${VERSION} IS PUBLISHED on NuGet.org but main and ${TAG} were not pushed."
      red "Do NOT reuse this version — finish by hand:"
      red "  git fetch ${REMOTE}"
      red "  git checkout main && git merge ${REMOTE}/main   # only if it moved"
      red "  git push ${REMOTE} main && git push ${REMOTE} ${TAG}"
      print_dev_steps
      return 0
      ;;
    main-pushed)
      red "main and ${TAG} are on ${REMOTE}, but dev was not synced. Finish by hand:"
      red "  git fetch ${REMOTE}"
      print_dev_steps
      return 0
      ;;
  esac

  bold "Nothing was published — rolling back local state..."
  git merge --abort >/dev/null 2>&1 || true
  git checkout --quiet --force "$SOURCE_BRANCH" >/dev/null 2>&1 || true

  local head_now
  head_now=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "")
  if [[ "$head_now" != "$SOURCE_BRANCH" ]]; then
    red "Could not switch back to ${SOURCE_BRANCH} (HEAD is on '${head_now}')."
    red "Refusing to move refs under a checked-out branch. Recover by hand:"
    red "  git merge --abort; git checkout --force ${SOURCE_BRANCH}"
    [[ -n "$TAG" ]] && red "  git tag -d ${TAG}"
    [[ $RELEASE_CREATED -eq 1 ]] && red "  git branch -D ${RELEASE_BRANCH}"
    if [[ $MAIN_EXISTED -eq 1 ]]; then red "  git branch -f main ${MAIN_START_SHA}"; else red "  git branch -D main"; fi
    if [[ $DEV_EXISTED -eq 1 ]]; then red "  git branch -f dev ${DEV_START_SHA}"; else red "  git branch -D dev"; fi
    return 0
  fi

  [[ -n "$TAG" ]] && git tag -d "$TAG" >/dev/null 2>&1 || true
  [[ $RELEASE_CREATED -eq 1 ]] && git branch --quiet -D "$RELEASE_BRANCH" >/dev/null 2>&1 || true
  restore_ref main "$MAIN_START_SHA" "$MAIN_EXISTED"
  restore_ref dev "$DEV_START_SHA" "$DEV_EXISTED"
  green "✓ Local state restored: back on ${SOURCE_BRANCH}, main and dev at their starting commits"
  red "  ${SOURCE_BRANCH} stays pushed to ${REMOTE}; dev on ${REMOTE} is untouched."
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

bold "▶ Preflight checks..."

[[ $# -eq 1 ]] || die "Usage: $0 patch|minor|major"
BUMP="$1"
[[ "$BUMP" =~ ^(patch|minor|major)$ ]] || die "Bump must be one of: patch, minor, major"

[[ -n "${NUGET_API_KEY:-}" ]] || die "NUGET_API_KEY env var is not set"

command -v dotnet >/dev/null || die "dotnet not found — install the .NET 10 SDK"
command -v git    >/dev/null || die "git not found"

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

bold "  Contacting ${REMOTE}..."
git ls-remote --exit-code "$REMOTE" >/dev/null 2>&1 \
  || die "Cannot reach ${REMOTE} — check network and credentials"
[[ -z "$(git ls-remote --tags "$REMOTE" "refs/tags/${TAG}")" ]] \
  || die "Tag ${TAG} already exists on ${REMOTE} — that version was already released"

git fetch --quiet --prune "$REMOTE" || die "git fetch from ${REMOTE} failed"

git rev-parse --verify --quiet "refs/remotes/${REMOTE}/main" >/dev/null || die "${REMOTE}/main does not exist"
git rev-parse --verify --quiet "refs/remotes/${REMOTE}/dev"  >/dev/null || die "${REMOTE}/dev does not exist"

if git rev-parse --verify --quiet "refs/remotes/${REMOTE}/${SOURCE_BRANCH}" >/dev/null; then
  git merge-base --is-ancestor "${REMOTE}/${SOURCE_BRANCH}" HEAD \
    || die "${SOURCE_BRANCH} is behind ${REMOTE}/${SOURCE_BRANCH} — pull and rerun"
fi

for branch in main dev; do
  if git rev-parse --verify --quiet "refs/heads/${branch}" >/dev/null; then
    git merge-base --is-ancestor "$branch" "${REMOTE}/${branch}" \
      || die "Local ${branch} has commits not on ${REMOTE}/${branch} — reconcile ${branch} first"
  fi
done

MAIN_START_SHA=$(git rev-parse --verify --quiet refs/heads/main || true)
[[ -n "$MAIN_START_SHA" ]] && MAIN_EXISTED=1
DEV_START_SHA=$(git rev-parse --verify --quiet refs/heads/dev || true)
[[ -n "$DEV_START_SHA" ]] && DEV_EXISTED=1

STAGE="local"
green "✓ Preflight passed — releasing ${TAG} (${BUMP} from ${CURRENT_VERSION}) from '${SOURCE_BRANCH}'"

bold "\n▶ Pushing '${SOURCE_BRANCH}'..."
git push "$REMOTE" "$SOURCE_BRANCH" || die "Failed to push ${SOURCE_BRANCH}"
green "✓ ${SOURCE_BRANCH} pushed"

bold "\n▶ Merging '${SOURCE_BRANCH}' into dev..."
git checkout --quiet dev
git merge --ff-only --quiet "${REMOTE}/dev" || die "dev could not fast-forward to ${REMOTE}/dev"
git merge --no-ff "$SOURCE_BRANCH" \
  -m "$(merge_subject "$SOURCE_BRANCH" dev)" \
  -m "Release ${TAG} from ${SOURCE_BRANCH}" \
  || die "Merging ${SOURCE_BRANCH} into dev failed — resolve it on ${SOURCE_BRANCH} and rerun"
MERGED_ORIGIN_DEV=$(git rev-parse "${REMOTE}/dev")
green "✓ dev merged (local)"

bold "\n▶ Restoring and testing dev..."
dotnet restore || die "dotnet restore failed"
dotnet test -c Release --no-restore --logger "console;verbosity=minimal" \
  || die "Tests failed on dev after merging ${SOURCE_BRANCH}"
green "✓ Tests passed"

bold "\n▶ Creating ${RELEASE_BRANCH} and bumping ${CURRENT_VERSION} → ${VERSION}..."
git checkout --quiet -b "$RELEASE_BRANCH" dev
RELEASE_CREATED=1
sed -i "s|<Version>${CURRENT_VERSION}</Version>|<Version>${VERSION}</Version>|" "$CSPROJ"
grep -q "<Version>${VERSION}</Version>" "$CSPROJ" || die "Version bump did not apply to $CSPROJ"
git add "$CSPROJ"
git commit --quiet -m "chore(release): bump version to ${VERSION}"
green "✓ Bumped and committed on ${RELEASE_BRANCH}"

bold "\n▶ Merging '${RELEASE_BRANCH}' into main..."
git fetch --quiet --prune "$REMOTE" main dev || die "git fetch from ${REMOTE} failed"
git merge-base --is-ancestor "${REMOTE}/main" HEAD \
  || die "${REMOTE}/main is not contained in dev — merge main into dev and rerun"
git checkout --quiet main
git merge --ff-only --quiet "${REMOTE}/main" || die "main could not fast-forward to ${REMOTE}/main"
git merge --no-ff "$RELEASE_BRANCH" \
  -m "$(merge_subject "$RELEASE_BRANCH" main)" \
  -m "Release ${TAG} from ${SOURCE_BRANCH}" \
  || die "Merge into main failed"
MERGED_ORIGIN_MAIN=$(git rev-parse "${REMOTE}/main")
green "✓ main merged (local)"

bold "\n▶ Rebuilding and testing the merged tree..."
dotnet build -c Release || die "Release build of the merged tree failed"
dotnet test -c Release --no-build --logger "console;verbosity=minimal" \
  || die "Tests failed on the merged tree — main brought in a regression"
green "✓ Merged tree is green"

bold "\n▶ Packing ${PACKAGE_ID} ${VERSION}..."
NUPKG="${PACK_DIR}/${PACKAGE_ID}.${VERSION}.nupkg"
rm -f "$NUPKG"
dotnet pack "$CSPROJ" -c Release --no-build -o "$PACK_DIR" || die "dotnet pack failed"
[[ -f "$NUPKG" ]] || die "Expected package $NUPKG was not produced by dotnet pack"
green "✓ Packed $NUPKG"

git tag -a "$TAG" -m "Release ${TAG}"
green "✓ Tagged ${TAG}"

bold "\n▶ Final check before the irreversible step..."
git fetch --quiet --prune "$REMOTE" main dev || die "git fetch from ${REMOTE} failed"
[[ "$(git rev-parse "${REMOTE}/main")" == "$MERGED_ORIGIN_MAIN" ]] \
  || die "${REMOTE}/main moved during the release — aborting BEFORE publishing. Rerun."
[[ "$(git rev-parse "${REMOTE}/dev")" == "$MERGED_ORIGIN_DEV" ]] \
  || die "${REMOTE}/dev moved during the release — aborting BEFORE publishing. Rerun."
green "✓ ${REMOTE}/main and ${REMOTE}/dev are still at the commits we merged"

bold "\n▶ Publishing ${NUPKG} to NuGet.org..."
dotnet nuget push "$NUPKG" \
  --api-key "$NUGET_API_KEY" \
  --source "$NUGET_SOURCE" \
  --skip-duplicate \
  || die "NuGet push failed — nothing was published"
STAGE="published"
green "✓ Published ${PACKAGE_ID} ${VERSION}"

bold "\n▶ Pushing main and ${TAG}..."
git push "$REMOTE" main || die "Failed to push main"
git push "$REMOTE" "$TAG" || die "Failed to push ${TAG}"
STAGE="main-pushed"

bold "\n▶ Syncing dev onto main..."
git checkout --quiet dev
git merge --ff-only --quiet main || die "dev could not fast-forward onto main"
git push "$REMOTE" dev || die "Failed to push dev"

git checkout --quiet "$SOURCE_BRANCH"
git branch --quiet -D "$RELEASE_BRANCH"
STAGE="done"

green "\n✓ Release ${TAG} complete"
bold "  dev == main == ${TAG}, all pushed to ${REMOTE}"
bold "  Install: dotnet tool install -g ${PACKAGE_ID}"
bold "  Update:  dotnet tool update -g ${PACKAGE_ID}"

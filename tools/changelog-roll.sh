#!/usr/bin/env bash
#
# Rolls the "## [Unreleased]" section of CHANGELOG.md into a dated "## [VERSION] - DATE" section and
# resets [Unreleased] to the empty seed template. Rewrites the two reference links at the bottom so
# [Unreleased] compares VERSION..HEAD and a fresh [VERSION] link is added. Edits the file in place.
#
# Called on a Stable (v-tag) release; the build workflow commits the result back to the default
# branch. A rolled changelog only touches a .md file, which build.yml's paths-ignore skips — so the
# commit-back never re-triggers a build.
#
# Input is env, never argv splicing (SE-135):
#   VERSION    the release version, e.g. 0.3.0                 (required)
#   DATE       ISO date, e.g. 2026-07-18                       (required)
#   REPO       owner/name for the compare links (default: Lionear/SqlExplorer)
#   CHANGELOG  path to the changelog (default: CHANGELOG.md)
set -euo pipefail

: "${VERSION:?VERSION is required (e.g. 0.3.0)}"
: "${DATE:?DATE is required (e.g. 2026-07-18)}"
REPO="${REPO:-Lionear/SqlExplorer}"
FILE="${CHANGELOG:-CHANGELOG.md}"
[ -f "$FILE" ] || { echo "Changelog not found: $FILE" >&2; exit 1; }

# These values reach a file write and later a git commit. Keep them boring (the pipeline's backstop).
printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.]+)?$' \
  || { echo "Refusing VERSION '$VERSION'" >&2; exit 1; }
printf '%s' "$DATE" | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' \
  || { echo "Refusing DATE '$DATE'" >&2; exit 1; }
printf '%s' "$REPO" | grep -Eq '^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$' \
  || { echo "Refusing REPO '$REPO'" >&2; exit 1; }

# Idempotency: if this version already has a heading, there is nothing to roll (a re-run of the same
# release). Leave the file untouched so the commit-back is a no-op.
if grep -q "^## \[$VERSION\]" "$FILE"; then
  echo "CHANGELOG already has [$VERSION]; nothing to roll." >&2
  exit 0
fi

# Previous top version (for the new compare link): the tag between "/compare/" and "...HEAD" in the
# existing [Unreleased] link. Captured with a group so the trailing "..." never leaks into PREV.
PREV=$(sed -n 's|^\[Unreleased\]:.*/compare/\(v[0-9][A-Za-z0-9.+-]*\)\.\.\.HEAD.*|\1|p' "$FILE" | head -1)

awk -v version="$VERSION" -v date="$DATE" -v repo="$REPO" -v prev="$PREV" '
  BEGIN { rolled = 0 }

  # Rewrite the reference links at the bottom.
  /^\[Unreleased\]:/ {
    print "[Unreleased]: https://github.com/" repo "/compare/v" version "...HEAD"
    if (prev != "")
      print "[" version "]: https://github.com/" repo "/compare/" prev "...v" version
    else
      print "[" version "]: https://github.com/" repo "/releases/tag/v" version
    next
  }
  # Drop a stale [version] link if one somehow exists (idempotency guard already handles the heading).
  /^\[/ && $0 ~ ("^\\[" version "\\]:") { next }

  # At the Unreleased heading: emit a fresh empty Unreleased block, then open the dated section.
  /^## \[Unreleased\]/ {
    print "## [Unreleased]"
    print ""
    print "_Nothing yet._"
    print ""
    print "## [" version "] - " date
    rolled = 1
    inbody = 1
    next
  }
  # While copying the old Unreleased body into the dated section, skip the seed placeholder lines.
  inbody {
    if ($0 ~ /^## \[/) { inbody = 0; print; next }          # reached next section: stop skipping
    if ($0 ~ /^_Nothing yet\._$/) next
    if ($0 ~ /^### / || $0 ~ /^- / || $0 ~ /^[[:space:]]*$/ || $0 ~ /^<!--/ || $0 ~ /-->/ || inhtml) {
      if ($0 ~ /<!--/) inhtml = 1
      if ($0 ~ /-->/)  { inhtml = 0; next }
      if (inhtml) next
      if ($0 ~ /^- Nothing yet\.?$/) next
    }
    print
    next
  }
  { print }

  END { if (!rolled) { print "changelog-roll: no [Unreleased] heading found" > "/dev/stderr"; exit 1 } }
' "$FILE" \
| awk 'NF {blank=0; print; next} {blank++} blank<2 {print}' > "$FILE.tmp"

mv "$FILE.tmp" "$FILE"
echo "Rolled [Unreleased] into [$VERSION] - $DATE (prev: ${PREV:-none})" >&2

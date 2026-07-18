#!/usr/bin/env bash
#
# Prints the body of the "## [Unreleased]" section of CHANGELOG.md as markdown — everything between
# that heading and the next "## [" heading, with the HTML comment template and empty placeholder
# sections stripped. This is what fills the GitHub release notes and update.json (SE-137), replacing
# the git-log-derived notes: the changelog is now the source of truth for "what changed".
#
# Input is env, never argv splicing (same hardening as the rest of the pipeline, SE-135):
#   CHANGELOG   path to the changelog file (default: CHANGELOG.md)
# Output: markdown on stdout. Empty output (nothing printed) when the section has no real entries —
#         callers substitute their own "No changes recorded." in that case.
set -euo pipefail

FILE="${CHANGELOG:-CHANGELOG.md}"
[ -f "$FILE" ] || { echo "Changelog not found: $FILE" >&2; exit 1; }

# One awk pass: capture the Unreleased body, drop <!-- --> comment blocks, then emit only the
# "### Section" groups that actually carry a bullet (so an empty "### Added / - Nothing yet." seed
# produces no output). "- Nothing yet." is treated as no entry.
awk '
  /^## \[Unreleased\]/ { grab = 1; next }
  grab && /^## \[/     { grab = 0 }   # next version section ends it
  grab && /^\[[^]]+\]:/ { grab = 0 }  # ...or the reference-link block, when Unreleased is last
  grab                 { body[n++] = $0 }

  END {
    incomment = 0
    # First strip comment blocks.
    m = 0
    for (i = 0; i < n; i++) {
      line = body[i]
      if (line ~ /<!--/)  incomment = 1
      if (incomment) { if (line ~ /-->/) incomment = 0; continue }
      clean[m++] = line
    }

    # Walk sections; buffer each "### X" group and only flush it if it has a real bullet.
    header = ""; has = 0; buflen = 0
    for (i = 0; i < m; i++) {
      line = clean[i]
      if (line ~ /^### /) {
        flush(header, has, buf, buflen)
        header = line; has = 0; buflen = 0
        continue
      }
      if (line ~ /^- / && line !~ /^- Nothing yet\.?$/) has = 1
      if (header != "") buf[buflen++] = line
      else if (line !~ /^[[:space:]]*$/ && line !~ /^_Nothing yet\._$/) { print line }   # stray text
    }
    flush(header, has, buf, buflen)
  }

  function flush(h, ok, b, len,   j) {
    if (h == "" || !ok) return
    print h
    for (j = 0; j < len; j++) if (b[j] !~ /^- Nothing yet\.?$/) print b[j]
  }
' "$FILE" \
| awk 'NF {blank=0; print; next} {blank++} blank<2 && NR>1 {print}' \
| sed -e '1{/^$/d}'

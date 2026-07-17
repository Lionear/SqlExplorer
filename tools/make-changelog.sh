#!/usr/bin/env bash
#
# Generates release notes (markdown) from git history for one build, grouped by conventional-commit type.
# Used by build.yml to fill the `notes` field of update.json (SE-137, Fase 0).
#
# Input is env, never argv splicing (same hardening line as the rest of the pipeline, SE-135):
#   RANGE   git revision range, e.g. "v0.1.0..v0.2.0" or "v0.1.0..HEAD". Required.
#           If the low side is empty (no prior tag) pass just "HEAD".
# Output: markdown on stdout.
set -euo pipefail

: "${RANGE:?RANGE is required (e.g. v0.1.0..HEAD)}"

feats=""
fixes=""
other=""

# %s is the subject line only; merges are dropped, and the release-bump chore is noise in a changelog.
while IFS= read -r subject; do
  case "$subject" in
    "chore(release)"*) continue ;;
    feat:*|feat\(*\):*) feats="${feats}- ${subject}"$'\n' ;;
    fix:*|fix\(*\):*)   fixes="${fixes}- ${subject}"$'\n' ;;
    *)                  other="${other}- ${subject}"$'\n' ;;
  esac
done < <(git log --no-merges --pretty=format:'%s' "$RANGE")

if [ -z "$feats$fixes$other" ]; then
  echo "No changes recorded."
  exit 0
fi

[ -n "$feats" ] && printf '### Features\n%s\n' "$feats"
[ -n "$fixes" ] && printf '### Fixes\n%s\n' "$fixes"
[ -n "$other" ] && printf '### Other\n%s\n' "$other"

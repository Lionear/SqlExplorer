#!/usr/bin/env bash
#
# Builds the update.json manifest for one channel (SE-137, Fase 0): scans a directory of build assets,
# computes each one's SHA-256, and maps its filename to a per-RID key the app looks up. This manifest —
# refreshed every build and published as a release asset — is the source of truth the in-app updater
# checks, because a rolling tag (nightly/preview) never changes even when its assets do.
#
# Input is env, never argv splicing (SE-135 hardening):
#   CHANNEL        stable | preview | nightly. Required.
#   VERSION        full version stamp, e.g. 0.2.0-nightly.20260717.42. Required.
#   COMMIT         short commit the build was cut from. Required.
#   PUBLISHED_AT   ISO-8601 timestamp. Required.
#   ASSET_DIR      directory holding the build assets. Required.
#   DOWNLOAD_BASE  URL prefix the asset download links are built from (no trailing slash), e.g.
#                  https://github.com/Lionear/SqlExplorer/releases/download/nightly. Required.
#   NOTES_FILE     markdown release notes to embed (optional; empty if unset/missing).
#   OUT            output path for update.json. Required.
set -euo pipefail

: "${CHANNEL:?}"; : "${VERSION:?}"; : "${COMMIT:?}"; : "${PUBLISHED_AT:?}"
: "${ASSET_DIR:?}"; : "${DOWNLOAD_BASE:?}"; : "${OUT:?}"

assets='{}'
for path in "$ASSET_DIR"/*; do
  [ -f "$path" ] || continue
  name="$(basename "$path")"

  case "$name" in
    *-win-x64-setup.exe)   key=win-x64-setup;   kind=installer ;;
    *-win-arm64-setup.exe) key=win-arm64-setup; kind=installer ;;
    *-win-x64.zip)         key=win-x64;         kind=zip ;;
    *-win-arm64.zip)       key=win-arm64;       kind=zip ;;
    *-x86_64.AppImage)     key=linux-x64;       kind=appimage ;;
    *-osx-arm64.dmg)       key=osx-arm64;       kind=dmg ;;
    update.json)           continue ;;
    *) echo "::warning::make-update-manifest: unmapped asset '$name' (skipped)"; continue ;;
  esac

  sha="$(sha256sum "$path" | cut -d' ' -f1)"
  size="$(stat -c%s "$path")"
  url="$DOWNLOAD_BASE/$name"

  assets="$(jq \
    --arg k "$key" --arg url "$url" --arg sha "$sha" --arg kind "$kind" --argjson size "$size" \
    '.[$k] = {url: $url, sha256: $sha, kind: $kind, size: $size}' <<<"$assets")"
done

notes=""
if [ -n "${NOTES_FILE:-}" ] && [ -f "$NOTES_FILE" ]; then
  notes="$(cat "$NOTES_FILE")"
fi

jq -n \
  --argjson schemaVersion 1 \
  --arg channel "$CHANNEL" \
  --arg version "$VERSION" \
  --arg commit "$COMMIT" \
  --arg publishedAt "$PUBLISHED_AT" \
  --arg notes "$notes" \
  --argjson assets "$assets" \
  '{schemaVersion: $schemaVersion, channel: $channel, version: $version, commit: $commit,
    publishedAt: $publishedAt, notes: $notes, assets: $assets}' > "$OUT"

echo "Wrote $OUT:"
cat "$OUT"

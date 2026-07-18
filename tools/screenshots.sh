#!/usr/bin/env bash
#
# Regenerate the README screenshots from the real app, headless (no display, no real database). Each
# scene renders the actual views/view-models via SqlExplorer.Screenshots against a throwaway config dir
# and a synthetic demo database — see src/SqlExplorer.Screenshots. Run after a UI change that the README
# should reflect; commit the updated PNGs under docs/images/.
set -euo pipefail
cd "$(dirname "$0")/.."

run() { dotnet run --project src/SqlExplorer.Screenshots -c Debug -- "$@"; }

run --scene hero  --out docs/images/hero.png         --size 1320x840
run --scene store --out docs/images/plugin-store.png --size 1000x720

echo "Screenshots written to docs/images/."

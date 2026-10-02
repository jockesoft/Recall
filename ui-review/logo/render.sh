#!/usr/bin/env bash
# Rebuilds every logo file: the SVGs (make_logo.py) and the raster files that
# are rendered from them with headless Chromium.
#
#   ./ui-review/logo/render.sh [play|check|bookmark]      default: play
#
# Writes Recall.Web/wwwroot/{favicon.svg,favicon.ico,apple-touch-icon.png},
# wwwroot/images/{logo.svg,logo-light.svg,logo-mark.svg,og-image.png} and
# ui-review/screenshots/logo-options.png.
#
# Needs: python3 with fonttools, brotli and pillow (PYTHON=... to pick one),
# and the Chromium that Playwright downloaded (CHROMIUM=... to pick another).
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
WWW="$ROOT/Recall.Web/wwwroot"
PYTHON="${PYTHON:-python3}"
CHROMIUM="${CHROMIUM:-$(ls -d "$HOME"/Library/Caches/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-*/chrome-headless-shell 2>/dev/null | tail -1)}"
VARIANT="${1:-play}"
TMP="$(mktemp -d)"

"$PYTHON" "$HERE/make_logo.py" --variant "$VARIANT"

shot() { # output width height url [scale]
  "$CHROMIUM" --headless --hide-scrollbars --default-background-color=00000000 \
    --force-device-scale-factor="${5:-1}" --window-size="$2,$3" --screenshot="$1" "$4" > /dev/null 2>&1
}

# An SVG opened directly is drawn at its own size, so put it in a page that
# stretches it over the window.
svg_page() { # svg-path -> url of a page showing it full-window
  local page="$TMP/$(basename "$1").html"
  printf '<body style="margin:0"><img src="file://%s" style="display:block;width:100vw;height:100vh">' "$1" > "$page"
  echo "file://$page"
}

for size in 16 32; do shot "$TMP/favicon-$size.png" "$size" "$size" "$(svg_page "$WWW/favicon.svg")"; done
shot "$WWW/apple-touch-icon.png" 180 180 "$(svg_page "$HERE/touch-icon.svg")"
shot "$WWW/images/og-image.png" 1200 630 "file://$HERE/og.html"
mkdir -p "$ROOT/ui-review/screenshots"
shot "$ROOT/ui-review/screenshots/logo-options.png" 1280 440 "file://$HERE/sheet.html" 2

"$PYTHON" - "$TMP" "$WWW/favicon.ico" <<'PY'
import sys
from PIL import Image
tmp, out = sys.argv[1], sys.argv[2]
images = [Image.open(f"{tmp}/favicon-{s}.png").convert("RGBA") for s in (16, 32)]
images[1].save(out, format="ICO", sizes=[(16, 16), (32, 32)], append_images=[images[0]])
PY

rm -r "$TMP"
echo "logo files rebuilt with the '$VARIANT' variant"

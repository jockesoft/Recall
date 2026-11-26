#!/usr/bin/env bash
# Rebuilds every logo file: the SVGs (make_logo.py) and the raster files that
# are rendered from them with headless Chromium.
#
#   ./ui-review/logo/render.sh [play|check|bookmark]      default: bookmark
#
# Writes Recall.Web/wwwroot/{favicon.svg,favicon.ico,apple-touch-icon.png},
# wwwroot/images/{logo.svg,logo-light.svg,logo-mark.svg,og-image.png},
# wwwroot/images/icons/{icon-192.png,icon-512.png,icon-maskable-512.png} (the
# web app manifest's icons) and ui-review/screenshots/logo-options.png.
#
# Needs: python3 with the packages in requirements.txt (PYTHON=... to pick one;
#   python3 -m venv .venv && .venv/bin/pip install -r ui-review/logo/requirements.txt),
# and the headless Chromium that Playwright downloaded
#   (dotnet run --project ui-review/Capture -- --install).
#
# The browser never touches the macOS keychain or a real profile: only
# Playwright's own headless shell (never an installed Chrome, Edge or Brave;
# there is no CHROMIUM=... any more), always with --use-mock-keychain and
# --password-store=basic, and a throwaway profile in this run's temporary
# folder, which is deleted when the script ends, also on an error or Ctrl-C.
# launch_guard refuses any launch that breaks one of those rules, before it
# starts. A keychain prompt is a bug to fix here, never something to approve.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
WWW="$ROOT/Recall.Web/wwwroot"
PYTHON="${PYTHON:-python3}"
VARIANT="${1:-bookmark}"

if [ -n "${CHROMIUM:-}" ]; then
  echo "render.sh: CHROMIUM is not honoured: the logo is rendered with Playwright's bundled headless Chromium only." >&2
  exit 1
fi

PLAYWRIGHT_CACHE="${PLAYWRIGHT_BROWSERS_PATH:-$HOME/Library/Caches/ms-playwright}"
CHROMIUM="$(ls -d "$PLAYWRIGHT_CACHE"/chromium_headless_shell-*/chrome-headless-shell-*/chrome-headless-shell 2>/dev/null | tail -1 || true)"
if [ -z "$CHROMIUM" ] || [ ! -x "$CHROMIUM" ]; then
  echo "render.sh: Playwright's headless Chromium was not found in $PLAYWRIGHT_CACHE;" \
       "install it with: dotnet run --project ui-review/Capture -- --install" >&2
  exit 1
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
trap 'rm -rf "$TMP"; exit 130' INT
trap 'rm -rf "$TMP"; exit 143' TERM HUP

# Every launch gets these: no keychain, no password store of the OS, and a
# throwaway profile that goes with $TMP.
CHROMIUM_ARGS=(--headless --use-mock-keychain --password-store=basic --user-data-dir="$TMP/profile"
  --no-first-run --hide-scrollbars --default-background-color=00000000)

refuse() { echo "render.sh: browser launch refused: $1" >&2; exit 1; }

# The same rules as ui-review/Capture's BrowserLaunch, checked before every launch.
launch_guard() { # the arguments the browser is about to get
  case "$CHROMIUM" in
    "$PLAYWRIGHT_CACHE"/*) ;;
    *) refuse "$CHROMIUM is not Playwright's bundled headless Chromium." ;;
  esac
  local mock=0 basic=0 profile=0 arg
  for arg in "$@"; do
    case "$arg" in
      --use-mock-keychain) mock=1 ;;
      --password-store=basic) basic=1 ;;
      --user-data-dir="$TMP"/*) profile=1 ;;
      --user-data-dir=*|--profile-directory*) refuse "$arg points the browser at a profile outside this run's temporary folder." ;;
    esac
  done
  [ "$mock" = 1 ] || refuse "Chromium lacks --use-mock-keychain."
  [ "$basic" = 1 ] || refuse "Chromium lacks --password-store=basic."
  [ "$profile" = 1 ] || refuse "Chromium has no throwaway profile in this run's temporary folder."
}

"$PYTHON" "$HERE/make_logo.py" --variant "$VARIANT"

shot() { # output width height url [scale]
  local args=("${CHROMIUM_ARGS[@]}" --force-device-scale-factor="${5:-1}" --window-size="$2,$3" --screenshot="$1" "$4")
  launch_guard "${args[@]}"
  "$CHROMIUM" "${args[@]}" > /dev/null 2>&1
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
mkdir -p "$WWW/images/icons"
for size in 192 512; do shot "$WWW/images/icons/icon-$size.png" "$size" "$size" "$(svg_page "$HERE/app-icon.svg")"; done
shot "$WWW/images/icons/icon-maskable-512.png" 512 512 "$(svg_page "$HERE/app-icon-maskable.svg")"
shot "$WWW/images/og-image.png" 1200 630 "file://$HERE/og.html"
mkdir -p "$ROOT/ui-review/screenshots"
shot "$ROOT/ui-review/screenshots/logo-options.png" 1280 560 "file://$HERE/sheet.html" 2

"$PYTHON" - "$TMP" "$WWW/favicon.ico" <<'PY'
import sys
from PIL import Image
tmp, out = sys.argv[1], sys.argv[2]
images = [Image.open(f"{tmp}/favicon-{s}.png").convert("RGBA") for s in (16, 32)]
images[1].save(out, format="ICO", sizes=[(16, 16), (32, 32)], append_images=[images[0]])
PY

echo "logo files rebuilt with the '$VARIANT' variant"

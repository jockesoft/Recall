#!/usr/bin/env python3
"""Builds the Recall logo files from one description of the mark.

    python3 ui-review/logo/make_logo.py [--variant play|check|bookmark]

Writes, into Recall.Web/wwwroot:
    images/logo.svg         navbar lockup: mark + "RECALL", cream wordmark (dark backgrounds)
    images/logo-light.svg   the same with an ink wordmark (light backgrounds)
    images/logo-mark.svg    the mark alone
    favicon.svg             the mark on a dark tile, drawn heavier so it holds at 16px
and into ui-review/logo/:
    variant-<name>.svg      the lockup for each of the three variants
    favicon-<name>.svg      the favicon for each variant
    touch-icon.svg          the favicon without rounded corners (source of apple-touch-icon.png)
    sheet.html              comparison sheet of the three variants
    og.html                 source of the Open Graph image

The wordmark is Big Shoulders Display 800 converted to paths, so the SVG does
not depend on the font being loaded. Needs fontTools and brotli
(pip install fonttools brotli). The PNG and ICO files are rendered from these
SVGs by render.sh, next to this file, which runs this script first.
"""
import argparse
from pathlib import Path

from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont

ROOT = Path(__file__).resolve().parents[2]
WWW = ROOT / "Recall.Web" / "wwwroot"
OUT = Path(__file__).resolve().parent
FONT = WWW / "lib/fontsource/big-shoulders-display/files/big-shoulders-display-latin-800-normal.woff2"

# Theme colours (tokens.css).
AMBER = "#e8a33d"   # --tvdb-signal
CREAM = "#f1ece2"   # --tvdb-on-ink / --tvdb-paper
INK = "#151b24"     # --tvdb-ink
INK_2 = "#1f2733"   # --tvdb-ink-2

# The mark is drawn on a 64 x 64 grid. Stroke 4 with round caps and joins is
# Phosphor's regular weight (16 on its 256 grid).
STROKE = 4
SCREEN = dict(x=8, y=18, w=48, h=36, r=8)

DETAILS = {
    # Each detail sits in the middle of the screen (centre 32, 36).
    "play": lambda c: f'<path d="M27.5 28.5 L39.5 36 L27.5 43.5 Z" fill="{c}" stroke="{c}" stroke-width="2.5" stroke-linejoin="round"/>',
    "check": lambda c: f'<path d="M22.5 36.5 L29 43 L41.5 30" fill="none" stroke="{c}" stroke-width="{STROKE}" stroke-linecap="round" stroke-linejoin="round"/>',
    "bookmark": lambda c: f'<path d="M26 27.5 H38 V45 L32 40.5 L26 45 Z" fill="{c}" stroke="{c}" stroke-width="2.5" stroke-linejoin="round"/>',
}


def mark(variant: str, detail_colour: str, stroke: float = STROKE, outline: str = AMBER) -> str:
    s = SCREEN
    return (
        f'<g fill="none" stroke="{outline}" stroke-width="{stroke}" stroke-linecap="round" stroke-linejoin="round">'
        f'<path d="M20 6 L32 18 L44 6"/>'
        f'<rect x="{s["x"]}" y="{s["y"]}" width="{s["w"]}" height="{s["h"]}" rx="{s["r"]}"/>'
        f"</g>"
        + DETAILS[variant](detail_colour)
    )


def wordmark(text: str, x: float, cap_top: float, cap_bottom: float, colour: str, tracking: float = 0.035):
    """The text as one path; returns (svg, right edge). Caps span cap_top..cap_bottom."""
    font = TTFont(FONT)
    glyphs, cmap, metrics = font.getGlyphSet(), font.getBestCmap(), font["hmtx"]
    units, cap = font["head"].unitsPerEm, font["OS/2"].sCapHeight
    scale = (cap_bottom - cap_top) / cap
    pen = SVGPathPen(glyphs, ntos=lambda v: f"{v:.2f}".rstrip("0").rstrip("."))
    cursor = x
    for ch in text:
        name = cmap[ord(ch)]
        # Font coordinates have y up and the baseline at 0; SVG has y down.
        glyphs[name].draw(TransformPen(pen, (scale, 0, 0, -scale, cursor, cap_bottom)))
        cursor += (metrics[name][0] + tracking * units) * scale
    right = cursor - tracking * units * scale
    return f'<path fill="{colour}" d="{pen.getCommands()}"/>', right


def lockup(variant: str, wordmark_colour: str, detail_colour: str) -> str:
    s = SCREEN
    # Caps are as tall as the screen and sit on its bottom edge.
    text, right = wordmark("RECALL", 68, s["y"] - STROKE / 2, s["y"] + s["h"] + STROKE / 2, wordmark_colour)
    width = round(right + 4)
    return (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} 64" width="{width}" height="64" role="img" aria-label="Recall">'
        f"<title>Recall</title>{mark(variant, detail_colour)}{text}</svg>\n"
    )


def mark_only(variant: str) -> str:
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64" role="img" aria-label="Recall">'
        f"<title>Recall</title>{mark(variant, CREAM)}</svg>\n"
    )


def favicon(variant: str, tile_radius: int = 14) -> str:
    # On a dark tile so it reads on light and dark browser chrome alike, and
    # drawn heavier than the navbar mark: at 16px a stroke of 4 would be 1px.
    inner = mark(variant, CREAM, stroke=5.5)
    return (
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64">'
        f'<rect width="64" height="64" rx="{tile_radius}" fill="{INK_2}"/>'
        f'<g transform="translate(32 32.5) scale(0.9) translate(-32 -30)">{inner}</g></svg>\n'
    )


def sheet() -> str:
    def row(label, height, width):
        cells = "".join(
            f'<div class="bar" style="width:{width}px"><img src="variant-{v}.svg" height="{height}" alt="">'
            f'<span class="links"><span>Home</span><span>Search</span><span>My Library</span></span></div>'
            for v in DETAILS
        )
        return f'<h2>{label}</h2><div class="row">{cells}</div>'

    names = "".join(f"<div>{i + 1}. {v}</div>" for i, v in enumerate(DETAILS))
    icons = "".join(
        f'<div class="icons"><img src="favicon-{v}.svg" width="16" height="16" alt="">'
        f'<img src="favicon-{v}.svg" width="32" height="32" alt="">'
        f'<img src="favicon-{v}.svg" width="64" height="64" alt="">'
        f'<span class="light"><img src="favicon-{v}.svg" width="16" height="16" alt=""></span></div>'
        for v in DETAILS
    )
    return f"""<!doctype html><meta charset="utf-8"><title>Recall logo options</title>
<style>
  @font-face {{ font-family: "IBM Plex Sans"; font-weight: 500;
    src: url("../../Recall.Web/wwwroot/lib/fontsource/ibm-plex-sans/files/ibm-plex-sans-latin-500-normal.woff2") format("woff2"); }}
  body {{ margin: 0; padding: 28px 32px 32px; background: {INK}; color: {CREAM}; font: 500 14px "IBM Plex Sans", sans-serif; width: 1216px; }}
  h1 {{ margin: 0 0 4px; font-size: 20px; }} p {{ margin: 0 0 20px; opacity: .72; }}
  h2 {{ margin: 22px 0 8px; font-size: 12px; letter-spacing: .08em; text-transform: uppercase; opacity: .72; }}
  .row, .names {{ display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; align-items: start; }}
  .names {{ font-size: 16px; }}
  .bar {{ box-sizing: border-box; display: flex; align-items: center; gap: 20px; padding: 8px 16px; background: {INK_2};
          border-bottom: 1px solid rgba(255,255,255,.08); max-width: 100%; }}
  .links {{ display: flex; gap: 16px; opacity: .72; overflow: hidden; white-space: nowrap; }}
  .icons {{ display: flex; align-items: center; gap: 16px; }}
  .light {{ display: inline-flex; padding: 8px 12px; background: #dee1e6; border-radius: 8px 8px 0 0; }}
</style>
<h1>Recall logo options</h1>
<p>Mark in amber with a cream detail, wordmark in Big Shoulders Display 800 (as paths). On the navbar background.</p>
<div class="names">{names}</div>
{row("Desktop navbar, 32px high", 32, 384)}
{row("Phone navbar, 28px high (390px wide)", 28, 390)}
<h2>Favicon at 16, 32 and 64px, and at 16px on a light browser tab</h2>
<div class="row">{icons}</div>
"""


def og(variant: str) -> str:
    return f"""<!doctype html><meta charset="utf-8">
<style>
  @font-face {{ font-family: "IBM Plex Sans"; font-weight: 500;
    src: url("../../Recall.Web/wwwroot/lib/fontsource/ibm-plex-sans/files/ibm-plex-sans-latin-500-normal.woff2") format("woff2"); }}
  html, body {{ margin: 0; width: 1200px; height: 630px; }}
  body {{ display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 44px;
          background: {INK}; color: {CREAM}; font: 500 44px/1.25 "IBM Plex Sans", sans-serif; }}
  img {{ height: 190px; }}
  div {{ opacity: .82; text-align: center; }}
  span {{ color: {AMBER}; }}
</style>
<img src="variant-{variant}.svg" alt="">
<div>Track every show. <span>Never miss the next episode.</span></div>
"""


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", choices=list(DETAILS), default="play")
    variant = parser.parse_args().variant

    for v in DETAILS:
        (OUT / f"variant-{v}.svg").write_text(lockup(v, CREAM, CREAM))
        (OUT / f"favicon-{v}.svg").write_text(favicon(v))
    (OUT / "sheet.html").write_text(sheet())
    (OUT / "og.html").write_text(og(variant))

    (WWW / "images/logo.svg").write_text(lockup(variant, CREAM, CREAM))
    (WWW / "images/logo-light.svg").write_text(lockup(variant, INK, INK))
    (WWW / "images/logo-mark.svg").write_text(mark_only(variant))
    (WWW / "favicon.svg").write_text(favicon(variant))
    # iOS rounds the corners itself and wants an opaque square.
    (OUT / "touch-icon.svg").write_text(favicon(variant, tile_radius=0))
    print(f"wrote the '{variant}' variant")


if __name__ == "__main__":
    main()

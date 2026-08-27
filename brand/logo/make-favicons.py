#!/usr/bin/env python3
"""Regenerate the favicons from the brand mark.

    python3 brand/logo/make-favicons.py

Source of truth is `brand/logo/cryptosmith-favicon.svg` — a rounded-square plate
with the mark fitted by width. The geometry is reproduced here rather than
rasterised because the only SVG rasteriser on a stock macOS is Quick Look, which
mattes transparency onto white; a rounded icon needs transparent corners or every
dark browser tab shows a white box behind them.

Reproduced, not redrawn: the numbers below are lifted from that file. If the mark
or the plate changes there, change them here and re-run — do not eyeball it.

Outputs:
    src/web/favicon.ico                     16 · 32 · 48, transparent corners
    src/web/apple-touch-icon.png            180, flattened (iOS masks its own corners)
    src/CryptoSmithX.WebApp/wwwroot/favicon.ico

Needs Pillow (`pip install Pillow`). Nothing else.
"""

from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]

# ── geometry, straight out of cryptosmith-favicon.svg (a 512 viewBox) ───────
PLATE_R, EDGE_R, EDGE_W, EDGE_INSET = 112, 108, 8, 4
PLATE = (10, 8, 18)         # #0A0812  --ink-950
EDGE = (245, 184, 79, 77)   # #F5B84F at .30 — the sanctioned logo-plate border
MARK = (255, 217, 135)      # #FFD987
DOT = (185, 127, 46)        # #B97F2E

# the mark lives in a 40-unit box, placed by translate(-16.7 -16.7) scale(13.63):
# fitted by width to 82% of the plate, centred. See the comment in the SVG.
OFF, SCALE = -16.7, 13.63
STAR = [(20, 23.54), (15.97, 27.57), (12.43, 24.03), (16.46, 20),
        (12.43, 15.97), (15.97, 12.43), (20, 16.46), (24.03, 12.43),
        (27.57, 15.97), (23.54, 20), (27.57, 24.03), (24.03, 27.57)]
DOTS = [(6.6, 20), (33.4, 20)]
DOT_R = 2

SUPERSAMPLE = 4  # drawn 4× and reduced; PIL has no antialiased vector fill


def render(px: int) -> Image.Image:
    """The icon at `px` square, RGBA, corners transparent."""
    n = px * SUPERSAMPLE
    k = n / 512  # 512-viewBox units → device pixels

    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, n - 1, n - 1], radius=PLATE_R * k, fill=PLATE + (255,))

    # the edge is translucent gold, so it goes on its own layer and composites
    edge = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ins = EDGE_INSET * k
    ImageDraw.Draw(edge).rounded_rectangle(
        [ins, ins, n - 1 - ins, n - 1 - ins], radius=EDGE_R * k,
        outline=EDGE, width=max(1, round(EDGE_W * k)))
    img = Image.alpha_composite(img, edge)
    d = ImageDraw.Draw(img)

    def place(x, y):
        return ((OFF + SCALE * x) * k, (OFF + SCALE * y) * k)

    d.polygon([place(x, y) for x, y in STAR], fill=MARK + (255,))
    for x, y in DOTS:
        cx, cy = place(x, y)
        r = DOT_R * SCALE * k
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=DOT + (255,))

    return img.resize((px, px), Image.LANCZOS)


def main() -> None:
    web = ROOT / "src/web"
    app = ROOT / "src/CryptoSmithX.WebApp/wwwroot"

    # .ico carries its own small sizes; each is reduced from a clean 4× render
    # rather than from one big bitmap, so 16 px stays legible.
    # Largest first: Pillow builds the .ico from the image it is saved on and
    # cannot upscale, so saving from the 16 px frame silently yields a 16-only file.
    sizes = [48, 32, 16]
    frames = [render(s) for s in sizes]
    for out in (web / "favicon.ico", app / "favicon.ico"):
        frames[0].save(out, format="ICO",
                       sizes=[(s, s) for s in sizes], append_images=frames[1:])
        print(f"wrote {out.relative_to(ROOT)}  ({', '.join(f'{s}×{s}' for s in sizes)})")

    # iOS composites the icon onto its own tile and rounds the corners itself,
    # so this one is flattened onto the disc colour — transparency there shows
    # up as black corners on older devices.
    touch = Image.new("RGB", (180, 180), PLATE)
    coin = render(180)
    touch.paste(coin, (0, 0), coin)
    touch.save(web / "apple-touch-icon.png", format="PNG")
    print(f"wrote {(web / 'apple-touch-icon.png').relative_to(ROOT)}  (180×180, flattened)")


if __name__ == "__main__":
    main()

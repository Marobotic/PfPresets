#!/usr/bin/env python3
"""
Renders images/icon.png — the plugin's installer icon.

Same mark as the site's favicon and the top-left of pfa.marobotic.dev, at 512x512:
an accent square with a rising line across it. Geometry is the favicon's 32-unit
viewBox scaled 16x, so the two cannot drift apart.

Two things are deliberate:

  - The line's caps and joins are square and mitered, not round. The plugin draws
    at FrameRounding 0 almost everywhere and the site follows it; a rounded stroke
    here would be the one place the mark disagrees with everything it sits next to.
  - Drawn at 4x and resampled down. PIL has no antialiased primitives, and at 512
    the diagonals come out visibly stepped otherwise. Supersampling smooths the
    edges without rounding any corner, which is the distinction that matters.

Run:  python3 images/make_icon.py
"""

from PIL import Image, ImageDraw

SIZE = 512
SS = 4  # supersample factor

ACCENT = "#9b6dff"  # --accent, theme.css / PluginUI.Theme.cs
GROUND = "#171614"  # --ground

# The favicon, verbatim:
#   <rect width="32" height="32" fill="#9b6dff"/>
#   <path d="M5 23 L12 15 L17 20 L27 8" stroke="#171614" stroke-width="3.4"
#         stroke-linecap="square" stroke-linejoin="miter"/>
VIEWBOX = 32
PATH = [(5, 23), (12, 15), (17, 20), (27, 8)]
STROKE = 3.4

W = SIZE * SS
scale = W / VIEWBOX

img = Image.new("RGBA", (W, W), ACCENT)
d = ImageDraw.Draw(img)

pts = [(x * scale, y * scale) for x, y in PATH]
sw = STROKE * scale

# Segment at a time, with a square stamped at every vertex. ImageDraw's own
# joint="curve" rounds both the corners and the ends.
for a, b in zip(pts, pts[1:]):
    d.line([a, b], fill=GROUND, width=round(sw))

h = sw / 2
for x, y in pts:
    d.rectangle([x - h, y - h, x + h, y + h], fill=GROUND)

img = img.resize((SIZE, SIZE), Image.LANCZOS)

out = __file__.rsplit("/", 1)[0] + "/icon.png"
img.save(out, "PNG", optimize=True)
print(f"wrote {out} {img.size}")

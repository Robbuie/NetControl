"""Draws the application icon. Build-time only; nothing here ships.

Ported from File Compare's packaging/icon.py, which was ported from File Manager's.
The family shares a mark as well as a stylesheet: a dark rounded tile, light
shapes on it, one stroke of the accent. Redline PDF is a sheet with a red line
through it; File Manager is two panes with the active one's header in Drafting
blue; File Compare is two panes with the difference drawn across both.

NetControl is the laptop and the panel: one wide card along the top - the
machine the tool runs on - and three device cards below it on a bus. One
device, and the drop that reaches it, is drawn in the accent: the one being
given its address, which is what the tool is for in one shape. The accent is
cyan, NetControl's own default (Theme.DefaultAccent), so the four read as a set
without two of them sharing a colour. At 16px it is a bar, three squares and
one cyan one, which is still that.

Pillow rather than anything .NET on purpose: this is a chore, not a build step,
like refreshing oui.bin. Both outputs are committed, so a checkout builds the
app and the installer without Python.

    pip install pillow
    python tools/icon.py

Writes `src/NetControl.App/Assets/NetControl.ico` (the sizes Windows asks for,
embedded in the exe and used by the installer) and `assets/icon.png` (256px,
for the README).
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw

#: Everything is drawn at this size and reduced, which is what gives the
#: corners and the thin lines clean edges.
CANVAS = 1024
SCALE = CANVAS // 256

#: The tile and the cards are the same values File Compare's icon uses, so the
#: four sit together in a taskbar. The accent is Theme.cs's "cyan" triple.
TILE_TOP = (32, 36, 44)
TILE_BOTTOM = (16, 18, 22)
CARD = (233, 237, 244)
CARD_DIM = (203, 210, 221)
ROW = (168, 176, 189)
WIRE = (110, 118, 132)
ACCENT = (54, 191, 210)

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def _px(value: float) -> int:
    return int(round(value * SCALE))


def draw() -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    canvas = ImageDraw.Draw(image)

    gradient = Image.new("RGB", (1, CANVAS))
    for y in range(CANVAS):
        ratio = y / (CANVAS - 1)
        gradient.putpixel((0, y), tuple(
            int(round(top + (bottom - top) * ratio))
            for top, bottom in zip(TILE_TOP, TILE_BOTTOM)
        ))
    gradient = gradient.resize((CANVAS, CANVAS))
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, CANVAS - 1, CANVAS - 1), radius=_px(58), fill=255,
    )
    image.paste(gradient, (0, 0), mask)

    inset = _px(38)
    radius = _px(12)

    # The machine: a wide card along the top, two rows on it.
    host = (inset, _px(50), CANVAS - inset, _px(98))
    canvas.rounded_rectangle(host, radius=radius, fill=CARD)
    pad = _px(14)
    row_h = _px(7)
    for i, frac in enumerate((1.0, 0.55)):
        y = host[1] + _px(14) + i * _px(16)
        x1 = host[0] + pad + (host[2] - host[0] - 2 * pad) * frac
        canvas.rounded_rectangle((host[0] + pad, y, x1, y + row_h), radius=row_h // 2, fill=ROW)

    # Three devices on a bus. Equal widths, equal gaps, the same outer inset as
    # the host so the edges line up.
    gap = _px(16)
    width = (CANVAS - 2 * inset - 2 * gap) / 3
    dev_top = _px(134)
    dev_bottom = _px(206)
    devices = []
    for i in range(3):
        x0 = inset + i * (width + gap)
        devices.append((int(x0), dev_top, int(x0 + width), dev_bottom))

    # The bus, and a drop to each device. Wire grey, except the drop to the
    # device being addressed.
    wire = _px(7)
    bus_y = _px(116)
    trunk_x = CANVAS // 2
    canvas.rectangle((trunk_x - wire // 2, host[3], trunk_x + wire // 2, bus_y), fill=WIRE)
    centres = [(d[0] + d[2]) // 2 for d in devices]
    canvas.rectangle((centres[0] - wire // 2, bus_y - wire // 2,
                      centres[-1] + wire // 2, bus_y + wire // 2), fill=WIRE)
    chosen = 2
    for i, cx in enumerate(centres):
        colour = ACCENT if i == chosen else WIRE
        canvas.rectangle((cx - wire // 2, bus_y - wire // 2, cx + wire // 2, dev_top), fill=colour)

    for i, box in enumerate(devices):
        canvas.rounded_rectangle(box, radius=radius, fill=CARD if i == chosen else CARD_DIM)
        # The chosen device's header is the accent, the way File Manager's
        # active pane is: the one thing on the tile that is in colour.
        if i == chosen:
            header = (box[0], box[1], box[2], box[1] + _px(20))
            canvas.rounded_rectangle(header, radius=radius, fill=ACCENT)
            canvas.rectangle((box[0], header[3] - radius, box[2], header[3]), fill=ACCENT)
        start = box[1] + _px(30)
        for r in range(3):
            y = start + r * _px(14)
            frac = 0.5 if r == 2 else 1.0
            x1 = box[0] + _px(10) + (box[2] - box[0] - _px(20)) * frac
            canvas.rounded_rectangle((box[0] + _px(10), y, x1, y + row_h),
                                     radius=row_h // 2, fill=ROW)
    return image


def main() -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)
    master = draw()
    small = master.resize((256, 256), Image.LANCZOS)

    ico = os.path.join(root, "src", "NetControl.App", "Assets", "NetControl.ico")
    os.makedirs(os.path.dirname(ico), exist_ok=True)
    small.save(ico, format="ICO", sizes=[(s, s) for s in ICO_SIZES])

    png = os.path.join(root, "assets", "icon.png")
    os.makedirs(os.path.dirname(png), exist_ok=True)
    small.save(png, format="PNG")

    print(f"wrote {ico}")
    print(f"wrote {png}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

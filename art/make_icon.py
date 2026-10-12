"""Draws the Kohan II Mod Manager icon: a gold-rimmed crimson heater shield with "K2",
plus a "MODS" ribbon at the larger sizes. Writes src/K2ModManager/K2ModManager.ico
(16-256 px) and art/icon_preview.png. Needs Pillow; uses Georgia Bold and Segoe UI Black from Windows.

    py -3 art/make_icon.py
"""

import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "georgiab.ttf")
SMALL_FONT = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "seguibl.ttf")   # Segoe UI Black
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

CRIMSON_TOP = (150, 22, 30)
CRIMSON_BOTTOM = (88, 10, 18)
GOLD = (232, 188, 82)
GOLD_DARK = (150, 104, 30)
INK = (40, 6, 10)


def shield(w, h, inset):
    """Heater shield outline: straight sides for the top 45%, then curves to a point."""
    x0, x1, y0, y1 = inset, w - inset, inset, h - inset
    pts = [(x0, y0), (x1, y0)]
    mid = y0 + (y1 - y0) * 0.45
    pts.append((x1, mid))
    n = 40
    cx = (x0 + x1) / 2
    for i in range(1, n + 1):          # right side: quarter-ellipse-ish curve to the point
        t = i / n
        x = x1 - (x1 - cx) * (t ** 1.6)
        y = mid + (y1 - mid) * (1 - (1 - t) ** 1.9)
        pts.append((x, y))
    for i in range(n - 1, -1, -1):     # mirror on the left
        t = i / n
        x = x0 + (cx - x0) * (t ** 1.6)
        y = mid + (y1 - mid) * (1 - (1 - t) ** 1.9)
        pts.append((x, y))
    pts.append((x0, mid))
    return pts


def gradient(w, h, top, bottom):
    g = Image.new("RGB", (w, h))
    d = ImageDraw.Draw(g)
    for y in range(h):
        t = y / max(h - 1, 1)
        d.line([(0, y), (w, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)))
    return g


def fitted(draw, path, text, max_w, max_h, stroke_ratio=0.0):
    """Largest font from this file whose rendering of text fits max_w x max_h."""
    size = int(max_h * 1.6)
    while size > 8:
        font = ImageFont.truetype(path, size)
        l, t, r, b = draw.textbbox((0, 0), text, font=font, stroke_width=int(size * stroke_ratio))
        if r - l <= max_w and b - t <= max_h:
            return font, int(size * stroke_ratio)
        size = int(size * 0.97)
    return ImageFont.truetype(path, 8), 0


def centered_text(draw, box, text, font, fill, stroke=0, stroke_fill=None):
    l, t, r, b = draw.textbbox((0, 0), text, font=font, stroke_width=stroke)
    x = box[0] + (box[2] - box[0] - (r - l)) / 2 - l
    y = box[1] + (box[3] - box[1] - (b - t)) / 2 - t
    draw.text((x, y), text, font=font, fill=fill, stroke_width=stroke, stroke_fill=stroke_fill)


def render(size, ribbon):
    S = 1024                                        # draw big, scale down
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    w = int(S * (0.86 if ribbon else 0.94))
    left = (S - w) // 2
    rim = int(S * (0.055 if ribbon else 0.075))     # heavier rim at small sizes

    # soft shadow
    shadow = Image.new("L", (S, S), 0)
    ImageDraw.Draw(shadow).polygon([(x + left, y + S * 0.03) for x, y in shield(w, S, S * 0.03)], fill=110)
    img.paste((0, 0, 0, 255), (0, 0), shadow.filter(ImageFilter.GaussianBlur(S * 0.02)))

    outer = [(x + left, y) for x, y in shield(w, S, S * 0.03)]
    inner = [(x + left, y) for x, y in shield(w, S, S * 0.03 + rim)]
    d = ImageDraw.Draw(img)
    d.polygon(outer, fill=GOLD_DARK)
    d.polygon([(x + left, y) for x, y in shield(w, S, S * 0.03 + rim * 0.35)], fill=GOLD)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).polygon(inner, fill=255)
    img.paste(gradient(S, S, CRIMSON_TOP, CRIMSON_BOTTOM), (0, 0), mask)

    d = ImageDraw.Draw(img)
    inner_w = w - 2 * (S * 0.03 + rim)
    if ribbon:
        font, stroke = fitted(d, FONT, "K2", inner_w * 0.80, S * 0.40, stroke_ratio=0.025)
        centered_text(d, (0, S * 0.12, S, S * 0.61), "K2", font, GOLD, stroke=stroke, stroke_fill=INK)
        # ribbon across the lower shield
        ry0, ry1 = S * 0.635, S * 0.80
        rx0, rx1 = S * 0.10, S * 0.90
        tail = S * 0.05
        d.polygon([(rx0 - tail, ry0 + S * 0.03), (rx0 + S * 0.03, ry0 + S * 0.03), (rx0 + S * 0.03, ry1 + S * 0.03),
                   (rx0 - tail, ry1 + S * 0.03), (rx0 + tail * 0.2, (ry0 + ry1) / 2 + S * 0.03)], fill=GOLD_DARK)
        d.polygon([(rx1 + tail, ry0 + S * 0.03), (rx1 - S * 0.03, ry0 + S * 0.03), (rx1 - S * 0.03, ry1 + S * 0.03),
                   (rx1 + tail, ry1 + S * 0.03), (rx1 - tail * 0.2, (ry0 + ry1) / 2 + S * 0.03)], fill=GOLD_DARK)
        d.rectangle([rx0, ry0, rx1, ry1], fill=GOLD, outline=GOLD_DARK, width=int(S * 0.012))
        rfont = ImageFont.truetype(FONT, int(S * 0.125))
        centered_text(d, (rx0, ry0, rx1, ry1), "MODS", rfont, INK)
    else:
        # small sizes: a heavy sans without outline stays readable at 16-40 px
        font, _ = fitted(d, SMALL_FONT, "K2", inner_w * 0.92, S * 0.50)
        centered_text(d, (0, S * 0.06, S, S * 0.66), "K2", font, GOLD)

    out = img.resize((size, size), Image.LANCZOS)
    return out


def main():
    frames = [render(s, ribbon=s >= 48) for s in SIZES]
    ico = os.path.join(ROOT, "src", "K2ModManager", "K2ModManager.ico")
    frames[-1].save(ico, format="ICO", sizes=[(s, s) for s in SIZES], append_images=frames[:-1])
    # preview: every size side by side on light and dark backgrounds
    pad = 12
    W = sum(SIZES) + pad * (len(SIZES) + 1)
    sheet = Image.new("RGBA", (W, (256 + pad * 2) * 2), (240, 240, 240, 255))
    ImageDraw.Draw(sheet).rectangle([0, 256 + pad * 2, W, sheet.height], fill=(32, 32, 32, 255))
    for row in range(2):
        x = pad
        for s, f in zip(SIZES, frames):
            sheet.alpha_composite(f, (x, row * (256 + pad * 2) + pad + (256 - s)))
            x += s + pad
    sheet.save(os.path.join(ROOT, "art", "icon_preview.png"))
    print(f"wrote {ico} ({os.path.getsize(ico):,} bytes) and art/icon_preview.png")


if __name__ == "__main__":
    main()

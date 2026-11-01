"""Builds icon (.ico), PNG and installer BMP assets from the rendered branding.

Usage: python make_assets.py <brandingDir>
Prefers original artwork in <brandingDir>/original/ (vedar-logo.png, vedar-wordmark.png) when present.
"""
import os
import sys
from PIL import Image

d = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else ".")
gen = os.path.join(d, "generated")
orig = os.path.join(d, "original")


def load(name_original, name_generated):
    p = os.path.join(orig, name_original)
    if os.path.exists(p):
        print("using original artwork", p)
        return Image.open(p).convert("RGBA")
    return Image.open(os.path.join(gen, name_generated)).convert("RGBA")


logo = load("vedar-logo.png", "vedar-logo-1024.png")
wordmark = load("vedar-wordmark.png", "vedar-wordmark-1550.png")
icon_src = Image.open(os.path.join(gen, "vedar-icon-1024.png")).convert("RGBA")
if os.path.exists(os.path.join(orig, "vedar-logo.png")):
    # Build the icon plate from the original logo.
    plate = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    from PIL import ImageDraw
    ImageDraw.Draw(plate).rounded_rectangle([16, 16, 1008, 1008], radius=200, fill=(255, 255, 255, 255), outline=(213, 227, 245, 255), width=12)
    l = logo.copy()
    l.thumbnail((880, 880), Image.LANCZOS)
    plate.alpha_composite(l, ((1024 - l.width) // 2, (1024 - l.height) // 2))
    icon_src = plate

sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
icon_src.save(os.path.join(d, "vedar.ico"), sizes=[(s, s) for s in sizes])
logo.resize((256, 256), Image.LANCZOS).save(os.path.join(d, "vedar-logo-256.png"))
logo.resize((128, 128), Image.LANCZOS).save(os.path.join(d, "vedar-logo-128.png"))
icon_src.resize((512, 512), Image.LANCZOS).save(os.path.join(d, "vedar-icon-512.png"))
wm = wordmark.copy()
wm.thumbnail((620, 120), Image.LANCZOS)
wm.save(os.path.join(d, "vedar-wordmark.png"))

# Inno Setup wizard images (24-bit BMP), 100% and 200% DPI.
navy = (11, 31, 58)
for scale, (w, h) in ((1, (164, 314)), (2, (328, 628))):
    img = Image.new("RGB", (w, h), navy)
    l = icon_src.copy()
    l.thumbnail((int(w * 0.8), int(w * 0.8)), Image.LANCZOS)
    img.paste(l, ((w - l.width) // 2, int(h * 0.18)), l)
    img.save(os.path.join(d, f"wizard-image{'' if scale == 1 else '-2x'}.bmp"))
for scale, (w, h) in ((1, (55, 58)), (2, (110, 116))):
    img = Image.new("RGB", (w, h), (255, 255, 255))
    l = icon_src.copy()
    l.thumbnail((w - 4, h - 4), Image.LANCZOS)
    img.paste(l, ((w - l.width) // 2, (h - l.height) // 2), l)
    img.save(os.path.join(d, f"wizard-small{'' if scale == 1 else '-2x'}.bmp"))
print("assets written to", d)

#!/usr/bin/env python3
"""App icon: three EQ sliders on a blue rounded square."""
import sys
from PIL import Image, ImageDraw

S = 256
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle((8, 8, S - 8, S - 8), 52, fill="#2563eb")
for x, knob in ((72, 150), (128, 92), (184, 128)):
    d.rounded_rectangle((x - 9, 52, x + 9, 204), 9, fill=(255, 255, 255, 110))
    d.ellipse((x - 26, knob - 26, x + 26, knob + 26), fill="white")
img.save(sys.argv[1], sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])

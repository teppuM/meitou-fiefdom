"""Builds Fiefdom's icon in the style of the Amputations and Execution thumbnails: an in-game backdrop cropped square to 1024, a drawn
silhouette of a walled town with lit windows and a banner, the title in large cream Georgia Bold and "[ MeitouClient ]" in gold under it,
both with a dark outline and drop shadow, and an optional BETA stamp in the corner.
Usage: python make_icon.py <backdrop.png> <out.png> [--beta]"""
import sys

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont

S = 1024
SIL = (30, 19, 14)            # the town's silhouette
LIT = (255, 186, 92)          # lit windows and the gate's glow
FONT = r'C:\Windows\Fonts\georgiab.ttf'


def backdrop(path):
    im = Image.open(path).convert('RGB')
    w, h = im.size
    im = im.crop(((w - h) // 2, 0, (w - h) // 2 + h, h)).resize((S, S), Image.LANCZOS)
    im = ImageEnhance.Color(im).enhance(1.1)
    warm = Image.new('RGB', (S, S), (255, 150, 80))                  # an evening warmth over the scene
    im = Image.blend(im, warm, 0.12)
    shade = Image.new('L', (S, S), 0)                                 # darken the top for the title and the foot for the town
    g = ImageDraw.Draw(shade)
    for y in range(S):
        top = 150 * max(0, 1 - y / 360) ** 1.5
        foot = 120 * max(0, (y - 620) / 404) ** 1.2
        g.line([(0, y), (S, y)], fill=int(max(top, foot)))
    return Image.composite(Image.new('RGB', (S, S), (8, 5, 4)), im, shade)


def merlons(d, x0, x1, y, size=26, gap=22):
    x = x0
    while x + size <= x1:
        d.rectangle([x, y - size, x + size, y], fill=SIL)
        x += size + gap


def town(im):
    glow = Image.new('L', (S, S), 0)
    gd = ImageDraw.Draw(glow)
    d = ImageDraw.Draw(im)

    # Houses behind the wall: Kenshi's flat-roofed blocks at different heights.
    for x0, x1, top in [(300, 380, 700), (640, 735, 690), (330, 410, 650), (705, 760, 640)]:
        d.rectangle([x0, top, x1, 760], fill=SIL)
        d.rectangle([x0 - 6, top - 8, x1 + 6, top], fill=SIL)

    # The keep, with a banner on its pole.
    d.rectangle([420, 520, 604, 760], fill=SIL)
    merlons(d, 420, 604, 520, 24, 16)
    d.rectangle([509, 360, 515, 500], fill=SIL)
    d.polygon([(515, 366), (600, 380), (585, 404), (602, 428), (515, 420)], fill=(150, 36, 26))

    # The wall and its two towers.
    d.rectangle([110, 740, 914, 900], fill=SIL)
    merlons(d, 110, 914, 740)
    d.rectangle([150, 580, 270, 900], fill=SIL)
    merlons(d, 150, 270, 580, 24, 24)
    d.rectangle([754, 610, 874, 900], fill=SIL)
    merlons(d, 754, 874, 610, 24, 24)
    d.rectangle([0, 892, S, S], fill=(20, 12, 9))

    # The gate, open and lit from within, and lit windows: someone lives here.
    d.rectangle([462, 800, 562, 900], fill=LIT)
    d.pieslice([462, 750, 562, 850], 180, 360, fill=LIT)
    gd.rectangle([440, 770, 584, 900], fill=150)
    windows = [(486, 580), (526, 580), (486, 650), (526, 650), (198, 640), (198, 720), (802, 670), (802, 740), (346, 720), (676, 715),
               (365, 670)]
    for x, y in windows:
        d.rectangle([x, y, x + 14, y + 22], fill=LIT)
        gd.rectangle([x - 8, y - 8, x + 22, y + 30], fill=120)

    glow = glow.filter(ImageFilter.GaussianBlur(22))
    return Image.composite(Image.new('RGB', (S, S), LIT), im, glow.point(lambda v: v // 2))


def title(im, beta):
    d = ImageDraw.Draw(im)

    def centred(text, size, y, fill, stroke):
        font = ImageFont.truetype(FONT, size)
        while d.textlength(text, font=font) > 940:
            size -= 4
            font = ImageFont.truetype(FONT, size)
        x = (S - d.textlength(text, font=font)) / 2
        for dx, dy in [(5, 6), (3, 4)]:
            d.text((x + dx, y + dy), text, font=font, fill=(10, 6, 4))
        d.text((x, y), text, font=font, fill=fill, stroke_width=stroke, stroke_fill=(22, 12, 6))

    centred('FIEFDOM', 170, 40, (236, 228, 208), 6)
    centred('[ MeitouClient ]', 64, 245, (232, 186, 92), 4)
    if not beta:
        return im

    # A stamp in the top right corner, turned a little.
    stamp = Image.new('RGBA', (300, 120), (0, 0, 0, 0))
    sd = ImageDraw.Draw(stamp)
    sd.rounded_rectangle([6, 6, 294, 114], 14, outline=(214, 60, 40, 255), width=9)
    font = ImageFont.truetype(FONT, 76)
    tw = sd.textlength('BETA', font=font)
    sd.text(((300 - tw) / 2, 14), 'BETA', font=font, fill=(214, 60, 40, 255))
    stamp = stamp.rotate(-14, expand=True, resample=Image.BICUBIC)
    im = im.convert('RGBA')
    im.alpha_composite(stamp, (S - stamp.width - 18, 330))
    return im.convert('RGB')


src, out = sys.argv[1], sys.argv[2]
icon = title(town(backdrop(src)), '--beta' in sys.argv)
icon.save(out)
print(out, icon.size)

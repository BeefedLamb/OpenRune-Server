"""Export the login title art, bitmap fonts and flame runes for the Unity client.

Usage: python export_title.py <cache_dir> <out_dir>

Writes <out_dir>/ui/title.jpg (index 10, the left half of the background), the index-8
sprites logo, titlebox, titlebutton and the 12 rune frames, and b12.fnt / p12.fnt.
A font file is "FNT1", then 256 glyphs: u8 advance, ox, oy, sw, sh, then sw*sh alpha bytes.
Advances come from index 13 (the client's font advance table).
"""
import os
import sys

from PIL import Image

from cache import Cache
from export_textures import decode_sprite

SPRITES = 8
FONTS = 13
TITLES = 10
NAMES = ("logo", "titlebox", "titlebutton")
FONT_NAMES = ("b12_full", "p12_full")


def sprite_metrics(data):
    n = len(data)
    count = (data[n - 2] << 8) | data[n - 1]
    p = n - 7 - count * 8
    p += 5
    rows = []
    for _ in range(4):
        row = []
        for _c in range(count):
            row.append((data[p] << 8) | data[p + 1])
            p += 2
        rows.append(row)
    return rows  # ox, oy, sw, sh


def write_font(cache, ui, name):
    sid = cache.archive_by_name(SPRITES, name).id
    raw = cache.archive_data(SPRITES, sid)
    frames = decode_sprite(raw)
    ox, oy, sw, sh = sprite_metrics(raw)
    advances = cache.files(FONTS, cache.archive_by_name(FONTS, name).id)[0]
    out = bytearray(b"FNT1")
    for i in range(256):
        w, h = sw[i], sh[i]
        adv = advances[i] if i < len(advances) else 0
        out += bytes((adv & 255, ox[i] & 255, oy[i] & 255, w & 255, h & 255))
        if w == 0 or h == 0:
            continue
        canvas_w = frames[i][0]
        pix = frames[i][2]
        for y in range(h):
            for x in range(w):
                o = ((oy[i] + y) * canvas_w + (ox[i] + x)) * 4
                out.append(pix[o + 3] if o + 3 < len(pix) else 0)
    path = os.path.join(ui, name.split("_")[0] + ".fnt")
    with open(path, "wb") as f:
        f.write(out)
    print(name, len(out))


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    ui = os.path.join(out_dir, "ui")
    os.makedirs(ui, exist_ok=True)
    jpg = cache.archive_data(TITLES, cache.archive_by_name(TITLES, "title.jpg").id)
    with open(os.path.join(ui, "title.jpg"), "wb") as f:
        f.write(jpg)
    for name in NAMES:
        archive = cache.archive_by_name(SPRITES, name)
        width, height, pixels = decode_sprite(cache.archive_data(SPRITES, archive.id))[0]
        Image.frombytes("RGBA", (width, height), pixels).save(os.path.join(ui, name + ".png"))
        print(name, width, height)
    runes = decode_sprite(cache.archive_data(SPRITES, cache.archive_by_name(SPRITES, "runes").id))
    for i, (width, height, pixels) in enumerate(runes):
        Image.frombytes("RGBA", (width, height), pixels).save(os.path.join(ui, "rune%d.png" % i))
    print("runes", len(runes))
    for name in FONT_NAMES:
        write_font(cache, ui, name)
    print("title.jpg", len(jpg))


if __name__ == "__main__":
    main()

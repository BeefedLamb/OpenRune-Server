"""Export the login title art for the Unity client.

Usage: python export_title.py <cache_dir> <out_dir>

Writes <out_dir>/ui/title.jpg (index 10, archive "title.jpg", the left half of the
background) and the index-8 sprites logo, titlebox and titlebutton as PNG.
"""
import os
import sys

from PIL import Image

from cache import Cache
from export_textures import decode_sprite

SPRITES = 8
TITLES = 10
NAMES = ("logo", "titlebox", "titlebutton")


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
    print("title.jpg", len(jpg))


if __name__ == "__main__":
    main()

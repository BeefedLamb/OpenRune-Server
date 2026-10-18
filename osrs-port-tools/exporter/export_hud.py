"""Export the minimap frame, compass, dots and orbs for the Unity client.

Usage: python export_hud.py <cache_dir> <out_dir>

Writes <out_dir>/ui/hud/*.png from index 8, plus ui/p11.fnt.
"""
import os
import sys

from PIL import Image

from cache import Cache
from export_textures import decode_sprite
from export_title import write_font

SPRITES = 8
OUT = {
    "mapback": 12,
    "compass": 169,
    "maparrow": 424,
    "mapdots": 300,
    "orb_empty": 1059,
    "orb_hp": 1060,
    "orb_prayer": 1063,
    "orb_run": 1064,
    "orb_spec": 1607,
    "icon_hp": 1067,
    "icon_prayer": 1068,
    "icon_walk": 1069,
    "icon_run": 1070,
    "icon_spec": 1610,
    "orb_frame": 1071,
}


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    hud = os.path.join(out_dir, "ui", "hud")
    os.makedirs(hud, exist_ok=True)
    for name, sid in OUT.items():
        frames = decode_sprite(cache.archive_data(SPRITES, sid))
        for i, (width, height, pixels) in enumerate(frames):
            fn = name + ("" if len(frames) == 1 else str(i)) + ".png"
            Image.frombytes("RGBA", (width, height), pixels).save(os.path.join(hud, fn))
        print(name, len(frames), frames[0][0], frames[0][1])
    write_font(cache, os.path.join(out_dir, "ui"), "p11_full")


if __name__ == "__main__":
    main()

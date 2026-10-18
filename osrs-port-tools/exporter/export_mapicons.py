"""Export minimap wall flags, map-scene sprites and map-function icons.

Usage: python export_mapicons.py <cache_dir> <out_dir>

Rewrites <out_dir>/world/locs.txt with three extra columns (wallOrDoor, mapScene, mapArea)
and writes <out_dir>/ui/hud/mapscene/<id>.png plus <out_dir>/ui/hud/mapfunc/<areaId>.png.
"""
import os
import sys

from PIL import Image

from cache import Buf, Cache
from defs import LocDefs
from export_textures import decode_sprite

SPRITES = 8
MAPSCENE = 317


def decode_area(data):
    b = Buf(data)
    sprite = -1
    while True:
        op = b.u8()
        if op == 0:
            return sprite
        if op == 1:
            sprite = b.big_smart2()
        elif op in (2, 18, 25):
            b.big_smart2()
        elif op == 3 or op == 17 or 10 <= op <= 14:
            b.string()
        elif op in (4, 5):
            b.u24()
        elif op in (6, 7, 8, 28, 29, 30):
            b.u8()
        elif op == 15:
            n = b.u8()
            for _ in range(n * 2):
                b.i16()
            b.i32()
            m = b.u8()
            for _ in range(m):
                b.i32()
            for _ in range(n):
                b.i8()
        elif op == 16:
            pass
        elif op == 19:
            b.u16()
        elif op in (21, 22):
            b.i32()
        elif op == 23:
            b.u8(); b.u8(); b.u8()
        elif op == 24:
            b.i16(); b.i16()
        else:
            raise ValueError(f"area opcode {op}")


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    locdefs = LocDefs(cache)
    world = os.path.join(out_dir, "world")
    os.makedirs(world, exist_ok=True)
    with open(os.path.join(world, "locs.txt"), "w", encoding="utf-8") as f:
        for lid in sorted(locdefs.raw):
            raw = locdefs.get(lid)
            d = locdefs.resolve(lid)
            if raw is None or d is None or (d.models is None and d.name == "null"):
                continue
            ops = "|".join((o or "").replace("|", "/") for o in d.ops)
            f.write(
                f"{lid}\t{d.name}\t{d.size_x}\t{d.size_y}\t{ops}\t{1 if d.rotated else 0}\t"
                f"{d.solid}\t{d.offset[0]}\t{d.offset[1]}\t{d.offset[2]}\t{d.id}\t"
                f"{d.wall_or_door}\t{d.map_scene}\t{d.map_area}\n"
            )
    scene_dir = os.path.join(out_dir, "ui", "hud", "mapscene")
    func_dir = os.path.join(out_dir, "ui", "hud", "mapfunc")
    os.makedirs(scene_dir, exist_ok=True)
    os.makedirs(func_dir, exist_ok=True)
    frames = decode_sprite(cache.archive_data(SPRITES, MAPSCENE))
    for i, (width, height, pixels) in enumerate(frames):
        Image.frombytes("RGBA", (width, height), pixels).save(os.path.join(scene_dir, f"{i}.png"))
    areas = cache.files(2, 35)
    written = 0
    for aid, data in areas.items():
        sid = decode_area(data)
        if sid is None or sid < 0:
            continue
        try:
            decoded = decode_sprite(cache.archive_data(SPRITES, sid))
        except Exception:
            continue
        if not decoded:
            continue
        width, height, pixels = decoded[0]
        Image.frombytes("RGBA", (width, height), pixels).save(os.path.join(func_dir, f"{aid}.png"))
        written += 1
    print(f"mapscene {len(frames)}, mapfunc {written}")


if __name__ == "__main__":
    main()

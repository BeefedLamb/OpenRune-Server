"""Export the body-part (identkit) and worn-item models the Unity client needs to draw players.

Usage: python export_player_models.py <cache_dir> <out_dir>

Run `gradlew :or-cache:exportPlayerParts` first: it writes <out_dir>/world/player_items.txt
(worn models of every equipable item). This script then
  * decodes every identkit (config archive 3) itself and writes <out_dir>/world/player_kits.txt:
      idkId \t bodyPart \t nonSelectable(0/1) \t models(a,b,..) \t recolours(from:to,..) \t retextures(from:to,..)
  * writes every model referenced by kits and items to <out_dir>/world/player_models.bin.gz, in
    the same format as models.bin.gz / npc_models.bin.gz (Unity's WorldData.LoadModels reads it).
"""
import os
import sys

from cache import Buf, Cache
from defs import CONFIG_INDEX
from export_npcs import write_models_to

IDK_ARCHIVE = 3


def decode_kit(data):
    """Identkit config. Rev 240 stores model ids as i32 (opcode 5, chatheads 70-79)."""
    b = Buf(data)
    part, nonsel, models, recol, retex = -1, False, [], [], []
    while True:
        op = b.u8()
        if op == 0:
            break
        if op == 1:
            part = b.u8()
        elif op == 2:
            n = b.u8()
            models = [b.u16() for _ in range(n)]
        elif op == 3:
            nonsel = True
        elif op == 5:
            n = b.u8()
            models = [b.i32() for _ in range(n)]
        elif op == 40:
            n = b.u8()
            recol = [(b.u16(), b.u16()) for _ in range(n)]
        elif op == 41:
            n = b.u8()
            retex = [(b.u16(), b.u16()) for _ in range(n)]
        elif 60 <= op < 70:
            b.u16()
        elif 70 <= op < 80:
            b.i32()
        else:
            raise ValueError(f"unknown identkit opcode {op}")
    return part, nonsel, models, recol, retex


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    world = os.path.join(out_dir, "world")
    cache = Cache(cache_dir)
    ids = set()
    kits = bad = 0
    with open(os.path.join(world, "player_kits.txt"), "w", encoding="utf-8") as f:
        f.write("# OPK1\n")
        for kid, data in sorted(cache.files(CONFIG_INDEX, IDK_ARCHIVE).items()):
            try:
                part, nonsel, models, recol, retex = decode_kit(data)
            except Exception as e:
                bad += 1
                print("identkit", kid, e)
                continue
            kits += 1
            ids.update(m for m in models if m >= 0)
            f.write("\t".join([
                str(kid), str(part), "1" if nonsel else "0",
                ",".join(str(m) for m in models),
                ",".join(f"{a}:{c}" for a, c in recol),
                ",".join(f"{a}:{c}" for a, c in retex),
            ]) + "\n")
    items = 0
    with open(os.path.join(world, "player_items.txt"), encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if p[0] != "I":
                continue
            items += 1
            for field in (p[2], p[3]):
                ids.update(int(m) for m in field.split(",") if m and int(m) >= 0)
    path = os.path.join(world, "player_models.bin.gz")
    write_models_to(path, cache, ids)
    print(f"identkits {kits} (bad {bad}), wearable items {items}, player models {len(ids)}, "
          f"{os.path.getsize(path) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()

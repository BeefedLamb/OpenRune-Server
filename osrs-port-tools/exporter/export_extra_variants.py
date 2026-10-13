"""Add the loc variants the Unity client needs for runtime loc changes (opened doors, tree
stumps, depleted rocks...) that may not appear in the static map, and write locs.txt.

Usage: python3 export_extra_variants.py <cache_dir> <out_dir>
Appends to <out_dir>/world/variants.bin.gz and models.bin.gz (existing ids keep their index) and
rewrites locs.txt: id, name, sizeX, sizeY, op1|..|op5, rotated, solid, offX, offY, offZ, resolvedId.
"""
import gzip
import os
import struct
import sys

from cache import Cache
from defs import LocDefs
from export_npcs import write_models_to
from verify_world import read_variants


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    out = os.path.join(out_dir, "world")
    cache = Cache(cache_dir)
    vpath = os.path.join(out, "variants.bin.gz")
    tex, variants = read_variants(vpath)
    have = {(v[0], v[1], bool(v[2])) for v in variants}
    models_have = set()
    for v in variants:
        models_have.update(v[3])
    locdefs = LocDefs(cache)
    map_names = {locdefs.resolve(lid).name for lid in {v[0] for v in variants}}

    extra = []
    new_models = set()
    for lid in sorted(locdefs.raw):
        d = locdefs.resolve(lid)
        if d is None or d.models is None or d.name == "null":
            continue
        if not (d.name in map_names or any(d.ops)):
            continue
        for t in sorted(set(d.model_types or [10])):
            ids = d.model_for(t)
            if not ids:
                continue
            key = (d.id, t, bool(d.rotated))
            if key in have:
                continue
            have.add(key)
            extra.append((d, t, bool(d.rotated), ids))
            new_models.update(i for i in ids if i not in models_have)

    with gzip.open(vpath, "wb", 6) as f:
        f.write(struct.pack("<H", len(tex)))
        for tid, hsl in sorted(tex.items()):
            f.write(struct.pack("<HH", tid, hsl))
        f.write(struct.pack("<I", len(variants) + len(extra)))
        for lid, mtype, mirror, ids, rec, ret, scale in variants:
            write_variant(f, lid, mtype, mirror, ids, rec, ret, scale)
        for d, mtype, mirror, ids in extra:
            write_variant(f, d.id, mtype, mirror, ids, d.recolor, d.retexture, d.scale)

    if new_models:
        write_models_to(os.path.join(out, "models_extra.bin.gz"), cache, new_models)

    with open(os.path.join(out, "locs.txt"), "w", encoding="utf-8") as f:
        for lid in sorted(locdefs.raw):
            raw = locdefs.get(lid)
            d = locdefs.resolve(lid)
            if raw is None or d is None or (d.models is None and d.name == "null"):
                continue
            ops = "|".join((o or "").replace("|", "/") for o in d.ops)
            f.write(
                f"{lid}\t{d.name}\t{d.size_x}\t{d.size_y}\t{ops}\t{1 if d.rotated else 0}\t"
                f"{d.solid}\t{d.offset[0]}\t{d.offset[1]}\t{d.offset[2]}\t{d.id}\n"
            )
    print(f"variants {len(variants)} + {len(extra)}, new models {len(new_models)}")


def write_variant(f, lid, mtype, mirror, ids, rec, ret, scale):
    f.write(struct.pack("<IBBB", lid, mtype, 1 if mirror else 0, len(ids)))
    f.write(b"".join(struct.pack("<I", i) for i in ids))
    f.write(struct.pack("<B", len(rec)))
    f.write(b"".join(struct.pack("<HH", a, b) for a, b in rec))
    f.write(struct.pack("<B", len(ret)))
    f.write(b"".join(struct.pack("<HH", a, b) for a, b in ret))
    f.write(struct.pack("<hhh", *scale))


if __name__ == "__main__":
    main()

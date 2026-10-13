"""Export NPC definitions and the models they use for the Unity client.

Usage: python3 export_npcs.py <cache_dir> <out_dir>
Writes <out_dir>/world/npcs.txt (one NPC per line, tab-separated:
  id, name, size, combatLevel, scaleXZ, scaleY, models(comma), recolors(a:b,...), op1|op2|op3|op4|op5)
and <out_dir>/world/npc_models.bin.gz in the same format as models.bin.gz.
Also rewrites locs.json as locs.txt (id, name, sizeX, sizeY, op1|..|op5).
"""
import gzip
import json
import os
import struct
import sys

from cache import Buf, Cache
from defs import CONFIG_INDEX, LocDefs
from models import ModelCache

NPC_ARCHIVE = 9


class NpcDef:
    def __init__(self, nid):
        self.id = nid
        self.name = "null"
        self.models = []
        self.size = 1
        self.combat = -1
        self.scale_xz = 128
        self.scale_y = 128
        self.recolor = []
        self.retexture = []
        self.ops = [""] * 5
        self.morphs = None
        self.visible = True


def decode_npc(nid, data):
    d = NpcDef(nid)
    b = Buf(data)
    while True:
        op = b.u8()
        if op == 0:
            break
        if op == 1:
            n = b.u8()
            d.models = [b.u16() for _ in range(n)]
        elif op == 2:
            d.name = b.string()
        elif op == 12:
            d.size = b.u8()
        elif op in (13, 14, 15, 16, 18, 103, 114, 116, 124, 126, 146):
            b.u16()
        elif op in (17, 115, 117):
            b.u16(); b.u16(); b.u16(); b.u16()
        elif 30 <= op < 35:
            t = b.string()
            d.ops[op - 30] = "" if t.lower() == "hidden" else t
        elif op == 40:
            n = b.u8()
            d.recolor = [(b.u16(), b.u16()) for _ in range(n)]
        elif op == 41:
            n = b.u8()
            d.retexture = [(b.u16(), b.u16()) for _ in range(n)]
        elif op == 42:
            b.u16()
        elif op in (60, 62):
            n = b.u8()
            for _ in range(n):
                b.u16() if op == 60 else b.i32()
        elif op == 61:
            n = b.u8()
            d.models = [b.i32() for _ in range(n)]
        elif 74 <= op <= 79:
            b.u16()
        elif op in (93, 99, 107, 109, 111, 122, 123, 129, 130, 145, 147):
            if op == 107:
                d.visible = False
        elif op == 95:
            d.combat = b.u16()
        elif op == 97:
            d.scale_xz = b.u16()
        elif op == 98:
            d.scale_y = b.u16()
        elif op in (100, 101):
            b.i8()
        elif op == 102:
            bits = b.u8()
            n = bits.bit_length()
            for i in range(n):
                if bits & (1 << i):
                    if b.d[b.p] & 0x80:
                        b.i32()
                    else:
                        b.u16()
                    if b.d[b.p] < 128:
                        b.u8()
                    else:
                        b.u16()
        elif op in (106, 118):
            b.u16(); b.u16()
            if op == 118:
                b.u16()
            n = b.u8()
            d.morphs = [b.u16() for _ in range(n + 1)]
        elif op == 249:
            n = b.u8()
            for _ in range(n):
                kind = b.u8()
                b.u24()
                if kind == 1:
                    b.string()
                elif kind == 2:
                    b.i32(); b.i32()
                else:
                    b.i32()
        elif op == 251:
            b.u8(); b.u8(); b.string()
        elif op == 252:
            b.u8(); b.u16(); b.u16(); b.i32(); b.i32(); b.string()
        elif op == 253:
            b.u8(); b.u16(); b.u16(); b.u16(); b.i32(); b.i32(); b.string()
        else:
            raise ValueError(f"npc {nid}: unknown opcode {op}")
    return d


def write_models(path, cache, ids):
    mc = ModelCache(cache)
    with gzip.open(path, "wb", 6) as f:
        f.write(struct.pack("<I", len(ids)))
        for mid in sorted(ids):
            m = mc.get(mid)
            if m is None or not m.vx:
                f.write(struct.pack("<IHI", mid, 0, 0))
                continue
            faces = []
            for i in range(len(m.f1)):
                if m.render_types and m.render_types[i] == 2:
                    continue
                if m.alphas and m.alphas[i] >= 254:
                    continue
                tex = m.textures[i] if m.textures else -1
                faces.append((m.f1[i], m.f3[i], m.f2[i], m.colors[i] & 0xFFFF, tex))
            f.write(struct.pack("<IHI", mid, len(m.vx), len(faces)))
            f.write(b"".join(struct.pack("<hhh", c16(x), c16(-y), c16(z)) for x, y, z in zip(m.vx, m.vy, m.vz)))
            f.write(b"".join(struct.pack("<HHHHh", a, b, c, col, tex) for a, b, c, col, tex in faces))


def c16(v):
    return max(-32768, min(32767, int(round(v))))


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    out = os.path.join(out_dir, "world")
    os.makedirs(out, exist_ok=True)
    cache = Cache(cache_dir)
    npcs = {}
    bad = 0
    for nid, data in cache.files(CONFIG_INDEX, NPC_ARCHIVE).items():
        try:
            npcs[nid] = decode_npc(nid, data)
        except Exception as e:
            bad += 1
            if bad < 5:
                print("npc", nid, e)
    models = set()
    with open(os.path.join(out, "npcs.txt"), "w", encoding="utf-8") as f:
        for nid in sorted(npcs):
            d = npcs[nid]
            models.update(d.models)
            f.write("\t".join([
                str(nid), d.name.replace("\t", " "), str(d.size), str(d.combat),
                str(d.scale_xz), str(d.scale_y),
                ",".join(str(m) for m in d.models),
                ",".join(f"{a}:{b}" for a, b in d.recolor),
                ",".join(f"{a}:{b}" for a, b in d.retexture),
                "|".join(o.replace("|", "/") for o in d.ops),
            ]) + "\n")
    write_models(os.path.join(out, "npc_models.bin.gz"), cache, models)
    locs = json.load(open(os.path.join(out, "locs.json")))
    locdefs = LocDefs(cache)
    with open(os.path.join(out, "locs.txt"), "w", encoding="utf-8") as f:
        for lid in sorted(int(k) for k in locs):
            d = locdefs.get(lid)
            if d is None:
                continue
            ops = "|".join((o or "").replace("|", "/") for o in d.ops)
            f.write(f"{lid}\t{d.name}\t{d.size_x}\t{d.size_y}\t{ops}\n")
    print(f"npcs {len(npcs)} (bad {bad}), npc models {len(models)}, locs {len(locs)}")


if __name__ == "__main__":
    main()

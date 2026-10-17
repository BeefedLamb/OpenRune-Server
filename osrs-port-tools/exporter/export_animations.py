"""Export OSRS animation data (frames + framemaps) and model vertex labels for the Unity client.

Usage: python export_animations.py <cache_dir> <out_dir>

Run `gradlew :or-cache:exportSequences` first: it writes <out_dir>/world/seqs.txt (every sequence,
decoded by OpenRune's rev-aware codec). This script then writes:
  * <out_dir>/world/anims.bin.gz   frame-bases (framemaps) and frames used by legacy (non-skeletal)
    sequences, little-endian:
      "OAN1", u32 framemapCount, per framemap: i32 id, u16 n, u8 types[n], per group: u16 c, u8 labels[c]
              u32 frameCount,    per frame:    i32 frameId, u16 framemapId, u16 t,
                                               per transform: u16 group, i16 dx, i16 dy, i16 dz
  * <out_dir>/world/model_skins.bin.gz  per-vertex labels (vertex skins) of NPC + player models:
      u32 count, per model: i32 id, u16 vertexCount, u8 labels[vertexCount]
Frame decoding follows the client (FrameLoader): an implicit origin (type 0) transform is inserted
before a non-origin transform when the frame skipped the group's preceding origin.
Skeletal ("maya") sequences (skeletalId >= 0) are not exported (flagged as unsupported).
"""
import gzip
import os
import struct
import sys

from cache import Buf, Cache
from models import ModelCache

FRAME_INDEX = 0
FRAMEMAP_INDEX = 1


def decode_framemap(data):
    b = Buf(data)
    n = b.u8()
    types = [b.u8() for _ in range(n)]
    counts = [b.u8() for _ in range(n)]
    groups = [[b.u8() for _ in range(c)] for c in counts]
    return types, groups


def decode_frame(data, framemaps, cache, fm_cache):
    b = Buf(data)
    fm_id = b.u16()
    n = b.u8()
    vals = Buf(data, 3 + n)
    if fm_id not in fm_cache:
        try:
            fm_cache[fm_id] = decode_framemap(cache.archive_data(FRAMEMAP_INDEX, fm_id))
        except Exception:
            fm_cache[fm_id] = None
    fm = fm_cache[fm_id]
    if fm is None:
        return None
    types = fm[0]
    out = []
    last = -1
    for i in range(n):
        attr = b.u8()
        if attr <= 0:
            continue
        if i >= len(types):
            return None
        if types[i] != 0:
            for j in range(i - 1, last, -1):
                if types[j] == 0:
                    out.append((j, 0, 0, 0))
                    break
        d = 128 if types[i] == 3 else 0
        x = vals.s_smart() if attr & 1 else d
        y = vals.s_smart() if attr & 2 else d
        z = vals.s_smart() if attr & 4 else d
        out.append((i, x, y, z))
        last = i
    framemaps[fm_id] = fm
    return fm_id, out


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    world = os.path.join(out_dir, "world")
    cache = Cache(cache_dir)
    groups_needed = {}
    skeletal = legacy = 0
    with open(os.path.join(world, "seqs.txt"), encoding="utf-8") as f:
        for line in f:
            p = line.rstrip("\n").split("\t")
            if p[0] != "S":
                continue
            if int(p[8]) >= 0:
                skeletal += 1
                continue
            legacy += 1
            for fid in (int(x) for x in p[14].split(",") if x):
                if fid < 0:
                    continue
                groups_needed.setdefault(fid >> 16, set()).add(fid & 0xFFFF)
    framemaps, fm_cache, frames = {}, {}, {}
    bad_groups = bad_frames = 0
    for gid in sorted(groups_needed):
        try:
            files = cache.files(FRAME_INDEX, gid)
        except Exception:
            bad_groups += 1
            continue
        for fid in groups_needed[gid]:
            data = files.get(fid)
            if data is None:
                bad_frames += 1
                continue
            try:
                r = decode_frame(data, framemaps, cache, fm_cache)
            except (IndexError, struct.error):
                r = None
            if r is None:
                bad_frames += 1
                continue
            frames[(gid << 16) | fid] = r
    path = os.path.join(world, "anims.bin.gz")
    with gzip.open(path, "wb", 6) as f:
        f.write(b"OAN1")
        f.write(struct.pack("<I", len(framemaps)))
        for fm_id in sorted(framemaps):
            types, groups = framemaps[fm_id]
            f.write(struct.pack("<iH", fm_id, len(types)))
            f.write(bytes(types))
            for g in groups:
                f.write(struct.pack("<H", len(g)))
                f.write(bytes(g))
        f.write(struct.pack("<I", len(frames)))
        for fid in sorted(frames):
            fm_id, ts = frames[fid]
            f.write(struct.pack("<iHH", fid, fm_id, len(ts)))
            f.write(b"".join(struct.pack("<Hhhh", g, x, y, z) for g, x, y, z in ts))
    print(f"sequences: {legacy} legacy, {skeletal} skeletal (skipped); framemaps {len(framemaps)}, "
          f"frames {len(frames)} (bad groups {bad_groups}, bad frames {bad_frames}), "
          f"{os.path.getsize(path) / 1e6:.1f} MB")

    # vertex labels of every NPC / player model
    ids = set()
    for name in ("npc_models.bin.gz", "player_models.bin.gz"):
        p = os.path.join(world, name)
        if not os.path.exists(p):
            continue
        with gzip.open(p, "rb") as g:
            d = g.read()
        off = 4
        for _ in range(struct.unpack_from("<I", d, 0)[0]):
            mid, nv, nf = struct.unpack_from("<IHI", d, off)
            off += 10 + nv * 6 + nf * 10
            if nv:
                ids.add(mid)
    mc = ModelCache(cache)
    skinned = 0
    spath = os.path.join(world, "model_skins.bin.gz")
    with gzip.open(spath, "wb", 6) as f:
        rows = []
        for mid in sorted(ids):
            m = mc.get(mid)
            if m is None or not m.vskins:
                continue
            rows.append((mid, m.vskins))
        f.write(struct.pack("<I", len(rows)))
        for mid, vs in rows:
            f.write(struct.pack("<iH", mid, len(vs)))
            f.write(bytes(v & 0xFF for v in vs))
        skinned = len(rows)
    print(f"model skins: {skinned} of {len(ids)} npc/player models, {os.path.getsize(spath) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()

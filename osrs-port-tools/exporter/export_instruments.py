"""Export music patches (index 15) and Vorbis samples (index 14).

Usage: python export_instruments.py <cache_dir> <out_dir>

Writes <out_dir>/world/instruments.bin.gz, little-endian:
    "OSI1"
    u32 patchCount, then per patch: i32 id, u32 length, raw MusicPatch bytes
    u32 sampleCount, then per sample: i32 id, u32 length, raw VorbisSample bytes
    u32 setupLength, index-14 archive 0 (the shared Vorbis setup)

Unity decodes these the way the client does. Patches that point at index 4
use the synths already in sounds.bin.gz.
"""
import gzip
import os
import struct
import sys

from cache import Cache

PATCH_INDEX = 15
SAMPLE_INDEX = 14


def dump(cache, index, skip=()):
    out = []
    bad = 0
    for aid in sorted(cache.info(index).archives):
        if aid in skip:
            continue
        try:
            files = cache.files(index, aid)
            data = files.get(0) or next(iter(files.values()), None)
        except Exception:
            bad += 1
            continue
        if data:
            out.append((aid, data))
        else:
            bad += 1
    return out, bad


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    setup = cache.files(SAMPLE_INDEX, 0).get(0)
    if not setup:
        raise SystemExit("index 14 archive 0 (vorbis setup) is missing")
    patches, patch_bad = dump(cache, PATCH_INDEX)
    samples, sample_bad = dump(cache, SAMPLE_INDEX, skip={0})
    path = os.path.join(out_dir, "world", "instruments.bin.gz")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with gzip.open(path, "wb", 6) as f:
        f.write(b"OSI1")
        f.write(struct.pack("<I", len(patches)))
        for aid, data in patches:
            f.write(struct.pack("<iI", aid, len(data)))
            f.write(data)
        f.write(struct.pack("<I", len(samples)))
        for aid, data in samples:
            f.write(struct.pack("<iI", aid, len(data)))
            f.write(data)
        f.write(struct.pack("<I", len(setup)))
        f.write(setup)
    print(
        f"instruments {len(patches)} patches ({patch_bad} bad), "
        f"{len(samples)} samples ({sample_bad} bad), {os.path.getsize(path) / 1e6:.1f} MB"
    )


if __name__ == "__main__":
    main()

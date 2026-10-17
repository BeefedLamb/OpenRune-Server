"""Export the cache's synth sound effects (index 4) for the Unity client.

Usage: python export_sounds.py <cache_dir> <out_dir>

Writes <out_dir>/world/sounds.bin.gz, little-endian:
    "OSN1", u32 count, per sound: i32 id, u32 length, the raw sound-effect bytes.
The bytes are the client's SoundEffect format (up to 10 instruments of envelopes, oscillators and
a filter); Unity synthesises them on first use, like the client does.
"""
import gzip
import os
import struct
import sys

from cache import Cache

SOUND_INDEX = 4


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    sounds = {}
    bad = 0
    for sid in sorted(cache.info(SOUND_INDEX).archives):
        try:
            files = cache.files(SOUND_INDEX, sid)
        except Exception:
            bad += 1
            continue
        data = files.get(0) or next(iter(files.values()), None)
        if data:
            sounds[sid] = data
    path = os.path.join(out_dir, "world", "sounds.bin.gz")
    with gzip.open(path, "wb", 6) as f:
        f.write(b"OSN1")
        f.write(struct.pack("<I", len(sounds)))
        for sid, data in sounds.items():
            f.write(struct.pack("<iI", sid, len(data)))
            f.write(data)
    print(f"sounds {len(sounds)} (bad {bad}), {os.path.getsize(path) / 1e6:.1f} MB")


if __name__ == "__main__":
    main()

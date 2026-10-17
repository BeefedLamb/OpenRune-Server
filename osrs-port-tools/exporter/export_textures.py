"""Export OSRS textures and per-face texture coordinates for the Unity client.

Usage: python export_textures.py <cache_dir> <world_dir>

Writes, next to the existing world export:
  textures.bin     TEX1: 128x128 RGBA slices, one per texture id, plus scroll speeds
  model_uvs.bin.gz UV1: UVs for every face of models.bin.gz and models_extra.bin.gz

Texture pixels come from index 9 (rev 233 definitions: one sprite, average HSL, animation)
and the sprite in index 8. 64x64 sprites are nearest-neighbour scaled to 128.
"""

import gzip
import os
import struct
import sys

from cache import Cache
from models import ModelCache, face_uvs

SPRITE_INDEX = 8
TEXTURE_INDEX = 9
SIZE = 128


def load_textures(cache):
    files = cache.files(TEXTURE_INDEX, 0)
    out = {}
    for fid, data in files.items():
        sprite = (data[0] << 8) | data[1]
        anim_dir = data[4]
        anim_speed = data[5]
        out[fid] = (sprite, anim_dir, anim_speed)
    return out


def decode_sprite(data):
    n = len(data)
    count = (data[n - 2] << 8) | data[n - 1]
    p = n - 7 - count * 8
    width = (data[p] << 8) | data[p + 1]
    height = (data[p + 2] << 8) | data[p + 3]
    palette_len = data[p + 4] + 1
    p += 5
    off_x = []
    off_y = []
    sw = []
    sh = []
    for _ in range(count):
        off_x.append((data[p] << 8) | data[p + 1])
        p += 2
    for _ in range(count):
        off_y.append((data[p] << 8) | data[p + 1])
        p += 2
    for _ in range(count):
        sw.append((data[p] << 8) | data[p + 1])
        p += 2
    for _ in range(count):
        sh.append((data[p] << 8) | data[p + 1])
        p += 2
    pal_at = n - 7 - count * 8 - (palette_len - 1) * 3
    palette = [0]
    q = pal_at
    for _ in range(1, palette_len):
        rgb = (data[q] << 16) | (data[q + 1] << 8) | data[q + 2]
        q += 3
        palette.append(1 if rgb == 0 else rgb)
    q = 0
    frames = []
    for i in range(count):
        flags = data[q]
        q += 1
        dim = sw[i] * sh[i]
        idx = [0] * dim
        if (flags & 1) == 0:
            idx = list(data[q:q + dim])
            q += dim
        else:
            for x in range(sw[i]):
                for y in range(sh[i]):
                    idx[sw[i] * y + x] = data[q]
                    q += 1
        alpha = [0] * dim
        if flags & 2:
            if (flags & 1) == 0:
                alpha = list(data[q:q + dim])
                q += dim
            else:
                for x in range(sw[i]):
                    for y in range(sh[i]):
                        alpha[sw[i] * y + x] = data[q]
                        q += 1
        else:
            alpha = [255 if v else 0 for v in idx]
        pixels = bytearray(width * height * 4)
        for y in range(sh[i]):
            for x in range(sw[i]):
                k = y * sw[i] + x
                rgb = palette[idx[k]]
                a = alpha[k]
                dx, dy = x + off_x[i], y + off_y[i]
                if dx < 0 or dy < 0 or dx >= width or dy >= height:
                    continue
                o = (dy * width + dx) * 4
                pixels[o] = (rgb >> 16) & 255
                pixels[o + 1] = (rgb >> 8) & 255
                pixels[o + 2] = rgb & 255
                pixels[o + 3] = a
        frames.append((width, height, bytes(pixels)))
    return frames


def to_size(width, height, pixels):
    if width == SIZE and height == SIZE:
        return pixels
    out = bytearray(SIZE * SIZE * 4)
    for y in range(SIZE):
        sy = min(height - 1, y * height // SIZE)
        for x in range(SIZE):
            sx = min(width - 1, x * width // SIZE)
            s = (sy * width + sx) * 4
            d = (y * SIZE + x) * 4
            out[d:d + 4] = pixels[s:s + 4]
    return bytes(out)


def scroll(anim_dir, anim_speed):
    """UV per second. The client shifts by `speed` pixels a frame at 50fps on a 128 texture."""
    if anim_speed == 0 or anim_dir == 0:
        return 0.0, 0.0
    step = anim_speed * 50.0 / SIZE
    if anim_dir == 1:
        return 0.0, step
    if anim_dir == 3:
        return 0.0, -step
    if anim_dir == 4:
        return step, 0.0
    if anim_dir == 2:
        return -step, 0.0
    return 0.0, 0.0


def write_textures(cache, out_dir):
    defs = load_textures(cache)
    max_id = max(defs) if defs else -1
    body = bytearray(b"TEX1" + struct.pack("<HH", max_id + 1, SIZE))
    sprites = {}
    for fid in range(max_id + 1):
        if fid not in defs:
            body += struct.pack("<Bhh", 1, 0, 0)
            body += bytes(SIZE * SIZE * 4)
            continue
        sid, ad, asp = defs[fid]
        if sid not in sprites:
            sprites[sid] = decode_sprite(cache.archive_data(SPRITE_INDEX, sid))[0]
        w, h, pixels = sprites[sid]
        su, sv = scroll(ad, asp)
        body += struct.pack("<Bhh", 1, int(round(su * 256)), int(round(sv * 256)))
        body += to_size(w, h, pixels)
    path = os.path.join(out_dir, "textures.bin")
    with open(path, "wb") as f:
        f.write(body)
    print(f"textures: {len(defs)} of {max_id + 1} ids, {os.path.getsize(path) / 1e6:.1f} MB")


def model_ids(path):
    with gzip.open(path, "rb") as f:
        data = f.read()
    count = struct.unpack_from("<I", data, 0)[0]
    p = 4
    ids = []
    for _ in range(count):
        mid, nv, nf = struct.unpack_from("<IHI", data, p)
        ids.append((mid, nf))
        p += 10 + nv * 6 + nf * 10
    return ids


def write_uvs(cache, out_dir):
    models = ModelCache(cache)
    seen = {}
    for name in ("models.bin.gz", "models_extra.bin.gz"):
        path = os.path.join(out_dir, name)
        if not os.path.isfile(path):
            continue
        for mid, nf in model_ids(path):
            seen.setdefault(mid, nf)
    body = bytearray(b"UV1")
    body += struct.pack("<I", len(seen))
    missing = mismatch = textured = 0
    for mid, nf in seen.items():
        m = models.get(mid)
        if m is None or not m.vx:
            uv = [(0, 0, 0, 0, 0, 0)] * nf
            missing += 1
        else:
            uv = face_uvs(m)
            if len(uv) != nf:
                uv = [(0, 0, 0, 0, 0, 0)] * nf
                mismatch += 1
            elif m.textures and any(t >= 0 for t in m.textures):
                textured += 1
        body += struct.pack("<II", mid, nf)
        for row in uv:
            body += struct.pack("<hhhhhh", *row)
    path = os.path.join(out_dir, "model_uvs.bin.gz")
    with gzip.open(path, "wb", 6) as f:
        f.write(body)
    print(f"uvs: {len(seen)} models, {textured} textured, {missing} missing, {mismatch} mismatched")


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    write_textures(cache, out_dir)
    write_uvs(cache, out_dir)


if __name__ == "__main__":
    main()

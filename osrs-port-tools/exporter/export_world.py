"""Export the whole OSRS world for the Unity client.

Usage: python3 export_world.py <cache_dir> <out_dir> [--only rx,ry ...]

Writes to <out_dir>/world/:
  models.bin.gz    every model any placed object uses, once (Unity space, 1/128 tile units)
  variants.bin.gz  per (object, model type, mirrored) variant: model ids, recolours, scale
  locs.json        object id -> name, right-click options, footprint
  index.bin        map square -> pack file, offset, length
  pack_N.bin       concatenated gzip blobs, one per map square (terrain + placements)

All integers little-endian. Unity coordinates: x = east, y = up, z = north, world position
in tiles equals the OSRS tile coordinate (no origin shift).
"""
import gzip
import json
import math
import os
import struct
import sys
import time

from cache import Cache
from scene import DIAG_X, DIAG_Y, ROT_X, ROT_Y, Scene, TriSoup

PACK_LIMIT = 28 * 1024 * 1024


class WorldExporter:
    def __init__(self, cache_dir, out_dir):
        self.cache = Cache(cache_dir)
        self.scene = Scene(self.cache)
        self.out = os.path.join(out_dir, "world")
        os.makedirs(self.out, exist_ok=True)
        self.square_ids = sorted(i for i in self.cache.info(5).archives if i <= 25286)
        self.variants = {}
        self.variant_list = []
        self.models_used = set()
        self.loc_info = {}
        self.index = []
        self.pack_no = 0
        self.pack = None
        self.pack_size = 0

    # ------------------------------------------------------------ square loading
    def ensure_loaded(self, rx, ry):
        if (rx, ry) in self.scene.tiles:
            return
        try:
            self.scene.load_square(rx, ry)
        except KeyError:
            pass

    def evict_column(self, rx):
        for key in [k for k in self.scene.tiles if k[0] == rx]:
            del self.scene.tiles[key]
            self.scene.locs.pop(key, None)

    # ------------------------------------------------------------ variants
    def variant_id(self, d, mtype, mirror):
        key = (d.id, mtype, mirror)
        vid = self.variants.get(key)
        if vid is None:
            ids = d.model_for(mtype)
            if not ids:
                self.variants[key] = -1
                return -1
            vid = len(self.variant_list)
            self.variants[key] = vid
            self.variant_list.append((d, mtype, mirror, ids))
            self.models_used.update(ids)
            if d.id not in self.loc_info:
                self.loc_info[d.id] = [d.name, [o for o in d.ops if o], d.size_x, d.size_y]
        return vid

    # ------------------------------------------------------------ placements
    def placements(self, rx, ry):
        s = self.scene
        out = []
        wall_disp = {}
        locs = s.locs.get((rx, ry), [])
        for lid, lx, ly, level, ltype, orient in locs:
            if ltype <= 3:
                d = s.locdefs.resolve(lid)
                if d:
                    wall_disp[(lx, ly, level)] = d.decor_displacement
        for lid, lx, ly, level, ltype, orient in locs:
            d = s.locdefs.resolve(lid)
            if d is None or d.models is None:
                continue
            x, y = rx * 64 + lx, ry * 64 + ly
            sx, sy = (d.size_y, d.size_x) if orient in (1, 3) else (d.size_x, d.size_y)
            if ltype not in (10, 11):
                sx = sy = 1
            xa, xb = x + (sx >> 1), x + ((sx + 1) >> 1)
            ya, yb = y + (sy >> 1), y + ((sy + 1) >> 1)
            h = (s.height(level, xa, ya) + s.height(level, xb, ya)
                 + s.height(level, xa, yb) + s.height(level, xb, yb)) >> 2
            cx, cy = lx * 128 + sx * 64, ly * 128 + sy * 64
            decor = 1 if (ltype == 22 or 4 <= ltype <= 8 or d.solid == 0) else 0
            jobs = []
            if ltype == 22 or 12 <= ltype <= 21 or ltype in (0, 1, 3, 9):
                jobs.append((ltype, orient, 0, 0, 0))
            elif ltype in (10, 11):
                jobs.append((10, orient, 0, 0, 256 if ltype == 11 else 0))
            elif ltype == 2:
                jobs.append((2, orient + 4, 0, 0, 0))
                jobs.append((2, (orient + 1) & 3, 0, 0, 0))
            elif ltype == 4:
                jobs.append((4, orient, 0, 0, 0))
            elif ltype == 5:
                disp = wall_disp.get((lx, ly, level), 16)
                jobs.append((4, orient, disp * ROT_X[orient], disp * ROT_Y[orient], 0))
            elif ltype == 6:
                disp = wall_disp.get((lx, ly, level), 16) // 2
                jobs.append((4, orient + 4, disp * DIAG_X[orient], disp * DIAG_Y[orient], 0))
            elif ltype == 7:
                jobs.append((4, ((orient + 2) & 3) + 4, 0, 0, 0))
            elif ltype == 8:
                disp = wall_disp.get((lx, ly, level), 16) // 2
                jobs.append((4, orient + 4, disp * DIAG_X[orient], disp * DIAG_Y[orient], 0))
                jobs.append((4, ((orient + 2) & 3) + 4, 0, 0, 0))
            for mtype, mrot, dx, dz, extra in jobs:
                mirror = d.rotated if d.model_types is None else d.rotated ^ (mrot > 3)
                vid = self.variant_id(d, mtype, bool(mirror))
                if vid < 0:
                    continue
                r = mrot & 3
                yaw = (r * 512 + extra) & 2047
                ox, oz = 0, 0
                if mtype == 4 and mrot > 3:
                    yaw = (yaw + 256) & 2047
                    ox, oz = rotate_offset(45, -45, r)
                ox += dx + d.offset[0]
                oz += dz + d.offset[2]
                oy = d.offset[1]
                out.append((vid, lx, ly, level, yaw, cx + ox, cy + oz, -(h + oy), decor))
        return out

    # ------------------------------------------------------------ terrain
    def terrain(self, rx, ry):
        s = self.scene
        soup = TriSoup()
        x0, y0 = rx * 64, ry * 64
        s.build_terrain(x0, y0, x0 + 63, y0 + 63, soup)
        verts = bytearray()
        cols = bytearray()
        n = 0
        for t in range(0, len(soup.pos), 3):
            tri = [soup.pos[t], soup.pos[t + 2], soup.pos[t + 1]]
            tcol = [soup.col[t], soup.col[t + 2], soup.col[t + 1]]
            for (px, py, pz), (r, g, b) in zip(tri, tcol):
                verts += struct.pack("<hhh", int(px - x0 * 128), int(-py), int(pz - y0 * 128))
                cols += bytes((int(r * 255), int(g * 255), int(b * 255)))
                n += 1
        return n, bytes(verts), bytes(cols)

    # ------------------------------------------------------------ writing
    def open_pack(self):
        if self.pack:
            self.pack.close()
        self.pack = open(os.path.join(self.out, f"pack_{self.pack_no}.bin"), "wb")
        self.pack_size = 0

    def write_square(self, rx, ry, blob):
        if self.pack is None or self.pack_size + len(blob) > PACK_LIMIT:
            if self.pack is not None:
                self.pack_no += 1
            self.open_pack()
        self.index.append((rx, ry, self.pack_no, self.pack_size, len(blob)))
        self.pack.write(blob)
        self.pack_size += len(blob)

    def export_square(self, rx, ry):
        s = self.scene
        for nx in (rx - 1, rx, rx + 1):
            for ny in (ry - 1, ry, ry + 1):
                self.ensure_loaded(nx, ny)
        x0, y0 = rx * 64, ry * 64
        s.compute_heights(x0 - 2, y0 - 2, x0 + 66, y0 + 66)
        n, verts, cols = self.terrain(rx, ry)
        places = self.placements(rx, ry)
        body = bytearray(b"SQ1" + struct.pack("<ii", rx, ry))
        body += struct.pack("<I", n) + verts + cols
        body += struct.pack("<I", len(places))
        for vid, lx, ly, level, yaw, px, pz, py, decor in places:
            body += struct.pack("<IBBBHhhhB", vid, lx, ly, level, yaw,
                                clamp16(px), clamp16(pz), clamp16(py), decor)
        self.write_square(rx, ry, gzip.compress(bytes(body), 6))
        return n // 3, len(places)

    def write_models(self):
        path = os.path.join(self.out, "models.bin.gz")
        with gzip.open(path, "wb", 6) as f:
            f.write(struct.pack("<I", len(self.models_used)))
            for mid in sorted(self.models_used):
                m = self.scene.models.get(mid)
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
                f.write(b"".join(struct.pack("<hhh", clamp16(x), clamp16(-y), clamp16(z))
                                 for x, y, z in zip(m.vx, m.vy, m.vz)))
                f.write(b"".join(struct.pack("<HHHHh", a, b, c, col, tex) for a, b, c, col, tex in faces))

    def write_variants(self):
        path = os.path.join(self.out, "variants.bin.gz")
        tex = sorted(self.scene.tex_colors.items())
        with gzip.open(path, "wb", 6) as f:
            f.write(struct.pack("<H", len(tex)))
            for tid, hsl in tex:
                f.write(struct.pack("<HH", tid, hsl))
            f.write(struct.pack("<I", len(self.variant_list)))
            for d, mtype, mirror, ids in self.variant_list:
                f.write(struct.pack("<IBBB", d.id, mtype, 1 if mirror else 0, len(ids)))
                f.write(b"".join(struct.pack("<I", i) for i in ids))
                f.write(struct.pack("<B", len(d.recolor)))
                f.write(b"".join(struct.pack("<HH", a, b) for a, b in d.recolor))
                f.write(struct.pack("<B", len(d.retexture)))
                f.write(b"".join(struct.pack("<HH", a, b) for a, b in d.retexture))
                f.write(struct.pack("<hhh", *d.scale))
        with open(os.path.join(self.out, "locs.json"), "w") as f:
            json.dump(self.loc_info, f)

    def write_index(self):
        with open(os.path.join(self.out, "index.bin"), "wb") as f:
            f.write(b"OSW1" + struct.pack("<I", len(self.index)))
            for rx, ry, pack, off, ln in self.index:
                f.write(struct.pack("<BBBII", rx, ry, pack, off, ln))

    def run(self, only=None):
        t = time.time()
        squares = [(i >> 8, i & 255) for i in self.square_ids]
        if only:
            squares = [sq for sq in squares if sq in only]
        squares.sort()
        total_t = total_p = 0
        last_rx = None
        for n, (rx, ry) in enumerate(squares):
            if last_rx is not None and rx > last_rx:
                for col in [c for c in {k[0] for k in self.scene.tiles} if c < rx - 1]:
                    self.evict_column(col)
            last_rx = rx
            tri, pl = self.export_square(rx, ry)
            total_t += tri
            total_p += pl
            if n % 100 == 0:
                print(f"{n}/{len(squares)} squares, {total_t:,} terrain tris, {total_p:,} placements, "
                      f"{len(self.variant_list):,} variants, {time.time() - t:.0f}s", flush=True)
        if self.pack:
            self.pack.close()
        self.write_index()
        self.write_models()
        self.write_variants()
        print(f"done: {len(squares)} squares, {total_p:,} placements, {len(self.variant_list):,} variants, "
              f"{len(self.models_used):,} models, {time.time() - t:.0f}s")


def rotate_offset(x, z, r):
    for _ in range(r):
        x, z = z, -x
    return x, z


def clamp16(v):
    return max(-32768, min(32767, int(round(v))))


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    only = None
    if "--only" in sys.argv:
        only = {tuple(int(v) for v in a.split(",")) for a in sys.argv[sys.argv.index("--only") + 1:]}
    WorldExporter(cache_dir, out_dir).run(only)


if __name__ == "__main__":
    main()

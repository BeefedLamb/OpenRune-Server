"""Builds a coloured triangle soup for a set of map squares (terrain + locs)."""

from defs import (LocDefs, decode_locs, decode_tiles, hsl16_to_rgb, load_overlays,
                  load_texture_colors, load_underlays, rgb24)
from models import ModelCache

ROT_X = (1, 0, -1, 0)
ROT_Y = (0, -1, 0, 1)
DIAG_X = (1, -1, -1, 1)
DIAG_Y = (-1, -1, 1, 1)
BLEND = 5
HIDDEN_OVERLAY = 0xFF00FF


def _cos_table():
    import math
    return [int(65536.0 * math.cos(i * 2 * math.pi / 2048)) for i in range(2048)]


_COS = _cos_table()


def _noise(x, y):
    n = (x + y * 57) & 0xFFFFFFFF
    n ^= (n << 13) & 0xFFFFFFFF
    n &= 0xFFFFFFFF
    if n & 0x80000000:
        n -= 1 << 32
    v = (n * ((n * n * 15731 + 789221) & 0xFFFFFFFF) + 1376312589) & 0x7FFFFFFF
    return (v >> 19) & 255


def _smooth(x, y):
    corners = _noise(x - 1, y - 1) + _noise(x + 1, y - 1) + _noise(x - 1, y + 1) + _noise(x + 1, y + 1)
    sides = _noise(x - 1, y) + _noise(x + 1, y) + _noise(x, y - 1) + _noise(x, y + 1)
    return _noise(x, y) // 4 + sides // 8 + corners // 16


def _interp(a, b, x, y):
    f = (65536 - _COS[1024 * x // y]) >> 1
    return ((f * b) >> 16) + ((a * (65536 - f)) >> 16)


def _inoise(x, y, freq):
    ix, fx, iy, fy = x // freq, x & (freq - 1), y // freq, y & (freq - 1)
    v1, v2 = _smooth(ix, iy), _smooth(ix + 1, iy)
    v3, v4 = _smooth(ix, iy + 1), _smooth(ix + 1, iy + 1)
    return _interp(_interp(v1, v2, fx, freq), _interp(v3, v4, fx, freq), fy, freq)


def procedural_height(x, y):
    n = (_inoise(x + 45365, y + 91923, 4) - 128
         + ((_inoise(10294 + x, y + 37821, 2) - 128) >> 1)
         + ((_inoise(x, y, 1) - 128) >> 2))
    n = 35 + int(n * 0.3)
    return max(10, min(60, n))


class Scene:
    def __init__(self, cache):
        self.cache = cache
        self.underlays = load_underlays(cache)
        self.overlays = load_overlays(cache)
        self.tex_colors = load_texture_colors(cache)
        self.locdefs = LocDefs(cache)
        self.models = ModelCache(cache)
        self.tiles = {}
        self.locs = {}

    # ------------------------------------------------------------ loading
    def load_square(self, rx, ry):
        files = self.cache.files(5, (rx << 8) | ry)
        self.tiles[(rx, ry)] = decode_tiles(files[0])
        self.locs[(rx, ry)] = decode_locs(files[1]) if 1 in files else []

    def _tile(self, key, z, wx, wy):
        sq = self.tiles.get((wx >> 6, wy >> 6))
        if sq is None:
            return None
        return sq[key][z][wx & 63][wy & 63]

    def compute_heights(self, x0, y0, x1, y1):
        """Heights (osrs units, negative = up) for corner grid [x0..x1]x[y0..y1] inclusive."""
        self.hx0, self.hy0 = x0, y0
        w, h = x1 - x0 + 1, y1 - y0 + 1
        H = [[[0] * h for _ in range(w)] for _ in range(4)]
        for i in range(w):
            for j in range(h):
                wx, wy = min(max(x0 + i, 0), x1), y0 + j
                for z in range(4):
                    raw = self._tile("height", z, x0 + i, y0 + j)
                    if self.tiles.get(((x0 + i) >> 6, (y0 + j) >> 6)) is None:
                        H[z][i][j] = H[z][i - 1][j] if i else (H[z][i][j - 1] if j else 0)
                        continue
                    if raw is None:
                        if z == 0:
                            H[0][i][j] = -procedural_height(x0 + i + 0xE3B7B, y0 + j + 0x87CCE) * 8
                        else:
                            H[z][i][j] = H[z - 1][i][j] - 240
                    else:
                        v = 0 if raw == 1 else raw
                        H[z][i][j] = (-v * 8) if z == 0 else H[z - 1][i][j] - v * 8
        self.H = H

    def height(self, z, wx, wy):
        i = min(max(wx - self.hx0, 0), len(self.H[0]) - 1)
        j = min(max(wy - self.hy0, 0), len(self.H[0][0]) - 1)
        return self.H[z][i][j]

    # ------------------------------------------------------------ terrain
    def _blended_underlay(self, z, x0, y0, x1, y1):
        """Box-blurred underlay colour per tile (client blends in an 11x11 window)."""
        cols = {}
        for wx in range(x0 - BLEND, x1 + BLEND + 1):
            for wy in range(y0 - BLEND, y1 + BLEND + 1):
                u = self._tile("underlay", z, wx, wy)
                if u:
                    cols[(wx, wy)] = self.underlays.get(u - 1, (0, 0, 0))
        out = {}
        for wx in range(x0, x1 + 1):
            for wy in range(y0, y1 + 1):
                r = g = b = n = 0
                for dx in range(-BLEND, BLEND + 1):
                    for dy in range(-BLEND, BLEND + 1):
                        c = cols.get((wx + dx, wy + dy))
                        if c:
                            r += c[0]; g += c[1]; b += c[2]; n += 1
                if n:
                    out[(wx, wy)] = (r / n, g / n, b / n)
        return out

    def _overlay_color(self, oid):
        o = self.overlays.get(oid - 1)
        if o is None:
            return None
        if o.texture >= 0 and o.texture in self.tex_colors:
            return hsl16_to_rgb(self.tex_colors[o.texture])
        if o.rgb == HIDDEN_OVERLAY:
            return None
        return rgb24(o.rgb)

    def is_bridge(self, wx, wy):
        """OSRS bridge flag: settings[1] & 0x2 on a tile shifts every plane >= 1 of that
        tile down one render level (client MapLoader: `if (settings[1][x][y] & 2) level--`)."""
        s = self._tile("settings", 1, wx, wy)
        return bool(s and (s & 0x2))

    def display_level(self, level, wx, wy):
        """Plane a loc/tile at `level` is drawn and walked on, after the bridge shift."""
        return level - 1 if level > 0 and self.is_bridge(wx, wy) else level

    def build_terrain(self, x0, y0, x1, y1, out, planes=range(4)):
        for z in planes:
            blends = {}
            for wx in range(x0, x1 + 1):
                for wy in range(y0, y1 + 1):
                    # Bridge tiles: render level z shows source plane z+1 (the deck, at its
                    # own heights); on level 0 plane 0's surface (ground under it) stays too.
                    if self.is_bridge(wx, wy):
                        srcs = ([0] if z == 0 else []) + ([z + 1] if z + 1 < 4 else [])
                    else:
                        srcs = [z]
                    for sz in srcs:
                        if sz not in blends:
                            blends[sz] = self._blended_underlay(sz, x0, y0, x1 + 1, y1 + 1)
                        self._terrain_tile(sz, wx, wy, blends[sz], out)

    def _terrain_tile(self, z, wx, wy, blend, out):
        u = self._tile("underlay", z, wx, wy)
        o = self._tile("overlay", z, wx, wy)
        if u is None or (not u and not o):
            return
        corners = [(wx, wy), (wx + 1, wy), (wx + 1, wy + 1), (wx, wy + 1)]
        pos = [(cx * 128, self.height(z, cx, cy), cy * 128) for cx, cy in corners]
        under = [blend.get(c) or blend.get((wx, wy)) for c in corners] if u else None
        over = self._overlay_color(o) if o else None
        path = self._tile("path", z, wx, wy)
        rot = self._tile("rot", z, wx, wy)
        tris = ((0, 1, 2), (0, 2, 3))
        if over is None or path == 0:
            if over is not None:
                for t in tris:
                    out.add_tri([pos[i] for i in t], [over] * 3)
            elif under:
                for t in tris:
                    out.add_tri([pos[i] for i in t], [under[i] for i in t])
            return
        # Shaped overlay (paths, coastlines): approximate by splitting the tile
        # along a diagonal chosen by the rotation; overlay on one half.
        a = rot & 3
        tri_over = ((a, (a + 1) & 3, (a + 2) & 3),)
        tri_under = ((a, (a + 2) & 3, (a + 3) & 3),)
        for t in tri_over:
            out.add_tri([pos[i] for i in t], [over] * 3)
        if under:
            for t in tri_under:
                out.add_tri([pos[i] for i in t], [under[i] for i in t])

    # ------------------------------------------------------------ locs
    def _loc_model(self, d, ltype, orient):
        ids = d.model_for(ltype)
        if not ids:
            return None
        parts = [self.models.get(m) for m in ids]
        parts = [p for p in parts if p is not None and p.vx]
        if not parts:
            return None
        m = parts[0].copy()
        for p in parts[1:]:
            off = len(m.vx)
            m.vx += p.vx; m.vy += p.vy; m.vz += p.vz
            m.f1 += [i + off for i in p.f1]
            m.f2 += [i + off for i in p.f2]
            m.f3 += [i + off for i in p.f3]
            m.colors += p.colors
            m.render_types = _cat(m.render_types, p.render_types, len(m.f1) - len(p.f1), len(p.f1), 0)
            m.alphas = _cat(m.alphas, p.alphas, len(m.f1) - len(p.f1), len(p.f1), 0)
            m.textures = _cat(m.textures, p.textures, len(m.f1) - len(p.f1), len(p.f1), -1)
        mirror = d.rotated if d.model_types is None else d.rotated ^ (orient > 3)
        if mirror:
            m.mirror_z()
        if ltype == 4 and orient > 3:
            m.rotate(256)
            m.translate(45, 0, -45)
        r = orient & 3
        if r == 1:
            m.rot90()
        elif r == 2:
            m.rot180()
        elif r == 3:
            m.rot270()
        for src, dst in d.recolor:
            m.recolor(src, dst)
        for src, dst in d.retexture:
            m.retexture(src, dst)
        if d.scale != (128, 128, 128):
            m.resize(*d.scale)
        if d.offset != (0, 0, 0):
            m.translate(*d.offset)
        return m

    def _emit(self, m, wx, wy, h, out, dx=0, dy=0):
        bx, by = wx + dx, wy + dy
        for i in range(len(m.f1)):
            if m.render_types and m.render_types[i] == 2:
                continue
            if m.alphas and m.alphas[i] >= 254:
                continue
            tex = m.textures[i] if m.textures else -1
            if tex != -1 and tex in self.tex_colors:
                col = hsl16_to_rgb(self.tex_colors[tex])
            else:
                col = hsl16_to_rgb(m.colors[i] & 0xFFFF)
            a, b, c = m.f1[i], m.f2[i], m.f3[i]
            out.add_tri([(bx + m.vx[k], h + m.vy[k], by + m.vz[k]) for k in (a, b, c)], [col] * 3)

    def build_locs(self, squares, out, meta, decor_out=None):
        wall_disp = {}
        for (rx, ry) in squares:
            for lid, lx, ly, level, ltype, orient in self.locs.get((rx, ry), []):
                if ltype <= 3:
                    d = self.locdefs.resolve(lid)
                    if d:
                        wall_disp[(rx * 64 + lx, ry * 64 + ly, level)] = d.decor_displacement
        for (rx, ry) in squares:
            for lid, lx, ly, level, ltype, orient in self.locs.get((rx, ry), []):
                d = self.locdefs.resolve(lid)
                if d is None:
                    continue
                x, y = rx * 64 + lx, ry * 64 + ly
                sx, sy = (d.size_y, d.size_x) if orient in (1, 3) else (d.size_x, d.size_y)
                if ltype not in (10, 11):
                    sx = sy = 1
                xa, xb = x + (sx >> 1), x + ((sx + 1) >> 1)
                ya, yb = y + (sy >> 1), y + ((sy + 1) >> 1)
                h = (self.height(level, xa, ya) + self.height(level, xb, ya)
                     + self.height(level, xa, yb) + self.height(level, xb, yb)) >> 2
                cx, cy = x * 128 + sx * 64, y * 128 + sy * 64
                meta.append({"id": d.id, "name": d.name, "x": x, "y": y, "level": level,
                             "type": ltype, "rot": orient, "ops": [o for o in d.ops if o]})
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
                    disp = wall_disp.get((x, y, level), 16)
                    jobs.append((4, orient, disp * ROT_X[orient], disp * ROT_Y[orient], 0))
                elif ltype == 6:
                    disp = wall_disp.get((x, y, level), 16) // 2
                    jobs.append((4, orient + 4, disp * DIAG_X[orient], disp * DIAG_Y[orient], 0))
                elif ltype == 7:
                    jobs.append((4, ((orient + 2) & 3) + 4, 0, 0, 0))
                elif ltype == 8:
                    disp = wall_disp.get((x, y, level), 16) // 2
                    jobs.append((4, orient + 4, disp * DIAG_X[orient], disp * DIAG_Y[orient], 0))
                    jobs.append((4, ((orient + 2) & 3) + 4, 0, 0, 0))
                target = out
                if decor_out is not None and (ltype == 22 or 4 <= ltype <= 8 or d.solid == 0):
                    target = decor_out
                for mtype, mrot, dx, dy, extra in jobs:
                    m = self._loc_model(d, mtype, mrot)
                    if m is None:
                        continue
                    if extra:
                        m.rotate(extra)
                    self._emit(m, cx, cy, h, target, dx, dy)


def _cat(a, b, na, nb, default):
    if a is None and b is None:
        return None
    return (a if a is not None else [default] * na) + (b if b is not None else [default] * nb)


class TriSoup:
    """Flat-shaded triangle list in OSRS units (x east, y down, z north)."""

    def __init__(self):
        self.pos = []
        self.col = []

    def add_tri(self, verts, cols):
        self.pos.extend(verts)
        self.col.extend(cols)

    def __len__(self):
        return len(self.pos) // 3

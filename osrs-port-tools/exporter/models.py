"""OSRS model decoder (type 2 and type 3 formats, the only ones in rev 241 caches)."""

import math
import struct

from cache import Buf


class Model:
    __slots__ = ("vx", "vy", "vz", "f1", "f2", "f3", "colors", "render_types",
                 "alphas", "textures", "vskins", "tex_coord", "tex_p", "tex_m", "tex_n")

    def copy(self):
        m = Model()
        m.vx, m.vy, m.vz = list(self.vx), list(self.vy), list(self.vz)
        m.f1, m.f2, m.f3 = list(self.f1), list(self.f2), list(self.f3)
        m.colors = list(self.colors)
        m.render_types = list(self.render_types) if self.render_types else None
        m.alphas = list(self.alphas) if self.alphas else None
        m.textures = list(self.textures) if self.textures else None
        m.vskins = list(self.vskins) if getattr(self, 'vskins', None) else None
        m.tex_coord = list(self.tex_coord) if getattr(self, "tex_coord", None) else None
        m.tex_p = list(self.tex_p) if getattr(self, "tex_p", None) else None
        m.tex_m = list(self.tex_m) if getattr(self, "tex_m", None) else None
        m.tex_n = list(self.tex_n) if getattr(self, "tex_n", None) else None
        return m

    # ---- transforms (same maths as the client) ----
    def mirror_z(self):
        self.vz = [-z for z in self.vz]
        self.f1, self.f3 = self.f3, self.f1

    def rot90(self):
        self.vx, self.vz = list(self.vz), [-x for x in self.vx]

    def rot180(self):
        self.vx = [-x for x in self.vx]
        self.vz = [-z for z in self.vz]

    def rot270(self):
        self.vx, self.vz = [-z for z in self.vz], list(self.vx)

    def rotate(self, angle):
        s = math.sin(2 * math.pi * angle / 2048)
        c = math.cos(2 * math.pi * angle / 2048)
        nx = [s * z + c * x for x, z in zip(self.vx, self.vz)]
        nz = [c * z - s * x for x, z in zip(self.vx, self.vz)]
        self.vx, self.vz = nx, nz

    def translate(self, dx, dy, dz):
        self.vx = [x + dx for x in self.vx]
        self.vy = [y + dy for y in self.vy]
        self.vz = [z + dz for z in self.vz]

    def resize(self, sx, sy, sz):
        self.vx = [x * sx / 128 for x in self.vx]
        self.vy = [y * sy / 128 for y in self.vy]
        self.vz = [z * sz / 128 for z in self.vz]

    def recolor(self, src, dst):
        self.colors = [dst if c == src else c for c in self.colors]

    def retexture(self, src, dst):
        if self.textures:
            self.textures = [dst if t == src else t for t in self.textures]


def _footer_flags(data, size):
    return Buf(data, len(data) - size)


def decode_type3(data):
    h = _footer_flags(data, 26)
    vc, fc, tc = h.u16(), h.u16(), h.u8()
    has_rtypes, prio, has_alpha, has_tskin, has_tex, has_vskin, has_maya = (
        h.u8(), h.u8(), h.u8(), h.u8(), h.u8(), h.u8(), h.u8())
    xlen, ylen, zlen, flen, tclen, vskin_len = h.u16(), h.u16(), h.u16(), h.u16(), h.u16(), h.u16()

    simple = per = 0
    rtypes = []
    if tc > 0:
        rtypes = list(data[0:tc])
        for t in rtypes:
            if t == 0:
                simple += 1
            if 1 <= t <= 3:
                per += 1

    off = tc + vc
    face_rtype_off = off
    if has_rtypes == 1:
        off += fc
    face_idx_type_off = off
    off += fc
    prio_off = off
    if prio == 255:
        off += fc
    tskin_off = off
    if has_tskin == 1:
        off += fc
    vskin_off = off
    off += vskin_len
    alpha_off = off
    if has_alpha == 1:
        off += fc
    face_idx_off = off
    off += flen
    tex_off = off
    if has_tex == 1:
        off += fc * 2
    tcoord_off = off
    off += tclen
    color_off = off
    off += fc * 2
    vx_off = off
    off += xlen
    vy_off = off
    off += ylen
    vz_off = off

    m = Model()
    flags = Buf(data, tc)
    bx, by, bz = Buf(data, vx_off), Buf(data, vy_off), Buf(data, vz_off)
    vx, vy, vz = [0] * vc, [0] * vc, [0] * vc
    x = y = z = 0
    for i in range(vc):
        f = flags.u8()
        if f & 1:
            x += bx.s_smart()
        if f & 2:
            y += by.s_smart()
        if f & 4:
            z += bz.s_smart()
        vx[i], vy[i], vz[i] = x, y, z

    bc = Buf(data, color_off)
    br = Buf(data, face_rtype_off)
    ba = Buf(data, alpha_off)
    bt = Buf(data, tex_off)
    bco = Buf(data, tcoord_off)
    colors = [0] * fc
    rt = [0] * fc if has_rtypes == 1 else None
    al = [0] * fc if has_alpha == 1 else None
    tx = [-1] * fc if has_tex == 1 else None
    coord = [-1] * fc
    for i in range(fc):
        colors[i] = bc.u16()
        if rt is not None:
            rt[i] = br.i8()
        if al is not None:
            al[i] = ba.u8()
        if tx is not None:
            tx[i] = bt.u16() - 1
            if tc > 0 and tx[i] != -1:
                coord[i] = bco.u8() - 1

    f1, f2, f3 = _decode_faces(data, face_idx_off, face_idx_type_off, fc)
    m.vx, m.vy, m.vz, m.f1, m.f2, m.f3 = vx, vy, vz, f1, f2, f3
    m.colors, m.render_types, m.alphas, m.textures = colors, rt, al, tx
    m.tex_coord = coord if tc > 0 else None
    m.tex_p, m.tex_m, m.tex_n = _type3_tex_tris(data)
    m.vskins = _vskins(data, vskin_off, vc, has_vskin, has_maya)
    return m


def decode_type2(data):
    h = _footer_flags(data, 23)
    vc, fc, tc = h.u16(), h.u16(), h.u8()
    has_info, prio, has_alpha, has_tskin, has_vskin, has_maya = (
        h.u8(), h.u8(), h.u8(), h.u8(), h.u8(), h.u8())
    xlen, ylen, zlen, flen, vskin_len = h.u16(), h.u16(), h.u16(), h.u16(), h.u16()

    off = vc
    face_idx_type_off = off
    off += fc
    prio_off = off
    if prio == 255:
        off += fc
    tskin_off = off
    if has_tskin == 1:
        off += fc
    info_off = off
    if has_info == 1:
        off += fc
    vskin_off = off
    off += vskin_len
    alpha_off = off
    if has_alpha == 1:
        off += fc
    face_idx_off = off
    off += flen
    color_off = off
    off += fc * 2
    tex_tri_off = off
    off += tc * 6
    vx_off = off
    off += xlen
    vy_off = off
    off += ylen
    vz_off = off

    m = Model()
    flags = Buf(data, 0)
    bx, by, bz = Buf(data, vx_off), Buf(data, vy_off), Buf(data, vz_off)
    vx, vy, vz = [0] * vc, [0] * vc, [0] * vc
    x = y = z = 0
    for i in range(vc):
        f = flags.u8()
        if f & 1:
            x += bx.s_smart()
        if f & 2:
            y += by.s_smart()
        if f & 4:
            z += bz.s_smart()
        vx[i], vy[i], vz[i] = x, y, z

    bc = Buf(data, color_off)
    bi = Buf(data, info_off)
    ba = Buf(data, alpha_off)
    colors = [0] * fc
    rt = [0] * fc if has_info == 1 else None
    tx = [-1] * fc if has_info == 1 else None
    al = [0] * fc if has_alpha == 1 else None
    coord = [-1] * fc
    for i in range(fc):
        colors[i] = bc.u16()
        if has_info == 1:
            info = bi.u8()
            rt[i] = 1 if info & 1 else 0
            if info & 2:
                tx[i] = colors[i]
                colors[i] = 127
                coord[i] = info >> 2
        if al is not None:
            al[i] = ba.u8()

    f1, f2, f3 = _decode_faces(data, face_idx_off, face_idx_type_off, fc)
    m.vx, m.vy, m.vz, m.f1, m.f2, m.f3 = vx, vy, vz, f1, f2, f3
    m.colors, m.render_types, m.alphas = colors, rt, al
    m.textures = tx if tx and any(t != -1 for t in tx) else None
    m.tex_coord = coord if tc > 0 else None
    tp, tm, tn = [], [], []
    if tc > 0:
        bt = Buf(data, tex_tri_off)
        for _ in range(tc):
            tp.append(bt.u16())
            tm.append(bt.u16())
            tn.append(bt.u16())
    m.tex_p, m.tex_m, m.tex_n = tp, tm, tn
    m.vskins = _vskins(data, vskin_off, vc, has_vskin, has_maya)
    return m


def _vskins(data, off, vc, has_vskin, has_maya):
    """Per-vertex labels (vertex skins) that animation frame-bases transform; None if absent."""
    if has_vskin != 1 or has_maya == 1:
        return None
    return list(data[off:off + vc])


def _decode_faces(data, idx_off, type_off, fc):
    bi, bt = Buf(data, idx_off), Buf(data, type_off)
    f1, f2, f3 = [0] * fc, [0] * fc, [0] * fc
    a = b = c = last = 0
    for i in range(fc):
        t = bt.u8()
        if t == 1:
            a = bi.s_smart() + last
            b = bi.s_smart() + a
            c = bi.s_smart() + b
            last = c
        elif t == 2:
            b = c
            c = bi.s_smart() + last
            last = c
        elif t == 3:
            a = c
            c = bi.s_smart() + last
            last = c
        elif t == 4:
            tmp = a
            a = b
            b = tmp
            c = bi.s_smart() + last
            last = c
        f1[i], f2[i], f3[i] = a, b, c
    return f1, f2, f3


def _type3_tex_tris(data):
    """Vertex indices (P, M, N) of each type-0 texture triangle. Other types stay unset."""
    h = _footer_flags(data, 26)
    vc, fc, tc = h.u16(), h.u16(), h.u8()
    has_rtypes, prio, has_alpha, has_tskin, has_tex, has_vskin, has_maya = (
        h.u8(), h.u8(), h.u8(), h.u8(), h.u8(), h.u8(), h.u8())
    xlen, ylen, zlen, flen, tclen, vskin_len = h.u16(), h.u16(), h.u16(), h.u16(), h.u16(), h.u16()
    if tc <= 0:
        return [], [], []
    simple = 0
    for t in data[0:tc]:
        if t == 0:
            simple += 1
    off = tc + vc
    if has_rtypes == 1:
        off += fc
    off += fc
    if prio == 255:
        off += fc
    if has_tskin == 1:
        off += fc
    off += vskin_len
    if has_alpha == 1:
        off += fc
    off += flen
    if has_tex == 1:
        off += fc * 2
    off += tclen
    off += fc * 2
    off += xlen + ylen + zlen
    b = Buf(data, off)
    p, m, n = [-1] * tc, [-1] * tc, [-1] * tc
    for i in range(tc):
        if data[i] != 0:
            continue
        p[i], m[i], n[i] = b.u16(), b.u16(), b.u16()
    return p, m, n


def face_uvs(m):
    """UVs for faces the world export keeps, in its corner order (A, C, B). Units are 1/256 of a texture."""
    out = []
    for i in range(len(m.f1)):
        if m.render_types and m.render_types[i] == 2:
            continue
        if m.alphas and m.alphas[i] >= 254:
            continue
        tex = m.textures[i] if m.textures else -1
        if tex < 0:
            out.append((0, 0, 0, 0, 0, 0))
            continue
        a, b, c = m.f1[i], m.f2[i], m.f3[i]
        coord = m.tex_coord[i] if m.tex_coord else -1
        p = midx = n = -1
        if coord >= 0 and m.tex_p and coord < len(m.tex_p) and m.tex_p[coord] >= 0:
            p, midx, n = m.tex_p[coord], m.tex_m[coord], m.tex_n[coord]
        ua, va, ub, vb, uc, vc = _project_uv(m.vx, m.vy, m.vz, a, b, c, p, midx, n)
        out.append((_uv16(ua), _uv16(va), _uv16(uc), _uv16(vc), _uv16(ub), _uv16(vb)))
    return out


def _uv16(v):
    return max(-32768, min(32767, int(round(v * 256))))


def _project_uv(vx, vy, vz, a, b, c, p, m, n):
    """RuneLite's static (unprojected) texture mapping. P is (0, 0), M is (1, 0), N is (0, 1)."""
    if p < 0:
        return 0.0, 0.0, 1.0, 0.0, 0.0, 1.0
    t1x, t1y, t1z = vx[p], vy[p], vz[p]
    tx, ty, tz = vx[m] - t1x, vy[m] - t1y, vz[m] - t1z
    bx, by, bz = vx[n] - t1x, vy[n] - t1y, vz[n] - t1z
    nx = ty * bz - tz * by
    ny = tz * bx - tx * bz
    nz = tx * by - ty * bx
    ux, uy, uz = by * nz - bz * ny, bz * nx - bx * nz, bx * ny - by * nx
    vx_, vy_, vz_ = ty * nz - tz * ny, tz * nx - tx * nz, tx * ny - ty * nx
    du = ux * tx + uy * ty + uz * tz
    dv = vx_ * bx + vy_ * by + vz_ * bz
    if du == 0 or dv == 0:
        return 0.0, 0.0, 1.0, 0.0, 0.0, 1.0
    us, vs = [], []
    for i in (a, b, c):
        rx, ry, rz = vx[i] - t1x, vy[i] - t1y, vz[i] - t1z
        us.append((ux * rx + uy * ry + uz * rz) / du)
        vs.append((vx_ * rx + vy_ * ry + vz_ * rz) / dv)
    return us[0], vs[0], us[1], vs[1], us[2], vs[2]


def decode_model(data):
    tail = data[-2:]
    if tail == b"\xff\xfd":
        return decode_type3(data)
    if tail == b"\xff\xfe":
        return decode_type2(data)
    raise ValueError("unsupported model format")


class ModelCache:
    def __init__(self, cache):
        self.cache = cache
        self.models = {}

    def get(self, mid):
        if mid not in self.models:
            try:
                self.models[mid] = decode_model(self.cache.archive_data(7, mid))
            except (KeyError, ValueError, IndexError, struct.error):
                self.models[mid] = None
        return self.models[mid]

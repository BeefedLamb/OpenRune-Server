"""OSRS model decoder (type 2 and type 3 formats, the only ones in rev 241 caches)."""

import math

from cache import Buf


class Model:
    __slots__ = ("vx", "vy", "vz", "f1", "f2", "f3", "colors", "render_types",
                 "alphas", "textures")

    def copy(self):
        m = Model()
        m.vx, m.vy, m.vz = list(self.vx), list(self.vy), list(self.vz)
        m.f1, m.f2, m.f3 = list(self.f1), list(self.f2), list(self.f3)
        m.colors = list(self.colors)
        m.render_types = list(self.render_types) if self.render_types else None
        m.alphas = list(self.alphas) if self.alphas else None
        m.textures = list(self.textures) if self.textures else None
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
    for i in range(fc):
        colors[i] = bc.u16()
        if rt is not None:
            rt[i] = br.i8()
        if al is not None:
            al[i] = br_alpha = ba.u8()
        if tx is not None:
            tx[i] = bt.u16() - 1
            if tc > 0 and tx[i] != -1:
                bco.u8()

    f1, f2, f3 = _decode_faces(data, face_idx_off, face_idx_type_off, fc)
    m.vx, m.vy, m.vz, m.f1, m.f2, m.f3 = vx, vy, vz, f1, f2, f3
    m.colors, m.render_types, m.alphas, m.textures = colors, rt, al, tx
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
    for i in range(fc):
        colors[i] = bc.u16()
        if has_info == 1:
            info = bi.u8()
            rt[i] = 1 if info & 1 else 0
            if info & 2:
                tx[i] = colors[i]
                colors[i] = 127
        if al is not None:
            al[i] = ba.u8()

    f1, f2, f3 = _decode_faces(data, face_idx_off, face_idx_type_off, fc)
    m.vx, m.vy, m.vz, m.f1, m.f2, m.f3 = vx, vy, vz, f1, f2, f3
    m.colors, m.render_types, m.alphas = colors, rt, al
    m.textures = tx if tx and any(t != -1 for t in tx) else None
    return m


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
            except KeyError:
                self.models[mid] = None
        return self.models[mid]

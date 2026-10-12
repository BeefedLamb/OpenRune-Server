"""Decoders for the OSRS definitions the map exporter needs."""

from cache import Buf

CONFIG_INDEX = 2
UNDERLAY_ARCHIVE = 1
OVERLAY_ARCHIVE = 4
LOC_ARCHIVE = 6
TEXTURE_INDEX = 9
MODEL_INDEX = 7
MAP_INDEX = 5


# ---------------------------------------------------------------- colours

def hsl16_to_rgb(hsl: int, brightness: float = 0.7):
    """Packed 16-bit Jagex HSL -> (r, g, b) floats 0..1 (matches JagexColor.HSLtoRGB)."""
    hue = ((hsl >> 10) & 63) / 64.0 + 0.5 / 64.0
    sat = ((hsl >> 7) & 7) / 8.0 + 0.5 / 8.0
    lum = (hsl & 127) / 128.0
    chroma = (1.0 - abs(2.0 * lum - 1.0)) * sat
    x = chroma * (1 - abs(((hue * 6.0) % 2.0) - 1.0))
    m = lum - chroma / 2
    r = g = b = m
    seg = int(hue * 6.0)
    if seg == 0:
        r += chroma; g += x
    elif seg == 1:
        g += chroma; r += x
    elif seg == 2:
        g += chroma; b += x
    elif seg == 3:
        b += chroma; g += x
    elif seg == 4:
        b += chroma; r += x
    else:
        r += chroma; b += x
    return tuple(min(1.0, max(0.0, c)) ** brightness for c in (r, g, b))


def rgb24(v: int):
    return ((v >> 16) & 255) / 255.0, ((v >> 8) & 255) / 255.0, (v & 255) / 255.0


# ---------------------------------------------------------------- floors

def load_underlays(cache):
    out = {}
    for fid, data in cache.files(CONFIG_INDEX, UNDERLAY_ARCHIVE).items():
        b = Buf(data)
        color = 0
        while b.remaining():
            op = b.u8()
            if op == 0:
                break
            if op == 1:
                color = b.u24()
            else:
                raise ValueError(f"underlay {fid}: opcode {op}")
        out[fid] = rgb24(color)
    return out


class Overlay:
    __slots__ = ("rgb", "texture", "hide_underlay", "secondary")


def load_overlays(cache):
    out = {}
    for fid, data in cache.files(CONFIG_INDEX, OVERLAY_ARCHIVE).items():
        b = Buf(data)
        o = Overlay()
        o.rgb, o.texture, o.hide_underlay, o.secondary = 0, -1, True, -1
        while b.remaining():
            op = b.u8()
            if op == 0:
                break
            if op == 1:
                o.rgb = b.u24()
            elif op == 2:
                o.texture = b.u8()
            elif op == 5:
                o.hide_underlay = False
            elif op == 7:
                o.secondary = b.u24()
            else:
                raise ValueError(f"overlay {fid}: opcode {op}")
        out[fid] = o
    return out


def load_texture_colors(cache):
    """Texture id -> average colour (rev 233+ texture format stores it as packed HSL)."""
    out = {}
    for fid, data in cache.files(TEXTURE_INDEX, 0).items():
        b = Buf(data)
        b.u16()  # sprite id
        out[fid] = b.u16()
    return out


# ---------------------------------------------------------------- locs (objects)

class LocDef:
    def __init__(self, lid):
        self.id = lid
        self.name = "null"
        self.models = None
        self.model_types = None
        self.size_x = 1
        self.size_y = 1
        self.rotated = False
        self.scale = (128, 128, 128)
        self.offset = (0, 0, 0)
        self.recolor = []
        self.retexture = []
        self.decor_displacement = 16
        self.contoured = -1
        self.morphs = None
        self.ops = [None] * 5
        self.solid = 2

    def model_for(self, ltype):
        """(model_id_list, needs_mirror_base) for a scene request of loc type `ltype`."""
        if self.models is None:
            return None
        if self.model_types is None:
            if ltype != 10:
                return None
            return list(self.models)
        for t, m in zip(self.model_types, self.models):
            if t == ltype:
                return [m]
        return None


def _skip_ops(b, op):
    if 30 <= op < 35:
        b.string()
    elif op == 100:
        b.u8(); b.u8(); b.string()
    elif op == 101:
        b.u8(); b.u16(); b.u16(); b.i32(); b.i32(); b.string()
    elif op == 102:
        b.u8(); b.u16(); b.u16(); b.u16(); b.i32(); b.i32(); b.string()
    else:
        return False
    return True


def decode_loc(lid, data):
    d = LocDef(lid)
    b = Buf(data)
    sx = sy = sz = 128
    ox = oy = oz = 0
    while True:
        op = b.u8()
        if op == 0:
            break
        if op in (1, 6):
            n = b.u8()
            if n:
                models, types = [], []
                for _ in range(n):
                    models.append(b.u16() if op == 1 else b.i32())
                    types.append(b.u8())
                d.models, d.model_types = models, types
        elif op in (5, 7):
            n = b.u8()
            if n:
                d.model_types = None
                d.models = [b.u16() if op == 5 else b.i32() for _ in range(n)]
        elif op == 2:
            d.name = b.string()
        elif op == 14:
            d.size_x = b.u8()
        elif op == 15:
            d.size_y = b.u8()
        elif op == 17:
            d.solid = 0
        elif op == 18:
            pass
        elif op == 19:
            b.u8()
        elif op == 21:
            d.contoured = 0
        elif op in (22, 23, 27, 62, 64, 73, 74, 89, 90, 94):
            if op == 62:
                d.rotated = True
            elif op == 27:
                d.solid = 1
        elif op == 24:
            b.u16()
        elif op == 28:
            d.decor_displacement = b.u8()
        elif op in (29, 39):
            b.i8()
        elif 30 <= op < 35:
            text = b.string()
            if text.lower() != "hidden":
                d.ops[op - 30] = text
        elif op == 40:
            n = b.u8()
            d.recolor = [(b.u16(), b.u16()) for _ in range(n)]
        elif op == 41:
            n = b.u8()
            d.retexture = [(b.u16(), b.u16()) for _ in range(n)]
        elif op == 42:
            b.u16()
        elif op == 61:
            b.u16()
        elif op == 65:
            sx = b.u16()
        elif op == 66:
            sy = b.u16()
        elif op == 67:
            sz = b.u16()
        elif op == 68:
            b.u16()
        elif op == 69:
            b.u8()
        elif op == 70:
            ox = b.i16()
        elif op == 71:
            oy = b.i16()
        elif op == 72:
            oz = b.i16()
        elif op == 75:
            b.u8()
        elif op in (77, 92):
            b.u16(); b.u16()
            default = b.u16() if op == 92 else -1
            n = b.u8()
            morphs = []
            for _ in range(n + 1):
                v = b.u16()
                morphs.append(-1 if v == 0xFFFF else v)
            morphs.append(-1 if default == 0xFFFF else default)
            d.morphs = morphs
        elif op == 78:
            b.u16(); b.u8(); b.u8()
        elif op == 79:
            b.u16(); b.u16(); b.u8(); b.u8()
            n = b.u8()
            for _ in range(n):
                b.u16()
        elif op == 81:
            d.contoured = b.u8() * 256
        elif op == 82:
            b.u16()
        elif op == 91:
            b.u8()
        elif op == 93:
            b.u8(); b.u16(); b.u8(); b.u16()
        elif op == 95:
            b.u8()
        elif op == 96:
            b.u8()
        elif _skip_ops(b, op):
            pass
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
        else:
            raise ValueError(f"loc {lid}: unknown opcode {op} at {b.p}")
    d.scale = (sx, sy, sz)
    d.offset = (ox, oy, oz)
    return d


class LocDefs:
    def __init__(self, cache):
        self.raw = cache.files(CONFIG_INDEX, LOC_ARCHIVE)
        self.cache = {}

    def get(self, lid):
        if lid not in self.cache:
            data = self.raw.get(lid)
            self.cache[lid] = decode_loc(lid, data) if data is not None else None
        return self.cache[lid]

    def resolve(self, lid):
        """Follow multiloc morphs to the default (varbit 0) variant."""
        d = self.get(lid)
        seen = 0
        while d is not None and d.morphs and d.models is None and seen < 4:
            nxt = d.morphs[0] if d.morphs[0] != -1 else next((m for m in d.morphs if m != -1), -1)
            if nxt == -1:
                return d
            d = self.get(nxt)
            seen += 1
        return d


# ---------------------------------------------------------------- map squares

def decode_tiles(data):
    """Returns dict with height[z][x][y] (None = procedural), overlay, path, rot, settings, underlay."""
    b = Buf(data)
    shape = lambda v: [[[v] * 64 for _ in range(64)] for _ in range(4)]
    height = shape(None)
    overlay, path, rot, settings, underlay = shape(0), shape(0), shape(0), shape(0), shape(0)
    for z in range(4):
        for x in range(64):
            for y in range(64):
                while True:
                    attr = b.u16()
                    if attr == 0:
                        break
                    if attr == 1:
                        height[z][x][y] = b.u8()
                        break
                    if attr <= 49:
                        overlay[z][x][y] = b.i16()
                        path[z][x][y] = (attr - 2) // 4
                        rot[z][x][y] = (attr - 2) & 3
                    elif attr <= 81:
                        settings[z][x][y] = attr - 49
                    else:
                        underlay[z][x][y] = attr - 81
    return dict(height=height, overlay=overlay, path=path, rot=rot, settings=settings,
                underlay=underlay)


def decode_locs(data):
    b = Buf(data)
    out = []
    lid = -1
    while True:
        step = b.u_smart_incr()
        if step == 0:
            break
        lid += step
        pos = 0
        while True:
            dp = b.u_smart()
            if dp == 0:
                break
            pos += dp - 1
            ly = pos & 0x3F
            lx = (pos >> 6) & 0x3F
            level = (pos >> 12) & 3
            attr = b.u8()
            out.append((lid, lx, ly, level, attr >> 2, attr & 3))
    return out

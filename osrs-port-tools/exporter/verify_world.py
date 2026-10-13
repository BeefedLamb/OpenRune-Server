"""Rebuild a square from the exported world files the way the Unity loader will, and render it."""
import gzip, json, math, struct, sys
import numpy as np
from defs import hsl16_to_rgb
from preview import raster

def read_models(path):
    d = gzip.open(path).read(); p = 4; out = {}
    (n,) = struct.unpack_from('<I', d, 0)
    for _ in range(n):
        mid, nv, nf = struct.unpack_from('<IHI', d, p); p += 10
        V = np.frombuffer(d, '<i2', nv * 3, p).reshape(-1, 3).astype(np.float64); p += nv * 6
        F = np.frombuffer(d, np.dtype([('a','<u2'),('b','<u2'),('c','<u2'),('col','<u2'),('tex','<i2')]), nf, p); p += nf * 10
        out[mid] = (V, F)
    return out

def read_variants(path):
    d = gzip.open(path).read(); p = 0
    (nt,) = struct.unpack_from('<H', d, p); p += 2
    tex = {}
    for _ in range(nt):
        t, h = struct.unpack_from('<HH', d, p); p += 4; tex[t] = h
    (nv,) = struct.unpack_from('<I', d, p); p += 4
    vs = []
    for _ in range(nv):
        lid, mtype, mirror, nm = struct.unpack_from('<IBBB', d, p); p += 7
        ids = list(struct.unpack_from('<%dI' % nm, d, p)); p += 4 * nm
        (nr,) = struct.unpack_from('<B', d, p); p += 1
        rec = [struct.unpack_from('<HH', d, p + 4 * i) for i in range(nr)]; p += 4 * nr
        (nx,) = struct.unpack_from('<B', d, p); p += 1
        ret = [struct.unpack_from('<HH', d, p + 4 * i) for i in range(nx)]; p += 4 * nx
        scale = struct.unpack_from('<hhh', d, p); p += 6
        vs.append((lid, mtype, mirror, ids, rec, ret, scale))
    return tex, vs

def build_variant(models, tex, v, cache):
    key = id(v)
    if key in cache: return cache[key]
    lid, mtype, mirror, ids, rec, ret, scale = v
    Vs, Cs = [], []
    for mid in ids:
        V, F = models[mid]
        if len(F) == 0: continue
        V = V.copy()
        if mirror: V[:, 2] = -V[:, 2]
        V *= np.array(scale) / 128.0
        tri = np.stack([V[F['a']], V[F['c']], V[F['b']]] if mirror else [V[F['a']], V[F['b']], V[F['c']]], axis=1)
        col = F['col'].astype(int); tx = F['tex'].astype(int)
        for a, b in rec: col[col == a] = b
        for a, b in ret: tx[tx == a] = b
        rgb = np.array([hsl16_to_rgb(tex[t]) if (t != -1 and t in tex) else hsl16_to_rgb(c) for c, t in zip(col, tx)])
        Vs.append(tri); Cs.append(rgb)
    out = (np.concatenate(Vs) if Vs else np.zeros((0, 3, 3)), np.concatenate(Cs) if Cs else np.zeros((0, 3)))
    cache[key] = out
    return out

def load_square(world, rx, ry, models, tex, vs, vcache):
    idx = open(f'{world}/index.bin', 'rb').read()
    (n,) = struct.unpack_from('<I', idx, 4)
    for i in range(n):
        x, y, pack, off, ln = struct.unpack_from('<BBBII', idx, 8 + 11 * i)
        if (x, y) == (rx, ry): break
    else: raise KeyError
    with open(f'{world}/pack_{pack}.bin', 'rb') as f:
        f.seek(off); d = gzip.decompress(f.read(ln))
    assert d[:3] == b'SQ1'; p = 11
    (nv,) = struct.unpack_from('<I', d, p); p += 4
    TV = np.frombuffer(d, '<i2', nv * 3, p).reshape(-1, 3).astype(float) / 128.0; p += nv * 6
    TC = np.frombuffer(d, 'u1', nv * 3, p).reshape(-1, 3) / 255.0; p += nv * 3
    TV = TV + np.array([rx * 64, 0, ry * 64])
    (npl,) = struct.unpack_from('<I', d, p); p += 4
    P = np.frombuffer(d, np.dtype([('vid','<u4'),('lx','u1'),('lz','u1'),('level','u1'),('yaw','<u2'),('px','<i2'),('pz','<i2'),('py','<i2'),('decor','u1')]), npl, p)
    tris, cols = [TV.reshape(-1, 3, 3)], [TC.reshape(-1, 3, 3)]
    for r in P:
        T, C = build_variant(models, tex, vs[r['vid']], vcache)
        if len(T) == 0: continue
        th = r['yaw'] * 2 * math.pi / 2048
        s, c = math.sin(th), math.cos(th)
        X, Y, Z = T[..., 0], T[..., 1], T[..., 2]
        nx = c * X + s * Z; nz = -s * X + c * Z
        W = np.stack([nx / 128 + rx * 64 + r['px'] / 128.0, Y / 128 + r['py'] / 128.0, nz / 128 + ry * 64 + r['pz'] / 128.0], axis=-1)
        tris.append(W); cols.append(C[:, None, :].repeat(3, axis=1))
    return np.concatenate(tris), np.concatenate(cols)

if __name__ == '__main__':
    world = sys.argv[1]
    models = read_models(f'{world}/models.bin.gz'); tex, vs = read_variants(f'{world}/variants.bin.gz')
    T, C = load_square(world, 50, 50, models, tex, vs, {})
    print('tris', len(T))
    # back to OSRS units for the shared rasteriser (x east, y down, z north)
    P = (T * np.array([128, -128, 128])).reshape(-1, 3).tolist(); Cl = C.reshape(-1, 3).tolist()
    X0 = Y0 = 3200 * 128; S = 12 / 128.0; W = H = 768
    raster(P, Cl, W, H, lambda Q: ((Q[:, :, 0] - X0) * S, H - (Q[:, :, 2] - Y0) * S, Q[:, :, 1])).save('/tmp/verify_top.png')
    # perspective at the castle entrance, same camera as before
    cx, cz = 3226 * 128 + 64, 3218 * 128 + 64
    Pn = np.array(P).reshape(-1, 3, 3); d2 = (Pn[:, :, 0].mean(1) - cx) ** 2 + (Pn[:, :, 2].mean(1) - cz) ** 2
    gy = Pn[np.argmin(d2), :, 1].mean(); cam = np.array([cx, gy - 170, cz])
    yaw, pitch = math.radians(-90), math.radians(5)
    f = np.array([math.sin(yaw) * math.cos(pitch), math.sin(pitch), math.cos(yaw) * math.cos(pitch)]); up = np.array([0, -1.0, 0])
    r = np.cross(up, f); r /= np.linalg.norm(r); u = np.cross(f, r); F = 1 / math.tan(math.radians(70) / 2); Wd, Hd = 960, 540
    def project(Q):
        v = Q - cam; x, y, z = v @ r, v @ u, v @ f; z = np.where(z < 20, -1, z)
        sx = Wd / 2 + (x / z) * F * Wd / 2; sy = Hd / 2 - (y / z) * F * Wd / 2; sx[(z <= 0).any(axis=1)] = -1e9
        return sx, sy, z
    raster(P, Cl, Wd, Hd, project).save('/tmp/verify_p1.png')

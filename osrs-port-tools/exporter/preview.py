import sys, time, numpy as np
from cache import Cache
from scene import Scene, TriSoup
from PIL import Image

def raster(pos, col, img_w, img_h, project):
    P = np.array(pos, dtype=np.float64).reshape(-1, 3, 3)
    C = np.array(col, dtype=np.float64).reshape(-1, 3, 3).mean(axis=1)
    sx, sy, depth = project(P)  # (n,3) each
    # flat shading from face normal (osrs y is down)
    a, b, c = P[:, 0], P[:, 1], P[:, 2]
    n = np.cross(b - a, c - a); n /= (np.linalg.norm(n, axis=1, keepdims=True) + 1e-9)
    light = np.array([-0.4, -0.8, -0.3]); light /= np.linalg.norm(light)
    shade = 0.45 + 0.55 * np.abs(n @ light)
    C = np.clip(C * shade[:, None], 0, 1)
    zbuf = np.full((img_h, img_w), np.inf); img = np.zeros((img_h, img_w, 3)) + 0.1
    for t in range(len(P)):
        x0, x1 = int(max(0, np.floor(sx[t].min()))), int(min(img_w - 1, np.ceil(sx[t].max())))
        y0, y1 = int(max(0, np.floor(sy[t].min()))), int(min(img_h - 1, np.ceil(sy[t].max())))
        if x0 > x1 or y0 > y1: continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, bx, cx), (ay, by, cy) = sx[t], sy[t]
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9: continue
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        w2 = 1 - w0 - w1
        m = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not m.any(): continue
        d = w0 * depth[t, 0] + w1 * depth[t, 1] + w2 * depth[t, 2]
        sub = zbuf[y0:y1 + 1, x0:x1 + 1]
        upd = m & (d < sub)
        sub[upd] = d[upd]
        img[y0:y1 + 1, x0:x1 + 1][upd] = C[t]
    return Image.fromarray((img * 255).astype(np.uint8))

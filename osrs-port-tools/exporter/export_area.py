"""Export OSRS map squares to a .osm file for the Unity client.

Usage: python3 export_area.py <cache_dir> <out.osm> <rx> <ry> [radius]
Exports map square (rx, ry) with all locs, plus terrain for squares within `radius`.
Unity coords: x = east, y = up, z = north, 1 unit = 1 tile, origin = SW corner of (rx, ry).
"""
import json, struct, sys
from cache import Cache
from scene import Scene, TriSoup


def write_chunk(f, name, soup, base_x, base_y):
    n = len(soup.pos)
    f.write(struct.pack('<H', len(name.encode())) + name.encode())
    f.write(struct.pack('<i', n))
    pos = bytearray(n * 12)
    col = bytearray(n * 4)
    for t in range(0, n, 3):
        # one axis flips between OSRS and Unity, so swap two corners to keep winding
        for k, src in enumerate((t, t + 2, t + 1)):
            x, y, z = soup.pos[src]
            struct.pack_into('<fff', pos, (t + k) * 12, x / 128.0 - base_x, -y / 128.0, z / 128.0 - base_y)
            r, g, b = soup.col[src]
            col[(t + k) * 4:(t + k) * 4 + 4] = bytes((int(r * 255), int(g * 255), int(b * 255), 255))
    f.write(pos)
    f.write(col)


def main():
    cache_dir, out, rx, ry = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
    radius = int(sys.argv[5]) if len(sys.argv) > 5 else 1
    s = Scene(Cache(cache_dir))
    squares = [(x, y) for x in range(rx - radius, rx + radius + 1) for y in range(ry - radius, ry + radius + 1)]
    for sq in squares + [(x, y) for x in range(rx - radius - 1, rx + radius + 2) for y in (ry - radius - 1, ry + radius + 1)] \
            + [(x, y) for x in (rx - radius - 1, rx + radius + 1) for y in range(ry - radius, ry + radius + 1)]:
        try:
            s.load_square(*sq)
        except KeyError:
            pass
    x0, y0 = (rx - radius) * 64, (ry - radius) * 64
    x1, y1 = (rx + radius + 1) * 64, (ry + radius + 1) * 64
    s.compute_heights(x0 - 1, y0 - 1, x1 + 1, y1 + 1)
    base_x, base_y = rx * 64, ry * 64
    chunks, meta = [], []
    for (sx, sy) in squares:
        if (sx, sy) not in s.tiles:
            continue
        t = TriSoup()
        s.build_terrain(sx * 64, sy * 64, sx * 64 + 63, sy * 64 + 63, t)
        chunks.append((f'terrain_{sx}_{sy}', t))
    solid, decor = TriSoup(), TriSoup()
    s.build_locs([(rx, ry)], solid, meta, decor)
    chunks.append((f'locs_{rx}_{ry}', solid))
    chunks.append((f'decor_{rx}_{ry}', decor))
    with open(out, 'wb') as f:
        f.write(b'OSM1' + struct.pack('<iii', base_x, base_y, len(chunks)))
        for name, soup in chunks:
            write_chunk(f, name, soup, base_x, base_y)
    with open(out.rsplit('.', 1)[0] + '_locs.json', 'w') as f:
        json.dump({'base': [base_x, base_y], 'locs': meta}, f)
    for name, soup in chunks:
        print(name, len(soup), 'tris')


if __name__ == '__main__':
    main()

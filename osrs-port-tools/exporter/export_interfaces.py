"""Export a fixed-mode interface's graphic components and their sprites.

Usage: python export_interfaces.py <cache_dir> <out_dir> <interface_id>

Writes <out_dir>/ui/if/<id>.txt and <out_dir>/ui/if/sprite/<spriteId>.png.
Each line of the text file is one graphic, already laid out in the 765x503 page:

    x y w h sprite tiled opacity

Positions follow the client's alignment modes. Hidden components are left out.
"""
import os
import sys

from PIL import Image

from cache import Buf, Cache
from export_textures import decode_sprite

PAGE_W = 765
PAGE_H = 503
SPRITES = 8


def skip_listener(b):
    n = b.u8()
    for _ in range(n):
        kind = b.u8()
        if kind == 0:
            b.i32()
        elif kind == 1:
            b.string()
        else:
            raise ValueError(f"listener {kind}")


def skip_triggers(b):
    n = b.u8()
    for _ in range(n):
        b.i32()


def decode_component(data, wid):
    b = Buf(data)
    if b.u8() != 255:
        raise ValueError(f"component {wid} is not IF3")
    t = b.u8()
    b.u16()
    x, y = b.i16(), b.i16()
    w = b.u16()
    h = b.i16() if t == 9 else b.u16()
    wm, hm, xm, ym = b.i8(), b.i8(), b.i8(), b.i8()
    parent = b.u16()
    if parent == 0xFFFF:
        parent = -1
    hidden = b.u8() == 1
    sprite = -1
    tiled = 0
    opacity = 0
    if t == 0:
        b.u16(); b.u16(); b.u8()
    elif t == 5:
        sprite = b.i32()
        b.u16()
        tiled = b.u8()
        opacity = b.u8()
        b.u8(); b.i32(); b.u8(); b.u8()
    elif t == 6:
        b.i32(); b.i16(); b.i16()
        b.u16(); b.u16(); b.u16(); b.u16(); b.u16(); b.u8(); b.u16()
    elif t == 4:
        b.u16(); b.string(); b.u8(); b.u8(); b.u8(); b.u8(); b.i32()
    elif t == 3:
        b.i32(); b.u8(); b.u8()
    elif t == 9:
        b.u8(); b.i32(); b.u8()
    else:
        raise ValueError(f"component {wid} type {t}")
    b.u24()
    b.string()
    for _ in range(b.u8()):
        b.string()
    b.u8(); b.u8(); b.u8()
    b.string()
    for _ in range(18):
        skip_listener(b)
    skip_triggers(b); skip_triggers(b); skip_triggers(b)
    if b.remaining():
        raise ValueError(f"component {wid} leftover {b.remaining()}")
    return {
        "id": wid, "type": t, "x": x, "y": y, "w": w, "h": h,
        "wm": wm, "hm": hm, "xm": xm, "ym": ym, "parent": parent,
        "hidden": hidden, "sprite": sprite, "tiled": tiled, "opacity": opacity,
    }


def align(mode, raw, parent_size, size):
    if mode == 0:
        return raw
    if mode == 1:
        return (parent_size - size) // 2 + raw
    if mode == 2:
        return parent_size - size - raw
    if mode == 3:
        return (parent_size * raw) >> 14
    if mode == 4:
        return ((parent_size - size) * raw) >> 14
    return ((parent_size - size) * (raw + 16384)) >> 14


def layout(rows):
    by_id = {r["id"]: r for r in rows}
    done = {}

    def box(i):
        if i in done:
            return done[i]
        r = by_id[i]
        if r["parent"] < 0 or r["parent"] not in by_id:
            px, py, pw, ph = 0, 0, PAGE_W, PAGE_H
        else:
            px, py, pw, ph = box(r["parent"])
        w, h = r["w"], r["h"]
        if r["wm"] == 1:
            w = pw - r["w"]
        elif r["wm"] == 2:
            w = (pw * r["w"]) >> 14
        if r["hm"] == 1:
            h = ph - r["h"]
        elif r["hm"] == 2:
            h = (ph * r["h"]) >> 14
        ax = align(r["xm"], r["x"], pw, w)
        ay = align(r["ym"], r["y"], ph, h)
        done[i] = (px + ax, py + ay, w, h)
        return done[i]

    placed = []
    for r in rows:
        if r["hidden"] or r["type"] != 5 or r["sprite"] < 0:
            continue
        x, y, w, h = box(r["id"])
        placed.append((r["id"], x, y, w, h, r["sprite"], r["tiled"], r["opacity"]))
    return placed


def main():
    cache_dir, out_dir, iid = sys.argv[1], sys.argv[2], int(sys.argv[3])
    cache = Cache(cache_dir)
    files = cache.files(3, iid)
    rows = [decode_component(data, fid) for fid, data in sorted(files.items())]
    placed = layout(rows)
    ui = os.path.join(out_dir, "ui", "if")
    sprites = os.path.join(ui, "sprite")
    os.makedirs(sprites, exist_ok=True)
    needed = sorted({p[5] for p in placed})
    for sid in needed:
        path = os.path.join(sprites, f"{sid}.png")
        if os.path.exists(path):
            continue
        frames = decode_sprite(cache.archive_data(SPRITES, sid))
        width, height, pixels = frames[0]
        Image.frombytes("RGBA", (width, height), pixels).save(path)
        print(f"sprite {sid} {width}x{height}")
    with open(os.path.join(ui, f"{iid}.txt"), "w", encoding="utf-8") as f:
        for _, x, y, w, h, sprite, tiled, opacity in placed:
            f.write(f"{x}\t{y}\t{w}\t{h}\t{sprite}\t{tiled}\t{opacity}\n")
    print(f"interface {iid}: {len(placed)} graphics, {len(needed)} sprites")


if __name__ == "__main__":
    main()

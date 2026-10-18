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


def save_sprite(cache, sprites, sid):
    frames = decode_sprite(cache.archive_data(SPRITES, sid))
    for i, (width, height, pixels) in enumerate(frames):
        name = f"{sid}.png" if i == 0 else f"{sid}_{i}.png"
        path = os.path.join(sprites, name)
        if os.path.exists(path):
            continue
        Image.frombytes("RGBA", (width, height), pixels).save(path)
        print(f"sprite {sid} frame {i} {width}x{height}")


def write_fixed_chat(cache, out_dir):
    """The opaque chat box as the client builds it: parchment, then the filter row."""
    ui = os.path.join(out_dir, "ui", "if")
    sprites = os.path.join(ui, "sprite")
    os.makedirs(sprites, exist_ok=True)
    for sid in (1017, 1018, 3051, 3057, 316):
        save_sprite(cache, sprites, sid)
    y_bar = 480
    buttons = (3, 65, 127, 189, 251, 313, 375)
    lines = [
        (0, 338, 519, 142, 1017, 0, 0, 0),
        (0, y_bar, 519, 23, 1018, 0, 0, 0),
    ]
    for x in buttons:
        lines.append((x, y_bar + 1, 56, 22, 3051, 0, 0, 0))
    lines.append((437, y_bar + 1, 79, 22, 3057, 0, 0, 0))
    lines.append((499, 343, 16, 16, 316, 0, 0, 0))
    lines.append((499, 460, 16, 16, 316, 0, 0, 1))
    with open(os.path.join(ui, "chat.txt"), "w", encoding="utf-8") as f:
        for row in lines:
            f.write("\t".join(str(v) for v in row) + "\n")
    print(f"chat box: {len(lines)} graphics")


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
        save_sprite(cache, sprites, sid)
    with open(os.path.join(ui, f"{iid}.txt"), "w", encoding="utf-8") as f:
        for _, x, y, w, h, sprite, tiled, opacity in placed:
            f.write(f"{x}\t{y}\t{w}\t{h}\t{sprite}\t{tiled}\t{opacity}\t0\n")
    print(f"interface {iid}: {len(placed)} graphics, {len(needed)} sprites")
    if iid == 548:
        write_fixed_chat(cache, out_dir)


if __name__ == "__main__":
    main()

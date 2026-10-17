"""Export cache music (index 6) and jingles (index 11) as standard MIDI.

Usage: python export_music.py <cache_dir> <out_dir>

Writes <out_dir>/world/music.bin.gz, little-endian:
    "OSM1", u32 count, per track: i32 id, u8 kind (0 song, 1 jingle), u32 length, MIDI bytes.

The cache stores Jagex's packed MIDI. This is the same conversion as the client's track loader:
the packed opcodes become a type-0/1 SMF a soundfont synth can play.
"""
import gzip
import os
import struct
import sys

from cache import Cache

SONG_INDEX = 6
JINGLE_INDEX = 11

NOTE_ON = 0x90
NOTE_OFF = 0x80
CONTROL_CHANGE = 0xB0
PITCH_WHEEL_CHANGE = 0xE0
CHANNEL_PRESSURE = 0xD0
POLYPHONIC_KEY_PRESSURE = 0xA0
PROGRAM_CHANGE = 0xC0
META = 0xFF
END_OF_TRACK = 0x2F
TEMPO = 0x51

JAG_NOTE_ON = 0
JAG_NOTE_OFF = 1
JAG_CONTROL_CHANGE = 2
JAG_PITCH_BEND = 3
JAG_CHANNEL_PRESSURE = 4
JAG_POLY_PRESSURE = 5
JAG_PROGRAM_CHANGE = 6
JAG_END_OF_TRACK = 7
JAG_TEMPO = 23


def s8(v):
    return v - 256 if v > 127 else v


class In:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def u16(self):
        v = (self.d[self.p] << 8) | self.d[self.p + 1]
        self.p += 2
        return v

    def varint(self):
        value = s8(self.d[self.p])
        self.p += 1
        shifted = 0
        while value < 0:
            shifted = (shifted | (value & 0x7F)) << 7
            value = s8(self.d[self.p])
            self.p += 1
        return shifted | value

    def skip(self, n):
        self.p += n


class Out:
    def __init__(self):
        self.d = bytearray()

    def u8(self, v):
        self.d.append(v & 0xFF)

    def u16(self, v):
        self.d += struct.pack(">H", v & 0xFFFF)

    def u32(self, v):
        self.d += struct.pack(">I", v & 0xFFFFFFFF)

    def varint(self, var):
        if var & 0xFFFFFF80:
            if var & 0xFFFFC000:
                if var & 0xFFE00000:
                    if var & 0xF0000000:
                        self.u8((var >> 28) | 0x80)
                    self.u8((var >> 21) | 0x80)
                self.u8((var >> 14) | 0x80)
            self.u8((var >> 7) | 0x80)
        self.u8(var & 0x7F)

    def patch_u32(self, at, v):
        struct.pack_into(">I", self.d, at, v & 0xFFFFFFFF)


def jagex_to_midi(data):
    src = In(data)
    src.p = len(data) - 3
    tracks = src.u8()
    division = src.u16()
    src.p = 0
    tempo_n = ctrl_n = note_on_n = note_off_n = 0
    wheel_n = ch_pressure_n = key_pressure_n = program_n = 0
    for _ in range(tracks):
        prev = -1
        while True:
            status = src.u8()
            if status != prev:
                pass
            prev = status & 15
            if status == JAG_END_OF_TRACK:
                break
            if status == JAG_TEMPO:
                tempo_n += 1
            elif prev == JAG_NOTE_ON:
                note_on_n += 1
            elif prev == JAG_NOTE_OFF:
                note_off_n += 1
            elif prev == JAG_CONTROL_CHANGE:
                ctrl_n += 1
            elif prev == JAG_PITCH_BEND:
                wheel_n += 1
            elif prev == JAG_CHANNEL_PRESSURE:
                ch_pressure_n += 1
            elif prev == JAG_POLY_PRESSURE:
                key_pressure_n += 1
            elif prev == JAG_PROGRAM_CHANGE:
                program_n += 1
            else:
                raise ValueError(f"bad opcode {status}")
    varint_at = src.p
    opcode_count = (
        tracks + tempo_n + ctrl_n + note_on_n + note_off_n + wheel_n + ch_pressure_n + key_pressure_n + program_n
    )
    for _ in range(opcode_count):
        src.varint()

    mod_n = mod2_n = vol_n = vol2_n = pan_n = pan2_n = 0
    nr_msb_n = nr_lsb_n = r_msb_n = r_lsb_n = commands_n = other_n = 0
    controller = 0
    ctrl_numbers_at = src.p
    for _ in range(ctrl_n):
        controller = (controller + s8(data[src.p])) & 127
        src.p += 1
        if controller in (0, 32):
            program_n += 1
        elif controller == 1:
            mod_n += 1
        elif controller == 33:
            mod2_n += 1
        elif controller == 7:
            vol_n += 1
        elif controller == 39:
            vol2_n += 1
        elif controller == 10:
            pan_n += 1
        elif controller == 42:
            pan2_n += 1
        elif controller == 99:
            nr_msb_n += 1
        elif controller == 98:
            nr_lsb_n += 1
        elif controller == 101:
            r_msb_n += 1
        elif controller == 100:
            r_lsb_n += 1
        elif controller in (64, 65, 120, 121, 123):
            commands_n += 1
        else:
            other_n += 1

    commands_at = src.p
    src.skip(commands_n)
    poly_at = src.p
    src.skip(key_pressure_n)
    pressure_at = src.p
    src.skip(ch_pressure_n)
    wheel_hi_at = src.p
    src.skip(wheel_n)
    mod_at = src.p
    src.skip(mod_n)
    vol_at = src.p
    src.skip(vol_n)
    pan_at = src.p
    src.skip(pan_n)
    notes_at = src.p
    src.skip(note_on_n + note_off_n + key_pressure_n)
    notes_on_at = src.p
    src.skip(note_on_n)
    other_at = src.p
    src.skip(other_n)
    notes_off_at = src.p
    src.skip(note_off_n)
    mod2_at = src.p
    src.skip(mod2_n)
    vol2_at = src.p
    src.skip(vol2_n)
    pan2_at = src.p
    src.skip(pan2_n)
    program_at = src.p
    src.skip(program_n)
    wheel_lo_at = src.p
    src.skip(wheel_n)
    nr_msb_at = src.p
    src.skip(nr_msb_n)
    nr_lsb_at = src.p
    src.skip(nr_lsb_n)
    r_msb_at = src.p
    src.skip(r_msb_n)
    r_lsb_at = src.p
    src.skip(r_lsb_n)
    tempo_at = src.p

    out = Out()
    out.u32(0x4D546864)
    out.u32(6)
    out.u16(1 if tracks > 1 else 0)
    out.u16(tracks)
    out.u16(division)
    src.p = varint_at
    channel = 0
    note = vel_on = vel_off = wheel = pressure = poly = 0
    last = [0] * 128
    controller = 0
    op = 0
    for _track in range(tracks):
        out.u32(0x4D54726B)
        length_at = len(out.d)
        out.u32(0)
        start = len(out.d)
        prev = -1
        while True:
            delta = src.varint()
            out.varint(delta)
            status = data[op] & 255
            op += 1
            changed = status != prev
            prev = status & 15
            if status == JAG_END_OF_TRACK:
                out.u8(META)
                out.u8(END_OF_TRACK)
                out.u8(0)
                out.patch_u32(length_at, len(out.d) - start)
                break
            if status == JAG_TEMPO:
                out.u8(META)
                out.u8(TEMPO)
                out.u8(3)
                out.u8(data[tempo_at])
                out.u8(data[tempo_at + 1])
                out.u8(data[tempo_at + 2])
                tempo_at += 3
                continue
            channel ^= status >> 4
            if prev == JAG_NOTE_ON:
                if changed:
                    out.u8(NOTE_ON + channel)
                note = (note + s8(data[notes_at])) & 127
                notes_at += 1
                vel_on = (vel_on + s8(data[notes_on_at])) & 127
                notes_on_at += 1
                out.u8(note)
                out.u8(vel_on)
            elif prev == JAG_NOTE_OFF:
                if changed:
                    out.u8(NOTE_OFF + channel)
                note = (note + s8(data[notes_at])) & 127
                notes_at += 1
                vel_off = (vel_off + s8(data[notes_off_at])) & 127
                notes_off_at += 1
                out.u8(note)
                out.u8(vel_off)
            elif prev == JAG_CONTROL_CHANGE:
                if changed:
                    out.u8(CONTROL_CHANGE + channel)
                controller = (controller + s8(data[ctrl_numbers_at])) & 127
                ctrl_numbers_at += 1
                out.u8(controller)
                if controller in (0, 32):
                    raw = data[program_at]
                    program_at += 1
                elif controller == 1:
                    raw = data[mod_at]
                    mod_at += 1
                elif controller == 33:
                    raw = data[mod2_at]
                    mod2_at += 1
                elif controller == 7:
                    raw = data[vol_at]
                    vol_at += 1
                elif controller == 39:
                    raw = data[vol2_at]
                    vol2_at += 1
                elif controller == 10:
                    raw = data[pan_at]
                    pan_at += 1
                elif controller == 42:
                    raw = data[pan2_at]
                    pan2_at += 1
                elif controller == 99:
                    raw = data[nr_msb_at]
                    nr_msb_at += 1
                elif controller == 98:
                    raw = data[nr_lsb_at]
                    nr_lsb_at += 1
                elif controller == 101:
                    raw = data[r_msb_at]
                    r_msb_at += 1
                elif controller == 100:
                    raw = data[r_lsb_at]
                    r_lsb_at += 1
                elif controller in (64, 65, 120, 121, 123):
                    raw = data[commands_at]
                    commands_at += 1
                else:
                    raw = data[other_at]
                    other_at += 1
                value = (s8(raw) + last[controller]) & 127
                last[controller] = value
                out.u8(value)
            elif prev == JAG_PITCH_BEND:
                if changed:
                    out.u8(PITCH_WHEEL_CHANGE + channel)
                wheel += s8(data[wheel_lo_at])
                wheel_lo_at += 1
                wheel += s8(data[wheel_hi_at]) << 7
                wheel_hi_at += 1
                out.u8(wheel & 127)
                out.u8((wheel >> 7) & 127)
            elif prev == JAG_CHANNEL_PRESSURE:
                if changed:
                    out.u8(CHANNEL_PRESSURE + channel)
                pressure = (pressure + s8(data[pressure_at])) & 127
                pressure_at += 1
                out.u8(pressure)
            elif prev == JAG_POLY_PRESSURE:
                if changed:
                    out.u8(POLYPHONIC_KEY_PRESSURE + channel)
                note = (note + s8(data[notes_at])) & 127
                notes_at += 1
                poly = (poly + s8(data[poly_at])) & 127
                poly_at += 1
                out.u8(note)
                out.u8(poly)
            elif prev == JAG_PROGRAM_CHANGE:
                if changed:
                    out.u8(PROGRAM_CHANGE + channel)
                out.u8(data[program_at])
                program_at += 1
            else:
                raise ValueError(f"bad status {status}")
    return bytes(out.d)


def collect(cache, index, kind):
    tracks = []
    bad = 0
    for aid in sorted(cache.info(index).archives):
        try:
            files = cache.files(index, aid)
            data = files.get(0) or next(iter(files.values()), None)
            if not data:
                bad += 1
                continue
            midi = jagex_to_midi(data)
        except Exception:
            bad += 1
            continue
        if not midi.startswith(b"MThd"):
            bad += 1
            continue
        tracks.append((aid, kind, midi))
    return tracks, bad


def main():
    cache_dir, out_dir = sys.argv[1], sys.argv[2]
    cache = Cache(cache_dir)
    songs, song_bad = collect(cache, SONG_INDEX, 0)
    jingles, jingle_bad = collect(cache, JINGLE_INDEX, 1)
    tracks = songs + jingles
    path = os.path.join(out_dir, "world", "music.bin.gz")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with gzip.open(path, "wb", 6) as f:
        f.write(b"OSM1")
        f.write(struct.pack("<I", len(tracks)))
        for aid, kind, midi in tracks:
            f.write(struct.pack("<iBI", aid, kind, len(midi)))
            f.write(midi)
    print(
        f"music {len(songs)} songs ({song_bad} bad), {len(jingles)} jingles ({jingle_bad} bad), "
        f"{os.path.getsize(path) / 1e6:.1f} MB"
    )


if __name__ == "__main__":
    main()

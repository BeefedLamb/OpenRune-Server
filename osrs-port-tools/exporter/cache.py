"""Minimal reader for the OSRS JS5 disk cache (main_file_cache.dat2 + idx files)."""

import bz2
import gzip
import os
import struct
import zlib

SECTOR_SIZE = 520


def java_hash(name: str) -> int:
    h = 0
    for c in name:
        h = (h * 31 + ord(c)) & 0xFFFFFFFF
    return h - (1 << 32) if h & 0x80000000 else h


class Buf:
    __slots__ = ("d", "p")

    def __init__(self, data: bytes, pos: int = 0):
        self.d = data
        self.p = pos

    def remaining(self):
        return len(self.d) - self.p

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def i8(self):
        v = self.d[self.p]
        self.p += 1
        return v - 256 if v > 127 else v

    def u16(self):
        v = (self.d[self.p] << 8) | self.d[self.p + 1]
        self.p += 2
        return v

    def i16(self):
        v = self.u16()
        return v - 65536 if v > 32767 else v

    def u24(self):
        d, p = self.d, self.p
        self.p += 3
        return (d[p] << 16) | (d[p + 1] << 8) | d[p + 2]

    def i32(self):
        v = struct.unpack_from(">i", self.d, self.p)[0]
        self.p += 4
        return v

    def u32(self):
        v = struct.unpack_from(">I", self.d, self.p)[0]
        self.p += 4
        return v

    def big_smart(self):
        if self.d[self.p] & 0x80:
            return self.i32() & 0x7FFFFFFF
        return self.u16()

    def big_smart2(self):
        """-1-able big smart used by ObjectDefinition models (protocol variant)."""
        if self.d[self.p] & 0x80:
            return self.i32() & 0x7FFFFFFF
        v = self.u16()
        return -1 if v == 32767 else v

    def u_smart(self):
        if self.d[self.p] < 128:
            return self.u8()
        return self.u16() - 32768

    def s_smart(self):
        if self.d[self.p] < 128:
            return self.u8() - 64
        return self.u16() - 49152

    def u_smart_incr(self):
        total = 0
        v = self.u_smart()
        while v == 32767:
            total += 32767
            v = self.u_smart()
        return total + v

    def string(self):
        end = self.d.index(0, self.p)
        s = self.d[self.p:end].decode("latin-1")
        self.p = end + 1
        return s

    def bytes(self, n):
        v = self.d[self.p:self.p + n]
        self.p += n
        return v


def xtea_decrypt(data: bytes, keys) -> bytes:
    if not keys or all(k == 0 for k in keys):
        return data
    out = bytearray(data)
    n = len(data) // 8
    delta = 0x9E3779B9
    k = [x & 0xFFFFFFFF for x in keys]
    for b in range(n):
        v0, v1 = struct.unpack_from(">II", out, b * 8)
        s = (delta * 32) & 0xFFFFFFFF
        for _ in range(32):
            v1 = (v1 - ((((v0 << 4) ^ (v0 >> 5)) + v0) ^ (s + k[(s >> 11) & 3]))) & 0xFFFFFFFF
            s = (s - delta) & 0xFFFFFFFF
            v0 = (v0 - ((((v1 << 4) ^ (v1 >> 5)) + v1) ^ (s + k[s & 3]))) & 0xFFFFFFFF
        struct.pack_into(">II", out, b * 8, v0, v1)
    return bytes(out)


def decompress_container(data: bytes, keys=None) -> bytes:
    compression = data[0]
    length = struct.unpack_from(">i", data, 1)[0]
    if compression == 0:
        return xtea_decrypt(data[5:5 + length], keys)
    payload = xtea_decrypt(data[5:5 + length + 4], keys)
    decompressed_len = struct.unpack_from(">i", payload, 0)[0]
    body = payload[4:4 + length]
    if compression == 1:
        out = bz2.decompress(b"BZh1" + body)
    elif compression == 2:
        out = gzip.decompress(body)
    elif compression == 3:
        import lzma  # noqa: F401  (LZMA containers are not used by OSRS)
        raise ValueError("LZMA container")
    else:
        raise ValueError(f"unknown compression {compression}")
    if len(out) != decompressed_len:
        raise ValueError("decompressed length mismatch")
    return out


class ArchiveInfo:
    __slots__ = ("id", "name_hash", "crc", "revision", "file_ids", "file_name_hashes")


class IndexInfo:
    def __init__(self, data: bytes):
        b = Buf(data)
        protocol = b.u8()
        if protocol >= 6:
            self.revision = b.i32()
        flags = b.u8()
        named = flags & 1
        sized = flags & 4
        rd = b.big_smart if protocol >= 7 else b.u16
        count = rd()
        archives = []
        last = 0
        for _ in range(count):
            last += rd()
            a = ArchiveInfo()
            a.id = last
            a.name_hash = 0
            archives.append(a)
        if named:
            for a in archives:
                a.name_hash = b.i32()
        for a in archives:
            a.crc = b.i32()
        if sized:
            for a in archives:
                b.i32()
                b.i32()
        for a in archives:
            a.revision = b.i32()
        counts = [rd() for _ in archives]
        for a, n in zip(archives, counts):
            ids = []
            last = 0
            for _ in range(n):
                last += rd()
                ids.append(last)
            a.file_ids = ids
            a.file_name_hashes = [0] * n
        if named:
            for a in archives:
                a.file_name_hashes = [b.i32() for _ in a.file_ids]
        self.archives = {a.id: a for a in archives}
        self.by_name = {a.name_hash: a for a in archives if a.name_hash}


class Cache:
    def __init__(self, path: str):
        self.path = path
        self.dat = open(os.path.join(path, "main_file_cache.dat2"), "rb")
        self._idx = {}
        self._info = {}

    def _index_file(self, index: int) -> bytes:
        if index not in self._idx:
            with open(os.path.join(self.path, f"main_file_cache.idx{index}"), "rb") as f:
                self._idx[index] = f.read()
        return self._idx[index]

    def raw(self, index: int, archive: int) -> bytes:
        idx = self._index_file(index)
        off = archive * 6
        if off + 6 > len(idx):
            raise KeyError((index, archive))
        size = int.from_bytes(idx[off:off + 3], "big")
        sector = int.from_bytes(idx[off + 3:off + 6], "big")
        if size == 0 or sector == 0:
            raise KeyError((index, archive))
        extended = archive > 0xFFFF
        header = 10 if extended else 8
        out = bytearray()
        chunk = 0
        while len(out) < size:
            self.dat.seek(sector * SECTOR_SIZE)
            block = self.dat.read(SECTOR_SIZE)
            if extended:
                a, c, nxt, ix = struct.unpack_from(">IHI", block[:10] + b"\0")[0], None, None, None
                a = struct.unpack_from(">I", block, 0)[0]
                c = struct.unpack_from(">H", block, 4)[0]
                nxt = int.from_bytes(block[6:9], "big")
                ix = block[9]
            else:
                a = struct.unpack_from(">H", block, 0)[0]
                c = struct.unpack_from(">H", block, 2)[0]
                nxt = int.from_bytes(block[4:7], "big")
                ix = block[7]
            if a != archive or c != chunk or ix != index:
                raise IOError(f"sector mismatch idx{index} archive {archive}")
            take = min(size - len(out), SECTOR_SIZE - header)
            out += block[header:header + take]
            sector = nxt
            chunk += 1
        return bytes(out)

    def info(self, index: int) -> IndexInfo:
        if index not in self._info:
            self._info[index] = IndexInfo(decompress_container(self.raw(255, index)))
        return self._info[index]

    def archive_data(self, index: int, archive: int, keys=None) -> bytes:
        return decompress_container(self.raw(index, archive), keys)

    def files(self, index: int, archive: int, keys=None) -> dict:
        info = self.info(index).archives[archive]
        data = self.archive_data(index, archive, keys)
        ids = info.file_ids
        if len(ids) == 1:
            return {ids[0]: data}
        n = len(ids)
        chunks = data[-1]
        b = Buf(data, len(data) - 1 - chunks * n * 4)
        sizes = [[0] * chunks for _ in range(n)]
        for c in range(chunks):
            run = 0
            for i in range(n):
                run += b.i32()
                sizes[i][c] = run
        out = [bytearray() for _ in range(n)]
        p = 0
        for c in range(chunks):
            for i in range(n):
                s = sizes[i][c]
                out[i] += data[p:p + s]
                p += s
        return {ids[i]: bytes(out[i]) for i in range(n)}

    def archive_by_name(self, index: int, name: str):
        return self.info(index).by_name.get(java_hash(name))

#!/usr/bin/env python3
"""Dump the client's item icons out of its .pc archives as PNGs.

The admin panel (SkySaga.Game/Admin) serves whatever this writes, so items show their real
in-game artwork instead of a placeholder tile.

Only ~37 of the 314 inventory items actually have an icon texture: the game renders most
items from their 3D model into the inventory slot, and only these have a 2D `t_icons_<Name>`
sprite. Everything else falls back to the panel's initials tile.

Format notes (ported from SkySagaResourceExplorer, which reversed it):

  .pc archive     u32 fileCount @12, u32 version @52 (170 or 173), u32 dataOffset @368,
                  then a 48-byte entry per file from 0x180:
                  {u32 pad, u32 nameHash, i32 size, i32 compressedSize, i32 nameOffset,
                   u32 fourCC, 24 bytes pad}
                  File data starts at 0x180 + dataOffset and each entry is padded to 16
                  bytes. An entry is zlib-compressed when size != compressedSize.
  name hash       CRC-32, poly 0x04C11DB7, non-reflected, **init 0**, no final xor, over the
                  **lowercased** name (Util.ComputeCrc32 on both client and server).
  TEXR resource   u16 width, height, depth, flags @84; u8 format @92; u8 levels @94;
                  pixels from @124. format 1 = raw BGRA, 18 = DXT1, 20 = DXT3, 22 = DXT5.

Usage:
    uv run --with pillow Tools/IconExtract/extract_icons.py [--data DIR] [--out DIR]

The client itself is not in the repository; point --data (or SKYSAGA_DIR) at your copy.
"""

import argparse
import glob
import io
import json
import os
import struct
import sys
import zlib

from PIL import Image

# Tools/IconExtract/extract_icons.py -> the repository root is two directories up.
REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# The client is not part of the repository, so its Data directory has to be pointed at:
# SKYSAGA_DIR (the folder holding Client/), or --data.
DEFAULT_DATA = os.path.join(
    os.environ.get("SKYSAGA_DIR", os.path.join(REPO, "SkySaga Infinite Isles")), "Client", "Data")
DEFAULT_GEO = os.path.join(REPO, "Bundled", "10414", "geodata.json")
DEFAULT_OUT = os.path.join(REPO, "Servers", "SkySaga.Game", "Admin", "icons")

# UI textures worth having so the panel can look like the real rucksack.
CHROME = ["t_icons_emptyslot", "t_icons_placeholder"]

_TABLE = []
for _i in range(256):
    _c = _i << 24
    for _ in range(8):
        _c = ((_c << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if _c & 0x80000000 else (_c << 1) & 0xFFFFFFFF
    _TABLE.append(_c)


def crc32(name: str) -> int:
    crc = 0
    for byte in name.lower().encode("utf-8"):
        crc = _TABLE[((crc >> 24) ^ byte) & 0xFF] ^ ((crc << 8) & 0xFFFFFFFF)
    return crc & 0xFFFFFFFF


def index_pack(path):
    """hash -> (offset, size, compressedSize, fourCC) for one .pc, plus its bytes."""
    with open(path, "rb") as handle:
        blob = handle.read()

    if len(blob) < 400 or struct.unpack_from("<i", blob, 52)[0] not in (170, 173):
        return {}, blob

    count = struct.unpack_from("<i", blob, 12)[0]
    data_offset = struct.unpack_from("<i", blob, 368)[0]

    entries = {}
    pos = 384
    read_offset = 384 + data_offset

    for _ in range(count):
        if pos + 48 > len(blob):
            break

        name_hash, size, compressed_size, _name_offset, four_cc = struct.unpack_from("<IiiiI", blob, pos + 4)

        entries[name_hash] = (read_offset, size, compressed_size, struct.pack("<I", four_cc).decode("latin1"))

        pos += 48
        read_offset += compressed_size

        padding = compressed_size % 16
        if padding:
            read_offset += 16 - padding

    return entries, blob


def dds(width, height, four_cc, payload):
    """Wrap raw DXT blocks in a DDS header so Pillow can decode them."""
    return (
        b"DDS " + struct.pack("<IIIIIII", 124, 0x1007, height, width, len(payload), 0, 0)
        + b"\0" * 44 + struct.pack("<II", 32, 4) + four_cc + b"\0" * 20
        + struct.pack("<I", 0x1000) + b"\0" * 16 + payload
    )


def decode_texture(blob):
    width, height, _depth, flags = struct.unpack_from("<HHHH", blob, 84)
    fmt = blob[92]

    if flags & 2:  # cube map
        return None

    pixels = blob[124:]

    if fmt == 1:
        return Image.frombytes("RGBA", (width, height), pixels[: width * height * 4], "raw", "BGRA")

    four_cc = {18: b"DXT1", 20: b"DXT3", 22: b"DXT5"}.get(fmt)

    if four_cc is None:
        return None

    # Only the top mip level is needed; DXT1 is 8 bytes per 4x4 block, DXT3/5 are 16.
    blocks = ((width + 3) // 4) * ((height + 3) // 4)
    needed = blocks * (8 if four_cc == b"DXT1" else 16)

    return Image.open(io.BytesIO(dds(width, height, four_cc, pixels[:needed]))).convert("RGBA")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--data", default=DEFAULT_DATA, help="client Data/ directory with the .pc archives")
    parser.add_argument("--geodata", default=DEFAULT_GEO, help="geodata.json to read the item list from")
    parser.add_argument("--out", default=DEFAULT_OUT, help="directory to write PNGs into")
    parser.add_argument("--size", type=int, default=0, help="resize output to NxN (0 keeps native size)")
    args = parser.parse_args()

    packs = sorted(glob.glob(os.path.join(args.data, "*.pc")))

    if not packs:
        sys.exit(f"no .pc archives under {args.data}")

    with open(args.geodata, encoding="utf-8-sig") as handle:
        geo = json.load(handle)

    resources = next(value for key, value in geo.items() if key.lower() == "resources")
    items = [entry["Name"] for entry in resources if entry.get("IsInventoryItem")]

    # What we want, keyed by the output file name.
    wanted = {name: f"t_icons_{name}" for name in items}
    wanted.update({name: name for name in CHROME})

    by_hash = {crc32(resource): out_name for out_name, resource in wanted.items()}

    os.makedirs(args.out, exist_ok=True)

    written = 0

    for pack in packs:
        entries, blob = index_pack(pack)

        for name_hash, (offset, size, compressed_size, four_cc) in entries.items():
            out_name = by_hash.get(name_hash)

            if out_name is None or four_cc != "TEXR":
                continue

            raw = blob[offset:offset + compressed_size]

            try:
                payload = zlib.decompress(raw) if size != compressed_size else raw
                image = decode_texture(payload)
            except Exception as error:  # a single bad texture must not stop the dump
                print(f"  {out_name}: decode failed ({error})")
                continue

            if image is None:
                continue

            if args.size:
                image = image.resize((args.size, args.size), Image.LANCZOS)

            image.save(os.path.join(args.out, f"{out_name}.png"))
            written += 1

    print(f"wrote {written} icons to {args.out} ({len(items)} inventory items, the rest have no 2D icon)")


if __name__ == "__main__":
    main()

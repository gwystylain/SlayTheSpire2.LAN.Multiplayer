"""Read and write Godot 4 .pck files (pack format 3, unencrypted).

There is no Godot project in this repo: the pck was exported by the upstream author, so
releases rebuild it by script instead. tools/package.py imports this module; the CLI is
for inspecting packs by hand.

  python tools/pck_tool.py list <in.pck>
  python tools/pck_tool.py roundtrip <in.pck>
      rebuild the pack from its own entries and check the bytes are identical
  python tools/pck_tool.py replace <in.pck> <out.pck> <prefix> [<dir>]
      copy <in.pck> without the entries under <prefix>, adding the files in <dir> there.
      tools/pck_base.pck is the 1.6.3 pck put through this with no <dir>, prefix
      "SlayTheSpire2.LAN.Multiplayer/localization".

Layout: "GDPC", u32 format 3, u32 major/minor/patch, u32 flags (2 = PACK_REL_FILEBASE),
u64 file_base, u64 dir_offset, 64 reserved bytes, padded to 16. File data follows, each entry
padded to 16 with offsets relative to file_base. The directory at dir_offset is u32 count, then
per entry: u32 path length (padded to 4), path bytes (no res:// prefix), u64 offset, u64 size,
the data's md5, u32 flags 0. Godot sorts the directory by path.
"""
import hashlib
import os
import struct
import sys

ALIGN = 16


def pad(n, align):
    return (align - n % align) % align


def read_pck(path):
    """Return (header, entries, data_order, raw bytes); entries are (path, content) in directory order."""
    with open(path, "rb") as f:
        data = f.read()
    magic, fmt, major, minor, patch, flags = struct.unpack_from("<4s5I", data, 0)
    if magic != b"GDPC" or fmt != 3:
        raise ValueError(f"{path}: not a pack format 3 pck ({magic!r}, {fmt})")
    if flags & 1:
        raise ValueError(f"{path}: encrypted directory not supported")
    file_base, dir_offset = struct.unpack_from("<QQ", data, 24)
    (count,) = struct.unpack_from("<I", data, dir_offset)
    pos = dir_offset + 4
    entries = []
    for _ in range(count):
        (plen,) = struct.unpack_from("<I", data, pos)
        pos += 4
        name = data[pos:pos + plen].rstrip(b"\0").decode("utf-8")
        pos += plen
        ofs, size = struct.unpack_from("<QQ", data, pos)
        pos += 16
        md5 = data[pos:pos + 16]
        pos += 16
        (eflags,) = struct.unpack_from("<I", data, pos)
        pos += 4
        content = data[file_base + ofs:file_base + ofs + size]
        if hashlib.md5(content).digest() != md5:
            raise ValueError(f"{path}: md5 mismatch for {name}")
        if eflags != 0:
            raise ValueError(f"{path}: unsupported entry flags {eflags} for {name}")
        entries.append((name, content, ofs))
    data_order = [n for n, _, _ in sorted(entries, key=lambda e: e[2])]
    return (major, minor, patch, flags), [(n, c) for n, c, _ in entries], data_order, data


def write_pck(header, entries, data_order=None):
    """entries go into the directory in the order given; file data in data_order (default: the same)."""
    major, minor, patch, flags = header
    out = bytearray()
    out += struct.pack("<4s5I", b"GDPC", 3, major, minor, patch, flags)
    out += struct.pack("<QQ", 0, 0)
    out += b"\0" * 64
    out += b"\0" * pad(len(out), ALIGN)
    file_base = len(out)

    contents = dict(entries)
    offsets = {}
    for name in data_order or [n for n, _ in entries]:
        offsets[name] = len(out) - file_base
        out += contents[name]
        out += b"\0" * pad(len(out), ALIGN)

    dir_offset = len(out)
    out += struct.pack("<I", len(entries))
    for name, content in entries:
        raw = name.encode("utf-8")
        p = pad(len(raw), 4)
        out += struct.pack("<I", len(raw) + p) + raw + b"\0" * p
        out += struct.pack("<QQ", offsets[name], len(content))
        out += hashlib.md5(content).digest()
        out += struct.pack("<I", 0)

    struct.pack_into("<QQ", out, 24, file_base, dir_offset)
    return bytes(out)


def replace_prefix(entries, prefix, added):
    """Drop every entry under prefix, add (path, content) pairs, and sort by path as Godot does."""
    prefix = prefix.rstrip("/") + "/"
    kept = [(n, c) for n, c in entries if not n.startswith(prefix)]
    for name, _ in added:
        if not name.startswith(prefix):
            raise ValueError(f"{name} is not under {prefix}")
    return sorted(kept + list(added), key=lambda e: e[0])


def files_under(src_dir, prefix):
    """(prefix/relative path, bytes) for every file below src_dir."""
    prefix = prefix.rstrip("/") + "/"
    added = []
    for root, _, files in os.walk(src_dir):
        for fn in files:
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, src_dir).replace(os.sep, "/")
            with open(full, "rb") as fh:
                added.append((prefix + rel, fh.read()))
    return added


def main():
    cmd = sys.argv[1]
    header, entries, data_order, original = read_pck(sys.argv[2])
    if cmd == "list":
        print(f"Godot {header[0]}.{header[1]}.{header[2]}, flags {header[3]}, {len(entries)} entries")
        for name, content in entries:
            print(f"{len(content):8d}  {name}")
    elif cmd == "roundtrip":
        rebuilt = write_pck(header, entries, data_order)
        same = rebuilt == original
        print(f"{len(entries)} entries, {len(original)} bytes, rebuilt identical: {same}")
        if not same:
            sys.exit(1)
    elif cmd == "replace":
        out_path, prefix = sys.argv[3], sys.argv[4]
        added = files_under(sys.argv[5], prefix) if len(sys.argv) > 5 else []
        merged = replace_prefix(entries, prefix, added)
        with open(out_path, "wb") as fh:
            fh.write(write_pck(header, merged))
        if read_pck(out_path)[1] != merged:
            sys.exit(f"{out_path} did not read back as written")
        print(f"{len(merged) - len(added)} kept, {len(added)} added, wrote {out_path}")
    else:
        sys.exit(f"unknown command {cmd}")


if __name__ == "__main__":
    main()

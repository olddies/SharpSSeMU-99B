#!/usr/bin/env python3
"""Parse the SSeMU 0.99B (2.1.7) packet structs into a language-neutral IR.

Single source of truth for the wire format shared by the C++ client and the
C# server (SharpSSeMU). Both sides are generated from this IR rather than
transcribed by hand -- hand transcription is what produced the six layout
bugs documented in SharpSSeMU/docs/development-log.es.md (CharSet 13-vs-18, ItemInfo 5-vs-12,
PartyLife 1-byte-per-member, Teleport.gate BYTE-vs-WORD, field order in
PMSG_VIEWPORT_PLAYER, MAX_DS_LEVEL 4-vs-7).

Input is the *authoritative* server tree:

    Source/Source/Emulator 0.99 (2.1.7)/GameServer/*.h

Do NOT point this at Source/Source/Emulator/ (no version suffix). That tree is
a much later season and has no relation to this project; it is the trap that
cost a full porting pass. The discriminator is stdafx.h:

    #define GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]"

Layout rules implemented here mirror MSVC x86, because that is what built the
binaries this protocol has to interoperate with:

  * Natural alignment: a member of size N starts at a multiple of
    min(N, pack), where `pack` is the active #pragma pack value (default 8).
  * Arrays align to their element type, not their total size.
  * The struct's own alignment is the max of its members' alignments, and
    sizeof() is rounded up to that -- so trailing padding is real and counts
    on the wire.

That last group is not pedantry. PMSG_LIFE_SEND reaches offset 7 and then
declares a DWORD, so MSVC inserts a pad byte at offset 7; reading the packet
without it yields the garbage HP/mana values the port kept hitting.

Usage:
    python parse_protocol.py --gameserver-dir <path> --out protocol.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# Scalar type -> (size, alignment) under MSVC x86.
SCALARS: dict[str, tuple[int, int]] = {
    "BYTE": (1, 1),
    "char": (1, 1),
    "bool": (1, 1),
    "WORD": (2, 2),
    "short": (2, 2),
    "USHORT": (2, 2),
    "DWORD": (4, 4),
    "int": (4, 4),
    "UINT": (4, 4),
    "long": (4, 4),
    "float": (4, 4),
    "QWORD": (8, 8),
    "double": (8, 8),
    "__int64": (8, 8),
}

DEFAULT_PACK = 8

# Macros the headers gate struct members on. GAMESERVER_EXTRA defaults to 1 in
# stdafx.h, so the shipped server has the extra members present.
DEFAULT_DEFINES: dict[str, int] = {"GAMESERVER_EXTRA": 1}

# Win32 SDK constants used as array bounds. Only needed by non-packet structs
# (file paths and the like), but they have to resolve for the pass to complete.
PLATFORM_CONSTANTS: dict[str, int] = {"MAX_PATH": 260}

_COMMENT_BLOCK = re.compile(r"/\*.*?\*/", re.S)
_STRUCT = re.compile(r"^struct\s+(\w+)\s*$\s*^\{(.*?)^\};", re.S | re.M)
_FIELD = re.compile(
    r"^\s*(?:struct\s+)?([A-Za-z_]\w*)\s+([A-Za-z_]\w*)\s*((?:\[\s*\w+\s*\])*)\s*;"
)
_ARRAY_DIM = re.compile(r"\[\s*(\w+)\s*\]")
_PRAGMA_PACK = re.compile(r"^\s*#\s*pragma\s+pack\s*\(\s*(\d*)\s*\)")
_PP_IF = re.compile(r"^\s*#\s*if\s*\(?\s*(\w+)\s*==\s*(\d+)\s*\)?")
_PP_IFDEF = re.compile(r"^\s*#\s*ifdef\s+(\w+)")
_PP_IFNDEF = re.compile(r"^\s*#\s*ifndef\s+(\w+)")
_PP_ELSE = re.compile(r"^\s*#\s*else")
_PP_ENDIF = re.compile(r"^\s*#\s*endif")


class ParseError(Exception):
    """Raised when a header cannot be interpreted; never guess a layout."""


def strip_comments(text: str) -> str:
    """Remove block and line comments, keeping line structure intact."""
    text = _COMMENT_BLOCK.sub("", text)
    return "\n".join(line.split("//")[0] for line in text.split("\n"))


def find_constants(texts: list[str]) -> dict[str, int]:
    """Collect `#define NAME <int>` and `const int NAME = <int>` array bounds."""
    consts: dict[str, int] = {}
    for text in texts:
        for m in re.finditer(r"^\s*#\s*define\s+(\w+)\s+\(?\s*(\d+)\s*\)?\s*$", text, re.M):
            consts.setdefault(m.group(1), int(m.group(2)))
        for m in re.finditer(r"\bconst\s+int\s+(\w+)\s*=\s*(\d+)\s*;", text):
            consts.setdefault(m.group(1), int(m.group(2)))
    return consts


def active_lines(body: str, defines: dict[str, int]) -> list[str]:
    """Evaluate the preprocessor conditionals inside a struct body.

    Only the forms these headers actually use are supported -- #if(X==N),
    #ifdef, #ifndef, #else, #endif. Anything else raises rather than guessing,
    because a silently-dropped member is a silently-wrong wire layout.
    """
    out: list[str] = []
    stack: list[bool] = []
    for line in body.split("\n"):
        if m := _PP_IF.match(line):
            stack.append(defines.get(m.group(1), 0) == int(m.group(2)))
            continue
        if m := _PP_IFDEF.match(line):
            stack.append(m.group(1) in defines)
            continue
        if m := _PP_IFNDEF.match(line):
            stack.append(m.group(1) not in defines)
            continue
        if _PP_ELSE.match(line):
            if not stack:
                raise ParseError("#else without #if")
            stack[-1] = not stack[-1]
            continue
        if _PP_ENDIF.match(line):
            if not stack:
                raise ParseError("#endif without #if")
            stack.pop()
            continue
        if _PRAGMA_PACK.match(line):
            # Packing can change mid-struct; the value in effect at each member
            # declaration is the one MSVC applies to it. Kept as a marker so the
            # field loop can track it positionally.
            if all(stack):
                out.append(line)
            continue
        if line.lstrip().startswith("#"):
            raise ParseError(f"unsupported preprocessor directive: {line.strip()!r}")
        if all(stack):
            out.append(line)
    if stack:
        raise ParseError("unterminated #if")
    return out


def parse_pack_regions(text: str) -> list[tuple[int, int]]:
    """Return (line_index, pack_value) transitions for `#pragma pack(...)`.

    `#pragma pack()` with no argument restores the default (8), which is how
    these headers close a packed region.
    """
    regions: list[tuple[int, int]] = []
    for i, line in enumerate(text.split("\n")):
        if m := _PRAGMA_PACK.match(line):
            arg = m.group(1)
            regions.append((i, int(arg) if arg else DEFAULT_PACK))
    return regions


def pack_at_line(regions: list[tuple[int, int]], line_index: int) -> int:
    """The active #pragma pack value at a given line."""
    pack = DEFAULT_PACK
    for at, value in regions:
        if at <= line_index:
            pack = value
        else:
            break
    return pack


def layout(
    fields: list[tuple[str, str, list[int], int]],
    known: dict[str, dict],
) -> tuple[list[dict], int, int]:
    """Compute MSVC member offsets, total size, and struct alignment.

    Each field carries the ``#pragma pack`` value that was in effect where it
    was declared -- these headers change packing mid-struct (BloodCastle.h
    wraps the members of PMSG_BLOOD_CASTLE_SCORE_SEND in pack(1) from inside
    the braces), and MSVC honours the value at each member's declaration.

    Returns (members, size, alignment). Members include synthesised
    ``__pad`` entries so the wire layout is explicit rather than implied.
    """
    members: list[dict] = []
    offset = 0
    struct_align = 1

    for type_name, name, dims, pack in fields:
        if type_name in SCALARS:
            elem_size, elem_align = SCALARS[type_name]
            kind = "scalar"
        elif type_name in known:
            nested = known[type_name]
            elem_size, elem_align = nested["size"], nested["align"]
            kind = "struct"
        else:
            raise ParseError(f"unknown type {type_name!r} for member {name!r}")

        count = 1
        for d in dims:
            count *= d
        total = elem_size * count

        align = min(elem_align, pack)
        struct_align = max(struct_align, align)

        if offset % align:
            pad = align - (offset % align)
            members.append({"name": "__pad", "type": "BYTE", "count": pad,
                            "offset": offset, "size": pad, "padding": True})
            offset += pad

        members.append({
            "name": name,
            "type": type_name,
            "kind": kind,
            "count": count,
            "dims": dims,
            "offset": offset,
            "size": total,
            "elem_size": elem_size,
            "pack": pack,
        })
        offset += total

    size = offset
    if size % struct_align:
        pad = struct_align - (size % struct_align)
        members.append({"name": "__pad_tail", "type": "BYTE", "count": pad,
                        "offset": size, "size": pad, "padding": True})
        size += pad

    return members, size, struct_align


def parse_headers(
    dirs: list[Path], defines: dict[str, int]
) -> tuple[dict[str, dict], list[str]]:
    """Parse the wire structs of one or more server projects into the IR.

    Several projects contribute packets: the GameServer defines the bulk, but
    the ConnectServer owns the 0xF4 family (server list / server info) and the
    SSeMU-specific name list, so both have to be parsed to describe what the
    client actually talks.

    Structs are resolved iteratively so nested types (PBMSG_HEAD and friends)
    are laid out before the packets that embed them. Returns the resolved
    structs plus the names of the server-internal ones that were skipped.
    """
    sources: list[Path] = []
    for d in dirs:
        found = sorted(d.glob("*.h"))
        if not found:
            raise ParseError(f"no headers found in {d}")
        sources.extend(found)

    raw = {p: strip_comments(p.read_text(encoding="utf-8", errors="replace")) for p in sources}
    consts = dict(PLATFORM_CONSTANTS)
    consts.update(find_constants(list(raw.values())))
    consts.update({k: v for k, v in defines.items()})

    pending: list[dict] = []
    for path, text in raw.items():
        regions = parse_pack_regions(text)
        for m in _STRUCT.finditer(text):
            name, body = m.group(1), m.group(2)
            line_index = text[: m.start()].count("\n")
            try:
                lines = active_lines(body, defines)
            except ParseError as exc:
                raise ParseError(f"{path.name}: {name}: {exc}") from exc

            fields: list[tuple[str, str, list[int], int]] = []
            cur_pack = pack_at_line(regions, line_index)
            for line in lines:
                if pm := _PRAGMA_PACK.match(line):
                    cur_pack = int(pm.group(1)) if pm.group(1) else DEFAULT_PACK
                    continue
                if "(" in line or "this->" in line or "return" in line:
                    continue  # member function, not a data member
                fm = _FIELD.match(line)
                if not fm:
                    continue
                type_name, field_name, dim_text = fm.group(1), fm.group(2), fm.group(3)
                if type_name in ("void", "return", "public", "private", "struct"):
                    continue
                dims: list[int] = []
                for d in _ARRAY_DIM.findall(dim_text):
                    if d.isdigit():
                        dims.append(int(d))
                    elif d in consts:
                        dims.append(consts[d])
                    else:
                        raise ParseError(
                            f"{path.name}: {name}.{field_name}: unresolved array bound {d!r}")
                fields.append((type_name, field_name, dims, cur_pack))

            pending.append({
                "name": name,
                "file": path.name,
                "project": path.parent.name,
                "pack": pack_at_line(regions, line_index),
                "fields": fields,
            })

    # Only structs reachable from a PMSG_* root are part of the wire protocol.
    # The headers also declare plenty of server-internal structs built on Win32
    # and engine types (SOCKET, HANDLE, CRITICAL_SECTION, CShop...) that have no
    # layout we could or should compute -- those are skipped, not guessed at.
    by_name = {e["name"]: e for e in pending}
    required: set[str] = set()
    frontier = [n for n in by_name if n.startswith("PMSG_")]
    while frontier:
        name = frontier.pop()
        if name in required or name not in by_name:
            continue
        required.add(name)
        frontier.extend(t for t, _, _, _ in by_name[name]["fields"] if t not in SCALARS)

    known: dict[str, dict] = {}
    remaining = [e for e in pending if e["name"] in required]
    skipped = sorted(n for n in by_name if n not in required)
    while remaining:
        progressed = False
        deferred = []
        for entry in remaining:
            types = {t for t, _, _, _ in entry["fields"]}
            unresolved = types - set(SCALARS) - set(known)
            if unresolved:
                deferred.append(entry)
                continue
            members, size, align = layout(entry["fields"], known)
            known[entry["name"]] = {
                "name": entry["name"],
                "file": entry["file"],
                "pack": entry["pack"],
                "size": size,
                "align": align,
                "members": members,
            }
            progressed = True
        if not progressed:
            names = ", ".join(sorted(e["name"] for e in deferred))
            missing = sorted({t for e in deferred for t, _, _, _ in e["fields"]}
                             - set(SCALARS) - set(known))
            raise ParseError(
                f"cannot resolve wire struct(s) {names} (missing types: {missing})")
        remaining = deferred

    return known, skipped


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source-dir", required=True, type=Path, action="append",
                    dest="source_dirs", metavar="DIR",
                    help="directorio de un proyecto del emulador (repetible): "
                         "GameServer, ConnectServer, ...")
    ap.add_argument("--out", required=True, type=Path, help="output IR (JSON)")
    ap.add_argument("--define", action="append", default=[], metavar="NAME=VALUE",
                    help="override a preprocessor define (default GAMESERVER_EXTRA=1)")
    args = ap.parse_args()

    defines = dict(DEFAULT_DEFINES)
    for item in args.define:
        key, _, value = item.partition("=")
        defines[key] = int(value or 1)

    # El marcador de versión vive en el stdafx.h del GameServer; alcanza con que
    # UNO de los directorios lo tenga para confirmar que es el árbol correcto.
    markers = [d / "stdafx.h" for d in args.source_dirs]
    seen = [m for m in markers if m.exists()]
    if seen and not any("0.99B CHS" in m.read_text(encoding="utf-8", errors="replace")
                        for m in seen):
        print("error: ninguno de los directorios indicados es el árbol 0.99B (2.1.7) -- "
              "ningún stdafx.h tiene el marcador '0.99B CHS'. No se adivina.",
              file=sys.stderr)
        return 2

    try:
        structs, skipped = parse_headers(args.source_dirs, defines)
    except ParseError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1

    packets = {k: v for k, v in structs.items() if k.startswith("PMSG_")}
    args.out.write_text(
        json.dumps({"defines": defines, "structs": structs}, indent=1),
        encoding="utf-8")

    print(f"parsed {len(structs)} wire structs ({len(packets)} PMSG_*) -> {args.out}")
    print(f"skipped {len(skipped)} server-internal structs (not on the wire)")
    padded = [v for v in packets.values() if any(m.get("padding") for m in v["members"])]
    print(f"{len(padded)} packet structs carry compiler padding on the wire:")
    for v in sorted(padded, key=lambda s: s["name"]):
        pads = [f"{m['offset']}(+{m['size']})" for m in v["members"] if m.get("padding")]
        print(f"  {v['name']:<44} size={v['size']:<4} pad@ {', '.join(pads)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

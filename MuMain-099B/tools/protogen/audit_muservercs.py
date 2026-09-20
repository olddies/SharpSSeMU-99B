#!/usr/bin/env python3
"""Cross-check SharpSSeMU's hand-written packet builders against the IR.

For every PMSG_ struct that carries compiler padding on the wire, locate the C#
builder that SharpSSeMU uses for it and count the bytes that builder actually
emits. A count that falls short of the struct's real sizeof() means the padding
was dropped -- the same defect class as the character-list bug, and one the
project's own tests cannot catch, because WorldTestClient parses with the same
layout the builder writes.

This is a triage tool, not an oracle: it points at the builders a human has to
read. Loop bodies are reported per iteration, and anything it cannot attribute
is listed as unmatched rather than silently passed.

Usage:
    python audit_muservercs.py --ir protocol_099b.json --muservercs <path>
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

# C# write call -> bytes emitted. Width-bearing calls take their count from a
# literal argument; the rest are fixed width.
FIXED_WIDTH = {
    "WriteByte": 1,
    "WriteUInt16": 2,
    "WriteUInt32": 4,
}
SIZED = ("WriteFixedString", "WriteBytes")

_CALL = re.compile(r"\.(\w+)\s*\(([^;]*)\)\s*;")
_TRAILING_INT = re.compile(r",\s*(\d+)\s*\)?\s*$")
_FIXEDSTR = re.compile(r"FixedString\s*\([^,]+,\s*(\d+)\s*\)")
_WRITE_RANGE = re.compile(r"\.Write\s*\(\s*[\w.]+\s*,\s*\d+\s*,\s*(\d+)\s*\)")


def split_loop(body: str) -> tuple[str, str]:
    """Split a builder into (prefix, loop body).

    Builders that emit a repeated struct write a fixed prefix first (result,
    count) and then one struct per iteration. Comparing the whole method
    against a single struct's sizeof would count that prefix as drift, so the
    two parts are measured separately.
    """
    m = re.search(r"\b(?:foreach|for)\s*\(", body)
    if not m:
        return body, ""
    start = body.find("{", m.end())
    if start < 0:
        return body, ""
    depth, i = 0, start
    while i < len(body):
        if body[i] == "{":
            depth += 1
        elif body[i] == "}":
            depth -= 1
            if depth == 0:
                break
        i += 1
    return body[:m.start()], body[start:i]


def count_bytes(body: str, consts: dict[str, int] | None = None) -> tuple[int, list[str]]:
    """Sum the bytes a builder body writes. Returns (total, unresolved calls)."""
    consts = consts or {}
    total = 0
    unresolved: list[str] = []

    def width(arg: str) -> int | None:
        arg = arg.strip()
        if arg.isdigit():
            return int(arg)
        return consts.get(arg.rsplit(".", 1)[-1])

    for line in body.split("\n"):
        stripped = line.strip()
        if not stripped or stripped.startswith("//"):
            continue

        if m := _WRITE_RANGE.search(stripped):
            total += int(m.group(1))
            continue
        if m := _FIXEDSTR.search(stripped):
            total += int(m.group(1))
            continue

        for m in _CALL.finditer(stripped):
            name, args = m.group(1), m.group(2)
            if name in FIXED_WIDTH:
                total += FIXED_WIDTH[name]
            elif name in SIZED or name in ("Write", "WriteZeros", "Skip"):
                # Width is the last argument -- a literal, or a named constant
                # such as Item.WireByteSize that we resolve from the sources.
                last = args.rsplit(",", 1)[-1] if "," in args else args
                if (w := width(last)) is not None:
                    total += w
                else:
                    unresolved.append(stripped)
    return total, unresolved


def collect_constants(root: Path) -> dict[str, int]:
    """Harvest `const int NAME = <int>` from the C# sources for width lookups."""
    consts: dict[str, int] = {}
    for path in root.rglob("*.cs"):
        if "/obj/" in path.as_posix() or "/bin/" in path.as_posix():
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for m in re.finditer(r"\bconst\s+(?:int|byte)\s+(\w+)\s*=\s*(\d+)\s*;", text):
            consts.setdefault(m.group(1), int(m.group(2)))
    return consts


def extract_methods(text: str) -> dict[str, tuple[str, str, int]]:
    """Map method name -> (preceding doc-comment, body, line number)."""
    methods: dict[str, tuple[str, str, int]] = {}
    for m in re.finditer(
            r"((?:^\s*///[^\n]*\n)*)^\s*public static byte\[\]\s+(\w+)\s*\(", text, re.M):
        doc, name = m.group(1), m.group(2)
        # Body runs from the opening brace to the matching close.
        start = text.find("{", m.end())
        if start < 0:
            continue
        depth, i = 0, start
        while i < len(text):
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        methods[name] = (doc, text[start:i], text[:m.start()].count("\n") + 1)
    return methods


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ir", required=True, type=Path)
    ap.add_argument("--muservercs", required=True, type=Path,
                    help="SharpSSeMU/src/MuServer.GameServer/Protocol")
    args = ap.parse_args()

    ir = json.loads(args.ir.read_text(encoding="utf-8"))
    structs = ir["structs"]

    methods: dict[str, tuple[str, str, int, str]] = {}
    for path in sorted(args.muservercs.glob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        for name, (doc, body, line) in extract_methods(text).items():
            methods[name] = (doc, body, line, path.name)

    consts = collect_constants(args.muservercs.parents[2])

    padded = {n: s for n, s in structs.items()
              if n.startswith("PMSG_") and any(m.get("padding") for m in s["members"])}

    print(f"{len(padded)} PMSG_ structs carry wire padding.\n")
    flagged, ok, manual, unmatched = [], [], [], []

    for name, s in sorted(padded.items()):
        # A builder claims a struct by naming it in its doc-comment. The
        # convention here is "<summary>PMSG_X, C1:YY -- ...", so a leading
        # mention identifies the owner; mentions further in the prose are just
        # cross-references (PartyLifeSend explains itself by pointing at
        # PMSG_PARTY_LIST) and must not be mistaken for ownership.
        # Note the name may be a prefix of a sibling (PMSG_PARTY_LIST vs
        # PMSG_PARTY_LIST_SEND, the row vs its envelope), so require the name to
        # end the token -- '_' is a word character, so \b alone would not do it.
        lead = re.compile(r"<summary>\s*(?:Un renglón de\s+)?"
                          + re.escape(name) + r"(?![A-Za-z0-9_])")
        owners = [(mn, d, b, ln, f) for mn, (d, b, ln, f) in methods.items()
                  if lead.search(d)]
        ambiguous = False
        if not owners:
            # No builder announces this struct as its subject -- it is emitted
            # inside one whose comment names the envelope. Attribution is a
            # guess from here, so its results are reported as needing eyes.
            ambiguous = True
            owners = [(mn, d, b, ln, f) for mn, (d, b, ln, f) in methods.items()
                      if name in d]
        if not owners:
            unmatched.append(name)
            continue
        for mn, _doc, body, line, fname in owners:
            prefix, loop_body = split_loop(body)
            pads = sum(m["size"] for m in s["members"] if m.get("padding"))
            header = _header_size(s, structs)

            if loop_body:
                # One struct per iteration; the prefix is packet-level, not part
                # of the repeated element.
                written, unresolved = count_bytes(loop_body, consts)
                expected = s["size"]
            else:
                written, unresolved = count_bytes(body, consts)
                # PacketBuilder prepends the header, so the body is sizeof minus it.
                expected = s["size"] - header

            entry = (name, mn, fname, line, expected, written, pads,
                     bool(loop_body), unresolved)
            if written == expected and not unresolved:
                ok.append(entry)
            elif unresolved or ambiguous:
                manual.append(entry)
            else:
                flagged.append(entry)

    print("=== REVISAR: el builder no emite el sizeof real ===")
    for name, mn, fname, line, expected, written, pads, loop, unres in flagged:
        tag = " [por elemento del bucle]" if loop else ""
        print(f"  {name}")
        print(f"    {fname}:{line}  {mn}(){tag}")
        print(f"    esperado = {expected} bytes  (incluye {pads} de padding)")
        print(f"    escribe  = {written} bytes   -> faltan {expected - written}")
        print()
    if not flagged:
        print("  (ninguno)\n")

    if manual:
        print("=== verificar a mano: atribucion ambigua o ancho no resoluble ===")
        for name, mn, fname, line, expected, written, *_rest in manual:
            print(f"  {name:<38} {fname}:{line} {mn}()  (parcial: {written}/{expected})")
        print()

    print("=== consistente con el IR ===")
    for name, mn, fname, line, expected, written, *_ in ok:
        print(f"  {name:<40} {mn}() -> {written} == {expected}")

    print(f"\n=== sin builder identificable ({len(unmatched)}) ===")
    print("  " + ", ".join(unmatched))
    return 0


def _header_size(s: dict, structs: dict) -> int:
    """Bytes of the leading PBMSG/PSBMSG/PWMSG header, which PacketBuilder adds."""
    first = next((m for m in s["members"] if not m.get("padding")), None)
    if first and first.get("kind") == "struct" and first["type"].endswith("MSG_HEAD"):
        return first["size"]
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

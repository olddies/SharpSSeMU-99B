#!/usr/bin/env python3
"""Anota el IR con el opcode, la dirección y el transporte de cada paquete.

`parse_protocol.py` saca el *layout* de los headers; el opcode no está ahí, sino
en el código que arma y despacha los paquetes:

* **servidor -> cliente**: cada `GC*Send` declara un `PMSG_X pMsg;` y lo timbra
  con `pMsg.header.set(head[, sub], size)`. La variante `setE` marca el tipo
  cifrado (0xC3/0xC4) en vez del plano (0xC1/0xC2).
* **cliente -> servidor**: el `switch` de `ProtocolCore` (Protocol.cpp) mapea
  cada head al handler que castea el buffer a su `PMSG_*_RECV`.

Sin esto el cliente tendría el layout pero no sabría con qué head mandar ni a
qué struct parsear lo que llega, que es justamente donde se cometen los errores
al transcribir a mano.

Uso:
    python annotate_opcodes.py --ir protocol_099b.json \
        --source-dir "<...>/GameServer" --source-dir "<...>/ConnectServer"
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path

# pMsg.header.set(0xF3, 0x00, sizeof(pMsg))  /  .setE(0x1C, sizeof(pMsg))
_HEADER_SET = re.compile(
    r"(\w+)\s*(?:\[\d+\])?\s*\.header\.(setE?)\s*\(\s*"
    r"(0x[0-9A-Fa-f]+)\s*(?:,\s*(0x[0-9A-Fa-f]+)\s*)?,")
_COMMENT_BLOCK = re.compile(r"/\*.*?\*/", re.S)


def strip_comments(text: str) -> str:
    text = _COMMENT_BLOCK.sub("", text)
    return "\n".join(line.split("//")[0] for line in text.split("\n"))


def find_declaration(text: str, upto: int, var: str) -> str | None:
    """El tipo PMSG_ declarado para `var` más cerca hacia atrás de `upto`."""
    window = text[max(0, upto - 4000):upto]
    found = None
    for m in re.finditer(r"\b(PMSG_\w+)\s+" + re.escape(var) + r"\s*[;\[=]", window):
        found = m.group(1)
    return found


def annotate_server_to_client(dirs: list[Path]) -> dict[str, dict]:
    """Recorre los .cpp buscando dónde se timbra el header de cada paquete."""
    out: dict[str, dict] = {}
    for path in sorted(f for d in dirs for f in d.glob("*.cpp")):
        text = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
        for m in _HEADER_SET.finditer(text):
            var, kind, head, sub = m.group(1), m.group(2), m.group(3), m.group(4)
            struct = find_declaration(text, m.start(), var)
            if struct is None:
                continue
            entry = {
                "head": int(head, 16),
                "sub": int(sub, 16) if sub else None,
                "encrypted": kind == "setE",
                "direction": "server_to_client",
                "source": f"{path.name}",
            }
            prior = out.get(struct)
            if prior and (prior["head"], prior["sub"]) != (entry["head"], entry["sub"]):
                # The same struct stamped with two different opcodes: real (some are reused), so all of them are
                # kept instead of keeping only one.
                prior.setdefault("aliases", []).append(entry)
            else:
                out.setdefault(struct, entry)
    return out


def function_body(text: str, start: int) -> str:
    """El cuerpo `{...}` de la función que empieza en `start`."""
    depth, started = 0, False
    for i, ch in enumerate(text[start:], start):
        if ch == "{":
            depth += 1
            started = True
        elif ch == "}":
            depth -= 1
            if started and depth == 0:
                return text[start:i]
    return text[start:]


def annotate_client_to_server(dirs: list[Path]) -> dict[str, dict]:
    """Extrae los switch de despacho: head (y sub) -> PMSG_*_RECV.

    Cada proyecto tiene el suyo -- ``ProtocolCore`` en el GameServer,
    ``ConnectServerProtocolCore`` en el ConnectServer -- con la misma forma, así
    que se buscan todas las funciones cuyo nombre termina en ``ProtocolCore``.
    """
    out: dict[str, dict] = {}
    for path in sorted(f for d in dirs for f in d.glob("*.cpp")):
        text = strip_comments(path.read_text(encoding="utf-8", errors="replace"))
        for fm in re.finditer(r"\bvoid\s+(\w*ProtocolCore)\s*\(", text):
            out.update(scan_dispatch(function_body(text, fm.start()),
                                     f"{path.name} ({fm.group(1)})"))
    return out


def scan_dispatch(body: str, source: str) -> dict[str, dict]:
    """Recorre un switch de despacho ya recortado."""
    out: dict[str, dict] = {}
    head = sub = None
    for line in body.split("\n"):
        if (m := re.match(r"(\s*)case (0x[0-9A-Fa-f]{2}):", line)):
            indent = len(m.group(1).expandtabs(4)) // 4
            code = int(m.group(2), 16)
            if indent <= 2:
                head, sub = code, None
            else:
                sub = code
            continue
        if (m := re.search(r"\(\s*\(\s*(PMSG_\w+)\s*\*\s*\)", line)) and head is not None:
            out.setdefault(m.group(1), {
                "head": head,
                "sub": sub,
                "encrypted": None,  # decided by the client when sending
                "direction": "client_to_server",
                "source": source,
            })
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--ir", required=True, type=Path)
    ap.add_argument("--source-dir", required=True, type=Path, action="append",
                    dest="source_dirs", metavar="DIR",
                    help="directorio de un proyecto del emulador (repetible)")
    args = ap.parse_args()

    ir = json.loads(args.ir.read_text(encoding="utf-8"))
    structs = ir["structs"]

    s2c = annotate_server_to_client(args.source_dirs)
    c2s = annotate_client_to_server(args.source_dirs)

    annotated = 0
    for name, info in {**s2c, **c2s}.items():
        if name in structs:
            structs[name]["opcode"] = info
            annotated += 1

    ir["structs"] = structs
    args.ir.write_text(json.dumps(ir, indent=1), encoding="utf-8")

    packets = [n for n in structs if n.startswith("PMSG_")]
    unmapped = [n for n in packets if "opcode" not in structs[n]]
    print(f"anotados {annotated} structs con opcode "
          f"({len(s2c)} servidor->cliente, {len(c2s)} cliente->servidor)")
    print(f"{len(packets) - len(unmapped)}/{len(packets)} PMSG_ con opcode conocido")
    if unmapped:
        print(f"sin opcode ({len(unmapped)}): {', '.join(sorted(unmapped)[:12])}"
              + (" ..." if len(unmapped) > 12 else ""))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

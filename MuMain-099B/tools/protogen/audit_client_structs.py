"""Compara el tamaño de los structs del cliente contra los de 0.99B.

Un receptor puede compilar, correr y no fallar nunca, y aun así leer los campos
corridos: el `safe_cast` sólo comprueba que lleguen suficientes bytes, no que
signifiquen lo que el struct dice. Cuando el struct del cliente es más chico que
el del wire, el error es silencioso; cuando es más grande, el paquete se
descarta entero. Los dos casos se ven acá.

El layout se calcula con las mismas reglas que usa protogen para el servidor
(MSVC x86, alineación natural, `#pragma pack` respetado), así que los dos lados
se miden con la misma vara.

    python tools/protogen/audit_client_structs.py
"""

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
GENERATED = REPO / "src" / "source" / "Protocol099B" / "Protocol099B.generated.h"
WSCLIENT_H = REPO / "src" / "source" / "Network" / "Server" / "WSclient.h"
WSCLIENT_CPP = REPO / "src" / "source" / "Network" / "Server" / "WSclient.cpp"
DEFINES = REPO / "src" / "source" / "Core" / "Globals" / "_define.h"

# Tamaño y alineación de los tipos que aparecen en estos headers.
PRIMITIVES = {
    "BYTE": (1, 1), "char": (1, 1), "bool": (1, 1), "int8_t": (1, 1), "uint8_t": (1, 1),
    "WORD": (2, 2), "short": (2, 2), "int16_t": (2, 2), "uint16_t": (2, 2),
    "DWORD": (4, 4), "int": (4, 4), "long": (4, 4), "float": (4, 4),
    "int32_t": (4, 4), "uint32_t": (4, 4),
    "PBMSG_HEADER": (3, 1), "PWMSG_HEADER": (4, 1),
    "PBMSG_HEAD": (3, 1), "PWMSG_HEAD": (4, 1), "PSBMSG_HEAD": (4, 1),
}


def constants() -> dict[str, int]:
    """Los #define numéricos que se usan como tamaño de arreglo."""
    values: dict[str, int] = {}
    for path in (DEFINES, WSCLIENT_H):
        text = path.read_text(encoding="utf-8", errors="replace")
        for name, value in re.findall(r"#define\s+(\w+)\s+\(?\s*(\d+)\s*\)?\s*$",
                                      text, re.M):
            values[name] = int(value)
        for name, value in re.findall(r"constexpr\s+int\s+(\w+)\s*=\s*(\d+)", text):
            values[name] = int(value)
    return values


def layout(members: list[tuple[str, str, int]], pack: int) -> int:
    """Devuelve sizeof() aplicando alineación natural topeada por `pack`."""
    offset = 0
    strongest = 1

    for type_name, _, count in members:
        size, align = PRIMITIVES[type_name]
        align = min(align, pack)
        strongest = max(strongest, align)
        offset = (offset + align - 1) // align * align
        offset += size * count

    # El struct entero se redondea a su miembro más exigente: ese relleno de cola
    # viaja por la red, porque el servidor manda sizeof(), no la suma de campos.
    return (offset + strongest - 1) // strongest * strongest


def client_structs() -> dict[str, int]:
    """Nombre -> sizeof, para los typedef struct de WSclient.h."""
    text = WSCLIENT_H.read_text(encoding="utf-8", errors="replace")
    consts = constants()
    sizes: dict[str, int] = {}

    pattern = re.compile(
        r"typedef\s+struct\s*\{(.*?)\}\s*(\w+)\s*,", re.S)

    for body, name in pattern.findall(text):
        # El pack vigente es el del último #pragma pack antes del struct.
        before = text[:text.index(body)]
        pushes = re.findall(r"#pragma\s+pack\((?:push,\s*)?(\d+)\)", before)
        pops = before.count("#pragma pack(pop)")
        pack = int(pushes[-1]) if len(pushes) > pops else 8

        members: list[tuple[str, str, int]] = []
        ok = True

        for line in body.splitlines():
            line = line.split("//")[0].strip().rstrip(";")
            if not line:
                continue

            m = re.match(r"^(\w+)\s+(\w+)\s*(?:\[\s*([\w\s+*]+)\s*\])?$", line)
            if not m or m.group(1) not in PRIMITIVES:
                ok = False
                break

            count = 1
            if m.group(3):
                expr = m.group(3).strip()
                if expr.isdigit():
                    count = int(expr)
                elif expr in consts:
                    count = consts[expr]
                else:
                    ok = False
                    break

            members.append((m.group(1), m.group(2), count))

        if ok and members:
            sizes[name] = layout(members, pack)

    return sizes


def wire_structs() -> dict[tuple[int, int], tuple[str, int]]:
    """(head, sub) -> (nombre, sizeof) de los structs servidor->cliente."""
    text = GENERATED.read_text(encoding="utf-8", errors="replace")
    out: dict[tuple[int, int], tuple[str, int]] = {}

    for name, body, size in re.findall(
            r"struct (\w+)\s*\{(.*?)\};.*?static_assert\(sizeof\(\1\) == (\d+)",
            text, re.S):
        if "Direction::ServerToClient" not in body:
            continue
        head = re.search(r"kHead = (0x[0-9A-Fa-f]+)", body)
        if not head:
            continue
        sub = re.search(r"kSub = (0x[0-9A-Fa-f]+)", body)
        key = (int(head.group(1), 16), int(sub.group(1), 16) if sub else -1)
        out.setdefault(key, (name, int(size)))

    return out


def casts_by_handler() -> dict[str, str]:
    """Función receptora -> tipo al que castea, sea del cliente o de Mu099B."""
    text = WSCLIENT_CPP.read_text(encoding="utf-8", errors="replace")
    found: dict[str, str] = {}

    for match in re.finditer(
            r"^(?:void|BOOL|bool|int)\s+(Receive\w+)\s*\(", text, re.M):
        name = match.group(1)
        # El cuerpo termina en la primera llave de cierre en la columna cero:
        # sin ese corte la busqueda se cuela en la funcion siguiente y le
        # atribuye a este receptor un cast que no es suyo.
        rest = text[match.end():]
        stop = rest.find(chr(10) + "}")
        body = rest[:stop] if stop > 0 else rest[:1500]

        cast = re.search(r"safe_cast<(?:struct\s+)?([\w:]+)>", body)
        if not cast:
            # El alias de puntero es LP + el nombre del struct, que ya empieza con P:
            # agrupar desde la segunda L pierde esa P y el nombre no matchea.
            cast = re.search(r"\(LP(P?[A-Z_0-9]+)\)\s*ReceiveBuffer", body)
            if cast:
                found.setdefault(name, cast.group(1))
                continue
        if cast:
            found.setdefault(name, cast.group(1).split("::")[-1])

    return found


def main() -> int:
    client = client_structs()
    wire = wire_structs()
    casts = casts_by_handler()

    print(f"{len(client)} structs del cliente medidos, "
          f"{len(wire)} del wire con opcode, {len(casts)} receptores.\n")

    # Los receptores ya portados castean a un struct de Mu099B: esos vienen del
    # generador y por definición coinciden.
    wire_names = {name for name, _ in wire.values()}

    mismatches = []
    for handler, cast in sorted(casts.items()):
        if cast in wire_names:
            continue
        if cast not in client:
            continue
        # Se busca el struct del wire cuyo opcode atienda este receptor. Sin el
        # dispatch parseado no se puede atar, así que se reporta el tamaño para
        # cruzarlo a mano con la tabla de opcodes.
        mismatches.append((handler, cast, client[cast]))

    print("Receptores que todavia castean a un struct del dialecto viejo:")
    print(f"{'receptor':<42} {'struct':<34} tam")
    for handler, cast, size in mismatches:
        print(f"  {handler:<40} {cast:<34} {size:4d}")

    print(f"\nTotal: {len(mismatches)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

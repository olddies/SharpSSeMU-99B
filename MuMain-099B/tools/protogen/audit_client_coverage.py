"""Busca opcodes que el servidor manda y el cliente no atiende.

Es el fallo más silencioso de todos: el paquete llega bien formado, se descifra
bien, y no lo recoge nadie. No hay error, no hay log, la funcionalidad
simplemente no existe. Así estuvieron la experiencia por matar (0x9C) y las dos
de estadísticas del personaje (0xF3:E0 y 0xF3:E1).

    python tools/protogen/audit_client_coverage.py

Sale con código 1 si encuentra alguno, para poder engancharlo a CI.

Deliberadamente NO intenta atar cada opcode con su función receptora: el
dispatch del cliente son `switch` anidados con bastante ruido en el medio, y un
parser que se equivoca en la atribución es peor que no tenerlo -- reporta como
rotas cosas que andan y distrae de las que sí lo están. Para saber si un
receptor lee bien lo que le llega está `audit_client_structs.py`, que compara
tamaños.
"""

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SERVER = REPO.parent.parent / "SharpSSeMU" / "src" / "MuServer.GameServer"
WSCLIENT = REPO / "src" / "source" / "Network" / "Server" / "WSclient.cpp"

# Opcodes que el GameServer manda al DataServer, no al cliente: salen de los
# mismos builders pero no tienen por qué estar en el dispatch.
TO_DATA_SERVER = {0x70, 0x72}

# El dispatch usa estas constantes en vez del número. Buscar el literal no las
# encuentra, pero están atendidas.
BY_MACRO = {
    0xD7: "PACKET_MOVE",
    0xD0: "PACKET_POSITION",
    0xD9: "PACKET_ATTACK",
    0x19: "PACKET_MAGIC_ATTACK",
}


def server_opcodes() -> set[tuple[int, int | None]]:
    """Opcodes que el GameServer puede mandar, sacados de sus builders."""
    pattern = re.compile(
        r"PacketBuilder\.Build(?:C1|C2)(?:Sub|NoSub)?\("
        r"\s*0x([0-9A-Fa-f]+)\s*(?:,\s*0x([0-9A-Fa-f]+))?")

    found: set[tuple[int, int | None]] = set()
    for path in SERVER.rglob("*.cs"):
        text = path.read_text(encoding="utf-8", errors="replace")
        for head, sub in pattern.findall(text):
            found.add((int(head, 16), int(sub, 16) if sub else None))

    return found


def main() -> int:
    text = WSCLIENT.read_text(encoding="utf-8", errors="replace")
    dispatch = text[text.index("switch (HeadCode)"):]

    def atendido(value: int) -> bool:
        if re.search(rf"case\s+0x{value:02X}\s*:", dispatch, re.I):
            return True
        macro = BY_MACRO.get(value)
        return macro is not None and re.search(rf"case\s+{macro}\s*:", dispatch) is not None

    sent = server_opcodes()
    al_cliente = [op for op in sent if op[0] not in TO_DATA_SERVER]
    faltan: list[str] = []

    for head, sub in sorted(al_cliente, key=lambda k: (k[0], -1 if k[1] is None else k[1])):
        # Un sub-código se atiende en el switch anidado de su cabecera; alcanza
        # con que exista ese case, porque en este protocolo los sub-códigos no
        # se repiten entre ramas.
        if not atendido(sub if sub is not None else head):
            faltan.append(f"0x{head:02X}" + (f":0x{sub:02X}" if sub is not None else ""))

    print(f"El servidor puede mandarle al cliente {len(al_cliente)} opcodes distintos.")

    if faltan:
        print(f"\nSin atender ({len(faltan)}):")
        for op in faltan:
            print(f"  {op}")
        return 1

    print("Todos tienen un case en el dispatch.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

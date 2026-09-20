"""Compara -- y sincroniza -- la lista de warps del cliente con la del GameServer.

Esta es la unica de las tres tablas donde no coincidir **rompe el juego**, no
sólo el tooltip.

Cuando elegis un destino en la ventana de movimiento, el cliente manda el
`index` de su propia tabla (`MoveCommandData.cpp` -> `Mu099B::SendTeleportMove`).
El servidor busca ese numero en su `Data/Move/Move.txt`. Si los dos lados no
numeran igual, pedis un mapa y vas a otro.

Y no falla ruidosamente: `OnTeleportMoveAsync` hace

    var move = _moves.Get(recv.MoveIndex);
    int gateNumber = move?.GateNumber ?? recv.MoveIndex;

o sea que si el indice no existe **lo usa como numero de puerta** y te
teletransporta igual -- gratis, y sin mirar el nivel.

El cruce entre las dos tablas se hace por **numero de puerta**, no por indice ni
por nombre: la puerta es la misma en los dos lados y es unica. Los nombres
tambien coinciden, lo que confirma el cruce.

    python tools/gamedata/audit_move_table.py
    python tools/gamedata/audit_move_table.py --sincronizar

`--sincronizar` reescribe la tabla del cliente con la numeracion del servidor,
**conservando los nombres de cada idioma**. Eso ultimo no es cosmetico: la
ventana decide si podes viajar comparando el nombre del mapa contra
`I18N::Game::Icarus` y `I18N::Game::Atlans` para exigirte alas o montura. Pisar
los nombres en español con los del servidor romperia esos chequeos.

Formato: un `int` con la cantidad y despues esa cantidad de registros de 84
bytes -- `MOVEREQINFO_FILE`, con `#pragma pack(1)`, nombres en UTF-8 -- cada uno
cifrado con el XOR de tres bytes. No lleva checksum.
"""

import argparse
import re
import shutil
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LOCAL = REPO / "src" / "bin" / "Data" / "Local"
SERVIDOR = (REPO.parent / "SharpSSeMU" / "src" / "MuServer.GameServer"
            / "bin" / "Debug" / "net10.0" / "Data" / "Move" / "Move.txt")

BUX = bytes([0xFC, 0xCF, 0xAB])
REGISTRO = 84
LARGO_NOMBRE = 32
NIVEL_MAXIMO_POR_DEFECTO = 400


def bux(datos):
    salida = bytearray(datos)
    for i in range(len(salida)):
        salida[i] ^= BUX[i % 3]
    return bytes(salida)


def texto(crudo):
    """Solo para mostrar en el informe. Nunca para volver a escribir.

    Los archivos que vienen con el cliente no estan todos en el mismo juego de
    caracteres: el nombre de Icarus en español es `CD 63 61 72 6F`, o sea `Í` en
    Latin-1, aunque el cliente los lea con `ConvertFromUtf8`. Decodificar y
    volver a codificar convertiria ese byte en el caracter de reemplazo y
    dejaria "?caro" en pantalla. Por eso los nombres se copian **en bytes**, sin
    tocarlos: no hace falta saber en que codificacion estan para conservarlos.
    """
    fin = crudo.find(b"\x00")
    if fin >= 0:
        crudo = crudo[:fin]
    for juego in ("utf-8", "cp1252"):
        try:
            return crudo.decode(juego)
        except UnicodeDecodeError:
            continue
    return crudo.decode("utf-8", "replace")


def leer_cliente(path):
    datos = path.read_bytes()
    cantidad = struct.unpack_from("<i", datos, 0)[0]
    esperado = 4 + REGISTRO * cantidad
    if len(datos) < esperado:
        raise SystemExit("%s dice tener %d entradas pero mide %d bytes."
                         % (path.name, cantidad, len(datos)))

    filas = []
    for i in range(cantidad):
        r = bux(datos[4 + i * REGISTRO:4 + (i + 1) * REGISTRO])
        indice, = struct.unpack_from("<i", r, 0)
        nivel, maximo, zen, puerta = struct.unpack_from("<4i", r, 68)
        filas.append({
            "index": indice,
            # Los nombres se guardan crudos y se reescriben tal cual; `texto`
            # solo se usa para el informe. Ver el comentario de esa funcion.
            "nombre_bytes": bytes(r[4:36]),
            "sub_bytes": bytes(r[36:68]),
            "principal": texto(r[4:36]),
            "secundario": texto(r[36:68]),
            "nivel": nivel,
            "maximo": maximo,
            "zen": zen,
            "puerta": puerta,
        })
    return filas


def escribir_cliente(path, filas):
    salida = bytearray(struct.pack("<i", len(filas)))
    for fila in filas:
        registro = bytearray(REGISTRO)
        struct.pack_into("<i", registro, 0, fila["index"])
        for off, clave in ((4, "nombre_bytes"), (36, "sub_bytes")):
            crudo = fila[clave][:LARGO_NOMBRE]
            registro[off:off + len(crudo)] = crudo
        struct.pack_into("<4i", registro, 68, fila["nivel"], fila["maximo"],
                         fila["zen"], fila["puerta"])
        salida += bux(bytes(registro))
    path.write_bytes(bytes(salida))


def entero(valor, defecto=0):
    """Move.txt usa "*" para "sin limite"."""
    return defecto if valor == "*" else int(valor)


def leer_servidor(path):
    filas = []
    for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
        t = linea.strip()
        if not t or t.startswith("//") or t == "end":
            continue
        if not t.split()[0].isdigit():
            continue

        indice = int(t.split()[0])
        resto = t.split(None, 1)[1]
        comillas = re.search(r'"([^"]*)"', resto)
        nombre = comillas.group(1) if comillas else ""
        campos = re.sub(r'"[^"]*"', "", resto).split()
        # zen, nivel min, nivel max, reset min, reset max, AL0..AL3, puerta
        filas.append({
            "index": indice,
            "nombre": nombre,
            "zen": entero(campos[0]),
            "nivel": entero(campos[1]),
            "maximo": entero(campos[2], NIVEL_MAXIMO_POR_DEFECTO),
            "puerta": int(campos[-1]),
        })
    return filas


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sincronizar", action="store_true",
                        help="reescribe la tabla del cliente con la numeracion "
                             "del servidor")
    args = parser.parse_args()

    if not SERVIDOR.exists():
        print("No encuentro %s" % SERVIDOR)
        return 1

    servidor = leer_servidor(SERVIDOR)
    por_puerta_srv = {f["puerta"]: f for f in servidor}
    if len(por_puerta_srv) != len(servidor):
        print("El servidor repite numeros de puerta; el cruce no es fiable.")
        return 1

    archivos = sorted(LOCAL.glob("*/MoveReq_*.bmd"))
    archivos += [p for p in sorted(LOCAL.glob("*/movereq_*.bmd"))
                 if p not in archivos]
    if not archivos:
        print("No encuentro ningun MoveReq_<idioma>.bmd en %s" % LOCAL)
        return 1

    salida = 0
    for path in archivos:
        cliente = leer_cliente(path)
        por_puerta_cli = {f["puerta"]: f for f in cliente}

        print("== %s" % path.relative_to(REPO))
        print("   entradas: cliente %d, servidor %d" % (len(cliente), len(servidor)))

        mal_numeradas = [(f, por_puerta_cli[f["puerta"]]) for f in servidor
                         if f["puerta"] in por_puerta_cli
                         and por_puerta_cli[f["puerta"]]["index"] != f["index"]]
        sobran = [f for f in cliente if f["puerta"] not in por_puerta_srv]
        faltan = [f for f in servidor if f["puerta"] not in por_puerta_cli]

        otros = []
        for f in servidor:
            c = por_puerta_cli.get(f["puerta"])
            if not c:
                continue
            if c["zen"] != f["zen"]:
                otros.append((c["principal"], "zen", f["zen"], c["zen"]))
            if c["nivel"] != f["nivel"]:
                otros.append((c["principal"], "nivel", f["nivel"], c["nivel"]))

        if mal_numeradas:
            por_indice_srv = {f["index"]: f for f in servidor}
            print("   %d destinos con el indice equivocado: pedis uno y vas a otro."
                  % len(mal_numeradas))
            for _, c in mal_numeradas:
                destino = por_indice_srv.get(c["index"])
                print("     %-16s manda %-4d y el servidor entiende %s"
                      % (c["principal"], c["index"],
                         destino["nombre"] if destino
                         else "la puerta %d (sin control de nivel ni cobro)"
                              % c["index"]))
        if otros:
            print("   %d diferencias de precio o de nivel:" % len(otros))
            for nombre, campo, esperado, actual in otros:
                print("     %-16s %-6s servidor=%-8s cliente=%s"
                      % (nombre, campo, esperado, actual))
        if sobran:
            print("   %d destinos que el servidor no tiene (el menu los ofrece "
                  "y llevan a cualquier lado):" % len(sobran))
            print("     %s" % ", ".join(f["principal"] for f in sobran))
        if faltan:
            print("   %d destinos del servidor que el menu no ofrece:" % len(faltan))
            print("     %s" % ", ".join(f["nombre"] for f in faltan))

        if not (mal_numeradas or otros or sobran or faltan):
            print("   la tabla coincide con la del servidor.")
            print()
            continue

        salida = 1
        if not args.sincronizar:
            print()
            continue

        respaldo = path.with_suffix(".bmd.antes-de-sincronizar")
        if not respaldo.exists():
            shutil.copyfile(path, respaldo)
            print("   respaldo en %s" % respaldo.name)

        nuevas = []
        for f in sorted(servidor, key=lambda x: x["index"]):
            c = por_puerta_cli.get(f["puerta"])
            # El nombre se conserva del cliente, y en bytes: la ventana lo
            # compara contra I18N para los chequeos de Icarus y Atlans, y no
            # todos los archivos estan en la misma codificacion. Solo las
            # entradas que el cliente no tenia estrenan nombre, y ese viene del
            # servidor, que es ASCII.
            crudo = (c["nombre_bytes"] if c
                     else f["nombre"].encode("ascii", "replace"))
            nombre = crudo.ljust(LARGO_NOMBRE, b"\x00")[:LARGO_NOMBRE]
            sub = (c["sub_bytes"] if c else nombre)
            sub = sub.ljust(LARGO_NOMBRE, b"\x00")[:LARGO_NOMBRE]
            nuevas.append({
                "index": f["index"],
                "nombre_bytes": nombre,
                "sub_bytes": sub,
                "nivel": f["nivel"],
                "maximo": c["maximo"] if c else f["maximo"],
                "zen": f["zen"],
                "puerta": f["puerta"],
            })

        escribir_cliente(path, nuevas)
        print("   %d entradas escritas (antes %d)." % (len(nuevas), len(cliente)))

        # Releer del disco: si el cifrado o el empaquetado quedaron mal, falla
        # aca y no cuando el cliente intente abrir el archivo. Los nombres se
        # comparan **en bytes** contra los de antes, que es exactamente lo que
        # se me escapo la primera vez: decodificar y recodificar habia
        # convertido la `Í` de "Ícaro" en el caracter de reemplazo.
        vuelta = {f["puerta"]: f for f in leer_cliente(path)}
        if len(vuelta) != len(nuevas):
            print("   NO CUADRA al releer: %d entradas, esperaba %d."
                  % (len(vuelta), len(nuevas)))
            return 1

        for esperada in nuevas:
            leida = vuelta.get(esperada["puerta"])
            if leida is None:
                print("   NO CUADRA: falta la puerta %d." % esperada["puerta"])
                return 1
            for clave in ("index", "nivel", "zen", "maximo",
                          "nombre_bytes", "sub_bytes"):
                if leida[clave] != esperada[clave]:
                    print("   NO CUADRA en la puerta %d, campo %s: %r != %r"
                          % (esperada["puerta"], clave, leida[clave],
                             esperada[clave]))
                    return 1

        conservados = sum(1 for f in nuevas
                          if por_puerta_cli.get(f["puerta"])
                          and por_puerta_cli[f["puerta"]]["nombre_bytes"]
                          == f["nombre_bytes"])
        print("   verificado: %d entradas con el indice del servidor, "
              "%d nombres intactos byte a byte.\n" % (len(vuelta), conservados))
        salida = 0

    if salida and not args.sincronizar:
        print("Corre con --sincronizar para arreglarlo.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

"""Compara -- y opcionalmente sincroniza -- la tabla de items del cliente con la
del GameServer 0.99B.

El cliente no le pregunta al servidor cuanto pega una espada: lo lee de su
propio `Data/Local/<idioma>/Item_<idioma>.bmd` y lo muestra en el tooltip. El
servidor calcula el combate con `Data/Item/Item.txt`. Son dos copias de la misma
tabla y nadie las mantiene sincronizadas.

Cuando difieren no se rompe nada: el tooltip miente. Dice un requisito de nivel
que no es el que el servidor va a exigir, o un daño que no es el que va a
aplicar. Por eso esto empieza siendo una auditoria: no falla sola, hay que ir a
mirarla.

    python tools/gamedata/audit_item_table.py
    python tools/gamedata/audit_item_table.py --detalle
    python tools/gamedata/audit_item_table.py --sincronizar

`--sincronizar` copia los valores del servidor al cliente y deja un respaldo.
Toca unicamente los items que el servidor define; los que el cliente trae de mas
(la tabla de Season 6 entera) quedan como estan, porque este servidor nunca los
va a mandar.

Los nombres se sincronizan solo en el archivo en ingles: la tabla del servidor
esta en ingles y pisar con ella el archivo en español seria una traduccion al
reves. En los demas idiomas se sincronizan los numeros y nada mas.

Formato del .bmd: registros de 84 bytes (nombre de 30 mas los campos, con la
alineacion natural de MSVC), cada uno cifrado con el XOR de tres bytes de
siempre, y un checksum DWORD al final. Es el formato "legacy" que
`ItemDataLoader` reconoce por el tamaño del archivo.
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
            / "bin" / "Debug" / "net10.0" / "Data" / "Item" / "Item.txt")

BUX = bytes([0xFC, 0xCF, 0xAB])
REGISTRO = 84
GRUPOS = 16
POR_GRUPO = 512
CLAVE_CHECKSUM = 0xE2F1
LARGO_NOMBRE = 30

# Client field -> (offset within the record, struct format). These are the offsets of
# ITEM_ATTRIBUTE_FILE_LEGACY; if someone touches ItemFieldDefs.h they have to be redone.
CAMPOS = {
    "TwoHand": (30, "B"), "Level": (32, "H"), "Slot": (34, "B"),
    "Skill": (36, "H"), "Width": (38, "B"), "Height": (39, "B"),
    "DamageMin": (40, "B"), "DamageMax": (41, "B"), "Blocking": (42, "B"),
    "Defense": (43, "B"), "MagicDefense": (44, "B"), "WeaponSpeed": (45, "B"),
    "WalkSpeed": (46, "B"), "Durability": (47, "B"), "MagicDur": (48, "B"),
    "MagicPower": (49, "B"), "ReqStrength": (50, "H"), "ReqDexterity": (52, "H"),
    "ReqEnergy": (54, "H"), "ReqVitality": (56, "H"), "ReqLeadership": (58, "H"),
    "ReqLevel": (60, "H"), "Value": (62, "B"), "Zen": (64, "i"),
    "AttType": (68, "B"),
}
for _i in range(5):
    CAMPOS["Clase%d" % _i] = (69 + _i, "B")
for _i in range(7):
    CAMPOS["Resist%d" % _i] = (76 + _i, "B")

# Item.txt column -> client field. The ones not here have no client-side equivalent: HaveSerial, HaveOption,
# DropItem and SetAttr are decisions only the server makes.
COLUMNAS = {
    "Slot": "Slot", "Skill": "Skill", "Width": "Width", "Height": "Height",
    "Level": "Level", "DamageMin": "DamageMin", "DamageMax": "DamageMax",
    "Defense": "Defense", "MagicDefense": "MagicDefense",
    "DefenseSuccessRate": "Blocking", "AttackSpeed": "WeaponSpeed",
    "WalkSpeed": "WalkSpeed", "Durability": "Durability",
    "MagicDurability": "MagicDur", "MagicDamageRate": "MagicPower",
    "ReqLevel": "ReqLevel", "ReqStrength": "ReqStrength",
    "ReqDexterity": "ReqDexterity", "ReqEnergy": "ReqEnergy",
    "ReqVitality": "ReqVitality", "ReqLeadership": "ReqLeadership",
    "BuyMoney": "Zen", "Value": "Value",
    "IceRes": "Resist0", "PoisonRes": "Resist1", "LightRes": "Resist2",
    "FireRes": "Resist3", "EarthRes": "Resist4", "WindRes": "Resist5",
    "WaterRes": "Resist6",
    "DW": "Clase0", "DK": "Clase1", "FE": "Clase2", "MG": "Clase3",
    "DL": "Clase4",
}


# --------------------------------------------------------------------------
# El .bmd
# --------------------------------------------------------------------------

def bux(registro):
    """El cifrado es su propio inverso, asi que esta funcion sirve para los dos
    sentidos. Se aplica por registro, no sobre el archivo entero: el contador
    vuelve a cero en cada item."""
    salida = bytearray(registro)
    for i in range(len(salida)):
        salida[i] ^= BUX[i % 3]
    return bytes(salida)


def checksum(buffer, clave=CLAVE_CHECKSUM):
    """Puerto de GenerateCheckSum2 (ZzzInfomation.h). Se calcula sobre los datos
    ya cifrados, que es como estan en el archivo."""
    resultado = (clave << 9) & 0xFFFFFFFF
    pos = 0
    while pos <= len(buffer) - 4:
        palabra = struct.unpack_from("<I", buffer, pos)[0]
        if (pos // 4 + clave) % 2 == 0:
            resultado ^= palabra
        else:
            resultado = (resultado + palabra) & 0xFFFFFFFF
        if pos % 16 == 0:
            resultado ^= ((clave + resultado) & 0xFFFFFFFF) >> ((pos // 4) % 8 + 1)
        resultado &= 0xFFFFFFFF
        pos += 4
    return resultado


def leer_cliente(path):
    """Devuelve {(grupo, indice): registro descifrado}."""
    datos = path.read_bytes()
    esperado = REGISTRO * GRUPOS * POR_GRUPO + 4
    if len(datos) != esperado:
        raise SystemExit("%s mide %d bytes; esperaba %d (formato legacy de 84)."
                         % (path.name, len(datos), esperado))

    guardado = struct.unpack_from("<I", datos, len(datos) - 4)[0]
    calculado = checksum(datos[:-4])
    if guardado != calculado:
        raise SystemExit("%s tiene el checksum roto (0x%08X en el archivo, "
                         "0x%08X calculado)." % (path.name, guardado, calculado))

    tabla = {}
    for n in range(GRUPOS * POR_GRUPO):
        crudo = datos[n * REGISTRO:(n + 1) * REGISTRO]
        tabla[(n // POR_GRUPO, n % POR_GRUPO)] = bux(crudo)
    return tabla


def escribir_cliente(path, tabla):
    buffer = bytearray()
    for n in range(GRUPOS * POR_GRUPO):
        buffer += bux(tabla[(n // POR_GRUPO, n % POR_GRUPO)])
    buffer += struct.pack("<I", checksum(bytes(buffer)))
    path.write_bytes(bytes(buffer))


def nombre(registro):
    crudo = registro[:LARGO_NOMBRE]
    fin = crudo.find(b"\x00")
    if fin >= 0:
        crudo = crudo[:fin]
    return crudo.decode("cp1252", "replace").strip()


def campo(registro, cual):
    off, fmt = CAMPOS[cual]
    return struct.unpack_from("<" + fmt, registro, off)[0]


def poner_campo(registro, cual, valor):
    off, fmt = CAMPOS[cual]
    salida = bytearray(registro)
    struct.pack_into("<" + fmt, salida, off, valor)
    return bytes(salida)


def poner_nombre(registro, texto):
    """Escribe un nombre, o se planta si no puede hacerlo sin perder nada.

    Los archivos del cliente no estan todos en la misma codificacion -- en la
    tabla de warps el nombre de Icarus en español trae la `Í` en Latin-1 -- y
    escribir con `errors="replace"` cambia el caracter que no entra por un signo
    de pregunta sin decir nada. Hoy todos los nombres del servidor son ASCII y
    esto nunca se dispara; el dia que deje de ser cierto, es mejor que falle.
    """
    crudo = texto.encode("cp1252", "strict")
    if len(crudo) >= LARGO_NOMBRE:
        raise SystemExit("El nombre %r no entra en %d bytes."
                         % (texto, LARGO_NOMBRE))
    salida = bytearray(registro)
    salida[:LARGO_NOMBRE] = crudo + bytes(LARGO_NOMBRE - len(crudo))
    return bytes(salida)


# --------------------------------------------------------------------------
# El Item.txt del servidor
# --------------------------------------------------------------------------

def leer_servidor(path):
    """Item.txt: un bloque por grupo, cada uno con su propio encabezado.

    Los encabezados no coinciden entre grupos -- un escudo tiene
    DefenseSuccessRate donde una espada tiene DamageMin -- asi que las columnas
    se leen de cada bloque en vez de asumirse.
    """
    tabla = {}
    grupo = None
    columnas = None

    for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
        texto = linea.strip()
        if not texto:
            continue
        if texto == "end":
            grupo, columnas = None, None
            continue
        if texto.startswith("//"):
            columnas = texto.lstrip("/").split()[1:]  # se saltea "Type"
            continue
        if grupo is None:
            if texto.isdigit():
                grupo = int(texto)
            continue
        if not columnas:
            continue

        indice = int(texto.split()[0])
        resto = texto.split(None, 1)[1]

        # The name comes in quotes and has spaces; it is taken out before splitting into columns and put back in
        # its place.
        comillas = re.search(r'"([^"]*)"', resto)
        etiqueta = comillas.group(1) if comillas else ""
        partes = re.sub(r'"[^"]*"', "\x00", resto).split()

        valores = []
        for parte in partes:
            if parte == "\x00":
                valores.append(etiqueta)
            else:
                try:
                    valores.append(int(parte))
                except ValueError:
                    valores.append(parte)

        tabla[(grupo, indice)] = dict(zip(columnas, valores))

    return tabla


# --------------------------------------------------------------------------

def comparar(cliente, servidor, con_nombres):
    """Devuelve (diferencias, faltan, comparados).

    `diferencias` es {campo: [(clave, etiqueta, valor_servidor, valor_cliente)]}.
    """
    diferencias = {}
    faltan = []
    comparados = 0

    for clave, fila in sorted(servidor.items()):
        registro = cliente.get(clave)
        etiqueta = fila.get("Name", "")

        if registro is None or not nombre(registro):
            faltan.append((clave, etiqueta))
            continue

        comparados += 1
        if con_nombres and nombre(registro).lower() != etiqueta.lower():
            diferencias.setdefault("Name", []).append(
                (clave, etiqueta, etiqueta, nombre(registro)))

        for columna, destino in COLUMNAS.items():
            esperado = fila.get(columna)
            if not isinstance(esperado, int):
                continue
            actual = campo(registro, destino)
            if actual != esperado:
                diferencias.setdefault(destino, []).append(
                    (clave, etiqueta, esperado, actual))

    return diferencias, faltan, comparados


def sincronizar(cliente, servidor, con_nombres):
    """Escribe los valores del servidor sobre la tabla del cliente en memoria."""
    cambios = 0
    for clave, fila in servidor.items():
        registro = cliente.get(clave)
        if registro is None or not nombre(registro):
            continue

        etiqueta = fila.get("Name", "")
        if con_nombres and etiqueta and nombre(registro) != etiqueta:
            registro = poner_nombre(registro, etiqueta)
            cambios += 1

        for columna, destino in COLUMNAS.items():
            esperado = fila.get(columna)
            if not isinstance(esperado, int):
                continue
            if campo(registro, destino) != esperado:
                registro = poner_campo(registro, destino, esperado)
                cambios += 1

        cliente[clave] = registro
    return cambios


def informar(path, diferencias, faltan, comparados, servidor, cliente, detalle):
    print("== %s" % path.relative_to(REPO))
    print("   items en el servidor : %d" % len(servidor))
    print("   comparados           : %d" % comparados)
    print("   sin definir en el cliente: %d" % len(faltan))

    sobran = sum(1 for (g, i), r in cliente.items()
                 if i < 32 and nombre(r) and (g, i) not in servidor)
    print("   en el cliente y no en el servidor (indice < 32): %d" % sobran)

    if not diferencias:
        print("   sin diferencias: el cliente describe los items como el "
              "servidor los aplica.")
    else:
        total = sum(len(v) for v in diferencias.values())
        print("   %d diferencias en %d campos:" % (total, len(diferencias)))
        for destino in sorted(diferencias, key=lambda k: -len(diferencias[k])):
            filas = diferencias[destino]
            print("     %-14s %3d" % (destino, len(filas)))
            muestras = filas if detalle else filas[:3]
            for (grupo, indice), etiqueta, esperado, actual in muestras:
                print("        g%-2d i%-3d %-28s servidor=%-8s cliente=%s"
                      % (grupo, indice, etiqueta[:28], esperado, actual))
            if not detalle and len(filas) > 3:
                print("        ... y %d mas (--detalle)" % (len(filas) - 3))

    if faltan:
        print("   el servidor puede mandar items que el cliente no define:")
        for (grupo, indice), etiqueta in faltan:
            print("     g%-2d i%-3d %s" % (grupo, indice, etiqueta))
    print()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--detalle", action="store_true",
                        help="lista cada diferencia en vez de resumirlas")
    parser.add_argument("--sincronizar", action="store_true",
                        help="copia los valores del servidor al cliente")
    args = parser.parse_args()

    if not SERVIDOR.exists():
        print("No encuentro la tabla del servidor en %s" % SERVIDOR)
        return 1

    servidor = leer_servidor(SERVIDOR)

    archivos = sorted(LOCAL.glob("*/item_*.bmd"))
    archivos = [p for p in archivos
                if re.fullmatch(r"item_[a-z]{3}\.bmd", p.name.lower())]
    if not archivos:
        print("No encuentro ningun Item_<idioma>.bmd en %s" % LOCAL)
        return 1

    pendientes = 0
    for path in archivos:
        con_nombres = path.name.lower() == "item_eng.bmd"
        cliente = leer_cliente(path)
        diferencias, faltan, comparados = comparar(cliente, servidor, con_nombres)
        informar(path, diferencias, faltan, comparados, servidor, cliente,
                 args.detalle)
        pendientes += sum(len(v) for v in diferencias.values())

        if args.sincronizar and diferencias:
            respaldo = path.with_suffix(".bmd.antes-de-sincronizar")
            if not respaldo.exists():
                shutil.copyfile(path, respaldo)
                print("   respaldo en %s" % respaldo.name)

            cambios = sincronizar(cliente, servidor, con_nombres)
            escribir_cliente(path, cliente)
            print("   %d valores escritos." % cambios)

            # Re-read from disk: if the encryption or the checksum went wrong, this fails here and not when the
            # client tries to open the file.
            diferencias, _, _ = comparar(leer_cliente(path), servidor, con_nombres)
            if diferencias:
                print("   QUEDARON DIFERENCIAS despues de escribir.")
                return 1
            print("   verificado: 0 diferencias al releer.\n")

    if args.sincronizar:
        print("Listo. Volve a copiar Data/ al build o reconstruilo para que el "
              "cliente use los archivos nuevos.")
    elif pendientes:
        print("Corre con --sincronizar para copiar los valores del servidor.")

    return 0


if __name__ == "__main__":
    sys.exit(main())

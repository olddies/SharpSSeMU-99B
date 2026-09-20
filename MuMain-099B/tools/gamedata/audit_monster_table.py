"""Comprueba que el cliente sepa el nombre de todos los monstruos del servidor.

De las tablas duplicadas esta es la mas simple: de la lista de monstruos el
cliente sólo usa **el numero y el nombre** (`OpenMonsterScript` lee el tercer
token y descarta el resto; las estadisticas las calcula el servidor y manda el
resultado). Asi que la unica pregunta es si a cada clase de monstruo le
corresponde un nombre.

Cuando no lo encuentra, `getMonsterName` devuelve `"()"`. No se rompe nada: te
para adelante un bicho sin nombre.

No hay `--sincronizar`, y no es lo mismo que con las habilidades. Aca la razon es
otra: **los nombres del cliente estan traducidos** (`Jefe Guerrero Esqueleto`,
`Ogro Gigante`) y los del servidor estan en ingles. Copiarlos pisaria las tres
traducciones con el ingles. Lo que falte hay que agregarlo a mano, en cada
idioma.

    python tools/gamedata/audit_monster_table.py
"""

import argparse
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LOCAL = REPO / "src" / "bin" / "Data" / "Local"
DATOS = (REPO.parent / "SharpSSeMU" / "src" / "MuServer.GameServer"
         / "bin" / "Debug" / "net10.0" / "Data")
LISTA = DATOS / "Monster" / "MonsterList.txt"
SPAWNS = DATOS / "Monster" / "Spawn"


def numerados(path):
    """{numero: nombre} de las lineas que empiezan con un numero y traen un
    nombre entre comillas. Sirve igual para MonsterList.txt y para los
    NpcName_<idioma>.txt, que tienen formatos distintos pero comparten eso.

    Se lee en bytes y se decodifica con cp1252: los archivos del cliente traen
    acentos en Latin-1 y comentarios en coreano, y un decode estricto se cae.
    """
    tabla = {}
    for linea in path.read_bytes().decode("cp1252", "replace").splitlines():
        texto = linea.strip()
        if not texto or texto.startswith("//") or texto == "end":
            continue
        cabeza = texto.split()[0]
        if not cabeza.isdigit():
            continue
        comillas = re.search(r'"([^"]*)"', texto)
        if comillas:
            tabla[int(cabeza)] = comillas.group(1)
    return tabla


def clases_que_aparecen():
    """Las clases de monstruo que el servidor realmente pone en el mundo.

    Un nombre que falta pero que nunca se instancia es un problema latente; uno
    que falta y spawnea se ve hoy. Conviene distinguirlos.
    """
    clases = set()
    if not SPAWNS.is_dir():
        return clases
    for path in SPAWNS.glob("*.txt"):
        for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
            texto = linea.strip()
            if not texto or texto.startswith("//") or texto == "end":
                continue
            campos = texto.split()
            # MonsterIndex SpawnRadius LocationX LocationY [Direction]
            if len(campos) >= 4 and campos[0].isdigit():
                clases.add(int(campos[0]))
    return clases


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()

    if not LISTA.exists():
        print("No encuentro %s" % LISTA)
        return 1

    servidor = numerados(LISTA)
    spawnean = clases_que_aparecen()

    archivos = sorted(LOCAL.glob("*/NpcName_*.txt"))
    if not archivos:
        print("No encuentro ningun NpcName_<idioma>.txt en %s" % LOCAL)
        return 1

    print("monstruos en el servidor: %d" % len(servidor))
    print("de esos, aparecen en algun spawn: %d"
          % len([c for c in servidor if c in spawnean]))
    print()

    problemas = 0
    for path in archivos:
        cliente = numerados(path)
        faltan = [c for c in sorted(servidor) if c not in cliente]
        vacios = [c for c in sorted(servidor)
                  if c in cliente and not cliente[c].strip()]

        print("== %s  (%d entradas)" % (path.relative_to(REPO), len(cliente)))
        if not faltan and not vacios:
            print("   todos los monstruos del servidor tienen nombre.")
        for c in faltan + vacios:
            aviso = "SE VE HOY" if c in spawnean else "latente, no spawnea"
            print("   falta el %-4d %-28s -> se muestra \"()\"  (%s)"
                  % (c, servidor[c], aviso))
            problemas += 1
        print()

    # El nombre lo busca por numero de clase, asi que un spawn de una clase que
    # ni siquiera esta en MonsterList tampoco tendria nombre.
    huerfanos = sorted(c for c in spawnean if c not in servidor)
    if huerfanos:
        print("Clases que spawnean y no estan en MonsterList.txt: %s"
              % ", ".join(str(c) for c in huerfanos))
        problemas += len(huerfanos)

    return 1 if problemas else 0


if __name__ == "__main__":
    sys.exit(main())

"""Compara la tabla de habilidades del cliente con la del GameServer 0.99B.

Es la hermana de `audit_item_table.py`, con una diferencia que importa: **esta no
sincroniza**, y no es un descuido.

Con los items las dos tablas dicen lo mismo con los mismos campos, asi que copiar
del servidor al cliente es correcto. Con las habilidades no: las dos tablas usan
las mismas columnas para cosas distintas.

Dos ejemplos, los dos verificados contra el codigo:

* `Range` en el servidor vale 0 para las habilidades de area (Evil Spirit, Nova,
  Twisting Slash...), porque el alcance de esas lo decide `Radio`. En el cliente
  `Distance` es la distancia desde la que te deja *lanzar*
  (`CSkillManager::GetSkillDistance`, que usa `SkillCast`). Copiar el 0 dejaria
  esas habilidades inlanzables.
* Las columnas de requisitos (`ReqLevel`, `ReqEnergy`, `ReqLeadership`,
  `ReqKillCount`, `ReqGuildStatus`) estan en cero para *todas* las habilidades
  del servidor. El cliente si los tiene, y ademas los usa de forma doble
  -- `GetSkillInformation_Energy` documenta que `Energy` es costo directo o
  factor por nivel segun el caso. Copiar los ceros borraria todos los requisitos
  de la interfaz.

Por eso esto informa y no toca nada. Las diferencias que quedan hay que mirarlas
una por una y decidir; el informe las agrupa para que se pueda.

    python tools/gamedata/audit_skill_table.py
"""

import argparse
import re
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
LOCAL = REPO / "src" / "bin" / "Data" / "Local"
SERVIDOR = (REPO.parent.parent / "SharpSSeMU" / "src" / "MuServer.GameServer"
            / "bin" / "Debug" / "net10.0" / "Data" / "Skill" / "SkillList.txt")

BUX = bytes([0xFC, 0xCF, 0xAB])
REGISTRO = 108           # SKILL_ATTRIBUTE_FILE: nombre de 50 mas los campos
MAX_SKILLS = 650
LARGO_NOMBRE = 50
CLAVE_CHECKSUM = 0x1F2E  # ver abajo: se detecta sola si no es esta

# Offsets de SKILL_ATTRIBUTE_FILE con la alineacion natural de MSVC.
CAMPOS = {
    "Level": (50, "H"), "Damage": (52, "H"), "Mana": (54, "H"),
    "AbilityGuage": (56, "H"), "Distance": (60, "I"), "Delay": (64, "i"),
    "Energy": (68, "i"), "Charisma": (72, "H"), "MasteryType": (74, "B"),
    "SkillUseType": (75, "B"), "SkillBrand": (76, "I"), "KillCount": (80, "B"),
    "Duty0": (81, "B"), "Duty1": (82, "B"), "Duty2": (83, "B"),
    "Clase0": (84, "B"), "Clase1": (85, "B"), "Clase2": (86, "B"),
    "Clase3": (87, "B"), "Clase4": (88, "B"), "Clase5": (89, "B"),
    "Clase6": (90, "B"), "SkillRank": (91, "B"), "Icono": (92, "H"),
    "TypeSkill": (94, "B"), "Strength": (96, "i"), "Dexterity": (100, "i"),
    "ItemSkill": (104, "B"), "IsDamage": (105, "B"), "Effect": (106, "H"),
}

# Columna de SkillList.txt -> campo del cliente.
COLUMNAS = {
    "Damage": "Damage", "MP": "Mana", "BP": "AbilityGuage",
    "Range": "Distance", "Delay": "Delay", "Effect": "Effect",
    "ReqLevel": "Level", "ReqEnergy": "Energy",
    "ReqLeadership": "Charisma", "ReqKillCount": "KillCount",
    "DW": "Clase0", "DK": "Clase1", "FE": "Clase2", "MG": "Clase3",
    "DL": "Clase4",
}

# Las que el cliente interpreta distinto. Se informan aparte y con el motivo,
# en vez de mezclarlas con las diferencias que si son diferencias.
SEMANTICA = {
    "Range": "el servidor pone 0 en las de area; el cliente lo usa como "
             "distancia de lanzamiento",
}

# Campos que el cliente carga y despues no mira nunca. Una diferencia aca no
# cambia nada en pantalla, y perseguirla es tiempo perdido -- por eso van
# separadas. Sale de buscar quien lee cada campo de SKILL_ATTRIBUTE:
INERTES = {
    "Effect": "SKILL_ATTRIBUTE::Effect solo aparece en los metadatos del "
              "editor; en el juego no lo lee nadie",
    "DW": "RequireClass de habilidades solo se usa en CheckUseMasterSkill "
          "(habilidades de gremio)",
    "DK": "idem DW",
    "FE": "idem DW",
    "MG": "idem DW",
    "DL": "idem DW",
}


def bux(registro):
    salida = bytearray(registro)
    for i in range(len(salida)):
        salida[i] ^= BUX[i % 3]
    return bytes(salida)


def leer_cliente(path):
    datos = path.read_bytes()
    esperado = REGISTRO * MAX_SKILLS + 4
    if len(datos) != esperado:
        raise SystemExit("%s mide %d bytes; esperaba %d." %
                         (path.name, len(datos), esperado))
    return [bux(datos[i * REGISTRO:(i + 1) * REGISTRO])
            for i in range(MAX_SKILLS)]


def nombre(registro):
    crudo = registro[:LARGO_NOMBRE]
    fin = crudo.find(b"\x00")
    if fin >= 0:
        crudo = crudo[:fin]
    return crudo.decode("cp1252", "replace").strip()


def campo(registro, cual):
    off, fmt = CAMPOS[cual]
    return struct.unpack_from("<" + fmt, registro, off)[0]


def leer_servidor(path):
    columnas = None
    tabla = {}
    for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
        texto = linea.strip()
        if not texto:
            continue
        if texto.startswith("//"):
            if "Index" in texto:
                columnas = texto.lstrip("/").split()[1:]
            continue
        if texto == "end" or not columnas:
            continue
        if not texto.split()[0].isdigit():
            continue

        indice = int(texto.split()[0])
        resto = texto.split(None, 1)[1]
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
                    valores.append(parte)   # el servidor usa "*" para "no aplica"

        tabla[indice] = dict(zip(columnas, valores))
    return tabla


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()

    if not SERVIDOR.exists():
        print("No encuentro %s" % SERVIDOR)
        return 1

    servidor = leer_servidor(SERVIDOR)
    path = LOCAL / "Eng" / "skill_eng.bmd"
    if not path.exists():
        print("No encuentro %s" % path)
        return 1
    cliente = leer_cliente(path)

    filas = [(i, f, cliente[i]) for i, f in sorted(servidor.items())
             if i < MAX_SKILLS and nombre(cliente[i])]

    # Una columna que vale 0 en las N habilidades no dice nada: no es que el
    # cliente este mal, es que el servidor no la llena. Se detecta sola para que
    # el informe no la cuente como diferencia.
    vacias = []
    for columna in COLUMNAS:
        valores = [f.get(columna) for _, f, _ in filas]
        if valores and all(v == 0 for v in valores):
            vacias.append(columna)

    print("habilidades en el servidor: %d" % len(servidor))
    print("comparadas                : %d" % len(filas))
    if vacias:
        print()
        print("Columnas que el servidor deja en cero para todas (no se comparan):")
        print("   %s" % ", ".join(sorted(vacias)))
        print("   El cliente si tiene estos valores y los usa. La tabla del")
        print("   servidor esta incompleta, no el cliente.")

    nombres = [(i, f.get("Name", ""), nombre(r)) for i, f, r in filas
               if f.get("Name", "").lower() != nombre(r).lower()]
    if nombres:
        print()
        print("Nombres distintos: %d" % len(nombres))
        print("   No es un error: son los nombres de dos versiones del juego. El")
        print("   que se ve en pantalla es el del cliente; el del servidor solo")
        print("   sale en sus logs. Cambiarlos es una decision de fidelidad.")
        for indice, esperado, actual in nombres:
            print("     %-4d servidor %-30s cliente %s" % (indice, esperado, actual))

    reales, semanticas, inertes = {}, {}, {}
    for columna, destino in COLUMNAS.items():
        if columna in vacias:
            continue
        for indice, fila, registro in filas:
            esperado = fila.get(columna)
            if not isinstance(esperado, int):
                continue
            actual = campo(registro, destino)
            if actual != esperado:
                # La excusa del "0 es de area" solo vale cuando el 0 lo pone el
                # servidor. Si los dos lados dicen un numero y no es el mismo,
                # es un desacuerdo de verdad y va con los otros.
                if columna in SEMANTICA and esperado == 0:
                    bolsa = semanticas
                elif columna in INERTES:
                    bolsa = inertes
                else:
                    bolsa = reales
                bolsa.setdefault(columna, []).append(
                    (indice, nombre(registro), esperado, actual))

    if semanticas:
        print()
        print("Diferencias esperadas por como cada lado usa la columna:")
        for columna, lista in semanticas.items():
            print("   %s -- %s (%d)" % (columna, SEMANTICA[columna], len(lista)))
            for indice, etiqueta, esperado, actual in lista:
                print("     %-4d %-26s servidor=%-6s cliente=%s"
                      % (indice, etiqueta[:26], esperado, actual))

    if inertes:
        print()
        print("Diferencias en campos que el cliente no lee:")
        for columna, lista in inertes.items():
            print("   %s -- %s (%d)" % (columna, INERTES[columna], len(lista)))
            for indice, etiqueta, esperado, actual in lista:
                print("     %-4d %-26s servidor=%-6s cliente=%s"
                      % (indice, etiqueta[:26], esperado, actual))

    print()
    if not reales:
        print("Sin diferencias que cambien lo que el jugador ve.")
        return 0

    total = sum(len(v) for v in reales.values())
    print("Diferencias a mirar: %d" % total)
    for columna in sorted(reales, key=lambda k: -len(reales[k])):
        print("   %s (%d)" % (columna, len(reales[columna])))
        for indice, etiqueta, esperado, actual in reales[columna]:
            print("     %-4d %-26s servidor=%-6s cliente=%s"
                  % (indice, etiqueta[:26], esperado, actual))
    return 0


if __name__ == "__main__":
    sys.exit(main())

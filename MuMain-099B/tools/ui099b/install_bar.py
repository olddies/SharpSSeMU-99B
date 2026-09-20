"""Instala las texturas de la barra inferior de 0.99B en MuMain.

A diferencia de las siluetas de casilla, acá no hace falta convertir nada: los
dos clientes usan OZJ y el renderizador recibe el ancho y el alto explícitos en
cada llamada, así que alcanza con poner el archivo de 0.99B con el nombre que
MuMain busca. Lo que sí hay que cambiar son las medidas del lado del código, y
eso vive en NewUIMainFrameWindow.

Las dos barras no son la misma con otra pintura: la de 0.99B mide 48 píxeles de
alto contra 51, tiene tres teclas rápidas en vez de cuatro y no tiene la fila
numerada. Este script sólo mueve archivos; la geometría se ajusta aparte.

    python tools/ui099b/install_bar.py [--instalar]
"""

import argparse
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DESTINO = REPO / "src" / "bin" / "Data" / "Interface"
ORIGEN = REPO.parent.parent / "MuClient" / "Data" / "Interface"

# 0.99B -> el nombre con el que MuMain pide la textura. Los nombres no
# coinciden entre versiones, pero el rol de cada pieza sí.
BARRA = [
    ("menu01_new.OZJ", "newui_menu01.OZJ"),      # tramo izquierdo, 256 de ancho
    ("Menu02.OZJ", "newui_menu02.OZJ"),          # tramo del medio, 128
    ("Menu03_new.OZJ", "newui_menu03.OZJ"),      # tramo derecho, 256
    ("Menu_Blue.OZJ", "newui_menu_blue.OZJ"),    # medidor de mana
    ("Menu_Green.OZJ", "newui_menu_green.OZJ"),  # medidor de resistencia
    ("Menu_Red.OZJ", "newui_menu_red.OZJ"),      # medidor de vida
    ("Menu03_new_AG.OZJ", "newui_menu_AG.OZJ"),  # medidor de AG
]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--instalar", action="store_true")
    args = parser.parse_args()

    # newui_menu03 vive en una subcarpeta en MuMain; se busca donde esté.
    ubicaciones = {p.name.lower(): p for p in DESTINO.rglob("*") if p.is_file()}

    plan = []
    for origen_nombre, destino_nombre in BARRA:
        origen = ORIGEN / origen_nombre
        destino = ubicaciones.get(destino_nombre.lower())

        if not origen.exists():
            print(f"  falta en 0.99B: {origen_nombre}")
            continue
        if destino is None:
            print(f"  no encuentro en MuMain: {destino_nombre}")
            continue

        plan.append((origen, destino))

    print(f"{len(plan)} texturas de barra a reemplazar:")
    for origen, destino in plan:
        relativo = destino.relative_to(DESTINO)
        print(f"    {origen.name:<22} -> {relativo}")

    if not args.instalar:
        print("\n(Sin --instalar no se toco nada.)")
        return 0

    respaldo = DESTINO.parent / "Interface.s6"
    if not respaldo.exists():
        shutil.copytree(DESTINO, respaldo)
        print(f"\nRespaldo en {respaldo}")

    for origen, destino in plan:
        shutil.copy2(origen, destino)

    print(f"\n{len(plan)} texturas instaladas.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

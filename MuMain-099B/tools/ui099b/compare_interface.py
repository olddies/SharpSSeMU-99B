"""Compara los assets de interfaz de 0.99B contra los que trae MuMain.

Reemplazar una textura por otra del mismo nombre es trivial; lo que no es
trivial es que tenga otro tamaño. El layout de estas ventanas está a mano en el
código, con coordenadas y anchos escritos como números: una imagen más grande no
se reescala sola, se sale del recuadro o tapa lo de al lado.

Por eso la primera pregunta no es "cuáles tienen el mismo nombre" sino "cuáles
tienen además las mismas dimensiones". Esas son intercambio directo de verdad;
el resto necesita tocar el layout.

    python tools/ui099b/compare_interface.py [--copiar]

Sin argumentos sólo informa. Con --copiar reemplaza únicamente los que coinciden
en dimensiones, que son los que no pueden romper nada.

Formatos: OZJ son 24 bytes de encabezado y después un JPEG; OZT son 4 bytes y
después un TGA. Las dimensiones salen de esos formatos, no del encabezado
propio, que no las lleva.
"""

import argparse
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DESTINO = REPO / "src" / "bin" / "Data" / "Interface"
ORIGEN = REPO.parent / "MuClient" / "Data" / "Interface"

OZJ_HEADER = 24
OZT_HEADER = 4


def jpeg_size(data: bytes) -> tuple[int, int] | None:
    """Ancho y alto del primer marcador SOF de un JPEG."""
    i = 2  # se saltea el SOI
    while i + 9 < len(data):
        if data[i] != 0xFF:
            i += 1
            continue

        marker = data[i + 1]
        # Los SOF llevan las dimensiones; C4, C8 y CC son otra cosa pese al rango.
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            height = (data[i + 5] << 8) | data[i + 6]
            width = (data[i + 7] << 8) | data[i + 8]
            return width, height

        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            i += 2
            continue

        length = (data[i + 2] << 8) | data[i + 3]
        i += 2 + length

    return None


def tga_size(data: bytes) -> tuple[int, int] | None:
    """Ancho y alto del encabezado TGA, que los trae en los offsets 12 y 14."""
    if len(data) < 18:
        return None
    width = data[12] | (data[13] << 8)
    height = data[14] | (data[15] << 8)
    return width, height


def dimensions(path: Path) -> tuple[int, int] | None:
    data = path.read_bytes()
    suffix = path.suffix.lower()

    if suffix == ".ozj":
        return jpeg_size(data[OZJ_HEADER:])
    if suffix == ".ozt":
        return tga_size(data[OZT_HEADER:])
    if suffix == ".tga":
        return tga_size(data)
    if suffix in (".jpg", ".jpeg"):
        return jpeg_size(data)

    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--copiar", action="store_true",
                        help="reemplaza los que coinciden en dimensiones")
    args = parser.parse_args()

    if not ORIGEN.is_dir():
        print(f"No encuentro la interfaz de 0.99B en {ORIGEN}")
        return 1

    # El sistema de archivos de Windows no distingue mayúsculas pero los nombres
    # sí difieren entre los dos clientes (Cursor.ozt contra CURSOR.OZT), así que
    # el cruce se hace en minúsculas.
    destino = {p.name.lower(): p for p in DESTINO.iterdir() if p.is_file()}

    iguales, distintas, ilegibles, solo_origen = [], [], [], []

    for origen in sorted(ORIGEN.iterdir()):
        if not origen.is_file():
            continue

        par = destino.get(origen.name.lower())
        if par is None:
            solo_origen.append(origen.name)
            continue

        a, b = dimensions(origen), dimensions(par)
        if a is None or b is None:
            ilegibles.append((origen.name, a, b))
        elif a == b:
            iguales.append((origen, par, a))
        else:
            distintas.append((origen.name, a, b))

    print(f"0.99B tiene {sum(1 for p in ORIGEN.iterdir() if p.is_file())} archivos de interfaz.\n")

    print(f"Mismo nombre y mismas dimensiones: {len(iguales)}  <- intercambio directo")
    for origen, _, size in iguales[:10]:
        print(f"    {origen.name:<28} {size[0]}x{size[1]}")
    if len(iguales) > 10:
        print(f"    ... y {len(iguales) - 10} mas")

    print(f"\nMismo nombre, DISTINTAS dimensiones: {len(distintas)}  <- hay que tocar el layout")
    for nombre, a, b in distintas:
        print(f"    {nombre:<28} 0.99B {a[0]}x{a[1]}   MuMain {b[0]}x{b[1]}")

    if ilegibles:
        print(f"\nNo pude leer las dimensiones: {len(ilegibles)}")
        for nombre, a, b in ilegibles:
            print(f"    {nombre:<28} {a} / {b}")

    print(f"\nSolo en 0.99B (ventanas que S6 rediseño): {len(solo_origen)}")

    if not args.copiar:
        print("\n(Sin --copiar no se toco nada.)")
        return 0

    respaldo = DESTINO.parent / "Interface.s6"
    if not respaldo.exists():
        # Copia entera antes de tocar nada: sin esto no hay forma de volver
        # atrás si el resultado no gusta.
        shutil.copytree(DESTINO, respaldo)
        print(f"\nRespaldo del original en {respaldo}")

    for origen, par, _ in iguales:
        shutil.copy2(origen, par)

    print(f"{len(iguales)} archivos reemplazados.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

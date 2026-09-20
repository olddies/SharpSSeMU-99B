"""Convierte las siluetas de casilla vacía de 0.99B al formato que usa MuMain.

No es un intercambio de archivos: los dos clientes dibujan lo mismo pero de
forma incompatible.

* 0.99B usa `Item_*.OZJ`: un mosaico **opaco** de 40x40, la silueta sobre un
  fondo oscuro liso, sin marco.
* MuMain usa `newui_item_*.OZT`: 46x46 con canal alfa, la misma silueta pero con
  un marco metálico dibujado encima y las esquinas redondeadas.

Los dos son opacos en el centro, así que no hay nada que recortar -- intentar
generar transparencia sólo agrega ruido. Lo que hay que hacer es rellenar el
lienzo del tamaño de la casilla con el fondo del propio mosaico y centrar la
imagen de 0.99B: queda nítida y llena la casilla, sin marco, que es como se veía
en ese cliente.

    python tools/ui099b/convert_slots.py --previsualizar salida.png
    python tools/ui099b/convert_slots.py --instalar

`--previsualizar` arma una hoja de contactos para mirar el resultado antes de
tocar nada. `--instalar` escribe los OZT, respaldando los originales.
"""

import argparse
import shutil
import struct
import sys
from io import BytesIO
from pathlib import Path

from PIL import Image

REPO = Path(__file__).resolve().parents[2]
DESTINO = REPO / "src" / "bin" / "Data" / "Interface"
ORIGEN = REPO.parent.parent / "MuClient" / "Data" / "Interface"

OZJ_HEADER = 24
OZT_HEADER = 4

# 0.99B -> MuMain. Los nombres no coinciden, pero el rol sí; el tamaño destino
# es el de la casilla, que está escrito a mano en SetEquipmentSlotInfo.
SLOTS = [
    ("Item_Cap.OZJ", "newui_item_cap.OZT", (46, 46)),
    ("Item_Upper.OZJ", "newui_item_upper.OZT", (46, 66)),
    ("Item_Lower.OZJ", "newui_item_lower.OZT", (46, 46)),
    ("Item_Gloves.OZJ", "newui_item_gloves.OZT", (46, 46)),
    ("Item_Boots.OZJ", "newui_item_boots.OZT", (46, 46)),
    ("Item_Ring.OZJ", "newui_item_ring.OZT", (28, 28)),
    ("Item_NeckLace.OZJ", "newui_item_necklace.OZT", (28, 28)),
    ("Item_Wing.OZJ", "newui_item_wing.OZT", (61, 46)),
    ("Item_Fairy.OZJ", "newui_item_fairy.OZT", (46, 46)),
    ("Item_Weapon01.OZJ", "newui_item_weapon(L).OZT", (46, 66)),
    ("Item_Weapon02.OZJ", "newui_item_weapon(R).OZT", (46, 66)),
]

def load_ozj(path: Path) -> Image.Image:
    return Image.open(BytesIO(path.read_bytes()[OZJ_HEADER:])).convert("RGB")


def load_ozt(path: Path) -> Image.Image:
    """Lee un OZT (TGA de 32 bits sin comprimir, escrito de abajo hacia arriba)."""
    data = path.read_bytes()[OZT_HEADER:]
    width = data[12] | (data[13] << 8)
    height = data[14] | (data[15] << 8)
    depth = data[16]
    descriptor = data[17]

    pixels = data[18 + data[0]:]
    mode = "BGRA" if depth == 32 else "BGR"
    image = Image.frombytes("RGBA" if depth == 32 else "RGB", (width, height),
                            pixels[:width * height * (depth // 8)], "raw", mode)

    # El bit 5 del descriptor dice si la primera fila es la de arriba.
    if not (descriptor & 0x20):
        image = image.transpose(Image.FLIP_TOP_BOTTOM)

    return image.convert("RGBA")


def save_ozt(image: Image.Image, path: Path) -> None:
    """Escribe un OZT: cuatro bytes de encabezado y un TGA de 32 bits."""
    image = image.convert("RGBA")
    width, height = image.size

    header = bytes([
        0, 0, 2,              # sin id, sin paleta, RGB sin comprimir
        0, 0, 0, 0, 0,        # especificación de paleta, vacía
        0, 0, 0, 0,           # origen en (0, 0)
        width & 0xFF, width >> 8,
        height & 0xFF, height >> 8,
        32,                   # bits por píxel
        0x28,                 # alfa de 8 bits, primera fila arriba
    ])

    body = image.tobytes("raw", "BGRA")
    path.write_bytes(bytes(OZT_HEADER) + header + body)


def background_color(image: Image.Image) -> tuple[int, int, int]:
    """El color del fondo del mosaico, promediado de las cuatro esquinas."""
    rgb = image.convert("RGB")
    w, h = rgb.size
    esquinas = [rgb.getpixel(p) for p in
                ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1))]
    return tuple(sum(c[i] for c in esquinas) // 4 for i in range(3))


def convert(origen: Path, size: tuple[int, int]) -> Image.Image:
    """Centra el mosaico de 0.99B en un lienzo del tamaño de la casilla.

    Se centra en vez de estirar: el de 0.99B mide menos que la casilla y
    agrandarlo lo deja borroso. El relleno alrededor va del color de su propio
    fondo, así que la unión no se nota.
    """
    imagen = load_ozj(origen)
    lienzo = Image.new("RGBA", size, background_color(imagen) + (255,))
    lienzo.paste(imagen, ((size[0] - imagen.width) // 2,
                          (size[1] - imagen.height) // 2))
    return lienzo


def checkerboard(size: tuple[int, int]) -> Image.Image:
    """Fondo a cuadros para que se vea qué quedó transparente y qué no."""
    fondo = Image.new("RGBA", size, (90, 90, 90, 255))
    pixels = fondo.load()
    for y in range(size[1]):
        for x in range(size[0]):
            if ((x // 8) + (y // 8)) % 2 == 0:
                pixels[x, y] = (130, 130, 130, 255)
    return fondo


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--previsualizar", metavar="PNG",
                        help="arma una hoja de contactos y no toca nada mas")
    parser.add_argument("--instalar", action="store_true",
                        help="escribe los OZT, respaldando los originales")
    args = parser.parse_args()

    convertidas = []
    for nombre_origen, nombre_destino, size in SLOTS:
        origen = ORIGEN / nombre_origen
        if not origen.exists():
            print(f"  falta {nombre_origen}")
            continue
        convertidas.append((nombre_destino, convert(origen, size), size))

    print(f"{len(convertidas)} siluetas convertidas.")

    if args.previsualizar:
        # Tres columnas: como queda la de 0.99B, y al lado la de MuMain para
        # comparar. Sobre cuadros, para ver la transparencia.
        alto = max(s[1] for _, _, s in convertidas) + 8
        hoja = Image.new("RGBA", (240, alto * len(convertidas)), (40, 40, 40, 255))

        for i, (nombre, imagen, size) in enumerate(convertidas):
            y = i * alto + 4

            fondo = checkerboard(size)
            fondo.alpha_composite(imagen)
            hoja.alpha_composite(fondo, (10, y))

            actual = DESTINO / nombre
            if actual.exists():
                original = load_ozt(actual)
                base = checkerboard(original.size)
                base.alpha_composite(original)
                hoja.alpha_composite(base, (110, y))

        hoja.convert("RGB").save(args.previsualizar)
        print(f"Previsualizacion en {args.previsualizar} "
              f"(izquierda 0.99B convertida, derecha la actual de MuMain)")
        return 0

    if not args.instalar:
        print("(Sin --instalar ni --previsualizar no se hizo nada mas.)")
        return 0

    respaldo = DESTINO.parent / "Interface.s6"
    if not respaldo.exists():
        shutil.copytree(DESTINO, respaldo)
        print(f"Respaldo en {respaldo}")

    for nombre, imagen, _ in convertidas:
        save_ozt(imagen, DESTINO / nombre)

    print(f"{len(convertidas)} archivos escritos.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

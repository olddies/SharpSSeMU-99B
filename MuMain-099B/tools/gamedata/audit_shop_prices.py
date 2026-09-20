"""Compara el precio que el GameServer cobra con el que su propia tabla declara.

Las tiendas no mandan precios: `Data/Shop/*.txt` sólo lista qué vende cada NPC,
y el precio lo calcula cada lado por su cuenta. El cliente lo muestra
(`ItemValue`, ZzzInfomation.cpp:1457) y el servidor lo cobra
(`ComputeShopBuyPrice`/`ComputeShopSellPrice`, ClientProtocolHandler.cs).

El servidor trae una tabla de precios explícitos en `Data/Item/ItemValue.txt`
--joyas, alas, entradas de evento, pociones de asedio-- que es la que fija el
valor real de esos objetos en 0.99B.

**Durante un tiempo no la leyó nadie.** El nombre no aparecía en ningún `.cs`ni
en el `.ini`, así que esos objetos caían a la fórmula general de
`CItem::Value()`, que para ellos da cualquier cosa: el Jewel of Bless salía
18.700 en vez de 9.000.000. El cliente mostraba el número correcto, así que en
la tienda se veía uno y se cobraba otro. Ya está arreglado
(`SharpSSeMU/src/MuServer.GameServer/World/ItemValue.cs`), y esta herramienta
queda como guardia.

Lo que comprueba ahora:

1. Que las 72 filas del archivo apunten a items que existen en `Item.txt`.
2. Que ninguna quede tapada por un `BuyMoney` explícito, que se consulta antes.
3. Cuánto se aleja cada precio del que daría la fórmula general -- o sea, qué
   tan caro sale que la tabla vuelva a quedar desconectada.

    python tools/gamedata/audit_shop_prices.py
    python tools/gamedata/audit_shop_prices.py --todos

La fórmula de abajo es un puerto de `ComputeGeneralPrice`, y es la única parte
transcrita. El control de que está bien viene de afuera: da 18.700 para el
Jewel of Bless, que es exactamente lo que el servidor cobraba antes del arreglo
y lo que su test afirmaba.

Y ahí está la moraleja que conviene no perder: ese test daba por correcto un
precio que la tabla del mismo servidor declaraba en 9.000.000, porque estaba
escrito contra la implementación y no contra la fuente. El mismo error que el
test del serial de 17 bytes, que comparaba una implementación contra sí misma.
"""

import argparse
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DATOS = (REPO.parent / "SharpSSeMU" / "src" / "MuServer.GameServer"
         / "bin" / "Debug" / "net10.0" / "Data" / "Item")
ITEM = DATOS / "Item.txt"
VALORES = DATOS / "ItemValue.txt"

MAX_DINERO = 2_000_000_000


def redondeo_dos_etapas(v):
    """Primero múltiplo de 10 si >=100, DESPUES múltiplo de 100 si >=1000."""
    if v >= 100:
        v = (v // 10) * 10
    if v >= 1000:
        v = (v // 100) * 100
    return max(v, 0)


def redondeo_una_etapa(v):
    if v >= 10:
        v = (v // 10) * 10
    return max(v, 0)


def entero(texto, defecto=0):
    try:
        return int(texto)
    except (TypeError, ValueError):
        return defecto


def leer_item_txt(path):
    """{(seccion, sub): {columna: valor}}. Cada grupo trae su propio encabezado."""
    tabla = {}
    grupo = None
    columnas = None
    for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
        t = linea.strip()
        if not t:
            continue
        if t == "end":
            grupo = columnas = None
            continue
        if t.startswith("//"):
            columnas = t.lstrip("/").split()[1:]
            continue
        if grupo is None:
            if t.isdigit():
                grupo = int(t)
            continue
        if not columnas:
            continue

        sub = int(t.split()[0])
        resto = t.split(None, 1)[1]
        comillas = re.search(r'"([^"]*)"', resto)
        etiqueta = comillas.group(1) if comillas else ""
        partes = re.sub(r'"[^"]*"', "\x00", resto).split()
        valores = [etiqueta if p == "\x00" else p for p in partes]
        tabla[(grupo, sub)] = dict(zip(columnas, valores))
    return tabla


def leer_item_value(path):
    """Las filas de ItemValue.txt: (seccion, sub, nivel) -> dinero.

    El índice viene como `04,007`. El nivel y el grado pueden ser `*`, que
    significa "cualquiera"; el nivel comodín se guarda como None.
    """
    filas = []
    for linea in path.read_text(encoding="utf-8", errors="replace").splitlines():
        t = linea.strip()
        if not t or t.startswith("//") or t == "end":
            continue
        campos = t.split()
        if len(campos) < 4 or "," not in campos[0]:
            continue
        seccion, sub = (int(x) for x in campos[0].split(","))
        nivel = None if campos[1] == "*" else int(campos[1])
        comentario = re.search(r"//\s*(.*)$", t)
        filas.append({
            "seccion": seccion,
            "sub": sub,
            "nivel": nivel,
            "dinero": int(campos[3]),
            "nombre": comentario.group(1).strip() if comentario else "",
        })
    return filas


def precio_del_servidor(seccion, sub, fila, nivel_item=0, durabilidad=1):
    """Puerto de ComputeShopBuyPrice + ComputeGeneralPrice."""
    comprar = entero(fila.get("BuyMoney"))
    if comprar:
        return redondeo_dos_etapas(comprar)

    valor = entero(fila.get("Value"))
    if valor > 0:
        precio = (valor * valor * 10) // 12
        # The special branch for section 14 potions (sub 0-8), which scales by level and by quantity and rounds
        # in a single stage.
        if seccion == 14 and 0 <= sub <= 8:
            if sub in (3, 6):
                precio *= 2
            precio *= 1 << max(0, min(nivel_item, 15))
            precio *= max(durabilidad, 1)
            return redondeo_una_etapa(min(precio, MAX_DINERO))
        return redondeo_dos_etapas(min(precio, MAX_DINERO))

    nivel = entero(fila.get("Level")) + max(0, min(nivel_item, 15)) * 3
    nivel += {5: 4, 6: 10, 7: 25, 8: 45, 9: 65, 10: 95, 11: 135,
              12: 185, 13: 245, 14: 305, 15: 365}.get(nivel_item, 0)

    if seccion == 13:
        general = nivel ** 3 + 100
    elif seccion == 12:
        general = ((nivel + 40) * nivel) * nivel * 11 + 40_000_000
    else:
        general = ((nivel + 40) * nivel) * nivel // 8 + 100
        if 0 <= seccion <= 5 and fila.get("TwoHand") != "1":
            general = (general * 80) // 100

    return redondeo_dos_etapas(min(general, MAX_DINERO))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--todos", action="store_true",
                        help="lista tambien las filas que coinciden")
    args = parser.parse_args()

    for path in (ITEM, VALORES):
        if not path.exists():
            print("No encuentro %s" % path)
            return 1

    items = leer_item_txt(ITEM)
    valores = leer_item_value(VALORES)

    print("filas en ItemValue.txt: %d" % len(valores))
    print()

    huerfanas, tapadas = [], []
    detalle = []
    peor = None

    for fila in valores:
        clave = (fila["seccion"], fila["sub"])
        definicion = items.get(clave)

        if definicion is None:
            huerfanas.append(fila)
            continue

        # BuyMoney is consulted before this table, so a row whose item has it set would never be used.
        if entero(definicion.get("BuyMoney")):
            tapadas.append(fila)
            continue

        nivel = fila["nivel"] or 0
        sin_tabla = precio_del_servidor(fila["seccion"], fila["sub"],
                                        definicion, nivel)
        declarado = fila["dinero"]

        if sin_tabla == declarado:
            continue

        if sin_tabla:
            razon = declarado / sin_tabla
            nota = ("%.0fx mas barato sin la tabla" % razon if razon >= 1
                    else "%.0fx mas caro sin la tabla" % (1 / razon))
            if peor is None or razon > peor[0]:
                peor = (razon, fila["nombre"] or str(clave))
        else:
            nota = "sin la tabla daria 0"

        detalle.append(((fila["nombre"] or "%d,%d" % clave)[:26], fila["nivel"],
                        declarado, sin_tabla, nota))

    if args.todos and detalle:
        print("%-26s %6s %14s %14s   %s"
              % ("item", "nivel", "declarado", "sin la tabla", ""))
        for nombre, nivel, declarado, sin_tabla, nota in detalle:
            print("%-26s %6s %14s %14s   %s"
                  % (nombre, nivel if nivel is not None else "*",
                     "{:,}".format(declarado), "{:,}".format(sin_tabla), nota))
        print()

    print("filas que la tabla salva de la formula general: %d de %d"
          % (len(detalle), len(valores)))
    if peor:
        print("la mas cara de perder: %s, %.0f veces" % (peor[1], peor[0]))
    if detalle and not args.todos:
        print("(--todos para verlas una por una)")

    problemas = 0

    if huerfanas:
        problemas += len(huerfanas)
        print()
        print("Filas que apuntan a items que no existen en Item.txt: %d"
              % len(huerfanas))
        for fila in huerfanas:
            print("   %d,%d %s" % (fila["seccion"], fila["sub"], fila["nombre"]))

    if tapadas:
        problemas += len(tapadas)
        print()
        print("Filas que no se van a usar porque el item ya trae BuyMoney: %d"
              % len(tapadas))
        for fila in tapadas:
            print("   %d,%d %s" % (fila["seccion"], fila["sub"], fila["nombre"]))

    if not problemas:
        print()
        print("Sin problemas: las %d filas apuntan a items reales y ninguna "
              "queda tapada." % len(valores))

    return 1 if problemas else 0


if __name__ == "__main__":
    sys.exit(main())

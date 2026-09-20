"""e2e de tiendas de NPC: hablar, listar, comprar y vender.

También cubre la regresión de "solo veo el primer personaje": siembra un segundo
personaje en la MISMA cuenta para que el cliente tenga que leer el segundo
renglón de PMSG_CHARACTER_LIST_SEND con el stride correcto.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

pg = _env.Postgres()
dep = None
test_rc = 1

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("shop", pg)

    # El monstruo de prueba se spawnea PRIMERO (MonsterRegistry.SpawnAll) y se queda con el índice 0;
    # el NPC de tienda, spawneado después, con el índice 1 -- determinístico, y el bloque de ataque
    # del cliente compartido (que no está gateado) necesita al monstruo igual.
    dep.seed_test_monster()

    # Tienda de NPC: un solo NPC (clase 500, arbitraria -- SpawnNpc no depende de MonsterList.txt
    # para NPCs) en el mapa 0, con un único item en stock. No se recorta Item.txt: hacerlo dejaría
    # sin balance a la sección 0 y el paso de equipar, que corre antes, fallaría.
    #
    # El precio del "Jewel of Bless" (14,13) sale de Data/Item/ItemValue.txt, que lo declara en
    # 9.000.000 -- comprar 9.000.000, vender 3.000.000.
    #
    # Antes este test afirmaba 18.700 / 6.200, que es lo que da la fórmula general de
    # CItem::Value() a partir de la columna Value=150 de Item.txt. Y era lo que el servidor cobraba,
    # porque no cargaba ItemValue.txt: 72 filas de precios explícitos que no leía nadie. O sea que
    # el test estaba escrito contra la implementación y no contra la fuente, y fijaba como correcto
    # un precio 481 veces más bajo que el declarado. El cliente, mientras tanto, mostraba
    # 9.000.000 (los tiene escritos a mano en ItemValue, ZzzInfomation.cpp): en la tienda se veía un
    # número y se cobraba otro.
    (dep.game_dir / "Data" / "ShopManager.txt").write_text(
        '500   0   201   150   0   1   1   1   1   *   "TestShop"\nend\n', encoding="utf-8")
    shop_dir = dep.game_dir / "Data" / "Shop"
    shop_dir.mkdir(parents=True, exist_ok=True)
    (shop_dir / "TestShop.txt").write_text(
        "14,013   0   1   0   0   0   0   0   //Jewel of Bless\nend\n", encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9100, "test", "Hero1Two"),
                        (9101, "admin", "Hero2"))
    dep.place_heroes()
    dep.seed_equippable_weapon("Hero1")
    # Alcanza para comprar el Jewel of Bless a 9.000.000 y que sobre.
    pg.sql("UPDATE character SET money=20000000 WHERE name='Hero1';")
    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "shop", "Hero1Two",
        timeout=180).returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

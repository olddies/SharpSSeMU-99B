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

    # The test monster is spawned FIRST (MonsterRegistry.SpawnAll) and keeps index 0; the shop NPC, spawned
    # afterwards, gets index 1 -- deterministic, and the shared client's attack block (which is not gated) needs
    # the monster anyway.
    dep.seed_test_monster()

    # NPC shop: a single NPC (class 500, arbitrary -- SpawnNpc does not depend on MonsterList.txt for NPCs) on
    # map 0, with a single item in stock. Item.txt is not trimmed: doing so would leave section 0 without
    # balance and the equip step, which runs earlier, would fail. The price of the "Jewel of Bless" (14,13)
    # comes from Data/Item/ItemValue.txt, which declares it at 9,000,000 -- buying 9,000,000, selling 3,000,000.
    # Before, this test asserted 18,700 / 6,200, which is what the general CItem::Value() formula gives from the
    # Value=150 column of Item.txt. And it was what the server charged, because it did not load ItemValue.txt:
    # 72 rows of explicit prices that nobody read. That is, the test was written against the implementation and
    # not against the source, and it fixed as correct a price 481 times lower than the declared one. The client,
    # meanwhile, showed 9,000,000 (it has them written by hand in ItemValue, ZzzInfomation.cpp): in the shop one
    # number was seen and another was charged.
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
    # Enough to buy the Jewel of Bless at 9,000,000 with some left over.
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

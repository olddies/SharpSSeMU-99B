"""Trainer e2e: the pet mix of the Trainer's window, the pet it gives, and the repair of pets.

Hero1 has the ingredients of a Dark Raven in his inventory (a Jewel of Chaos, the raven's Spirit, a Jewel of Creation and two
of Bless and of Soul), a Dark Horse worn down to 100 durability, and plenty of zen; the Trainer stands next to him and the
mix is set to always work. Checked over the network: the Trainer opens its window, each ingredient goes into its box (item
move with flag 5), the mix gives a Dark Raven in the box (result 100), he takes it to his inventory, it starts at level 1, the
Trainer repairs the horse for zen, and away from the Trainer a pet is not repaired; and in the database afterwards, that the
new raven has a pet row of its own (the DataServer gave it a serial) and the horse was saved at full durability.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

HORSE_SERIAL = 5003

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("trainer", pg, game_port=55908,
                          join_port=55978, data_port=55968, connect_port=55565)

    dep.seed_test_monster()  # monster 0
    # The Trainer (class 226) right beside Hero1: NPC 1, after the test monster.
    spawn = dep.game_dir / "Data" / "Monster" / "Spawn" / "000 - Test.txt"
    spawn.write_text("0\n3    0    200    150    3\n226    0    199    151    3\nend\n", encoding="utf-8")

    # The pet mix always works.
    chaos_mix = dep.game_dir / "Data" / "GameServerInfo - ChaosMix.dat"
    text = chaos_mix.read_text(encoding="utf-8")
    for level in range(4):
        before = f"PetMixRate_AL{level} = 60"
        assert before in text, f"the ChaosMix.dat has no '{before}' to change"
        text = text.replace(before, f"PetMixRate_AL{level} = 100")
    chaos_mix.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    def jewel(index_low: int, level: int = 0, durability: int = 1) -> bytes:
        # The 16-byte database format: index low byte, level and options, durability, serial (none), the index's high bit.
        return bytes([index_low, level * 8, durability, 0, 0, 0, 0, 0x80])

    horse = bytes([0xA4, 0x00, 100]) + HORSE_SERIAL.to_bytes(4, "big") + bytes([0x80])
    items = {
        12: jewel(0x8F),             # Jewel of Chaos (12,15)
        13: jewel(0xBF, level=1, durability=255),  # Spirit of the Dark Raven (13,31), level 1
        14: jewel(0xD6),             # Jewel of Creation (14,22)
        15: jewel(0xCD), 16: jewel(0xCD),  # Jewel of Bless (14,13) x2
        17: jewel(0xCE), 18: jewel(0xCE),  # Jewel of Soul (14,14) x2
        22: horse,                   # the Dark Horse worn down
    }
    pg.sql("UPDATE character SET inventory = decode('" + _env.inventory_hex(items) + "','hex'), money=20000000, clevel=20 "
           "WHERE name='Hero1';")
    pg.sql(f"INSERT INTO pet_item_info (item_serial, pet_level, pet_exp) VALUES ({HORSE_SERIAL}, 2, 0);")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "trainer",
        timeout=120)
    test_rc = result.returncode

    time.sleep(1.5)
    rows = pg.sql("SELECT item_serial || '/' || pet_level FROM pet_item_info ORDER BY item_serial;").stdout
    print("pet rows:", rows)

    # The raven the mix made got the DataServer's first serial; the horse keeps its own.
    if "1/1" not in rows:
        failures.append("the new Dark Raven should have a pet row of its own at level 1")

    durability = pg.sql("SELECT get_byte(inventory, 22 * 16 + 2) FROM character WHERE name='Hero1';").stdout
    print("horse durability in the database:", durability)

    if "255" not in durability:
        failures.append("the repaired Dark Horse should have been saved at full durability")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

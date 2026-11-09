"""Dark Raven e2e: a Dark Lord orders his raven to attack at random and it kills a monster for him.

Hero1 is a Dark Lord who wears a Dark Raven (level 3, with the experience that level starts at) under a serial of its own,
and has the command it asks for. Three monsters stand two tiles away. Checked over the network: the DataServer gives the
raven its level, the order to attack at random is confirmed (C1:A7), everybody sees the raven attack a monster (C1:A8), the
monster dies and its death is credited to Hero1 (he gets the experience popup), the raven gets its share of that
experience (C1:A9) and the order to stand down is confirmed; and in the database afterwards, that the raven was saved with
the experience it gained when Hero1 left.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import re
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

SERIAL = 5002
LEVEL = 3
EXPERIENCE = 35_100  # the experience at which level 3 starts (13 * 3^3 * 100)

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []
output = ""

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("darkraven", pg, game_port=55906,
                          join_port=55976, data_port=55966, connect_port=55563)

    dep.seed_test_monster()
    dep.seed_drop_monsters()  # monsters 0, 1 and 2, two tiles from Hero1, with 30 life each
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # The Dark Raven (13,5 = item 421) in the left hand (slot 1), 255 durability, under its serial: the index low byte, the
    # level and options, the durability, the serial big-endian and the index's high bit in byte 7.
    raven = bytes([0xA5, 0x00, 0xFF]) + SERIAL.to_bytes(4, "big") + bytes([0x80])
    pg.sql("UPDATE character SET class=64, clevel=20, leadership=300, "
           "inventory = decode('" + _env.inventory_hex({1: raven}) + "','hex') WHERE name='Hero1';")
    pg.sql(f"INSERT INTO pet_item_info (item_serial, pet_level, pet_exp) VALUES ({SERIAL}, {LEVEL}, {EXPERIENCE});")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "raven", str(LEVEL), str(EXPERIENCE),
        timeout=150)
    test_rc = result.returncode
    output = result.stdout

    time.sleep(1.5)
    saved = pg.sql(f"SELECT pet_level || '/' || pet_exp FROM pet_item_info WHERE item_serial={SERIAL};").stdout
    print("pet row:", saved)

    match = re.search(r"RAVENEXP=(\d+)", output)

    if not match:
        failures.append("the client printed no RAVENEXP")
    elif f"{LEVEL}/{match.group(1)}" not in saved:
        failures.append(f"the raven should have been saved as level {LEVEL} with experience {match.group(1)}")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

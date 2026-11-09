"""Helper wear e2e: a monster hits a player who wears a Guardian Angel and the angel wears down.

Hero1 wears a Guardian Angel (durability 200) and has a lot of life; the test monster stands two tiles away and attacks him.
Checked over the network: the server tells Hero1 the durability of the angel going down (C1:2A, slot 8); and in the database
afterwards, that the angel was saved with the durability it had left when Hero1 left.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import re
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

DURABILITY = 200

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []
output = ""

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("helperwear", pg, game_port=55907,
                          join_port=55977, data_port=55967, connect_port=55564)

    dep.seed_test_monster()  # one Spider two tiles from Hero1
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # The Guardian Angel (13,0 = item 416) in the helper slot (8): the index low byte, the level and options, the durability,
    # the serial (none) and the index's high bit in byte 7. Plenty of vitality so the Spider does not kill him meanwhile.
    angel = bytes([0xA0, 0x00, DURABILITY, 0, 0, 0, 0, 0x80])
    pg.sql("UPDATE character SET inventory = decode('" + _env.inventory_hex({8: angel}) + "','hex'), vitality=300, clevel=20 "
           "WHERE name='Hero1';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "wear", str(DURABILITY),
        timeout=150)
    test_rc = result.returncode
    output = result.stdout

    time.sleep(1.5)
    saved = pg.sql("SELECT get_byte(inventory, 8 * 16 + 2) FROM character WHERE name='Hero1';").stdout
    print("angel durability in the database:", saved)

    match = re.search(r"ANGELDUR=(\d+)", output)

    if not match:
        failures.append("the client printed no ANGELDUR")
    else:
        stored = re.search(r"\n\s+(\d+)\s*\n", saved)

        # The monster keeps hitting until Hero1 is gone, so the angel may have lost a point more than the last one told.
        if not stored or not 0 < int(stored.group(1)) <= int(match.group(1)):
            failures.append(f"the angel should have been saved with {match.group(1)} durability or a little less, not {saved.strip()}")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

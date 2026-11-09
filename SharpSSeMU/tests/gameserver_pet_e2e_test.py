"""Pet e2e: a Dark Horse keeps its level and experience in the database and gains experience from a kill.

Hero1 wears a Dark Horse (level 4, with a given experience) that the test put in his inventory under a serial of its own
and in the pet table. Checked over the network: the DataServer gives the horse its level and experience back when the
client asks (C1:A9), and a kill gives the horse a tenth of the experience the player gets, in a pet info packet that goes
out just before the experience popup; and in the database afterwards, that the horse was saved with the new experience
when Hero1 left.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import re
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

SERIAL = 5001
LEVEL = 4
EXPERIENCE = 100_000  # level 4 starts at 89,600 and level 5 at 187,500

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []
output = ""

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("pet", pg, game_port=55905,
                          join_port=55975, data_port=55965, connect_port=55562)

    dep.seed_test_monster()
    dep.seed_drop_monsters()  # monster index 1: "Test Item Dropper", level 20, dies in one blow
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()  # (198,150): within reach of the monsters at (200,149..151)

    # The Dark Horse (13,4 = item 420) in the helper slot, 255 durability, under its serial: the 16-byte database
    # format has the index low byte, the level and options, the durability, the serial big-endian and the index's high
    # bit in byte 7.
    horse = bytes([0xA4, 0x00, 0xFF]) + SERIAL.to_bytes(4, "big") + bytes([0x80])
    pg.sql("UPDATE character SET inventory = decode('" + _env.inventory_hex({8: horse}) + "','hex'), "
           "clevel=20, strength=800 WHERE name='Hero1';")
    pg.sql(f"INSERT INTO pet_item_info (item_serial, pet_level, pet_exp) VALUES ({SERIAL}, {LEVEL}, {EXPERIENCE});")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "pet", str(LEVEL), str(EXPERIENCE),
        timeout=120)
    test_rc = result.returncode
    output = result.stdout

    time.sleep(1.5)
    saved = pg.sql(f"SELECT pet_level || '/' || pet_exp FROM pet_item_info WHERE item_serial={SERIAL};").stdout
    print("pet row:", saved)

    match = re.search(r"PETEXP=(\d+)", output)

    if not match:
        failures.append("the client printed no PETEXP")
    elif f"{LEVEL}/{match.group(1)}" not in saved:
        failures.append(f"the horse should have been saved as level {LEVEL} with experience {match.group(1)}")
    elif int(match.group(1)) <= EXPERIENCE:
        failures.append("the horse should have more experience than it began with")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

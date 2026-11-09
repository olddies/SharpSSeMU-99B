"""PK e2e: in the open, one player kills an innocent one and becomes a murderer.

Two level-20 characters stand in Lorencia outside the safe zone, with no monsters around. Hero1, with a lot of
strength, attacks Hero2 until Hero2 dies. Checked over the network: the self-defense notice, the PK level packet
(Hero1 is now a level-4 murderer) and Hero2's death with Hero1 as the killer; and in the database afterwards, the PK
level, kill count and PK time Hero1 kept.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import re
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("pk", pg, game_port=55903,
                          join_port=55973, data_port=55963, connect_port=55560)

    # No monsters: the real monster list, and an empty spawn file, so only the two players are in Lorencia.
    dep.copy_server_data("Monster", "MonsterList.txt")
    spawn_dir = dep.game_dir / "Data" / "Monster" / "Spawn"
    for old in spawn_dir.glob("*.txt"):
        old.unlink()
    (spawn_dir / "000 - Test.txt").write_text("0\nend\n", encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()  # (198,150) and (200,152): outside the safe zone of Lorencia, within reach of each other

    # Above level 5, which the original asks of both to fight; Hero1 hits hard enough to need a single blow.
    pg.sql("UPDATE character SET clevel=20, strength=800 WHERE name='Hero1';")
    pg.sql("UPDATE character SET clevel=20 WHERE name='Hero2';")

    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "pk",
        timeout=120).returncode

    time.sleep(1.5)
    rows = pg.sql("SELECT name || '=' || pk_level || '/' || pk_count || '/' || pk_time FROM character ORDER BY name;").stdout
    print("characters:", rows)

    hero1 = re.search(r"Hero1=(\d+)/(\d+)/(\d+)", rows)
    hero2 = re.search(r"Hero2=(\d+)/(\d+)/(\d+)", rows)

    if not hero1 or (int(hero1.group(1)), int(hero1.group(2))) != (4, 1):
        failures.append("Hero1 should be a level-4 murderer with one kill")
    elif not 21500 <= int(hero1.group(3)) <= 21600:
        failures.append(f"Hero1's PK time should be about six hours (21,600 s), it is {hero1.group(3)}")

    if not hero2 or (int(hero2.group(1)), int(hero2.group(2))) != (3, 0):
        failures.append("Hero2 was only killed: still a normal player")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

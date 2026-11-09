"""Duel e2e: one player asks another to duel in the open, wins the only point the test server asks for, and no PK follows.

Two level-20 characters stand in Lorencia outside the safe zone, with no monsters around. Hero1 asks Hero2 to duel,
Hero2 accepts, and Hero1 -- with a lot of strength -- hits Hero2 until it dies. The server is set to end a duel at
one point. Checked over the network: the request reaching Hero2, both told the duel starts, the score, Hero1 getting
no self-defense notice and no PK level, the end of the duel for both, and Hero2 coming back near where it fell and not
at the town's gate; and in the database afterwards, the duel ranking (Hero1 one win, Hero2 one loss) and that Hero1 is
still a normal player with no kill and no PK time.

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

    dep = _env.Deployment("duel", pg, game_port=55904,
                          join_port=55974, data_port=55964, connect_port=55561)

    # No monsters: the real monster list, and an empty spawn file, so only the two players are in Lorencia.
    dep.copy_server_data("Monster", "MonsterList.txt")
    spawn_dir = dep.game_dir / "Data" / "Monster" / "Spawn"
    for old in spawn_dir.glob("*.txt"):
        old.unlink()
    (spawn_dir / "000 - Test.txt").write_text("0\nend\n", encoding="utf-8")

    # One point wins the duel, so that a single kill shows the whole ending (the shipped value is ten).
    common = dep.game_dir / "Data" / "GameServerInfo - Common.dat"
    text = common.read_text(encoding="utf-8")
    assert "DuelMaxScore = 10" in text, "the Common.dat has no 'DuelMaxScore = 10' to change"
    assert "DuelSwitch = 1" in text, "the Common.dat should ship with the duels on"
    common.write_text(text.replace("DuelMaxScore = 10", "DuelMaxScore = 1"), encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()  # (198,150) and (200,152): outside the safe zone of Lorencia, within reach of each other

    # Above level 5, which the original asks of both to fight; Hero1 hits hard enough to need a single blow.
    pg.sql("UPDATE character SET clevel=20, strength=800 WHERE name='Hero1';")
    pg.sql("UPDATE character SET clevel=20 WHERE name='Hero2';")

    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "duel",
        timeout=120).returncode

    time.sleep(1.5)
    ranking = pg.sql("SELECT name || '=' || win_score || '/' || lose_score FROM ranking_duel ORDER BY name;").stdout
    print("duel ranking:", ranking)

    if "Hero1=1/0" not in ranking:
        failures.append("the winner, Hero1, should be in the duel ranking with one win and no loss")

    if "Hero2=0/1" not in ranking:
        failures.append("the loser, Hero2, should be in the duel ranking with no win and one loss")

    rows = pg.sql("SELECT name || '=' || pk_level || '/' || pk_count || '/' || pk_time FROM character ORDER BY name;").stdout
    print("characters:", rows)

    hero1 = re.search(r"Hero1=(\d+)/(\d+)/(\d+)", rows)

    if not hero1 or (int(hero1.group(1)), int(hero1.group(2)), int(hero1.group(3))) != (3, 0, 0):
        failures.append("a kill in a duel is no PK: Hero1 should still be a normal player with no kill and no PK time")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

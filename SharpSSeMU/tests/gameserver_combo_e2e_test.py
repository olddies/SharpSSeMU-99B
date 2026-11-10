"""Combo e2e: three blows of a Blade Knight in a row are a combo.

Hero1 is a Blade Knight who has finished his quest (the Dark Knight's combo asks for quest 3); Hero2 is one who has not. The test monster is the
Spider of the other tests with plenty of life, so the blows are told apart by their damage packets rather than by its death.
Checked over the network: Hero1's Falling Slash, Twisting Slash and Rageful Blow in a row make the third a combo (the damage packet carries
the combo flag and the server sends the combo's own skill echo, which Hero2, who watches, sees too); with another skill in the middle the
chain is broken and there is none; Hero2, who has not finished the quest, gets none from the same chain.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import re
import sys

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

    dep = _env.Deployment("combo", pg, game_port=55913,
                          join_port=55983, data_port=55973, connect_port=55568)

    dep.seed_test_monster()

    # The Spider (class 3) with 20000 life instead of 30, so that it takes every blow of the test and the damage packets can be read.
    monster_list = dep.game_dir / "Data" / "Monster" / "MonsterList.txt"
    text = monster_list.read_text(encoding="utf-8", errors="replace")
    text, changed = re.subn(r'(^3\s+0\s+"Spider"\s+2\s+)30(\s)', r'\g<1>20000\2', text, count=1, flags=re.M)

    if changed != 1:
        raise SystemExit("could not find the Spider row of MonsterList.txt")

    monster_list.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # Two Blade Knights (class 17 = Dark Knight, second class), strong enough for the skills' cost. Hero1 has finished quest 3: its two bits
    # (the top of the first byte) read 2, "finished".
    quest = "80" + "00" * 49
    pg.sql("UPDATE character SET class=17, clevel=150, strength=400, dexterity=200, energy=200, vitality=200 "
           f"WHERE name='Hero1';")
    pg.sql(f"UPDATE character SET quest = decode('{quest}', 'hex') WHERE name='Hero1';")
    pg.sql("UPDATE character SET class=17, clevel=150, strength=400, dexterity=200, energy=200, vitality=200 "
           "WHERE name='Hero2';")
    pg.sql(f"UPDATE character SET quest = decode('{'00' * 50}', 'hex') WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "combo",
        timeout=200)
    test_rc = result.returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

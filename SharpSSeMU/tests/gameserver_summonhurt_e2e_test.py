"""Summon-hurt e2e: the monster a summon hits answers, and the summon can die.

Hero1 is a Fairy Elf who calls her Goblin; the test monster is the Spider of the other tests with plenty of life and a bite hard enough to
kill a Goblin (45 life) in a few blows. Checked over the network: the Goblin goes for the Spider, the Spider answers it, Hero1's life bar for
the summon goes down (F3:20), everybody who sees the Goblin sees it die (C1:17), it leaves their views (C1:14), the bar is emptied, and Hero1
can call another.

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

    dep = _env.Deployment("summonhurt", pg, game_port=55919,
                          join_port=55989, data_port=55979, connect_port=55573)

    dep.seed_test_monster()

    # The Spider (class 3): 20000 life instead of 30, and a bite of 14 to 18 instead of 4 to 7.
    monster_list = dep.game_dir / "Data" / "Monster" / "MonsterList.txt"
    text = monster_list.read_text(encoding="utf-8", errors="replace")
    text, changed = re.subn(r'(^3\s+0\s+"Spider"\s+2\s+)30(\s+0\s+)4(\s+)7(\s)', r'\g<1>20000\g<2>14\g<3>18\4', text, count=1, flags=re.M)

    if changed != 1:
        raise SystemExit("could not find the Spider row of MonsterList.txt")

    monster_list.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    pg.sql("UPDATE character SET class=32, clevel=150, strength=100, dexterity=300, energy=300, vitality=100, mana=3000, bp=500 "
           "WHERE name='Hero1';")
    pg.sql("UPDATE character SET class=0, clevel=150, strength=100, dexterity=100, energy=100, vitality=300 WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "summonhurt",
        timeout=200)
    test_rc = result.returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

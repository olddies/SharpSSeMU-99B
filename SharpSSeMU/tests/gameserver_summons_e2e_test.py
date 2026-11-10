"""Summons e2e: the Fairy Elf calls her Goblin.

Hero1 is a Fairy Elf who knows Summon Goblin; Hero2 stands by. The test monster is the Spider of the other tests with plenty of life, so the
summon's blows are told apart by their damage numbers rather than by its death. Checked over the network: the cast brings the Goblin into
both players' view in the summon list (with the magic circle, the class of the Goblin and Hero1's name) and tells Hero1 its life; the
Goblin goes for the monster and Hero1 (not Hero2) sees its damage numbers; Hero2 cannot hurt it; and a second cast sends it away -- it
leaves both views and Hero1's bar is emptied.

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

    dep = _env.Deployment("summons", pg, game_port=55917,
                          join_port=55987, data_port=55977, connect_port=55572)

    dep.seed_test_monster()

    # The Spider (class 3) with 20000 life instead of 30, so that the summon's blows can be told apart from its death.
    monster_list = dep.game_dir / "Data" / "Monster" / "MonsterList.txt"
    text = monster_list.read_text(encoding="utf-8", errors="replace")
    text, changed = re.subn(r'(^3\s+0\s+"Spider"\s+2\s+)30(\s)', r'\g<1>20000\2', text, count=1, flags=re.M)

    if changed != 1:
        raise SystemExit("could not find the Spider row of MonsterList.txt")

    monster_list.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # A Fairy Elf (class 32) strong enough for the skill's level and mana; Hero2 is a bystander.
    pg.sql("UPDATE character SET class=32, clevel=150, strength=100, dexterity=300, energy=300, vitality=100, mana=3000, bp=500 "
           "WHERE name='Hero1';")
    pg.sql("UPDATE character SET class=0, clevel=150, strength=100, dexterity=100, energy=100, vitality=100 WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "summon",
        timeout=200)
    test_rc = result.returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

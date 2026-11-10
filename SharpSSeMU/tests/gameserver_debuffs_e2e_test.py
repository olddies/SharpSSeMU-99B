"""Debuffs e2e: the skills that leave something on the one they hit -- the poison, the ice and the arrow that roots -- on a monster.

The test monster is the Spider of the other tests with plenty of life (the poison takes three percent of what is left, so a Spider
of 30 would feel nothing). Hero1 is a Dark Wizard, Hero2 a Muse Elf, both with the energy and the level the skills ask for.
Checked over the network: Hero1's Poison puts the poison on the monster (C1:07 to whoever sees it, the skill echo with its success
flag), keeps taking life every two seconds as damage numbers of the poison's colour for Hero1 alone, and ends by itself after
twenty seconds; her Ice joins it, a group of its own; Hero2's Ice Arrow roots the monster, which does not move while it lasts.

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

    dep = _env.Deployment("debuffs", pg, game_port=55911,
                          join_port=55981, data_port=55971, connect_port=55567)

    dep.seed_test_monster()

    # The Spider (class 3) with 20000 life instead of 30, so that three percent of it is something to see.
    monster_list = dep.game_dir / "Data" / "Monster" / "MonsterList.txt"
    text = monster_list.read_text(encoding="utf-8", errors="replace")
    text, changed = re.subn(r'(^3\s+0\s+"Spider"\s+2\s+)30(\s)', r'\g<1>20000\2', text, count=1, flags=re.M)

    if changed != 1:
        raise SystemExit("could not find the Spider row of MonsterList.txt")

    monster_list.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # Dark Wizard (Poison, Ice) and Muse Elf (Ice Arrow asks for the second class: 33 is 32 + 1), strong enough for the mana and the level.
    pg.sql("UPDATE character SET class=0, clevel=60, energy=400 WHERE name='Hero1';")
    pg.sql("UPDATE character SET class=33, clevel=60, energy=400 WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "debuff",
        timeout=200)
    test_rc = result.returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

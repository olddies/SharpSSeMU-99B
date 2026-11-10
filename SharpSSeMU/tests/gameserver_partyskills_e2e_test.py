"""Party skills e2e: the Soul Master's Teleport Ally and the Dark Lord's Summon Party.

Hero1 is a Dark Lord, Hero2 a Soul Master (Teleport Ally asks for the second class), both with the mana and the level the skills ask for, in
a party. Checked over the network: Hero2's Teleport Ally sends him six tiles away (the skill echo, seen by Hero1 too, and the new position),
and eight tiles or more is refused with nothing sent; Hero1's Summon Party tells Hero2 the seconds left five times, shows the cast and
brings Hero2 within four tiles of Hero1; and when Hero2 walks during the countdown it is called off and he stays where he is.

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

    dep = _env.Deployment("partyskills", pg, game_port=55915,
                          join_port=55985, data_port=55975, connect_port=55569)

    dep.seed_test_monster()

    # Summon Party waits 30 seconds between casts; the test calls it twice in a row.
    skill_list = dep.game_dir / "Data" / "Skill" / "SkillList.txt"
    text = skill_list.read_text(encoding="utf-8", errors="replace")
    text, changed = re.subn(r'(^63\s+"Summon Party"\s+0\s+70\s+30\s+0\s+0\s+)30000(\s)', r'\g<1>0\2', text, count=1, flags=re.M)

    if changed != 1:
        raise SystemExit("could not find the Summon Party row of SkillList.txt")

    skill_list.write_text(text, encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # A Dark Lord (class 64) and a Soul Master (class 1 = Dark Wizard, second class), with mana and AG for the skills.
    pg.sql("UPDATE character SET class=64, clevel=150, strength=300, dexterity=200, energy=400, vitality=200, mana=3000, bp=1000 "
           "WHERE name='Hero1';")
    pg.sql("UPDATE character SET class=1, clevel=150, strength=100, dexterity=100, energy=400, vitality=100, mana=3000, bp=1000 "
           "WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "partyskills",
        timeout=200)
    test_rc = result.returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

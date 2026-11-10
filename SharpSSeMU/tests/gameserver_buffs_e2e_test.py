"""Buffs e2e: the skills that help, the effects they put on players, and what the database keeps of them.

Hero1 is a Fairy Elf (energy 300) whose row carries a Mana Shield that was running when she last left; Hero2 is a Dark Wizard.
Checked over the network: the Mana Shield comes back when Hero1 enters the world; Hero1's Greater Damage on Hero2, her Greater
Defense and her Heal on herself each send what the original sends (the skill echo, the cost, the effect packet C1:07 to the
target and to whoever sees them, the numbers worked out again, her life); a skill that is not an Elf's does nothing; Hero2's
Mana Shield starts and, cancelled, ends. And in the database afterwards: Hero1's row keeps the restored Mana Shield (with some
of its time gone) and her Greater Defense, Hero2's keeps the Greater Damage with the value the energy gives, and the cancelled
shield is not there.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

ENERGY = 300
MANA_SHIELD, GREATER_DAMAGE, GREATER_DEFENSE = 4, 1, 2

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []


def records(hex_text: str) -> list[dict]:
    """The sixteen 13-byte effect records of a row: index, time left, the four powers."""
    raw = bytes.fromhex(hex_text.strip())
    out = []
    for n in range(16):
        r = raw[n * 13:(n + 1) * 13]
        if len(r) < 13 or r[0] == 0xFF:
            out.append(None)
        else:
            out.append({"index": r[0], "count": int.from_bytes(r[1:5], "big"), "value": int.from_bytes(r[5:7], "big")})
    return out


def kept(name: str) -> list[dict]:
    rows = pg.sql(f"SELECT encode(effect_list, 'hex') FROM character WHERE name='{name}';").stdout.splitlines()
    for line in rows:
        line = line.strip()
        if len(line) == 416:
            return [r for r in records(line) if r]
    return []


try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("buffs", pg, game_port=55909,
                          join_port=55979, data_port=55969, connect_port=55566)

    dep.seed_test_monster()
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # A Mana Shield of 100 seconds and power 20 that was running when Hero1 left (the 13-byte record, then empty slots).
    shield = bytes([MANA_SHIELD]) + (100).to_bytes(4, "big") + (20).to_bytes(2, "big") + bytes(6)
    row = shield + b"\xff" * (208 - len(shield))
    pg.sql(f"UPDATE character SET class=32, clevel=20, energy={ENERGY}, effect_list = decode('{row.hex()}', 'hex') "
           "WHERE name='Hero1';")
    pg.sql(f"UPDATE character SET class=0, clevel=20, energy={ENERGY} WHERE name='Hero2';")

    dep.start_game()

    result = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "buff",
        timeout=150)
    test_rc = result.returncode

    time.sleep(1.5)

    hero1 = kept("Hero1")
    hero2 = kept("Hero2")
    print("Hero1 keeps:", hero1)
    print("Hero2 keeps:", hero2)

    # Hero1: the shield that came back (it has lost some of its 100 seconds) and the Greater Defense she cast on herself,
    # whose power is twice (2 + energy / 8).
    shield_row = next((r for r in hero1 if r["index"] == MANA_SHIELD), None)
    defense_row = next((r for r in hero1 if r["index"] == GREATER_DEFENSE), None)

    if shield_row is None or not 0 < shield_row["count"] < 100 or shield_row["value"] != 20:
        failures.append(f"Hero1 should keep the restored Mana Shield with some of its time gone, got {shield_row}")

    if defense_row is None or defense_row["value"] != (2 + ENERGY // 8) * 2:
        failures.append(f"Hero1 should keep a Greater Defense of {(2 + ENERGY // 8) * 2}, got {defense_row}")

    # Hero2: the Greater Damage Hero1 cast on him (3 + energy / 7) and no shield (he cancelled it).
    damage_row = next((r for r in hero2 if r["index"] == GREATER_DAMAGE), None)

    if damage_row is None or damage_row["value"] != 3 + ENERGY // 7:
        failures.append(f"Hero2 should keep a Greater Damage of {3 + ENERGY // 7}, got {damage_row}")

    if any(r["index"] == MANA_SHIELD for r in hero2):
        failures.append("Hero2 cancelled his Mana Shield, so it should not have been saved")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

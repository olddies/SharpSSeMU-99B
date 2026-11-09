"""Chaos Castle e2e: two players pay to go in, wait for the start and one kills the other.

The whole event runs on a ChaosCastle.dat of TEST timing (no notification wait, a one-minute fight, no closing
wait) and starts with the console command 'cc forcestart', like the Devil Square run. The Chaos Castle has no
monsters here (the stage spawn file is emptied), so the last player standing wins at once. It covers the entry,
the start, player against player combat over the network, the death with its killer, the score windows and the
ranking and the position kept in the database.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

# GET_ITEM(13,29) = 13*32+29 = 445 -- "Armor of Guardsman", the entrance ticket.
ARMOR_OF_GUARDSMAN = 13 * 32 + 29

pg = _env.Postgres()
dep = None
test_rc = 1
failures = []

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("chaoscastle", pg, game_port=55902,
                          join_port=55972, data_port=55962, connect_port=55559)

    for terrain in ("Terrain19.att", "Terrain3.att"):
        dep.copy_server_data("Terrain", terrain)

    # The castle's data: the real entry levels, an empty monster list for its stages, and a test ChaosCastle.dat:
    # the wait is 0 minutes, the fight 1 minute, the closing 0. The start time of the file is a distant
    # placeholder; 'cc forcestart' injects the real one. Two players are needed to start, and nobody wins the
    # pot of the entry fees (ChaosCastleMoneyRate 0), so the zen left is exactly what the fee leaves.
    dep.copy_server_data("Event", "EventEntryLevel.dat")
    event_dir = dep.game_dir / "Data" / "Event"
    (event_dir / "EventStageSpawn.dat").write_text("0\nend\n\n1\nend\n\n2\nend\n", encoding="utf-8")
    (event_dir / "ChaosCastle.dat").write_text(
        "0\n5 0 1 0\nend\n\n"
        "1\n2099 1 1 * 0 0 0\nend\n\n"
        "2\n0 1000 500\n1 1500 100\n2 2000 1500\n3 2500 2000\n4 3000 2500\n5 3500 3000\nend\n",
        encoding="utf-8")

    settings = dep.game_dir / "Data" / "GameServerInfo - Event.dat"
    text = settings.read_text(encoding="utf-8")
    for before, after in (("ChaosCastleMinUser = 1", "ChaosCastleMinUser = 2"),
                          ("ChaosCastleMoneyRate = 80", "ChaosCastleMoneyRate = 0")):
        assert before in text, f"the Event.dat has no '{before}' to change"
        text = text.replace(before, after)
    settings.write_text(text, encoding="utf-8")

    # Everybody who enters appears inside a 2x2 box of the first castle, so that they are within reach of each
    # other (the real box is 6x8).
    gates = dep.game_dir / "Data" / "Move" / "Gate.txt"
    rows = gates.read_text(encoding="utf-8", errors="replace").splitlines()
    patched = []
    for row in rows:
        fields = row.split()
        if len(fields) >= 7 and fields[0] == "82":
            fields[3:7] = ["31", "88", "32", "89"]
            row = "   ".join(fields)
        patched.append(row)
    gates.write_text("\n".join(patched), encoding="utf-8")

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()

    # Level 20 falls in the first castle ([15,49]). Hero1 has a lot of strength so that a few blows kill Hero2
    # even through the castle's damage rate; both carry the Armor of Guardsman in slot 13 and the zen for the fee.
    armor = _env.inventory_hex({13: _env.item_bytes(ARMOR_OF_GUARDSMAN, durability=1)})
    pg.sql("UPDATE character SET clevel=20, strength=800, money=100000, inventory = decode('"
           + armor + "','hex') WHERE name='Hero1';")
    pg.sql("UPDATE character SET clevel=20, money=100000, inventory = decode('"
           + armor + "','hex') WHERE name='Hero2';")

    dep.start_game()

    game = dep.servers.procs["game"]
    time.sleep(1.0)
    game.stdin.write("cc forcestart 1 30\n")
    game.stdin.flush()
    print("'cc forcestart 1 30' enviado por consola")

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", "chaoscastle",
        timeout=280).returncode

    # What the server kept: the winner's score in the ranking (a fifth of the 1,000 experience of the kill), the
    # fee that was charged and where each of them ended up (the winner leaves the castle for Devias, map 2, when it
    # closes; the loser respawns in Lorencia, map 0).
    time.sleep(1.5)
    ranking = pg.sql("SELECT name || '=' || score FROM ranking_chaos_castle ORDER BY name;").stdout
    print("ranking_chaos_castle:", ranking)
    if "Hero1=200" not in ranking:
        failures.append("the winner's score (200) is not in ranking_chaos_castle")

    rows = pg.sql("SELECT name || '=' || money || '/' || map_number FROM character ORDER BY name;").stdout
    print("characters:", rows)
    if "Hero1=75000/2" not in rows:
        failures.append("Hero1 should have paid the fee and be back in Devias (75000/map 2)")
    if "Hero2=75000/0" not in rows:
        failures.append("Hero2 should have paid the fee and respawned in Lorencia (75000/map 0)")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

for failure in failures:
    print("FALLO:", failure)

sys.exit(test_rc if not failures else 1)

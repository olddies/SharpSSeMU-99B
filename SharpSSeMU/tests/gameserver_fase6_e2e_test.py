"""Fase 6 e2e: eventos especiales -- Devil Square de punta a punta.

Corre el ciclo completo del evento (abrir -> STAND -> START -> CLEAN) con un
DevilSquare.dat de PRUEBA de 1 minuto por etapa en vez de los 1/20/4 reales, y
dispara la apertura con el comando de consola 'ds forcestart' en vez de esperar
un horario. Cubre entrada por ticket, spawn por etapa, puntaje y ranking.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

# GET_ITEM(14,19) = 14*32+19 = 467 -- "Devil's Invitation", el ticket de entrada.
DEVIL_INVITATION = 14 * 32 + 19

pg = _env.Postgres()
dep = None
test_rc = 1

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("fase6", pg, game_port=55901,
                          join_port=55971, data_port=55961, connect_port=55558)

    # Mapas: Lorencia (combate del flujo compartido), Devil Square (10) y Noria (4).
    for terrain in ("Terrain10.att", "Terrain4.att"):
        dep.copy_server_data("Terrain", terrain)

    # Monsters: the map 0 test one (shared flow) plus the REAL pool of Type==4 positions that DevilSquareManager
    # uses to instantiate at runtime.
    dep.seed_test_monster()
    dep.copy_server_data("Monster", "Spawn", "009 - Devil Square 1.txt")

    # Real event data (brackets and classes) + a test DevilSquare.dat with NotifyTime=EventTime=CloseTime=1
    # minute (the minimum representable: the real format is always in whole minutes) so that the whole cycle
    # runs in a couple of minutes instead of ~25. The section 1 schedule is a distant placeholder -- the opening
    # is triggered by 'ds forcestart', which injects its own entry.
    dep.copy_server_data("Event", "EventEntryLevel.dat")
    dep.copy_server_data("Event", "EventStageSpawn.dat")
    (dep.game_dir / "Data" / "Event" / "DevilSquare.dat").write_text(
        "0\n5 1 1 1\nend\n\n"
        "1\n2099 1 1 * 0 0 0\nend\n\n"
        "2\n0 6000 4000 4000 2000 2000 2000 1000 1000 1000 1000\nend\n\n"
        "3\n0 30000 25000 25000 20000 20000 20000 15000 15000 15000 15000\nend\n",
        encoding="utf-8")

    dep.start_infra()

    pg.sql("INSERT INTO memb_info (account, password, owner_name) "
           "VALUES ('newacc', 'newacc', 'SSeMU') ON CONFLICT (account) DO NOTHING;")
    pg.sql("INSERT INTO memb_info (account, password, owner_name) "
           "VALUES ('test3', 'test3', 'SSeMU') ON CONFLICT (account) DO NOTHING;")

    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"),
                        (9102, "test3", "Hero3"))
    dep.place_heroes()
    dep.seed_equippable_weapon("Hero1")

    # Hero3 goes DEDICATED to the Devil Square section, in its own account, so as not to alter the combat timing
    # that the shared flow (phases 4/5) assumes. Level 15 falls within [10,99], the "Common" range of Devil
    # Square 1 in EventEntryLevel.dat. High Strength because the event's real monsters (Skeleton Archer /
    # Cyclops: HP 850-1100, Defense 35-45) are unbeatable with the Strength of a newly created character under
    # this phase's placeholder damage formula -- technical debt already documented in the README.
    pg.sql("UPDATE character SET map_number=0, map_pos_x=210, map_pos_y=150, "
           "clevel=15, strength=800, inventory = decode('"
           + _env.inventory_hex({13: _env.item_bytes(DEVIL_INVITATION, level=1, durability=1)})
           + "','hex') WHERE name='Hero3';")

    dep.start_game()

    # The GameServer reads commands from its console; 'ds forcestart' injects an immediate schedule instead of
    # making the test wait for the next one in the file.
    game = dep.servers.procs["game"]
    time.sleep(1.0)
    game.stdin.write("ds forcestart\n")
    game.stdin.flush()
    print("'ds forcestart' enviado por consola")

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2",
        "newacc", "newacc", "NewHero", "test3", "test3", "Hero3",
        timeout=400).returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

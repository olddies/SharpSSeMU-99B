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

    # Monstruos: el de prueba del mapa 0 (flujo compartido) más el pool REAL de
    # posiciones Type==4 que DevilSquareManager usa para instanciar en runtime.
    dep.seed_test_monster()
    dep.copy_server_data("Monster", "Spawn", "009 - Devil Square 1.txt")

    # Datos de evento reales (brackets y clases) + un DevilSquare.dat de prueba con
    # NotifyTime=EventTime=CloseTime=1 minuto (el mínimo representable: el formato
    # real es siempre en minutos enteros) para que el ciclo entero corra en un par
    # de minutos en vez de ~25. El horario de la sección 1 es un placeholder lejano
    # -- la apertura la dispara 'ds forcestart', que inyecta su propia entrada.
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

    # Hero3 va DEDICADO a la sección de Devil Square, en su propia cuenta, para no
    # alterar la temporización de combate que el flujo compartido (fases 4/5) asume.
    # Nivel 15 cae dentro de [10,99], el rango "Common" de Devil Square 1 en
    # EventEntryLevel.dat. Strength alto porque los monstruos reales del evento
    # (Skeleton Archer / Cyclops: HP 850-1100, Defense 35-45) son intratables con el
    # Strength de un personaje recién creado bajo la fórmula de daño placeholder de
    # esta fase -- deuda técnica ya documentada en el README.
    pg.sql("UPDATE character SET map_number=0, map_pos_x=210, map_pos_y=150, "
           "clevel=15, strength=800, inventory = decode('"
           + _env.inventory_hex({13: _env.item_bytes(DEVIL_INVITATION, level=1, durability=1)})
           + "','hex') WHERE name='Hero3';")

    dep.start_game()

    # El GameServer lee comandos de su consola; 'ds forcestart' inyecta un horario
    # inmediato en vez de hacer al test esperar al próximo del archivo.
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

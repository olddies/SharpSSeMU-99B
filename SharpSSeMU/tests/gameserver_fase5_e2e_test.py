"""Fase 5 e2e: chat, party, amigos y creación de personaje vía GameServer.

Levanta PostgreSQL + JoinServer + DataServer + GameServer y corre WorldTestClient
con tres cuentas: dos con personaje sembrado y una vacía, para ejercitar el flujo
real de creación de personaje (F3:01).

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import sys

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "WorldTestClient")

pg = _env.Postgres()
dep = None
test_rc = 1

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("fase5", pg)

    dep.seed_test_monster()

    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()
    dep.seed_equippable_weapon("Hero1")

    # Cuenta NUEVA sin personajes -- para el flujo real de creación vía GameServer (bug reportado
    # probando con el cliente real: F3:01 no estaba implementado y el cliente quedaba pegado en la
    # pantalla de creación).
    pg.sql("INSERT INTO memb_info (account, password, owner_name) "
           "VALUES ('newacc', 'newacc', 'SSeMU') ON CONFLICT (account) DO NOTHING;")

    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2",
        "newacc", "newacc", "NewHero").returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

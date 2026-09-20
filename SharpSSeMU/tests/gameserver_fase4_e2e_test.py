"""Fase 4 e2e: monstruos y combate.

Hero1 ataca al monstruo de prueba sembrado hasta matarlo y verifica el ciclo de
daño / muerte / experiencia / respawn.

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

    dep = _env.Deployment("fase4", pg)
    dep.seed_test_monster()
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()
    dep.seed_equippable_weapon("Hero1")
    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", timeout=120).returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

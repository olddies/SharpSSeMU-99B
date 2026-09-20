"""Fase 3 e2e: items e inventario (formato de item, lista, mover/equipar).

Siembra una espada real en el primer slot de mochila de Hero1 y verifica el
ciclo ITEM_LIST / ITEM_MOVE / ITEM_EQUIPMENT contra los tres servidores.

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

# (el item concreto lo pone _env.Deployment.seed_equippable_weapon)
FIRST_BACKPACK_SLOT = 12  # los 12 primeros son los slots de equipo

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("fase3", pg)
    dep.seed_test_monster()
    dep.start_infra()
    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))

    # Mismo mapa y a 2 tiles: el viewport (rango 12) los ve mutuamente sin
    # depender del spawn por defecto.
    dep.place_heroes()

    # Sembrar la espada para poder probar equipar sin depender todavía de recoger
    # del suelo. Sobrescribe el inventario entero, así que el equipo por defecto
    # de la clase desaparece: el único item es el que el test va a mover.
    dep.seed_equippable_weapon("Hero1", FIRST_BACKPACK_SLOT)

    r = pg.sql("SELECT name,map_number,map_pos_x,map_pos_y FROM character;")
    print("personajes:", r.stdout.strip())

    dep.start_game()

    test_rc = dep.run_world_test_client(
        "test", "test", "Hero1", "admin", "admin", "Hero2", timeout=120).returncode

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

"""Levanta la pila completa de servidores y la deja corriendo.

No es un test: sirve para probar a mano con el cliente real. Usa la misma
infraestructura que los tests end-to-end (cluster PostgreSQL desechable, puertos
libres, configuración generada), imprime los datos de conexión y se queda
esperando hasta que se lo corte.

    python tests/run_stack.py
"""

import sys
import time

import _env

CONNECT_TCP = 44405

_env.preflight()
_env.ensure_built("MuServer.ConnectServer", "MuServer.JoinServer",
                  "MuServer.DataServer", "MuServer.GameServer")

pg = _env.Postgres()
dep = None

try:
    pg.start()
    pg.seed_schema()
    print("base de datos lista")

    dep = _env.Deployment("manual", pg)
    dep.seed_test_monster()
    dep.start_connect(CONNECT_TCP)
    dep.start_infra()

    dep.seed_characters((9100, "test", "Hero1"), (9101, "admin", "Hero2"))
    dep.place_heroes()
    dep.seed_equippable_weapon("Hero1")

    dep.start_game()

    print()
    print("=" * 62)
    print("  Pila lista. Para conectar el cliente:")
    print()
    print(f"    Main.exe connect /u127.0.0.1 /p{dep.connect_tcp_port}")
    print()
    print(f"  ConnectServer TCP : {dep.connect_tcp_port}")
    print(f"  GameServer        : {dep.game_port}")
    print(f"  JoinServer        : {dep.join_port}")
    print(f"  DataServer        : {dep.data_port}")
    print(f"  PostgreSQL        : {pg.port}")
    print()
    print("  Cuentas: test/test (Hero1) y admin/admin (Hero2)")
    print("=" * 62)
    print()
    print("Ctrl+C para bajar todo.")

    while True:
        muertos = dep.servers.report_deaths()
        if muertos:
            print(f"\n¡Se cayó: {', '.join(muertos)}!")
            break
        time.sleep(2)

except KeyboardInterrupt:
    print("\nbajando la pila...")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(0)

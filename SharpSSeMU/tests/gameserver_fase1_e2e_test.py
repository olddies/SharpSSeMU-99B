"""Fase 1 e2e: núcleo de conexión y login contra el GameServer.

Corre TestClient (login cifrado auténtico, mismas claves reales) contra cinco
escenarios: credenciales válidas, password inválido, cuenta inexistente,
re-login tras desconexión limpia, y password inválido en cuenta sin sesión
previa. Falla si el login válido no es aceptado.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import subprocess
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.JoinServer", "MuServer.DataServer",
                  "MuServer.GameServer", "TestClient")

pg = _env.Postgres()
dep = None
test_rc = 1

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("fase1", pg, game_port=55901, join_port=55906)
    dep.start_infra()
    dep.start_game()

    tc = _env.ROOT / "src" / "TestClient" / "bin" / "Debug" / _env.TFM / "TestClient.dll"

    def run_client(account: str, password: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [_env.DOTNET, str(tc), "127.0.0.1", str(dep.game_port),
             _env.SERIAL, _env.VERSION,
             _env.client_data("Enc1.dat"), _env.client_data("Dec2.dat"),
             account, password],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=30)

    scenarios = [
        ("TEST 1: login válido (test/test)", "test", "test"),
        ("TEST 2: password inválido (test/wrongpass)", "test", "wrongpass"),
        ("TEST 3: cuenta inexistente (nope12345/nope)", "nope12345", "nope"),
        ("TEST 4: re-login tras desconexión limpia (test/test)", "test", "test"),
        ("TEST 5: password inválido en cuenta nueva (admin/wrongpass)", "admin", "wrongpass"),
    ]

    results = []
    for title, account, password in scenarios:
        print(f"\n=== {title} ===")
        time.sleep(0.5)
        r = run_client(account, password)
        print(r.stdout)
        print(r.stderr)
        results.append((title, r.returncode, r.stdout))

    # El caso que tiene que quedar verde es el login válido: los negativos solo se
    # inspeccionan por log (TestClient no distingue el motivo en su código de salida).
    valid_logins = [r for t, r, _out in results if t.startswith(("TEST 1", "TEST 4"))]
    test_rc = 0 if all(rc == 0 for rc in valid_logins) else 1

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

sys.exit(test_rc)

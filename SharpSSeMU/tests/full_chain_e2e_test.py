"""e2e de la cadena completa: ConnectServer -> JoinServer -> DataServer -> GameServer.

Verifica lo que haría el cliente real al arrancar: pedirle al ConnectServer la
lista de servidores (0xF4:02), resolver el ServerCode a IP:puerto (0xF4:03) y
después loguear contra ese GameServer con el paquete cifrado auténtico.

Requisitos: PostgreSQL y el SDK de .NET instalados. Ver tests/README.md.
"""

import socket
import struct
import subprocess
import sys
import time

import _env

_env.preflight()
_env.ensure_built("MuServer.ConnectServer", "MuServer.JoinServer",
                  "MuServer.DataServer", "MuServer.GameServer", "TestClient")

CONNECT_TCP = 44405  # preferido; Deployment.start_connect reserva uno libre si esta tomado

pg = _env.Postgres()
dep = None
failures = []

try:
    pg.start()
    pg.seed_schema()
    print("seed OK")

    dep = _env.Deployment("fullchain", pg)
    dep.start_connect(CONNECT_TCP)
    connect_tcp = dep.connect_tcp_port
    dep.start_infra()
    dep.start_game()

    # El GameServer late hacia el ConnectServer una vez por segundo (0xA1); hay que
    # darle tiempo antes de preguntar por la lista.
    time.sleep(3.0)

    def c1sub(head: int, sub: int, payload: bytes = b"") -> bytes:
        return bytes([0xC1, 4 + len(payload), head, sub]) + payload

    s = socket.create_connection(("127.0.0.1", connect_tcp), timeout=5)
    s.settimeout(5)
    try:
        time.sleep(0.3)
        print("ConnectServer al conectar (init + namelist):", s.recv(4096).hex())

        s.sendall(c1sub(0xF4, 0x02))  # lista de servidores
        time.sleep(0.3)
        resp = s.recv(4096)
        print("Respuesta 0xF4:02:", resp.hex())
        # C2 header(4) + count(1) + [ServerCode(2) UserPct(1) Type(1)]*count
        ok_list = len(resp) >= 10 and resp[5] == 1
        print(f"[{'OK' if ok_list else 'FALLO'}] GameServer aparece en la lista del ConnectServer")
        if not ok_list:
            failures.append("lista de servidores")

        s.sendall(c1sub(0xF4, 0x03, struct.pack("<H", 0)))  # resolver ServerCode 0
        time.sleep(0.3)
        resp2 = s.recv(4096)
        print("Respuesta 0xF4:03:", resp2.hex())
        ip_text = resp2[4:20].split(b"\x00")[0].decode("ascii", errors="replace")
        port_val = resp2[20] | (resp2[21] << 8)
        ok_resolve = ip_text == "127.0.0.1" and port_val == dep.game_port
        print(f"[{'OK' if ok_resolve else 'FALLO'}] Resuelto IP={ip_text} Port={port_val} "
              f"(esperado 127.0.0.1:{dep.game_port})")
        if not ok_resolve:
            failures.append("resolución de ServerCode")
    finally:
        s.close()

    # Login auténtico contra el GameServer que resolvió el ConnectServer. El
    # ClientVersion del wire son los bytes ASCII {'1','0','2','0','0'} (ServerInfo.cpp).
    tc = _env.ROOT / "src" / "TestClient" / "bin" / "Debug" / _env.TFM / "TestClient.dll"
    r = subprocess.run(
        [_env.DOTNET, str(tc), "127.0.0.1", str(dep.game_port), _env.SERIAL, "10200",
         _env.client_data("Enc1.dat"), _env.client_data("Dec2.dat"), "test", "test"],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
    print("\n=== Login auténtico con los valores REALES del paquete ===")
    print(r.stdout)
    print(r.stderr)
    if r.returncode != 0:
        failures.append("login contra GameServer")

finally:
    if dep is not None:
        dep.shutdown()
    pg.stop()

if failures:
    print(f"\n=== {len(failures)} FALLO(S): {', '.join(failures)} ===")
sys.exit(1 if failures else 0)

"""Runner to keep all 4 MuServer servers running locally in one process."""
import os
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

ROOT = Path(__file__).resolve().parent
DOTNET = "dotnet"

SERVERS = [
    ("ConnectServer", ROOT / "src" / "MuServer.ConnectServer" / "bin" / "Debug" / "net10.0"),
    ("JoinServer", ROOT / "src" / "MuServer.JoinServer" / "bin" / "Debug" / "net10.0"),
    ("DataServer", ROOT / "src" / "MuServer.DataServer" / "bin" / "Debug" / "net10.0"),
    ("GameServer", ROOT / "src" / "MuServer.GameServer" / "bin" / "Debug" / "net10.0"),
]

PORTS = [44405, 55557, 55970, 55960, 55900]

def cleanup_ports():
    try:
        r = subprocess.run(["netstat", "-ano"], capture_output=True, text=True)
        pids_to_kill = set()
        for line in r.stdout.splitlines():
            for port in PORTS:
                if f":{port} " in line:
                    parts = line.strip().split()
                    if parts:
                        pid = parts[-1]
                        if pid.isdigit() and int(pid) != os.getpid() and int(pid) != 0:
                            pids_to_kill.add(int(pid))
        for pid in pids_to_kill:
            subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
    except Exception as ex:
        print(f"[Aviso] No se pudieron limpiar puertos previos: {ex}")

cleanup_ports()
time.sleep(0.5)

procs = {}

def shutdown(signum=None, frame=None):
    print("\n[ServerRunner] Deteniendo todos los servidores...", flush=True)
    for name, p in list(procs.items()):
        try:
            p.terminate()
            p.wait(timeout=2)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass
    print("[ServerRunner] Servidores detenidos.", flush=True)
    sys.exit(0)

signal.signal(signal.SIGINT, shutdown)
signal.signal(signal.SIGTERM, shutdown)

print("=" * 60, flush=True)
print("  Iniciando los 4 servidores SSeMU 0.99B (C# Port)", flush=True)
print("=" * 60, flush=True)

for name, cwd in SERVERS:
    dll = cwd / f"MuServer.{name}.dll"
    if not dll.exists():
        print(f"[ERROR] No se encontró {dll}", flush=True)
        sys.exit(1)
    
    p = subprocess.Popen(
        [DOTNET, str(dll)],
        cwd=str(cwd),
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace"
    )
    procs[name] = p
    print(f"-> {name} iniciado (PID {p.pid})", flush=True)
    time.sleep(1.0)

print("=" * 60, flush=True)
print("  Todos los servidores están activos y listos:", flush=True)
print("  - ConnectServer : TCP 44405, UDP 55557")
print("  - JoinServer    : TCP 55970")
print("  - DataServer    : TCP 55960")
print("  - GameServer    : TCP 55900")
print("=" * 60, flush=True)

def stream_output(server_name, proc):
    for line in iter(proc.stdout.readline, ''):
        print(f"[{server_name}] {line.rstrip()}", flush=True)

for name, p in procs.items():
    t = threading.Thread(target=stream_output, args=(name, p), daemon=True)
    t.start()

try:
    while True:
        time.sleep(1)
        for name, p in list(procs.items()):
            ret = p.poll()
            if ret is not None:
                print(f"[ALERTA] {name} finalizó inesperadamente con código {ret}", flush=True)
except KeyboardInterrupt:
    shutdown()

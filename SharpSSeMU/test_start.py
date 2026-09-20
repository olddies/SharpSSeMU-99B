import subprocess
import time
import socket
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DOTNET = "dotnet"

servers = [
    ("ConnectServer", ROOT / "src" / "MuServer.ConnectServer" / "bin" / "Debug" / "net10.0", 44405),
    ("JoinServer", ROOT / "src" / "MuServer.JoinServer" / "bin" / "Debug" / "net10.0", 55970),
    ("DataServer", ROOT / "src" / "MuServer.DataServer" / "bin" / "Debug" / "net10.0", 55960),
    ("GameServer", ROOT / "src" / "MuServer.GameServer" / "bin" / "Debug" / "net10.0", 55900),
]

procs = {}

print("Starting servers...")
for name, cwd, port in servers:
    dll = cwd / f"MuServer.{name}.dll"
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
    print(f"Launched {name} (PID {p.pid})")

time.sleep(3.0)

for name, cwd, port in servers:
    p = procs[name]
    status = p.poll()
    if status is not None:
        out, _ = p.communicate()
        print(f"[{name}] CRASHED with code {status}:\n{out}")
    else:
        # Check port
        try:
            s = socket.create_connection(("127.0.0.1", port), timeout=2)
            s.close()
            print(f"[{name}] RUNNING and listening on port {port} OK")
        except Exception as ex:
            print(f"[{name}] Port {port} check failed: {ex}")

print("\nCleaning up test run...")
for name, p in procs.items():
    p.terminate()
    try:
        out, _ = p.communicate(timeout=2)
        print(f"--- {name} LOG ---")
        print(out)
    except Exception:
        p.kill()

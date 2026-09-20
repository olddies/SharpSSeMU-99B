import subprocess
import re
import os

ports = [44405, 55557, 55970, 55960, 55900]

r = subprocess.run(["netstat", "-ano"], capture_output=True, text=True)
pids_to_kill = set()

for line in r.stdout.splitlines():
    for port in ports:
        if f":{port} " in line:
            parts = line.strip().split()
            if parts:
                pid = parts[-1]
                if pid.isdigit() and int(pid) != os.getpid():
                    pids_to_kill.add(int(pid))
                    print(f"Found process {pid} using port {port}: {line.strip()}")

for pid in pids_to_kill:
    try:
        subprocess.run(["taskkill", "/F", "/PID", str(pid)], capture_output=True)
        print(f"Killed PID {pid}")
    except Exception as e:
        print(f"Could not kill PID {pid}: {e}")

if not pids_to_kill:
    print("No processes holding MuServer ports.")

#!/usr/bin/env python3
"""Build the release zip: the compiled servers with a default local configuration, the database scripts, the data
and client tools, a start script and a quick-start guide.

    python SharpSSeMU/tools/release/build_release.py --version 0.1.0 [--out <folder>]

The zip carries no game data and no client: whoever downloads it imports the tables from their own original
package (tools/data/import_data.py) and points their own 0.99B client at the server (tools/client). The game
servers need the .NET 10 runtime; the zip is framework-dependent and runs on Windows and Linux, the start script
is for Windows.
"""

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
SHARP = HERE.parent.parent  # SharpSSeMU/
REPO = SHARP.parent
sys.path.insert(0, str(SHARP))
import deploy_configs  # noqa: E402

SERVERS = ["ConnectServer", "JoinServer", "DataServer", "GameServer", "AdminPanel"]

START_BAT = r"""@echo off
title SharpSSeMU-99B
set ROOT=%~dp0

echo [1/4] ConnectServer (TCP 44405, UDP 55557)
start "SharpSSeMU - ConnectServer" /d "%ROOT%ConnectServer" cmd /k dotnet MuServer.ConnectServer.dll
timeout /t 2 /nobreak >nul
echo [2/4] JoinServer (TCP 55970)
start "SharpSSeMU - JoinServer" /d "%ROOT%JoinServer" cmd /k dotnet MuServer.JoinServer.dll
timeout /t 2 /nobreak >nul
echo [3/4] DataServer (TCP 55960)
start "SharpSSeMU - DataServer" /d "%ROOT%DataServer" cmd /k dotnet MuServer.DataServer.dll
timeout /t 2 /nobreak >nul
echo [4/4] GameServer (TCP 55900)
start "SharpSSeMU - GameServer" /d "%ROOT%GameServer" cmd /k dotnet MuServer.GameServer.dll

echo.
echo Servers started in their own windows. Admin panel (optional): start_adminpanel.bat
"""

ADMIN_BAT = r"""@echo off
rem Web admin panel on http://localhost:5281 (the password is printed in this window on first start).
start "SharpSSeMU - AdminPanel" /d "%~dp0AdminPanel" cmd /k dotnet MuServer.AdminPanel.dll
"""

QUICKSTART = """# SharpSSeMU-99B {version} — quick start

🌐 **English** · [Español](#español)

A MU Online 0.99B server in C#, to run on your own machine. Full documentation:
https://github.com/olddies/SharpSSeMU-99B

**Not included** (see `NOTICE.md`): the game client and the game data tables. You need your own
original 0.99B client (`main.exe` + SSeMU `Main.dll`) and the original SSeMU 0.99B server package, which
provides the `Data/` tables.

## 1. Requirements

- [.NET 10 runtime](https://dotnet.microsoft.com/download) (ASP.NET Core runtime too, for the admin panel)
- [PostgreSQL 16](https://www.postgresql.org/download/)
- Python 3.10+ (the data and client tools)

## 2. Database

```powershell
psql -U postgres -c "CREATE USER muserver WITH PASSWORD 'muserver';"
createdb -U postgres -O muserver muonline
foreach ($f in '001_accounts','002_characters','003_default_class_seed','004_friends') {{
  psql -U muserver -d muonline -f "db/postgres/$f.sql"
}}
```

This creates the test accounts `test`/`test` and `admin`/`admin` (local use only). Other credentials:
edit `JoinServer/JoinServer.ini` and `DataServer/DataServer.ini`.

## 3. Game data (from your own package)

```powershell
python tools/data/import_data.py --source <your SSeMU package folder> --dest GameServer
```

It copies only the tables the GameServer reads and checks them. The configuration files
(`GameServer/Data/GameServerInfo - *.dat`) come with this zip and are kept.

## 4. Start

```powershell
start_servers.bat
```

## 5. Your client

The original client refuses `127.0.0.1`; `127.0.0.2` is the same machine:

```powershell
python tools/client/configure_client.py set <your client folder> --ip 127.0.0.2 --port 44405 --from-server GameServer/GameServer.ini
```

Start `main.exe`, choose the server within a minute and log in with `test` / `test`.

## Español

Un servidor de MU Online 0.99B en C#, para correr en tu máquina. **No incluye** el cliente ni las tablas del
juego: necesitás tu propio cliente 0.99B original y el paquete original del servidor SSeMU 0.99B.

1. Requisitos: runtime de .NET 10 (y ASP.NET Core para el panel), PostgreSQL 16, Python 3.10+.
2. Base de datos: los mismos comandos del paso 2 de arriba (crea las cuentas de prueba `test`/`test` y `admin`/`admin`).
3. Datos del juego: `python tools/data/import_data.py --source <carpeta de tu paquete SSeMU> --dest GameServer`.
4. Arrancar: `start_servers.bat`.
5. Tu cliente (el original rechaza `127.0.0.1`; `127.0.0.2` es la misma máquina):
   `python tools/client/configure_client.py set <carpeta de tu cliente> --ip 127.0.0.2 --port 44405 --from-server GameServer/GameServer.ini`
   Abrí `main.exe`, elegí el servidor antes de un minuto e iniciá sesión con `test` / `test`.
"""


def publish(project: str, dest: Path) -> None:
    csproj = SHARP / "src" / f"MuServer.{project}" / f"MuServer.{project}.csproj"
    result = subprocess.run(["dotnet", "publish", str(csproj), "-c", "Release", "-o", str(dest), "--nologo"],
                            capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"dotnet publish failed for {project}:\n{result.stdout}\n{result.stderr}")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", required=True, help="e.g. 0.1.0")
    parser.add_argument("--out", default=str(REPO / "release"), help="output folder (default: <repo>/release)")
    args = parser.parse_args()

    name = f"SharpSSeMU-99B-v{args.version}"
    out = Path(args.out)
    stage = out / name
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)

    for server in SERVERS:
        print(f"publishing {server}...")
        publish(server, stage / server)

    deploy_configs.write_server_configs(stage / "ConnectServer", stage / "JoinServer", stage / "DataServer", stage / "GameServer")

    # The admin panel finds the GameServer data and the DataServer configuration next to it in this layout.
    settings_path = stage / "AdminPanel" / "appsettings.json"
    settings = json.loads(settings_path.read_text(encoding="utf-8"))
    settings["Urls"] = "http://localhost:5281"
    settings["GameServer"] = {"DataPath": "../GameServer/Data"}
    settings["DataServer"] = {"IniPath": "../DataServer/DataServer.ini"}
    settings_path.write_text(json.dumps(settings, indent=2) + "\n", encoding="utf-8")

    shutil.copytree(SHARP / "db" / "postgres", stage / "db" / "postgres")
    for tool in ("data", "client"):
        shutil.copytree(SHARP / "tools" / tool, stage / "tools" / tool,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    for doc in ("LICENSE", "NOTICE.md"):
        shutil.copy(REPO / doc, stage / doc)

    (stage / "start_servers.bat").write_text(START_BAT.replace("\n", "\r\n"), encoding="utf-8")
    (stage / "start_adminpanel.bat").write_text(ADMIN_BAT.replace("\n", "\r\n"), encoding="utf-8")
    (stage / "QUICKSTART.md").write_text(QUICKSTART.format(version=args.version), encoding="utf-8")

    # The admin panel references the GameServer project, which drags a copy of its .dat files along; the panel
    # edits the GameServer's own (../GameServer/Data), so the copy would only confuse.
    shutil.rmtree(stage / "AdminPanel" / "Data", ignore_errors=True)

    # Nothing from a developer's machine may slip in: logs, local data tables, keys.
    for server in SERVERS:
        for leftover in ("LOG", "Hack", "status.json"):
            path = stage / server / leftover
            if path.is_dir():
                shutil.rmtree(path)
            elif path.exists():
                path.unlink()
    data_dir = stage / "GameServer" / "Data"
    extra = [p.name for p in data_dir.iterdir() if not p.name.startswith("GameServerInfo - ")] if data_dir.exists() else []
    if extra:
        sys.exit(f"GameServer/Data carries files that are not ours: {extra}")

    zip_path = out / f"{name}.zip"
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for path in sorted(stage.rglob("*")):
            if path.is_file():
                zf.write(path, Path(name) / path.relative_to(stage))

    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    print(f"{zip_path}  {zip_path.stat().st_size / 1024 / 1024:.1f} MB")
    print(f"sha256 {digest}")


if __name__ == "__main__":
    main()

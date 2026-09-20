# Getting started

🌐 **English** · [Español](es/GETTING_STARTED.md)

This guide takes you from a fresh checkout to a character walking around Lorencia on your own
machine. Everything runs on `127.0.0.1`, so no firewall or router configuration is needed.

- [1. Prerequisites](#1-prerequisites)
- [2. Bring your own game files](#2-bring-your-own-game-files)
- [3. Database](#3-database)
- [4. Build and deploy the server](#4-build-and-deploy-the-server)
- [5. Run the servers](#5-run-the-servers)
- [6. Connect a client](#6-connect-a-client)
- [7. The AdminPanel](#7-the-adminpanel)
- [8. Configuration reference](#8-configuration-reference)
- [9. Troubleshooting](#9-troubleshooting)

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0** | building and running the server |
| [PostgreSQL](https://www.postgresql.org/download/) | 16 (any recent version should work) | accounts, characters, inventories |
| Python | 3.10+ | deployment helper, launcher and the end-to-end tests |
| Windows | 10/11 | the game client only runs on Windows; the servers are cross-platform |

Tested toolchain: .NET SDK 10.0.302, PostgreSQL 16.14, Python 3.14, CMake 4.2, MSVC 14.44.

## 2. Bring your own game files

This repository contains **no game assets** (see [`NOTICE.md`](../NOTICE.md)). The server loads the
original SSeMU `Data/` tables at startup and the test-suite reads the client's encryption keys, so
place the original package folders **next to** `SharpSSeMU/` — that is, in the repository root:

```
<repo root>/
├── SharpSSeMU/            ← this repo (server)
├── MuMain-099B/           ← this repo (client)
├── MuClient/              ← YOU provide: the original 0.99B client
│   ├── main.exe, Main.dll
│   └── Data/              (needs at least Enc1.dat / Dec2.dat)
├── MuServer99B/           ← YOU provide: the original SSeMU 0.99B server package
│   ├── Data/              (Monster/, Item/, Skill/, Shop/, Event/, Hack/, Move.txt …)
│   └── GameServer/DATA/   (GameServerInfo - *.dat)
└── Source/                ← optional: SSeMU C++ emulator sources, used as the reference
```

All three folders are git-ignored. Which ones you need depends on what you want to do:

| You want to… | You need |
|---|---|
| run the server | `MuServer99B/Data` (+ `MuServer99B/GameServer/DATA`) |
| play with the original client | `MuClient/` |
| play with the ported client | a client `Data/` folder in `MuMain-099B/src/bin/Data` (see [BUILDING](BUILDING.md#client)) |
| run the end-to-end tests | `MuClient/Data/Enc1.dat` + `MuServer99B/Data` |
| audit the port against the original C++ | `Source/` |

## 3. Database

Create the role and database, then apply the four schema files **in order**:

```bash
psql -U postgres -c "CREATE USER muserver WITH PASSWORD 'muserver';"
createdb -U postgres -O muserver muonline

psql -U muserver -d muonline -f SharpSSeMU/db/postgres/001_accounts.sql          # memb_info, memb_stat + seed accounts
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/002_characters.sql        # characters, inventories, rankings
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/003_default_class_seed.sql # base stats per class
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/004_friends.sql           # friend list
```

`001_accounts.sql` seeds two accounts, `test`/`test` and `admin`/`admin`. **Development only.**

> Passwords are stored as configured by `MD5Encryption` in `JoinServer.ini` (`0` = plain text,
> the local-development default). Turn it on and change the seed passwords for anything shared.

## 4. Build and deploy the server

```bash
dotnet build SharpSSeMU/SharpSSeMU.sln
python SharpSSeMU/deploy_configs.py
```

`deploy_configs.py` writes the `.ini` files each server needs into its `bin/Debug/net10.0`
folder (ports, the Postgres connection string, `ServerList.dat`…) and copies `MuServer99B/Data`,
`Hack/` and the `GameServerInfo - *.dat` files next to the GameServer binaries. It is safe to
re-run; it overwrites the generated files. If your PostgreSQL credentials differ from the defaults,
edit the connection strings at the top of that script (or the generated `JoinServer.ini` /
`DataServer.ini`).

For a Release / Linux deployment, see [`SharpSSeMU/README.md`](../SharpSSeMU/README.md#deployment).

### Import and validate your game data

This repository does not ship the game tables (items, monsters, spawns, shops...). Two tools in
`SharpSSeMU/tools/data/` build and check the data folder from **your own** original package:

```bash
# copy only what the GameServer reads (byte for byte) and validate the result
python SharpSSeMU/tools/data/import_data.py --source MuServer99B --dest <GameServer output folder>

# validate any existing folder (the one that contains Data/ and Hack/)
python SharpSSeMU/tools/data/validate_data.py --root <GameServer output folder>
```

The validator explains in plain words what is missing or inconsistent (missing files, duplicate ids, shops that
sell undefined items, warps to unknown gates, terrain files with the wrong size, bad ports/serial in the
configuration...). Errors must be fixed before starting the GameServer; warnings are informational (for example,
events whose maps you do not use). `--dry-run` on the importer only lists what would be copied.

## 5. Run the servers

Windows, four console windows (ConnectServer → JoinServer → DataServer → GameServer, with the
right delays):

```powershell
SharpSSeMU\start_servers.bat
```

or all four in a single console:

```bash
python SharpSSeMU/run_servers.py
```

or by hand, one terminal each, from `SharpSSeMU/src/<Project>/bin/Debug/net10.0`:

```bash
dotnet MuServer.ConnectServer.dll
dotnet MuServer.JoinServer.dll
dotnet MuServer.DataServer.dll
dotnet MuServer.GameServer.dll
```

A healthy start-up makes the GameServer log how many maps, monster types, spawned
monsters (~2 900), items, skills and NPC shops it loaded, and finish with
`GameServer ready on TCP port 55900`.

| Server | Port | Role |
|---|---|---|
| ConnectServer | TCP **44405**, UDP 55557 | server list; the client connects here first |
| GameServer | TCP 55900 | gameplay |
| JoinServer | TCP 55970 | account login (internal) |
| DataServer | TCP 55960 | characters, inventory, rankings (internal) |
| AdminPanel | HTTP 5281 | web administration (optional) |

If a previous run left ports occupied: `python SharpSSeMU/kill_ports.py`.

## 6. Connect a client

**Original client** (`MuClient/main.exe`; set `ServerSerial` in `GameServer.ini` to its `ClientSerial`, see the serial note in the testing guide): its `Main.dll` already points at `127.0.0.1:44405`
(`ClientVersion = 1.02.00`, `ClientSerial = <your client's serial>` — the same values as
`GameServer.ini`). Start `main.exe` and log in with `test` / `test`. Step-by-step manual
walkthrough for this route: [`SharpSSeMU/TESTING_WITH_REAL_CLIENT.md`](../SharpSSeMU/TESTING_WITH_REAL_CLIENT.md).

**Ported client** (this repo's `MuMain-099B`): build it ([BUILDING](BUILDING.md#client)), then run
`Main.exe`. Its `config.ini` (next to the executable) holds the target:

```ini
[CONNECTION SETTINGS]
ServerIP=127.0.0.1
ServerPort=44405
```

Command-line override: `Main.exe connect /u127.0.0.1 /p44405`.

First run: create a character, select it and enter the world. See
[Known limitations](STATUS.md#known-limitations) for what is not implemented yet.

## 7. The AdminPanel

A web UI to tune the server without touching files by hand. From the project folder:

```bash
cd SharpSSeMU/src/MuServer.AdminPanel
dotnet run            # → http://localhost:5281
```

> Start it with `dotnet run` from the project folder. Running the built `.dll` from `bin/` directly
> does not resolve the panel's static web assets and the page renders broken.

- **Password:** set `Admin:Password` in `appsettings.json`. If it is empty, a random password is
  generated on start-up and printed to the console.
- **Rate presets:** the *Rates* page has one-click presets (Classic x1, Soft x10, Fast x100, Testing) that fill in
  experience, drop and zen values. They only load the values into the page; nothing is written until you press *Save*.
- **What it edits:** the real game data files (`Data/…` and `GameServerInfo - *.dat`, keeping
  comments and formatting; a `.bak` is written the first time), and — for the *Characters* page —
  the PostgreSQL database directly (stats, inventory, warehouse, with a searchable item picker).
- **Live vs. restart:** the GameServer reads its configuration **once at start-up**, so file edits
  apply after a GameServer restart. Only *Connected accounts* and *Global message* act on the
  running server.
- **Characters:** a connected character lives in the GameServer's memory and is autosaved over
  your edit — disconnect the character before editing it.
- **Security:** the panel writes server configuration. Keep it on your internal network.

Settings (all optional, in `appsettings.json`): `GameServer:DataPath` (the GameServer's `Data/`
folder), `DataServer:IniPath`, `Database:ConnectionString`, `Admin:Password`.

## 8. Configuration reference

| File | Where | What |
|---|---|---|
| `ConnectServer.ini`, `ServerList.dat`, `BlackList.txt` | ConnectServer output folder | ports; the list of game servers shown to the client |
| `JoinServer.ini`, `AllowableIpList.txt` | JoinServer output folder | Postgres connection string, `MD5Encryption`, allowed IPs |
| `DataServer.ini`, `AllowableIpList.txt`, `BadSyntax.txt` | DataServer output folder | Postgres connection string, forbidden character names |
| `GameServer.ini` | GameServer output folder | server name/code/port, version + serial check, addresses of the other servers |
| `Data/GameServerInfo - *.dat` | GameServer output folder | ~520 gameplay settings: rates, formulas, command permissions, events (8 files, all loaded) |
| `Data/**/*.txt` | GameServer output folder | monsters, spawns, items, skills, shops, gates, quests, events |

`GameServer.ini` is specific to this port (the original keeps that data inside a binary):

```ini
[GameServerInfo]
ServerName = SSeMU GameServer_0
ServerCode = 0
ServerPort = 55900
ServerVersion = 1.02.00
ServerSerial = SharpSSeMU99B-v1
ServerMaxUserNumber = 300
JoinServerAddress = 127.0.0.1
JoinServerPort = 55970
DataServerAddress = 127.0.0.1
DataServerPort = 55960
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
```

## 9. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Client stuck before the server-select screen | ConnectServer not running or port 44405 blocked. Check its console. |
| Client stuck on the character-select screen | GameServer not reachable, or DataServer down (it serves the character list). |
| "Account full" when creating a class other than Dark Wizard | The class row is missing from `default_class_type`: re-apply `003_default_class_seed.sql`. |
| `Address already in use` on start | A previous run is still alive: `python SharpSSeMU/kill_ports.py`. |
| Cannot rebuild: *file is being used by another process* | Stop the servers first; a running `.exe`/`.dll` is locked. |
| AdminPanel shows *GameServer offline* | `Data/status.json` is not being written: GameServer not running, or `GameServer:DataPath` points elsewhere. |
| AdminPanel page renders unstyled / broken | Start it with `dotnet run` from `src/MuServer.AdminPanel`, not from `bin/`. |
| Config edits have no effect | Restart the GameServer — it reads its files once at start-up. |
| Postgres authentication failed | Credentials in `JoinServer.ini` / `DataServer.ini` do not match your role. |
| Inventory/stat edits reverted | The character was online; the GameServer autosave overwrote them. |

Server logs are printed to each console and written to a `LOG/` folder beside each binary.

## Language (English / Spanish)

English is the primary language of the server logs and the AdminPanel; Spanish is a full second language.

- **Server logs** — set `Language = en` (default) or `Language = es` in each server's `.ini`
  (`[ConnectServerInfo]`, `[JoinServerInfo]`, `[DataServerInfo]`, `[GameServerInfo]`), or override it for the
  process with the `MUSERVER_LANG` environment variable. The same setting translates the few notices the
  GameServer sends to players.
- **AdminPanel** — use the `EN | ES` switcher in the top bar. The choice is stored in a cookie; the first visit
  follows the browser's `Accept-Language`.
- **Adding a translation** — English text is the lookup key. Spanish lives in
  `SharpSSeMU/src/MuServer.Shared/Localization/es/*.json` (`logs.json`, `messages.json`, `admin.json`). A missing
  entry simply falls back to English.

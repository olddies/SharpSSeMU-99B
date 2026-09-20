# SharpSSeMU — the SSeMU 0.99B server in C#/.NET 10

🌐 **English** · the detailed chronological development log is in [`docs/development-log.md`](docs/development-log.md) (Spanish original: [`development-log.es.md`](docs/development-log.es.md))

A C# re-implementation of the SSeMU 2.1.7 MU Online **0.99B** server. It keeps the original binary
protocol byte for byte, so both the **original, unmodified client** (`main.exe` + `Main.dll`) and
the [ported MuMain client](../MuMain-099B/) connect to it.

Why only the server is rewritten: the original client's 3D engine lives in `main.exe`, a closed
binary, and `Main.dll` hooks it through hard-coded memory addresses — there is nothing meaningful
to "convert". Replicating the protocol exactly keeps the existing client working as is. (This is
the same approach as [OpenMU](https://github.com/MUnique/OpenMU).)

> Start here if you want to **run** it: [`../docs/GETTING_STARTED.md`](../docs/GETTING_STARTED.md).
> Feature coverage: [`../docs/STATUS.md`](../docs/STATUS.md). Design: [`../docs/ARCHITECTURE.md`](../docs/ARCHITECTURE.md).

## Modules

| Project | Port | State | Role |
|---|---|---|---|
| `MuServer.ConnectServer` | TCP 44405 · UDP 55557 | ✅ | server list, load reports, IP limits |
| `MuServer.JoinServer` | TCP 55970 | ✅ | account login (PostgreSQL) |
| `MuServer.DataServer` | TCP 55960 | ✅ | characters, inventory, warehouse, rankings, friends |
| `MuServer.GameServer` | TCP 55900 | 🔶 playable | the game — see [STATUS](../docs/STATUS.md) |
| `MuServer.AdminPanel` | HTTP 5281 | 🔶 | web administration (Blazor Server + MudBlazor) |
| `MuServer.Shared` | — | ✅ | framing, ciphers, script tokenizer, INI reader, logger |
| `TestClient`, `WorldTestClient` | — | test harness | simulated encrypted game clients for the e2e tests |

## Quick run

```bash
# database (once) -- see docs/GETTING_STARTED.md for the full walkthrough
createdb muonline
for f in 001_accounts 002_characters 003_default_class_seed 004_friends; do
  psql -d muonline -f db/postgres/$f.sql
done

dotnet build SharpSSeMU.sln
python deploy_configs.py         # writes .ini files + copies the game Data/ next to the binaries
start_servers.bat                # Windows; or: python run_servers.py
```

`deploy_configs.py` expects the original package folder `MuServer99B/` beside `SharpSSeMU/`
(it is not distributed here — see [`../NOTICE.md`](../NOTICE.md)).

Helper scripts in this folder:

| Script | Purpose |
|---|---|
| `deploy_configs.py` | generate per-server `.ini` files and copy `Data/`, `Hack/`, `GameServerInfo - *.dat` into the output folders |
| `start_servers.bat` | open the four servers in separate consoles, in the right order |
| `run_servers.py` | run the four servers from one console (frees the ports first) |
| `kill_ports.py` | kill whatever holds ports 44405/55557/55970/55960/55900 |
| `test_start.py` | smoke test: start the four servers and check they stay up |
| `tests/` | end-to-end tests (see [`docs/BUILDING.md`](../docs/BUILDING.md#tests)) |

## Configuration

| File | Server | Key contents |
|---|---|---|
| `ConnectServer.ini`, `ServerList.dat`, `BlackList.txt` | ConnectServer | ports, list of game servers |
| `JoinServer.ini`, `AllowableIpList.txt` | JoinServer | `JoinServerPostgres`, `MD5Encryption`, allowed IPs |
| `DataServer.ini`, `AllowableIpList.txt`, `BadSyntax.txt` | DataServer | `DataServerPostgres`, forbidden names |
| `GameServer.ini` | GameServer | name/code/port, `ServerVersion`, `ServerSerial`, other servers' addresses |
| `Data/GameServerInfo - *.dat` | GameServer | ~520 gameplay settings (8 files, all loaded) |
| `Data/**` | GameServer | monsters, spawns, items, skills, shops, gates, quests, events |

`ServerVersion` / `ServerSerial` must match the client's `Main.dll` (`1.02.00` for the stock package;
`SharpSSeMU99B-v1` is the serial of our own client and the server default; see
[`TESTING_WITH_REAL_CLIENT.md`](TESTING_WITH_REAL_CLIENT.md) for the stock client); the serial also derives the stream-cipher key.

The PostgreSQL connection string format:

```
JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
```

## Database

Schema in [`db/postgres/`](db/postgres/), applied in order:

| File | Creates |
|---|---|
| `001_accounts.sql` | `memb_info`, `memb_stat`, seed accounts `test`/`admin` (**dev only**) |
| `002_characters.sql` | `character`, `account_character`, `default_class_type`, rankings, options, … (the DataServer creates the warehouse and guild tables on demand) |
| `003_default_class_seed.sql` | starting stats per class |
| `004_friends.sql` | friend list and requests |

The original used SQL Server + ODBC + `WZ_*` stored procedures; the equivalent queries are in
`MuServer.JoinServer/Db` and `MuServer.DataServer/Db` (Npgsql). Character names are the primary key.

## AdminPanel

```bash
cd src/MuServer.AdminPanel && dotnet run       # http://localhost:5281
```

Edits the real files under the GameServer's `Data/` (comments and formatting preserved, `.bak` on
first write): rates (exp/drop/zen), warps, items, prices, monsters, spawns, shops, Chaos Machine,
skills, gates, quests, messages, events, and all 8 `GameServerInfo - *.dat`. The *Characters* page
edits stats, inventory and warehouse in PostgreSQL with a searchable item picker. It also shows live
server status (`Data/status.json`) and sends global messages.

- Password: `Admin:Password` in `appsettings.json` (a random one is printed at start-up if empty).
- Other settings: `GameServer:DataPath`, `DataServer:IniPath`, `Database:ConnectionString`.
- Run it with `dotnet run` from the project folder (not the `.dll` in `bin/`).
- The GameServer reads config once at start-up — restart it to apply edits. Edit characters only
  while they are **offline** (an online character is autosaved over your changes).
- It writes server configuration: keep it on a trusted network.

## Deployment

```bash
for p in ConnectServer JoinServer DataServer GameServer; do
  dotnet publish src/MuServer.$p -c Release -r linux-x64 --self-contained
done
```

Copy each server's config files next to its published binary, and the `Data/` + `Hack/` folders
next to the GameServer. Ports are listed above; only **44405** (ConnectServer) and **55900**
(GameServer) need to be reachable by players.

## Code layout

```
SharpSSeMU/
├── SharpSSeMU.sln
├── db/postgres/                   schema + seed
├── docs/development-log.md     chronological log (English; Spanish original: development-log.es.md)
├── deploy_configs.py, run_servers.py, kill_ports.py, test_start.py, start_servers.bat
├── tests/                         Python e2e tests + _env.py (disposable PostgreSQL cluster)
└── src/
    ├── MuServer.Shared/           Protocol/ (framing, packet reader/writer), Crypto/ (stream + block ciphers,
    │                              game framer), Scripting/MemScript.cs (tokenizer for Data/*.txt|dat),
    │                              Config/IniFile.cs, Logging/Log.cs
    ├── MuServer.ConnectServer/    Net/ (TCP gate + UDP), server list, IP tracking
    ├── MuServer.JoinServer/       Db/ (Npgsql accounts)
    ├── MuServer.DataServer/       Db/ (Npgsql characters/inventory/rankings), Protocol/
    ├── MuServer.GameServer/       Config/, Net/, Protocol/ (ClientProtocolHandler + packet builders),
    │                              World/ (maps, registries, ViewportTicker, items, Devil Square, balance),
    │                              Data/ (shipped GameServerInfo - *.dat), StatusWriter, GlobalMessagePoller
    ├── MuServer.AdminPanel/       Components/Pages (one page per data file), Repositories/ (file editors that
    │                              preserve comments), Auth/
    ├── TestClient/                Phase-1 harness: authentic encrypted login
    └── WorldTestClient/           Phase 2+ harness: simulated clients (viewport, movement, items, combat)
```

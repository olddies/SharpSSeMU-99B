> **Manual walkthrough.** This guide does by hand what `deploy_configs.py` automates. For the
> quick path see [`../docs/GETTING_STARTED.md`](../docs/GETTING_STARTED.md).
>
> 🌐 **English** · [Español](COMO_PROBAR_CON_CLIENTE_REAL.md)

# Building and starting everything to test with the official client

This guide runs **the four C# servers** (`SharpSSeMU/`) on your own Windows PC and connects the
real, original client (`MuClient/main.exe`) to them. Windows is used throughout because the client
only runs there — .NET 10 is cross-platform, so the servers behave the same on Windows for this
local test and can be moved to Linux later for production.

Everything runs on `127.0.0.1`, so no ports need opening and no firewall configuration is needed.

## 0. About the client

`MuServer99B/Tools/GetMainInfo/MainInfo.ini` (a tool that reads these values out of the package's
own `Main.dll`) lists:

```
IpAddress = 127.0.0.1
IpAddressPort = 44405
ClientVersion = 1.02.00
ClientSerial = <your client's serial>
```

So the `Main.dll` in `MuClient/` **already points at `127.0.0.1:44405`** — the ConnectServer port.
Nothing needs patching with MuMaker for a local test. If the client cannot connect, this is the
first thing to check (see *Troubleshooting* below).

`ClientVersion = 1.02.00` and `ClientSerial = <your client's serial>` are exactly the values to put in the
GameServer configuration so the login version/serial check matches (step 4).

## 1. Prerequisites

- **.NET 10 SDK** for Windows: <https://dotnet.microsoft.com/download/dotnet/10.0> (the normal installer).
- **PostgreSQL** for Windows, either:
  - the official installer: <https://www.postgresql.org/download/windows/>, or
  - Docker Desktop: `docker run --name mu-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:16`
- The original game package next to `SharpSSeMU/` (see
  [Bring your own game files](../docs/GETTING_STARTED.md#2-bring-your-own-game-files)).

Check that `dotnet --version` prints `10.x` from a console before continuing.

## 2. Create the database

With PostgreSQL running on `127.0.0.1:5432`, from `psql` (or pgAdmin, or `docker exec`):

```sql
CREATE DATABASE muonline;
CREATE ROLE muserver LOGIN PASSWORD 'muserver' SUPERUSER;   -- local development only
```

Then apply the four schemas, **in this order**, to the `muonline` database:

```bat
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\001_accounts.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\002_characters.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\003_default_class_seed.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\004_friends.sql"
```

This seeds two test accounts: **`test`/`test`** and **`admin`/`admin`** (see `001_accounts.sql` to
change the passwords or add more). Development only.

## 3. Build the four servers

From `SharpSSeMU/` (cmd or PowerShell):

```bat
cd src\MuServer.ConnectServer && dotnet build
cd ..\MuServer.JoinServer && dotnet build
cd ..\MuServer.DataServer && dotnet build
cd ..\MuServer.GameServer && dotnet build
```

(or simply `dotnet build SharpSSeMU.sln` from `SharpSSeMU/`.) Each project's output lands in its
own `bin\Debug\net10.0\` — **that is where the configuration files of the next step must go**: the
servers look for their `.ini` next to their own `.dll`, not in the project folder.

## 4. Configuration files

Put these files in the `bin\Debug\net10.0\` folder of the matching project. You can create them by
hand — the exact contents below are the real package values, consistent with each other.
(`python deploy_configs.py` generates all of this for you.)

### `MuServer.ConnectServer\bin\Debug\net10.0\ConnectServer.ini`

```ini
[ConnectServerInfo]
ConnectServerPortTCP = 44405
ConnectServerPortUDP = 55557
ConnectServerMaxUserNumber = 500
MaxConnectionPerIP = 50
MaxPacketPerSecond = 0
MaxConnectionIdle = 60
```

`MaxConnectionPerIP` **must be non-zero**: with `0` the ConnectServer accepts a client's socket and
immediately drops it (compatibility with the original's behaviour).

### `MuServer.ConnectServer\bin\Debug\net10.0\BlackList.txt`

```
0
end
```

### `MuServer.ConnectServer\bin\Debug\net10.0\ServerList.dat`

```
   0            "GameServer_0"   "127.0.0.1"        55900       1
end
```

(`MuServer99B/ConnectServer/ServerList.dat` points at an old LAN IP of the original developer, so
use this `127.0.0.1` version instead of copying the original as is.)

### `MuServer.JoinServer\bin\Debug\net10.0\JoinServer.ini`

```ini
[JoinServerInfo]
JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver
JoinServerPort = 55970
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
CaseSensitive = 0
MD5Encryption = 0
```

### `MuServer.JoinServer\bin\Debug\net10.0\AllowableIpList.txt`

```
0
"127.0.0.1"
end
```

### `MuServer.DataServer\bin\Debug\net10.0\DataServer.ini`

```ini
[DataServerInfo]
DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver
DataServerPort = 55960
```

### `MuServer.DataServer\bin\Debug\net10.0\AllowableIpList.txt`

```
0
"127.0.0.1"
end
```

### `MuServer.DataServer\bin\Debug\net10.0\BadSyntax.txt`

```
"fuck"
"admin"
end
```

### `MuServer.GameServer\bin\Debug\net10.0\GameServer.ini`

```ini
[GameServerInfo]
ServerName = SSeMU GameServer_0
ServerCode = 0
ServerPort = 55900
ServerVersion = 1.02.00
ServerSerial = SharpSSeMU99B-v1
ServerEncDecKey1 = 0
ServerEncDecKey2 = 0
ServerMaxUserNumber = 300
JoinServerAddress = 127.0.0.1
JoinServerPort = 55970
DataServerAddress = 127.0.0.1
DataServerPort = 55960
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
```

`ServerVersion` and `ServerSerial` must match the values your real client uses (step 0); the serial also derives
the stream-cipher key.

> **About the serial.** SharpSSeMU-99B uses its own default serial, `SharpSSeMU99B-v1` (16 characters, set in
> `ServerSerial`), and so does our own client (`MuMain-099B`, `Serial` in `WSclient.cpp`). The **stock** client's
> serial is baked into its own files and cannot be changed by this project: to use the stock client, set
> `ServerSerial` in `GameServer.ini` to the `ClientSerial` value shown in your client's configuration. Client and
> server must have the same serial; it also derives the stream-cipher key.

### `MuServer.GameServer\bin\Debug\net10.0\Hack\Enc2.dat` and `Hack\Dec1.dat`

Binary files. Copy them as they are from `MuServer99B\Data\Hack\` into a new `Hack\` subfolder of
`MuServer.GameServer\bin\Debug\net10.0\`.

### `MuServer.GameServer\bin\Debug\net10.0\Data\` — the game data

Copy the **whole** `MuServer99B\Data\` folder (monsters, spawns, items, skills, shops, events,
`Move.txt` …) into a `Data\` subfolder next to the GameServer binaries, and also copy the eight
`MuServer99B\GameServer\DATA\GameServerInfo - *.dat` files into that same `Data\` folder. Without
these the GameServer has no world to load.

## 5. Start everything, in this order

Each in its own console window (so you can see the logs and stop them separately). PostgreSQL must
be up before JoinServer/DataServer:

```bat
cd MuServer.ConnectServer\bin\Debug\net10.0 && dotnet MuServer.ConnectServer.dll
cd MuServer.JoinServer\bin\Debug\net10.0    && dotnet MuServer.JoinServer.dll
cd MuServer.DataServer\bin\Debug\net10.0    && dotnet MuServer.DataServer.dll
cd MuServer.GameServer\bin\Debug\net10.0    && dotnet MuServer.GameServer.dll
```

Within a few seconds the ConnectServer console should show:

```
[SocketUDP] JoinServer connected
[SocketUDP] GameServer connected [GameServer_0] [127.0.0.1:55900][0]
```

Seeing both lines means the whole chain is alive and ready for the client (this is exactly what the
automated test `tests/full_chain_e2e_test.py` checks).

## 6. Launch the client

Run `MuClient\main.exe`. It should:

1. connect to the ConnectServer and show "GameServer_0" in the server list;
2. on selecting it, connect straight to the GameServer (`127.0.0.1:55900`);
3. show the login screen — use `test` / `test` or `admin` / `admin`;
4. let you create or pick a character and enter the world.

Login working already confirms that the encryption layers and the protocol are right. What is and
is not implemented once you are in the game: see [`../docs/STATUS.md`](../docs/STATUS.md).

To use the **ported client** instead, see [`../docs/BUILDING.md`](../docs/BUILDING.md#client).

## Troubleshooting

- **The client cannot connect at all**: the step-0 assumption (that `Main.dll` already points at
  `127.0.0.1:44405`) is the first thing to verify. If it does not, use
  `MuServer99B/Tools/MuMaker/MuMaker.exe` to patch the IP in a copy of `Main.dll` (that tool
  produces a distributable client with the IP baked in).
- **"database does not exist" or a connection error in JoinServer/DataServer**: check that
  PostgreSQL is listening on the port in the `.ini` (5432 by default) and that the `muserver`
  role exists.
- **GameServer does not appear in the client's list**: look in the ConnectServer console for
  `[SocketUDP] GameServer connected`. If it does not show up within ~5 seconds, check that
  `GameServer.ini` has `ConnectServerPort = 55557` (the UDP port — not 44405, which is TCP) and
  that the ConnectServer was up before the GameServer started.
- **The client connects, then is dropped immediately**: `MaxConnectionPerIP` is `0` in
  `ConnectServer.ini` (see step 4).
- **Login rejected with "wrong version"**: check that `ServerVersion = 1.02.00` is exactly that,
  dots included, in `GameServer.ini`.
- **Ports already in use**: if something is already running on 44405/55557/55970/55960/55900, stop
  it, or change the ports consistently in every `.ini` involved (`python kill_ports.py` frees them).

## Port summary

| Server | Port | Protocol | Who connects |
|---|---|---|---|
| ConnectServer | 44405 | TCP | the real client |
| ConnectServer | 55557 | UDP | JoinServer/GameServer heartbeats |
| JoinServer | 55970 | TCP | GameServer |
| DataServer | 55960 | TCP | GameServer |
| GameServer | 55900 | TCP | the real client (after choosing a server) |
| PostgreSQL | 5432 | TCP | JoinServer, DataServer |

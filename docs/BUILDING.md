# Building and testing

🌐 **English** · [Español](es/BUILDING.md)

- [Server](#server) — C# / .NET 10
- [Client](#client) — C++ / CMake, Windows
- [Tests](#tests)
- [Regenerating the protocol library](#regenerating-the-protocol-library)

## Server

```bash
dotnet build SharpSSeMU/SharpSSeMU.sln            # Debug, all projects
dotnet build SharpSSeMU/SharpSSeMU.sln -c Release
```

Projects (`SharpSSeMU/src/`):

| Project | Output | Notes |
|---|---|---|
| `MuServer.Shared` | library | packet framing, ciphers, script tokenizer, INI reader, logger |
| `MuServer.ConnectServer` / `JoinServer` / `DataServer` / `GameServer` | console apps | the four servers |
| `MuServer.AdminPanel` | ASP.NET Core Blazor Server app | references GameServer + DataServer |
| `TestClient`, `WorldTestClient` | console apps | simulated game clients used by the e2e tests |

NuGet dependencies are restored automatically: Npgsql 8.0.3 and MudBlazor 9.9.0.

**Publishing a standalone Linux build:**

```bash
for p in ConnectServer JoinServer DataServer GameServer; do
  dotnet publish SharpSSeMU/src/MuServer.$p -c Release -r linux-x64 --self-contained
done
```

Copy each server's `.ini` files (and, for the GameServer, the `Data/` and `Hack/` folders) next to
the published binaries — [GETTING_STARTED](GETTING_STARTED.md#8-configuration-reference) lists
which file goes where.

> **Windows gotcha:** you cannot rebuild a server while it is running (the `.exe`/`.dll` is locked
> and MSBuild fails with *MSB3027*). Stop it first.

## Client

The client is a CMake project (`MuMain-099B`). It is a fork of
[sven-n/MuMain](https://github.com/sven-n/MuMain); the upstream build guides in
[`MuMain-099B/docs/build/`](../MuMain-099B/docs/build/README.md) (Visual
Studio, CLion, Rider, console, WSL/MinGW) apply unchanged. What follows is the verified
Windows path used by this project.

### Requirements

| Tool | Version |
|---|---|
| CMake | 3.25+ (tested 4.2.3) |
| Visual Studio 2022 with the *Desktop development with C++* workload | MSVC 14.44 tested |
| .NET SDK | 10.0 (builds the Native-AOT network helper library) |
| Ninja | bundled with Visual Studio 2022 |
| Git | to fetch third-party sources |

### 1. Fetch third-party sources

```powershell
scripts\setup-thirdparty.ps1            # SDL + SDL_mixer
scripts\setup-thirdparty.ps1 -WithEditor   # + Dear ImGui, only for *-mueditor presets
# Linux/macOS: scripts/setup-thirdparty.sh   (WITH_EDITOR=1 for imgui)
```

The **version pair matters**: `SDL release-3.4.x` + `SDL_mixer release-3.2.x`. SDL_mixer uses
`SDL_ALIGNED(16)`, which only exists from SDL 3.4; SDL 3.2.x fails to compile
`SDL_mixer_spatialization.c`. The script pins the right pair.

### 2. Provide the client `Data/` folder

Copy a MU client `Data/` folder into `MuMain-099B/src/bin/Data/` (it is git-ignored).
The build copies it, together with `fonts/` and `config.ini`, next to the executable.

### 3. Configure and build

From a **"x86 Native Tools Command Prompt for VS 2022"** (or after running the VS 2022
`vcvars32.bat`), in `MuMain-099B`:

```powershell
cmake --preset windows-x86 -DBUILD_TESTING=ON
cmake --build --preset windows-x86-debug        # or windows-x86-release
```

Presets: `windows-x86`, `windows-x64`, and the `*-mueditor` variants that add the ImGui in-game
editor (F12). The client is a 32-bit application; `windows-x86` is the tested configuration.

> **Ninja / Visual Studio versions:** if you have more than one Visual Studio installed,
> `vswhere -latest` may pick an edition that does not ship Ninja (VS 2026 did not, at the time of
> writing). Configure from the VS 2022 developer prompt and, if needed, pass
> `-DCMAKE_MAKE_PROGRAM=<path to VS2022's ninja.exe>`.

### 4. Run

```powershell
out\build\windows-x86\src\Debug\Main.exe
```

Target server: `config.ini` next to the executable (`ServerIP`, `ServerPort=44405`) or
`Main.exe connect /u127.0.0.1 /p44405`.

## Tests

### Client unit tests (C++, 152 tests)

They exercise the 0.99B protocol layer: ciphers, framing, item/charset encoding, and the exact
bytes of every packet builder — no server needed.

```powershell
cd MuMain-099B\out\build\windows-x86
ctest -C Debug --output-on-failure
```

Requires the client configured with `-DBUILD_TESTING=ON` (see above). `tools/probe099b` is a
manual probe for use against a live server; it is built with the tests but is not part of CTest.

### Server end-to-end tests (Python)

Each script spins up a **disposable PostgreSQL cluster** (`initdb` in a temp dir, loopback,
`trust` auth — your real database is never touched), builds the projects it needs, launches the
servers on free ports and drives them with a simulated client speaking the real encrypted binary
protocol.

```bash
python SharpSSeMU/tests/full_chain_e2e_test.py          # ConnectServer → Join → Data → Game
python SharpSSeMU/tests/gameserver_fase1_e2e_test.py    # login
python SharpSSeMU/tests/gameserver_fase4_e2e_test.py    # monsters & combat
```

Requirements: PostgreSQL binaries, .NET 10 SDK, and the original package folders
(`MuClient/Data/Enc1.dat`, `MuServer99B/Data`) — see
[GETTING_STARTED](GETTING_STARTED.md#2-bring-your-own-game-files). Exit code 0 = pass.

**Known state:** `full_chain` and `fase1` pass completely. The other phases run far (16–36 green
assertions, including all protocol steps) and stop at one point — the `WorldTestClient` combat
sequence — for a fixture-balance reason (the test monster kills the 60-HP starting character),
not a protocol one. Details in `SharpSSeMU/tests/README.md` (Spanish).

## Regenerating the protocol library

The client's `Protocol099B.generated.h` is generated, never edited. With the SSeMU 0.99B emulator
sources available in `Source/`:

```bash
cd MuMain-099B/tools/protogen
GS="../../../../Source/Source/Emulator 0.99 (2.1.7)/GameServer"
CS="../../../../Source/Source/Emulator 0.99 (2.1.7)/ConnectServer"
python parse_protocol.py    --source-dir "$GS" --source-dir "$CS" --out protocol_099b.json
python annotate_opcodes.py  --ir protocol_099b.json --source-dir "$GS" --source-dir "$CS"
python emit_cpp.py          --ir protocol_099b.json --out ../../src/source/Protocol099B/Protocol099B.generated.h
```

The parser refuses to run against the wrong emulator tree (it checks for the `0.99B CHS` marker).
Audit scripts (`audit_client_coverage.py`, `audit_client_structs.py`, `audit_muservercs.py`)
cross-check client and server against the IR. See `tools/protogen/README.md` (Spanish).

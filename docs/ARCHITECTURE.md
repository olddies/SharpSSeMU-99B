# Architecture

🌐 **English** · [Español](es/ARCHITECTURE.md)

## Goals and non-goals

- **Goal:** a readable, hackable C# server that speaks the **exact** MU Online 0.99B (SSeMU 2.1.7)
  wire protocol, and a client that speaks it too, so the two can be developed and verified
  against each other and against the original emulator.
- **Non-goal:** inventing a new protocol or "improving" gameplay. Behaviour follows the original
  emulator's C++ source; deviations are bugs and are documented as such.

## Components

```
                       ┌────────────────────────────────────────────────────┐
                       │                   PostgreSQL  (muonline)           │
                       │ memb_info · character · warehouse · guilds · …     │
                       └───────▲──────────────────▲─────────────────▲───────┘
                               │                  │                 │
                        ┌──────┴─────┐     ┌──────┴─────┐    ┌──────┴──────┐
   client ── 44405 ──►  │ Connect    │     │ Join       │    │ Data        │
   (server list)        │ Server     │     │ Server     │    │ Server      │
                        └────────────┘     └──────▲─────┘    └──────▲──────┘
                                                  │ 55970           │ 55960
   client ── 55900 ──────────────────────►  ┌─────┴─────────────────┴─────┐
   (gameplay, encrypted)                    │          GameServer         │──► reads Data/*.txt|*.dat
                                            └─────────────┬───────────────┘        ▲
                                                          │ writes status.json     │ edits
                                                          ▼                        │
                                                   ┌──────────────┐  ┌─────────────┴─┐
                                                   │ (Data/ dir)  │◄─┤  AdminPanel   │ :5281
                                                   └──────────────┘  └───────────────┘
```

| Component | Original C++ | This port | Responsibility |
|---|---|---|---|
| **ConnectServer** | `ConnectServer/` | `MuServer.ConnectServer` | Answers the client's first connection with the server list; receives load reports from GameServers over UDP |
| **JoinServer** | `JoinServer/` | `MuServer.JoinServer` | Account authentication (`memb_info`) and the account (VIP) level |
| **DataServer** | `DataServer/` | `MuServer.DataServer` | All persistence: character list/creation, character load/save, inventories, warehouses, rankings, friends, kill counts |
| **GameServer** | `GameServer/` | `MuServer.GameServer` | The game: world, monsters, combat, items, skills, parties, events. Talks to the client, JoinServer and DataServer |
| **AdminPanel** | *(new)* | `MuServer.AdminPanel` | Web UI that edits the GameServer's data/config files and the character database, and shows live status |
| **Shared** | — | `MuServer.Shared` | Packet framing, ciphers, script tokenizer, INI reader, logging |

The original talks to SQL Server via ODBC and stored procedures (`WZ_*`); this port replaces that
layer with **Npgsql + PostgreSQL** (`db/postgres/*.sql`). Character names are the primary key.

### GameServer internals (`MuServer.GameServer/`)

| Folder | Contents |
|---|---|
| `Net/` | the game socket (staged decrypt → frame), outgoing connections to Join/DataServer |
| `Protocol/` | `ClientProtocolHandler` (the opcode dispatcher and handlers), packet builders/parsers |
| `World/` | maps, player/monster/party registries, `ViewportTicker` (the 200 ms world tick), items, balance tables, Devil Square |
| `Config/` | typed views over `GameServerInfo - *.dat`, `GameServer.ini` |
| `Data/` | the 8 shipped `GameServerInfo - *.dat` defaults |
| `StatusWriter.cs`, `GlobalMessagePoller.cs` | the file-based bridge to the AdminPanel (`status.json` out, `global-message.json` in) |

**World tick.** `ViewportTicker` runs every 200 ms: respawns dead monsters, sweeps expired ground
items, runs monster AI (target selection, movement, attack — using the same hit/defense/damage
pipeline as players), mana/BP regeneration, party life broadcasts, periodic autosave, and diffs
each player's visible set to send *appear* / *disappear* packets.

**Persistence.** The GameServer keeps a player's state in memory and saves through the DataServer
on disconnect, after experience gains (throttled to once a minute) and on a periodic autosave. Editing a connected character in the
database is therefore lost on the next save.

## The wire protocol

Packets use the classic MU framing — `C1`/`C2` (plain) and `C3`/`C4` (block-encrypted) with 1- or
2-byte lengths — and the GameServer socket applies **three layers**, in this order on receive:

1. **Stream cipher** (port of `HackCheck.cpp`) over *everything*, including the type/length bytes.
   Its key derives from the 17-byte `ServerSerial`.
2. **Block cipher** (SimpleModulus, 8 plaintext bytes → 11 ciphertext bytes) for `C3`/`C4`.
3. **XorData obfuscation** — asymmetric: the client obfuscates what it sends, the server does not.

Because layer 1 hides packet lengths, no generic framer can split the stream first; the socket must
decrypt *before* segmenting. That is why the client's game socket is native C++ instead of using
the C# network library (which still serves the ConnectServer, sent in the clear).

**Traps worth knowing** (each cost real debugging time; details in the
[protocol document](../MuMain-099B/docs/protocol-099b.md)):

- *Errors do not fail, they lie.* A struct one byte too short reads shifted fields and shows wrong
  numbers; one too long makes the packet be silently discarded. Nothing crashes.
- **Trailing struct padding is on the wire** — the emulator sends `sizeof(struct)` for MSVC x86 layout.
- Two class encodings coexist (`base<<4` in the database / create request, `base<<5` in the CharSet).
- Items are **5 bytes** on the 0.99B wire (not 12), with a split index/level/option encoding, and
  two index numberings (32 per section server-side, 512 per group client-side).
- A dispatcher pointing at a *later-season* handler compiles and connects fine, then misreads every
  packet of that opcode. This was the most frequent bug class (party, quests, shop, skills, jewels).

## How correctness is established

No layout or formula is written from memory:

1. **Generated wire structs.** `tools/protogen` parses the emulator headers, models MSVC x86
   layout (alignment, `#pragma pack`, tail padding) and emits `Protocol099B.generated.h` — 259
   structs, 211 with opcodes — each guarded by `static_assert(sizeof)` / `offsetof`.
2. **Audit scripts** (`tools/protogen/audit_*.py`, `tools/gamedata/audit_*.py`) cross-check the
   client, the server and the data tables for size mismatches, unhandled opcodes and disagreeing
   item/skill/monster/shop data.
3. **Ported, not reinvented.** Server logic cites the original file and line it ports
   (e.g. combat: `Attack.cpp` `MissCheck` → `GetTargetDefense` → `GetAttackDamage[Wizard]` →
   damage floor; monsters go through the *same* pipeline, as in the original).
4. **Tests.** 152 client unit tests pin exact packet bytes and crypto against values derived from
   the original algorithm (never against the implementation itself); Python e2e tests drive the
   real server stack with a simulated encrypted client; the ground truth for "does this belong to
   0.99B?" is the original client's `Data/Interface`.

The client is based on a Season 5.2→6 codebase, so it contains many features that do not exist in
0.99B (Rage Fighter, Illusion Temple, Master Skill Tree, Castle Siege…). Before treating any
unported client code as a bug, confirm the feature exists in real 0.99B.

## Data files

The GameServer is data-driven from the original tables: `Data/Monster/MonsterList.txt`,
`Monster/Spawn/*.txt`, `Item/Item.txt`, `Skill/SkillList.txt`, `Shop/*.txt` + `ShopManager.txt`,
`Event/*`, `Move.txt`, gates, quests, and the 8 `GameServerInfo - *.dat` (INI-formatted text with
~520 settings). They are read once at start-up.

> **Development log.** This document is the server's chronological diary: what was ported in each
> phase and the real bugs found while testing. It is historical and very detailed; some sections
> describe a state that has since been superseded. For the current guide see
> [`../README.md`](../README.md) and [`../../docs/STATUS.md`](../../docs/STATUS.md).
>
> 🌐 **English** · [Español](development-log.es.md)

# SharpSSeMU — C#/.NET port of the SSeMU 0.99B (2.1.7) server

A rewrite of the MU Online 0.99B private server in C#/.NET 10, designed to run on Linux while keeping
the **original client unmodified** (`main.exe` + `Main.dll`). The client is not touched at all: the
same binary protocol is preserved byte for byte so that it connects without changes.

## Why the client is not rewritten

The client's 3D/DirectX engine lives inside `main.exe`, whose source code is not part of this package
(it is a closed binary). `Main.dll` is not a simple network shim either: it uses Detours to hook
functions of `main.exe` at **hard-coded memory addresses**. There is nothing there that could
usefully be "converted to C#" — which is why the scope of this project is exclusively the **server**,
replicating the exact protocol so that the current compiled client keeps working as is. It is the same
approach OpenMU uses, the reference C# project for MU Online.

## Current state

| Module | State | Original lines (C++) |
|---|---|---|
| ConnectServer | ✅ Ported and tested end-to-end | ~4,600 |
| JoinServer | ✅ Ported and tested end-to-end (against a real PostgreSQL) | ~7,000 |
| DataServer (character persistence) | ✅ Core ported and tested end-to-end (against a real PostgreSQL) | ~9,300 |
| GameServer | 🔶 Phases 1-7 done and tested end-to-end: **connection/login** + **entering the world** + **items and inventory** (first pass: item format, inventory list, move/equip) + **monsters and combat** (first pass: static monsters, melee attack, death/experience/level/respawn) + **real item/combat balance** (`CharacterCalcAttribute` + real `Item.txt`) + **chat/party/friends** (first pass: public and party chat, local and cross-GameServer whisper, party with level-based experience sharing, friend list with online/offline status) + **Devil Square** (first pass of special events: full state engine, ticket entry, per-stage monster spawn, score, experience/zen reward, ranking in DataServer) + **skills and mana** (first pass: casting single-target attack skills, real magic damage, periodic mana/BP regeneration). Missing: Blood Castle/Chaos Castle/Kalima/Illusion Temple (same engine, different data set — see "Next steps"), Lua (confirmed unused in this package), Guild, PartyMatching, friend mail, the rest of the items (floor pickup, trade, shops), monster AI, PvP, area/duration/combo/teleport skills and the skill-learning system | ~81,500 |
| AdminPanel (web) | 🔶 A new addition with no equivalent in the original: edits the real `Data/` files (exp/drop/zen rates, warps, items, prices, monsters, spawns, shops, skills, gates, quests, messages, events, Chaos Machine and all 8 `GameServerInfo - *.dat`) and shows live status/sends global messages to the running GameServer. With password login; no hot config reload — see "AdminPanel" below | — |

### Real bugs found testing with the real client (main.exe)

The automated tests (WorldTestClient/TestClient) simulate a client that already "knows" several things
in advance, so they did not exercise two steps the real client always performs. Both are already fixed:

- **ConnectServer never sent the server name list (C2:F3:EA)** on connect — `BuildNameListPacket()`
  existed but nobody called it. The real client waits for that packet before showing the server
  selection screen; without it, it disconnects by itself. Fixed in
  `MuServer.ConnectServer/Net/TcpGateServer.cs`.
- **GameServer never answered the character list (C1:F3:00)** when login finished — the real client
  requests it automatically before it can choose a character (0xF3:0x03), and stays stuck on the
  selection screen forever if it does not arrive. `DataServerProtocolHandler` (DataServer) already had
  the whole Postgres query side implemented; what was missing was for the GameServer to request the
  list and convert DataServer's compact answer into the `CharSet[13]` the client expects
  (`ClientProtocolHandler.OnCharacterListRequestAsync`/`OnCharacterListFromDataServerAsync`, reusing
  `PlayerObject.BuildCharSet`). Minimal handlers were also added for 0xF3:0x09 (HardwareId, validates
  GUID format) and 0x0E (client keep-alive, no-op) so that they stop being logged as "not implemented".
- **GameServer never handled character creation (C1:F3:01)** — with an account without characters, the
  real client shows the create-character screen and, on confirming name/class, sends `F3:01`; with no
  answer it stays stuck there forever (same symptom as the character-list bug, but one step later in
  the flow). The DataServer side (`0x02`, `OnCharacterCreateAsync`) was already complete — it was only
  used by the test scripts connecting straight to DataServer to seed data, never by the GameServer
  itself. The missing thin adapter was added: `ClientProtocolHandler.OnCharacterCreateRequestAsync`
  (forwards to DataServer) / `OnCharacterCreateResultFromDataServerAsync` (applies the same `Class`
  byte conversion `DGCharacterCreateRecv` did and sends `PMSG_CHARACTER_CREATE_SEND`). The prior
  class/`CARD_CODE` validation of `CGCharacterCreateRecv` (unlocking MG/DL/SU/RF by account level) was
  not replicated — it is forwarded directly and DataServer, which is already the real authority on
  which classes exist (`default_class_type`), rejects any non-enabled class with `result=2`, so the
  visible effect is the same without duplicating logic. Tested end to end with a real new account (0
  characters → create → list updates → select → enters the world) in
  `tests/gameserver_fase5_e2e_test.py`.
- **GameServer crashed with `ArgumentOutOfRangeException` when processing real movement (C1:D7)** —
  `PMSG_MOVE_RECV` declares `BYTE path[8]` as a fixed field in the original C++ struct
  (`Protocol.h:253-259`), but the real client does not always send all 8 bytes: it only sends those it
  needs according to the number of steps encoded in the nibbles of `path[0]`; reading too much in C++
  is "safe" (adjacent buffer memory, garbage but no exception) but our C# framer builds a `byte[]` of
  exactly the size received on the wire, so `p.AsSpan(5, 8)` blew up with real packets shorter than 13
  bytes. The automated tests never detected it because `FakeMuClient.SendMoveAsync` always sends the
  full 8-byte path. Fixed in `MoveRecv.Parse` (`MuServer.GameServer/Protocol/WorldPackets.cs`) by
  trimming the read to the actual available size (`Math.Clamp(p.Length - 5, 0, 8)`) and filling the
  rest with zeros. `FakeMuClient.SendMoveShortPathAsync` (sends only 1 path byte) was added, plus a
  dedicated test step in `WorldTestClient/Program.cs` so this case stays permanently covered.
- **GameServer crashed with `IndexOutOfRangeException` when processing a real action/pose (C1:18)** —
  same pattern as the movement bug above: `PMSG_ACTION_RECV` declares an optional final field
  `index[2]` in the C++ struct (`Protocol.h:196-202`) that the real client sometimes does not send.
  `ActionRecv.Parse` assumed the fixed length and blew up with `p[5]`/`p[6]` out of range. Fixed by
  defensively reading byte by byte (`MuServer.GameServer/Protocol/WorldPackets.cs`), like
  `MoveRecv.Parse`. Reported by the user with production logs (6 repeated exceptions in under 15
  seconds of playing with `main.exe`).
- **Only the account's first character was visible on the selection screen** (reported by the user:
  "I only see the first one I created" with 4 characters created) — root cause confirmed by reading the
  real struct: this build (`GAMESERVER_UPDATE=803`) uses `BYTE CharSet[18]` everywhere (`User.h:778`,
  `Protocol.h:767`, `Viewport.h`, `Friend.h`, `ItemManager.h`), not the `CharSet[13]` this port had
  assumed since earlier phases (an older build size). In addition, `PMSG_CHARACTER_LIST_SEND`
  (`Protocol.h:750-759`) has an `ExtWarehouse` byte in the header (`GAMESERVER_UPDATE>=602`) that was
  missing, and each `PMSG_CHARACTER_LIST` row (`Protocol.h:761-769`) sends `GuildStatus` at the end
  (missing) and, having no `#pragma pack(1)`, the real compiler inserts 1 byte of padding to align the
  `WORD Level` (also missing). With all this together, the first character (slot 0) was read "by luck"
  with the fields shifted but landing in the right place for the name; the second character onwards was
  completely misaligned and the client discarded it. Fixed in
  `PlayerObject.CharSet`/`RebuildCharSet`/`BuildCharSet` (18 bytes, plus the wing branches
  `GAMESERVER_UPDATE>=601` and the extended pet variants `>=201`/`>=401`/`>=601`, re-derived byte by
  byte from `ObjectManager.cpp:1753-2038` and `DSProtocol.cpp:755-1075`) and in
  `ClientPackets.CharacterListSend` (`ExtWarehouse` byte, alignment padding, `CharSet[18]`,
  `GuildStatus`). Incidentally, re-reading the full struct of `PMSG_VIEWPORT_PLAYER`
  (`Viewport.h:45-70`) to fix its `CharSet` also revealed that it was completely missing the fields
  `attribute`/`MuunItem[2]`/`level[2]`/`MaxHP[4]`/`CurHP[4]` (added in `GAMESERVER_UPDATE>=701`/`>=803`)
  and that the effect counter (`count`) was in the old position (2 bytes, before the name) instead of
  at the end (1 byte, after `CurHP`) — fixed in `WorldPacketBuilder.ViewportPlayerAppear`, including
  the non-standard packing of `MaxHP`/`CurHP` (`SET_NUMBERHB(SET_NUMBERHW(x))`, byte order
  `[b31-24, b15-8, b23-16, b7-0]`, not standard big-endian). `attribute` and `MuunItem` are always sent
  as 0/empty because the elemental attribute and Muun systems are not ported. Covered by a dedicated
  test: the `test` account of `tests/gameserver_shop_e2e_test.py` now seeds a second character
  (`Hero1Two`) and `WorldTestClient` explicitly verifies it is read at the correct slot and offset (a
  direct regression of the reported bug, not just "the first one still works").

### ConnectServer: what was implemented

- C1/C2 packet framing identical to the original (`MuServer.Shared/Protocol/PacketFramer.cs`).
- `MemScript` parser (the same plain-text tokenizer that **all** modules of the original server use for
  their `Data/` files) — ready to be reused in JoinServer/DataServer/GameServer.
- `BlackList.txt` and `ServerList.dat` are read as they are (same format, same relative paths).
- `ConnectServer.ini` keeps being used unchanged (same keys: `ConnectServerPortTCP`,
  `ConnectServerPortUDP`, `MaxConnectionPerIP`, `MaxPacketPerSecond`, `MaxConnectionIdle`).
- UDP heartbeats 0xA1 (GameServer) / 0xA2 (JoinServer) to know which servers are alive.
- Client protocol: `0xF4:0x02` (request server list), `0xF4:0x03` (request a server's IP:port),
  automatic sending of `0xC1:00` (init) and `0xF3:0xEA` (name list) on connect.
- Per-IP connection limit, packets-per-second limit, session-duration limit — with the same semantics
  (and the same oddity) as the original: `MaxConnectionIdle` is measured from when the client connects,
  not from the last activity (that is how the original C++ behaved: it never updated the "online time").
  Documented in the code where relevant.
- Console commands equivalent to those of the Windows menu: `reload blacklist`, `reload serverlist`,
  `reload config`, `exit`.

Tested with a simulated TCP + UDP client: it connects, receives init + list, sees a GameServer appear
after its heartbeat with the correct user percentage, and resolves a server's IP:port — byte for byte
equal to the format the real client expects.

### JoinServer: what was implemented

- Complete GameServer↔JoinServer protocol: `0x00` (server info), `0x01` (account login), `0x02`
  (logout), `0x05` (VIP level query), `0x11` (save VIP level), `0x20` (online users), `0x30` (external
  kick) — same structs/sizes as `JoinServerProtocol.h`. All packets use `PBMSG_HEAD` (C1, no
  sub-code), unlike ConnectServer.
- `AllowableIpList.txt` (GameServer IP whitelist) with the same "0 ... end" format. There is no
  rate-limit/idle-timeout here because the original did not have one either (few trusted
  connections, not public clients).
- In-memory account sessions (equivalent to `CAccountManager`): prevents double login, expires
  hanging "moves between servers" at 30s (like `DisconnectProc`), and cleans everything up if the owning
  GameServer goes down (`ClearServerAccountInfo`).
- Client UDP heartbeat `0xA2` to ConnectServer every 1s.
- **Data layer migrated from SQL Server/ODBC to PostgreSQL** (`Npgsql`, parameterised queries — the
  original built the SQL with `sprintf`, vulnerable to injection; this was fixed here without changing
  behaviour). The 4 stored procedures JoinServer used directly were translated: `WZ_CONNECT_MEMB`,
  `WZ_DISCONNECT_MEMB`, `WZ_GetAccountLevel` (expires the VIP level if it lapsed) and
  `WZ_SetAccountLevel` (same level = adds seconds; different level = restarts the expiry) — see
  `Db/NpgsqlAccountRepository.cs`. The schema of the 2 tables it uses (`memb_info`, `memb_stat`) is in
  `db/postgres/001_accounts.sql`, with the name mapping documented in the file itself.
- **Pending/not yet ported**: the `.ini`'s `MD5Encryption=1` mode (MU's own scheme with an `MD5_KEYVAL`
  substitution table, not standard MD5) — for now only `MD5Encryption=0` works (plain-text password,
  which is the factory default of the original `.ini`).

Tested end to end against a real PostgreSQL (started from scratch, schema applied, seed `test`/`admin`
accounts): login, double-login rejection, non-existent account, raising the VIP level and verifying
that it persists in the database after logout/login, logout, and the UDP heartbeat — all byte for byte
against the format GameServer expects.

### DataServer: what was implemented

- Complete DataServer↔GameServer protocol (all in-scope codes): `0x00` server info, `0x01` character
  list, `0x02` create character, `0x03` delete character, `0x04` character info, `0x05` create item,
  `0x06`/`0x1A` option data (wings/skin/etc.), `0x07` pet info, `0x08`/`0x09` mail/global notice,
  `0x0B` monster-kill counter, `0x10` save full character (the biggest struct, ~30 fields), `0x11`
  save inventory, `0x12` save options, `0x13` save pets, `0x14`/`0x15` save reset/master-reset,
  `0x16`-`0x19` save rankings (Blood/Chaos/Devil/Illusion), `0x1B` creation card, `0x20`/`0x21`
  connect/disconnect character, `0x30` global whisper — same structs/sizes as `DataServerProtocol.h`.
- Inventory compaction for the character selection screen: the original reduces each equipment slot
  from 16 to 5 bytes (`CompactInventory`) — ported byte for byte, including empty-slot detection
  (`0xFF`/specific bit masks).
- `BadSyntax.txt` (forbidden account/character names) with the same "..." format as the original.
- Reset/master-reset/event counter with the same daily/weekly/monthly "restart" logic the original
  solved with SQL Server's `DATEDIFF` — here approximated with calendar comparison (`ISOWeek` for the
  week), documented in the code as an intentional approximation (exact behaviour at time-zone
  boundaries may differ slightly from the original; it does not affect ~99% of cases).
- **Data layer migrated from SQL Server/ODBC to PostgreSQL** (`Npgsql`, parameterised queries). 13
  tables in `db/postgres/002_characters.sql` + `003_default_class_seed.sql`: `account_character`,
  `default_class_type`, `character`, `option_data`, `reset_data`, `event_entry_count`, `ranking_duel`,
  `ranking_blood_castle`, `ranking_chaos_castle`, `ranking_devil_square`, `ranking_illusion_temple`
  (new table, the original did not rank Illusion Temple), `monster_kill_count` (new table, previously
  it lived only in memory), `pet_item_info`.
- **Real bug found and fixed in the original package**: `DataServerProtocol.h` (the header DataServer
  uses) lacks 17 bytes (`IsNewChar`, `Married`, `MarryName[11]`) that `DSProtocol.h` (the header the
  GameServer uses for the same packet) does expect — an out-of-sync version, probably the remains of a
  half-implemented marriage feature. Since both sides of the protocol are controlled, it was fixed by
  adding those 3 fields (with default 0/0/"") in the `CharacterInfoSend` builder, documented in the code.
- **Pending/not yet ported**: warehouse (vault), guilds, `CustomPick`, and the rest of the DataServer
  packets outside the scope of characters/inventory/rankings — to be added when the corresponding
  GameServer phases need them.

Tested end to end against a real PostgreSQL: list characters (empty, with Hero, after deleting), create
character (success and duplicate name), load character info, save and re-read character info (verifies
real persistence), accumulate Blood Castle ranking, count monster kills x2, delete character. 12/12
cases OK.

### GameServer — Phase 1: connection core and login

Of the 6 phases planned to port the game core (79k lines), Phase 1 covers the real client's socket
and login. It is the most delicate part of the whole project because, unlike
ConnectServer/JoinServer/DataServer (flat C1/C2 protocol between trusted processes), the real client's
socket uses **4 superimposed obfuscation/encryption layers**:

1. **Stream cipher over the whole socket** (`GameStreamCipher`, port of `HackCheck.cpp`): XOR +
   byte-by-byte subtraction over absolutely everything that goes in/out, with 1-byte keys derived at
   start-up from a fixed "CustomerName" (`"SSE"`) XORed against the `.ini`'s `ServerSerial`.
2. **Block cipher** (`PacketCipher`, port of `PacketManager.cpp`), only for packets marked C3/C4 (e.g.
   login): converts 8-byte blocks into 11-byte blocks using `Modulus/Key/Xor` tables loaded from the
   original binary files (`Hack/Enc2.dat`/`Dec1.dat` on the server side, `Enc1.dat`/`Dec2.dat` on the
   client side) — **no key was invented**, the package's real files are read.
3. **Chained XOR de-obfuscation** (`XorData`, inside `PacketCipher`), on receive only:
   `buff[n] ^= buff[n-1]^Filtro[n%32]`. The original never applies it when sending (confirmed by reading
   `SocketManager.cpp`) — the real client (a closed binary) must carry the "forward" counterpart, which
   was deduced by solving the recurrence and is used only in the test harness.
4. **Argument XOR** (3 bytes, `{0xFC,0xCF,0xAB}`) applied specifically to the account/password fields
   inside the login packet, on top of everything above.

The 4 layers were validated with real material: first with a `PacketCipher` round-trip using the
package's 4 real `.dat` files (client↔server in both directions), and then with a C# test client
(`src/TestClient/`) that builds a login **exactly as the real client would** — same keys, same block
cipher, same obfuscation — because there is no way to have the real `.exe` running in this environment.

**What was implemented:**

- `GameStreamCipher`, `PacketCipher` and `GameClientFramer` in `MuServer.Shared/Crypto/` — the complete
  framer that replicates `CSocketManager::DataRecv` + `CPacketManager::ExtractPacket`: separates
  C1/C2/C3/C4 packets, block-decrypts where applicable, and "synthesises" the final logical packet as
  the rest of the protocol sees it.
- Login handshake: the server sends `GCConnectClientSend` (C1:F1:00, assigned index + server
  version/code) without block encryption; the client answers `PMSG_CONNECT_ACCOUNT` (C3:F1:01) with
  account/password; the server validates the client version and serial against the `.ini` (rejects with
  result 6 if they do not match), forwards the account/password to JoinServer (reusing the already
  tested protocol) and returns the real result to the client.
- Disconnect notification to JoinServer (`GJDisconnectAccountSend`) — without it an account stayed as a
  "ghost" connected in JoinServer after closing the socket, and the same client's next login attempt
  failed with "already connected" instead of really validating credentials. Found and fixed thanks to
  the end-to-end test with the real test client.
- Outgoing connections to JoinServer and DataServer (automatic reconnection if they drop), and the
  `GameServerConfig` container (the subset of `ServerInfo.h` needed for this phase — the original has
  ~500 game-balance fields which are added only when the phase that uses them needs them).
- **Pending/out of scope for Phase 1**: everything that happens after login — entering the world, maps,
  movement, characters, items, combat, chat, guilds, GM commands, special events and the Lua engine. Any
  packet with a head other than `0xF1` is logged as "not implemented" and ignored, and does not break
  the connection.

Tested end to end with real JoinServer + DataServer + PostgreSQL running, and the test client building
authentic packets with the package's real keys: valid login (result 1), invalid password (result 0),
non-existent account (result 2), and re-login of the same account after a clean disconnect (confirms
the disconnect notification works and left no ghost session). 5/5 cases OK. The test script stays in
`tests/gameserver_fase1_e2e_test.py` to re-run whenever wanted.

**Second round of tests, with ConnectServer added to the chain** (to validate the full flow the real
client uses: ConnectServer → server list → IP resolution → GameServer), using the package's real values
(`ServerVersion=1.02.00`, `ServerSerial=PoweredSetecSoft`, taken from
`MuServer99B/GameServer/DATA/GameServerInfo - Common.dat` and `MuServer99B/Tools/GetMainInfo/MainInfo.ini`),
it found and fixed two more bugs:

- **`ServerVersion` badly parsed**: the original builds the 5 version bytes by taking specific indices
  `{0,2,3,5,6}` of a dotted string like `"1.02.00"` (to discard the dots, see `ServerInfo.cpp` lines
  396-408) — the initial port copied the first 5 raw bytes of the string, which with the package's real
  value gave an incorrect `ClientVersion` that the real client would have rejected. Fixed in
  `GameServerConfig.cs`.
- **The GameServer's UDP `0xA1` heartbeat to ConnectServer was missing** (the original's
  `GameServerLiveProc`) — without it, ConnectServer never shows the server in the list nor can it
  resolve the IP:port for the client, even though login itself worked when testing straight against
  GameServer. `Net/ConnectServerHeartbeatClient.cs` was added (same pattern as JoinServer's).

With both fixes, tested end to end with the 4 real servers + PostgreSQL: the heartbeat arrives,
ConnectServer shows the server and resolves `127.0.0.1:55900` correctly, and the authenticated login
(with the client's real encryption keys) returns result 1. Script in `tests/full_chain_e2e_test.py`. See
`COMO_PROBAR_CON_CLIENTE_REAL.md` / `TESTING_WITH_REAL_CLIENT.md` for instructions on bringing everything
up and testing with `MuClient/main.exe`.

### GameServer — Phase 2: entering the world

With login already working (Phase 1), Phase 2 covers everything needed for the client to select a
character, appear on the map, see other players and move — the port of `User`/`ObjectManager`/`Map`/
`Viewport`/`Move` from `Protocol.cpp`.

**What was implemented:**

- `GameMap`/`MapRegistry` (`World/`): port of `CMap` — loads `Terrain<N>.att` (3-byte header + a
  `width*height` grid, indexed `[y*height+x]` like the original), blocking attributes (bits `4`/`8`,
  confirmed byte by byte against `Protocol.cpp:1023` and `ObjectManager.cpp:2621`) and the dynamic
  "occupied" bit (`SetStandAttr`/`DelStandAttr`, bit `2`).
- `PlayerObject`/`PlayerRegistry`: replaces the original god-struct `OBJECTSTRUCT` (`gObj[10000]`) with
  an idiomatic class + dictionary — the server's internal structures do not need to be byte-exact, only
  the network protocol.
- `ViewportTicker`: periodic tick (200ms) that makes nearby players appear/disappear according to view
  range (12 tiles), plus immediate broadcast on every movement — behaviourally equivalent to
  `gObjViewportProc` + the original's `VpPlayer[]`/`VpPlayer2[]`, simplified to a single
  `HashSet<int> VisibleTo` per player.
- Character selection (`0xF3:0x03`): forwards to DataServer (`0x04`), builds `PlayerObject` with all the
  returned fields, computes the appearance `CharSet[13]` (byte 0 with the exact formula
  `ChangeUp*16 - byte0/32 + Class*32`; the rest of the equipment bytes are deferred to Phase 3, when the
  item system is ported), and answers `CHARACTER_INFO_SEND` (C3) + `NEW_CHARACTER_INFO_SEND`.
- Movement (`0xD7`): decodes the 8-byte path packed in nibbles using the 8-direction `RoadPathTable`
  (`Util.cpp`), validates against a ±15-tile box and against the map's blocked tiles, and if valid
  broadcasts `MOVE_SEND` to the current observers (`VisibleTo`) — port of `CGMoveRecv`
  (`Protocol.cpp:901-1076`). The time-counter anti-speedhack is not ported: it ships disabled by default
  in the original `.ini` (`CheckMoveHack=0`), so omitting it does not change the factory behaviour.
- Sending encrypted C3 packets from the server (`ClientSession.SendEncryptedAsync`) — until now the
  server only received C3 (login), it never sent them. It was confirmed by reading
  `SocketManager.cpp::DataSend` that the original does **not** apply XorData de-obfuscation when sending
  (an intentional asymmetry, see below).
- **A real protocol finding**: the server's XorData de-obfuscation is applied to **every** incoming
  packet, not just C3/C4 (login) — it includes plain C1/C2 such as character selection and movement.
  Phase 1 never needed it because the only packet the client sends there is the login. It was discovered
  with a real test that sent `0xF3:0x03` without that layer and the server received a corrupted header;
  it was fixed by adding the "forward" obfuscation (`PacketCipher.ObfuscateInPlace`, the mathematical
  counterpart of `DeobfuscateInPlace`) to the test harness, since the real client (a closed binary) must
  ship it from the factory.
- **Pending/out of scope for Phase 2**: full `CharSet` rendering (weapon/armour slots — depends on the
  Phase 3 item system), NPCs/monsters in the viewport, teleport/portals, and the time-counter
  anti-speedhack (off by default just like the original).

Tested end to end with real JoinServer + DataServer + PostgreSQL and **two** simultaneous authenticated
test clients (`src/WorldTestClient/`): both log in, select a character, enter the world (they receive
and decrypt `CHARACTER_INFO_SEND` correctly), see each other appear through the viewport (`0x12`), and
one's movement propagates in real time to the other (it receives its own echo and the other client
receives the broadcast) — 4/4 cases OK. Script in `tests/gameserver_fase2_e2e_test.py`.

While debugging this test it was found (and left documented as a lesson, not as a server bug) that the
spawn coordinates initially chosen for the test fell on really blocked tiles of the real
`Terrain1.att` (`attr & 0x04` set) — the server correctly rejected the movement by sending a correction
`POSITION_SEND` (`0xD0`) instead of `MOVE_SEND` (`0xD7`), which is exactly the expected behaviour; the
adjustment was to choose really walkable test coordinates, not a change to the server.

### GameServer — Phase 3: items and inventory (first pass)

With the character already in the world (Phase 2), Phase 3 starts the item system — the port of
`Item`/`ItemManager` from `Item.h`/`ItemManager.cpp`. This first pass covers the core needed to have
real equipment (not just the opaque blob DataServer already stored/forwarded since Phase 1): the item
format that travels over the network, the inventory list on entering the world, move/equip/unequip
within one's own inventory, and having the equipped gear reflected in the `CharSet` (one's own and other
players' through the viewport).

**What was implemented:**

- `Item` (`World/Item.cs`): port of the three different byte formats the original uses for the same item
  (confirmed byte by byte against `ItemManager.cpp:1593-1690`): 5 bytes for the client-server wire
  (`ToWireBytes`/`ItemByteConvert`), 16 bytes for persistence in DataServer (`ToDbBytes`/
  `DBItemByteConvert`, of which only 10 have real content) and its read inverse (`FromDbBytes`/
  `ConvertItemByte`). The item identifier (`Index`) is a single WORD packed as `section*32 + subindex`
  (`GET_ITEM`), like the original.
- `PlayerObject.Items[108]`: the inventory decoded from the raw blob DataServer already sent
  (`DecodeInventory`) — the blob and the decoded array are kept in sync via `SetItem` so that a future
  save to DataServer does not have to re-serialise all 108 slots.
- `RebuildCharSet` (Phase 2) now uses real equipment: a full port of `CharacterMakePreviewCharSet`
  (`ObjectManager.cpp:1139-1268`) — the equipped weapon/helm/armour/pants/gloves/boots/wings/pet are
  reflected in the 13 appearance bytes with the same exact bit formula as the original (including
  excellent/set glows). The "full set" bit stays pending for Phase 4 (it depends on recalculating
  attributes).
- Packets (`Protocol/WorldPackets.cs`, class `ItemPacketBuilder`): `ItemListSend` (`C4:F3:10`, full list
  on entering the world — the first packet of this phase that needs a 2-byte size, see below),
  `ItemMoveRecv`/`ItemMoveSend` (`C1:24`/`C3:24`, move/equip/unequip within the inventory),
  `ItemChangeSend` (`C1:25`) and `ItemEquipmentSend` (`C1:F3:13`, updated CharSet for the client itself).
- `ClientSession.SendEncryptedC4Async`: a variant of `SendEncryptedAsync` with a 2-byte size header (C4
  instead of C3) — needed because a full inventory list can exceed the 255 bytes that fit in C3's
  1-byte size. Same contract (block encryption, no outgoing XorData).
- `OnItemMoveAsync`: move an item from one slot to another within one's own inventory (swaps if the
  destination is occupied). If the source or destination slot falls in the equipment range (0-11), it
  rebuilds the `CharSet`, notifies the client itself (`ITEM_EQUIPMENT_SEND`) and refreshes the appearance
  for the current observers by re-sending a `VIEWPORT_PLAYER_APPEAR` with the new data (the original
  uses a dedicated viewport "change" packet that was not ported yet — reappearing achieves the same
  visual result).
- **Pending/out of scope for this Phase 3 pass** (documented so as not to confuse "not implemented" with
  "bug"): validation that the item type is compatible with the destination slot (e.g. the original
  prevents putting a helm in the weapon slot — here any item goes in any slot), picking up/dropping floor
  items (`ItemBag`/`CMapItem`, needs the map's item viewport), Trade, Warehouse, ChaosBox, Shop/NPC and
  Personal Shop (`SourceFlag`/`TargetFlag` other than `0` are rejected with `result=0` instead of being
  half-implemented), using/consuming items (potions, scrolls — the effect logic is Phase 4), and the
  damage/defense/requirements calculation from `Item.txt` (the balance table is not loaded in this port
  yet).

Tested end to end reusing the Phase 2 harness (`src/WorldTestClient/`), seeding a real sword directly in
the database (Hero1's inventory slot 12, with the same 16-byte format DataServer would use) so as not to
depend on floor pickup (not yet ported): both clients receive `ITEM_LIST_SEND` with the correct item
count (including the 2 starter items `003_default_class_seed.sql` already seeded for Hero2's character),
A equips the seeded sword (`ITEM_MOVE_SEND` with `result=1`), `ITEM_EQUIPMENT_SEND` reflects the weapon
in `CharSet[1]`, and B sees A's updated `CharSet` through the viewport after equipping — 3/3 cases OK
(in addition to the 4 inherited from Phase 2, which keep running in the same script). Script in
`tests/gameserver_fase3_e2e_test.py`.

### GameServer — Phase 4: monsters and combat (first pass)

With the character in the world and the real equipment of Phase 3, Phase 4 starts monsters and basic
combat — a port of `Monster.cpp`/`MonsterManager.cpp`/`MonsterSetBase.cpp`/`Attack.cpp` (plus the
monster-death branch of `CObjectManager::CharacterLifeCheck`, `ObjectManager.cpp`). A deliberately
bounded first pass: **static** monsters (no patrol/chase AI) that the player can attack and kill, with
experience, levelling and respawn — confirmed safe at protocol level to leave them static for now (an
idle monster never enters the AI state that fires its own movement/attack packets, so a real client
cannot be missing anything on this side).

**What was implemented:**

- `MonsterInfoTable` (`World/MonsterInfo.cs`): loads `Data/Monster/MonsterList.txt` (the per-monster-class
  balance table — HP, damage, defense, hit/dodge rates, exp, etc.), the same `MemScript` format already
  used by `ServerList.dat`/`BlackList.txt`. The server's global multipliers (`m_MonsterMaxLifeRate` and
  similar) are not ported yet — equivalent to having them all at 100 (no change), the default of an
  untouched package.
- `MonsterSpawnTable` (`World/MonsterSpawnTable.cs`): loads `Data/Monster/Spawn/"NNN - Map.txt"` (the map
  number comes from the file name). Supports the 3 most common row types: fixed point, fixed point with
  ±3 jitter, and rectangular box + count (retrying up to 100 random points rejecting blocked/safe-zone
  tiles, like the original's `GetBoxPosition`).
- `Monster`/`MonsterRegistry` (`World/Monster.cs`, `World/MonsterRegistry.cs`): live monster instance +
  registry with the same index range the original reserves for monsters (0-7999, separate from the
  9000-9999 player range `PlayerRegistry` already used).
- Monster viewport (`ViewportTicker`): the same mechanism as the Phase 2 player viewport but for the
  monster registry — appear (`PMSG_VIEWPORT_MONSTER`, `C2:13`) and disappear (reuses
  `PMSG_VIEWPORT_DESTROY_SEND`, `C1:14`, the same packet as players).
- Basic melee attack (`ClientProtocolHandler.OnAttackAsync`, port of `CGAttackRecv`/`CAttack::Attack`,
  `C1:D9`): validates map/range/safe-zone like the original, computes hit/dodge
  (`CAttack::MissCheck`), target defense and raw damage with the same per-level damage floor
  (`Attack.cpp:365-366`), and sends the animation (`PMSG_ACTION_SEND`, `C1:18`) and damage number
  (`PMSG_DAMAGE_SEND`, `C1:D9`).
- Death, experience and levelling (`OnMonsterDeathAsync`/`GrantExperienceAsync`): on reaching 0 life it
  sends `PMSG_USER_DIE_SEND` (`C1:17`) to those who saw it, distributes experience proportional to each
  attacker's accumulated damage (`CharacterCalcExperienceAlone`, with the same level-difference penalty
  as the original), sends the experience popup (`PMSG_REWARD_EXPERIENCE_SEND`, `C1:9C`) and, if it
  levelled up, full heal + `PMSG_LEVEL_UP_SEND` (`C1:F3:05`).
- Respawn (`ViewportTicker.RespawnDeadMonsters`): the same timer as the original (`RegenTime*1000 +
  1000ms` fixed grace), revives at the original spawn point (re-resolving the position if it was a
  random box) with full life.
- **Pending/out of scope for this first pass** (documented so as not to confuse "not implemented" with
  "bug"): monster AI (patrol/chase/aggro — `gObjMonsterUpdateProc`), monster counter-attack towards the
  player, PvP, skills/magic, combos, party experience sharing (`CharacterCalcExperienceParty` — Social/
  Party is Phase 5), item drop on monster death. The real item/damage/defense balance
  (`PlayerObject.PhysiDamageMin/Max/Defense/AttackSuccessRate/DefenseSuccessRate`, which in this first
  pass came from a placeholder formula by Level/Strength/Agility/Vitality) was completed later — see the
  second pass below.

Tested end to end by extending the Phase 2/3 harness (`src/WorldTestClient/`): a synthetic spawn file
was seeded (a real spider, class 3, `Type=0` in `MonsterList.txt`) at a Lorencia coordinate confirmed
outside the safe zone by reading the real `Terrain1.att` (unlike `(125,125)`, used in Phases 2/3 for
viewport/movement, which is a safe zone and therefore useless for testing attack). Client A attacks it
until it dies, receives the experience popup, and — after waiting the configured respawn time (~11s) — a
new attack confirms that it came back with full life. 3/3 cases OK (plus the 7 inherited from Phases
2/3). Script in `tests/gameserver_fase4_e2e_test.py`.

### GameServer — Phase 4 (second pass): real item/combat balance

Replaces the placeholder damage/defense formula with the real port of
`CObjectManager::CharacterCalcAttribute` (`ObjectManager.cpp:1887-2523`) + the real balance of
`Data/Item/Item.txt` — from here on the equipped weapon and armour **do** change the player's
damage/defense, not just their appearance (`CharSet`).

**What was implemented:**

- `ItemBalanceTable` (`World/ItemBalance.cs`): loads the 16 sections of `Data/Item/Item.txt` (`MemScript`
  format, a different column layout per section — weapons, shield, helm/armour/pants, gloves, boots,
  wings, pets/rings, jewels/potions, orbs/scrolls — confirmed column by column against the real file's
  header comments). Generalised so that any item can resolve its balance stats by index
  (`section*32+subindex`, like the original's `GET_ITEM`).
- `CharacterBalanceConfig` (`Config/CharacterBalanceConfig.cs`): loads the constants of
  `GameServerInfo - Character.dat` (base physical damage/hit rate/defense per class — DW/DK/FE/MG/DL),
  with the same defaults the real file carries if it is not copied (a deployment without the `.dat` runs
  with factory balance anyway).
- `ItemCombatMath` (`World/ItemCombatMath.cs`): port of the +0..+15 upgrade-level scaling of
  `CItem::Convert` (linear `+level*3`, plus a quadratic "extra" — triangular numbers — for +10..+15;
  shields scale Defense differently: flat `+level`, without the extra). Without the excellent/set-item
  branches (`ItemOption.txt`/`SetItemOption.txt`, not ported — no item generated today has those options
  active anyway).
- `PlayerObject.RecalcCombatStats(ItemBalanceTable, CharacterBalanceConfig)`: the port itself — base
  physical damage per class (with FE's alternative bow formula), contribution of the equipped weapon(s)
  (a staff only adds half, it is a "magic weapon"), arrow/bolt bonus by ammunition level, a 55%
  dual-wield penalty (DK/MG/DL with two melee weapons), attack success rate, and defense/defense rate
  (Dexterity + the sum of the equipped armour/shield/wing pieces). It is recalculated on entering the
  world and also when equipping/unequipping (`OnItemMoveAsync`), not just once.
- **Explicitly documented simplifications** (in the `RecalcCombatStats` doc-comment): no
  critical/excellent/set-item (they depend on `ItemOption.txt`/`SetItemOption.txt`), no
  Defense/DefenseSuccessRate bonus for "5 armour pieces at the same high level"/"same visual set"
  (`ObjectManager.cpp:2314-2421`, depends on comparing visual indices between pieces), no
  PhysiSpeed/MagicSpeed (there is no server-side attack cooldown yet), no magic damage (no skills ported),
  and no PvP variants of hit/defense (this phase is player-versus-monster only).

Tested in two ways: (1) full regression of the Phase 2-6 harnesses (`gameserver_fase4_e2e_test.py`,
`gameserver_fase6_e2e_test.py`) copying the real `Item.txt`/`GameServerInfo - Character.dat` into the
test environment — 0 failures, including the real Devil Square combat (monsters with Defense 35-45) with
a character with no weapon equipped (only high Strength), and (2) a targeted check: Hero1's default item
(a ring, not a weapon) was replaced with a real sword ("Kris", `GET_ITEM(0,0)`, upgrade level +1) before
the already-existing equip-and-attack step in the Phase 4 flow — the damage observed in the server log
went from a fixed value of 2 (no weapon, class base formula only) to 11-16 per hit, exactly the range
computed by hand (`PhysiDamageMin`=12, `PhysiDamageMax`=18, minus the test spider's Defense=1),
confirming that the equipped weapon now truly takes part in the calculation.

### GameServer — Phase 5: chat, party and friends (first pass)

Port of Party.h/.cpp, the chat part of Protocol.cpp (`CGChatRecv`/`CGChatWhisperRecv`) and
Friend.h/DataServer/Friend.cpp. Guild (guild war, marks, alliances), PartyMatching (the party-search
board) and friend mail (`T_FriendMail`) are explicitly left out of this pass — the research before
implementing confirmed that none of the three is a hard dependency of basic chat/party/friends (they
are independent features mounted on top, not underneath).

**What was implemented:**

- **Public chat** (`C1:00`): echo to the speaker + broadcast to everyone who has them in their
  `VisibleTo` — reuses the same viewport mechanism that has moved players since Phase 2, like the
  original (`MsgSendV2`/`VpPlayer2[]`). Commands (`/`) and guild/Gens (`@`/`@@`/`@>`/`$`) are silently
  ignored (not ported). Party (`~`) sends the message as is, stealth included, to each member by direct
  `DataSend` — without going through the viewport, so it arrives regardless of map or distance, like the
  original.
- **Whisper** (`C1:02`): looks for the recipient first in this same GameServer process (equivalent to
  `gObjFind`); if not there, it uses the `0x72`/`0x73` protocol between GameServer and DataServer (the
  DataServer side was already complete from an earlier phase — this phase only added the missing
  GameServer half) for a cross-GameServer whisper within the same realm, with routing resolved by
  DataServer's in-memory registry (`CharacterSessionStore`).
- **Party** (`C1:40`-`C1:44`): invite/accept/reject, leave/kick (only the leader can kick others), the
  full list retransmitted to all members on every change, life/mana bars in periodic broadcast (~2s, via
  `ViewportTicker`), and automatic leader migration if slot 0 leaves (with `List<int>` no separate
  "promotion" step is needed as in the original's flat array with holes). A party dissolves entirely if
  it is left with 1 member after a departure (same threshold as the original: from 2 to 1 always
  dissolves). Maximum size 5, like the original.
- **Party experience sharing** (`CharacterCalcExperienceParty`): when the attacker who finishes a monster
  is in a party of 2+ members, the experience "loot" is computed over the TOTAL damage the whole party did
  to it (adding up everyone's, not just whoever dealt the final blow) and distributed proportionally to
  LEVEL among the members on the same map and within ≤10 tiles of the monster (`MAX_PARTY_DISTANCE`) —
  not by the damage each one did, so a member who did not get to hit it still gets their share if in
  range. The bonus tables by party size/class diversity (`m_PartyGeneralExperience`/
  `m_PartySpecialExperience`) are not ported yet — equivalent to no bonus (the same class of technical
  debt as the rest of Phase 4's global multipliers).
- **Friends** (`C1:C0`-`C1:C4`): request/accept-reject/delete/list, with real-time push of online/offline
  status changes (reuses the same DataServer in-memory registry that already fed whisper). Unlike the
  original (which indirects through a numeric GUID via `T_FriendMain`), here it is referenced directly by
  character name, since `character.name` is already a unique PK in this port — a schema simplification
  with no visible change of behaviour. New tables: `db/postgres/004_friends.sql` (`friend_list`,
  `friend_request`). The internal DataServer↔GameServer protocol for this (head `0xB0`) uses its own
  sub-code scheme instead of mirroring the original's `PSBMSG_HEAD` sub-codes 1 to 1, since this port
  does not need binary compatibility with an external DataServer.

Tested end to end by extending the Phase 2/3/4 harness (`src/WorldTestClient/`): public chat (echo +
reaches the other player), local whisper, invite/accept party (2-member list on both clients), party
chat, finishing a monster with the party active (both members receive their experience popup even though
only one attacked), B leaving (the party dissolves and A also receives the notice), and the complete
friends flow (request, accept, symmetric confirmation on both sides, list with online status, delete).
15/15 new cases OK (plus those inherited from Phases 2/3/4). Script in `tests/gameserver_fase5_e2e_test.py`.

### GameServer — Phase 6: special events, first pass (Devil Square)

Port of `DevilSquare.h/.cpp` + the "Devil Square" portion of `Protocol.h/.cpp`
(`CGDevilSquareEnterRecv`, `CGEventRemainTimeRecv`) + the ranking save in DataServer
(`GDRankingDevilSquareSaveSend`, head `0x3F` — the DataServer side was already complete from before this
phase, it only needed the GameServer to call it). Prior research (a dedicated subagent) confirmed that of
the original's 4 box events (Blood Castle, Chaos Castle, Devil Square, Kalima) all have real data in this
package, that Illusion Temple and Golden Archer are dead code in this build (`GAMESERVER_UPDATE=803`,
gated by `#if`), and that Lua is "wired" into the engine but with no functional script in the shipped
files — which is why it was decided not to port Lua at all and to start with Devil Square (its ranking
save was already implemented on the DataServer side, and it reuses already established patterns:
`MemScript`, `ViewportTicker`, Phase 4 spawn/combat).

**What was implemented:**

- **Complete state engine** (`World/DevilSquareManager.cs`): the original's 5 phases
  (`BLANK→EMPTY→STAND→START→CLEAN→EMPTY`), an independent bracket per level (0-3 with real data in this
  package — brackets 4-6 exist in the code, as in the original, but without reward/gate data, see the
  quirk documented in the file itself). The opening schedule is recomputed from scratch every time from
  the full list in `DevilSquare.dat` (section 1, cron format with `*` wildcards), like `CheckSync` in the
  original — no persistent cursor is kept. The 4 monster stages inside START are added progressively
  according to the % of time remaining (75/50/25%, integer division in the same exact order as the
  original) and are NOT cleaned between stages (they accumulate, like `CDevilSquare::StageSpawn`).
- **Data loading** (`World/DevilSquareData.cs`): `DevilSquare.dat` (schedules + experience/zen reward
  tables by bracket/rank), `EventEntryLevel.dat` (level range per bracket, generic loader by section —
  reusable for Blood/Chaos/Kalima when ported) and `EventStageSpawn.dat` (which monster classes enter at
  each stage). `MonsterSpawnTable` now also captures the `Type==4` rows (event position pool, previously
  discarded) without instantiating them at start-up — `MonsterRegistry.SpawnAll` skips them explicitly;
  only `DevilSquareManager` consumes them at runtime via the new `MonsterRegistry.SpawnOne`.
- **Entry** (`C1:90`): validates bracket, ticket (`GET_ITEM(14,19)` "Devil's Invitation" with embedded
  level = bracket+1, or `GET_ITEM(13,46)` without level), open window, the character's level range
  (`EventEntryLevelTable.GetDevilSquareLevel`, last-match-wins like the original) and capacity; if
  everything passes, it consumes 1 unit of the ticket, registers the participant and teleports
  (`C3:1C`, new `WorldPacketBuilder.TeleportSend` — the first use of this packet in the port).
- **Score**: credit to the attacker with the MOST accumulated damage on the monster (not whoever dealt
  the final blow), `score += monster.Level * (bracket+1)`, like `MonsterDieProc`. It is a system
  independent of normal experience sharing (which keeps applying the same, unchanged) — a Devil Square
  monster gives experience AND event score at once.
- **Close** (`SetState_CLEAN`): computes the final ranking (tie-break by entry order, not by array index
  as the original since a list is used here instead of a fixed 50-slot array), applies experience (reuses
  the same levelling pipeline of normal combat) and zen (with a simple cap against overflow) to the first
  10 places, sends `PMSG_DEVIL_SQUARE_SCORE_SEND` (`C1:93`, with the original's exact quirk: entry #0 is
  always the receiver itself, repeated again at its real position if it is in the top 9) to each
  participant, and saves the ranking in DataServer (head `0x3F`) for each one.
- **New console command** `ds forcestart [bracket] [seconds]`: port of `IDM_EVENT_FORCEDEVILSQUARE` (the
  original's admin menu) — forces the next opening without
  waiting for the real every-4-hours schedule. Useful both for real administration and for deterministic
  testing.

**Explicitly outside this first pass (documented technical debt, same criterion as the rest of the
project):**

- No NPC dialog (Charon) to open the selection window — that subsystem is not ported yet
  (`0x30`/`0x31` are still unhandled). You enter by sending `C1:90` directly.
- No text message catalogue (`Message.txt`) — the "opens in N minutes" notices are not sent; the
  30-second textless klaxon (`C1:92`) is.
- The ITEM reward (1st place only) is not ported — it requires the recursive "bag" system of
  `ItemBagManager`, outside this pass. Experience and zen are granted in full.
- The daily entry limit per account (`DSCount`) is not ported.

Tested end to end with a dedicated character (high Strength seeded by SQL — the real monsters of Devil
Square 1, Skeleton Archer/Cyclops with 850-1100 HP and Defense 35-45, are intractable with Phase 4's
placeholder damage formula using the low Strength of a freshly created character; it does not affect the
fidelity of the event engine itself) using `ds forcestart` through the console: entry with a real ticket,
teleport to Devil Square, appearance of the stage-0 monsters, death of one with score > 0, real closing
of the event (1-minute STAND + 1-minute START, without shortening the state engine itself — only the
minutes configured in the test `DevilSquare.dat`), reception of the score packet, and SQL confirmation
that the row was saved in `ranking_devil_square`. Script in `tests/gameserver_fase6_e2e_test.py`.

### GameServer — Phase 7: skills and mana (first pass)

Port of the "single-target attack skill cast" portion of `SkillManager.h/.cpp` (`CGSkillAttackRecv`,
`UseAttackSkill`, `BasicSkillAttack`) + the magic damage calculation of `CAttack::GetAttackDamageWizard`
(`Attack.cpp:1309-1384`) + the periodic mana/BP regeneration of `CharacterAutoRecuperation`
(`ObjectManager.cpp:1729-1758`). Prior research (direct reading of the `.cpp`, not just from a subagent —
a subagent had misquoted the file of the packet structs, corrected before implementing) confirmed that
`SkillList.txt` is a flat list (no sections, unlike `Item.txt`/`MonsterList.txt`) and that a skill's
`DamageMax` is not a column of the file but a fixed formula (`DamageMin + DamageMin/2`) applied at load
time.

**What was implemented:**

- `SkillInfoTable`/`SkillDamageTable` (`World/SkillInfo.cs`): loads `Data/Skill/SkillList.txt` (19 columns
  confirmed against the file's real header) and the optional multiplier of `Data/Skill/SkillDamage.txt`
  (the real shipped file has no data rows, so today it is a no-op — implemented anyway in case a
  different deployment brings data).
- **Cast** (`C3:19`, `ClientProtocolHandler.OnSkillAttackAsync`): validates life/map/safe-zone (the same
  check as the melee attack), enabled class (`RequireClass[class]`, which replaces the "learned skill"
  validation — see limitation below), required level, per-skill cooldown (`SkillDelay`, a timestamp per
  skill index) and range; if everything passes, it deducts mana/BP and sends `PMSG_MANA_SEND` — in THAT
  exact order, like the original: an out-of-range cast is entirely free (deducts nothing, sends
  nothing), but a valid cast that then misses the hit still cost its mana. Hit/dodge reuses the same
  calculation as the melee attack (`AttackSuccessRate`/`DefenseSuccessRate`) because the original does
  not document a separate "magic hit" formula in the reviewed parts of `Attack.cpp` — if it misses,
  `PMSG_DAMAGE_SEND` is sent with the miss bit and the method ends there, **without** sending the
  skill's visual packet, confirmed byte by byte against the original `Attack()` itself
  (`GCSkillAttackSend` is inside the same block that builds the damage, after the `MissCheck`, so a real
  miss does not send it in the original C++ either — it is not a simplification of this port).
- **Magic damage** (`PlayerObject.RecalcCombatStats`, a new step added at the end):
  `MagicDamageMin/Max = Energy/const (9,4 — identical for all 5 classes in the real .dat) +
  skill.DamageMin/Max`, with the same magic weapon bonus as the original (`+ (MagicDamageRate/2 +
  weaponLevel*2)%` if the right hand holds a sword or a staff). It is recomputed together with the rest of
  `RecalcCombatStats` (entering the world / equip-unequip), no separate step needed.
- **Mana/BP**: `PMSG_MANA_SEND` (`C1:27`) with the same big-endian layout as the original (`type +
  mana[2]BE + bp[2]BE`, NOT little-endian). Periodic regeneration every ~3s (`ViewportTicker`, new
  interval `ManaRegenTickInterval=15` over the already existing 200ms tick): `value = MaxMana *
  MPRecoveryRate[class] / 100`, confirmed against the real `.dat` that `HPRecoveryRate` is 0 for the 5
  classes (which is why life does NOT regenerate on its own through this mechanism, on purpose).
- **Visual packet** (`SkillAttackSend`, `C3:19` from server to client): unicast to the caster itself +
  viewport fan-out to whoever is watching, block-encrypted like `TeleportSend` (the first and second use
  of `SendEncryptedAsync` in the port).

**Explicitly outside this first pass (documented technical debt, same criterion as the rest of the
project):**

- No "learned skill" system (`GetSkill`/the character's skill list) — it is replaced by a direct
  class+level validation against `RequireClass`/`RequireLevel` at cast time. Since this port does not
  track `ChangeUp` (always 0), in practice only skills with `RequireClass==1` for the player's class are
  reachable.
- Only single-TARGET attack skills (`BasicSkillAttack`). No area (`MultiSkillAttack`), duration
  (sustained Teleport/Poison/Ice), combo, nor ally Teleport — all use another packet flow in the original,
  not ported yet.
- `MPConsumptionRate`/`BPConsumptionRate` (cost reduction by item/effect) assumed 100% always — there is
  no item-option system or active-effects system yet.
- `CheckSkillRequireKillPoint` (specific to Chaos Castle) is not ported.
- No `EffectList.txt` (buffs/debuffs) — the `Effect` field of `SkillList.txt` is loaded but not used yet.

Tested end to end (`tests/gameserver_skills_e2e_test.py`, a dedicated test monster "Bull Fighter" instead
of the "Spider" used by Phases 2-6, because it needs to survive 1 melee hit + 2 casts without dying before
the second): exact mana deduction, damage packet, the skill's visual packet, and periodic mana
regeneration without casting again. A cast's hit is probabilistic (the same calculation as melee, ~17%
individual miss chance against the test monster with the seeded weapon), so the harness retries each cast
until the first hit (`CastSkillUntilHitAsync` in `WorldTestClient/Program.cs`) and computes the expected
mana deduction as `cost × attempts` instead of assuming the first attempt always hits — a single attempt
without retry made the test intermittent (it failed once from a streak of bad luck during development,
confirmed by reading the original `Attack()` itself that a real miss does not send the visual packet
either, so it was not an encryption/decoding bug as was suspected at first).

### GameServer — Phase 8: first real-gameplay pass (stats, action, NPC shops)

A batch of fixes/features requested directly by testing with the real client (`main.exe`), from a
concrete list of gaps observed while playing: stats with "infinite numbers", several unhandled heads in the
console (`0xA0`, `0x31`, `0xA9`, `F3:0x06`, `0x18`, and `JoinServer 0x02`), lack of shops.

**Struct alignment bug (the real cause of the "infinite numbers"):** the real build has
`GAMESERVER_EXTRA==1` (`stdafx.h:9-11`), which adds `View*` fields (DWORD) to several outgoing packet
structs, but those structs do NOT have `#pragma pack(1)` — MSVC inserts automatic padding before any DWORD
that does not fall on a multiple of 4 relative to the start of the struct (header included). This port's
`PacketWriter` writes everything sequentially and did not replicate that padding in 5 already implemented
packets, so the real client read the final fields shifted (interpreted as garbage/"infinite"). It was
corrected byte by byte against the real lines of `Protocol.h` in `CharacterInfoSend`,
`NewCharacterInfoSend`, `LevelUpSend`, `DamageSend` and `ManaSend` (the latter was directly missing the
whole `ViewMP`/`ViewBP` fields, not just the padding).

**What else was implemented:**

- **Level-up point** (`C1:F3:06`, `CGLevelUpPointRecv`): adds 1 point to
  Strength/Dexterity/Vitality/Energy/Leadership, validated against the available `LevelUpPoint` and the
  `MaxStatPoint_AL0` cap from `GameServerInfo - Character.dat` (the 4 account tiers use the same value in
  the real `.dat`, so `AccountLevel` need not be tracked for this particular check). It triggers a full
  combat recalculation just like the original.
- **Action/pose** (`C1:18`, `CGActionRecv`): a direct port — the original does not even validate the
  optional "target" index, it forwards it as is to the echo. `PlayerObject.ActionNumber` is added for
  future use (third-party viewport of those who only now come to see someone seated, not ported in this
  pass — live forwarding to those who were already watching does work).
- **Minimal quest info / pet item info** (`C1:A0`/`C1:A9`): placeholder answers (0 quests defined, pet
  level/experience 0) just so the real client stops retrying — no real quest/pet system ported yet.
- **`JoinServer 0x02`** (`DisconnectAccountAckRecv`): investigated and confirmed a real no-op — this port
  already closes the session proactively when the socket truly disconnects (unlike the original, which
  depends on this asynchronous ack to free the account slot on the JoinServer side). The handler was added
  anyway, for completeness and to get rid of the "unhandled" log.
- **NPC shops** (`World/Shop.cs` + the "NPCs and shops" portion of `WorldPackets.cs` +
  `ClientProtocolHandler`): port of `ShopManager.h/.cpp` + `Shop.h/.cpp` + the "shop" branch of
  `NpcTalk.h/.cpp` (`CGNpcTalkRecv`/`CGNpcTalkCloseRecv`) + `CGItemBuyRecv`/`CGItemSellRecv` from
  `ItemManager.cpp`. A shop NPC is represented as one more `Monster` (same index/viewport mechanism, new
  field `Monster.ShopNumber`), spawned from `Data/ShopManager.txt` at start-up — the same approach the
  "uninstantiated" NPCs of Phase 4 already used (`MonsterInfoTable` with `Type!=0`), only that now they
  are instantiated. Each shop's items are loaded from `Data/Shop/<ShopPath>.txt` and packed in the 8×15
  grid with the same first-free-slot algorithm (top-left first-fit, by `Width`/`Height` from
  `ItemBalanceTable`) that `ShopRectCheck` uses in the original. Buy/sell price: a real port of
  `CItem::Value()` (`Item.cpp:916-975`) — confirmed by reading the source that this build is an extended
  version (with sockets, Pentagram, Muun items, custom wings/jewels via Lua) beyond vanilla 0.99B; only
  the "item without any special option" branch of the formula was ported (see gaps below), which is enough
  for a functional shop economy and gives prices identical to the original in the common case. The
  distance to the NPC is not validated (`gObjCalcDistance(lpObj,lpObj)` in the original is literally the
  distance from an object to itself — always 0 — almost certainly a bug of the original, replicated as is
  for fidelity). It was also confirmed against the real source that `GAMESERVER_SHOP==0` in this build
  (`stdafx.h:26`), so the alternative-currency ("Coin") shops and the extra price packet
  (`GCShopItemCoinPriceSend`) are real no-ops — they did not need porting.

**Explicitly outside this pass (documented technical debt, same criterion as the rest of the project):**

- Special-dialog NPCs (Trainer, Charon, GuildMaster, Warehouse) — every NPC is treated as a simple shop.
  No real quests tied to NPCs.
- Buy/sell price: the bonuses for special options (Luck/Skill/Additional/Excellent), sockets, Pentagram,
  and Muun/custom wing-jewel items were not ported (systems of this extended build, none ported in the
  project so far) — the computed price is that of a "base" item of the same level/type, not of an
  optimised piece.
- Buying: no stacking of consumables already in the inventory (`InventoryInsertItemStack`) — buying always
  looks for a new empty slot. Dark Horse/Dark Raven ("instant" item without going through the normal
  inventory) and the "Random Item" gacha items are bought like any normal item instead of their special
  logic (`GDCreateItemSend`/`CMossMerchant`, not ported).
- PKLevel/AccountLevel/GameMasterLevel are not tracked yet, so the shop restrictions by those 3 criteria
  (`m_PKLimitShop`, `CheckShopGameMasterLevel`, `CheckShopAccountLevel`) do not apply — equivalent to a
  server with no restriction configured.
- Castle Siege tax on purchases (`tax` in `CGItemBuyRecv`) always 0 — system not ported.
- Trade (player-to-player), Warehouse and Personal Shop remain unported (the same scope already
  documented in Phase 3).

Tested end to end (`tests/gameserver_shop_e2e_test.py`, a dedicated environment with a single test shop —
GET_ITEM(14,13) "Jewel of Bless", Value=150 — and its NPC): talking to the NPC, receiving the listing with
the item in the expected slot, buying it (inventory slot returned, money deducted exactly `18700`
according to the real formula), selling it back (money increased exactly `6200`), plus a light check of
the action (`0x18`, own echo) and the stat point (`F3:0x06`, the "no points available" path — the test
character did not get to level up in this script). The 5 padding fixes were verified indirectly: the full
regression of `gameserver_fase4_e2e_test.py`/`gameserver_skills_e2e_test.py` (which already exercise
`CharacterInfoSend`/`LevelUpSend`/`DamageSend`/`ManaSend`) keeps passing byte by byte after the fix, and
the layout of each field was confirmed manually against the real offsets of `Protocol.h` before touching
the code (not just "the test still passes" — the test has no way to detect a field with a value
*different* from the original's if the real client does not take part, so the primary verification was
reading the source, and the test only confirms nothing else broke).

Additionally (an explicit request from the original list: "spawns, monsters"), it was verified that the
GameServer starts cleanly pointing at the REAL `Data/` folder of `MuServer99B` (without any synthetic
test file): 15 maps, 4537 monster spawn rows in 29 files, 2879 monsters instantiated, 14 real NPC shops
with their items — zero errors. See point 1 of "Next steps" below for the detail.

### GameServer — critical correction: the wrong C++ source tree (item format, CharSet, Teleport, Party, Devil Square)

**Summary for whoever reads this later**: an earlier porting pass (documented below in its original,
struck-through version) investigated several packet formats citing `GAMESERVER_UPDATE=803` as
justification, and concluded (among other things) that this build used `MAX_ITEM_TYPE=512`/a 12-byte
`ItemInfo`, an 18-byte `CharSet`, a Teleport `gate` as WORD, etc. **All of that was research against the
WRONG C++ source tree.** This repo ships TWO copies of the emulator's source code:

- `Source/Source/Emulator/GameServer/` (no version suffix): a **much later** MU season (481 files — it has
  Kanturu, Raklion, CastleSiege, GensSystem, IllusionTemple, MasterSkillTree, sockets, JewelOfHarmony,
  Muun...) and it does define `GAMESERVER_UPDATE` (135 files use it). It has no relation to this project.
- `Source/Source/Emulator 0.99 (2.1.7)/GameServer/` (247 files): the REAL tree. The definitive proof is
  `stdafx.h:7`: `#define GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]"` — it matches EXACTLY the name
  of this project ("0.99B CHS SSeMU_2.1.7"). `grep -rl GAMESERVER_UPDATE` in this tree gives ZERO results
  — the macro does not exist here. Any comment of this port that cited "GAMESERVER_UPDATE>=NNN" was,
  without exception, researched against the wrong tree.

The bug was discovered because the user reported a real crash (`IndexOutOfRangeException` in
`ItemMoveRecv.Parse`, moving an inventory item with the real client) and the empty monster/NPC map in
their own deployment. Investigating the crash, the root cause was found and, pulling on the
thread, all the other places that cited `GAMESERVER_UPDATE` were audited — all 6 turned out wrong. Each
one was reverted to the real format (verified line by line against the correct tree, not by
assumption):

- **Item format** (`Item.cs`): `MAX_ITEM_TYPE=32` (not 512), `MAX_ITEM=512` (16 sections×32, not 8192),
  `GET_ITEM(x,y)=x*32+y`. The real wire `ItemInfo` is **5 bytes** (`MAX_ITEM_INFO=5`, an exact port of
  `CItemManager::ItemByteConvert`, ItemManager.cpp:1593-1612), not 12. The DB format
  (`ToDbBytes`/`FromDbBytes`) is a 16-byte slot with only 10 having real content (byte9 ALWAYS 0 in this
  build — the whole index fits in 9 bits, byte0 + 1 bit in byte7, no extra bits are needed); sockets/
  JewelOfHarmony/Muun/periodic items do not exist as FIELDS of `CItem` in this build (confirmed by reading
  the whole class, Item.h:36-115) — those fabricated fields were removed. It affected
  `ItemMoveRecv`/`ItemMoveSend`/`ItemChangeSend`/`ItemListSend`/`ItemGetSend`/`ItemBuySend`/
  `ShopItemListSend`/`ViewportItemAppear` and the entire DB format.
- **`CharSet`** (`PlayerObject.cs`): 13 bytes (not 18) — an exact port of
  `CObjectManager::CharacterMakePreviewCharSet` (ObjectManager.cpp:1139-1269). Without the "extension
  bits" at indices 12-17 that the previous pass had invented, and wings/pet only support the real indices
  of this build (wings 0-2/3-6/30, pet 0-4) — the branches 36-43/49/50/130-135/262-267 (wings) and
  37/64/65/67/80/106/123 (pet) were from the wrong season.
- **`PMSG_CHARACTER_LIST_SEND`** (`ClientPackets.cs`): without `ExtWarehouse` in the header nor
  `GuildStatus` per character — neither exists in this build's `Protocol.h`. The only real byte that was
  needed (and the real cause of the "I only see the first character" bug) is the 1-byte alignment padding
  the C++ compiler inserts before the `WORD Level` (the struct has no `#pragma pack(1)` in that region) —
  that DOES remain, it was the only correct part of the original fix.
- **`PMSG_TELEPORT_SEND`** (`WorldPackets.cs`): `gate` is BYTE (not WORD) — an exact port of
  `CMove::GCTeleportSend`, Move.cpp:279-292.
- **`PMSG_VIEWPORT_PLAYER`** (player appearance, `WorldPackets.cs`): `index[2]+x+y+CharSet[13]+
  count(WORD, effect list)+name[10]+tx+ty+DirAndPkLevel` — without the invented
  `attribute`/`MuunItem`/`level`/`MaxHP`/`CurHP` fields, and `count` goes BEFORE `name`, not at the end.
  ViewState uses the low 4 bits of `CharSet[0]` (not 3).
- **`PMSG_PARTY_LIST`/`PMSG_PARTY_LIFE`** (`WorldPackets.cs`): PartyList is 22 bytes/member
  (`name[10],number,map,x,y,CurLife(DWORD),MaxLife(DWORD)`, without `ServerCode` nor mana). PartyLife is
  **1 byte/member** (high nibble=slot, low nibble=life in tenths 0-9), with no mana nor name.
- **`MAX_DS_LEVEL`** (`DevilSquareData.cs`): 4 (not 7) — confirmed in `DevilSquare.h:10` and in the
  `for(n=0;n<4;n++)` loop of `CEventEntryLevel::GetDSLevel`.

**Full regression** (fase1/fase4/fase5/fase6/skills/shop/grounditem) run again after reverting everything
above — no failures, including an explicit verification that the player appearance packet now measures
exactly 37 bytes (4 header + 1 count + 32 of the real 1-player struct) and that Devil Square (which
exercises Teleport + `MAX_DS_LEVEL`) still closes the event correctly.

**Lesson for the rest of this port**: any comment that remains citing "GAMESERVER_UPDATE" in the code from
now on is either a case already verified as correct (e.g. `ItemChangeSend`, which turned out not to have
the invented `attribute` field) or a historical note explaining this very finding — one must not trust
research that cites that macro again without re-verifying it explicitly against
`Source/Source/Emulator 0.99 (2.1.7)/GameServer/`.

### GameServer — three more bugs found investigating "all characters look like Dark Wizard" and "empty map"

After the correction above, the user reported two additional symptoms that turned out to be separate bugs
(unrelated to the wrong source tree):

**1. All characters looked like Dark Wizard in the selector.** The DB stores `Class` as MU's raw
pre-shifted code (`0`=DW, `16`=DK, `32`=FE, `48`=MG, `64`=DL/SUM — see `db/postgres/002_characters.sql:21`
and the seed in `003_default_class_seed.sql`), which encodes at once `(CompactClassIndex*16 + ChangeUp)`.
But `PlayerObject.BuildCharSet` (real formula `cls*32`, port of `ObjectManager.cpp:1139-1269`) expects the
compact index 0-4, not the raw DB value — with any class other than DW (`0`), `cls*32` overflowed the byte
and was truncated to 0, rendering Dark Wizard for everyone. There was already a correct precedent for this
decomposition in `ClientProtocolHandler.cs:509-512` (character creation confirmation) but it had never
been applied to the two paths that did matter: `OnCharacterListFromDataServerAsync` (character selector)
and `OnCharacterInfoFromDataServerAsync` (world entry) — both corrected to `(Class/16, Class%16)` =
(compact class, ChangeUp).

**2. `PMSG_ITEM_GET_SEND` without its `ViewIndex` field.** An existing comment in `WorldPackets.cs` claimed
that `GAMESERVER_EXTRA` "is never 1" in this build — false: `stdafx.h:9-10` defines it unconditionally
(`#ifndef GAMESERVER_EXTRA #define GAMESERVER_EXTRA 1 #endif`). The `#if(GAMESERVER_EXTRA==1)` blocks of
`CharacterInfoSend` (13 `DWORD View*`, `Protocol.h:589-632`) were already well ported, but the one in
`ItemManager.h:109` (`PMSG_ITEM_GET_SEND.ViewIndex`, a `DWORD` at the end of the packet) had been omitted
entirely. Fixed in `MoneySend` (writes 0, port of the money branch of `CGItemGetRecv`) and `ItemGetSend`
(writes the `groundIndex`, an exact port of `pMsg.ViewIndex = index;`). Confirmed by grep that
`Viewport.h`/`Viewport.cpp`/`Move.h`/`Party.h`/`User.h` do NOT have `GAMESERVER_EXTRA` blocks — it does not
affect those packets.

**3. Empty map for the real client (no monsters/NPCs/other players) — the real root cause.**
`PlayerObject.RegenOk` (port of `char RegenOk`, `User.h:509`) started at `true` ("blocked until the client
sends `0xF3:0x12`"), and `ViewportTicker.TickAsync` skips an observer's ENTIRE sweep while `RegenOk==true`
(see `if (observer.RegenOk) continue;`) — no players, no monsters, no NPCs. But the original **starts at 0
(=false, NOT blocked)**: `gObjCharZeroSet` (`User.cpp:368`), called by `gObjAdd` on accepting the TCP
connection; neither login nor character selection nor `CharacterMakePreviewCharSet`/the full character
load (`ObjectManager.cpp:2733-2736`) touch `RegenOk` at all. The only place that sets it to 1 (blocked) is
a real teleport/gate (`gObjMoveGate`/`gObjTeleport`/`gObjSummonAlly`, `User.cpp:2114/2144/2168/2220/2259`)
— a mechanism this build does not yet have plugged into any incoming packet (the only sender of
`TeleportSend` is `DevilSquareManager`, which does not set `RegenOk=true` either).
`PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE` (`CGCharacterMoveViewportEnableRecv`, `Protocol.cpp:1300-1310`) ONLY
does `RegenOk = (RegenOk==1) ? 2 : RegenOk` — an ack of "I finished loading the map" that in the original
is a no-op if you did not come from a teleport. The regression test never detected this bug because
`WorldTestClient` sends that packet unconditionally on entering the world (masking the broken default); a
real MU client probably only sends it as an ack after a real teleport/gate, and since the initial login
never goes through that path, the player was left blocked forever. Fixed: `RegenOk` now starts at `false`,
matching the real behaviour documented above.

**Full regression run again after the three corrections** (fase1/fase4/fase5/fase6/skills/shop/grounditem)
— 0 FAILURES, with no need for retries.

### GameServer — fourth bug: `OnMoveAsync` stored the START position instead of the ARRIVAL one

Reported by the user as "when attacking, the first hit sends me back to my spawn spot". The real cause was
not the attack but the movement: `ClientProtocolHandler.OnMoveAsync` (Phase 2) did `player.X = recv.X;
player.Y = recv.Y;` — that is, it stored the movement packet's ANCHOR (the position THAT movement started
from) as if it were the current position, instead of `tx,ty` (the ARRIVAL position computed by walking
`RoadPathTable`). In the original, `CGMoveRecv` (Protocol.cpp:901-987) does NOT touch `lpObj->X` at all —
that confirmed position is updated gradually, tile by tile, by a separate periodic tick
(`CObjectManager::ObjectMoveProc`, ObjectManager.cpp:419-482) as the character visually walks. This port
resolves movement instantly (without that tick) and nowhere else in the code advances `X` towards `TX`, so
`player.X` had to end up at the arrival position at once — but since the anchor (the OLD position) was
stored, every movement left `player.X` "one movement behind" the place where the client was really
standing. The next movement (for example, approaching a monster to attack) almost always fell outside the
allowed 15-tile radius — measured against that old position — and the server answered with
`PositionSend(player.X,player.Y)`, the old position, frequently very close to where the character had
originally appeared. This also affected (to a lesser extent, unreported but real) the attack range in
`OnAttackAsync` and the view range of `ViewportTicker`, both compared against `player.X`. Fixed:
`player.X/Y` is now assigned `tx,ty` (arrival position), like `TX/TY`. Full regression
(fase1/fase4/fase5/fase6/skills/shop/grounditem) run again after the fix — 0 FAILURES.

### GameServer — packet alignment bugs (garbage damage/mana, no experience) and items that disappeared when moved

The user reported, playing with the real client after the previous corrections: the attack still sent
them back to their spawn spot, the hit numbers came out illogical (like "9998989898"), no experience was
gained on killing, and items disappeared when moved in the inventory. 4 real bugs were found and fixed,
none related to the wrong source tree this time:

**1) `PMSG_DAMAGE_SEND`/`PMSG_MANA_SEND` with 3 bytes of padding TOO MANY (the real cause of the garbage
numbers).** A comment from an earlier pass of this session assumed these two packets use a 4-byte header
(like `CharacterInfoSend`/`LevelUpSend`, which do because they are `C1:F3:xx` with a sub-code) and added 3
bytes of alignment padding before the `GAMESERVER_EXTRA` "View\*" `DWORD`s. But `PMSG_DAMAGE_SEND`
(`C1:D9`) and `PMSG_MANA_SEND` (`C1:27`) are direct opcodes WITHOUT a sub-code, so they use `PBMSG_HEAD`
(`Protocol.h:22-41`: `type+size+head`, 3 `BYTE` fields, **3 bytes**, not 4) — with that real header, the
offset after the previous fields already falls on a multiple of 4, so the correct padding is **zero
bytes**, not 3. The 3 extra bytes shifted `ViewCurHP`/`ViewDamageHP`/`ViewMP`/`ViewBP` and lengthened the
whole packet by 3 bytes, producing exactly the reported illogical numbers — and very probably part of the
cause of the "returns to my spawn spot", given that `DamageSend` is sent on every hit and a
size-shifted packet can desynchronise the rest of the encrypted stream for that client.

**2) `PMSG_REWARD_EXPERIENCE_SEND` (experience popup) without alignment padding (it was missing 1 byte).**
Real offset after `index[2]+WORD experience[2](4 bytes)+damage[2]` = 3+2+4+2 = 11, not a multiple of 4 —
1 byte of padding is needed before the 3 "View\*" `DWORD`s, which was never added. Fixed in
`MonsterDieSend`. This explains the "they don't give experience": the popup arrived with the `DWORD`s
shifted by 1 byte.

**3) `PMSG_ITEM_GET_SEND` (also used for `MoneySend`) without the padding that this same session's
`GAMESERVER_EXTRA`/`ViewIndex` fix should have added and did not.** Real offset after
`result(1)+ItemInfo[5]` = 3+1+5 = 9, not a multiple of 4 — 3 bytes are needed before `ViewIndex`. Fixed in
both (`MoneySend`/`ItemGetSend`).

**4) `PMSG_ITEM_MOVE_SEND.result` had inverted semantics, and the port did a "swap" that the real protocol
does not support (the real cause of "items disappear").** `result` is NOT a generic boolean "0=fail/1=
success" — it is the real return value of `MoveItemToInventoryFromInventory` (`ItemManager.cpp:1920-1978`):
`TargetFlag` (0 for Inventory) on success, `0xFF` on any failure. This port sent 0=fail/1=success, exactly
the opposite of what the real client expects (which only checks `!= 0xFF`, so a "fail" 0 was read as
success). Worse still: `InventoryAddItem` (`ItemManager.cpp:1052-1102`) **rejects the move with `0xFF` if
the destination slot already has an item** — there is no swap at protocol level, `PMSG_ITEM_MOVE_SEND`
only has room for ONE item in its answer. This port DID do a swap (moving the destination item back to the
origin) when the destination slot was occupied, but as the real client never finds out what happened to
the second item (the answer only describes the destination slot), it loses it from the UI even though the
server keeps tracking it correctly in the origin slot — the item "disappears" visually without being lost
on the server. Fixed: moving to an occupied slot is now rejected entirely (`result=0xFF`, touching no
slot), like the original; success sends `result=0` (`TargetFlag`).

**Full regression run again** (fase1/fase4/fase5/fase6/skills/shop/grounditem) -- 0 FAILURES. An assertion
of `WorldTestClient` that (incorrectly) expected `result==1` after equipping an item had to be updated; it
now expects `result==0`, matching the real semantics just discovered.

### GameServer — fifth attempt: `PMSG_POSITION_SEND` without the `index[2]` field (the real cause of "attacking sends me back to my spawn spot")

After the 4 fixes above the user reported that the bug persisted: attacking (which first requires walking to
approach the monster) sent them back exactly to the spot where they appear on logging in. The real bug was
found: `WorldPacketBuilder.PositionSend` (`C1:D0`, the packet the server sends to correct/force the
client's position when a movement is rejected for collision or for exceeding the 15-tile radius, see
`ClientProtocolHandler.OnMoveAsync`) sent **only `x,y`** (2 bytes of payload). The real struct
(`Protocol.h:339-345`) is:

```
struct PMSG_POSITION_SEND { PBMSG_HEAD header; BYTE index[2]; BYTE x; BYTE y; };
```

It was missing the initial `index[2]` — the real payload is 4 bytes, not 2. The client, which reads this
packet at fixed offsets expecting `index[0..1]+x+y`, ended up interpreting the 2 bytes we did send (our
`x,y`) as if they were `index[0..1]`, and the real `x,y` it used to reposition the character came from
wherever the following bytes of the stream landed (garbage, or the first bytes of the next packet) — a
corrupted position correction. Since this packet fires precisely when the client tries to move and the
server rejects that movement (the typical case when walking to approach an attack), and since the
character had never received a valid position correction since entering the world, the visible result was
"it always returns to the same place where I appeared when logging in" — which matches the report exactly.
Fixed: `PositionSend` now also takes the player's index and writes the 4 real bytes.

Full regression (fase1/fase4/fase5/fase6/skills/shop/grounditem) run again -- 0 FAILURES.

### GameServer — complete coverage of the 7 `GameServerInfo - *.dat` files

Up to this point, `ServerInfoConfig`/`CharacterBalanceConfig` only loaded the `Common.dat`/`Event.dat`/
`Character.dat` fields that already had a ported system behind them (~50 of ~845 real keys). The rest lived
unread — not in C#, nor available for a future system. A second layer of TOTAL coverage was added:

- `Config/GameServerInfoCommon.cs`, `GameServerInfoCharacter.cs`, `GameServerInfoChaosMix.cs`,
  `GameServerInfoCommand.cs`, `GameServerInfoEvent.cs`, `GameServerInfoItem.cs`, `GameServerInfoSkill.cs`,
  `GameServerInfoCustom.cs` — eight classes (7 files + `Custom.dat`, which the original reads with 3
  separate classes: `CCustomArena`/`CCustomAttack`/`CCustomPick`) which together expose the **845 real
  fields** of `CServerInfo` (`ServerInfo.h`/`.cpp` of the correct tree) as typed properties (`int`,
  `int[4]` per AccountLevel, `int[5]` per class DW/DK/FE/MG/DL, or `int[,]` for the 5 matrices
  `[class][AL]`/`[class][class]`), with the same name as the original `m_Xxx` field (without the prefix) and
  the same `.ini` key `GetPrivateProfileInt` uses in the real C++.
- Generated with a script that parsed the whole `ServerInfo.cpp` (1556 lines, 9 `Read*Info(section,path)`
  functions) plus `CustomArena.cpp`/`CustomAttack.cpp`/`CustomPick.cpp`, extracting each
  `GetPrivateProfileInt(section,"Key",0,path)` call — the default in the real C++ is **always 0**
  (confirmed by reading the whole file), so the "factory" values live entirely in the shipped `.dat`, not
  in the source code. It was verified programmatically that 844 of 845 generated keys exist as they are in
  the real `.dat` files (the only one missing, `CustomArenaDamageRate` in `Character.dat`, simply is not in
  the shipped file — it falls to the same default 0 the real `GetPrivateProfileInt` would give in that
  case, it is not a bug).
- The 7 real `GameServerInfo - *.dat` (`ChaosMix`/`Character`/`Command`/`Common`/`Custom`/`Event`/`Item`/
  `Skill`) were copied to `MuServer.GameServer/Data/` and the `.csproj` copies them automatically
  to the build (`CopyToOutputDirectory`) — unlike the rest of `Data/` (maps/monsters/items, too heavy for
  the repo), these 8 files are small plain text and now come ready out of the box, without the user having
  to copy them by hand from `GameServer/DATA/`.
- `GameServerConfig.cs` adds 5 new paths (`ServerInfoChaosMixPath`/`CommandPath`/`ItemDatPath`/
  `SkillDatPath`/`CustomPath`), all configurable through the `.ini` with the same real default
  (`Data/GameServerInfo - Xxx.dat`).
- `Program.cs` loads the 8 classes at start-up (log `[GameServerInfo] Cobertura completa cargada`, "full
  coverage loaded") and leaves them available as local variables so that the next system to be ported
  (Chaos Mix, `/reset`, Custom Arena/Attack/Pick, Mana Shield, PK, Trade, Guild, Jewel, Fruit, Quest,
  Blood/Chaos Castle...) plugs into them directly without writing the `.dat` parsing from scratch.

**Important**: a field being loaded here does NOT imply that the system that would consume it in the
original is already ported — see the catalogue of missing systems (portals/gates, monster AI,
Trade/Warehouse/PersonalShop, Guild, Blood/Chaos Castle, Quest, GM commands, Duel, Custom
Arena/Attack/Pick, DarkSpirit, reset...) reported to the user in this same session. This expansion is
exclusively the data layer: it closes the gap of "955 fields unread" so that no value stays hard-coded,
but connecting each field to a real game system remains separate work, system by system.

Full regression (fase1/fase4/fase5/fase6/skills/shop/grounditem) run again after this expansion — 0
FAILURES. A bug in the test scripts (`tests/*.py`) was also found and fixed along the way: they copied the
contents of `bin/Debug/net10.0` with a plain `shutil.copy`, which does not support subfolders — when the
`Data/` folder with the 8 new `.dat` appeared, all tests failed with `IsADirectoryError`. Fixed to
`shutil.copytree` for entries that are directories.

### GameServer — sixth bug found: progress was NEVER saved to the database (the real root cause of "all characters always return to X=182 Y=128")

The user reported, after testing with the real client, that the "when attacking I always return to the
same place" bug kept happening ALWAYS (with any character), and that new characters appeared at the same
position as an already existing one, "as if it lived in a loop". Full investigation:

1. **`X=182 Y=128` is NOT hard-coded incorrectly.** It was verified byte by byte against
   `MuServer99B/DB/MuOnline.sql` (the real `INSERT [dbo].[DefaultClassType]`, UTF-16, decoded with
   Python): the real seed row for DW/DK/MG/DL literally has `MapNumber=0, MapPosX=182, MapPosY=128`
   (FE/Elf does differ: `MapNumber=3, MapPosX=172, MapPosY=97`, Noria). Our
   `db/postgres/003_default_class_seed.sql` matches exactly those values — there is no invented literal
   there, it is the original seed of MU 0.99b.
2. **The real cause: `CObjectManager::DelCharacterInfo` (ObjectManager.cpp:625) sends
   `GDCharacterInfoSaveSend` (C2:0x30, GameServer→DataServer) right before disconnecting** — and
   `DataServerProtocolHandler.OnCharacterInfoSaveAsync` (the DataServer-side reception) was ALREADY 100%
   implemented and connected to `NpgsqlCharacterDataRepository.SaveCharacterAsync` for several sessions.
   What **never existed** was the GameServer side that builds and sends that packet —
   `DataServerCharacterPacketBuilder` only had `ConnectCharacter`(0x70)/`DisconnectCharacter`(0x71),
   neither of which carries position or any other stat. Result: the `character` row in Postgres is updated
   at CREATION (with the `DefaultClassType` seed) and never again — each login re-reads that same untouched
   row forever, no matter how much the character walked/levelled/moved items in any previous session. This
   also explains the report "a new character copies the first": there is no real copy, it is the same bug
   — any character, new or old, always reads/re-reads the same frozen snapshot of its row.
3. `DataServerCharacterPacketBuilder.CharacterInfoSaveSend` (`WorldPackets.cs`) was added, an exact mirror
   of `SDHP_CHARACTER_INFO_SAVE_SEND`/`GDCharacterInfoSaveSend` (`DSProtocol.cpp:991-1047`, ~30 fields:
   stats, inventory, skill, quest, effects, PK, and `Map/X/Y/Dir`), and
   `ClientProtocolHandler.SaveCharacterAsync` as the only producer of that packet. The fields that were
   read from DataServer on entering the world but discarded without saving (`ChatLimitTime`,
   `BCCount`/`CCCount`/`DSCount`) were added to `PlayerObject` so as not to overwrite them with 0 on every
   save. The THREE real triggers found in the C++ were replicated (each with its own throttle, just as the
   original spreads this logic across several call sites instead of centralising it):
   - **Disconnection** (`ObjectManager.cpp:623-627`): unconditional, no throttle — sent right before
     `DisconnectCharacter`, in `ClientProtocolHandler.OnDisconnectAsync`.
   - **Level-up** (`ObjectManager.cpp:1034-1038`): 60s throttle (`CharSaveTime`), fired inside
     `ApplyExperienceGainAsync` when `leveledUp` is true.
   - **Periodic autosave** (`User.cpp:2535-2541`): every 10 minutes (`AutoSaveTime`), unconditional for any
     connected player — new method `ViewportTicker.TickAutoSaveAsync`, runs on every viewport tick (200ms)
     like the rest of the periodic cycle.
4. **Verified with a new test** (a variant of `gameserver_fase5_e2e_test.py`): Hero1 moves from
   `(198,150)` to `(200,150)` via `WorldTestClient`, disconnects, and the Postgres row is read directly —
   before this fix it would have stayed at `(198,150)` (the seed), after the fix it ends at `(200,150)`
   (the real position of the session). Confirmed green.

Full regression (fase1/fase4/fase5/fase6/skills/shop/grounditem) run again — 0 FAILURES.

### GameServer — Phase 3 (continuation): floor items (pick up/drop)

A simplified port of `CMapItem` (`MapItem.h`/`.cpp`) + the part of `CMap` that manages the array of items
dropped per map (`Map.cpp:256-431`). Unlike players/monsters (GLOBAL index, see `MonsterRegistry`), floor
items are indexed PER MAP — `GroundItem`/`GroundItemRegistry` (`World/GroundItem.cs`) replicate the real
array of `MAX_MAP_ITEM=300` slots per map (`Map.h:12`) as a ring buffer that reuses the next free slot
from a cursor, like `CMap::MonsterItemDrop`/`ItemDrop`/`MoneyItemDrop`.

- **Appear/disappear** (`PMSG_VIEWPORT_ITEM`, `Viewport.h:119-125`): `C2:0x20` (appear, bit `0x80` of the
  index's high byte = "just fell", animates the fall on the client) and `C2:0x21` (disappear — a DIFFERENT
  header from the one players/monsters use, `C1:0x14`). Dropped money reuses `CItem` with
  `m_Index=GET_ITEM(14,15)` but a completely different `ItemInfo` packing from a normal item (a byte-by-byte
  port of `Viewport.cpp:1030-1046`, the amount spread over NON-contiguous bytes: `[1]=bits16-23,
  [2]=bits8-15, [4]=bits0-7, [6]=bits24-31`, with `0xFF` padding at `7-11`). The appear/disappear sweep
  reuses the same O(n) per-tick (200ms) pattern that already existed for monsters
  (`ViewportTicker.TickGroundItemsForObserverAsync`), with `PlayerObject.VisibleGroundItems` keeping the
  count per player.
- **Pick up** (`PMSG_ITEM_GET_RECV/SEND`, `C1/C3:0x22`): port of `CGItemGetRecv`
  (`ItemManager.cpp:3289-3528`) via `CMap::CheckItemGive` for the range (a 2-tile box on each axis, not
  Euclidean distance) and the loot-lock (owner/owner's party until half of the item's lifetime elapses,
  like the original's `m_LootTime`). `result`: `0xFF`=failed, `0xFE`=money (reuses the same struct as
  `MoneySend`), `0-234ish`=inventory slot (success) — the `0xFD` case ("stacked onto an existing item") is
  NOT ported because this port has no item stacking.
- **Drop** (`PMSG_ITEM_DROP_RECV/SEND`, `C1:0x23`): port of `CGItemDropRecv` (`ItemManager.cpp:3530-3718`),
  validates slot range, that the destination tile is not blocked (`CMap::IsBlocked`) and that the map has a
  free floor slot; if the dropped item was equipped, it recalculates `CharSet`/combat stats and refreshes
  the appearance for observers (the same path as `OnItemMoveAsync`).
- **Drop when a monster dies**: a VERY simplified port of `gObjMonsterDieGiveItem` (`Monster.cpp:54-284`).
  The original is a cascade of ~13 subsystems (`ItemBagManager` per monster class, boss/event drop tables,
  scheduled drop events, random set item, etc.); this pass only ports the "generic" path that covers most
  field monsters: an item roll (`Monster.ItemRate`, reusing `ItemBalanceTable.PickRandomDropItem` — the same
  `DropItem`/`Level` data of `Item.txt` that Phase 4's balance already loads) or, if no item came out, a
  money roll (`Monster.MoneyRate`, amount derived from the same level formula the experience distribution
  already uses, since the real `lpMonster->Money` is a dynamic by-product of the exp calculation, not a
  static column). Both rates are interpreted as "1 in N" (`Rng.Next(rate)==0`).
- **Outside this pass** (documented, not ported): `ItemBagManager` (special drops per class/event), boss
  drop tables, random sets, random level/excellent/socket options on the dropped item (it comes out "from
  the factory"), full party money distribution (`gServerInfo.m_PartyMoneyDistribute`, an unported server
  config — whoever picks up the money keeps it all), stacking with existing arrows/potions
  (`InventoryInsertItemStack`), high-level anti-dupe/anti-scam rules on drop (blocking +5/+6
  non-wings/excellent/set/JewelOfHarmony — not relevant yet because this port does not generate those
  items), special items with their own effect on drop (Siege Summon, Life Stone, Lost Map, etc.).

Tested end to end (`tests/gameserver_grounditem_e2e_test.py`, a dedicated environment with 2 deterministic
test monsters — one with `ItemRate=1` and another with `MoneyRate=1` — besides the real Spider shared with
Phase 4): killing the item monster → it appears on the floor with the correct index/position/`ItemInfo` and
visible to both players → a second player (not the owner) cannot pick it up while the loot-lock is in force
→ the owner can → picking up the same index again fails → dropping it back to the floor and picking it up
again works → the second player sees the `ViewportItemDestroy` on the next tick → killing the money monster
→ picking up gives `result=0xFE` and the total money goes up. Along the way, running the full regression, a
pre-existing bug (from the item-format migration of the previous section, not from this work) was found and
fixed in `tests/gameserver_fase6_e2e_test.py`: the "Devil's Invitation" ticket was seeded with the old
32-slot index (467) instead of the real 512-slot one (`GET_ITEM(14,19)=7187`), and the Python helper
`item_bytes()` did not write bits 9-12 of the index into byte9 (needed for any item of section≥8 with the new
stride) — fixed, Phase 6 passes cleanly again. Full regression (fase1/fase4/fase5/fase6/skills/shop/
grounditem) without failures.

### GameServer — Phase 3 (continuation): real `CServerInfo` (Common.dat/Event.dat, partial)

A PARTIAL port of `CServerInfo` (`ServerInfo.h`/`.cpp`, ~520 fields spread over 7 real INI files —
`GameServerInfo - {ChaosMix,Command,Common,Custom,Event,Item,Skill}.dat`, all plain text despite the `.dat`
extension, read with `GetPrivateProfileInt`/`String` just like our `IniFile`). Every `Read*Info()` of
`ServerInfo.cpp` was investigated in depth (including the finding that `Common.dat` is really parsed by
THREE different functions — `ReadStartupInfo`, `ReadCommonInfo` and `ReadHackInfo`, none of which
corresponds 1:1 with the file's name), but only what has a real consumer already ported in this project
was implemented (`Config/ServerInfoConfig.cs`):

- **Real experience formula** (`ExperienceMultiplierConstA=10`/`ConstB=1000`, an exact port of
  `gObjSetExperienceTable`, `User.cpp:276-297`): `WorldPacketBuilder.NextExperience` went from a
  `level²*1000` placeholder to `(n+9)*n*n*ConstA` (plus an extra term with `ConstB` for levels above 255) —
  it changes the real experience curve the client sees (exp bar, levelling) so that it matches the original
  pack.
- **`MaxLevel=400`**: replaces the hard-coded level cap in `ApplyExperienceGainAsync` (it was the same
  value by chance, now it is truly configurable).
- **`ItemDropTime=30`/`MoneyDropTime=30`** (`GameServerInfo - Common.dat`): the lifetime of an item dropped
  on the floor. **A real bug was fixed**: the port had 60s hard-coded (double the real value) since
  Phase 3; now `GroundItemLifetime`/`GroundItemLootLock` in `ClientProtocolHandler` read 30s/15s
  (`ItemDropTime*500`ms, an exact port of `MapItem.cpp:29-99`) from the real config.
- **`MonsterMaxLifeRate`/`DefenseRate`/`DefenseSuccessRateRate`/`PhysiDamageRate`/`AttackSuccessRateRate`**
  (all =100 in the real pack, port of `MonsterManager.cpp:173-178`): `MonsterRegistry.SpawnAll`/`SpawnOne`
  now scale `MaxLife`/`Defense`/`DefenseSuccessRate`/`DamageMin`/`DamageMax`/`AttackSuccessRate` by these
  rates when spawning. With the real values (100=no change) this is a no-op today, but the scaling
  mechanism itself was not plugged in before — an operator who raises `MonsterMaxLifeRate` to 150 in their
  `.dat` now sees it reflected.
- **`AddExperienceRate_AL0=1`** (port of `CharacterCalcExperienceAlone`, `ObjectManager.cpp:845`, a DIRECT
  non-percentage multiplier): plugged into `GrantExperienceAsync`. With the real value (1) it is a no-op.
- **`DevilSquareMaxUser=15`** (`GameServerInfo - Event.dat`): **a real bug fixed**, the port had an
  arbitrary cap of 50 participants per bracket since Phase 6; it now uses the real value.

All the `_AL0-3` (per "AccountLevel", account/VIP level 0-3) always use index 0 because this port does not
track AccountLevel per player yet (the same simplification as Phase 8's `MaxStatPoint_AL0`). **Explicitly
outside this pass** (documented in detail in the doc-comment of `ServerInfoConfig.cs`) because the system
that would consume it does not exist in this port: the ~90 fields of `ChaosMix.dat` (item/wing/Dinorant/
Fruit/pet mixing), the ~90 of `Command.dat` (`/reset` and `/masterreset`), all of `Custom.dat` (Custom
Arena/Attack/Pick — they are not even `CServerInfo`, they are read by 3 SSeMU classes of their own), all of
`Item.dat` (Transformation Ring, special item damage, potion rates by class), all of `Skill.dat` (Mana
Shield), and from `Common.dat`/`Event.dat` everything about PK/Trade/PersonalShop/Duel/Guild/Jewel/Fruit/
Quest, Blood Castle/Chaos Castle/Bonus Manager/Drop Event/Invasion Manager, and `PartyGeneralExperience`/
`PartySpecialExperience`/`PartyMaxGapLevel` (this port's party experience distribution uses a formula
structurally different from the real `CharacterCalcExperienceParty` — reconciling it is separate work,
plugging those 3 constants in is not enough).

Loaded in `Program.cs` (`ServerInfoConfig.Load(config.ServerInfoCommonPath, config.ServerInfoEventPath)`)
before spawning monsters, and exposed via `WorldPacketBuilder.ServerInfo` (a settable static property, with
a factory-defaults instance if it is never set) so that `ClientProtocolHandler`, `MonsterRegistry` and
`DevilSquareManager` read the same value without needing to inject it through each constructor. If
`GameServer.ini` does not define `ServerInfoCommonPath`/`ServerInfoEventPath`, or if the pointed-to file
does not exist, `IniFile.Load` returns an empty INI and each `GetInt` falls to its default — which are the
same real values documented above, so a deployment without copying `GameServerInfo - Common.dat`/`Event.dat`
still behaves like the original pack.

Full regression (fase1/fase4/fase5/fase6/skills/shop/grounditem) run again after this change, without
failures — including Phase 6 (Devil Square), which exercises both the new experience formula (levelling on
kill) and `DevilSquareMaxUser`.

### GameServer — the shop charged one price and the client displayed another

The pack ships `Data/Item/ItemValue.txt`: 72 explicit prices — jewels, event tickets, siege potions, arrows
by level — which in 0.99B override the result of the general formula of `CItem::Value()`. **Nobody opened
it**: the name appeared in no `.cs` nor in the `.ini`. All those objects were priced with the general
formula, which gives anything for them.

| | `ItemValue.txt` declares | the server charged |
|---|---|---|
| Jewel of Bless | 9,000,000 | 18,700 |
| Jewel of Life | 45,000,000 | 72,600 |
| Fruits | 33,000,000 | 100 |
| Jewel of Chaos | 810,000 | 40,082,300 |

All 72 rows were wrong, none matched. Selling a Bless paid 6,200 instead of 3,000,000. And it was not just an
internal discrepancy: the client **does** have the correct values (it carries them hand-written in
`ItemValue`, ZzzInfomation.cpp), so in the shop one number was seen and another was charged.

Fixed with `World/ItemValue.cs`, a new table that is loaded in `Program.cs` and that
`ComputeShopBuyPrice`/`ComputeShopSellPrice` consult **between `BuyMoney` and the general formula**. That
order matters in both directions: none of the 72 rows corresponds to an item with a `BuyMoney` other than
0, so the price of wings and orbs — which was already right — is not touched; and five rows (the two Siege
Potions, Ale, Bless and Soul) point to items that also carry the `Value` column, where it is the file that
has the right number.

Details worth knowing:

- **Level and grade.** A row can fix the `+0..+15` level, the grade, both or neither (`*` = any), and among
  several that apply the most specific one wins. Without that order, a Horn of Dinorant with no options
  would take the price of any of its seven rows depending on the file order. The grade is the special-options
  mask: today only the Dinorant uses it, whose three options add 300,000 each (960,000 / 1,260,000 /
  1,560,000), which is exactly what those seven rows declare.
- **What this port does not do.** The original scales some of these prices by the stacked quantity or by the
  remaining durability, each item in its own way. The file does not say which ones, so the value is returned
  as is: a stack is quoted as one unit. Low, but of the right order, against the factors of 300x to 6000x
  from before.
- **The test asserted the wrong price.** `gameserver_shop_e2e_test.py` took it as good that the Bless is
  bought at 18,700 — which was exactly what the implementation returned. It was written against the code and
  not against the source, the same mistake that already cost us dearly with the 17-byte serial. It now checks
  that selling pays 3,000,000, with the comment saying which row of the file it comes from.

On the client side, `tools/gamedata/audit_shop_prices.py` (in the MuMain-099B tree) remains as a guard: it
checks that the 72 rows point at real items, that none is covered by a `BuyMoney`, and how much it would cost
if the table became disconnected again.

### GameServer — asking the Chaos Box rate charged as if you had already mixed

The 0.99B wire separates asking from mixing: `0x88` (`PMSG_CHAOS_MIX_RATE_RECV`/`_SEND`) asks for the
success rate and required zen for what is in the Chaos Box, without touching anything; `0x86`
(`PMSG_CHAOS_MIX_RECV`/`_SEND`) really mixes. `OnChaosMixRateAsync` (0x88) called
`ChaosMixLogic.CalculateAndExecuteMix` — the same function `OnChaosMixRecvAsync` (0x86) uses to really mix.
Each rate query charged the zen, emptied the Chaos Box and rolled the die, exactly as if the player had
confirmed the mix.

No client in this repo triggers that path today — `MuMain` does not send `0x88` yet, its mix window is that
of another season and is not connected to any 0.99B file (see the note in MuMain-099B's
`docs/protocol-099b.md`) — but the bug is real and independent of that: any 0.99B client that does ask
before mixing, which is the normal flow, would have lost the items and the zen without having accepted
anything.

Fixed by separating computing from executing. `ChaosMixLogic.CalculateAndExecuteMix` takes an `execute`
parameter (default `true`, so as not to touch the only call site that must mutate); the six mix formulas
compute `(rate, zen)` the same in both cases, and only charge/empty the box/roll the die when
`execute: true`. `OnChaosMixRateAsync` passes `execute: false`; `OnChaosMixRecvAsync` keeps using the
default. Verified with a separate harness (`ChaosItem` with and without `execute`): the query gives the same
rate as the real execution, does not change the money, does not empty the box and does not roll the die; with
an empty box both return `(0, 0)` without touching anything; without money the query still computes the rate
instead of failing (correct: asking should not depend on being able to pay).

### GameServer — the Chaos Box formulas were invented, not a port

The doc-comment of `ChaosMixLogic.cs` said "exact port of the `CChaosBox` mix formulas (ChaosBox.cpp:1-1670)".
It was not. Compared against the real source code of the emulator — which is in the repo, at
`Source/Source/Emulator 0.99 (2.1.7)/GameServer/ChaosBox.cpp`, and had not been read for this system until
now — the success rate of the chaos weapon mix came from `10 + Σ(level×5 + Option3×2)`, a formula unrelated to
the original, which takes it from a configuration table by account level
(`gServerInfo.m_ChaosItemMixRate[AccountLevel]`, with `-1` as "use `totalValue/20000`"). The success item came
from an array of three fixed weapons instead of the real list in `Data/EventItemBag/Special/*.txt`. And
**`GameServerInfoChaosMix`, the class that already loads the 19 fields of that table, was loaded and unused**
since it was written (`Program.cs` read it into a local variable, `gsiChaosMix`, and there it stayed) —
exactly the same "full coverage loaded, zero systems connected" pattern that runs through this port.

Reading the real source also uncovered a genuine naming trap: in `ChaosBox.h`, the constant `CHAOS_MIX_WING1`
(the value `7` that travels on the wire) triggers the function **`Wing2Mix(type=0)`**, and `CHAOS_MIX_WING2`
(`11`) triggers **`Wing1Mix()`** — they are crossed. The previous code had the ingredients of those two mixes
swapped for following the constant's name instead of looking at which function it really triggers.

Rewritten against the real source for the six mixes a character of this build can reach (Chaos Item, Plus
Item +9→+10 and +10→+11, Fruit, and the two wing ones — also adding the "Cape" type, which was completely
missing): ingredients, rate formula, zen formula and which `GameServerInfoChaosMix`/`GameServerInfoCommon`
field each one comes from, verified line by line. Devil Square, Dinorant, Blood Castle and the two pet mixes
remain unported — they need live event state this server does not track, and are not reachable today anyway.

Two limits, documented in the class's doc-comment so they are not lost:

- **The success item does not use the full `ItemBagEx` engine.** It is chosen at random among the real
  candidates of each file (verified against `Data/EventItemBag/Special/*.txt`: the three chaos weapons, the
  wings of each generation, the Cape of Lord), but without the original's per-section weighting/class-filter
  system. And there is a stronger reason than "there was no time": the four files that were needed ship with
  `DropRate=0` from the factory, which in the real engine would make the bag **never** return an item even
  when winning the success roll — replicating it literally would have left the Chaos Box at "wins the roll,
  nothing happens". `ItemBagEx` is a system shared with Devil Square, Blood Castle and monster drops; it
  deserves its own port, not a rushed one as a dependency of this.
- **There is no account-level system.** The real formulas index by `AccountLevel` (0-3, premium/VIP).
  `PlayerObject.AccountLevel` was added, fixed at 0 (the base row) for all players — no account system that
  does not exist was invented.

Item delivery also changed: the original sends NEW items (Chaos Item, Fruit, the wings) through
`GDCreateItemSend` — a message to the DataServer, not the same packet as the mix — and only embeds the item in
`PMSG_CHAOS_MIX_SEND` when it is an item you ALREADY HAD that was upgraded (Plus Item Level). The port follows
that same distinction: new items are placed in an empty inventory slot and announced through `ItemMoveSend`
(the same path the `item` debug command already uses); the embedded result is only used for the in-place
upgrade.

Verified with a separate harness that loads the real `.dat`/`.txt` and exercises the six formulas: 21
assertions — the configured rate is respected exactly (60% for +9→+10, 90% for Fruit), the
`AddLuckSuccessRate2` bonus is added when applicable, +10→+11 demands 2 Bless and 2 Soul (not 1), Wing1(7)
demands a level-0 Feather and Wing2(11) demands a chaos weapon (not the other way round), Cape(24) demands
the level-1 Crest of the same item and not the level-0 Feather, a real execution raises the item's level and
charges exactly the expected zen.

### MuMain — two more dispatch bugs, and the window that opens but does not mix yet

On the client side (the MuMain repo) two bugs with the same recurring cause were found and fixed: confusing a
packet's subcode with a concept from another season.

- `0x31` subcode 3 is not "the mix finished" (what the receiver assumed, cleaning the window with
  success/breakage sounds every time it arrived) — it is the list of what is now in the Chaos Box, sent when
  the window opens. Subcode 5 is not "failed resurrection" either — it is the same list for the
  Trainer/pets window, not ported.
- The generic item-move receiver (`0x24`) did not look at the `result` field (which says which container the
  item went to) at all on the Chaos Box side, so an item moved there stayed drawn in the client's normal
  inventory even though the server had moved it.

With that fixed the window opens, syncs what was already in the box, and reflects the dragged items — but the
mix button still does not work: it must be decided which kind of mix corresponds to the box's contents (in
the real 0.99B the player chose this with a tab, information that is in no source available here) and the
rate requested through `0x88` before confirming. That piece remains pending, documented in MuMain-099B's
`docs/protocol-099b.md`, because it needs testing against the real running client.

### Area monsters all teleported to the corner of their rectangle

Testing with the real client, monsters "appeared and disappeared walking in a loop". The client log showed it
very clearly: all the spiders were announced (`0x13`) on **exactly the same tile, (183,93)**, and afterwards
walked in a small square of (180-183, 90-93).

The monster's passive wandering did this:

```csharp
int spawnX = monster.SpawnEntry.X;          // area spawn -> CORNER of the rectangle
int dist = Math.Max(1, monster.SpawnEntry.Dis);
if (dist > 4) dist = 3;                     // real radius (30) trimmed to 3
int newX = Math.Clamp(monster.X + rnd, spawnX - dist, spawnX + dist);
```

In area spawns (Type 1), `SpawnEntry.X/Y` is not that monster's position: it is the corner of the rectangle
**shared by all the monsters of that row** (in Lorencia, 45 spiders and 40 budge dragons over
(180,90)-(226,244)). `ResolvePosition` distributed them well across the whole area at start-up, but as soon
as one got its first wandering tick, `Clamp(200, 177, 183)` sent it at once to 183. The 85 monsters ended up
piled in a 7x7 square over the corner — and since the jump was instantaneous, they entered and left the
player's viewport one by one, which is the "loop" that was seen.

The original does something else (`gObjMonsterMoveCheck`, Monster.cpp:430-462): each monster stores **where it
appeared** (`StartX`/`StartY`, set at Monster.cpp:199,419) and a step is only accepted if the Euclidean
distance from the new tile to that point does not exceed its `Dis`. It is never relocated: an invalid step
simply is not taken.

Now `Monster` has `StartX`/`StartY` (set in the four spawn paths: the initial one, the respawn, the shop NPCs
and the dynamic event spawn) and the wandering is a one-tile step validated against that point with the real
`Dis`, without trimming.

### A character that died and disconnected was left in limbo: 0 life forever

Testing with the real client two symptoms appeared: the NPCs were not seen, and the monster could not be hit
nor did it attack. The second turned out to be a real bug, and a rather bad one.

The character was stored in the database with `life = 0` (and `max_life = 110`). With life at zero:

- **No monster attacks it**: the AI filters players by `Life > 0` both when building the list and when
  choosing a target, so the character was invisible to all the mobs.
- **And it cannot fight either**, because the client treats it as dead.

What was missing is the rescue the original does in `gObjSetCharacter` (ObjectManager.cpp:2739-2745): if a
character enters the world with `Life == 0`, it puts it into `OBJECT_DYING` with `DieRegen`, i.e. it sends it
into the normal revive flow. This port loaded the zero and left the character "alive but at zero", with no way
out: `RespawnDyingPlayersAsync` only looks at `IsDying`, which is runtime state and is lost on disconnecting.
So dying and leaving the game before respawning left the character permanently useless.

Now, on entering the world with life at zero it is marked as dying and the usual respawn revives it at the
respawn point.

The NPCs, on the other hand, **were not a bug**: the character was standing at (182,92), inside the
Spider/Budge Dragon spawn area, and the nearest Lorencia NPC is 28 tiles away with a view range of 12. That
is why mobs were seen and no NPC. The town is at x≈115-135, y≈110-145.

### The trade zen travelled on the wrong opcode (0x3B instead of 0x3A)

Comparing the `switch` of the emulator's `Protocol.cpp` against this port's dispatcher, 21 opcodes appeared
that the original handles and this port does not (Guild, friend mail, duel, Golden Archer, etc. — entire
unported systems). But one of those that **were** ported was wrong.

The original dispatches `CGTradeMoneyRecv` at **0x3A** (Protocol.cpp:124), and the header `protogen`
generates from those same sources confirms it (`PMSG_TRADE_MONEY_RECV::kHead == 0x3A`). But the struct in the
emulator's `Trade.h` carries a wrong comment saying `// C1:3B`, and that 3B was hand-transcribed on **both
sides**: in this server (`case 0x3B`) and in the client's builder (`Wire099B.cpp`, with the head written as a
literal instead of taken from the generated struct).

The result is that this project's client and server understood each other, so the bug was not visible, but
neither of them spoke the real protocol: a real 0.99B client sends 0x3A and here it would have been silently
discarded, leaving the exchange's zen at zero.

Now the server accepts **0x3A and 0x3B** (the latter for compatibility with clients already compiled with the
old value) and the client takes the head from the generated struct. It is exactly the case the rule "the wire
is generated, not transcribed" exists to prevent.

**Follow-up (the full `ctest` suite was run to verify without asking for a live test):** the client-side test
covering exactly this packet (`test_npc_shop_wire.cpp`, "trade and vault money travels big-endian") had been
left with the old value — it was still checking `trade.Data[2] == 0x3B` instead of `0x3A`, so it failed
against the already fixed code. The code was fine (confirmed by reading `Wire099B.cpp:474`, which does take
`PMSG_TRADE_MONEY_RECV::kHead`); what was outdated was the test's assertion, not the fix. Corrected; the full
suite gives 152/152 again.

### GameServer — the items left in the Chaos Box were lost

Moving an item from the inventory to the Chaos Box **removes** it from the inventory: the original does
`InventoryDelItem` in `MoveItemToChaosBoxFromInventory` (ItemManager.cpp:2381) and this port replicates it.
From then on the item lives only in `ChaosBoxItems`, and there were two paths that deleted it:

- **Closing the window with the generic 0x31** (`CGNpcTalkCloseRecv`) set `InChaosBox = false` but did not
  touch the box, so the item was left floating outside the inventory. The Chaos Box's specific 0x87
  (`OnChaosMixCloseAsync`) did return it, but not every path goes through there.
- **Talking to the Chaos Goblin again** called `ClearChaosBox()`, which empties the box at once: if something
  had been left from the previous step, it was permanently lost there. That is also not what the original
  does: `NpcChaosGoblin` (NpcTalk.cpp:165-187) clears nothing on opening — the items stay in the box, because
  there the box is persisted.

Now the three paths (0x31, 0x87 and opening the machine) go through the same helper, which returns whatever
is there to the inventory and only then empties the box. If the inventory is full the item stays in the box
instead of being discarded. It is a conscious deviation from the original — there the items stay inside on
closing — and the reason is that this port does not persist the Chaos Box in the DataServer yet: leaving them
there would lose them anyway as soon as the player disconnected.

Along the way the `NpcWarehouse` guard was ported (NpcTalk.cpp:189-198): the vault does not open if there are
items in the Chaos Box, which is what prevents having the same item counted in two places at once.

### GameServer — `MaxLevelUp` loaded but never applied: a single kill could raise hundreds of levels

Found while testing with experience set to 9999x to see what happened on levelling up:
`ApplyExperienceGainAsync` added all the gained experience and levelled up in an unbounded `while` as long as
it sufficed. The original (`CObjectManager::CharacterLevelUp`, ObjectManager.cpp:983-1041) has an explicit
cap, `gServerInfo.m_MaxLevelUp` (`MaxLevelUp` in `Common.dat`, 1 from the factory): a single experience event
(one kill) raises at most that many levels, and **whatever experience is left over is discarded** — it is not
saved for the next kill (`AddExperience -= (((--MaxLevelUp)==0)?AddExperience:(NextExperience-Experience))`).
The field was already being read (`GameServerInfoCommon`, for the panel) but `ServerInfoConfig` — the config
the GameServer really consumes — did not load it, so there was no cap: with a high rate, one kill took the
character from level 1 to level 400 in one blow.

`MaxLevelUp` was added to `ServerInfoConfig` and the level loop was rewritten to be an exact port of the
original, including the discarding of the excess. Along the way, another visible difference showed up by
looking at the same function: if there is a level-up, the `MonsterDieSend` packet must send experience **0**
in the popup (the real notice is given by the separate level-up packet) — the port always sent the real
experience gained, so two overlapping numbers were seen on screen.

### MuMain — levelling up did not show the golden aura

Another report from the same testing session. `ReceiveLevelUp099B` (the `PMSG_LEVEL_UP_SEND` receiver that is
plugged in for 0.99B) copied the new stats but never triggered the visual effect or the sound — the later
dialect's receiver does (`CreateJoint(BITMAP_FLARE, ...)` x15 + a `BITMAP_MAGIC`, or x20 if the class is
already second-evolution, plus `SOUND_LEVEL_UP`), but it is not connected for 0.99B. The same block was ported
to the end of `ReceiveLevelUp099B`. Full detail in
[`protocol-099b.md`](../../MuMain-099B/docs/protocol-099b.md).

### MuMain — picking up floor items locked up after the first attempt

Also from the same session: zen could be picked up only once, and afterwards no more items (neither zen nor
objects) could be picked up. `ReceiveGetItem099B` never reset the `SendGetItem` flag that blocks sending a
pick-up request while waiting for the previous one's answer — the code itself already had a comment predicting
the bug (`WSclient.cpp:428`). The reset the old receiver did have was ported, added to the four exits of the
099B version. Full detail in [`protocol-099b.md`](../../MuMain-099B/docs/protocol-099b.md).

### GameServer — `AccountLevel` (VIP) was computed correctly in JoinServer and thrown away in GameServer

Found while reviewing why the `_AL0`-`_AL3` rates (experience, zen drop, stat cap, and the four Chaos Mix
ones) always behaved as if all accounts were level 0: JoinServer already has the complete system
(`AccountRepository.GetAccountLevelAsync`, a real port of `WZ_GetAccountLevel` with expiry) and sends it to
GameServer in the login packet itself (`ConnectAccountSend`, field `AccountLevel`) — but
`OnJoinAccountResultAsync` only looked at `msg.Result` and threw the rest of the message away.
`PlayerObject.AccountLevel` was not even a field: it was a property returning a fixed `0`.

`ClientSession.AccountLevel` was added (filled in `OnJoinAccountResultAsync`, because the value arrives at
login, before the `PlayerObject` exists — a character has not been chosen yet) and `PlayerObject.AccountLevel`
now copies it on entering the world. With the field now real, the three places that indexed `[0]` by hand
instead of by the player were fixed: experience gained per kill, the rate of money dropped by monsters, and
the stat-point cap when distributing level-up points. The Chaos Mix system (`ChaosMixLogic`) already read
`player.AccountLevel` from before — it was only waiting for the value to stop being always 0, so the four mix
rates by account level start working end to end without touching that file.

What remains unported, on purpose, because it is a separate feature (not a wiring problem): the filter of
which NPCs are visible according to the player's account level (`Shop.cs`, columns AL0-AL3 of the NPC script)
and the shop access restrictions by PK/GM level (`CheckShopAccountLevel` of the original, see
`OnNpcTalkAsync`).

### GameServer — `MaxStatPoint_AL0-3` was read from the wrong file

Found while reviewing the `AccountLevel` correction above, looking for more places with the same problem.
`CharacterBalanceConfig.MaxStatPoint` (the cap of an individual stat when distributing level-up points,
indexed by `AccountLevel`) had a doc-comment that told half the truth: "it lives in Common.dat, not in
Character.dat" — but the code that read it (`Load`) used the **Character.dat** `ini` anyway
(`config.CharacterInfoPath`, the only file that method received) to look for a key that exists only in
Common.dat. The search never found anything and always fell to the hard-coded default (65000) for the 4
tiers, regardless of what the panel wrote into `GameServerInfo - Common.dat`. It was not noticeable because
the factory value shipped in Common.dat is also 65000 for all 4 — but there was no longer any way to give a
VIP account more stat headroom via config, it would always read 65000 no matter what.

The signature of `CharacterBalanceConfig.Load` was changed to also receive the path of Common.dat
(`config.ServerInfoCommonPath`, already loaded in Program.cs for `ServerInfoConfig`/`GameServerInfoCommon` at
the same start-up point) and to read `MaxStatPoint_AL{0..3}` from there. A single call site, no receivers to
migrate.

### GameServer — `0x1B` (Skill Cancel) added as a faithful no-op, not as a placeholder

From the list of unhandled opcodes: `0x1B` (`CGSkillCancelRecv`, SkillManager.cpp:2591-2601) in the original
cancels an active skill effect via `gEffectManager.DelEffect`. This port has no effect manager (the
duration-skills of `0x1E` are only the animation/projectile, with no persistent damage-over-time to track), so
there is nothing to cancel yet — the explicit `case` was added as a no-op so it does not pollute the log with
"not implemented" for a packet that, given the current scope, is already correctly handled. The day a real
effect manager is ported, this is the point where cancellation hooks in.

### What is missing and why it was not ported in this pass: Guild, Duel, Personal Shop, Golden Archer, Pet, Teleport Ally

Of the ~13 remaining unimplemented opcodes, I reserved them all for a separate pass instead of porting them
now, and not because of size alone:

- **Guild, Duel, Personal Shop and Teleport Ally require a SECOND connected account** to be really tested
  (a guild with more than one member, a 1v1 duel, buying from another player's shop, teleporting a party
  companion) — with a single test character there is no way to verify that the full flow works before
  delivering it.
- **Guild** (`Guild.cpp`+`GuildManager.cpp`, ~1600 lines) also needs a new database schema (guild table,
  members, ranks, mark) — a schema change is not something to slip in during a compatibility pass without
  planning it.
- **Golden Archer** is viable on its own (it is a vending machine against an NPC), but it also needs a new
  database column for the persistent per-account counter (`GDGoldenArcherAddCountSaveSend`) — the same
  reason as Guild, on a smaller scale.
- **Pet** (pet items with their own AI, level, hunger) is an entire game system on its own, not a function;
  not even the registry of live pets that the rest of this would assume exists.

If at some point there is a second account to test in pairs, Duel is the smallest and most self-contained of
the four multiplayer ones (a state machine over the player itself, with no arena map nor real guild
involved) and the logical candidate to go first.

### AdminPanel — web administration panel (Blazor Server + MudBlazor)

`src/MuServer.AdminPanel` is a web panel to configure the game without editing the `Data/` files by hand, in
the spirit of what OpenMU's panel offers. Central design decision: **the panel reads and writes the real
`Data/` files, not a copy nor an intermediate database.** There is no import or synchronisation that can get
out of sync — what is seen in the panel is literally what is in the file, and what is saved is what the
GameServer will read.

An important and explicit consequence in the UI: **the GameServer reads those files only once, at start-up**,
so any change only applies when it is restarted.

Each file is written atomically (temp + replace) so the server can never read a half-written file, and
**everything the panel does not edit is preserved**: comments, `;====` banners, key order and spacing stay
byte for byte the same; only the line of the value that actually changed is rewritten. Each page also leaves
a `.bak` the first time it saves during the session.

Pages:

- **Warps** (`Data/Move/Move.txt`) — name, zen, minimum/maximum level and gate of each destination. It is the
  same file client and server use, so a change here does not desynchronise them.
- **Items** (`Data/Item/Item.txt`) — the 16 sections of the file (weapons, shields, armours, wings, jewels,
  orbs...), each with its own column layout. The grid shows only the columns that apply to the chosen
  category, because the real format differs by section (wings, for example, have no `SetAttr` and order the
  requirements differently). A detail of the real file worth knowing: section 12 is not only "wings" — it
  also carries combat orbs and the Jewel of Chaos, sharing the wings' column layout, because the original
  parser (`ItemManager.cpp`) dispatches by section number and not by item type.
- **Rates** (`Data/GameServerInfo - Common.dat`) — the shortcut to what is always touched: experience
  multiplier, drop probability, zen, jewel rates, points per level, global monster multipliers and
  durability. Each field has a name and explanation in plain language instead of the raw identifier. The keys
  that are repeated per account level in the file (`_AL0`..`_AL3`) are shown as a single field and written
  into all four. NOTE: this has become outdated — the GameServer does track `AccountLevel` per player since
  JoinServer started sending it (see the section "`AccountLevel` (VIP) was computed correctly..." above), so
  today those 4 rates COULD differ by account level and the page is overwriting them all with the same
  value. Pending: split the 4 fields in the UI so the admin can give different rates per VIP.
- **Prices** (`Data/Item/ItemValue.txt`) — the 72 rows of explicit prices that override the general formula
  (jewels, event tickets, siege potions). It shows the item's real name by cross-referencing `Item.txt`.
- **Shops** (`Data/ShopManager.txt` + `Data/Shop/*.txt`) — what each NPC sells (with each item's name
  resolved from `Item.txt`, so as not to have to decipher `14,013`) and where each one stands. It allows
  adding and removing items from each shop.
- **Spawns** (`Data/Monster/Spawn/*.txt`) — where each monster appears, one file per map. The file groups the
  rows in blocks with different layouts and the page respects them: fixed position, area (which distributes a
  quantity within a rectangle, and only there the X2/Y2/quantity columns are shown), position with random
  offset, and the event positions that are not instantiated at start-up. Each row shows the monster's name
  cross-referencing `MonsterList.txt`.
- **Skills, Gates, Quests, Messages** (`Data/Skill/SkillList.txt`, `Data/Skill/SkillDamage.txt`,
  `Data/Move/Gate.txt`, `Data/Message.txt`, `Data/Quest/Quest.txt` + `QuestObjective.txt` +
  `QuestReward.txt`) — all of these share the same file shape (a comment header, one row per line, `end` at
  the end), so instead of writing seven almost identical repositories there is a generic one
  (`FlatTableRepository`) and a catalogue (`FlatTableCatalog`) where each table only declares its columns.
  Adding another table of this kind is adding an entry to the catalogue, not writing new code. The rows'
  indentation and the column widths are taken from the real file so the result is the same as the original.
- **Monsters** (`Data/Monster/MonsterList.txt`) — statistics of the 231 monster types (level, life, damage,
  defense, hit/evasion, ranges, speeds, respawn, item/zen rates). It defines *what* each monster is, not where
  it appears: the spawns are in `Data/Monster/Spawn/*.txt` and are not edited from the panel. The global
  multipliers of `Monster Settings` (in Configuration) are applied on top of these values when instantiating
  each monster.
- **Chaos Machine** (`Data/GameServerInfo - ChaosMix.dat`) — the success rates by mix type and by account
  level. Note: `-1` is not "0%", it is "no fixed rate" — those mixes (Chaos Box and wings) compute the
  percentage with a formula based on the money invested, see `ChaosMixLogic`.
- **Configuration** (the 8 `Data/GameServerInfo - *.dat`) — a generic editor for the ~845 fields. There is no
  hand-maintained field list: the file is parsed and grouped by the comment banners the file itself already
  carries, detecting by value whether the field is numeric or text. That means it works for all 8 files and
  does not go out of date if a `.dat` changes. It has a search by key name and a counter of pending changes.
- **Events** (`Data/Event/*.dat`) — schedules, durations and rewards of Devil Square, Blood Castle, Chaos
  Castle, Kalima, invasions, bonuses and event drops. These files are several numbered tables inside the same
  file, and **each section carries its own column names in a comment**, so the editor reads them from there
  instead of having a hand-written schema: it works for the 9 files without a line of code per file, and
  keeps working if one changes its columns. The page states that of these events only Devil Square and Blood
  Castle are ported on the server.
- **Connected accounts** — a live read-only list (account, character, class, level, reset, map).
- **Global message** — sends a notice to all connected players, as a golden on-screen news item or as a blue
  chat message.
- **Characters** — unlike everything above, this page does not touch `Data/`: a character does not live in a
  file, it lives in the same Postgres DataServer uses, so it talks directly to the database (it reuses
  `NpgsqlCharacterDataRepository`, the same repository the real DataServer already uses, instead of
  reimplementing the queries). It lists the characters, and on choosing one it allows editing stats (class,
  evolution, level, attributes, life/mana/BP, position, PK, fruits), the complete inventory (108 slots,
  equipment + backpack) and the vault — which belongs to the ACCOUNT, not to the character, and is shown
  separately with that clarification because it is easy to assume the opposite. Each item slot edits the same
  fields as <c>World.Item</c> (wire index, level, durability, luck, skill, option, excellent, set), and the
  name is resolved live against `Item.txt` so one can verify what each index is without guessing. The item's
  serial (the key of `pet_item_info` for pets) is preserved ONLY if the slot still has the same index it had
  when loaded — if the admin puts a different item in the slot, it does not inherit the previous one's serial
  (this avoids two items ending up pointing at the same pet row). The page explicitly warns that if the
  character is connected, the GameServer has its own in-memory copy and will overwrite this change on the next
  autosave — for a change to really stick, it must be made with the character disconnected.

  Implementation note: the items grid does **not** use `MudDataGrid` — with 108-120 editable rows,
  MudDataGrid in per-cell edit mode instantiates a full MudBlazor component (with its own JS interop) per cell,
  and several hundred of those at once made the first render so heavy that the Blazor Server circuit stopped
  answering the SignalR keepalive: the server finished computing everything (confirmed with logging) but the
  browser never got to apply the render batch, and the panel was left hanging on "loading" with no visible
  error. A native HTML table with `<input>` + `@bind` has no such cost and renders instantly. The whole page is
  also wrapped in an `<ErrorBoundary>` — if something inside the editor throws a render exception, the message
  is now seen on screen instead of silently hanging (the layout has no `blazor-error-ui` configured, so
  without this a render exception is indistinguishable from a hang).

  **The real cause of the hang was not (only) MudDataGrid.** When adding the item search (below) the same
  symptom reappeared — panel hanging on "loading", server completing everything correctly (reconfirmed with
  temporary logging) — despite already using the native table and without any extra `MudIconButton`. The
  underlying cause: the characters table's `OnRowClick` was written as
  `e => { if (e.Item is not null) _ = SelectAsync(e.Item); }` — a block lambda with no `return`, which the
  compiler infers as `Action<T>` instead of `Func<T, Task>`. MudTable invokes that delegate, sees it finish
  (synchronously, because it never returns the real `Task`), and there Blazor's handling of the event ends:
  the auto-render it performs after awaiting the `Task` of an `EventCallback` never fires, because there was
  never a `Task` to await. `SelectAsync` kept running in the background, correctly mutating
  `_row`/`_inventario`/etc., but nothing asked Blazor again to render with those new values — hence the silent
  hang. It was changed to a method with the signature `Task OnRowClickAsync(...)` (which can indeed be bound as
  `Func<T, Task>`) and an explicit `StateHasChanged()` was added in the `finally` of `SelectAsync` as a safety
  net. The rewrite to a native table above remains valid (fewer live components is better anyway), but the bug
  that really hung the panel was this one, not the grid's weight.

  **Item search**: each slot has a 🔍 button (native, not MudBlazor, for the same reason as the buttons above)
  that opens a single shared `MudDialog` (`ItemPickerDialog`, mounted once for the whole editor, never per row)
  with a search box by name or index against the complete `Item.txt` catalogue (349 items) — the equivalent,
  for choosing which item goes into a slot, of the visual search MuEditor offers for the same purpose. Choosing
  a result fills the slot's index and closes the dialog; it also has a direct "Empty slot" button.

The last two need to talk to the **running** GameServer, not to a configuration file, and were solved without
opening an HTTP port in the GameServer (which already has its own life cycle and game socket; adding a web
server to it for this is more risk than it is worth):

- **Live status**: the GameServer writes `Data/status.json` every 3s (`StatusWriter`) with name, connected
  players and the list of who they are. The panel reads it by polling. If the file is older than 15s, the
  panel treats it as "offline" instead of showing data that could already be false.
- **Global message**: the panel leaves a `Data/global-message.json` with a unique id; the GameServer picks it
  up within ≤2s (`GlobalMessagePoller`), broadcasts it to everyone with the same `NoticeSend` packet the chat's
  `/post` command already uses, and deletes the file. The id prevents a GameServer that starts later from
  re-sending an old message. It is a single-command-type channel on purpose: if more are needed at some point,
  that is when it is worth generalising it to a real queue.

A related correction that came out of this: the GameServer's `Program.cs` exited as soon as it started when
launched **without an interactive console** (as a service, or with output redirected to a file). The cause was
the command loop: `Console.ReadLine()` returns `null` immediately if there is no real console behind it, and
the code interpreted that `null` as "shut the server down". Now that case disables the commands but leaves the
server running, and orderly shutdown with Ctrl+C was added.

A fidelity detail that applies to all files: some come with mixed line endings (CRLF and LF in the same file)
and the panel normalises them to CRLF when saving. It is harmless — the server's tokenizer ignores whitespace
— but it explains why a raw `diff` may flag lines that did not really change.

**Login.** The whole panel is behind a password (session cookie, 7 days, sliding). The password comes from
`Admin:Password` in `appsettings.json` or from the environment variable `Admin__Password`; **if none is
configured a random one is generated at start-up and printed to the console**. That was chosen instead of
leaving it open or setting a default: the panel writes the server's configuration, so "no password" is not an
option, and a well-known default password is just as bad — generating it avoids both without being able to
lock anyone out, because it is in plain view at start-up. The comparison is constant-time, the login page uses
a layout of its own (so as not to leak the server name nor the connected players to whoever has not logged in
yet), and the `returnUrl` only accepts local paths, so the login cannot be used as a redirector to another
site.

An implementation detail worth knowing if this is touched: login and logout are normal `POST`s to
`/auth/login` and `/auth/logout`, not Blazor actions. The cookie is written in the HTTP response headers, and
a Blazor circuit can no longer touch them once it is running. They go under `/auth/` and not `/login` because
that route is already taken by the form page and two endpoints on the same route collide
(`AmbiguousMatchException`).

What the panel does **not** do yet: it cannot hot-reload the GameServer's configuration, it does not edit the
maps themselves (`Data/Terrain`, which are binary) nor the accounts/characters database (that lives in
Postgres, not in `Data/`).

## How to run it

Requires the .NET 10 SDK (or just the runtime for production) and an accessible PostgreSQL (JoinServer and
DataServer use it).

```bash
cd src/MuServer.ConnectServer && dotnet run
cd src/MuServer.JoinServer && dotnet run
cd src/MuServer.DataServer && dotnet run
cd src/MuServer.GameServer && dotnet run
```

The admin panel is optional and is run separately (it ends up at <http://localhost:5281>):

```bash
cd src/MuServer.AdminPanel && dotnet run
```

By default it assumes it is next to the GameServer in the repo and looks for its `Data/` in
`../MuServer.GameServer/bin/Debug/net10.0/Data`. For another deployment, point `GameServer:DataPath` in
`appsettings.json` at the GameServer's real `Data/` folder. The panel asks for a password (`Admin:Password` in
`appsettings.json`; if there is none, it generates one at start-up and prints it to the console). It still
**writes server configuration files**, so it is best kept on the internal network and not exposed to the
internet.

In each project's output directory (`bin/.../net10.0`) the original server's real configuration files must be
copied: for ConnectServer `ConnectServer.ini`, `BlackList.txt`, `ServerList.dat` (all 3 are as they are in
`MuServer99B/ConnectServer/`); for JoinServer `JoinServer.ini` and `AllowableIpList.txt`
(`MuServer99B/JoinServer/`); for DataServer `DataServer.ini`, `AllowableIpList.txt` and `BadSyntax.txt`
(`MuServer99B/DataServer/`); for GameServer the `Hack/` folder with `Enc2.dat`/`Dec1.dat` and the complete
`Data/` folder (both in the original package's `MuServer99B/Data/` — copying it whole, touching nothing, is
the only thing needed to have real spawns/items/skills/shops/events, see the re-verification note in "Next
steps" above). In each ConnectServer/JoinServer/DataServer `.ini` add the Postgres connection string, for
example:

```
JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
```

GameServer is the only case without a real text `.ini` in the original package — the original keeps that
configuration in files with a `.dat` extension that are really INI-like plain text (inside
`MuServer99B/GameServer/DATA/`: `ChaosMix.dat`/`Character.dat`/`Command.dat`/`Common.dat`/`Custom.dat`/
`Event.dat`/`Item.dat`/`Skill.dat`), the ~520 fields of `CServerInfo` (`ServerInfo.h`/`.cpp` of the original)
— global rate multipliers, admin command permissions, Chaos Mix formulas, custom event toggles, hack-detection
thresholds, log toggles, etc. Ported today: `Character.dat` in full (via `CharacterInfoPath`, used by
`CharacterCalcAttribute`) and a bounded portion of `Common.dat`/`Event.dat` (via
`ServerInfoCommonPath`/`ServerInfoEventPath` — see the section "real `CServerInfo`" above: experience formula,
`MaxLevel`, floor item lifetime, monster rates, `AddExperienceRate`, `DevilSquareMaxUser`). The rest
(`ChaosMix.dat`/`Command.dat`/`Custom.dat`/`Item.dat`/`Skill.dat` in full, and most fields of
`Common.dat`/`Event.dat`) remains unimplemented because the corresponding game system does not exist in this
port yet — it is the same "unported server config" mentioned repeatedly in this document, for example in
`gServerInfo.m_PartyMoneyDistribute` in the floor items section above. This port uses instead a plain-text
`GameServer.ini` with the same real `ServerCode`/`ServerName`/`ServerPort` values that ConnectServer's
`ServerList.dat` already carries:

```ini
[GameServerInfo]
ServerName = SSeMU GameServer_0
ServerCode = 0
ServerPort = 55900
ServerVersion = 1.02.00
ServerSerial = PoweredSetecSoft
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

To create the database: `createdb muonline` and apply in order `db/postgres/001_accounts.sql`,
`002_characters.sql`, `003_default_class_seed.sql` (and `004_friends.sql`).

To publish a standalone binary for Linux:

```bash
dotnet publish src/MuServer.ConnectServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.JoinServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.DataServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.GameServer -c Release -r linux-x64 --self-contained
```

## Next steps (in suggested order)

1. ~~Real spawns for an end-to-end playable map~~ **verified in Phase 8**: the compiled `GameServer.dll` was
   run pointing straight at the real `Data/` folder of `MuServer99B` (the 29 files of
   `Data/Monster/Spawn/*.txt`, `Data/ShopManager.txt`+`Data/Shop/`, `Data/Item/Item.txt`, etc., without any
   synthetic file) — it loaded the 15 maps, 4537 spawn rows (types 0/1/2/4 mixed, including the range+quantity
   ones of Devil Square/Blood/Chaos Castle) and 2879 instantiated monsters, plus 14 real NPC shops with their
   items, all without errors or exceptions. No code change was needed — `MonsterSpawnTable`/
   `MonsterRegistry.SpawnAll`/`ShopManagerTable` were already generic by file format since they were written.
   For a real deployment it is only needed to copy the complete `Data/` folder of the original package
   (`MuServer99B/Data`) next to the published `GameServer.dll` — no additional manual step.

   **Re-verified with the complete 4-server stack** (not just GameServer in isolation): a real deployment was
   assembled with `dotnet publish -c Release -r linux-x64 --self-contained` of the 4 projects, using the REAL
   configuration files of `MuServer99B/` (`ConnectServer.ini`+`BlackList.txt`+`ServerList.dat`,
   `JoinServer.ini`+`AllowableIpList.txt`, `DataServer.ini`+`AllowableIpList.txt`+`BadSyntax.txt`, only adding
   the Postgres connection string to them) and the complete real `Data/` folder for GameServer. Start-up log:
   15 maps, 231 monster types, 29 spawn files with 4537 rows, **2879 monsters instantiated**
   (`MonsterSetBase`/`MonsterSpawnTable` against the real data), 349 balance items, 58 skills, Devil Square (6
   schedules/4 brackets), 14 NPC shops with 14 NPCs spawned — the 4 processes connected to each other
   correctly (ConnectServer↔JoinServer↔GameServer↔DataServer). Note: `GameServer.ini` has no equivalent real
   text file in the original package — the original keeps that configuration in the binary
   `GameServerInfo - Common.dat` (not ported, see below), so this port uses a plain-text `.ini` with the same
   real `ServerCode`/`ServerPort` values that `ServerList.dat` already carries (see "How to run it" above for
   the exact format).
2. **Complete GameServer Phase 3**: Trade, Warehouse and Personal Shop (the NPC shops are already there, see
   Phase 8), item-type-per-slot validation and level/stat requirements when equipping (`Item.txt` is already
   loaded for damage/defense balance since Phase 4's second pass — it remains to also use it to validate the
   equipment itself). ~~Picking up/dropping floor items~~ **done** — see the section "GameServer — Phase 3
   (continuation): floor items" above.
3. **Complete GameServer Phase 4**: monster AI (patrol/chase/counter-attack), critical/excellent/set-item in
   damage (they depend on `ItemOption.txt`/`SetItemOption.txt`), PvP.
4. **Complete GameServer Phase 5**: Guild, PartyMatching, friend mail.
5. **Complete GameServer Phase 6 — more special events**: Blood Castle and Chaos Castle first (the same state
   engine as Devil Square, the same `EventStageSpawnTable`/`EventEntryLevelTable` already generic by section,
   only the data set and the specific entry/combat rules change — Blood Castle uses an NPC + a "kill the boss"
   objective, Chaos Castle is a battle royale with a shrinking map), then Kalima (a dungeon by levels with
   portals). NPC dialog (Charon and the rest) so that real entry does not depend on sending `C1:90` by hand.
   Illusion Temple/Golden Archer confirmed dead code in this build — not worth porting. Lua confirmed without
   real scripts in the package — it will not be ported.
6. **Complete GameServer Phase 7**: skill-learning system (`GetSkill`/the character's skill list, today
   replaced by a direct class+level validation), area/duration/combo/ally Teleport skills (`MultiSkillAttack`
   and the other unported branches of `RunningSkill`), `EffectList.txt` (skill buffs/debuffs).
7. ~~Real item index encoding (512 slots) + 12-byte `ItemInfo` wire format~~ **done**: see "Phase 8
   (continuation)" above. Sockets/pentagram/Muun/JewelOfHarmony/periodic items as GAME SYSTEMS remain
   unported (the bytes they occupy in the protocol are already there, but there is no gameplay logic behind
   them).

## Code structure

```
SharpSSeMU/
  db/postgres/
    001_accounts.sql           # memb_info/memb_stat (accounts) + test/admin seed
    002_characters.sql         # 13 character/inventory/ranking tables
    003_default_class_seed.sql # base values per class for creating a character
  src/
    MuServer.Shared/           # utilities shared between all modules
      Protocol/PacketHeader.cs   # builders/parsers of C1/C2/C3/C4 packets
      Protocol/PacketFramer.cs   # flat-stream framing (ConnectServer/JoinServer/DataServer)
      Protocol/PacketCursor.cs   # PacketReader/PacketWriter (avoids manual offset arithmetic)
      Crypto/GameStreamCipher.cs # stream cipher of the real client socket (GameServer)
      Crypto/PacketCipher.cs     # C3/C4 block cipher + XorData (GameServer)
      Crypto/GameClientFramer.cs # complete framer of the real client socket (GameServer)
      Scripting/MemScript.cs     # tokenizer for the Data/ .txt/.dat files
      Config/IniFile.cs          # .ini reader compatible with GetPrivateProfileInt/String
      Logging/Log.cs              # console + file logger
    MuServer.ConnectServer/    # see status table above
    MuServer.JoinServer/       # see status table above
      Db/                        # Npgsql layer (replaces QueryManager/ODBC + the WZ_* procs)
    MuServer.DataServer/       # see status table above
      Db/                        # Npgsql layer for characters/inventory/rankings
    MuServer.GameServer/       # see status table above — Phases 1-6 (login + world + items + combat + social + Devil Square) implemented
      Config/                    # subset of ServerInfo.h needed so far
      Net/                       # real client socket, outgoing connections to Join/DataServer
      Protocol/                  # packets and dispatcher of Phases 1-6
      World/                     # maps, player/monster registry, viewport, items, special events (Phases 2-4/6)
      StatusWriter.cs            # publishes Data/status.json every 3s (state + who is connected)
      GlobalMessagePoller.cs     # picks up Data/global-message.json left by the panel and broadcasts it to everyone
    MuServer.AdminPanel/       # web configuration panel (Blazor Server + MudBlazor) — see section above
      Components/Pages/          # one page per Data/ file (warps, items, chaos mix, config, etc.)
      Auth/AdminPassword.cs      # the panel's password (from config, or generated at start-up)
      Repositories/              # reading/writing the real files, preserving comments and format
        FlatTableRepository.cs     # generic reader/writer of Data/'s flat tables
        FlatTableCatalog.cs        # column schema of each flat table (skills, gates, quests, messages)
        SectionedTableRepository.cs # numbered tables of Data/Event (schema deduced from the file)
        IniDocument.cs             # line-by-line editable .ini (the 8 GameServerInfo - *.dat)
        ItemFileRepository.cs      # Item.txt and its 16 sections with different columns
        ItemValueFileRepository.cs # ItemValue.txt (explicit prices)
        MonsterFileRepository.cs   # MonsterList.txt
        MoveFileRepository.cs      # Move.txt
        RateSettings.cs            # catalogue of the settings "that are always touched", with their explanation
        ShopFileRepository.cs      # ShopManager.txt + Shop/*.txt
        SpawnFileRepository.cs     # Monster/Spawn/*.txt (blocks by spawn type)
    TestClient/                 # Phase 1 test harness: a C# client that builds an authentic encrypted login
                                 # (same real keys) against GameServer
    WorldTestClient/            # Phase 2-3 test harness: two complete simulated clients
                                 # (login + character selection + viewport + movement + items)
  tests/
    gameserver_fase1_e2e_test.py # orchestrates Postgres+JoinServer+DataServer+GameServer+TestClient
    gameserver_fase2_e2e_test.py # idem, with WorldTestClient (2 clients, viewport, movement)
    gameserver_fase3_e2e_test.py # idem, + seeding of a real item and equip/CharSet test
    full_chain_e2e_test.py       # complete chain ConnectServer→JoinServer→DataServer→GameServer
```

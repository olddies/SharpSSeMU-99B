# Project status

🌐 **English** · [Español](es/STATUS.md)

Legend: ✅ working · 🔶 partial / simplified · ❌ not implemented · ⛔ does not exist in 0.99B (out of scope)

The project is **playable but incomplete**. It has been exercised end-to-end by automated tests
and by hands-on sessions with a real client; expect rough edges outside the paths listed as
working. Fixes found by real play-testing are recorded in the
[server development log](../SharpSSeMU/docs/development-log.es.md) and the
[client protocol document](../MuMain-099B/docs/protocolo-099b.md) (both Spanish).

- [Server](#server) · [Client](#client) · [Known limitations](#known-limitations) · [Roadmap](#roadmap) · [Recently fixed](#recently-fixed)

## Server

### Infrastructure

| Module | State | Notes |
|---|---|---|
| ConnectServer | ✅ | server list, UDP load reports, IP limits, blacklist |
| JoinServer | ✅ | account login against PostgreSQL, account (VIP) level |
| DataServer | ✅ | characters, inventories, warehouses, rankings, friends |
| PostgreSQL schema | ✅ | `db/postgres/001`–`004` (accounts, characters, class seed, friends) |
| AdminPanel | 🔶 | edits all data files and characters, live status, global messages. No hot-reload of config; single shared password |
| End-to-end tests | 🔶 | `full_chain` + `fase1` fully green; other phases stop at one known fixture-balance point |

### GameServer

| Area | State | Notes |
|---|---|---|
| Login, character list / create / select | ✅ | which classes can be created is decided by the DataServer's `default_class_type` table |
| World entry, movement, viewport, warps/gates, 15 maps | ✅ | ~2 900 monsters from the real spawn files |
| Character stats & derived attributes | ✅ | real `CharacterCalcAttribute` per class; all 8 `GameServerInfo - *.dat` loaded |
| Items & inventory (equip, move, requirements, repair) | ✅ | 5-byte 0.99B item encoding, real `Item.txt` balance |
| Ground items (drop / pick up / expiry) | ✅ | |
| Item use: potions, town portal, **jewels** (Bless / Soul / Life), skill orbs & scrolls | ✅ | |
| NPC shops (buy / sell) | ✅ | 14 real shops; price shown = price charged |
| Warehouse, Trade | ✅ | |
| Chaos Machine | ✅ | real mix formulas and success rates |
| Combat: melee | ✅ | hit/dodge roll, defense (halved vs. players), min-damage floor — ported from `Attack.cpp` |
| Combat: monsters attack players | ✅ | same pipeline as players; spells use the skill's own damage range |
| Monster AI | 🔶 | idle/patrol/chase/attack; spell selection is heuristic (`GetMonsterAttackSkill`) |
| Skills & mana | 🔶 | single-target, duration and multi-target attacks, learning via orbs, mana/BP regen. No `EffectList.txt` buffs/debuffs, no combo/ally-teleport |
| Death, experience, level-up, respawn | ✅ | corpse stays visible until respawn (as in the original) |
| Loot | 🔶 | generic item/zen roll from `ItemRate`/`MoneyRate` and quest drops. No `ItemBag`, boss/event drop tables, random excellent/set options |
| Chat, whisper (cross-GameServer), party (+XP sharing), friends | ✅ | friend mail ❌ |
| Quests | 🔶 | quest info/state opcodes wired to the real quest tables |
| Guild | 🔶 | list / master window / create; needs guild schema work to finish |
| Devil Square | ✅ | full state machine, tickets, staged spawns, ranking |
| Blood Castle, Chaos Castle, Kalima | ❌ | same event engine as Devil Square, different data/rules |
| Duel, Personal Shop, Golden Archer, Pets (Dark Spirit/Raven), Teleport Ally, PartyMatching | ❌ | most need a second account to test properly |
| Excellent / set item options in damage & defense | ❌ | `ItemOption.txt` / `SetItemOption.txt` not loaded yet |
| Global damage multipliers (`GeneralDamageRate*`, per-map tables) | ❌ | treated as 100 % |
| Socket / pentagram / Muun / Harmony systems | ⛔ | later seasons |
| Illusion Temple, Castle Siege, Crywolf | ⛔ | not part of 0.99B (dead code in the original build) |
| Lua scripting | ⛔ | no real scripts ship with the package |

## Client

The client is a **fork of MuMain (Season 5.2 → 6)** whose network layer has been ported to the
0.99B protocol.

| Area | State | Notes |
|---|---|---|
| Native game socket, 3 encryption layers, framing | ✅ | verified against a live server |
| Server → client packets | ✅ | all handled except `0x88` (which has no receiver by design) |
| Client → server packets | ✅ | 57 of 59 opcodes; the 2 missing are DataServer-internal, not client packets |
| Generated protocol library + `static_assert` layout checks | ✅ | 259 structs; regenerate with `tools/protogen` |
| Unit tests | ✅ | 152 tests |
| Gameplay against SharpSSeMU | 🔶 | login → character → world → combat → items → shops → skills verified by play-testing; long tail still being audited |
| **User interface** | ❌ | still the Season 6 look; converting to the 0.99B look needs visual judgement — see the client's `docs/ui-099b.md`. **Biggest open help-wanted item** |
| Season-6-only features left in the code | 🔶 | Rage Fighter, Master Skill Tree, Illusion Temple, chat rooms, Castle Siege… are still compiled in; some paths still call the legacy (OpenMU-style) network layer instead of the native 0.99B one — confirm a feature exists in 0.99B (`MuClient/Data/Interface`) before "fixing" it |
| Linux build | ❌ | preset not working yet (Windows x86 is the supported target) |

## Known limitations

- **Single-tester coverage.** Automated tests cover protocol and server logic; real-client play has
  been done by one person. Bug reports with reproduction steps are very valuable.
- Character/spawn/monster data is loaded **once at start-up**; changing it requires a GameServer restart.
- The viewport sweep is O(n²) in online players — fine for tens of players, not for hundreds.
- Passwords are stored per JoinServer's `MD5Encryption` setting (plain text by default for local development); the AdminPanel has one shared password and is not hardened for the public internet.
- The server cannot start without the original `Data/` tables, which are not distributed here.
- The AdminPanel has no automated tests.

## Roadmap

Suggested order, easiest wins first:

1. **Client UI → 0.99B look** (see `docs/ui-099b.md`).
2. **Load `ItemOption.txt` / `SetItemOption.txt`** → excellent and set options in stats and damage.
3. **Blood Castle → Chaos Castle → Kalima** on the existing Devil Square engine.
4. **Duel**, then Guild completion (schema + ranks + marks), Personal Shop, Golden Archer.
5. Monster loot: `ItemBag`, boss and event drop tables.
6. Skill effects (`EffectList.txt` buffs/debuffs), combo and ally-teleport.
7. Config hot-reload + AdminPanel hardening (per-user auth).
8. Continue the client audit: remaining calls into the legacy network layer, removal of Season-6-only code.
9. Linux client build.

## Recently fixed

Bugs found by real play-testing in the latest session (all verified by rebuild + tests):

- **Shop:** only the first purchase worked — the 0.99B receive handler never cleared its in-flight flag.
- **Combat:** monsters ignored player defense and never missed; spell attacks used the physical formula. Now they use the original pipeline.
- **Monster death:** monsters vanished instantly; now the corpse stays until the respawn timer, as in the original.
- **Skills:** learned skills (e.g. Twisting Slash from an orb) never appeared in the selector — the 0.99B skill-list handler did not refresh the client's skill count.
- **Jewels:** upgrading an item with Bless/Soul/Life made the item disappear — the modify-item packet was parsed with the later-season item format.
- **Party / quests / guild create:** client sent requests through the legacy layer, so they never reached the server.

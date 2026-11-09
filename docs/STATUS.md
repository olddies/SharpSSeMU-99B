# Project status

🌐 **English** · [Español](es/STATUS.md)

Legend: ✅ working · 🔶 partial / simplified · ❌ not implemented · ⛔ does not exist in 0.99B (out of scope)

The project is **playable but incomplete**. It has been exercised end-to-end by automated tests
and by hands-on sessions with a real client; expect rough edges outside the paths listed as
working. Fixes found by real play-testing are recorded in the
[server development log](../SharpSSeMU/docs/development-log.md) and the
[client protocol document](../MuMain-099B/docs/protocol-099b.md).

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
| End-to-end tests | ✅ | all 10 green (`full_chain`, `fase1`-`fase6`, `grounditem`, `shop`, `skills`) |
| Unit tests | ✅ | NUnit, `SharpSSeMU/tests/MuServer.GameServer.Tests`: item options, damage formula, quest drop requisites (`dotnet test`) |

### GameServer

| Area | State | Notes |
|---|---|---|
| Login, character list / create / select / delete | ✅ | which classes can be created is decided by the DataServer's `default_class_type` table; delete follows `CharacterDeleteSwitch` / `PersonalCodeCheck` |
| Skill hot-keys and client options (`F3:30`) | ✅ | saved per character and sent back on entering the world |
| World entry, movement, viewport, warps/gates, 15 maps | ✅ | ~2 900 monsters from the real spawn files |
| Character stats & derived attributes | ✅ | real `CharacterCalcAttribute` per class; all 8 `GameServerInfo - *.dat` loaded |
| Items & inventory (equip, move, requirements, repair) | ✅ | 5-byte 0.99B item encoding, real `Item.txt` balance |
| Ground items (drop / pick up / expiry) | ✅ | |
| Item use: potions, town portal, **jewels** (Bless / Soul / Life), skill orbs & scrolls | ✅ | |
| NPC shops (buy / sell) | ✅ | 14 real shops; price shown = price charged |
| Warehouse, Trade | ✅ | |
| Chaos Machine | ✅ | real mix formulas and success rates; the item a mix gives comes from its `EventItemBag` bag; the Devil's Invitation, Invisibility Cloak and Horn of Dinorant mixes. Not the pet mixes: the pets themselves (Dark Horse, Dark Raven, their level and commands, the Trainer window) are not ported. The Lost Map has no mix in the original either: it comes from drops |
| Combat: melee and skills vs. monsters | ✅ | hit/dodge roll, defense, critical / excellent / ignore-defense hits (coloured by the client), wings and pets, DK/DL skill multipliers, min-damage floor, PvM damage rates — ported from `Attack.cpp` |
| Combat: monsters attack players | ✅ | same pipeline as players; spells use the skill's own damage range |
| Combat: player vs. player | 🔶 | the original's PvP pipeline (hit roll from the PvP success rates, halved defense, what the target wears, PvP and Chaos Castle damage rates), but **only inside a running Chaos Castle**: elsewhere players cannot hurt each other, because the PK system (PK levels, outlaws, self-defense, the items a murdered player drops) is not ported. Single-target attacks and skills; no area skills or reflect against players |
| Monster AI | 🔶 | idle/patrol/chase/attack; spell selection is heuristic (`GetMonsterAttackSkill`) |
| Skills & mana | 🔶 | single-target, duration and multi-target attacks, learning via orbs, mana/BP regen. No `EffectList.txt` buffs/debuffs, no combo/ally-teleport |
| Death, experience, level-up, respawn | ✅ | corpse stays visible until respawn (as in the original) |
| Loot | 🔶 | as in the original: quest items, `ItemDrop.txt` / `ItemMoneyDrop.txt` rules, then the generic roll (monster `ItemRate` vs `ItemDropRate`, per-map rates from `MapManager.txt`, excellent drops from level 25), with level/skill/luck/additional/excellent options rolled from `ItemOptionRate.txt` / `ExcellentOptionRate.txt`; zen = the experience the monster gave. Boss bags (Red Dragon, golden monsters, Skeleton King, White Wizard, Kundun) and the boxes, medals and Starter Rings that open when thrown on the ground come from `EventItemBag`. The drop events of `DropEvent.dat` run on their schedule (the package ships it empty) |
| Chat, whisper (cross-GameServer), party (+XP sharing), friends | ✅ | friend mail ❌ |
| Quests | 🔶 | quest info/state opcodes wired to the real quest tables |
| Guild | 🔶 | list / master window / create; needs guild schema work to finish |
| Devil Square | ✅ | full state machine, tickets, staged spawns, ranking, first-place item from its bag, daily entry cap and PK limit |
| Blood Castle | 🔶 | the whole event: entrance, stages (monsters, bridge, gate, statue), the Archangel's weapon and the Archangel, rewards and bag prize. The package only has the map of castle 1. Daily entry cap and PK limit as in the original |
| Kalima | 🔶 | the Lost Map gate (opens where it is thrown, for its owner and party, five entries or three minutes) and the gate to the next Kalima when a boss dies. The package only has the map of Kalima 1 |
| Chaos Castle | 🔶 | the whole event: entrance (Armor of Guardsman + fee), the wait in the safe zone, the fight with monsters and the other players while the outer rings of the arena fall away, the blow that throws players back, scores, experience, the winner's bag and ranking, the random winner of the entry fees. The package only has the map of castle 1. Tested end to end with two simulated clients, not yet in the real client |
| Duel, Personal Shop, Golden Archer, Pets (Dark Spirit/Raven), Teleport Ally, PartyMatching | ❌ | most need a second account to test properly |
| Item options: skill, luck, additional, excellent | ✅ | `ItemOption.txt`; excellent base bonuses, durability and requirements; damage reflect/reduction, life & mana after hunting, zen, HP recovery, full-armour set bonus |
| Set (ancient) items | ✅ | `SetItemType.txt` / `SetItemOption.txt`: the stats of a set piece, the bonuses of two or more pieces and of the full set, the aura, random set drops (per-map rate, `ItemDrop.txt` rows, bags such as Kundun's). Set pieces cannot be thrown on the ground |
| Per-map damage tables (`DamageTable`) | ❌ | treated as 100 % (`GeneralDamageRatePvM` / per-class PvM rates are applied) |
| Socket / pentagram / Muun / Harmony systems | ⛔ | later seasons |
| Illusion Temple, Castle Siege, Crywolf | ⛔ | not part of 0.99B (dead code in the original build) |
| Lua scripting | ⛔ | no real scripts ship with the package |

## Client

**Experimental.** The recommended client is the original 0.99B one, which the server is verified
against. The open-source client in this repository is a **fork of MuMain (Season 5.2 → 6)** whose network layer has been ported to the
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
2. **PK system** (PvP outside events: PK levels, outlaws, self-defense, item drops) and the **pets** (Dark Horse, Dark Raven, the Trainer and the pet mixes).
3. **Duel**, then Guild completion (schema + ranks + marks), Personal Shop, Golden Archer.
4. Skill effects (`EffectList.txt` buffs/debuffs), combo and ally-teleport.
5. Config hot-reload + AdminPanel hardening (per-user auth).
6. Continue the client audit: remaining calls into the legacy network layer, removal of Season-6-only code.
7. Linux client build.

## Recently fixed

Bugs found by real play-testing in the latest session (all verified by rebuild + tests):

- **Kill damage number:** the killing blow showed two numbers. Like the original server, it now sends no damage packet; its number travels in the kill reward packet, only to whoever landed it (or the party).
- **Original client:** it could not connect with `127.0.0.1` — Webzen's `main.exe` refuses that exact address. See [Getting started](GETTING_STARTED.md#6-connect-a-client).
- **Character delete** and **saved skill hot-keys** were not handled by the GameServer.
- **Chaos Castle and PvP:** the event did not exist and players could not attack each other. Both are in now (see the table). Along the way: the Cape of Lord was ignored when working out damage, a castle whose schedule had run out kept the scores still waiting, and the answer to an unknown chat command was just the command's own name (a `Loc.F` overload took the format for a language).
- **Entry limits:** Blood Castle, Chaos Castle and Devil Square now count each entry for the day (`BCCount` / `CCCount` / `DSCount`, reset daily by the DataServer) and honour `*MaxEntryCount_ALx` and the PK limit.
- **Chaos Machine:** the Horn of Dinorant mix is in, and the mixes never reached the item bags on a running server (the handler did not pass them), so the wing, fruit and chaos-weapon results always came from the built-in fallback list.
- **Developer commands:** `/item` and `/zen` gave any player any item and ten million zen. They now need `CommandDeveloperSwitch = 1` in `GameServerInfo - Command.dat` (off in the shipped file).
- **Kalima and drop events:** the Lost Map did nothing and `DropEvent.dat` was not read. Now a Lost Map thrown on the ground opens its gate, and the drop events run.
- **Blood Castle:** it was only a timer. It is now the whole event, and the Messenger of the Archangel in Devias (class 233) was mistaken for the Archangel (232), so nobody could open its entry window. Devil Square's remaining-time reply also carried the level instead of the event type.
- **Set items:** they did not exist (the files were never read). Set pieces now have their own stats, bonuses and aura, drop like in the original and Kundun's bag really gives its five. Like the original, items above +4, excellent items and set items can no longer be thrown on the ground.
- **Item bags:** bosses, Box of Luck, the Kundun boxes, the medals, the Starter Rings, the Chaos Machine results and the Devil Square prize were missing or hard-coded. They now come from `EventItemBagManager.txt` and `EventItemBag/` like the original.
- **Drops:** the *Item drop probability* setting (`ItemDropRate`) was ignored, the options of dropped items were a rough guess and the zen did not follow the original. Drops now come from the original files (`MapManager.txt`, `ItemDrop.txt`, `ItemOptionRate.txt`, `ExcellentOptionRate.txt`...) and a kill's zen is the experience it gave; the shipped default is now the original package's (`ItemDropRate` 100, about half the kills of a level-47 monster drop an item).
- **Item options:** skill and luck were swapped in drops, NPC shops and the AdminPanel; shops sold items at durability 0 (broken); weapons listed their skill without the skill option.
- **End-to-end tests** hung halfway: the harness never read the servers' console pipe, so a server blocked writing its log.

- **Shop:** only the first purchase worked — the 0.99B receive handler never cleared its in-flight flag.
- **Combat:** monsters ignored player defense and never missed; spell attacks used the physical formula. Now they use the original pipeline.
- **Monster death:** monsters vanished instantly; now the corpse stays until the respawn timer, as in the original.
- **Skills:** learned skills (e.g. Twisting Slash from an orb) never appeared in the selector — the 0.99B skill-list handler did not refresh the client's skill count.
- **Jewels:** upgrading an item with Bless/Soul/Life made the item disappear — the modify-item packet was parsed with the later-season item format.
- **Party / quests / guild create:** client sent requests through the legacy layer, so they never reached the server.

🌐 **English** · [Español](README.es.md)

# End-to-end tests

Each test brings up the real stack (PostgreSQL + JoinServer + DataServer + GameServer, plus
ConnectServer in `full_chain`) and exercises it with a client that speaks the authentic binary
protocol, encryption included.

## Requirements

* **PostgreSQL** installed (any recent version; tested with 16.14).
  On Windows: `winget install PostgreSQL.PostgreSQL.16`.
* **.NET 10 SDK**.
* The complete original package next to the repo: `MuClient/Data/` (encryption keys) and
  `MuServer99B/Data/` (maps, monsters, items, events).

Nothing else needs configuring. `_env.py` discovers PostgreSQL and `dotnet` on its own, resolves the
repo paths from its own location, and builds the projects the test needs before using them.

## Running them

```bash
python tests/gameserver_fase4_e2e_test.py
```

They exit 0 if they pass and 1 if any assertion fails. The client's `[OK]`/`[FALLO]` (failure)
lines go to stdout, and the log of each server is dumped at the end.

## Isolation

The tests **do not touch** an existing PostgreSQL installation nor need its credentials: each run
does an `initdb` of a disposable cluster in the temp directory, starts it on a free port with
`trust` authentication on loopback, and shuts it down at the end. The ports of the four servers are
also reserved dynamically, so two consecutive runs do not step on each other even if one left a
process hanging.

## Known state

`full_chain` and `fase1` pass completely. The rest get far (16–36 assertions green, including all of
the protocol part) and fail at **one** point, always the same one: the `WorldTestClient` combat
sequence.

The cause is diagnosed and **is not a protocol issue**: the test monster counter-attacks and Hero1,
with the 60 HP of a freshly created character, dies at some point along the run; from then on it can
no longer attack and the test waits for a packet that will never arrive. Raising its life through the
fixture is not enough (`RecalcCombatStats` recomputes `MaxLife` from `Vitality` on entering the
world) and raising `Vitality` unbalances the rest (with 100 it jumps to level 26 from a single kill
and breaks other steps). It is server balance, not a test problem.

Fixing the death detection uncovered this: previously the loop considered the monster dead just by
seeing a `0x17`, without looking at **which index** died. Since the server uses the same head to
announce that a player died, the test reported `[OK] Killed the monster` when in reality the
character had died and the monster was untouched. Now it compares the index and genuinely fails when
the combat does not work out.

## Fixture notes that matter

* **The test monster has to be the only one.** The `WorldTestClient` attack block is shared, not
  gated by arguments, and attacks index 0 assuming it is the test monster. The GameServer publishes
  its 29 real spawn files in its own `Data/`, and among them is `000 - Lorencia.txt`, which sorts
  before any `000 - Test.txt`. That is why `seed_test_monster()` empties the spawns directory before
  writing its own.
* **The build output is deployed first and the fixture on top.** The other way round, the
  GameServer's `Data/` silently overwrites the trimmed files the test has just written.
* **The servers need an open stdin.** All three run a `Console.ReadLine()` loop for their console
  commands and terminate when it returns null. Inheriting an already-closed stdin kills them right
  after they log that they are ready: it looks like a network problem and is not.
* **`MaxConnectionPerIP` is mandatory in `ConnectServer.ini`.**
  `IpConnectionTracker.CheckIpAddress` rejects the first connection from an IP when the limit is 0
  (compatibility with the original), so without that key the ConnectServer accepts the socket and
  drops it immediately.

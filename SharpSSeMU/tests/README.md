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

All ten tests pass (`full_chain`, `fase1`-`fase6`, `grounditem`, `shop`, `skills`).

The long-standing "one failure in the combat sequence" was not server balance: the harness started
each server with its console piped to the test and never read the pipe while the test ran. Once the
server had written enough, the pipe filled up and the server blocked inside `Console.WriteLine`,
holding the log lock, so the test waited for a packet that could not be sent. Server consoles now go
to `console.log` in each runtime folder (`%TEMP%/muservercs-e2e/rt-<tag>/<server>/`), and the tail is
printed at the end as before.

Two rules the combat steps rely on:

* **The killing blow sends no `0xD9`**, as in the original `CharacterLifeCheck`: the hit that kills
  ends in the monster's `0x17`, and its damage travels in the `0x9C` reward packet.
  `FakeMuClient.WaitForAttackOutcomeAsync` waits for either outcome.
* **`0x17` also announces a player's death**, so the death check compares the index that died with
  the monster's. Before that, a character dying looked like "killed the monster".

The fixtures seed what each scenario needs: Hero3 (Devil Square) gets high Vitality **and** current
Life, because `Life` is read from the character row and Vitality only raises `MaxLife`; the
ground-item test adds two deterministic drop monsters (`Deployment.seed_drop_monsters`).

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

🌐 **English** · [Español](protocolo-099b.es.md)

# The port to the 0.99B protocol

This client was born as Sven's Season 5.2 fork, heading towards Season 6. This document describes
the parallel work of making it speak the **MU Online 0.99B** protocol, that of the SSeMU 2.1.7
emulator, to use it against our own server (SharpSSeMU, in this same repository) without depending
on the original client's closed binary.

If you are going to touch networking, items, characters or the interface, read it first: almost
everything here was hard to find, and several of the errors give no symptom until much later.

## Why changing a few numbers is not enough

The two dialects are similar enough for the client to start, connect and show a login screen, and
different enough for nothing to work afterwards. Struct sizes differ by one or two bytes, the same
fields are in a different order, and several values travel with another encoding.

The result is that **errors do not fail: they lie**. A struct smaller than the wire's reads the
fields shifted and shows other numbers. A bigger one makes `safe_cast` discard the packet and the
whole function silently ceases to exist. Neither breaks anything visible.

That is why the rule of this port is: **nothing is transcribed by hand**.

## Where the truth comes from

### The generator

`tools/protogen/` reads the emulator's C++ sources and emits
`src/source/Protocol099B/Protocol099B.generated.h`: 259 structs, 211 with their opcode, each with
`static_assert`s of size and of the offset of every field.

What matters is not that it saves typing, but that it models the MSVC x86 layout — natural
alignment, `#pragma pack` including the one that appears in the middle of a struct, and **tail
padding**. That padding travels over the network, because the server sends `sizeof(struct)`, not the
sum of its fields. Transcribing structs by eye produces exactly the same six layout bugs we had
already found and fixed one by one on the server side.

If the protocol changes, regenerate. The generated file is not edited.

### The audits

Tools that turn "full compatibility" into a verifiable list:

| tool | answers |
|---|---|
| `tools/protogen/audit_client_structs.py` | which receivers cast to a struct of a different size than the wire's? |
| `tools/protogen/audit_client_coverage.py` | which opcodes does the server send that the client does not handle? |
| `tools/protogen/audit_muservercs.py` | which server structs do not match the emulator? |
| `tools/gamedata/audit_item_table.py` | which items does the client describe differently from how the server applies them? |
| `tools/gamedata/audit_skill_table.py` | the same for skills |
| `tools/gamedata/audit_move_table.py` | does the movement menu send to the map it claims? |
| `tools/gamedata/audit_monster_table.py` | does the client know the name of every monster? |
| `tools/gamedata/audit_shop_prices.py` | does the server charge what its own table declares? |

The first one paid off the most: it measures the client's structs with the same layout rules as the
server's and flags the two cases — reads shifted, or discards the packet — before anyone sees them
on screen.

## The item table was not the problem it seemed

For a while this appeared here as a pending risk: the client's table is the Season 6 one, so if any
version renumbered items within a group, objects would be drawn with the wrong name and icon.

Measured, it turned out false. Of the **349 items the GameServer defines, the client defines all
349 at the same (group, index)**, with the same name, the same inventory size, the same slot, the
same skill and the same requirements. The numbering is the same table; Season 6 extended it at the
top, it did not reorder it.

What there were, were **12 values** where the client said something different from what the server
applies, spread over six items: the damage of the three Archangel weapons, the defense of the Plate
Shield, a misspelled name (*Red Sprit Armor*) and the classes that can use the Dark Raven. That
breaks nothing — the server computes the same — but the tooltip lies.

`audit_item_table.py --sincronizar` (`--sync`) copied them from the server to the client, in the
three languages. It is worth re-running every time `Item.txt` is touched: the client does not find
out on its own.

Two format details, in case you have to come back:

- The `.bmd` is the *legacy* format of 84 bytes per record (a 30-byte name plus the fields).
  `ItemDataLoader` chooses the format by file size.
- The checksum (`GenerateCheckSum2`, key `0xE2F1`) is computed over the **already encrypted**
  data. The tool verifies it when reading, so a corrupt file is detected before anything is written.

### Skills are not synchronised, and that is on purpose

The skill table looks like the same case and is not: both sides use the same columns for different
things. `audit_skill_table.py` reports and touches nothing.

Two concrete reasons, both verified against the code:

- `Range` is **0 on the server for area skills** (Evil Spirit, Nova, Twisting Slash), because for
  those the reach is given by `Radio`. In the client that same number is the distance from which it
  lets you *cast* (`CSkillManager::GetSkillDistance`). Copying the 0 would leave them uncastable.
- The requirement columns (`ReqLevel`, `ReqEnergy`, `ReqLeadership`, `ReqKillCount`) are zero for
  **all** the server's skills. The client does have them and uses them. Copying them would erase all
  the requirements in the interface.

With that separated, of 58 skills four differences remain and **none changes what the player sees**:
two are from a monster attack, and the other two are skill reaches that the client resolves through
another path (`AT_SKILL_ADD_CRITICAL` is in `ClassAttack`'s exception list). The six `Effect`
differences and the Ice Storm class one fall on fields the client loads and never reads — the report
separates them so nobody chases them.

Thirteen names differ (*Soul Barrier* against *Mana Shield*, *Cometfall* against *Blast*). The one
seen on screen is the client's; the server's only appears in its logs. Aligning them is a fidelity
decision, not a fix.

### The warps were indeed broken

Of the three duplicated tables, this was the only one where a mismatch **breaks the game**, not just
the tooltip.

When choosing a destination, the client sends the `index` from its own table. The server looks it
up in its `Data/Move/Move.txt`. The two numberings had diverged: **20 of 23 destinations had
another's index**. You asked for Dungeon and landed in Devias2, you asked for Tarkan and landed in
LostTower5.

And it did not fail visibly, because of this in `OnTeleportMoveAsync`:

```csharp
var move = _moves.Get(recv.MoveIndex);
int gateNumber = move?.GateNumber ?? recv.MoveIndex;
```

If the index does not exist, **the number is used as a gate** and the trip happens anyway: without
charging the zen and without checking the level. The three destinations whose index did not exist on
the server side — Devias, Devias2 and Devias3 — fell through there.

In addition the menu offered 14 maps this server does not have (Elveland, Aida, Kanturu, Karutan,
Raklion, Vulcanus, Swamp), which through that same path moved you anywhere, and it did not offer
CustomArena, which does exist. Lorencia and Noria charged 2000 instead of 1000, so the client denied
the trip with 1500 in your pocket.

`audit_move_table.py --sincronizar` rebuilds the table with the server's numbering. The join between
the two tables is done **by gate number**, not by index or by name: the gate is the same on both
sides and is unique. That the names match on all 23 confirms the join is correct.

Two things to know if you touch it:

- **Names are kept, and copied as bytes.** The window decides whether you may travel by comparing
  the map name against `I18N::Game::Icarus` and `I18N::Game::Atlans` to demand wings or a mount, so
  overwriting them would break those checks. And they are copied without decoding because **the files
  are not all in the same encoding**: the Spanish name of Icarus is `CD 63 61 72 6F`, i.e. `Í` in
  Latin-1, even though the client reads them with `ConvertFromUtf8`. The tool's first version decoded
  to UTF-8 and re-encoded, and left `?caro`. The post-write verification caught it, and now compares
  names byte by byte.
- **`anStrifeIndex` has 42 written in the code** as the Gens map. With the old numbering that index
  was Vulcanus; with the server's it corresponds to nothing. There is no Gens map on this server, so
  it stays inert — but if `Move.txt` ever uses 42, it will flag itself.

### Monsters: one was missing

From the monster list the client only uses the number and the name; the stats are computed by the
server. Of the 231 the GameServer defines, the client named 230. Missing was **42, Red Dragon**, in
all three languages: `getMonsterName` returns `"()"` when it does not find the number.

It was not visible because 42 appears in no spawn file and invasions are not implemented on the
server side — but it was a matter of when, not if. Added to the three `NpcName_<language>.txt`,
next to the dragon-themed family (`43 Golden Budge Dragon`, `44 Dragon`) already there: *Red Dragon*
/ *Dragón Rojo* / *Dragão Vermelho*. That is why this audit does not synchronise like the items one:
the client's names are translated and the server's are in English, so adding one is a manual
translation, not a copy.

### Shops: the price shown is not the price charged

Shops do not send prices. `Data/Shop/*.txt` only says what each NPC sells, and each side computes
the number on its own: the client to display it (`ItemValue`, ZzzInfomation.cpp:1457), the server to
charge it (`ComputeShopBuyPrice`).

The part that does match is the main one: the server's `BuyMoney` field and the client's `iZen` are
equal across the 349 items, and both sides use it as the first choice. Wings and orbs are fine.

What did not match were the specially priced objects — jewels, event tickets, siege potions. The
server **had the right table** in `Data/Item/ItemValue.txt`, with the same numbers the client has
hard-coded (Bless 9,000,000, Soul 6,000,000, Chaos 810,000, Fruits 33,000,000...). **And nobody read
it**: the name appeared in no `.cs` nor in the `.ini`. It fell back to the general formula, which
gives something else:

| | declared | formula gave |
|---|---|---|
| Jewel of Bless | 9,000,000 | 18,700 |
| Jewel of Life | 45,000,000 | 72,600 |
| Fruits | 33,000,000 | 100 |
| Jewel of Chaos | 810,000 | 40,082,300 |

There are 72 rows and none matched. Selling a Bless paid 6,200 instead of 3,000,000; a Chaos was
bought for forty million.

**It is already fixed on the server side**, which is where it belonged: it would be a mistake to
make the client show 18,700, because the right number for 0.99B is the one in `ItemValue.txt` and
it is the one the client already showed. The fix is
`SharpSSeMU/src/MuServer.GameServer/World/ItemValue.cs`, which loads the table and consults it
between `BuyMoney` and the general formula.

Three things the episode left:

- **The server test pinned the wrong price.** `gameserver_shop_e2e_test.py` asserted that the Bless
  is bought at 18,700, which is exactly what the implementation gave. It was written against the
  code and not against the source — the same mistake as the 17-byte serial test. It now checks that
  selling pays 3,000,000, and the comment says which row of the file that number comes from.
- **Prices that scale by quantity were left out.** The original multiplies some of these values by
  the stack or by the remaining durability (the Symbol of Kundun is worth 30,000 per unit, arrows are
  worth their price times the fraction of durability they have left), and each item does it its own
  way. The file does not say which ones scale, so the port returns the value as is and documents it.
  A stack is quoted as one unit: low, but of the right order.
- **`audit_shop_prices.py` stayed on guard.** It no longer looks for the discrepancy — it is closed —
  but for the 72 rows to point at real items, for none to be covered by a `BuyMoney`, and for how much
  it would cost if the table became disconnected again.

## The three encryption layers

The GameServer socket carries three, in this order on receive:

1. **Stream cipher** (`StreamCipher099B`) — port of `HackCheck.cpp`. It covers *everything*,
   including the type and size bytes.
2. **Block cipher** (`BlockCipher099B`) — SimpleModulus, 8 plaintext bytes into 11 ciphertext. The
   keys turned out to be byte-for-byte identical to OpenMU's.
3. **XorData obfuscation** — and here there is an asymmetry: **the client obfuscates what it sends,
   the server does not obfuscate what it answers**.

The first layer is the reason the game socket is native instead of using the C# library: if the
cipher hides the size, no framer that works on the encrypted stream can separate packets. You have to
decrypt before segmenting. The ConnectServer, which travels in the clear, does keep using the library.

### The 17-byte serial

The stream cipher's key is derived from the `ServerSerial` in the `.ini`, which is a **17-byte**
field: the 16 characters plus the terminator. The server pads to that length before deriving.

Deriving over the 16 characters gives a different `key1` and shifts the whole stream. And this is the
sobering part: **there was a test that took it as good**, because it compared my implementation
against itself. Both sides must match the *original*, not each other. It was caught by the first
packet from a real server.

Since then the tests of this module compare against a reimplementation of the emulator's algorithm,
or against bytes captured from a live server, and say where each expected value came from.

## The encodings that deceive

### Two class encodings, in the same protocol

| where | formula | values |
|---|---|---|
| database, create character | `base << 4` | 0, 16, 32, 48, 64 |
| CharSet (appearance) | `base << 5` | 0, 32, 64, 96, 128 |

The CharSet shifts the class one bit more because the low bits carry the ViewState. And the pair of
creation packets uses **one in each direction**: the request goes in database format and the server
converts it to the CharSet's before answering.

Mixing them up is silent: both decoders return a valid number, just the wrong one — a Dark Knight
comes back as a Fairy Elf. The only one that matches is class 0, which is why creating a Dark Wizard
worked while everything else failed with "account full" (the server did not find the row in
`default_class_type` and returned `result=2`, a message that does not reveal the cause).

### An item is five bytes, not twelve

`ItemInfo099B` ports `ItemByteConvert`. Three things are not visible from reading the struct:

- The **ninth bit of the index** travels in the high bit of byte 3. Losing it turns any item in the
  second half of the table into another one 256 places earlier.
- The **third bit of the option level** weighs 4, not 1: the other two are in byte 1 and that one in 3.
- **Five zeros** mean "no item", and you have to check for it before unpacking anything, because
  index 0 is a real sword.

Also watch the emulator's names: `Option3` is the option level (+4/+8/+12/+16) and `NewOption` is the
excellent mask. It is easy to read them the wrong way round.

### Item indices have two numberings

0.99B advances by 32 per section; the client, by 512 per group. The sixteen sections are the same
sixteen groups in the same order, so the conversion is just changing the stride. A number above 32 is
an item 0.99B does not have: `ToWireItemIndex` rejects it instead of truncating it, because truncated
the server would understand another object.

### Ground zen always came out as zero

Money dropped on the floor does **not** travel in the normal item format. The server sends the zen's
index (section 14, sub 15) and puts the raw amount in bytes 1, 2 and 4 of the `ItemInfo`
(`CViewport::GCViewportItemSend`, Viewport.cpp:903-911), without going through the level/durability
encoding.

`ReceiveCreateItemViewport099B` did not distinguish that case: it passed the five bytes through
`DecodeItemInfo` like any item and called `CreateItemDrop`. The amount was read as level and
durability, and the pile was drawn as an ordinary object, so the zen always appeared as zero.

Now `DecodeDroppedMoney` recognises the zen index and unpacks the amount (the exact inverse of what
the server builds), and that branch calls `CreateMoneyDrop`, which is the one the client already used
for the loose-money packet 0x20.

### The damage number came out as the "HIT" sign

`ReceiveAttackDamage099B` sent every hit to `ReceiveAttackDamageCastle`, which is the **castle
siege** variant. That function forces the value to `-2` — the "HIT" sprite — and with the real damage
only chooses the colour of the sign:

```cpp
if (accumDamage > 0)
{
    rstDamage = -2;               // "HIT", never the figure
    if (accumDamage < 1000)       { /* red */ }
    else if (accumDamage < 3000)  { /* orange */ }
}
```

Hiding the number is what the siege does on purpose, but here it was applied to all combat. The normal
function, `ReceiveAttackDamage`, has the same signature and does draw the figure: it is the one called
now.

### A hand-transcribed head: the trade zen went out on 0x3B

`BuildTradeMoneyRequest` had the head written as a literal, `0x3B`, instead of taking it from the
generated struct. That value comes from a wrong comment in the emulator's `Trade.h` (`// C1:3B`); the
real opcode is **0x3A**, which is the one `Protocol.cpp` dispatches and the one `protogen` leaves in
`PMSG_TRADE_MONEY_RECV::kHead`.

The tricky part is that this project's own server had transcribed the same comment, so both sides
agreed and the zen exchange seemed to work. Against a real 0.99B server it did not: the packet is
silently discarded and the zen stays at zero. It is the reason structs are generated instead of copied.

### Hand-made big-endian

Several fields declared as two- or four-byte integers are filled byte by byte, starting from the most
significant (the original's `SET_NUMBER*`). Assigning them as native integers reverses them. It
happens with object indices, trade money and warehouse money.

In the money case the error is worse than a zero: it gives a huge figure, so it goes unnoticed until
somebody loses money.

## How it is put together

```
src/source/Protocol099B/
├── Protocol099B.generated.h   the structs, generated
├── Wire099B.{h,cpp}           builds packets, no transport
├── Send099B.{h,cpp}           sends them over the connection
├── ItemInfo099B.{h,cpp}       the five item bytes
├── CharSet099B.{h,cpp}        appearance and class
├── StreamCipher099B.{h,cpp}   layer 1
├── BlockCipher099B.{h,cpp}    layer 2 and obfuscation
├── GameFramer099B.{h,cpp}     receive: decrypt and segment
├── GameEncoder099B.{h,cpp}    send: obfuscate and encrypt
└── GameSocket099B.{h,cpp}     the socket, non-blocking
```

`Wire099B` does not depend on the transport on purpose: the tests verify the exact bytes without
linking the network.

`tools/probe099b/` is a probe run by hand against a live server. It covers what the tests cannot —
that the keys, the three ciphers and the framing combine correctly against the real server — and that
is why it is not in CTest.

## Beware of the transport

The game socket is native and **is not registered in the C# library's connection table**. Any send
function that calls `dotnet_Send*` with that connection's handle does not reach the server, and in the
worst case ends up on another socket.

`PacketFunctions_Base::GetHandle()` cuts that path: when the transport is native it returns an
invalid handle and leaves a warning in the log, once. **If you see that warning, the function that was
used is not ported yet.**

To add a send: a constructor in `Wire099B`, a function in `Send099B`, a test that pins the bytes, and
only then change the call site.

## Where we are

| | state |
|---|---|
| receive | every one the GameServer sends to the client, except `0x88` |
| send | 57 of the 59 opcodes the GameServer accepts |
| suite | 152 tests |

The two missing on the send side (`0x04` and `0x05`) are not client packets: they are messages the
GameServer receives from the DataServer and share the opcode space in the same `switch`.

### The chaos machine: what this said was wrong

This said that `0x88` (the chaos machine's success rate) had no receiver because the client computes
it on its own with `g_MixRecipeMgr`. It is true that it has no receiver. The reason it gave was an
unverified assumption, and it was wrong.

What is actually there:

- **`g_MixRecipeMgr` is dead.** It is Season 6's recipe system (Goblin Points, Jerridon, Chaos
  Card...), and loads its formulas from `mix.bmd` with `CMixRecipeMgr::OpenRecipeFile`. That function
  **is called by nobody**: `mix.bmd` is never opened. With no recipes loaded, `GetCurRecipe()` always
  gives `NULL`, `IsReadyToMix()` is never set to `true`, and the Season 6 mix window
  (`INTERFACE_MIXINVENTORY`, the one opened by talking to the NPC of value 3) stays inert: there is
  never a matching recipe, so the mix button is never enabled. It does not compute a number different
  from the server's — it computes nothing, because it never gets to try.
- **The real 0.99B wire does separate asking from mixing.** The generator takes it straight from
  `ChaosBox.cpp`: the client sends `0x88` (`PMSG_CHAOS_MIX_RATE_RECV`, only the type) to ask the rate
  before deciding, the server answers `0x88` (`PMSG_CHAOS_MIX_RATE_SEND`, rate and required zen)
  without touching anything; only when the player confirms does the client send `0x86` to really mix.
  `Send099B` has had `SendChaosMixRate` for a while, but no window calls it: there is no path in this
  client that today asks for the rate through `0x88`.
- **Meanwhile, the server had a real bug in `0x88`.** It is not cosmetic: `OnChaosMixRateAsync` called
  the same function that executes the mix for real (`ChaosMixLogic.CalculateAndExecuteMix`) — it
  charged the zen, emptied the Chaos Box and rolled the die **just by asking**. No client in this repo
  can trigger it today, because none sends `0x88` yet, but any real 0.99B client that does ask before
  mixing (which is the normal flow) would have lost the items and the zen without having accepted
  anything. Fixed in `SharpSSeMU/src/MuServer.GameServer/World/ChaosMixLogic.cs`: the function now
  takes an `execute` parameter, and the six mix formulas compute the rate the same but only
  charge/empty/roll the die when `execute: true`. `OnChaosMixRateAsync` passes `execute: false`.

### The mix formulas were not faithful either

`ChaosMixLogic.cs` said in its own comment that it was an "exact port" of `ChaosBox.cpp`. It was not.
The success rate came from an invented formula (`10 + Σ level×5`) instead of the real configuration
table (`GameServerInfoChaosMix`, loaded from the start but never connected to anything — `Program.cs`
read it into a local variable and there it stayed), and the success item came from an array of three
fixed weapons instead of the real list in `Data/EventItemBag/Special/*.txt`.

This was found and fixed by reading the emulator's **original source code**, which is in the repo
(`Source/Source/Emulator 0.99 (2.1.7)/GameServer/`) and had not been looked at for this system until
now. A real trap came out of it too: in `ChaosBox.h` the constant `CHAOS_MIX_WING1` (the wire's value
`7`) triggers the function `Wing2Mix(type=0)`, and `CHAOS_MIX_WING2` (`11`) triggers `Wing1Mix()` —
the names are crossed. The previous port had the ingredients of the two mixes swapped for following
the constant's name instead of the function it really triggers.

What remains faithful, verified line by line against the original: required ingredients, rate and zen
formulas, and which configuration table each one comes from — for Chaos Item, Plus Item +9→+10 and
+10→+11, Fruit, and the two wing mixes (also adding the "Cape" type, which was missing). What does not:
the success item in the mixes that create a new one is chosen among the real candidates of the
corresponding file, but without the full per-section weighting engine of `ItemBagEx` (which, as it
ships from the factory, also has the probability at 0 in the four files that were needed — replicating
it literally would have left the machine never delivering anything). That engine is shared with Devil
Square, Blood Castle and monster drops, and deserves its own port instead of a rushed one as a
dependency of this. The full detail of what is faithful and what is not is in the class's doc-comment.

Devil Square, Dinorant, Blood Castle and the two pet mixes remain unported: they need live event state
that this server does not track.

### The client side: the right window opens, the button does not mix yet

Two dispatch bugs were fixed, both from confusing a packet's subcode with a concept from another
season:

- **`0x31` subcode 3 is not "the mix finished".** It is the list of what is in the Chaos Box NOW
  (`GCChaosBoxSend`, sent when the window opens). The receiver treated it as if the mix had already
  finished and cleaned up the window with success/breakage sounds — which wiped the window's state
  right when opening it, before the player put anything in. Subcode 5 is not "failed resurrection"
  either: it is the same list but for the Trainer/pets window, not ported — it is now ignored instead
  of showing an invented message.
- **Moving an item into the Chaos Box did not show it there.** The generic item-move receiver (`0x24`)
  routes by slot number alone; it never looked at `result` (which says which container the item went
  to — the server sends `3` for the Chaos Box). An item moved into the box went in there on the server
  side but the client kept drawing it in the normal inventory.

With those two fixed, the window opens, shows what was already in the box, and reflects the items
dragged there — but the mix button keeps showing "you are missing items" always, because `case 3` no
longer calls the dead recipe system and there is nothing to replace it yet: it remains to decide which
kind of mix corresponds to what is in the box (there is no recipe that says so: in 0.99B this was
chosen by the player with a tab, information that is in no available source) and to send `0x88` to
show the percentage before mixing. It is the missing piece for the window to work end to end, and it
needs testing against the real client — something I cannot do myself.

What is missing, and why:

- **The interface** still has the Season 6 look. It is not a file swap; see [`ui-099b.md`](ui-099b.md).
- **The AG meter** stayed in its Season 6 position.
- An end-to-end combat test fails because the test character dies against the test monster. It is
  server balance, not the port's.

### Picking up floor items: it locks up after the first attempt

Reported in real testing: zen could be picked up only once, and afterwards no more items could be
picked up (neither zen nor objects) for the rest of the session. The symptom pointed at the client,
not the server: every "pick up" click (`MOVEMENT_GET`, `ZzzInterface.cpp`) is guarded behind
`SendGetItem == -1` so as not to send two requests for the same item while waiting for the answer —
and the code already had a comment on the variable (`WSclient.cpp:428`) predicting exactly this bug:
*"it may cause the stuck client bug, so that players can't pick up anything anymore"*.

The cause: `ReceiveGetItem099B` (the `PMSG_ITEM_GET_SEND` receiver that is really plugged in for
0.99B, not the old `ReceiveGetItem` which did do it right) never set `SendGetItem = -1` back on any of
its four exits — neither when it failed (`NOT_GET_ITEM`), nor when it was zen, nor when the item did
not fit in the inventory, nor on the normal success path. The first answer that arrived, whichever it
was, left the flag stuck forever; the next click did not even get to send the packet to the server.
The same reset the old dialect's receiver had was ported to the four exits of the 099B version.

### Levelling up: the golden aura did not appear

Another real-testing report. `ReceiveLevelUp099B` (the `PMSG_LEVEL_UP_SEND` receiver plugged in for
0.99B) copied the new stats — life, mana, experience, points — but never triggered the visual effect
or the sound. The later dialect's receiver (`ReceiveLevelUp`, still in the file, unused for 0.99B)
does: it creates the golden flashes around the character (`CreateJoint(BITMAP_FLARE, ...)`, 15 times,
plus a `BITMAP_MAGIC` effect — or the "Master" version with 20 flashes if the class is already
second-evolution) and plays `SOUND_LEVEL_UP`. That same block was ported, unchanged, to the end of
`ReceiveLevelUp099B`.

### Party sent into the void: three buttons that never migrated off the Dotnet layer

Found by auditing, with no live client, which of the `Mu099B::Send*` functions declared in
`Send099B.h` had no real caller (a `grep` of each against the whole tree). Of 59, twelve never appear
invoked; most are genuinely systems with no UI yet (Golden Archer, pets, hardware ID, the anti-latency
keepalive) and warrant nothing. But three DID have a real button behind them, and that button still
called the old Dotnet layer (`SocketClient->ToGameServer()->Send*`, the OpenMU protocol this port has
been replacing, see `src/source/Dotnet/`):

- **`CommandParty`** (`NewUICommandWindow.cpp`, the "Party" context menu when right-clicking another
  player) called `SendPartyInviteRequest` instead of `Mu099B::SendPartyRequest` — the same file
  already has the right pattern two lines above, in `CommandTrade`, so this single function was left
  out of that migration.
- **`CPartyMsgBoxLayout::OkBtnDown`/`CancelBtnDown`** (`NewUICommonMessageBox.cpp`, accepting/rejecting
  an invitation that reaches you) called `SendPartyInviteResponse` instead of
  `Mu099B::SendPartyRequestResult`.
- **`CNewUIPartyInfoWindow::LeaveParty`** (`NewUIPartyInfoWindow.cpp`, the exit button in the party
  window — reused both for leaving yourself and, if you are the leader, for kicking another) called
  `SendPartyPlayerKickRequest` instead of `Mu099B::SendPartyDeleteMember`.

A real 0.99B GameServer (and this port: `OnPartyRequestResultAsync`/`OnPartyDelMemberAsync`, already
implemented and waiting on the SharpSSeMU side) does not speak the OpenMU protocol, so these three
buttons did nothing — no visible error and no disconnection, the packet went straight into a dead
layer or to a format the real server does not recognise. All three were fixed to call the correct
`Mu099B::` function; it builds cleanly and the full suite (152 tests) stays green — there is no test
that covers "the button calls the right function" because that requires a running client, so this
kind of bug only shows up by grepping by hand, as here.

**Follow-up: the same bug, in the quest flow.** Before touching any of the 12 `Mu099B::Send*` with no
caller, it was first confirmed against `MuClient/Data/Interface/` (the real 0.99B client, not only the
emulator — see the note above on not confusing Season 6 with 0.99B) that Quest is a legitimate system
(`quest1.OZJ`/`quest2.OZJ` exist there) and not a later-season invention. With that confirmed,
`Mu099B::SendQuestState`/`SendQuestInfo` (opcodes 0xA2/0xA0, both already implemented on the
SharpSSeMU side — `OnQuestStateAsync`/`OnQuestInfoAsync`) also had no real caller, for the same reason
as Party: four sites were still on the old Dotnet layer.

`CSQuest` (`GameLogic/Quests/CSQuest.cpp`) is the central engine that advances the quest dialog —
`ProcessNextProgress()` is what runs when the player clicks "continue" and the condition is already
met. There, and in the other three places where the quest window (`NewUINPCQuest.cpp`) or the NPC
dialog itself (`ZzzInterface.cpp`, when talking to an NPC with `g_csQuest.IsInit()`) ask for the same
thing, they called `SendLegacyQuestStateSetRequest`/`SendLegacyQuestStateRequest` — despite the name,
"Legacy" here refers to **OpenMU**'s simpler quest dialect (still Dotnet, with its own
`LegacyQuestState` enum), not to anything of 0.99B. All four were fixed to
`Mu099B::SendQuestState`/`SendQuestInfo`. One detail worth writing down so as not to reopen it: the
packet's second field (`QuestState`) exists on the wire (`PMSG_QUEST_STATE_RECV`, confirmed with
`protogen` against the emulator's `Quest.h`) but **neither the original nor this port use it** to
decide the transition — `CQuest::CGQuestStateRecv` only looks at `QuestIndex` and at the state the
server already has stored for the player (`OnQuestStateAsync` logs it but does not use it either), so
the four sites send a fixed `1` on purpose, not a computed value.

A fifth place (`UI/Legacy/UIControls.cpp:5918`, `SendQuestStateRequest(questNumber, questGroup)`) was
deliberately left untouched: it is a different function, with a parameter shape that does not fit
`Mu099B::SendQuestState` (`questGroup` has no equivalent), it lives in the `UI/Legacy/` folder — the
old pre-NewUI UI, not necessarily reachable in this build — and there is no evidence it is the same
flow as `CSQuest`. Migrating it blindly without confirming it corresponds to something in real 0.99B
would be exactly the mistake the section above warns against.

### The shop: it was not possible to buy more than one item

Reported in real testing: the first purchase in an NPC shop worked fine, but no following click did
anything — it neither sent the packet nor showed an error. It is exactly the same unreset-flag bug
already found in `SendGetItem`/`ReceiveGetItem099B` (see the section above): `NewUINPCShop.cpp` guards
every buy click behind `if (BuyCost == 0) { Mu099B::SendItemBuy(...); BuyCost = ItemValue(...); }`
so as not to send two requests for the same item while waiting for the answer.

`ReceiveBuy099B` (the `PMSG_ITEM_BUY_SEND` receiver plugged in for 0.99B) never returned `BuyCost` to
`0` on any of its three exits — neither when the purchase failed with a message (`BuyFailed`), nor when
it failed silently (`BuyFailedSilent`), nor on the success path. The two `BuyCost = 0;` that already
existed in the file did not count: one is inside an old commented-out `ReceiveBuy` (dead code), and the
other is in `ReceiveBuyExtended`, a different dialect that 0.99B does not use. The reset was added to
the three exits of `ReceiveBuy099B`, same pattern as the `SendGetItem` fix. Verified with `ninja` +
`ctest -C Debug` (152/152).

### Monsters always hit, regardless of the player's defense

Reported in real testing: monsters did damage on every hit no matter how much defense the player had,
and the player never dodged. The formula in `ViewportTicker.cs` (the tick that makes monsters attack)
was invented, not ported: it had no miss/dodge check, it subtracted the player's full defense (without
the half that corresponds to an `OBJECT_USER` target), and the minimum-damage floor used
`monster_level / 3` instead of the real formula.

The original (the emulator's `Attack.cpp`) has no separate formula for monsters: `gObjMonsterAttack`
(`Monster.cpp:882`) builds a synthetic `PMSG_ATTACK_RECV`/`PMSG_SKILL_ATTACK_RECV` packet and puts it
through the same path (`CAttack::Attack`) a player attacking uses — it is a single symmetric function
for any attacker/target combination. That path was already well ported on the player-attacks-monster
side (`OnAttackAsync` in `ClientProtocolHandler.cs`), so it was used as the template:

- **Miss/dodge** (`CAttack::MissCheck`, `Attack.cpp:987-1031`): the same `AttackSuccessRate` (monster)
  vs. `DefenseSuccessRate` (player) check was added, including the 5% glancing blow when the attack
  was going to miss.
- **Target defense** (`CAttack::GetTargetDefense`, `Attack.cpp:1117-1154`): when the target is an
  `OBJECT_USER` the defense is halved — this always applies when the target is a player, not only for
  spells as the old code did.
- **Minimum damage floor** (`Attack.cpp:318-322`): `ATTACKER_level / 10` (minimum 1), not
  `monster_level / 3` (minimum 5); when the damage falls below the floor, `floor + rand(floor)` is
  used, not a direct clamp.

What was missing from this — the spell-type attack with its own magic damage formula, instead of
reusing the physical one with half the defense — was ported afterwards, see the section below.
Verified with `dotnet build` (0 errors).

### Monsters had no death effect: they vanished at once

Reported in real testing: when killing a monster, it disappeared instantly instead of showing any
death animation. The cause was in `OnMonsterDeathAsync` (`ClientProtocolHandler.cs`): besides sending
the death packet (`PMSG_USER_DIE_SEND`, 0x17), at the same instant it also removed the monster from the
viewport of everyone who saw it (`ViewportDestroy`, 0x14) and emptied its `VisibleTo`. The client
received "I died" and "disappear" in the same tick packet, so it never got to render the corpse.

The original (`CObjectManager::CharacterLifeCheck`, `ObjectManager.cpp:2893-2903`) does NOT do that:
on dying it only sets `Live = 0`, `State = OBJECT_DYING`, stores `RegenTime` and sends `GCUserDieSend` —
the object stays in the viewport of everyone who had it visible, playing its death animation normally.
It only removes the corpse from view when the respawn timer elapses: `gObjMonsterRegen`
(`Monster.cpp:369`) first calls `gObjClearViewport(lpObj)` (that is when it truly disappears) and then
revives the monster at its spawn point.

That same order was ported in three places:

- `OnMonsterDeathAsync`: it no longer removes the monster from the viewport or empties its `VisibleTo`
  — it only sends the death packet.
- `TickMonstersForObserverAsync` (`ViewportTicker.cs`): it used to treat any `IsDead` monster as "not
  visible", so the normal viewport diffing sweep sent it to be destroyed on the next tick anyway (with
  ~1 tick of delay, but the same symptom). Now a corpse in range keeps counting as visible — it is only
  no longer announced as "newly appeared" (its death was already announced with the 0x17).
- `RespawnDeadMonsters`: now, right before reviving the monster (the same point as `gObjClearViewport`
  inside `gObjMonsterRegen`), it sends `ViewportDestroy` to everyone who was still seeing the corpse
  and only then calls `_monsters.Respawn`. The reappearance packet (0x13) is sent naturally by the
  normal sweep, because the monster is no longer in anyone's `VisibleMonsters`.

Verified with `dotnet build` (0 errors).

### Monster spells used the physical formula, not the magic one

Reported in real testing, along with the rest of combat: monsters' ranged/spell attacks
(`isSpell == true` in `ViewportTicker.cs`) used exactly the same formula as a melee hit — the
monster's `PhysiDamageMin/Max` — only halving the defense before the fix above, and not even that
afterwards (because the halving now always applies). There was no real difference between "it hit me"
and "it cast a spell at me" other than the animation.

The original has two separate damage functions and `gObjMonsterAttack` (`Monster.cpp:882+`) picks one
or the other according to the attack type exactly as `GetMonsterAttackSkill` decides here:
`CAttack::GetAttackDamage` (physical) for melee, `CAttack::GetAttackDamageWizard`
(`Attack.cpp:1309-1384`) for spells. The key difference for an attacking monster: the magic branch
starts from `lpObj->MagicDamageMin/MagicDamageMax` — but `gObjSetMonster` (`Monster.cpp:206-365`),
which initialises a monster when spawning, **never touches those two fields**, so for any monster they
stay at zero. All the damage of a monster spell then comes from a single source:
`lpSkill->m_DamageMin`/`m_DamageMax`, the skill's own row in Skill.txt (plus `gSkillDamage.GetDamage`
at the end, an optional multiplier that today is a no-op because the real `SkillDamage.txt` carries no
data rows). That data was ALREADY ported (`SkillInfo.cs`/`SkillInfoTable`, used by the player's magic
attack in `ClientProtocolHandler.cs`) but `ViewportTicker` had no access to the table.

`SkillInfoTable`/`SkillDamageTable` were wired into `ViewportTicker` (the same instances `Program.cs`
already loads for the rest of the server) and, when `isSpell` is true and the chosen `skillId` has a
real row in `SkillList.txt`, the raw damage comes from `skill.DamageMin + rand(DamageMax - DamageMin)`
instead of the monster's `PhysiDamageMin/Max` — with the same optional `gSkillDamage.Apply` at the end,
after the damage floor. If the `skillId` has no row (some `GetMonsterAttackSkill` ids are heuristics
with no real skill behind them, see its own comment), it falls back to the physical formula as before,
so as not to leave the attack without damage. Verified with `dotnet build` (0 errors).

### Learning a skill with an orb did not make it appear in the selector

Reported in real testing: using an orb (e.g. the Twisting Slash one for Dark Knight) seemed to work —
the item was consumed, there was no error — but the skill never appeared in the window where the player
chooses which skill to use. Neither the newly learned skills nor, in some cases, the ones the character
already had on entering.

The same bug pattern that has already appeared twice (`SendGetItem`, `BuyCost`): the 099B receiver does
half of the job well and forgets the rest. Here the `CharacterAttribute->Skill[]` array DID end up
correctly written — `ReceiveMagicList099B` (the `PMSG_SKILL_LIST_SEND` receiver, C1:F3:11, really
plugged in for 0.99B) writes every slot correctly, both in the full login list and in the individual
adds/removals (`count` 0xFE/0xFF). The problem is that the selection window
(`NewUIMainFrameWindow.cpp:1389,1914,1965`) does not walk that array looking for non-empty slots — it
reads `CharacterAttribute->SkillNumber` directly, a separate counter. And that counter **was never
recomputed** in the 099B receiver.

The old dialect (`ReceiveMagicList`, same file, still active for the later protocol) does it: after
touching `Skill[]`, it walks the whole array and rebuilds `SkillNumber` (how many skills there are) and
`SkillMasterNumber` (how many are of Master type) from scratch, and also sanitises `Hero->CurrentSkill`
(the default selected skill) so it does not point at an empty slot. That same recomputation was ported
to the end of `ReceiveMagicList099B`, together with the cache notice
(`gSkillManager.InvalidateSkillAttributeRequirementsCache()`) that depends on the same change. **What
was NOT ported**, on purpose: the block below it that replaces a base skill with its "Master" version
(`AT_SKILL_POWER_SLASH_STR` overriding `AT_SKILL_POWER_SLASH`, etc.) — it is the Master skill tree
system, which does not exist in real 0.99B (confirm against the original client's `Data/Interface`);
porting it blindly would have been the same mistake this work keeps avoiding in Party/Quest/Guild.

Verified with `ninja` + `ctest -C Debug` (152/152).

### Upgrading an item with a jewel (Bless/Soul/Life/Guardian) made it disappear

Reported in real testing: using a Jewel of Bless on an item made it disappear from the inventory
instead of raising its level. The same bug applied to Soul, Life and any other jewel, because they all
share the same response packet.

It is not the usual "Dotnet layer never migrated" bug in the client→server direction — here the request
(`PMSG_ITEM_USE_RECV`, C1:26) already travels fine. It is the RESPONSE that was plugged in wrongly: the
server sends `PMSG_ITEM_MODIFY_SEND` (C1:F3:14) with 0.99B's fixed five-byte format
(`Item.WireByteSize`, see `Mu099B::PMSG_ITEM_MODIFY_SEND` generated by protogen), but the dispatcher of
`0xF3:0x14` (`WSclient.cpp`) pointed at `ReceiveModifyItemExtended` — the later-season dialect's
receiver, which interprets the item with `CalcItemLength` (variable-length format, twelve-odd bytes).
With the offset misread, `InsertItem` failed or inserted garbage, and the item being upgraded ended up
effectively deleted: it looks exactly like "I used the jewel and the item disappeared".

`ReceiveModifyItem099B` was added, the same pattern as `ReceiveGetItem099B`/`ReceiveBuy099B`
(`Mu099B::DecodeItemInfo` + `Mu099B::WriteClientItemBlock` over the real five bytes) combined with the
delete-before-insert that `ReceiveModifyItemExtended` already had (here the target slot already has an
item, unlike picking up from the floor or buying in a shop, where the slot is empty). The old dialect
was left untouched — it stays in the file, with no caller, like the rest of the later-season handlers.
Verified with `ninja` + `ctest -C Debug` (152/152).

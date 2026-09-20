🌐 **English** · [Español](README.es.md)

# protogen — the 0.99B wire format as a single source of truth

The 0.99B protocol is implemented in two places — the C# server SharpSSeMU and the C++ client — and
until now it was transcribed by hand in each. That transcription is what produced the layout bugs
documented in `SharpSSeMU/docs/development-log.md`: `CharSet` 13-vs-18, `ItemInfo`
5-vs-12, `PartyLife` at 1 byte per member, `Teleport.gate` BYTE-vs-WORD, the field order of
`PMSG_VIEWPORT_PLAYER`, `MAX_DS_LEVEL` 4-vs-7.

These tools derive the format from the original server's sources and leave it in an IR that either
side consumes.

## The right tree

The package ships **two** copies of the emulator. Only one applies:

| Path | What it is |
|---|---|
| `Source/Source/Emulator 0.99 (2.1.7)/` | **the right one.** `stdafx.h:7` says `GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]"` |
| `Source/Source/Emulator/` | a much later season (Kanturu, Raklion, MasterSkillTree, sockets). Unrelated to this project |

`parse_protocol.py` checks for the `0.99B CHS` marker and **aborts** if it does not find it, rather
than guessing. The telltale sign of the wrong tree is the `GAMESERVER_UPDATE` macro: it does not
exist in the right one.

## Flow

```bash
GS="../../../Source/Source/Emulator 0.99 (2.1.7)/GameServer"
CS="../../../Source/Source/Emulator 0.99 (2.1.7)/ConnectServer"

# 1. struct layout, from the headers
python parse_protocol.py --source-dir "$GS" --source-dir "$CS" --out protocol_099b.json

# 2. opcode, direction and transport, from the .cpp files
python annotate_opcodes.py --ir protocol_099b.json --source-dir "$GS" --source-dir "$CS"

# 3. C++ header for the client
python emit_cpp.py --ir protocol_099b.json --out ../../src/source/Protocol099B/Protocol099B.generated.h
```

Current state: **259 wire structs** (254 `PMSG_*`), **211 with an opcode**, **46 with alignment
padding on the wire**.

Both projects are needed: the GameServer defines the bulk, but the `0xF4` family (server list,
IP:port resolution) and SSeMU's own name list live in the ConnectServer.

## Why padding matters

The headers use `#pragma pack(1)` **only in specific regions** — sometimes opened from inside the
braces. Outside them MSVC's default packing applies, which inserts padding; and the original server
sends structs with `memcpy(..., sizeof(info))` or `header.set(head, sizeof(pMsg))`, so **that
padding travels on the wire**.

`PMSG_LIFE_SEND` is the textbook case:

```
offset 0  PBMSG_HEAD header   (3 bytes)
offset 3  BYTE type
offset 4  BYTE life[2]
offset 6  BYTE flag
offset 7  <-- 1 padding byte: the next DWORD aligns to 4
offset 8  DWORD ViewHP        (GAMESERVER_EXTRA==1)
```

Omitting the byte at offset 7 makes the client read `ViewHP` shifted: the "garbage damage/mana"
pattern that chased the port for several passes.

## Verification: the compiler is the referee

Neither the IR nor the generated header believe themselves. `emit_verifier.py` produces a `.cpp`
that redeclares every struct and adds `static_assert`s of `sizeof` and of `offsetof` per member; the
header from `emit_cpp.py` carries them built in. If the derived layout deviated from the one MSVC
builds, it would not compile.

```bash
python emit_verifier.py --ir protocol_099b.json --out verify_layout.cpp
# from a shell with vcvars32 (x86 -- the emulator and the client are 32-bit):
cl /nologo /c /std:c++20 /EHsc verify_layout.cpp
```

This step found a real bug in the emitter itself the first time it ran (it lost the `#pragma
pack(1)` declared inside the braces, in 7 structs). That is exactly its job.

## Server audit

`audit_muservercs.py` crosses the IR against SharpSSeMU's hand-written builders and flags those that
do not emit the real `sizeof`.

```bash
python audit_muservercs.py --ir protocol_099b.json \
  --muservercs ../../../SharpSSeMU/src/MuServer.GameServer/Protocol
```

It is a **triage tool, not an oracle**: it counts bytes, it does not compare offset by offset. It
separates into three buckets — confirmed mismatch, "check by hand" (ambiguous attribution or a width
that cannot be resolved statically), and consistent — so as not to present a heuristic as if it were
proof.

## What is left out

43 `PMSG_` structs have no opcode of their own, and that is fine: they are the **rows** repeated
inside an envelope (`PMSG_CHARACTER_LIST` inside `PMSG_CHARACTER_LIST_SEND`, `PMSG_SERVER_LIST`
inside `PMSG_SERVER_LIST_SEND`, `PMSG_ITEM_LIST`, `PMSG_FRIEND_LIST`...). Their layout is there,
which is what matters for reading them with the right stride.

## Client audit

Two more tools, looking at the other side of the wire: how much of all this MuMain knows how to read.

### `audit_client_coverage.py`

Enumerates the opcodes SharpSSeMU can send (taking them from its builders) and crosses them against
the client's `switch`. It tells you whether a `case` is missing, which is the quietest failure of
all: the packet arrives, nobody handles it, and the feature simply does not exist.

### `audit_client_structs.py`

Compares the **size** of the struct each receiver casts to against the wire's. It is the tool that
found the most bugs, because it distinguishes two failures that look the same from the outside:

* **The client struct is larger**: `safe_cast` rejects the packet and the function never runs. With a
  raw cast it is worse: it reads outside the buffer.
* **The client struct is smaller**: the fields are read shifted. Nothing fails, different numbers
  simply come out.

It models the layout with the same rules `parse_protocol.py` uses on the server side, so both sides
are measured with the same yardstick.

It does not tie a receiver to an opcode — the client's nested `switch` cannot be parsed reliably — so
the final crossing is by hand. It still reduces the problem from ninety-odd opcodes to a short list.

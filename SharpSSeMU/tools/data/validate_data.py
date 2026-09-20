#!/usr/bin/env python3
"""Validates a GameServer data folder before the server starts.

The GameServer reads its game tables (items, monsters, spawns, shops, gates...) once at start-up and, when
something is missing or inconsistent, it either logs one line and carries on with an empty table or misbehaves
in game. This tool checks the folder up front and explains, in plain words, what is wrong.

    python tools/data/validate_data.py --root <GameServer folder>      # folder that contains Data/ and Hack/

Exit code: 0 = no errors (warnings may exist), 1 = at least one error.
"""
import argparse
import os
import re
import sys

# -- report --------------------------------------------------------------------------------------------------


class Report:
    def __init__(self):
        self.errors = []
        self.warnings = []
        self.infos = []

    def error(self, where, msg):
        self.errors.append((where, msg))

    def warn(self, where, msg):
        self.warnings.append((where, msg))

    def info(self, msg):
        self.infos.append(msg)


# -- tiny tokenizer (same rules as the server's MemScript: // comments, "quoted strings", numbers, '*') ------

TOKEN = re.compile(r'"[^"]*"|[-+]?\d+(?:\.\d+)?|\*|[A-Za-z_][\w.]*|,')


def strip_comment(line):
    out, in_q = [], False
    i = 0
    while i < len(line):
        c = line[i]
        if c == '"':
            in_q = not in_q
        elif c == "/" and not in_q and line[i:i + 2] == "//":
            break
        out.append(c)
        i += 1
    return "".join(out)


def rows(path):
    """Yields (line_number, tokens) for every non-empty, non-comment line."""
    with open(path, "rb") as f:
        text = f.read().decode("latin-1")
    for n, raw in enumerate(text.splitlines(), 1):
        line = strip_comment(raw).strip()
        if not line:
            continue
        yield n, TOKEN.findall(line), line


def num(tok):
    if tok == "*":
        return -1
    try:
        return int(float(tok))
    except ValueError:
        return None


# -- layout ---------------------------------------------------------------------------------------------------

REQUIRED_FILES = [
    "Data/Item/Item.txt",
    "Data/Monster/MonsterList.txt",
    "Data/ShopManager.txt",
    "Data/Skill/SkillList.txt",
    "Data/Move/Gate.txt",
    "Data/Move/Move.txt",
    "Data/Message.txt",
    "Data/Quest/Quest.txt",
    "Data/Quest/QuestObjective.txt",
    "Data/Quest/QuestReward.txt",
    "Data/Event/DevilSquare.dat",
    "Data/Event/EventEntryLevel.dat",
    "Data/Event/EventStageSpawn.dat",
    "Hack/Enc2.dat",
    "Hack/Dec1.dat",
]
REQUIRED_DIRS = ["Data/Monster/Spawn", "Data/Shop", "Data/Terrain"]
OPTIONAL_FILES = ["Data/Item/ItemValue.txt", "Data/Skill/SkillDamage.txt"]
CONFIG_FILES = ["ChaosMix", "Character", "Command", "Common", "Custom", "Event", "Item", "Skill"]


def check_layout(root, rep):
    for rel in REQUIRED_FILES:
        p = os.path.join(root, rel)
        if not os.path.isfile(p):
            rep.error(rel, "missing file. The GameServer cannot load its tables without it.")
        elif os.path.getsize(p) == 0:
            rep.error(rel, "the file is empty.")
    for rel in REQUIRED_DIRS:
        p = os.path.join(root, rel)
        if not os.path.isdir(p) or not os.listdir(p):
            rep.error(rel, "missing or empty folder.")
    for rel in OPTIONAL_FILES:
        if not os.path.isfile(os.path.join(root, rel)):
            rep.warn(rel, "optional file not found; the server falls back to its built-in formula.")
    for name in CONFIG_FILES:
        rel = "Data/GameServerInfo - %s.dat" % name
        if not os.path.isfile(os.path.join(root, rel)):
            rep.error(rel, "missing configuration file (shipped with this repo under src/MuServer.GameServer/Data).")


# -- per-file checks --------------------------------------------------------------------------------------------


def check_terrain(root, rep):
    d = os.path.join(root, "Data", "Terrain")
    have = set()
    if not os.path.isdir(d):
        return have
    for fn in os.listdir(d):
        m = re.fullmatch(r"Terrain(\d+)\.att", fn, re.I)
        if not m:
            continue
        have.add(int(m.group(1)) - 1)  # Terrain<N>.att = map number N-1
        size = os.path.getsize(os.path.join(d, fn))
        if size != 65539:
            rep.error("Data/Terrain/" + fn, "size %d, expected 65539 (3-byte header + 256x256 attribute grid)." % size)
    if 0 not in have:
        rep.error("Data/Terrain", "Terrain1.att (Lorencia) is missing; characters start there.")
    return have


def parse_sections(path, rep, rel, max_sections=None):
    """Item.txt style: a lone number opens a section, rows follow, 'end' closes it. Returns {section: [rows]}."""
    sections, current, opened = {}, None, False
    for n, toks, line in rows(path):
        if len(toks) == 1 and num(toks[0]) is not None and toks[0] != "*":
            if opened:
                rep.error("%s:%d" % (rel, n), "section %d was not closed with 'end' before the next one." % current)
            current, opened = num(toks[0]), True
            sections[current] = []
        elif toks and toks[0].lower() == "end":
            if not opened:
                rep.error("%s:%d" % (rel, n), "'end' without an open section.")
            opened = False
        elif opened:
            sections[current].append((n, toks, line))
        else:
            rep.error("%s:%d" % (rel, n), "row outside of any section: %s" % line[:60])
    if opened:
        rep.error(rel, "the last section (%d) is not closed with 'end'." % current)
    return sections


def check_items(root, rep):
    rel = "Data/Item/Item.txt"
    p = os.path.join(root, rel)
    items = set()
    if not os.path.isfile(p):
        return items
    secs = parse_sections(p, rep, rel)
    if sorted(secs) != list(range(16)):
        rep.error(rel, "expected sections 0-15, found %s." % sorted(secs))
    for s, lst in secs.items():
        widths = {}
        seen = set()
        for n, toks, line in lst:
            idx = num(toks[0]) if toks else None
            if idx is None:
                rep.error("%s:%d" % (rel, n), "the first column (item index) is not a number.")
                continue
            if idx in seen:
                rep.error("%s:%d" % (rel, n), "duplicate item index %d in section %d." % (idx, s))
            seen.add(idx)
            items.add((s, idx))
            widths.setdefault(len(toks), []).append(n)
        if len(widths) > 1:
            common = max(widths, key=lambda k: len(widths[k]))
            for w, lines in widths.items():
                if w != common:
                    rep.error("%s:%d" % (rel, lines[0]),
                              "section %d: %d column(s) here but most rows have %d (%d row(s) affected)." % (s, w, common, len(lines)))
    rep.info("Item.txt: %d items in %d sections" % (len(items), len(secs)))
    return items


def check_monsters(root, rep):
    rel = "Data/Monster/MonsterList.txt"
    p = os.path.join(root, rel)
    classes = set()
    if not os.path.isfile(p):
        return classes
    widths, seen, ended = {}, set(), False
    for n, toks, line in rows(p):
        if toks and toks[0].lower() == "end":
            ended = True
            continue
        idx = num(toks[0]) if toks else None
        if idx is None:
            rep.error("%s:%d" % (rel, n), "the first column (monster class) is not a number.")
            continue
        if idx in seen:
            rep.error("%s:%d" % (rel, n), "duplicate monster class %d." % idx)
        seen.add(idx)
        classes.add(idx)
        widths.setdefault(len(toks), []).append(n)
    if not ended:
        rep.warn(rel, "no 'end' line at the end of the file.")
    if len(widths) > 1:
        common = max(widths, key=lambda k: len(widths[k]))
        for w, lines in widths.items():
            if w != common:
                rep.error("%s:%d" % (rel, lines[0]), "%d column(s) here but most rows have %d (%d row(s))." % (w, common, len(lines)))
    rep.info("MonsterList.txt: %d monster/NPC classes" % len(classes))
    return classes


SPAWN_WIDTH = {0: 5, 1: 8, 2: 5, 3: 5, 4: 5}  # class dist x y dir | class dist x y x2 y2 dir count


def check_spawns(root, rep, classes, terrain):
    d = os.path.join(root, "Data", "Monster", "Spawn")
    if not os.path.isdir(d):
        return
    total = 0
    no_terrain = []
    for fn in sorted(os.listdir(d)):
        rel = "Data/Monster/Spawn/" + fn
        m = re.match(r"(\d{3}) - .+\.txt$", fn)
        if not m:
            rep.warn(rel, "file name does not follow 'NNN - MapName.txt'; the server ignores it.")
            continue
        map_no = int(m.group(1))
        if map_no not in terrain:
            no_terrain.append(map_no)
        secs = parse_sections(os.path.join(d, fn), rep, rel)
        for typ, lst in secs.items():
            want = SPAWN_WIDTH.get(typ)
            for n, toks, line in lst:
                total += 1
                if want and len(toks) < want:
                    rep.error("%s:%d" % (rel, n), "type %d needs at least %d columns, found %d." % (typ, want, len(toks)))
                    continue
                cls = num(toks[0])
                if classes and cls not in classes:
                    rep.error("%s:%d" % (rel, n), "monster class %s does not exist in MonsterList.txt." % toks[0])
                coords = [num(t) for t in toks[2:4]]
                if typ == 1 and len(toks) >= 7:
                    coords += [num(t) for t in toks[4:6]]
                for c in coords:
                    if c is not None and c != -1 and not (0 <= c <= 255):
                        rep.error("%s:%d" % (rel, n), "coordinate %d outside the 0-255 map grid." % c)
                        break
    if no_terrain:
        rep.warn("Data/Monster/Spawn", "%d spawn file(s) belong to maps without a Terrain<N>.att (maps %s): those spawns can "
                 "never appear. Normal if you do not use those maps (e.g. Blood/Chaos Castle, Kalima)." % (len(no_terrain), ", ".join(map(str, no_terrain))))
    rep.info("Spawn files: %d spawn rows" % total)


def check_shops(root, rep, items, classes):
    rel = "Data/ShopManager.txt"
    p = os.path.join(root, rel)
    if not os.path.isfile(p):
        return
    shop_dir = os.path.join(root, "Data", "Shop")
    count = 0
    for n, toks, line in rows(p):
        if toks and toks[0].lower() == "end":
            break
        if len(toks) < 11:
            rep.error("%s:%d" % (rel, n), "expected 11 columns (NPC map x y dir AL0-3 GM \"file\"), found %d." % len(toks))
            continue
        count += 1
        npc = num(toks[0])
        if classes and npc not in classes:
            rep.warn("%s:%d" % (rel, n), "NPC class %s is not in MonsterList.txt." % toks[0])
        name = toks[-1].strip('"')
        fpath = os.path.join(shop_dir, name + ".txt")
        if not os.path.isfile(fpath):
            rep.error("%s:%d" % (rel, n), "shop file 'Data/Shop/%s.txt' does not exist." % name)
            continue
        for ln, raw_toks, raw_line in rows(fpath):
            if raw_toks and raw_toks[0].lower() == "end":
                break
            m = re.match(r"\s*(\d+)\s*,\s*(\d+)", raw_line)
            if not m:
                rep.error("Data/Shop/%s.txt:%d" % (name, ln), "row does not start with 'section,sub' (e.g. 14,013).")
                continue
            sec, sub = int(m.group(1)), int(m.group(2))
            if items and (sec, sub) not in items:
                rep.warn("Data/Shop/%s.txt:%d" % (name, ln), "item %d,%d is sold here but is not defined in Item.txt; the "
                         "server cannot show it." % (sec, sub))
    rep.info("ShopManager.txt: %d shop NPCs" % count)


def check_gates(root, rep, terrain):
    gates = {}
    gate_no_terrain = []
    rel = "Data/Move/Gate.txt"
    p = os.path.join(root, rel)
    if os.path.isfile(p):
        for n, toks, line in rows(p):
            if toks and toks[0].lower() == "end":
                break
            if len(toks) < 9:
                rep.error("%s:%d" % (rel, n), "expected at least 9 columns, found %d." % len(toks))
                continue
            idx, map_no = num(toks[0]), num(toks[2])
            gates[idx] = map_no
            if terrain and map_no not in terrain:
                gate_no_terrain.append(idx)
    if gate_no_terrain:
        rep.warn("Data/Move/Gate.txt", "%d gate(s) lead to maps without a terrain file (gates %s): players cannot use them." %
                 (len(gate_no_terrain), ", ".join(map(str, gate_no_terrain))))
    rel = "Data/Move/Move.txt"
    p = os.path.join(root, rel)
    if os.path.isfile(p):
        names = set()
        for n, toks, line in rows(p):
            if toks and toks[0].lower() == "end":
                break
            if len(toks) < 12:
                rep.error("%s:%d" % (rel, n), "expected 12 columns, found %d." % len(toks))
                continue
            name = toks[1].strip('"')
            if name in names:
                rep.error("%s:%d" % (rel, n), "duplicate warp name '%s'." % name)
            names.add(name)
            gate = num(toks[-1])
            if gates and gate not in gates:
                rep.error("%s:%d" % (rel, n), "warp '%s' points to gate %s, which is not in Gate.txt." % (name, toks[-1]))
    rep.info("Gate.txt/Move.txt: %d gates" % len(gates))


def check_skills(root, rep):
    rel = "Data/Skill/SkillList.txt"
    p = os.path.join(root, rel)
    if not os.path.isfile(p):
        return
    seen, count = set(), 0
    for n, toks, line in rows(p):
        if toks and toks[0].lower() == "end":
            break
        idx = num(toks[0])
        if idx is None:
            rep.error("%s:%d" % (rel, n), "the first column (skill index) is not a number.")
            continue
        if idx in seen:
            rep.error("%s:%d" % (rel, n), "duplicate skill index %d." % idx)
        seen.add(idx)
        count += 1
    rep.info("SkillList.txt: %d skills" % count)


def check_configs(root, rep):
    for name in CONFIG_FILES:
        rel = "Data/GameServerInfo - %s.dat" % name
        p = os.path.join(root, rel)
        if not os.path.isfile(p):
            continue
        text = open(p, "rb").read().decode("latin-1")
        if "[GameServerInfo]" not in text:
            rep.error(rel, "the [GameServerInfo] section header is missing.")
        vals = {}
        for n, raw in enumerate(text.splitlines(), 1):
            s = raw.strip()
            if not s or s.startswith(";") or s.startswith("["):
                continue
            if "=" not in s:
                rep.error("%s:%d" % (rel, n), "line is not 'Key = Value': %s" % s[:50])
                continue
            k, v = (x.strip() for x in s.split("=", 1))
            if k in vals:
                rep.warn("%s:%d" % (rel, n), "key '%s' appears twice; only one value is used." % k)
            vals[k] = v
        if name == "Common":
            ver = vals.get("ServerVersion", "")
            if not re.fullmatch(r"\d\.\d\d\.\d\d", ver):
                rep.error(rel, "ServerVersion '%s' should look like 1.02.00." % ver)
            ser = vals.get("ServerSerial", "")
            if not 1 <= len(ser) <= 16:
                rep.error(rel, "ServerSerial must be 1-16 characters (it is %d)." % len(ser))
            for k in ("ServerPort", "DataServerPort", "JoinServerPort", "ConnectServerPort"):
                if k in vals and not (vals[k].isdigit() and 0 < int(vals[k]) < 65536):
                    rep.error(rel, "%s = %s is not a valid TCP/UDP port." % (k, vals[k]))


def validate(root):
    rep = Report()
    if not os.path.isdir(os.path.join(root, "Data")):
        rep.error(root, "no 'Data' folder inside. Point --root at the folder that contains Data/ and Hack/.")
        return rep
    check_layout(root, rep)
    terrain = check_terrain(root, rep)
    items = check_items(root, rep)
    classes = check_monsters(root, rep)
    check_spawns(root, rep, classes, terrain)
    check_shops(root, rep, items, classes)
    check_gates(root, rep, terrain)
    check_skills(root, rep)
    check_configs(root, rep)
    return rep


def print_report(rep, limit=25):
    for i in rep.infos:
        print("  ok   " + i)
    for title, lst in (("ERROR", rep.errors), ("warning", rep.warnings)):
        for where, msg in lst[:limit]:
            print("  %-7s %s: %s" % (title, where, msg))
        if len(lst) > limit:
            print("  ... and %d more %s(s)" % (len(lst) - limit, title.lower()))
    print("\n%d error(s), %d warning(s)." % (len(rep.errors), len(rep.warnings)))
    if rep.errors:
        print("Fix the errors above before starting the GameServer.")
    else:
        print("Data folder looks good.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", required=True, help="folder that contains Data/ and Hack/ (the GameServer output folder)")
    ap.add_argument("--limit", type=int, default=25, help="max lines printed per category")
    a = ap.parse_args()
    rep = validate(a.root)
    print_report(rep, a.limit)
    sys.exit(1 if rep.errors else 0)


if __name__ == "__main__":
    main()

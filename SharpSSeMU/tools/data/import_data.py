#!/usr/bin/env python3
"""Builds a GameServer data folder from YOUR OWN copy of an original 0.99B server package.

This repository does not ship the game tables (items, monsters, spawns...): they belong to their owners. This tool
copies only the files the GameServer actually reads out of the package you provide, puts them where the server
expects them, and then validates the result, so you do not have to guess which files matter.

    python tools/data/import_data.py --source <path to your MuServer99B> --dest <GameServer folder>

  --source  your original server package (the folder that has Data/, or the Data folder itself)
  --dest    the GameServer output folder (created if missing). The result is  <dest>/Data  and  <dest>/Hack
  --dry-run only list what would be copied
  --force   overwrite files that already exist in the destination

The GameServerInfo - *.dat configuration files are NOT taken from your package: the ones shipped in this
repository are used (they are copied from src/MuServer.GameServer/Data unless --no-config is given).
Files are copied byte for byte, including any notices they contain.
"""
import argparse
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import validate_data  # noqa: E402

REPO_CONFIG_DIR = os.path.normpath(os.path.join(HERE, "..", "..", "src", "MuServer.GameServer", "Data"))

# (relative to the package's Data/, relative to <dest>)  -- file or folder
WANTED = [
    ("Item/Item.txt", "Data/Item/Item.txt"),
    ("Item/ItemValue.txt", "Data/Item/ItemValue.txt"),
    ("Monster/MonsterList.txt", "Data/Monster/MonsterList.txt"),
    ("Monster/Spawn", "Data/Monster/Spawn"),
    ("ShopManager.txt", "Data/ShopManager.txt"),
    ("Shop", "Data/Shop"),
    ("Skill/SkillList.txt", "Data/Skill/SkillList.txt"),
    ("Skill/SkillDamage.txt", "Data/Skill/SkillDamage.txt"),
    ("Move/Gate.txt", "Data/Move/Gate.txt"),
    ("Move/Move.txt", "Data/Move/Move.txt"),
    ("Message.txt", "Data/Message.txt"),
    ("Quest", "Data/Quest"),
    ("Terrain", "Data/Terrain"),
    ("Event", "Data/Event"),
    ("EventItemBag", "Data/EventItemBag"),
    ("EventItemBagManager.txt", "Data/EventItemBagManager.txt"),
    ("Character", "Data/Character"),
    ("Hack/Enc2.dat", "Hack/Enc2.dat"),
    ("Hack/Dec1.dat", "Hack/Dec1.dat"),
]


def find_data_dir(source):
    for cand in (source, os.path.join(source, "Data")):
        if os.path.isfile(os.path.join(cand, "Item", "Item.txt")) or os.path.isdir(os.path.join(cand, "Monster")):
            return cand
    return None


def copy_item(src, dst, force, dry, stats):
    if os.path.isdir(src):
        for dp, _dn, files in os.walk(src):
            for fn in files:
                s = os.path.join(dp, fn)
                d = os.path.join(dst, os.path.relpath(s, src))
                copy_item(s, d, force, dry, stats)
        return
    if os.path.exists(dst) and not force:
        stats["skipped"] += 1
        return
    stats["copied"] += 1
    if dry:
        print("  would copy", os.path.relpath(dst))
        return
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    shutil.copyfile(src, dst)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--source", required=True)
    ap.add_argument("--dest", required=True)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--no-config", action="store_true", help="do not copy the repo's GameServerInfo - *.dat files")
    a = ap.parse_args()

    data = find_data_dir(os.path.abspath(a.source))
    if not data:
        print("Could not find a Data folder (with Item/Item.txt, Monster/...) under: %s" % a.source)
        print("Point --source at your original server package (the folder that contains Data/).")
        sys.exit(2)
    dest = os.path.abspath(a.dest)
    print("Source Data: %s\nDestination:  %s\n" % (data, dest))

    stats = {"copied": 0, "skipped": 0}
    missing = []
    for rel_src, rel_dst in WANTED:
        s = os.path.join(data, rel_src)
        if not os.path.exists(s):
            missing.append(rel_src)
            continue
        copy_item(s, os.path.join(dest, rel_dst), a.force, a.dry_run, stats)

    if not a.no_config and os.path.isdir(REPO_CONFIG_DIR):
        for fn in sorted(os.listdir(REPO_CONFIG_DIR)):
            if fn.startswith("GameServerInfo") and fn.endswith(".dat"):
                copy_item(os.path.join(REPO_CONFIG_DIR, fn), os.path.join(dest, "Data", fn), a.force, a.dry_run, stats)

    print("Copied %d file(s), skipped %d already present (use --force to overwrite)." % (stats["copied"], stats["skipped"]))
    if missing:
        print("Not found in your package (skipped): " + ", ".join(missing))
    if a.dry_run:
        return
    print("\nValidating the result...\n")
    rep = validate_data.validate(dest)
    validate_data.print_report(rep)
    sys.exit(1 if rep.errors else 0)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Show or change where your original 0.99B client connects to.

The original client (main.exe + the SSeMU Main.dll) reads its connection settings from ServerInfo.sse, a small
obfuscated file next to main.exe. This tool reads and writes the few fields a local server needs, without the
GetMainInfo tool of the package:

    python configure_client.py show   <client folder or ServerInfo.sse>
    python configure_client.py set    <client folder> --ip 127.0.0.2 --port 44405 [--version 1.02.00] [--serial ...]
    python configure_client.py set    <client folder> --ip 127.0.0.2 --from-server <GameServer.ini>

Only the named fields change; everything else in the file (custom items, maps, effects...) is kept byte for byte.
The first time a file is changed a copy is saved as ServerInfo.sse.bak.

Two things the client needs to reach this server:
  * The address must NOT be 127.0.0.1: Webzen's main.exe refuses exactly that address. Use 127.0.0.2 (or your
    LAN IP). The GameServer address in the ConnectServer's ServerList.dat must follow the same rule.
  * ClientVersion and ClientSerial must match ServerVersion and ServerSerial in GameServer.ini.

This file format belongs to the SSeMU client; this script only exists so you can point the client you already
own at your own server.
"""

import argparse
import os
import re
import shutil
import struct
import sys

# MAIN_FILE_INFO (GetMainInfo.cpp): offset and size of each field, as the compiler lays it out.
FIELDS = {
    "CustomerName": (0, 32),
    "IpAddress": (65, 32),
    "IpAddressPort": (98, 2),
    "ClientVersion": (100, 8),
    "ClientSerial": (108, 17),
    "WindowName": (125, 32),
    "ClientName": (207, 32),
}
MIN_SIZE = 244


def _key(n):
    return n & 0xFF, (n >> 8) & 0xFF


def decode_byte(n, b):
    lo, hi = _key(n)
    return (((b - (0x30 ^ hi)) + (0x20 ^ hi)) & 0xFF) ^ (0x10 ^ lo)


def encode_byte(n, b):
    lo, hi = _key(n)
    return (((b ^ (0x10 ^ lo)) - (0x20 ^ hi)) + (0x30 ^ hi)) & 0xFF


def locate(path):
    if os.path.isdir(path):
        path = os.path.join(path, "ServerInfo.sse")
    if not os.path.isfile(path):
        sys.exit("ServerInfo.sse not found at %s -- pass the client folder (the one with main.exe) or the file." % path)
    return path


def read_field(data, name):
    offset, size = FIELDS[name]
    raw = bytes(decode_byte(offset + i, data[offset + i]) for i in range(size))
    if name == "IpAddressPort":
        return struct.unpack("<H", raw)[0]
    return raw.split(b"\0")[0].decode("latin-1")


def write_field(data, name, value):
    offset, size = FIELDS[name]
    if name == "IpAddressPort":
        raw = struct.pack("<H", value)
    else:
        raw = value.encode("latin-1")
        if len(raw) >= size:
            sys.exit("%s is too long: %d characters, at most %d." % (name, len(raw), size - 1))
        raw = raw.ljust(size, b"\0")
    for i, b in enumerate(raw):
        data[offset + i] = encode_byte(offset + i, b)


def load(path):
    data = bytearray(open(path, "rb").read())
    if len(data) < MIN_SIZE:
        sys.exit("%s is too small to be a ServerInfo.sse (%d bytes)." % (path, len(data)))
    return data


def server_values(ini_path):
    """ServerVersion and ServerSerial from a GameServer.ini (the [GameServerInfo] section)."""
    values = {}
    for line in open(ini_path, encoding="utf-8", errors="replace"):
        m = re.match(r"\s*(ServerVersion|ServerSerial)\s*=\s*(.*?)\s*$", line)
        if m:
            values[m.group(1)] = m.group(2)
    if "ServerVersion" not in values or "ServerSerial" not in values:
        sys.exit("%s has no ServerVersion/ServerSerial." % ini_path)
    return values["ServerVersion"], values["ServerSerial"]


def show(path):
    data = load(path)
    print(path)
    for name in ("IpAddress", "IpAddressPort", "ClientVersion", "ClientSerial", "WindowName", "ClientName"):
        print("  %-14s %s" % (name, read_field(data, name)))
    if read_field(data, "IpAddress") == "127.0.0.1":
        print("\n  warning: the original main.exe refuses 127.0.0.1 -- use 127.0.0.2 (set --ip 127.0.0.2).")
    if read_field(data, "CustomerName") != "SSE":
        print("\n  warning: CustomerName is '%s'. The server derives the stream-cipher keys from 'SSE' and the serial,"
              "\n  so this client will not understand it (the connection drops right after connecting)."
              % read_field(data, "CustomerName"))


def check(args, version, serial):
    problems = []
    if args.ip == "127.0.0.1":
        problems.append("the original main.exe refuses 127.0.0.1; use 127.0.0.2 or your LAN IP.")
    if args.port is not None and not 1 <= args.port <= 65535:
        problems.append("port must be between 1 and 65535.")
    if version is not None and not re.fullmatch(r"\d\.\d\d\.\d\d", version):
        problems.append("version must look like 1.02.00.")
    if serial is not None and len(serial) != 16:
        problems.append("the serial must be exactly 16 characters (the client sends 16).")
    return problems


def set_values(args):
    path = locate(args.client)
    version, serial = args.version, args.serial
    if args.from_server:
        version, serial = server_values(args.from_server)

    problems = check(args, version, serial)
    if problems and not args.force:
        sys.exit("Not changed:\n  - " + "\n  - ".join(problems) + "\n(--force writes it anyway.)")

    data = load(path)
    changes = {}
    if args.ip is not None:
        changes["IpAddress"] = args.ip
    if args.port is not None:
        changes["IpAddressPort"] = args.port
    if version is not None:
        changes["ClientVersion"] = version
    if serial is not None:
        changes["ClientSerial"] = serial
    if args.window_name is not None:
        changes["WindowName"] = args.window_name
    if not changes:
        sys.exit("Nothing to change: pass --ip, --port, --version, --serial, --from-server or --window-name.")

    before = {name: read_field(data, name) for name in changes}
    for name, value in changes.items():
        write_field(data, name, value)

    backup = path + ".bak"
    if not os.path.exists(backup):
        shutil.copyfile(path, backup)
    open(path, "wb").write(data)

    for name, value in changes.items():
        print("  %-14s %s -> %s" % (name, before[name], value))
    print("Saved %s (original kept as %s)." % (path, backup))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_show = sub.add_parser("show", help="print the connection settings")
    p_show.add_argument("client", help="client folder (with main.exe) or ServerInfo.sse")

    p_set = sub.add_parser("set", help="change the connection settings")
    p_set.add_argument("client", help="client folder (with main.exe) or ServerInfo.sse")
    p_set.add_argument("--ip", help="ConnectServer address (not 127.0.0.1)")
    p_set.add_argument("--port", type=int, help="ConnectServer TCP port (44405 by default on the server)")
    p_set.add_argument("--version", help="client version, e.g. 1.02.00 (must match ServerVersion)")
    p_set.add_argument("--serial", help="16-character serial (must match ServerSerial)")
    p_set.add_argument("--from-server", metavar="GameServer.ini", help="take version and serial from this GameServer.ini")
    p_set.add_argument("--window-name", help="window title")
    p_set.add_argument("--force", action="store_true", help="write even if a value looks wrong")

    args = parser.parse_args()
    if args.command == "show":
        show(locate(args.client))
    else:
        set_values(args)


if __name__ == "__main__":
    main()

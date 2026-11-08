import shutil
from pathlib import Path

# Repo layout: <ROOT>/SharpSSeMU/deploy_configs.py, with the original package
# (MuServer99B/, not shipped in this repo) as a sibling of SharpSSeMU/.
CS_ROOT = Path(__file__).resolve().parent
ROOT = CS_ROOT.parent


def write_server_configs(cs_dir: Path, js_dir: Path, ds_dir: Path, gs_dir: Path) -> None:
    """The default configuration of the four servers, for a local setup (everything on this machine). Used by
    this script and by tools/release/build_release.py, so the release zip gets exactly the same files."""
    for p in [cs_dir, js_dir, ds_dir, gs_dir]:
        p.mkdir(parents=True, exist_ok=True)

    # 1. ConnectServer configs
    (cs_dir / "ConnectServer.ini").write_text(
        "[ConnectServerInfo]\n"
        "Language = en\n"
        "ConnectServerPortTCP = 44405\n"
        "ConnectServerPortUDP = 55557\n"
        "ConnectServerMaxUserNumber = 500\n"
        "MaxConnectionPerIP = 50\n"
        "MaxPacketPerSecond = 0\n"
        "MaxConnectionIdle = 60\n",
        encoding="utf-8"
    )
    (cs_dir / "BlackList.txt").write_text("0\nend\n", encoding="utf-8")
    # 127.0.0.2, not 127.0.0.1: the original 0.99B main.exe refuses 127.0.0.1, also for the GameServer it is sent
    # to after the server list. It is still this machine, so the experimental ported client works with it too.
    (cs_dir / "ServerList.dat").write_text(
        '   0            "GameServer_0"   "127.0.0.2"        55900       1\nend\n',
        encoding="utf-8"
    )

    # 2. JoinServer configs
    (js_dir / "JoinServer.ini").write_text(
        "[JoinServerInfo]\n"
        "Language = en\n"
        "JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver\n"
        "JoinServerPort = 55970\n"
        "ConnectServerAddress = 127.0.0.1\n"
        "ConnectServerPort = 55557\n"
        "CaseSensitive = 0\n"
        "MD5Encryption = 0\n",
        encoding="utf-8"
    )
    (js_dir / "AllowableIpList.txt").write_text('0\n"127.0.0.1"\nend\n', encoding="utf-8")

    # 3. DataServer configs
    (ds_dir / "DataServer.ini").write_text(
        "[DataServerInfo]\n"
        "Language = en\n"
        "DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver\n"
        "DataServerPort = 55960\n",
        encoding="utf-8"
    )
    (ds_dir / "AllowableIpList.txt").write_text('0\n"127.0.0.1"\nend\n', encoding="utf-8")
    (ds_dir / "BadSyntax.txt").write_text('"fuck"\n"admin"\nend\n', encoding="utf-8")

    # 4. GameServer configs
    (gs_dir / "GameServer.ini").write_text(
        "[GameServerInfo]\n"
        "Language = en\n"
        "ServerName = SSeMU GameServer_0\n"
        "ServerCode = 0\n"
        "ServerPort = 55900\n"
        "ServerVersion = 1.02.00\n"
        "ServerSerial = SharpSSeMU99B-v1\n"
        "ServerEncDecKey1 = 0\n"
        "ServerEncDecKey2 = 0\n"
        "ServerMaxUserNumber = 300\n"
        "JoinServerAddress = 127.0.0.1\n"
        "JoinServerPort = 55970\n"
        "DataServerAddress = 127.0.0.1\n"
        "DataServerPort = 55960\n"
        "ConnectServerAddress = 127.0.0.1\n"
        "ConnectServerPort = 55557\n",
        encoding="utf-8"
    )


def copy_game_data(gs_dir: Path) -> None:
    """The original package's Data/ and Hack/ next to the GameServer (MuServer99B/ must be next to SharpSSeMU/)."""
    # 5. Hack folder
    hack_src = ROOT / "MuServer99B" / "Data" / "Hack"
    hack_dest = gs_dir / "Hack"
    hack_dest.mkdir(parents=True, exist_ok=True)
    if hack_src.exists():
        for f in hack_src.iterdir():
            if f.is_file():
                shutil.copy(f, hack_dest / f.name)

    # 6. Data folder
    data_src = ROOT / "MuServer99B" / "Data"
    data_dest = gs_dir / "Data"
    data_dest.mkdir(parents=True, exist_ok=True)
    if data_src.exists():
        for item in data_src.iterdir():
            target = data_dest / item.name
            if item.is_dir():
                shutil.copytree(item, target, dirs_exist_ok=True)
            else:
                shutil.copy(item, target)

    # Also copy GameServer/DATA/*.dat into Data/
    gs_data_src = ROOT / "MuServer99B" / "GameServer" / "DATA"
    if gs_data_src.exists():
        for item in gs_data_src.iterdir():
            if item.is_file():
                shutil.copy(item, data_dest / item.name)


if __name__ == "__main__":
    bin_dir = lambda name: CS_ROOT / "src" / f"MuServer.{name}" / "bin" / "Debug" / "net10.0"
    write_server_configs(bin_dir("ConnectServer"), bin_dir("JoinServer"), bin_dir("DataServer"), bin_dir("GameServer"))
    copy_game_data(bin_dir("GameServer"))
    print("All configuration files and data assets deployed successfully!")

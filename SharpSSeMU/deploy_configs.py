import os
import shutil
from pathlib import Path

# Repo layout: <ROOT>/SharpSSeMU/deploy_configs.py, with the original package
# (MuServer99B/, not shipped in this repo) as a sibling of SharpSSeMU/.
CS_ROOT = Path(__file__).resolve().parent
ROOT = CS_ROOT.parent

cs_bin = CS_ROOT / "src" / "MuServer.ConnectServer" / "bin" / "Debug" / "net10.0"
js_bin = CS_ROOT / "src" / "MuServer.JoinServer" / "bin" / "Debug" / "net10.0"
ds_bin = CS_ROOT / "src" / "MuServer.DataServer" / "bin" / "Debug" / "net10.0"
gs_bin = CS_ROOT / "src" / "MuServer.GameServer" / "bin" / "Debug" / "net10.0"

for p in [cs_bin, js_bin, ds_bin, gs_bin]:
    p.mkdir(parents=True, exist_ok=True)

# 1. ConnectServer configs
(cs_bin / "ConnectServer.ini").write_text(
    "[ConnectServerInfo]\n"
    "ConnectServerPortTCP = 44405\n"
    "ConnectServerPortUDP = 55557\n"
    "ConnectServerMaxUserNumber = 500\n"
    "MaxConnectionPerIP = 50\n"
    "MaxPacketPerSecond = 0\n"
    "MaxConnectionIdle = 60\n",
    encoding="utf-8"
)
(cs_bin / "BlackList.txt").write_text("0\nend\n", encoding="utf-8")
(cs_bin / "ServerList.dat").write_text(
    '   0            "GameServer_0"   "127.0.0.1"        55900       1\nend\n',
    encoding="utf-8"
)

# 2. JoinServer configs
(js_bin / "JoinServer.ini").write_text(
    "[JoinServerInfo]\n"
    "JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver\n"
    "JoinServerPort = 55970\n"
    "ConnectServerAddress = 127.0.0.1\n"
    "ConnectServerPort = 55557\n"
    "CaseSensitive = 0\n"
    "MD5Encryption = 0\n",
    encoding="utf-8"
)
(js_bin / "AllowableIpList.txt").write_text('0\n"127.0.0.1"\nend\n', encoding="utf-8")

# 3. DataServer configs
(ds_bin / "DataServer.ini").write_text(
    "[DataServerInfo]\n"
    "DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver\n"
    "DataServerPort = 55960\n",
    encoding="utf-8"
)
(ds_bin / "AllowableIpList.txt").write_text('0\n"127.0.0.1"\nend\n', encoding="utf-8")
(ds_bin / "BadSyntax.txt").write_text('"fuck"\n"admin"\nend\n', encoding="utf-8")

# 4. GameServer configs
(gs_bin / "GameServer.ini").write_text(
    "[GameServerInfo]\n"
    "ServerName = SSeMU GameServer_0\n"
    "ServerCode = 0\n"
    "ServerPort = 55900\n"
    "ServerVersion = 1.02.00\n"
    "ServerSerial = PoweredSetecSoft\n"
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

# 5. Hack folder
hack_src = ROOT / "MuServer99B" / "Data" / "Hack"
hack_dest = gs_bin / "Hack"
hack_dest.mkdir(parents=True, exist_ok=True)
if hack_src.exists():
    for f in hack_src.iterdir():
        if f.is_file():
            shutil.copy(f, hack_dest / f.name)

# 6. Data folder
data_src = ROOT / "MuServer99B" / "Data"
data_dest = gs_bin / "Data"
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

print("All configuration files and data assets deployed successfully!")

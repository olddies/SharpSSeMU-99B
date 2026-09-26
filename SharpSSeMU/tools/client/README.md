# Client tools

🌐 **English** · [Español](#español)

## `configure_client.py` — point your original 0.99B client at your server

The original client (Webzen's `main.exe` with the SSeMU `Main.dll`) reads where to connect from
`ServerInfo.sse`, next to `main.exe`. This script shows and changes those settings without the package's
`GetMainInfo` tool. Only the fields you name change; a copy of the original is kept as `ServerInfo.sse.bak`.

```bash
# what the client connects to now
python configure_client.py show <client folder>

# this machine, the default ConnectServer port, and the version/serial the GameServer expects
python configure_client.py set <client folder> --ip 127.0.0.2 --port 44405 --from-server <GameServer.ini>
```

- **Do not use `127.0.0.1`**: the original `main.exe` refuses exactly that address ("You are disconnected
  from the server"). `127.0.0.2` is the same machine. The GameServer address in the ConnectServer's
  `ServerList.dat` must follow the same rule (`deploy_configs.py` writes `127.0.0.2`).
- The client's version and serial must match `ServerVersion` / `ServerSerial` in `GameServer.ini`
  (`--from-server` copies them). The serial is exactly 16 characters.
- Other options: `--version`, `--serial`, `--window-name`, `--force` (write a value the checks reject).

The client itself is not part of this repository (see [`NOTICE.md`](../../../NOTICE.md)).

## Español

`configure_client.py` muestra y cambia a dónde conecta tu cliente original 0.99B (el `ServerInfo.sse` junto a
`main.exe`), sin la herramienta `GetMainInfo` del paquete. Solo cambian los campos que indicás y queda una copia
en `ServerInfo.sse.bak`.

```bash
python configure_client.py show <carpeta del cliente>
python configure_client.py set <carpeta del cliente> --ip 127.0.0.2 --port 44405 --from-server <GameServer.ini>
```

No uses `127.0.0.1` (el `main.exe` original rechaza esa dirección); `127.0.0.2` es la misma máquina. La versión
y el serial tienen que coincidir con `ServerVersion` / `ServerSerial` de `GameServer.ini` (`--from-server` los
copia). El cliente no forma parte de este repositorio (ver [`NOTICE.md`](../../../NOTICE.md)).

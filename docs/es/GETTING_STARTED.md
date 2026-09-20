# Puesta en marcha

🌐 [English](../GETTING_STARTED.md) · **Español**

Esta guía te lleva de un checkout limpio a un personaje caminando por Lorencia en tu propia
máquina. Todo corre en `127.0.0.1`, así que no hace falta configurar firewall ni router.

- [1. Requisitos](#1-requisitos)
- [2. Traé tus propios archivos del juego](#2-traé-tus-propios-archivos-del-juego)
- [3. Base de datos](#3-base-de-datos)
- [4. Compilar y desplegar el servidor](#4-compilar-y-desplegar-el-servidor)
- [5. Levantar los servidores](#5-levantar-los-servidores)
- [6. Conectar un cliente](#6-conectar-un-cliente)
- [7. El AdminPanel](#7-el-adminpanel)
- [8. Referencia de configuración](#8-referencia-de-configuración)
- [9. Problemas comunes](#9-problemas-comunes)

## 1. Requisitos

| Herramienta | Versión | Para qué |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | **10.0** | compilar y correr el servidor |
| [PostgreSQL](https://www.postgresql.org/download/) | 16 (cualquier versión reciente debería andar) | cuentas, personajes, inventarios |
| Python | 3.10+ | el script de despliegue, el lanzador y los tests end-to-end |
| Windows | 10/11 | el cliente del juego solo corre en Windows; los servidores son multiplataforma |

Toolchain probado: .NET SDK 10.0.302, PostgreSQL 16.14, Python 3.14, CMake 4.2, MSVC 14.44.

## 2. Traé tus propios archivos del juego

Este repositorio **no contiene assets del juego** (ver [`NOTICE.md`](../../NOTICE.md)). El servidor
carga al arrancar las tablas `Data/` originales de SSeMU y la suite de tests lee las claves de
cifrado del cliente, así que dejá las carpetas del paquete original **junto a** `SharpSSeMU/` — es
decir, en la raíz del repositorio:

```
<raíz del repo>/
├── SharpSSeMU/            ← este repo (servidor)
├── MuMain-099B/           ← este repo (cliente)
├── MuClient/              ← LO PONÉS VOS: el cliente 0.99B original
│   ├── main.exe, Main.dll
│   └── Data/              (necesita al menos Enc1.dat / Dec2.dat)
├── MuServer99B/           ← LO PONÉS VOS: el paquete de servidor SSeMU 0.99B original
│   ├── Data/              (Monster/, Item/, Skill/, Shop/, Event/, Hack/, Move.txt …)
│   └── GameServer/DATA/   (GameServerInfo - *.dat)
└── Source/                ← opcional: fuentes C++ del emulador SSeMU, usadas como referencia
```

Las tres carpetas están en `.gitignore`. Cuáles necesitás depende de lo que quieras hacer:

| Querés… | Necesitás |
|---|---|
| correr el servidor | `MuServer99B/Data` (+ `MuServer99B/GameServer/DATA`) |
| jugar con el cliente original | `MuClient/` |
| jugar con el cliente portado | una carpeta `Data/` de cliente en `MuMain-099B/src/bin/Data` (ver [BUILDING](BUILDING.md#cliente)) |
| correr los tests end-to-end | `MuClient/Data/Enc1.dat` + `MuServer99B/Data` |
| auditar el port contra el C++ original | `Source/` |

## 3. Base de datos

Creá el rol y la base, y aplicá los cuatro archivos de esquema **en orden**:

```bash
psql -U postgres -c "CREATE USER muserver WITH PASSWORD 'muserver';"
createdb -U postgres -O muserver muonline

psql -U muserver -d muonline -f SharpSSeMU/db/postgres/001_accounts.sql           # memb_info, memb_stat + cuentas semilla
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/002_characters.sql         # personajes, inventarios, rankings
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/003_default_class_seed.sql # stats base por clase
psql -U muserver -d muonline -f SharpSSeMU/db/postgres/004_friends.sql            # lista de amigos
```

`001_accounts.sql` siembra dos cuentas, `test`/`test` y `admin`/`admin`. **Solo para desarrollo.**

> Las contraseñas se guardan según `MD5Encryption` en `JoinServer.ini` (`0` = texto plano, el valor
> por defecto para desarrollo local). Activalo y cambiá las contraseñas semilla para cualquier uso compartido.

## 4. Compilar y desplegar el servidor

```bash
dotnet build SharpSSeMU/SharpSSeMU.sln
python SharpSSeMU/deploy_configs.py
```

`deploy_configs.py` escribe los `.ini` que necesita cada servidor en su carpeta `bin/Debug/net10.0`
(puertos, cadena de conexión a Postgres, `ServerList.dat`…) y copia `MuServer99B/Data`, `Hack/` y
los `GameServerInfo - *.dat` junto a los binarios del GameServer. Es seguro volver a correrlo;
sobrescribe los archivos generados. Si tus credenciales de PostgreSQL difieren de las por defecto,
editá las cadenas de conexión al principio de ese script (o los `JoinServer.ini` / `DataServer.ini` generados).

Para un despliegue Release / Linux ver [`SharpSSeMU/README.md`](../../SharpSSeMU/README.md#deployment).

## 5. Levantar los servidores

Windows, cuatro consolas (ConnectServer → JoinServer → DataServer → GameServer, con las demoras correctas):

```powershell
SharpSSeMU\start_servers.bat
```

o los cuatro en una sola consola:

```bash
python SharpSSeMU/run_servers.py
```

o a mano, una terminal cada uno, desde `SharpSSeMU/src/<Proyecto>/bin/Debug/net10.0`:

```bash
dotnet MuServer.ConnectServer.dll
dotnet MuServer.JoinServer.dll
dotnet MuServer.DataServer.dll
dotnet MuServer.GameServer.dll
```

Un arranque sano hace que el GameServer loguee (en español) cuántos mapas, tipos de monstruo,
monstruos instanciados (~2 900), items, skills y tiendas cargó, y termine con
`GameServer listo … en el puerto TCP 55900`.

| Servidor | Puerto | Rol |
|---|---|---|
| ConnectServer | TCP **44405**, UDP 55557 | lista de servidores; el cliente se conecta acá primero |
| GameServer | TCP 55900 | el juego |
| JoinServer | TCP 55970 | login de cuentas (interno) |
| DataServer | TCP 55960 | personajes, inventario, rankings (interno) |
| AdminPanel | HTTP 5281 | administración web (opcional) |

Si una corrida anterior dejó puertos ocupados: `python SharpSSeMU/kill_ports.py`.

## 6. Conectar un cliente

**Cliente original** (`MuClient/main.exe`): su `Main.dll` ya apunta a `127.0.0.1:44405`
(`ClientVersion = 1.02.00`, `ClientSerial = PoweredSetecSoft`, los mismos valores que `GameServer.ini`).
Abrí `main.exe` e iniciá sesión con `test` / `test`. Guía paso a paso de esta vía:
[`SharpSSeMU/COMO_PROBAR_CON_CLIENTE_REAL.md`](../../SharpSSeMU/COMO_PROBAR_CON_CLIENTE_REAL.md).

**Cliente portado** (el `MuMain-099B` de este repo): compilalo ([BUILDING](BUILDING.md#cliente)) y
ejecutá `Main.exe`. Su `config.ini` (junto al ejecutable) define el destino:

```ini
[CONNECTION SETTINGS]
ServerIP=127.0.0.1
ServerPort=44405
```

Por línea de comandos: `Main.exe connect /u127.0.0.1 /p44405`.

Primera vez: creá un personaje, elegilo y entrá al mundo. Ver
[Limitaciones conocidas](STATUS.md#limitaciones-conocidas) para lo que todavía no está implementado.

## 7. El AdminPanel

Una interfaz web para ajustar el servidor sin tocar archivos a mano. Desde la carpeta del proyecto:

```bash
cd SharpSSeMU/src/MuServer.AdminPanel
dotnet run            # → http://localhost:5281
```

> Levantalo con `dotnet run` desde la carpeta del proyecto. Correr el `.dll` compilado directamente
> desde `bin/` no resuelve los recursos estáticos del panel y la página se ve rota.

- **Contraseña:** definí `Admin:Password` en `appsettings.json`. Si está vacía, se genera una al azar al arrancar y se imprime en la consola.
- **Qué edita:** los archivos de datos reales del juego (`Data/…` y `GameServerInfo - *.dat`, conservando comentarios y formato; se escribe un `.bak` la primera vez) y — en la página *Personajes* — la base PostgreSQL directamente (stats, inventario, baúl, con selector de items con búsqueda).
- **En vivo vs. reinicio:** el GameServer lee su configuración **una sola vez al arrancar**, así que las ediciones de archivos aplican tras reiniciarlo. Solo *Cuentas conectadas* y *Mensaje global* actúan sobre el servidor en marcha.
- **Personajes:** un personaje conectado vive en la memoria del GameServer y su autoguardado pisa tu edición — desconectalo antes de editarlo.
- **Seguridad:** el panel escribe configuración del servidor. Mantenelo en tu red interna.

Ajustes (todos opcionales, en `appsettings.json`): `GameServer:DataPath` (carpeta `Data/` del GameServer),
`DataServer:IniPath`, `Database:ConnectionString`, `Admin:Password`.

## 8. Referencia de configuración

| Archivo | Dónde | Qué |
|---|---|---|
| `ConnectServer.ini`, `ServerList.dat`, `BlackList.txt` | carpeta de salida de ConnectServer | puertos; la lista de servidores que ve el cliente |
| `JoinServer.ini`, `AllowableIpList.txt` | carpeta de salida de JoinServer | cadena de Postgres, `MD5Encryption`, IPs permitidas |
| `DataServer.ini`, `AllowableIpList.txt`, `BadSyntax.txt` | carpeta de salida de DataServer | cadena de Postgres, nombres prohibidos |
| `GameServer.ini` | carpeta de salida de GameServer | nombre/código/puerto, chequeo de versión + serial, direcciones de los otros servidores |
| `Data/GameServerInfo - *.dat` | carpeta de salida de GameServer | ~520 ajustes de juego: tasas, fórmulas, permisos de comandos, eventos (8 archivos, todos cargados) |
| `Data/**/*.txt` | carpeta de salida de GameServer | monstruos, spawns, items, skills, tiendas, puertas, quests, eventos |

`GameServer.ini` es específico de este port (el original guarda esos datos dentro de un binario):

```ini
[GameServerInfo]
ServerName = SSeMU GameServer_0
ServerCode = 0
ServerPort = 55900
ServerVersion = 1.02.00
ServerSerial = PoweredSetecSoft
ServerMaxUserNumber = 300
JoinServerAddress = 127.0.0.1
JoinServerPort = 55970
DataServerAddress = 127.0.0.1
DataServerPort = 55960
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
```

## 9. Problemas comunes

| Síntoma | Causa probable / solución |
|---|---|
| El cliente se queda antes de la pantalla de selección de servidor | ConnectServer caído o puerto 44405 bloqueado. Mirá su consola. |
| El cliente se queda en la selección de personaje | GameServer inalcanzable, o DataServer caído (es quien entrega la lista de personajes). |
| "Cuenta llena" al crear una clase distinta de Dark Wizard | Falta la fila de la clase en `default_class_type`: volvé a aplicar `003_default_class_seed.sql`. |
| `Address already in use` al arrancar | Una corrida anterior sigue viva: `python SharpSSeMU/kill_ports.py`. |
| No se puede recompilar: *el archivo está siendo usado por otro proceso* | Detené los servidores primero; un `.exe`/`.dll` en ejecución está bloqueado. |
| AdminPanel muestra *GameServer sin conexión* | No se está escribiendo `Data/status.json`: GameServer apagado, o `GameServer:DataPath` apunta a otro lado. |
| La página del AdminPanel se ve sin estilos / rota | Levantalo con `dotnet run` desde `src/MuServer.AdminPanel`, no desde `bin/`. |
| Las ediciones de configuración no tienen efecto | Reiniciá el GameServer — lee sus archivos una sola vez al arrancar. |
| Falla la autenticación de Postgres | Las credenciales de `JoinServer.ini` / `DataServer.ini` no coinciden con tu rol. |
| Las ediciones de inventario/stats se revierten | El personaje estaba online; el autoguardado del GameServer las sobrescribió. |

Los logs de los servidores salen por cada consola y se escriben en una carpeta `LOG/` junto a cada binario.

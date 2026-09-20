> **Guía manual (histórica).** Explica paso a paso, a mano, lo que hoy automatiza `deploy_configs.py`.
> Para el camino actual ver [`../docs/es/GETTING_STARTED.md`](../docs/es/GETTING_STARTED.md).
>
> 🌐 [English](TESTING_WITH_REAL_CLIENT.md) · **Español**

# Cómo compilar y encender todo para probar con el cliente oficial

Esta guía es para correr **los 4 servidores en C#** (`SharpSSeMU/`) en tu propia PC Windows y
conectarte con el cliente real (`MuClient/main.exe`). Se usa Windows para todo (no Linux) porque
el cliente solo corre ahí — .NET 10 es multiplataforma, así que los servidores andan igual en
Windows para esta prueba local. Para producción después se pueden mover a un Linux.

Todo corre en `127.0.0.1` (tu misma PC), así que no hace falta abrir puertos ni configurar
firewall para esta prueba.

## 0. Buena noticia sobre el cliente

En `MuServer99B/Tools/GetMainInfo/MainInfo.ini` (una herramienta que lee esos datos desde el
propio `Main.dll` del paquete) figura:

```
IpAddress = 127.0.0.1
IpAddressPort = 44405
ClientVersion = 1.02.00
ClientSerial = <your client's serial>
```

Esto indica que el `Main.dll` que ya está en `MuClient/` **ya viene apuntando a `127.0.0.1:44405`**
— el mismo puerto que usa ConnectServer. No debería hacer falta parchear nada con MuMaker para
esta prueba local. Si al final el cliente no logra conectar, es la primera cosa a revisar (ver
sección de problemas comunes, al final).

Los valores `ClientVersion = 1.02.00` y `ClientSerial = <your client's serial>` son justamente los que
hay que poner en la configuración de GameServer para que la validación de versión/serial del login
coincida (ver paso 3).

## 1. Prerrequisitos

- **.NET 10 SDK** para Windows: https://dotnet.microsoft.com/download/dotnet/10.0 (el instalador
  normal, no hace falta nada especial).
- **PostgreSQL** para Windows. Cualquiera de estas dos opciones:
  - Instalador oficial: https://www.postgresql.org/download/windows/ (más simple si no tenés
    Docker).
  - Si ya tenés Docker Desktop: `docker run --name mu-postgres -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:14`

Verificá que `dotnet --version` responda `10.x` desde una consola (cmd o PowerShell) antes de
seguir.

## 2. Crear la base de datos

Con PostgreSQL corriendo en `127.0.0.1:5432`, desde `psql` (o pgAdmin, o `docker exec`):

```sql
CREATE DATABASE muonline;
CREATE ROLE muserver LOGIN PASSWORD 'muserver' SUPERUSER;
```

Después aplicá los 4 esquemas, en este orden, contra la base `muonline`:

```
SharpSSeMU/db/postgres/001_accounts.sql
SharpSSeMU/db/postgres/002_characters.sql
SharpSSeMU/db/postgres/003_default_class_seed.sql
SharpSSeMU/db/postgres/004_friends.sql
```

Con `psql`:

```bat
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\001_accounts.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\002_characters.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\003_default_class_seed.sql"
psql -U postgres -d muonline -f "SharpSSeMU\db\postgres\004_friends.sql"
```

Esto ya deja sembradas dos cuentas de prueba: **`test`/`test`** y **`admin`/`admin`** (ver
`001_accounts.sql` si querés cambiar la clave o agregar más).

## 3. Compilar los 4 servidores

Desde `SharpSSeMU/`, por cada proyecto (usa `cmd`/PowerShell):

```bat
cd src\MuServer.ConnectServer && dotnet build
cd ..\MuServer.JoinServer && dotnet build
cd ..\MuServer.DataServer && dotnet build
cd ..\MuServer.GameServer && dotnet build
```

Esto genera cada `.dll` en `bin\Debug\net10.0\` dentro de la carpeta de cada proyecto — ahí es
donde hay que copiar los archivos de configuración del paso siguiente (los servidores buscan su
`.ini` al lado de su propio `.dll`, no en la carpeta del proyecto).

## 4. Archivos de configuración

Copiá estos archivos a la carpeta `bin\Debug\net10.0\` de cada proyecto correspondiente. Podés
crearlos vos con el Bloc de notas — el contenido exacto es este (ya con los valores reales del
paquete, coherentes entre sí):

### `MuServer.ConnectServer\bin\Debug\net10.0\ConnectServer.ini`

```ini
[ConnectServerInfo]
ConnectServerPortTCP = 44405
ConnectServerPortUDP = 55557
ConnectServerMaxUserNumber = 500
MaxConnectionPerIP = 50
MaxPacketPerSecond = 0
MaxConnectionIdle = 60
```

`MaxConnectionPerIP` **no puede ser 0**: con 0 el ConnectServer acepta el socket del cliente y lo corta enseguida.

### `MuServer.ConnectServer\bin\Debug\net10.0\BlackList.txt`

```
0
end
```

### `MuServer.ConnectServer\bin\Debug\net10.0\ServerList.dat`

```
   0            "GameServer_0"   "127.0.0.1"        55900       1
end
```

(Si mirás `MuServer99B/ConnectServer/ServerList.dat`, el original apunta a una IP de LAN vieja del
desarrollador — por eso hay que usar esta versión con `127.0.0.1`, no copiar la original tal cual.)

### `MuServer.JoinServer\bin\Debug\net10.0\JoinServer.ini`

```ini
[JoinServerInfo]
JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver
JoinServerPort = 55970
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
CaseSensitive = 0
MD5Encryption = 0
```

### `MuServer.JoinServer\bin\Debug\net10.0\AllowableIpList.txt`

```
0
"127.0.0.1"
end
```

### `MuServer.DataServer\bin\Debug\net10.0\DataServer.ini`

```ini
[DataServerInfo]
DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver
DataServerPort = 55960
```

### `MuServer.DataServer\bin\Debug\net10.0\AllowableIpList.txt`

```
0
"127.0.0.1"
end
```

### `MuServer.DataServer\bin\Debug\net10.0\BadSyntax.txt`

```
"fuck"
"admin"
end
```

### `MuServer.GameServer\bin\Debug\net10.0\GameServer.ini`

```ini
[GameServerInfo]
ServerName = SSeMU GameServer_0
ServerCode = 0
ServerPort = 55900
ServerVersion = 1.02.00
ServerSerial = SharpSSeMU99B-v1
ServerEncDecKey1 = 0
ServerEncDecKey2 = 0
ServerMaxUserNumber = 300
JoinServerAddress = 127.0.0.1
JoinServerPort = 55970
DataServerAddress = 127.0.0.1
DataServerPort = 55960
ConnectServerAddress = 127.0.0.1
ConnectServerPort = 55557
```

`ServerVersion` y `ServerSerial` tienen que coincidir con los que usa tu cliente real (ver paso 0).

> **Sobre el serial.** SharpSSeMU-99B usa su propio serial por defecto, `SharpSSeMU99B-v1` (16 caracteres, en
> `ServerSerial`), y también lo usa nuestro cliente (`MuMain-099B`, `Serial` en `WSclient.cpp`). El serial del
> cliente **original** está dentro de sus propios archivos y este proyecto no puede cambiarlo: para usar el cliente
> original, poné en `ServerSerial` (`GameServer.ini`) el valor de `ClientSerial` que muestra la configuración de tu
> cliente. Cliente y servidor deben tener el mismo serial; además de él sale la clave del cifrado de flujo.

### `MuServer.GameServer\bin\Debug\net10.0\Hack\Enc2.dat` y `Hack\Dec1.dat`

Estos dos son binarios, no de texto — copialos tal cual desde
`MuServer99B\Data\Hack\Enc2.dat` y `MuServer99B\Data\Hack\Dec1.dat` a una subcarpeta `Hack\` nueva
dentro de `MuServer.GameServer\bin\Debug\net10.0\`.

### `MuServer.GameServer\bin\Debug\net10.0\Data\` — los datos del juego

Copiá la carpeta **completa** `MuServer99B\Data\` (monstruos, spawns, items, skills, tiendas, eventos,
`Move.txt` …) a una subcarpeta `Data\` junto a los binarios del GameServer, y copiá también los ocho
`MuServer99B\GameServer\DATA\GameServerInfo - *.dat` a esa misma `Data\`. Sin esto el GameServer no
tiene mundo que cargar.

## 5. Prender todo, en este orden

Cada uno en su propia ventana de consola (para ver los logs y poder pararlos por separado). El
orden importa poco entre sí salvo que Postgres tiene que estar arriba antes que JoinServer/
DataServer:

```bat
cd MuServer.ConnectServer\bin\Debug\net10.0 && dotnet MuServer.ConnectServer.dll
cd MuServer.JoinServer\bin\Debug\net10.0    && dotnet MuServer.JoinServer.dll
cd MuServer.DataServer\bin\Debug\net10.0    && dotnet MuServer.DataServer.dll
cd MuServer.GameServer\bin\Debug\net10.0    && dotnet MuServer.GameServer.dll
```

En la consola de ConnectServer deberías ver, a los pocos segundos:

```
[SocketUDP] JoinServer connected
[SocketUDP] GameServer connected [GameServer_0] [127.0.0.1:55900][0]
```

Si ves esas dos líneas, la cadena completa está viva y lista para el cliente (esto es exactamente
lo que confirmé con un test automatizado antes de escribir esta guía — ver
`SharpSSeMU/tests/full_chain_e2e_test.py`).

## 6. Lanzar el cliente

Ejecutá `MuClient\main.exe`. Debería:

1. Conectar a ConnectServer y mostrar "GameServer_0" en la lista de servidores.
2. Al elegirlo, conectar directo a GameServer (127.0.0.1:55900).
3. Mostrar la pantalla de login — usá `test` / `test` o `admin` / `admin`.

Que el login funcione ya confirma que las capas de cifrado y el protocolo están bien. Después
podés crear o elegir un personaje y entrar al mundo. Qué está y qué no está implementado dentro del
juego: ver [`../docs/es/STATUS.md`](../docs/es/STATUS.md). Para usar el **cliente portado**, ver
[`../docs/es/BUILDING.md`](../docs/es/BUILDING.md#cliente).

## Problemas comunes

- **El cliente no conecta ni siquiera al principio**: la hipótesis del paso 0 sobre que
  `Main.dll` ya apunta a `127.0.0.1:44405` no se pudo verificar sin ejecutar el binario. Si no conecta, usá `MuServer99B/Tools/MuMaker/MuMaker.exe` para parchear la IP en una
  copia de `Main.dll` (esa herramienta genera un cliente distribuible con la IP grabada).
- **"database does not exist" o error de conexión en JoinServer/DataServer**: revisá que
  Postgres esté escuchando en el puerto que pusiste en el `.ini` (5432 por defecto) y que el rol
  `muserver` exista.
- **GameServer no aparece en la lista del cliente**: fijate en la consola de ConnectServer si dice
  `[SocketUDP] GameServer connected` — si no aparece en ~5 segundos, revisá que
  `GameServer.ini` tenga `ConnectServerPort = 55557` (puerto UDP, no el 44405 que es TCP) y que
  ConnectServer esté arriba antes de que arranque GameServer.
- **Login rechazado con "versión incorrecta"**: revisá que `ServerVersion = 1.02.00` esté
  exactamente así en `GameServer.ini` (con los puntos).
- **Puertos ocupados**: si ya tenés algo corriendo en 44405/55557/55970/55960/55900, paralo o
  cambiá los puertos de forma consistente en todos los `.ini` involucrados.

## Resumen de puertos

| Servidor | Puerto | Protocolo | Quién se conecta |
|---|---|---|---|
| ConnectServer | 44405 | TCP | Cliente real |
| ConnectServer | 55557 | UDP | Heartbeats de JoinServer/GameServer |
| JoinServer | 55970 | TCP | GameServer |
| DataServer | 55960 | TCP | GameServer |
| GameServer | 55900 | TCP | Cliente real (tras elegir servidor) |
| PostgreSQL | 5432 | TCP | JoinServer, DataServer |

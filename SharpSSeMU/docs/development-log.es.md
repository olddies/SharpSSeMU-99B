🌐 [English](development-log.md) · **Español**

> **Registro de desarrollo (en español).** Este documento es el diario cronológico del servidor:
> qué se portó en cada fase y los bugs reales que se encontraron probando. Es histórico y muy detallado;
> algunas secciones describen un estado ya superado. Para la guía actual (inglés) ver
> [`../README.md`](../README.md) y [`../../docs/STATUS.md`](../../docs/STATUS.md).

# SharpSSeMU — Puerto a C#/.NET del servidor SSeMU 0.99B (2.1.7)

Reescritura del servidor privado de MU Online 0.99B a C#/.NET 10, pensada para correr en Linux,
manteniendo el **cliente original sin modificar** (`main.exe` + `Main.dll`). El cliente no se toca
para nada: se conserva el mismo protocolo binario byte a byte para que se conecte sin cambios.

## Por qué el cliente no se reescribe

El motor 3D/DirectX del cliente vive dentro de `main.exe`, cuyo código fuente no forma parte de
este paquete (es un binario cerrado). `Main.dll` tampoco es un simple shim de red: usa Detours para
hookear funciones de `main.exe` por **direcciones de memoria hardcodeadas**. No hay nada ahí que se
pueda "convertir a C#" de forma útil — por eso el alcance de este proyecto es exclusivamente el
**servidor**, replicando el protocolo exacto para que el cliente compilado actual siga funcionando
tal cual. Es el mismo enfoque que usa OpenMU, el proyecto de referencia en C# para MU Online.

## Estado actual

| Módulo | Estado | Líneas del original (C++) |
|---|---|---|
| ConnectServer | ✅ Portado y probado end-to-end | ~4 600 |
| JoinServer | ✅ Portado y probado end-to-end (contra PostgreSQL real) | ~7 000 |
| DataServer (persistencia de personajes) | ✅ Núcleo portado y probado end-to-end (contra PostgreSQL real) | ~9 300 |
| GameServer | 🔶 Fases 1-7 listas y probadas end-to-end: **conexión/login** + **entrar al mundo** + **items e inventario** (primera pasada: formato de item, lista de inventario, mover/equipar) + **monstruos y combate** (primera pasada: monstruos estáticos, ataque cuerpo a cuerpo, muerte/experiencia/nivel/respawn) + **balance real de items/combate** (`CharacterCalcAttribute` + `Item.txt` real) + **chat/party/amigos** (primera pasada: chat público y de grupo, whisper local y cruzado entre GameServers, party con reparto de experiencia por nivel, lista de amigos con estado online/offline) + **Devil Square** (primera pasada de eventos especiales: motor de estados completo, entrada por ticket, spawn de monstruos por etapa, puntaje, recompensa de experiencia/zen, ranking en DataServer) + **skills y maná** (primera pasada: casteo de skills de ataque a un solo objetivo, daño mágico real, regeneración periódica de maná/BP). Falta Blood Castle/Chaos Castle/Kalima/Illusion Temple (mismo motor, distinto set de datos — ver "Próximos pasos"), Lua (confirmado sin uso real en este paquete), Guild, PartyMatching, correo entre amigos, el resto de items (recoger del suelo, trade, tiendas), IA de monstruo, PvP, skills de área/duración/combo/teletransporte y el sistema de aprendizaje de skills | ~81 500 |
| AdminPanel (web) | 🔶 Agregado nuevo, sin equivalente en el original: edita los archivos reales de `Data/` (tasas de exp/drop/zen, teletransportes, items, precios, monstruos, spawns, tiendas, skills, puertas, quests, mensajes, eventos, Chaos Machine y los 8 `GameServerInfo - *.dat` completos) y muestra estado en vivo/manda mensajes globales al GameServer corriendo. Con login por contraseña; sin recarga de configuración en caliente — ver "AdminPanel" más abajo | — |

### Bugs reales encontrados probando con el cliente real (main.exe)

Los tests automatizados (WorldTestClient/TestClient) simulan un cliente que ya "sabe" varias cosas
de antemano, así que no ejercitaban dos pasos que el cliente real sí hace siempre. Ambos ya están
arreglados:

- **ConnectServer nunca mandaba la lista de nombres de servidor (C2:F3:EA)** al conectar --
  `BuildNameListPacket()` existía pero nadie lo llamaba. El cliente real se queda esperando ese
  paquete antes de mostrar la pantalla de selección de servidor; sin él, se desconecta solo. Arreglado
  en `MuServer.ConnectServer/Net/TcpGateServer.cs`.
- **GameServer nunca respondía la lista de personajes (C1:F3:00)** al terminar el login -- el cliente
  real la pide automáticamente antes de poder elegir personaje (0xF3:0x03), y se queda pegado en la
  pantalla de selección para siempre si no llega. `DataServerProtocolHandler` (DataServer) ya tenía
  todo el lado de la consulta a Postgres implementado; faltaba que GameServer pidiera la lista y
  convirtiera la respuesta compacta de DataServer a los `CharSet[13]` que espera el cliente
  (`ClientProtocolHandler.OnCharacterListRequestAsync`/`OnCharacterListFromDataServerAsync`,
  reusando `PlayerObject.BuildCharSet`). También se agregaron manejadores mínimos para 0xF3:0x09
  (HardwareId, valida formato de GUID) y 0x0E (keep-alive del cliente, no-op) para que dejen de
  loguearse como "no implementado".
- **GameServer nunca manejaba la creación de personaje (C1:F3:01)** -- con una cuenta sin
  personajes, el cliente real muestra la pantalla de crear personaje y, al confirmar nombre/clase,
  manda `F3:01`; sin respuesta se queda pegado ahí para siempre (mismo síntoma que el bug de la
  lista de personajes, pero un paso más adelante en el flujo). El lado DataServer (`0x02`,
  `OnCharacterCreateAsync`) ya estaba completo desde antes -- solo lo usaban los scripts de prueba
  conectándose directo a DataServer para sembrar datos, nunca el propio GameServer. Se agregó el
  adaptador delgado que faltaba: `ClientProtocolHandler.OnCharacterCreateRequestAsync` (reenvía a
  DataServer) / `OnCharacterCreateResultFromDataServerAsync` (aplica la misma conversión de bytes de
  `Class` que hacía `DGCharacterCreateRecv` y manda `PMSG_CHARACTER_CREATE_SEND`). La validación
  previa de clase/`CARD_CODE` de `CGCharacterCreateRecv` (desbloqueo de MG/DL/SU/RF por nivel de
  cuenta) no se replicó -- se reenvía directo y DataServer, que ya es la autoridad real sobre qué
  clases existen (`default_class_type`), rechaza cualquier clase no habilitada con `result=2`, así
  que el efecto visible es el mismo sin duplicar lógica. Probado de punta a punta con una cuenta
  nueva real (0 personajes → crear → lista se actualiza → seleccionar → entra al mundo) en
  `tests/gameserver_fase5_e2e_test.py`.
- **GameServer se caía con `ArgumentOutOfRangeException` al procesar movimiento real (C1:D7)** --
  `PMSG_MOVE_RECV` declara `BYTE path[8]` como campo fijo en el struct C++ original
  (`Protocol.h:253-259`), pero el cliente real no manda siempre los 8 bytes: sólo manda los que
  necesita según la cantidad de pasos codificada en los nibbles de `path[0]`; leer de más en C++ es
  "seguro" (memoria de buffer adyacente, basura pero sin excepción) pero nuestro framer de C# arma
  un `byte[]` del tamaño exacto recibido en el wire, así que `p.AsSpan(5, 8)` explotaba con paquetes
  reales más cortos de 13 bytes. Los tests automatizados nunca lo detectaron porque
  `FakeMuClient.SendMoveAsync` siempre manda el path completo de 8 bytes. Arreglado en
  `MoveRecv.Parse` (`MuServer.GameServer/Protocol/WorldPackets.cs`) recortando la lectura al tamaño
  real disponible (`Math.Clamp(p.Length - 5, 0, 8)`) y rellenando el resto con ceros. Se agregó
  `FakeMuClient.SendMoveShortPathAsync` (manda sólo 1 byte de path) más un paso de test dedicado en
  `WorldTestClient/Program.cs` para que este caso quede cubierto de forma permanente.
- **GameServer se caía con `IndexOutOfRangeException` al procesar acción/pose real (C1:18)** --
  mismo patrón que el bug de movimiento de arriba: `PMSG_ACTION_RECV` declara un campo final opcional
  `index[2]` en el struct C++ (`Protocol.h:196-202`) que el cliente real a veces no manda.
  `ActionRecv.Parse` asumía el largo fijo y explotaba con `p[5]`/`p[6]` fuera de rango. Arreglado
  leyendo byte a byte defensivamente (`MuServer.GameServer/Protocol/WorldPackets.cs`), igual que
  `MoveRecv.Parse`. Reportado por el usuario con logs de producción (6 excepciones repetidas en menos
  de 15 segundos jugando con `main.exe`).
- **Solo se veía el primer personaje de la cuenta en la pantalla de selección** (reportado por el
  usuario: "solo veo el primero que he creado" con 4 personajes creados) -- causa raíz confirmada
  leyendo el struct real: este build (`GAMESERVER_UPDATE=803`) usa `BYTE CharSet[18]` en todos lados
  (`User.h:778`, `Protocol.h:767`, `Viewport.h`, `Friend.h`, `ItemManager.h`), no el `CharSet[13]`
  que este puerto asumía desde fases anteriores (un tamaño de build más viejo). Además,
  `PMSG_CHARACTER_LIST_SEND` (`Protocol.h:750-759`) tiene un byte `ExtWarehouse` en el encabezado
  (`GAMESERVER_UPDATE>=602`) que faltaba, y cada renglón `PMSG_CHARACTER_LIST` (`Protocol.h:761-769`)
  manda `GuildStatus` al final (faltaba) y, al no tener `#pragma pack(1)`, el compilador real inserta
  1 byte de relleno para alinear el `WORD Level` (que también faltaba). Con todo esto junto, el
  primer personaje (slot 0) se leía "por suerte" con los campos corridos pero coincidiendo en el
  lugar justo para el nombre; el segundo personaje en adelante quedaba totalmente desalineado y el
  cliente lo descartaba. Arreglado en `PlayerObject.CharSet`/`RebuildCharSet`/`BuildCharSet` (18
  bytes, más las ramas de alas `GAMESERVER_UPDATE>=601` y las variantes extendidas de mascota
  `>=201`/`>=401`/`>=601`, re-derivadas byte a byte de `ObjectManager.cpp:1753-2038` y
  `DSProtocol.cpp:755-1075`) y en `ClientPackets.CharacterListSend` (byte `ExtWarehouse`, relleno de
  alineación, `CharSet[18]`, `GuildStatus`). De paso, al releer el struct completo de
  `PMSG_VIEWPORT_PLAYER` (`Viewport.h:45-70`) para corregir su `CharSet` también se encontró que le
  faltaban por completo los campos `attribute`/`MuunItem[2]`/`level[2]`/`MaxHP[4]`/`CurHP[4]`
  (agregados en `GAMESERVER_UPDATE>=701`/`>=803`) y que el contador de efectos (`count`) estaba en la
  posición vieja (2 bytes, antes del nombre) en vez de al final (1 byte, después de `CurHP`) --
  arreglado en `WorldPacketBuilder.ViewportPlayerAppear`, incluyendo el empaquetado no estándar de
  `MaxHP`/`CurHP` (`SET_NUMBERHB(SET_NUMBERHW(x))`, orden de bytes `[b31-24, b15-8, b23-16, b7-0]`,
  no big-endian estándar). `attribute` y `MuunItem` se mandan siempre en 0/vacío porque el sistema de
  atributos elementales y el de Muun no están portados. Cubierto con un test dedicado: la cuenta
  `test` de `tests/gameserver_shop_e2e_test.py` ahora siembra un segundo personaje (`Hero1Two`) y
  `WorldTestClient` verifica explícitamente que se lee en el slot y offset correctos (regresión
  directa del bug reportado, no solo "el primero sigue andando").

### ConnectServer: qué quedó implementado

- Framing de paquetes C1/C2 idéntico al original (`MuServer.Shared/Protocol/PacketFramer.cs`).
- Parser `MemScript` (mismo tokenizer de texto plano que usan **todos** los módulos del servidor
  original para sus archivos de `Data/`) — queda listo para reusarse en JoinServer/DataServer/GameServer.
- `BlackList.txt` y `ServerList.dat` se leen tal cual (mismo formato, mismas rutas relativas).
- `ConnectServer.ini` se sigue usando sin cambios (mismas claves: `ConnectServerPortTCP`,
  `ConnectServerPortUDP`, `MaxConnectionPerIP`, `MaxPacketPerSecond`, `MaxConnectionIdle`).
- Heartbeats UDP 0xA1 (GameServer) / 0xA2 (JoinServer) para saber qué servidores están vivos.
- Protocolo de cliente: `0xF4:0x02` (pedir lista de servidores), `0xF4:0x03` (pedir IP:puerto de un
  servidor), envío automático de `0xC1:00` (init) y `0xF3:0xEA` (lista de nombres) al conectar.
- Límite de conexiones por IP, límite de paquetes por segundo, límite de duración de sesión — con
  la misma semántica (y la misma rareza) que el original: `MaxConnectionIdle` se mide desde que se
  conecta el cliente, no desde la última actividad (así se comportaba el C++ original: nunca
  actualizaba el "online time"). Documentado en el código donde corresponde.
- Comandos por consola equivalentes a los del menú de Windows: `reload blacklist`,
  `reload serverlist`, `reload config`, `exit`.

Probado con un cliente TCP + UDP simulado: conecta, recibe init + lista, ve aparecer un GameServer
tras su heartbeat con el porcentaje de usuarios correcto, y resuelve IP:puerto de un servidor —
byte a byte igual al formato que espera el cliente real.

### JoinServer: qué quedó implementado

- Protocolo GameServer↔JoinServer completo: `0x00` (info de servidor), `0x01` (login de cuenta),
  `0x02` (logout), `0x05` (consulta de nivel VIP), `0x11` (guardar nivel VIP), `0x20` (usuarios
  online), `0x30` (kick externo) — mismos structs/tamaños que `JoinServerProtocol.h`.
  Todos los paquetes usan `PBMSG_HEAD` (C1, sin sub-código), a diferencia de ConnectServer.
- `AllowableIpList.txt` (whitelist de IPs de GameServer) con el mismo formato "0 ... end".
  No hay rate-limit/idle-timeout acá porque el original tampoco lo tenía (son pocas conexiones
  de confianza, no clientes públicos).
- Sesiones de cuenta en memoria (equivalente a `CAccountManager`): evita doble-login, expira
  "movimientos entre servidores" colgados a los 30s (igual que `DisconnectProc`), y limpia todo
  si el GameServer dueño se cae (`ClearServerAccountInfo`).
- Heartbeat UDP cliente `0xA2` hacia ConnectServer cada 1s.
- **Capa de datos migrada de SQL Server/ODBC a PostgreSQL** (`Npgsql`, consultas parametrizadas —
  el original armaba el SQL con `sprintf`, vulnerable a inyección; acá se corrigió sin cambiar el
  comportamiento). Se tradujeron los 4 stored procedures que JoinServer usaba directamente:
  `WZ_CONNECT_MEMB`, `WZ_DISCONNECT_MEMB`, `WZ_GetAccountLevel` (expira el nivel VIP si venció) y
  `WZ_SetAccountLevel` (mismo nivel = suma segundos; nivel distinto = reinicia vencimiento) — ver
  `Db/NpgsqlAccountRepository.cs`. El esquema de las 2 tablas que usa (`memb_info`, `memb_stat`)
  está en `db/postgres/001_accounts.sql`, con el mapeo de nombres documentado en el propio archivo.
- **Pendiente/no portado todavía**: el modo `MD5Encryption=1` del `.ini` (esquema propio de
  MU con tabla de sustitución `MD5_KEYVAL`, no MD5 estándar) — de momento solo funciona
  `MD5Encryption=0` (contraseña en texto plano, que es el default de fábrica del `.ini` original).

Probado de punta a punta contra un PostgreSQL real (arrancado desde cero, esquema aplicado,
cuentas `test`/`admin` semilla): login, rechazo de doble-login, cuenta inexistente, subir nivel
VIP y verificar que persiste en la base tras logout/login, logout, y el heartbeat UDP — todo
byte a byte contra el formato esperado por GameServer.

### DataServer: qué quedó implementado

- Protocolo DataServer↔GameServer completo (todos los códigos en alcance): `0x00` info de
  servidor, `0x01` lista de personajes, `0x02` crear personaje, `0x03` borrar personaje, `0x04`
  info de personaje, `0x05` crear item, `0x06`/`0x1A` datos de opciones (alas/piel/etc.), `0x07`
  info de pets, `0x08`/`0x09` correo/aviso global, `0x0B` contador de muertes de monstruo, `0x10`
  guardar personaje completo (el struct más grande, ~30 campos), `0x11` guardar inventario, `0x12`
  guardar opciones, `0x13` guardar pets, `0x14`/`0x15` guardar reset/master-reset, `0x16`-`0x19`
  guardar rankings (Blood/Chaos/Devil/Illusion), `0x1B` tarjeta de creación, `0x20`/`0x21`
  conectar/desconectar personaje, `0x30` whisper global — mismos structs/tamaños que
  `DataServerProtocol.h`.
- Compactación de inventario para la pantalla de selección de personaje: el original reduce cada
  slot de equipo de 16 a 5 bytes (`CompactInventory`) — portado byte a byte, incluida la detección
  de slot vacío (`0xFF`/máscaras de bits específicas).
- `BadSyntax.txt` (nombres de cuenta/personaje prohibidos) con el mismo formato "..." que el
  original.
- Reset/master-reset/contador de eventos con la misma lógica de "reinicio" diario/semanal/mensual
  que el original resolvía con `DATEDIFF` de SQL Server — acá se aproxima con comparación de
  calendario (`ISOWeek` para semana), documentado en el código como aproximación intencional (el
  comportamiento exacto en bordes de zona horaria puede diferir un poco del original; no afecta
  a los ~99% de los casos).
- **Capa de datos migrada de SQL Server/ODBC a PostgreSQL** (`Npgsql`, consultas parametrizadas).
  13 tablas en `db/postgres/002_characters.sql` + `003_default_class_seed.sql`: `account_character`,
  `default_class_type`, `character`, `option_data`, `reset_data`, `event_entry_count`,
  `ranking_duel`, `ranking_blood_castle`, `ranking_chaos_castle`, `ranking_devil_square`,
  `ranking_illusion_temple` (tabla nueva, el original no rankeaba Illusion Temple),
  `monster_kill_count` (tabla nueva, antes vivía solo en memoria), `pet_item_info`.
- **Bug real encontrado y corregido en el paquete original**: `DataServerProtocol.h` (el header
  que usa DataServer) le faltan 17 bytes (`IsNewChar`, `Married`, `MarryName[11]`) que
  `DSProtocol.h` (el header que usa GameServer para el mismo paquete) sí espera — versión
  desincronizada, probablemente resto de una feature de matrimonio a medio implementar. Al
  controlar ambos lados del protocolo se corrigió agregando esos 3 campos (con default 0/0/"") en
  el builder de `CharacterInfoSend`, documentado en el código.
- **Pendiente/no portado todavía**: warehouse (baúl), gremios, `CustomPick`, y el resto de
  paquetes de DataServer fuera del alcance de personajes/inventario/rankings — se agregan cuando
  las fases correspondientes de GameServer los necesiten.

Probado de punta a punta contra PostgreSQL real: listar personajes (vacío, con Hero, tras borrar),
crear personaje (éxito y nombre duplicado), cargar info de personaje, guardar y releer info de
personaje (verifica persistencia real), acumular ranking de Blood Castle, contar muertes de
monstruo x2, borrar personaje. 12/12 casos OK.

### GameServer — Fase 1: núcleo de conexión y login

De las 6 fases planeadas para portar el núcleo de juego (79k líneas), la Fase 1 cubre el socket
del cliente real y el login. Es la parte más delicada de todo el proyecto porque, a diferencia de
ConnectServer/JoinServer/DataServer (protocolo plano C1/C2 entre procesos de confianza), el socket
del cliente real usa **4 capas de ofuscación/cifrado superpuestas**:

1. **Cifrado de flujo de todo el socket** (`GameStreamCipher`, puerto de `HackCheck.cpp`): XOR +
   resta byte a byte sobre absolutamente todo lo que entra/sale, con claves de 1 byte derivadas al
   arrancar a partir de un "CustomerName" fijo (`"SSE"`) XOReado contra el `ServerSerial` del
   `.ini`.
2. **Cifrado por bloques** (`PacketCipher`, puerto de `PacketManager.cpp`), solo para paquetes
   marcados C3/C4 (ej. el login): convierte bloques de 8 bytes en bloques de 11 usando tablas
   `Modulus/Key/Xor` cargadas desde los archivos binarios originales (`Hack/Enc2.dat`/`Dec1.dat`
   del lado servidor, `Enc1.dat`/`Dec2.dat` del lado cliente) — **no se inventó ninguna clave**,
   se leen los archivos reales del paquete.
3. **Des-ofuscación XOR encadenada** (`XorData`, dentro de `PacketCipher`), solo en recepción:
   `buff[n] ^= buff[n-1]^Filtro[n%32]`. El original nunca la aplica al enviar (confirmado leyendo
   `SocketManager.cpp`) — el cliente real (binario cerrado) tiene que traer la contraparte "de
   ida", que se dedujo despejando la recurrencia y se usa solo en el arnés de pruebas.
4. **XOR de argumentos** (3 bytes, `{0xFC,0xCF,0xAB}`) aplicado específicamente a los campos
   cuenta/clave dentro del paquete de login, encima de todo lo anterior.

Las 4 capas se validaron con material real: primero con un round-trip de `PacketCipher` usando
los 4 archivos `.dat` reales del paquete (cliente↔servidor en ambos sentidos), y después con un
cliente de prueba en C# (`src/TestClient/`) que arma un login **exactamente como lo haría el
cliente real** — mismas claves, mismo cifrado por bloques, misma ofuscación — porque no hay forma
de tener el `.exe` real corriendo en este entorno.

**Qué quedó implementado:**

- `GameStreamCipher`, `PacketCipher` y `GameClientFramer` en `MuServer.Shared/Crypto/` — el
  framer completo que replica `CSocketManager::DataRecv` + `CPacketManager::ExtractPacket`: separa
  paquetes C1/C2/C3/C4, descifra por bloques cuando corresponde, y "sintetiza" el paquete lógico
  final tal como lo ve el resto del protocolo.
- Handshake de login: el servidor manda `GCConnectClientSend` (C1:F1:00, índice asignado +
  versión/código de servidor) sin cifrado por bloques; el cliente responde `PMSG_CONNECT_ACCOUNT`
  (C3:F1:01) con cuenta/clave; el servidor valida versión y serial de cliente contra el `.ini`
  (rechaza con resultado 6 si no coinciden), reenvía la cuenta/clave a JoinServer (reusando el
  protocolo ya probado) y devuelve el resultado real al cliente.
- Notificación de desconexión a JoinServer (`GJDisconnectAccountSend`) — sin esto una cuenta
  quedaba "fantasma" como conectada en JoinServer tras cerrar el socket, y el siguiente intento de
  login del mismo cliente fallaba con "ya conectada" en vez de validar credenciales de verdad. Se
  encontró y corrigió gracias al test end-to-end con el cliente de prueba real.
- Conexiones salientes a JoinServer y DataServer (reconexión automática si se caen), y el
  contenedor `GameServerConfig` (subconjunto de `ServerInfo.h` necesario para esta fase — el
  original tiene ~500 campos de balance de juego que se agregan recién cuando la fase que los usa
  los necesita).
- **Pendiente/fuera de alcance de la Fase 1**: todo lo que pasa después del login — entrar al
  mundo, mapas, movimiento, personajes, items, combate, chat, gremios, comandos de GM, eventos
  especiales y el motor Lua. Cualquier paquete con head distinto de `0xF1` se loguea como "no
  implementado" y se ignora, no rompe la conexión.

Probado de punta a punta con JoinServer + DataServer + PostgreSQL reales corriendo, y el cliente
de prueba armando paquetes auténticos con las claves reales del paquete: login válido (resultado
1), contraseña inválida (resultado 0), cuenta inexistente (resultado 2), y re-login de la misma
cuenta tras una desconexión limpia (confirma que la notificación de desconexión funciona y no dejó
sesión fantasma). 5/5 casos OK. El script de prueba queda en
`tests/gameserver_fase1_e2e_test.py` para volver a correrlo cuando se quiera.

**Segunda ronda de pruebas, con ConnectServer sumado a la cadena** (para validar el flujo completo
que usa el cliente real: ConnectServer → lista de servidores → resolución de IP → GameServer),
usando los valores reales del paquete (`ServerVersion=1.02.00`, `ServerSerial = the stock package's serial`,
sacados de `MuServer99B/GameServer/DATA/GameServerInfo - Common.dat` y de
`MuServer99B/Tools/GetMainInfo/MainInfo.ini`) encontró y corrigió dos bugs más:

- **`ServerVersion` mal parseado**: el original arma los 5 bytes de versión tomando índices
  específicos `{0,2,3,5,6}` de un string con puntos como `"1.02.00"` (para descartar los puntos,
  ver `ServerInfo.cpp` líneas 396-408) — el puerto inicial copiaba los primeros 5 bytes crudos del
  string, lo que con el valor real del paquete daba un `ClientVersion` incorrecto que el cliente
  real habría rechazado. Corregido en `GameServerConfig.cs`.
- **Faltaba el heartbeat UDP `0xA1`** de GameServer hacia ConnectServer (`GameServerLiveProc` del
  original) — sin él, ConnectServer nunca muestra el servidor en la lista ni puede resolverle la
  IP:puerto al cliente, aunque el login en sí funcionara probando directo contra GameServer.
  Agregado `Net/ConnectServerHeartbeatClient.cs` (mismo patrón que el de JoinServer).

Con ambos arreglos, probado de punta a punta con los 4 servidores + PostgreSQL reales: el
heartbeat llega, ConnectServer muestra el servidor y resuelve `127.0.0.1:55900` correctamente, y
el login autenticado (con las claves de cifrado reales del cliente) devuelve resultado 1. Script
en `tests/full_chain_e2e_test.py`. Ver `COMO_PROBAR_CON_CLIENTE_REAL.md` en la raíz del proyecto
para instrucciones de cómo levantar todo y probar con `MuClient/main.exe`.

### GameServer — Fase 2: entrar al mundo

Con el login ya funcionando (Fase 1), la Fase 2 cubre todo lo necesario para que el cliente
seleccione personaje, aparezca en el mapa, vea a otros jugadores y se mueva — el puerto de
`User`/`ObjectManager`/`Map`/`Viewport`/`Move` de `Protocol.cpp`.

**Qué quedó implementado:**

- `GameMap`/`MapRegistry` (`World/`): puerto de `CMap` — carga `Terrain<N>.att` (cabecera de 3
  bytes + grilla `width*height`, indexada `[y*height+x]` igual que el original), atributos de
  bloqueo (bits `4`/`8`, confirmado byte a byte contra `Protocol.cpp:1023` y
  `ObjectManager.cpp:2621`) y el bit dinámico "ocupado" (`SetStandAttr`/`DelStandAttr`, bit `2`).
- `PlayerObject`/`PlayerRegistry`: reemplaza el god-struct `OBJECTSTRUCT` original (`gObj[10000]`)
  por una clase idiomática + diccionario — las estructuras internas del servidor no necesitan ser
  byte-exactas, solo el protocolo de red.
- `ViewportTicker`: tick periódico (200ms) que hace aparecer/desaparecer jugadores cercanos según
  rango de vista (12 tiles), más difusión inmediata en cada movimiento — equivalente en
  comportamiento a `gObjViewportProc` + los `VpPlayer[]`/`VpPlayer2[]` del original, simplificado a
  un solo `HashSet<int> VisibleTo` por jugador.
- Selección de personaje (`0xF3:0x03`): reenvía a DataServer (`0x04`), arma `PlayerObject` con
  todos los campos devueltos, calcula el `CharSet[13]` de apariencia (byte 0 con la fórmula exacta
  `ChangeUp*16 - byte0/32 + Class*32`; el resto de bytes de equipo se difiere a la Fase 3, cuando
  se porte el sistema de items), y contesta `CHARACTER_INFO_SEND` (C3) + `NEW_CHARACTER_INFO_SEND`.
- Movimiento (`0xD7`): decodifica el path de 8 bytes empaquetado en nibbles usando la
  `RoadPathTable` de 8 direcciones (`Util.cpp`), valida contra una caja de ±15 tiles y contra
  tiles bloqueados del mapa, y si es válido difunde `MOVE_SEND` a los observadores actuales
  (`VisibleTo`) — puerto de `CGMoveRecv` (`Protocol.cpp:901-1076`). El anti-speedhack por contador
  de tiempo no se porta: viene apagado por defecto en el `.ini` original (`CheckMoveHack=0`), así
  que omitirlo no cambia el comportamiento de fábrica.
- Envío de paquetes C3 cifrados desde el servidor (`ClientSession.SendEncryptedAsync`) — hasta
  ahora el servidor solo recibía C3 (login), nunca los mandaba. Se confirmó leyendo
  `SocketManager.cpp::DataSend` que el original **no** aplica la des-ofuscación XorData al enviar
  (asimetría intencional, ver más abajo).
- **Hallazgo real de protocolo**: la des-ofuscación XorData del servidor se aplica a **todo**
  paquete entrante, no solo a los C3/C4 (login) — incluye los C1/C2 planos como selección de
  personaje y movimiento. La Fase 1 nunca lo necesitó porque el único paquete que manda el cliente
  ahí es el login. Se descubrió con un test real que mandaba `0xF3:0x03` sin esa capa y el servidor
  recibía la cabecera corrupta; se corrigió agregando la ofuscación "de ida" (`PacketCipher.ObfuscateInPlace`,
  la contraparte matemática de `DeobfuscateInPlace`) al arnés de pruebas, ya que el cliente real
  (binario cerrado) tiene que traerla de fábrica.
- **Pendiente/fuera de alcance de la Fase 2**: render completo del `CharSet` (slots de arma/armadura
  — depende del sistema de items de la Fase 3), NPCs/monstruos en el viewport, teletransporte/
  portales, y el anti-speedhack con contador de tiempo (apagado por defecto igual que el original).

Probado de punta a punta con JoinServer + DataServer + PostgreSQL reales y **dos** clientes de
prueba autenticados simultáneos (`src/WorldTestClient/`): ambos loguean, seleccionan personaje,
entran al mundo (reciben y descifran `CHARACTER_INFO_SEND` correctamente), se ven aparecer
mutuamente por el viewport (`0x12`), y el movimiento de uno se propaga en tiempo real al otro
(recibe su propio eco y el otro cliente recibe la difusión) — 4/4 casos OK. Script en
`tests/gameserver_fase2_e2e_test.py`.

Durante la depuración de este test se encontró (y quedó documentado como aprendizaje, no como bug
del servidor) que las coordenadas de spawn elegidas para la prueba inicialmente caían en tiles
realmente bloqueados del `Terrain1.att` real (`attr & 0x04` seteado) — el servidor rechazaba el
movimiento correctamente mandando `POSITION_SEND` (`0xD0`) de corrección en vez de `MOVE_SEND`
(`0xD7`), que es exactamente el comportamiento esperado; el ajuste fue elegir coordenadas de
prueba realmente transitables, no un cambio en el servidor.

### GameServer — Fase 3: items e inventario (primera pasada)

Con el personaje ya en el mundo (Fase 2), la Fase 3 arranca el sistema de items — el puerto de
`Item`/`ItemManager` de `Item.h`/`ItemManager.cpp`. Esta primera pasada cubre el núcleo necesario
para tener equipo real (no solo el blob opaco que ya guardaba/reenviaba DataServer desde la Fase
1): el formato de item que viaja por la red, la lista de inventario al entrar al mundo, mover/
equipar/desequipar dentro del inventario propio, y que el equipo puesto se vea reflejado en el
`CharSet` (propio y de los demás jugadores por viewport).

**Qué quedó implementado:**

- `Item` (`World/Item.cs`): puerto de los tres formatos de bytes distintos que usa el original para
  un mismo item (confirmados byte a byte contra `ItemManager.cpp:1593-1690`): 5 bytes para el
  wire cliente-servidor (`ToWireBytes`/`ItemByteConvert`), 16 bytes para persistencia en DataServer
  (`ToDbBytes`/`DBItemByteConvert`, de los cuales solo 10 tienen contenido real) y su inversa de
  lectura (`FromDbBytes`/`ConvertItemByte`). El identificador de item (`Index`) es un solo WORD
  empaquetado como `sección*32 + subíndice` (`GET_ITEM`), igual que el original.
- `PlayerObject.Items[108]`: el inventario decodificado del blob crudo que ya mandaba DataServer
  (`DecodeInventory`) -- el blob y el array decodificado se mantienen sincronizados vía `SetItem`
  para que un guardado futuro a DataServer no tenga que re-serializar los 108 slots enteros.
- `RebuildCharSet` (Fase 2) ahora usa equipo real: puerto completo de
  `CharacterMakePreviewCharSet` (`ObjectManager.cpp:1139-1268`) -- arma/casco/armadura/pantalón/
  guantes/botas/alas/mascota puestos se reflejan en los 13 bytes de apariencia con la misma
  fórmula exacta de bits del original (incluidos los brillos de excelente/set). El bit de
  "set completo" queda pendiente de la Fase 4 (depende de recalcular atributos).
- Paquetes (`Protocol/WorldPackets.cs`, clase `ItemPacketBuilder`): `ItemListSend` (`C4:F3:10`,
  lista completa al entrar al mundo -- primer paquete de esta fase que necesita tamaño de 2 bytes,
  ver más abajo), `ItemMoveRecv`/`ItemMoveSend` (`C1:24`/`C3:24`, mover/equipar/desequipar dentro
  del inventario), `ItemChangeSend` (`C1:25`) y `ItemEquipmentSend` (`C1:F3:13`, CharSet actualizado
  para el propio cliente).
- `ClientSession.SendEncryptedC4Async`: variante de `SendEncryptedAsync` con cabecera de tamaño de
  2 bytes (C4 en vez de C3) -- hacía falta porque una lista de inventario completa puede superar
  los 255 bytes que entran en el tamaño de 1 byte de C3. Mismo contrato (cifrado por bloques, sin
  XorData de salida).
- `OnItemMoveAsync`: mover un item de un slot a otro dentro del inventario propio (intercambia si
  el destino está ocupado). Si el slot origen o destino cae en el rango de equipo (0-11), rearma el
  `CharSet`, le avisa al propio cliente (`ITEM_EQUIPMENT_SEND`) y refresca la apariencia para los
  observadores actuales re-mandando un `VIEWPORT_PLAYER_APPEAR` con los datos nuevos (el original
  usa un paquete dedicado de "cambio" de viewport que no se portó todavía -- reaparecer logra el
  mismo resultado visual).
- **Pendiente/fuera de alcance de esta pasada de la Fase 3** (documentado para no confundir "no
  implementado" con "bug"): validación de que el tipo de item sea compatible con el slot destino
  (ej. el original impide poner un casco en el slot de arma -- acá cualquier item entra en
  cualquier slot), recoger/tirar items del suelo (`ItemBag`/`CMapItem`, necesita el viewport de
  items del mapa), Trade, Warehouse, ChaosBox, Shop/NPC y Personal Shop (`SourceFlag`/`TargetFlag`
  distintos de `0` se rechazan con `result=0` en vez de implementarse a medias), usar/consumir
  items (pociones, scrolls -- la lógica de efecto es Fase 4), y el cálculo de daño/defensa/
  requisitos desde `Item.txt` (la tabla de balance todavía no se carga en este puerto).

Probado de punta a punta reusando el arnés de la Fase 2 (`src/WorldTestClient/`), sembrando una
espada real directamente en la base de datos (slot 12 del inventario de Hero1, con el mismo
formato de 16 bytes que usaría DataServer) para no depender de recoger del suelo (todavía no
portado): ambos clientes reciben `ITEM_LIST_SEND` con el conteo correcto de items (incluidos los
2 items de arranque que ya sembraba `003_default_class_seed.sql` para el personaje de Hero2), A
equipa la espada sembrada (`ITEM_MOVE_SEND` con `result=1`), `ITEM_EQUIPMENT_SEND` refleja el arma
en `CharSet[1]`, y B ve el `CharSet` actualizado de A por viewport tras equipar — 3/3 casos OK
(además de los 4 heredados de la Fase 2, que se siguen corriendo en el mismo script). Script en
`tests/gameserver_fase3_e2e_test.py`.

### GameServer — Fase 4: monstruos y combate (primera pasada)

Con el personaje en el mundo y el equipo real de la Fase 3, la Fase 4 arranca monstruos y combate
básico — puerto de `Monster.cpp`/`MonsterManager.cpp`/`MonsterSetBase.cpp`/`Attack.cpp` (más la
rama de muerte de monstruo de `CObjectManager::CharacterLifeCheck`, `ObjectManager.cpp`). Primera
pasada deliberadamente acotada: monstruos **estáticos** (sin IA de patrulla/persecución) que el
jugador puede atacar y matar, con experiencia, subida de nivel y respawn — confirmado seguro a
nivel de protocolo dejarlos estáticos por ahora (un monstruo inactivo nunca entra al estado de IA
que dispara sus propios paquetes de movimiento/ataque, así que no le puede faltar nada a un
cliente real por este lado).

**Qué quedó implementado:**

- `MonsterInfoTable` (`World/MonsterInfo.cs`): carga `Data/Monster/MonsterList.txt` (tabla de
  balance por clase de monstruo — HP, daño, defensa, tasas de acierto/esquiva, exp, etc.), mismo
  formato `MemScript` que ya usaban `ServerList.dat`/`BlackList.txt`. Los multiplicadores globales
  de servidor (`m_MonsterMaxLifeRate` y similares) no están portados todavía — equivale a tenerlos
  todos en 100 (sin cambio), el default de un paquete sin tocar.
- `MonsterSpawnTable` (`World/MonsterSpawnTable.cs`): carga `Data/Monster/Spawn/"NNN - Mapa.txt"`
  (el número de mapa sale del nombre del archivo). Soporta los 3 tipos de fila más comunes: punto
  fijo, punto fijo con jitter ±3, y caja rectangular + cantidad (con reintento de hasta 100 puntos
  al azar rechazando tiles bloqueados/zona seguna, igual que `GetBoxPosition` del original).
- `Monster`/`MonsterRegistry` (`World/Monster.cs`, `World/MonsterRegistry.cs`): instancia de
  monstruo vivo + registro con el mismo rango de índices que el original reserva para monstruos
  (0-7999, separado del rango de jugadores 9000-9999 que ya usaba `PlayerRegistry`).
- Viewport de monstruos (`ViewportTicker`): mismo mecanismo que el viewport de jugadores de la
  Fase 2 pero para el registro de monstruos — aparecer (`PMSG_VIEWPORT_MONSTER`, `C2:13`) y
  desaparecer (reusa `PMSG_VIEWPORT_DESTROY_SEND`, `C1:14`, el mismo paquete que jugadores).
- Ataque cuerpo a cuerpo básico (`ClientProtocolHandler.OnAttackAsync`, puerto de `CGAttackRecv`/
  `CAttack::Attack`, `C1:D9`): valida mapa/rango/zona-segura igual que el original, calcula
  acierto/esquiva (`CAttack::MissCheck`), defensa del objetivo y daño crudo con el mismo piso de
  daño por nivel (`Attack.cpp:365-366`), y manda animación (`PMSG_ACTION_SEND`, `C1:18`) y número
  de daño (`PMSG_DAMAGE_SEND`, `C1:D9`).
- Muerte, experiencia y subida de nivel (`OnMonsterDeathAsync`/`GrantExperienceAsync`): al llegar a
  0 de vida manda `PMSG_USER_DIE_SEND` (`C1:17`) a los que lo veían, reparte experiencia
  proporcional al daño acumulado de cada atacante (`CharacterCalcExperienceAlone`, con el mismo
  castigo por diferencia de nivel del original), manda el popup de experiencia
  (`PMSG_REWARD_EXPERIENCE_SEND`, `C1:9C`) y, si subió de nivel, cura completo + `PMSG_LEVEL_UP_SEND`
  (`C1:F3:05`).
- Respawn (`ViewportTicker.RespawnDeadMonsters`): mismo temporizador que el original
  (`RegenTime*1000 + 1000ms` de gracia fija), revive en el punto de spawn original (re-resolviendo
  posición si era una caja aleatoria) con la vida llena.
- **Pendiente/fuera de alcance de esta primera pasada** (documentado para no confundir "no
  implementado" con "bug"): IA de monstruo (patrulla/persecución/aggro — `gObjMonsterUpdateProc`),
  contraataque de monstruo hacia el jugador, PvP, skills/magia, combos, reparto de experiencia en
  grupo (`CharacterCalcExperienceParty` — Social/Party es la Fase 5), drop de items al morir un
  monstruo. El balance real de items/daño/defensa (`PlayerObject.PhysiDamageMin/Max/Defense/
  AttackSuccessRate/DefenseSuccessRate`, que en esta primera pasada salían de una fórmula
  placeholder por Nivel/Fuerza/Agilidad/Vitalidad) se completó después — ver la segunda pasada más
  abajo.

Probado de punta a punta extendiendo el arnés de Fases 2/3 (`src/WorldTestClient/`): se sembró un
archivo de spawn sintético (una araña real, clase 3, `Type=0` en `MonsterList.txt`) en una
coordenada de Lorencia confirmada fuera de zona segura leyendo `Terrain1.att` real (a diferencia de
`(125,125)`, usado en las Fases 2/3 para viewport/movimiento, que sí es zona segura y por lo tanto
no sirve para probar ataque). El cliente A la ataca hasta matarla, recibe el popup de experiencia,
y — tras esperar el tiempo de respawn configurado (~11s) — un nuevo ataque confirma que revivió con
vida llena. 3/3 casos OK (más los 7 heredados de Fases 2/3). Script en
`tests/gameserver_fase4_e2e_test.py`.

### GameServer — Fase 4 (segunda pasada): balance real de items/combate

Reemplaza la fórmula placeholder de daño/defensa por el puerto real de
`CObjectManager::CharacterCalcAttribute` (`ObjectManager.cpp:1887-2523`) + el balance real de
`Data/Item/Item.txt` — a partir de acá el arma y la armadura equipadas **sí** cambian el daño/
defensa del jugador, no solo su apariencia (`CharSet`).

**Qué quedó implementado:**

- `ItemBalanceTable` (`World/ItemBalance.cs`): carga las 16 secciones de `Data/Item/Item.txt`
  (formato `MemScript`, un layout de columnas distinto por sección — armas, escudo, casco/armadura/
  pantalón, guantes, botas, alas, mascotas/anillos, joyas/pociones, orbes/pergaminos — confirmado
  columna por columna contra los comentarios de cabecera del archivo real). Generalizado para que
  cualquier item pueda resolver sus stats de balance por índice (`sección*32+subíndice`, igual que
  `GET_ITEM` del original).
- `CharacterBalanceConfig` (`Config/CharacterBalanceConfig.cs`): carga las constantes de
  `GameServerInfo - Character.dat` (daño físico base/acierto/defensa por clase — DW/DK/FE/MG/DL),
  con los mismos defaults que trae el archivo real si no se copia (un despliegue sin el `.dat` anda
  con el balance de fábrica igual).
- `ItemCombatMath` (`World/ItemCombatMath.cs`): puerto del escalado por nivel de mejora +0..+15 de
  `CItem::Convert` (lineal `+nivel*3`, más un "extra" cuadrático — números triangulares — para
  +10..+15; los escudos escalan Defensa distinto: flat `+nivel`, sin el extra). Sin las ramas de
  item excelente/set-item (`ItemOption.txt`/`SetItemOption.txt`, no portados — ningún item generado
  hoy tiene esas opciones activas de todas formas).
- `PlayerObject.RecalcCombatStats(ItemBalanceTable, CharacterBalanceConfig)`: el puerto en sí —
  daño físico base por clase (con la fórmula alternativa de FE con arco), aporte de arma(s)
  equipada(s) (báculo solo suma la mitad, es "arma mágica"), bono de flecha/perno por nivel de la
  munición, penalización de doble empuñadura al 55% (DK/MG/DL con dos armas cuerpo a cuerpo),
  acierto de ataque, y defensa/tasa de defensa (Dexterity + la suma de las piezas de armadura/
  escudo/alas equipadas). Se recalcula al entrar al mundo y también al equipar/desequipar
  (`OnItemMoveAsync`), no solo una vez.
- **Simplificaciones documentadas explícitamente** (en el doc-comment de `RecalcCombatStats`): sin
  crítico/excelente/set-item (dependen de `ItemOption.txt`/`SetItemOption.txt`), sin el bono de
  Defensa/DefenseSuccessRate por "5 piezas de armadura al mismo nivel alto"/"mismo set visual" (
  `ObjectManager.cpp:2314-2421`, depende de comparar índices visuales entre piezas), sin
  PhysiSpeed/MagicSpeed (no hay cooldown de ataque server-side todavía), sin daño mágico (no hay
  skills portados), y sin las variantes PvP de acierto/defensa (esta fase es solo jugador-contra-
  monstruo).

Probado de dos formas: (1) regresión completa de los arneses de Fases 2-6 (`gameserver_fase4_e2e_
test.py`, `gameserver_fase6_e2e_test.py`) copiando `Item.txt`/`GameServerInfo - Character.dat`
reales al entorno de prueba — 0 fallos, incluyendo el combate real de Devil Square (monstruos con
Defensa 35-45) con un personaje sin arma equipada (solo Fuerza alta), y (2) una verificación
dirigida: se reemplazó el item por defecto de Hero1 (un anillo, no un arma) por una espada real
("Kris", `GET_ITEM(0,0)`, nivel de mejora +1) antes del paso de equipar-y-atacar ya existente en el
flujo de Fase 4 — el daño observado en el log del servidor pasó de un valor fijo de 2 (sin arma,
solo fórmula base de clase) a 11-16 por golpe, exactamente el rango calculado a mano
(`PhysiDamageMin`=12, `PhysiDamageMax`=18, menos la Defensa=1 de la araña de prueba), confirmando
que el arma equipada ahora participa de verdad en el cálculo.

### GameServer — Fase 5: chat, party y amigos (primera pasada)

Puerto de Party.h/.cpp, la parte de chat de Protocol.cpp (`CGChatRecv`/`CGChatWhisperRecv`) y
Friend.h/DataServer/Friend.cpp. Guild (guild war, marcas, alianzas), PartyMatching (tablón de
búsqueda de grupo) y el correo entre amigos (`T_FriendMail`) quedan explícitamente fuera de esta
pasada — la investigación previa a implementar confirmó que ninguno de los tres es una dependencia
dura de chat/party/amigos básicos (son features independientes montadas encima, no debajo).

**Qué quedó implementado:**

- **Chat público** (`C1:00`): eco al propio hablante + broadcast a todo el que lo tenga en su
  `VisibleTo` — reusa el mismo mecanismo de viewport que ya movía jugadores desde la Fase 2, igual
  que el original (`MsgSendV2`/`VpPlayer2[]`). Comandos (`/`) y guild/Gens (`@`/`@@`/`@>`/`$`) se
  ignoran en silencio (no portados). Party (`~`) manda el mensaje tal cual, sigilo incluido, a cada
  miembro por `DataSend` directo — sin pasar por el viewport, así que llega sin importar mapa o
  distancia, igual que el original.
- **Whisper** (`C1:02`): busca al destinatario primero en este mismo proceso de GameServer
  (equivalente a `gObjFind`); si no está, usa el protocolo `0x72`/`0x73` entre GameServer y
  DataServer (el lado DataServer ya estaba completo desde una fase anterior — esta fase solo agregó
  la mitad que faltaba del lado GameServer) para whisper cruzado entre distintos GameServers de un
  mismo realm, con el ruteo resuelto por el registro en memoria de DataServer
  (`CharacterSessionStore`).
- **Party** (`C1:40`-`C1:44`): invitar/aceptar/rechazar, salir/expulsar (solo el líder puede
  expulsar a otros), lista completa retransmitida a todos los miembros en cada cambio, barras de
  vida/maná en broadcast periódico (~2s, vía `ViewportTicker`), y migración automática de líder si
  el slot 0 se va (con `List<int>` no hace falta un paso de "ascenso" aparte como en el array plano
  con huecos del original). Un grupo se disuelve entero si queda en 1 miembro tras una salida
  (mismo umbral que el original: de 2 a 1 siempre disuelve). Tamaño máximo 5, igual que el original.
- **Reparto de experiencia en grupo** (`CharacterCalcExperienceParty`): cuando el atacante que
  remata a un monstruo está en un grupo de 2+ miembros, el "botín" de experiencia se calcula sobre
  el daño TOTAL que le hizo el grupo entero (sumando el de todos los miembros, no solo de quien dio
  el golpe final) y se reparte proporcional al NIVEL entre los miembros que estén en el mismo mapa y
  a ≤10 tiles del monstruo (`MAX_PARTY_DISTANCE`) — no al daño que haya hecho cada uno, así que un
  miembro que no llegó a pegarle igual se lleva su parte si está en rango. Las tablas de bonus por
  tamaño/diversidad de clases del grupo (`m_PartyGeneralExperience`/`m_PartySpecialExperience`) no
  están portadas todavía — equivalen a sin bonus (misma clase de deuda técnica que el resto de
  multiplicadores globales de la Fase 4).
- **Amigos** (`C1:C0`-`C1:C4`): pedir/aceptar-rechazar/borrar/listar, con push de cambio de estado
  online/offline en tiempo real (reusa el mismo registro en memoria de DataServer que ya alimentaba
  whisper). A diferencia del original (que indirecciona por un GUID numérico vía `T_FriendMain`),
  acá se referencia directo por nombre de personaje, ya que `character.name` ya es PK única en este
  puerto — una simplificación de esquema sin cambio de comportamiento visible. Tablas nuevas:
  `db/postgres/004_friends.sql` (`friend_list`, `friend_request`). El protocolo interno
  DataServer↔GameServer para esto (head `0xB0`) usa un esquema de sub-códigos propio en vez de
  espejar 1 a 1 los sub-códigos `PSBMSG_HEAD` del original, ya que este puerto no necesita
  compatibilidad binaria con un DataServer externo.

Probado de punta a punta extendiendo el arnés de Fases 2/3/4 (`src/WorldTestClient/`): chat público
(eco + llega al otro jugador), whisper local, invitar/aceptar party (lista de 2 miembros en ambos
clientes), chat de grupo, remate de un monstruo con el grupo activo (ambos miembros reciben su
popup de experiencia aunque solo uno atacó), salida de B (el grupo se disuelve y A también recibe
el aviso), y el flujo completo de amigos (pedir, aceptar, confirmación simétrica en ambos lados,
lista con estado online, borrar). 15/15 casos nuevos OK (más los heredados de Fases 2/3/4). Script
en `tests/gameserver_fase5_e2e_test.py`.

### GameServer — Fase 6: eventos especiales, primera pasada (Devil Square)

Puerto de `DevilSquare.h/.cpp` + la porción "Devil Square" de `Protocol.h/.cpp`
(`CGDevilSquareEnterRecv`, `CGEventRemainTimeRecv`) + el guardado de ranking en DataServer
(`GDRankingDevilSquareSaveSend`, head `0x3F` — el lado DataServer ya estaba completo desde antes de
esta fase, solo faltaba que GameServer lo llamara). Investigación previa (subagente dedicado)
confirmó que de los 4 eventos de caja del original (Blood Castle, Chaos Castle, Devil Square,
Kalima) todos tienen datos reales en este paquete, que Illusion Temple y Golden Archer son código
muerto en este build (`GAMESERVER_UPDATE=803`, gateado por `#if`), y que Lua está "cableado" al
motor pero sin ningún script funcional en los archivos shippeados — por eso se decidió no portar
Lua en absoluto y arrancar Devil Square primero (su guardado de ranking ya estaba implementado del
lado DataServer, y reusa patrones ya establecidos: `MemScript`, `ViewportTicker`, spawn/combate de
la Fase 4).

**Qué quedó implementado:**

- **Motor de estados completo** (`World/DevilSquareManager.cs`): las 5 fases del original
  (`BLANK→EMPTY→STAND→START→CLEAN→EMPTY`), un bracket independiente por nivel (0-3 con datos reales
  en este paquete — brackets 4-6 existen en el código, igual que en el original, pero sin datos de
  recompensa/gate, ver quirk documentado en el propio archivo). El horario de apertura se recalcula
  desde cero cada vez a partir de la lista completa de `DevilSquare.dat` (sección 1, formato cron
  con comodines `*`), igual que `CheckSync` en el original — no se guarda un cursor persistente.
  Las 4 etapas de monstruos dentro de START se agregan progresivamente según el % de tiempo restante
  (75/50/25%, división entera en el mismo orden exacto que el original) y NO se limpian entre etapas
  (se acumulan, igual que `CDevilSquare::StageSpawn`).
- **Carga de datos** (`World/DevilSquareData.cs`): `DevilSquare.dat` (horarios + tablas de
  recompensa de experiencia/zen por bracket/puesto), `EventEntryLevel.dat` (rango de nivel por
  bracket, loader genérico por sección — reusable para Blood/Chaos/Kalima cuando se porten) y
  `EventStageSpawn.dat` (qué clases de monstruo entran en cada etapa). `MonsterSpawnTable` ahora
  también captura las filas `Type==4` (pool de posiciones de evento, antes se descartaban) sin
  instanciarlas al arrancar — `MonsterRegistry.SpawnAll` las salta explícitamente; solo
  `DevilSquareManager` las consume en runtime vía el nuevo `MonsterRegistry.SpawnOne`.
- **Entrada** (`C1:90`): valida bracket, ticket (`GET_ITEM(14,19)` "Devil's Invitation" con nivel
  embebido = bracket+1, o `GET_ITEM(13,46)` sin nivel), ventana abierta, rango de nivel del
  personaje (`EventEntryLevelTable.GetDevilSquareLevel`, último-match-gana igual que el original) y
  cupo; si todo pasa, consume 1 unidad del ticket, registra al participante y teletransporta
  (`C3:1C`, nuevo `WorldPacketBuilder.TeleportSend` — primer uso de este paquete en el puerto).
- **Puntaje**: crédito al atacante con MÁS daño acumulado sobre el monstruo (no al que dio el golpe
  final), `score += monstruo.Level * (bracket+1)`, igual que `MonsterDieProc`. Es un sistema
  independiente del reparto de experiencia normal (que sigue aplicando igual, sin cambios) — un
  monstruo de Devil Square da experiencia Y puntaje de evento a la vez.
- **Cierre** (`SetState_CLEAN`): calcula el ranking final (desempate por orden de ingreso, no por
  índice de array como el original ya que acá se usa una lista en vez de un arreglo fijo de 50
  slots), aplica experiencia (reusa el mismo pipeline de subida de nivel del combate normal) y zen
  (con tope simple contra overflow) a los primeros 10 puestos, manda `PMSG_DEVIL_SQUARE_SCORE_SEND`
  (`C1:93`, con el quirk exacto del original: la entrada #0 siempre es el propio receptor, repetida
  de nuevo en su posición real si entra en el top 9) a cada participante, y guarda el ranking en
  DataServer (head `0x3F`) para cada uno.
- **Comando de consola nuevo** `ds forcestart [bracket] [segundos]`: puerto de
  `IDM_EVENT_FORCEDEVILSQUARE` (menú de admin del original) — fuerza la próxima apertura sin
  esperar el horario real de cada 4 horas. Útil tanto para administración real como para testing
  determinístico.

**Explícitamente fuera de esta primera pasada (deuda técnica documentada, igual criterio que el
resto del proyecto):**

- Sin diálogo de NPC (Charon) para abrir la ventana de selección — ese subsistema no está portado
  todavía (`0x30`/`0x31` siguen sin manejar). Se entra mandando `C1:90` directo.
- Sin catálogo de mensajes de texto (`Message.txt`) — los avisos "abre en N minutos" no se mandan;
  el klaxon de 30 segundos sin texto (`C1:92`) sí.
- Recompensa de ITEM (solo 1er puesto) no portada — requiere el sistema recursivo de "bolsas" de
  `ItemBagManager`, fuera de esta pasada. Experiencia y zen sí se otorgan completos.
- Límite diario de entradas por cuenta (`DSCount`) no portado.

Probado de punta a punta con un personaje dedicado (Strength alto sembrado por SQL — los monstruos
reales de Devil Square 1, Skeleton Archer/Cyclops con 850-1100 HP y Defense 35-45, son intratables
con la fórmula de daño placeholder de la Fase 4 usando el Strength bajo de un personaje recién
creado; no afecta la fidelidad del motor de eventos en sí) usando `ds forcestart` por consola:
entrada con ticket real, teleport a Devil Square, aparición de monstruos de la etapa 0, muerte de
uno con puntaje > 0, cierre real del evento (STAND de 1 minuto + START de 1 minuto, sin acortar el
motor de estados en sí — solo los minutos configurados en el `DevilSquare.dat` de prueba), recepción
del paquete de puntaje, y confirmación por SQL de que la fila quedó guardada en
`ranking_devil_square`. Script en `tests/gameserver_fase6_e2e_test.py`.

### GameServer — Fase 7: skills y maná (primera pasada)

Puerto de la porción "casteo de skill de ataque a un solo objetivo" de `SkillManager.h/.cpp`
(`CGSkillAttackRecv`, `UseAttackSkill`, `BasicSkillAttack`) + el cálculo de daño mágico de
`CAttack::GetAttackDamageWizard` (`Attack.cpp:1309-1384`) + la regeneración periódica de maná/BP de
`CharacterAutoRecuperation` (`ObjectManager.cpp:1729-1758`). Investigación previa (lectura directa
del `.cpp`, no solo de subagente — un subagente había citado mal el archivo de los structs de
paquete, corregido antes de implementar) confirmó que `SkillList.txt` es una lista plana (sin
secciones, a diferencia de `Item.txt`/`MonsterList.txt`) y que `DamageMax` de un skill no es una
columna del archivo sino una fórmula fija (`DamageMin + DamageMin/2`) aplicada en tiempo de carga.

**Qué quedó implementado:**

- `SkillInfoTable`/`SkillDamageTable` (`World/SkillInfo.cs`): carga `Data/Skill/SkillList.txt` (19
  columnas confirmadas contra el encabezado real del archivo) y el multiplicador opcional de
  `Data/Skill/SkillDamage.txt` (el archivo real shippeado no tiene filas de datos, así que hoy es un
  no-op — implementado igual por si un despliegue distinto trae datos).
- **Casteo** (`C3:19`, `ClientProtocolHandler.OnSkillAttackAsync`): valida vida/mapa/zona segura
  (mismo chequeo que el ataque cuerpo a cuerpo), clase habilitada (`RequireClass[clase]`, sustituye
  la validación de "skill aprendido" — ver limitación abajo), nivel requerido, cooldown por skill
  (`SkillDelay`, timestamp por índice de skill) y rango; si todo pasa, descuenta maná/BP y manda
  `PMSG_MANA_SEND` — en ESE orden exacto, igual que el original: un casteo fuera de rango es
  enteramente gratis (no descuenta nada, no manda nada), pero un casteo válido que después erra el
  golpe igual costó su maná. El acierto/esquiva reusa el mismo cálculo que el ataque cuerpo a cuerpo
  (`AttackSuccessRate`/`DefenseSuccessRate`) porque el original no documenta una fórmula de "acierto
  mágico" separada en las partes revisadas de `Attack.cpp` — si erra, se manda `PMSG_DAMAGE_SEND` con
  el bit de miss y el método termina ahí, **sin** mandar el paquete visual del skill, confirmado
  byte a byte contra el propio `Attack()` original (`GCSkillAttackSend` está dentro del mismo bloque
  que arma el daño, después del `MissCheck`, así que un miss real tampoco lo manda en el C++
  original — no es una simplificación de este puerto).
- **Daño mágico** (`PlayerObject.RecalcCombatStats`, paso nuevo agregado al final): `MagicDamageMin/
  Max = Energía/const (9,4 — idénticos para las 5 clases en el `.dat` real) + skill.DamageMin/Max`,
  con el mismo bono de arma mágica que el original (`+ (MagicDamageRate/2 + nivelArma*2)%` si la
  mano derecha tiene una espada o un báculo). Se recalcula junto con el resto de `RecalcCombatStats`
  (entrar al mundo / equipar-desequipar), no hace falta un paso aparte.
- **Maná/BP**: `PMSG_MANA_SEND` (`C1:27`) con el mismo layout big-endian que el original (`type +
  mana[2]BE + bp[2]BE`, NO little-endian). Regeneración periódica cada ~3s (`ViewportTicker`, nuevo
  intervalo `ManaRegenTickInterval=15` sobre el tick de 200ms ya existente): `valor = MaxMana *
  MPRecoveryRate[clase] / 100`, confirmado contra el `.dat` real que `HPRecoveryRate` es 0 para las 5
  clases (por eso la vida NO regenera sola por este mecanismo, a propósito).
- **Paquete visual** (`SkillAttackSend`, `C3:19` de servidor a cliente): unicast al propio
  casteador + fan-out por viewport a quien lo esté viendo, cifrado por bloques igual que
  `TeleportSend` (primer y segundo uso de `SendEncryptedAsync` en el puerto).

**Explícitamente fuera de esta primera pasada (deuda técnica documentada, mismo criterio que el
resto del proyecto):**

- Sin sistema de "skill aprendido" (`GetSkill`/lista de skills del personaje) — se sustituye por una
  validación directa de clase+nivel contra `RequireClass`/`RequireLevel` en el momento del casteo.
  Como este puerto no trackea `ChangeUp` (siempre 0), en la práctica solo son alcanzables los skills
  con `RequireClass==1` para la clase del jugador.
- Solo skills de ataque a UN objetivo (`BasicSkillAttack`). Sin área (`MultiSkillAttack`), duración
  (Teleport/Poison/Ice sostenidos), combo, ni Teleport de aliado — todos usan otro flujo de paquete
  distinto en el original, no portado todavía.
- `MPConsumptionRate`/`BPConsumptionRate` (reducción de costo por item/efecto) asumidos 100% siempre
  — no hay sistema de opciones de item ni de efectos activos todavía.
- `CheckSkillRequireKillPoint` (específico de Chaos Castle) no portado.
- Sin `EffectList.txt` (buffs/debuffs) — el campo `Effect` de `SkillList.txt` se carga pero no se usa
  todavía.

Probado de punta a punta (`tests/gameserver_skills_e2e_test.py`, monstruo de prueba dedicado "Bull
Fighter" en vez del "Spider" que usan las Fases 2-6, porque necesita sobrevivir 1 golpe cuerpo a
cuerpo + 2 casteos sin morir antes del segundo): descuento de maná exacto, paquete de daño, paquete
visual del skill, y regeneración periódica de maná sin castear de nuevo. El acierto de un casteo es
probabilístico (mismo cálculo que el melee, ~17% de fallo individual contra el monstruo de prueba
con el arma sembrada), así que el arnés reintenta cada casteo hasta el primer acierto
(`CastSkillUntilHitAsync` en `WorldTestClient/Program.cs`) y calcula el descuento de maná esperado
como `costo × intentos` en vez de asumir que el primer intento siempre pega — un solo intento sin
reintento hacía al test intermitente (falló una vez por una racha de mala suerte durante el
desarrollo, confirmado leyendo el propio `Attack()` original que un miss real tampoco manda el
paquete visual, así que no era un bug de cifrado/decodificado como se sospechó en un primer
momento).

### GameServer — Fase 8: primer pase de jugabilidad real (stats, acción, tiendas de NPC)

Batch de arreglos/features pedido directamente por testing con el cliente real (`main.exe`), a
partir de una lista concreta de huecos observados jugando: stats con "números infinitos", varios
heads sin manejar en consola (`0xA0`, `0x31`, `0xA9`, `F3:0x06`, `0x18`, y `JoinServer 0x02`), falta
de tiendas.

**Bug de alineación de structs (la causa real de los "números infinitos"):** el build real tiene
`GAMESERVER_EXTRA==1` (`stdafx.h:9-11`), que agrega campos `View*` (DWORD) a varios structs de
paquete salientes, pero esos structs NO tienen `#pragma pack(1)` — MSVC inserta padding automático
antes de cualquier DWORD que no caiga en un múltiplo de 4 respecto al inicio del struct (header
incluido). El `PacketWriter` de este puerto escribe todo secuencial y no replicaba ese padding en 5
paquetes ya implementados, así que el cliente real leía los campos finales corridos (interpretados
como basura/"infinito"). Se corrigió byte a byte contra las líneas reales de `Protocol.h` en
`CharacterInfoSend`, `NewCharacterInfoSend`, `LevelUpSend`, `DamageSend` y `ManaSend` (este último
directamente le faltaban los campos `ViewMP`/`ViewBP` enteros, no solo el padding).

**Qué más quedó implementado:**

- **Punto de subida de nivel** (`C1:F3:06`, `CGLevelUpPointRecv`): agrega 1 punto a
  Strength/Dexterity/Vitality/Energy/Leadership, validado contra `LevelUpPoint` disponible y el tope
  `MaxStatPoint_AL0` de `GameServerInfo - Character.dat` (las 4 franjas de cuenta usan el mismo valor
  en el `.dat` real, así que no hace falta trackear `AccountLevel` para este chequeo en particular).
  Dispara un recálculo completo de combate igual que el original.
- **Acción/pose** (`C1:18`, `CGActionRecv`): puerto directo — el original ni siquiera valida el
  índice de "objetivo" opcional, lo reenvía tal cual al eco. Se agrega `PlayerObject.ActionNumber`
  para uso futuro (viewport de terceros que recién entran a ver a alguien sentado, no portado en esta
  pasada — el reenvío en vivo a los que ya lo estaban viendo sí funciona).
- **Quest info / pet item info mínimos** (`C1:A0`/`C1:A9`): respuestas placeholder (0 quests
  definidas, nivel/experiencia 0 de pet) solo para que el cliente real deje de reintentar — ningún
  sistema de quests/pets real portado todavía.
- **`JoinServer 0x02`** (`DisconnectAccountAckRecv`): investigado y confirmado no-op real — este
  puerto ya cierra la sesión de forma proactiva cuando el socket se desconecta de verdad (a
  diferencia del original, que depende de este ack asíncrono para liberar el slot de cuenta del lado
  JoinServer). Se agregó el handler igual, por completitud y para sacar el log de "no manejado".
- **Tiendas de NPC** (`World/Shop.cs` + la porción "NPCs y tiendas" de `WorldPackets.cs` +
  `ClientProtocolHandler`): puerto de `ShopManager.h/.cpp` + `Shop.h/.cpp` + la rama "tienda" de
  `NpcTalk.h/.cpp` (`CGNpcTalkRecv`/`CGNpcTalkCloseRecv`) + `CGItemBuyRecv`/`CGItemSellRecv` de
  `ItemManager.cpp`. Un NPC de tienda se representa como un `Monster` más (mismo mecanismo de
  índices/viewport, campo nuevo `Monster.ShopNumber`), spawneado desde `Data/ShopManager.txt` al
  arrancar — el mismo enfoque que ya usaban los NPCs "sin instanciar" de la Fase 4 (`MonsterInfoTable`
  con `Type!=0`), solo que ahora sí se instancian. Los items de cada tienda se cargan de
  `Data/Shop/<ShopPath>.txt` y se empaquetan en la grilla de 8×15 con el mismo algoritmo de
  primer-hueco-libre (top-left first-fit, por `Width`/`Height` de `ItemBalanceTable`) que usa
  `ShopRectCheck` en el original. Precio de compra/venta: puerto real de `CItem::Value()`
  (`Item.cpp:916-975`) — confirmado leyendo el fuente que este build es una versión extendida (con
  sockets, Pentagram, items Muun, alas/joyas custom vía Lua) más allá del 0.99B vanilla; se portó
  solo la rama "ítem sin ninguna opción especial" de la fórmula (ver huecos abajo), que alcanza para
  una economía de tienda funcional y da precios idénticos al original en el caso común. La distancia
  al NPC no se valida (`gObjCalcDistance(lpObj,lpObj)` en el original es literalmente la distancia de
  un objeto a sí mismo — siempre 0 — casi seguro un bug del original, replicado tal cual por
  fidelidad). Confirmado además contra el fuente real que `GAMESERVER_SHOP==0` en este build
  (`stdafx.h:26`), así que las tiendas de moneda alternativa ("Coin") y el paquete extra de precio
  (`GCShopItemCoinPriceSend`) son no-ops reales — no hacía falta portarlos.

**Explícitamente fuera de esta pasada (deuda técnica documentada, mismo criterio que el resto del
proyecto):**

- NPCs de diálogo especial (Trainer, Charon, GuildMaster, Warehouse) — todo NPC se trata como tienda
  simple. Sin quests reales atados a NPCs.
- Precio de compra/venta: no se portaron los bonos por opción especial (Luck/Skill/Adicional/
  Excelente), ni sockets, ni Pentagram, ni items Muun/alas-joyas custom (sistemas de esta build
  extendida, ninguno portado en el proyecto hasta ahora) — el precio calculado es el de un ítem
  "base" del mismo nivel/tipo, no el de una pieza optimizada.
- Compra: sin apilado de consumibles ya existentes en el inventario (`InventoryInsertItemStack`) — la
  compra siempre busca un slot vacío nuevo. Dark Horse/Dark Reaven (ítem "instantáneo" sin pasar por
  el inventario normal) y los ítems de gacha "Random Item" se compran como cualquier ítem normal en
  vez de su lógica especial (`GDCreateItemSend`/`CMossMerchant`, no portados).
- PKLevel/AccountLevel/GameMasterLevel no se trackean todavía, así que las restricciones de tienda
  por esos 3 criterios (`m_PKLimitShop`, `CheckShopGameMasterLevel`, `CheckShopAccountLevel`) no
  aplican — equivale a un server sin ninguna restricción configurada.
- Impuesto de Castle Siege sobre compras (`tax` en `CGItemBuyRecv`) siempre 0 — sistema no portado.
- Trade (jugador-a-jugador), Warehouse y Personal Shop siguen sin portar (mismo alcance ya
  documentado en Fase 3).

Probado de punta a punta (`tests/gameserver_shop_e2e_test.py`, entorno dedicado con una sola tienda
de prueba — GET_ITEM(14,13) "Jewel of Bless", Value=150 — y su NPC): hablar con el NPC, recibir el
listado con el item en el slot esperado, comprarlo (slot de inventario devuelto, dinero descontado
exactamente `18700` según la fórmula real), venderlo de vuelta (dinero aumentado exactamente `6200`),
más una verificación liviana de la acción (`0x18`, eco propio) y el punto de stat (`F3:0x06`, camino
de "sin puntos disponibles" — el personaje de prueba no llegó a subir de nivel en este script).
Los 5 fixes de padding se verificaron indirectamente: la regresión completa de
`gameserver_fase4_e2e_test.py`/`gameserver_skills_e2e_test.py` (que ya ejercitan
`CharacterInfoSend`/`LevelUpSend`/`DamageSend`/`ManaSend`) sigue pasando byte a byte después del fix,
y el layout de cada campo se confirmó manualmente contra los offsets reales de `Protocol.h` antes de
tocar el código (no solo "el test sigue pasando" — el test no tiene forma de detectar un campo con
valor *distinto* al del original si el cliente real no participa, así que la verificación primaria
fue lectura de fuente, y el test solo confirma que no se rompió nada más).

Adicionalmente (pedido explícito de la lista original: "spawns, monsters"), se verificó que el
GameServer arranca limpio apuntando a la carpeta `Data/` REAL de `MuServer99B` (sin ningún archivo
sintético de prueba): 15 mapas, 4537 filas de spawn de monstruo en 29 archivos, 2879 monstruos
instanciados, 14 tiendas de NPC reales con sus items — cero errores. Ver el punto 1 de "Próximos
pasos" más abajo para el detalle.

### GameServer — corrección crítica: el árbol de fuente C++ equivocado (formato de item, CharSet, Teleport, Party, Devil Square)

**Resumen para quien lea esto después**: una pasada de porting anterior (documentada más abajo en su
versión original, tachada) investigó varios formatos de paquete citando `GAMESERVER_UPDATE=803` como
justificación, y concluyó (entre otras cosas) que este build usaba `MAX_ITEM_TYPE=512`/`ItemInfo` de
12 bytes, `CharSet` de 18 bytes, `gate` de Teleport como WORD, etc. **Todo eso era investigación
contra el árbol de fuente C++ EQUIVOCADO.** Este repo trae DOS copias del código fuente del emulador:

- `Source/Source/Emulator/GameServer/` (sin sufijo de versión): una temporada de MU **mucho más
  tardía** (481 archivos — tiene Kanturu, Raklion, CastleSiege, GensSystem, IllusionTemple,
  MasterSkillTree, sockets, JewelOfHarmony, Muun...) y sí define `GAMESERVER_UPDATE` (135 archivos la
  usan). No tiene ninguna relación con este proyecto.
- `Source/Source/Emulator 0.99 (2.1.7)/GameServer/` (247 archivos): el árbol REAL. La prueba
  definitiva es `stdafx.h:7`: `#define GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]"` — coincide
  EXACTO con el nombre de este proyecto ("0.99B CHS SSeMU_2.1.7"). `grep -rl GAMESERVER_UPDATE` en
  este árbol da CERO resultados — la macro no existe acá. Cualquier comentario de este puerto que
  citara "GAMESERVER_UPDATE>=NNN" estaba, sin excepción, investigado contra el árbol equivocado.

El bug se descubrió porque el usuario reportó un crash real (`IndexOutOfRangeException` en
`ItemMoveRecv.Parse`, moviendo un item de inventario con el cliente real) y el mapa vacío de
monstruos/NPCs en su propio despliegue. Investigando el crash se encontró la causa raíz y, tirando del
hilo, se auditaron todos los demás lugares que citaban `GAMESERVER_UPDATE` — los 6 resultaron
equivocados. Se revirtió cada uno al formato real (verificado línea por línea contra el árbol
correcto, no por suposición):

- **Formato de item** (`Item.cs`): `MAX_ITEM_TYPE=32` (no 512), `MAX_ITEM=512` (16 secciones×32, no
  8192), `GET_ITEM(x,y)=x*32+y`. `ItemInfo` wire real es de **5 bytes** (`MAX_ITEM_INFO=5`, puerto
  exacto de `CItemManager::ItemByteConvert`, ItemManager.cpp:1593-1612), no 12. El formato DB
  (`ToDbBytes`/`FromDbBytes`) es un slot de 16 bytes con solo 10 con contenido real (byte9 SIEMPRE 0
  en este build — el índice completo entra en 9 bits, byte0 + 1 bit en byte7, no hacen falta bits
  extra); no existen sockets/JewelOfHarmony/Muun/items periódicos como CAMPOS de `CItem` en este
  build (confirmado leyendo la clase completa, Item.h:36-115) — esos campos fabricados se eliminaron.
  Afectaba `ItemMoveRecv`/`ItemMoveSend`/`ItemChangeSend`/`ItemListSend`/`ItemGetSend`/`ItemBuySend`/
  `ShopItemListSend`/`ViewportItemAppear` y el formato DB completo.
- **`CharSet`** (`PlayerObject.cs`): 13 bytes (no 18) — puerto exacto de
  `CObjectManager::CharacterMakePreviewCharSet` (ObjectManager.cpp:1139-1269). Sin los "bits de
  extensión" en índices 12-17 que la pasada anterior había inventado, y las alas/mascota solo
  soportan los índices reales de este build (alas 0-2/3-6/30, mascota 0-4) — las ramas 36-43/49/50/
  130-135/262-267 (alas) y 37/64/65/67/80/106/123 (mascota) eran de la temporada equivocada.
- **`PMSG_CHARACTER_LIST_SEND`** (`ClientPackets.cs`): sin `ExtWarehouse` en el header ni
  `GuildStatus` por personaje — ninguno de los dos existe en `Protocol.h` de este build. El único
  byte real que hacía falta (y la causa real del bug "solo veo el primer personaje") es el relleno de
  alineación de 1 byte que el compilador C++ inserta antes del `WORD Level` (el struct no tiene
  `#pragma pack(1)` en esa región) — eso SÍ sigue ahí, era la única parte correcta del fix original.
- **`PMSG_TELEPORT_SEND`** (`WorldPackets.cs`): `gate` es BYTE (no WORD) — puerto exacto de
  `CMove::GCTeleportSend`, Move.cpp:279-292.
- **`PMSG_VIEWPORT_PLAYER`** (aparición de jugadores, `WorldPackets.cs`): `index[2]+x+y+CharSet[13]+
  count(WORD, lista de efectos)+name[10]+tx+ty+DirAndPkLevel` — sin los campos `attribute`/
  `MuunItem`/`level`/`MaxHP`/`CurHP` inventados, y `count` va ANTES de `name`, no al final. ViewState
  usa los 4 bits bajos de `CharSet[0]` (no 3).
- **`PMSG_PARTY_LIST`/`PMSG_PARTY_LIFE`** (`WorldPackets.cs`): PartyList es de 22 bytes/miembro
  (`name[10],number,map,x,y,CurLife(DWORD),MaxLife(DWORD)`, sin `ServerCode` ni maná). PartyLife es
  de **1 byte/miembro** (nibble alto=slot, nibble bajo=vida en décimos 0-9), sin maná ni nombre.
- **`MAX_DS_LEVEL`** (`DevilSquareData.cs`): 4 (no 7) — confirmado en `DevilSquare.h:10` y en el loop
  `for(n=0;n<4;n++)` de `CEventEntryLevel::GetDSLevel`.

**Regresión completa** (fase1/fase4/fase5/fase6/skills/shop/grounditem) corrida de nuevo tras revertir
todo lo de arriba — sin fallos, incluyendo verificación explícita de que el paquete de aparición de
jugador ahora mide exactamente 37 bytes (4 header + 1 count + 32 del struct real de 1 jugador) y que
Devil Square (que ejercita Teleport + `MAX_DS_LEVEL`) sigue cerrando el evento correctamente.

**Lección para el resto de este puerto**: cualquier comentario que quede citando "GAMESERVER_UPDATE"
en el código a partir de ahora es o bien un caso ya verificado como correcto (ej. `ItemChangeSend`,
que resultó no tener el campo `attribute` inventado) o una nota histórica explicando este mismo
hallazgo — no hay que volver a confiar en investigación que cite esa macro sin re-verificarla contra
`Source/Source/Emulator 0.99 (2.1.7)/GameServer/` explícitamente.

### GameServer — tres bugs más encontrados investigando "todos los personajes se ven Dark Wizard" y "mapa vacío"

Tras la corrección de arriba, el usuario reportó dos síntomas adicionales que resultaron ser bugs
separados (no relacionados con el árbol de fuente equivocado):

**1. Todos los personajes se veían como Dark Wizard en el selector.** La DB guarda `Class` como el
código crudo de MU pre-shifteado (`0`=DW, `16`=DK, `32`=FE, `48`=MG, `64`=DL/SUM — ver
`db/postgres/002_characters.sql:21` y el seed en `003_default_class_seed.sql`), que codifica a la vez
`(ÍndiceDeClaseCompacto*16 + ChangeUp)`. Pero `PlayerObject.BuildCharSet` (fórmula real `cls*32`,
puerto de `ObjectManager.cpp:1139-1269`) espera el índice compacto 0-4, no el valor crudo de DB — con
cualquier clase que no fuera DW (`0`), `cls*32` desbordaba el byte y se truncaba a 0, renderizando
Dark Wizard para todos. Ya existía un precedente correcto de esta descomposición en
`ClientProtocolHandler.cs:509-512` (confirmación de creación de personaje) pero nunca se había
aplicado a los dos caminos que sí importaban: `OnCharacterListFromDataServerAsync` (selector de
personajes) y `OnCharacterInfoFromDataServerAsync` (entrada al mundo) — ambos corregidos a
`(Class/16, Class%16)` = (clase compacta, ChangeUp).

**2. `PMSG_ITEM_GET_SEND` sin su campo `ViewIndex`.** Un comentario existente en `WorldPackets.cs`
afirmaba que `GAMESERVER_EXTRA` "nunca vale 1" en este build — falso: `stdafx.h:9-10` lo define
incondicionalmente (`#ifndef GAMESERVER_EXTRA #define GAMESERVER_EXTRA 1 #endif`). Los bloques
`#if(GAMESERVER_EXTRA==1)` de `CharacterInfoSend` (13 `DWORD View*`, `Protocol.h:589-632`) ya estaban
bien portados, pero el de `ItemManager.h:109` (`PMSG_ITEM_GET_SEND.ViewIndex`, un `DWORD` al final del
paquete) se había omitido por completo. Corregido en `MoneySend` (escribe 0, puerto de la rama dinero
de `CGItemGetRecv`) e `ItemGetSend` (escribe el `groundIndex`, puerto exacto de
`pMsg.ViewIndex = index;`). Confirmado por grep que `Viewport.h`/`Viewport.cpp`/`Move.h`/`Party.h`/
`User.h` NO tienen bloques `GAMESERVER_EXTRA` — no afecta esos paquetes.

**3. Mapa vacío para el cliente real (sin monstruos/NPCs/otros jugadores) — la causa raíz real.**
`PlayerObject.RegenOk` (puerto de `char RegenOk`, `User.h:509`) arrancaba en `true` ("bloqueado hasta
que el cliente mande `0xF3:0x12`"), y `ViewportTicker.TickAsync` salta ENTERO el barrido de un
observador mientras `RegenOk==true` (ver `if (observer.RegenOk) continue;`) — ni jugadores, ni
monstruos, ni NPCs. Pero el original **arranca en 0 (=false, NO bloqueado)**: `gObjCharZeroSet`
(`User.cpp:368`), llamado por `gObjAdd` al aceptar la conexión TCP, ni el login ni la selección de
personaje ni `CharacterMakePreviewCharSet`/carga completa del personaje (`ObjectManager.cpp:2733-2736`)
tocan `RegenOk` para nada. El único lugar que lo pone en 1 (bloqueado) es un teleport/gate real
(`gObjMoveGate`/`gObjTeleport`/`gObjSummonAlly`, `User.cpp:2114/2144/2168/2220/2259`) — mecanismo que
este build todavía no tiene enchufado a ningún paquete entrante (el único emisor de `TeleportSend` es
`DevilSquareManager`, que tampoco setea `RegenOk=true`). `PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE`
(`CGCharacterMoveViewportEnableRecv`, `Protocol.cpp:1300-1310`) SOLO hace `RegenOk = (RegenOk==1) ? 2
: RegenOk` — es un ack de "terminé de cargar el mapa" que en el original es un no-op si no venías de
un teleport. El regression test nunca detectó este bug porque `WorldTestClient` manda ese paquete
incondicionalmente al entrar al mundo (enmascarando el default roto); un cliente real de MU
probablemente solo lo manda como ack posterior a un teleport/gate real, y como el login inicial nunca
pasa por ese camino, el jugador quedaba bloqueado para siempre. Corregido: `RegenOk` ahora arranca en
`false`, coincidiendo con el comportamiento real documentado arriba.

**Regresión completa corrida de nuevo tras las tres correcciones** (fase1/fase4/fase5/fase6/skills/
shop/grounditem) — 0 FALLO, sin necesidad de reintentos.

### GameServer — cuarto bug: `OnMoveAsync` guardaba la posición de ARRANQUE en vez de la de LLEGADA

Reportado por el usuario como "al atacar, el primer golpe me manda de vuelta a mi sitio de aparición".
La causa real no era el ataque sino el movimiento: `ClientProtocolHandler.OnMoveAsync` (Fase 2) hacía
`player.X = recv.X; player.Y = recv.Y;` — es decir, guardaba el ANCLA del paquete de movimiento (la
posición desde la que arrancó ESE movimiento) como si fuera la posición actual, en vez de `tx,ty` (la
posición de LLEGADA calculada recorriendo `RoadPathTable`). En el original, `CGMoveRecv`
(Protocol.cpp:901-987) NO toca `lpObj->X` en absoluto -- esa posición confirmada se actualiza de forma
gradual, tile por tile, por un tick periódico separado (`CObjectManager::ObjectMoveProc`,
ObjectManager.cpp:419-482) a medida que el personaje camina visualmente. Este puerto resuelve el
movimiento de forma instantánea (sin ese tick) y ningún otro lugar del código avanza `X` hacia `TX`,
así que `player.X` tenía que quedar en la posición de llegada de una vez -- pero como se guardaba el
ancla (la posición VIEJA), cada movimiento dejaba a `player.X` "un movimiento de atraso" respecto a
donde el cliente realmente estaba parado. El siguiente movimiento (por ejemplo, acercarse a un
monstruo para atacar) casi siempre caía fuera del radio de 15 tiles permitido -- medido contra esa
posición vieja -- y el servidor respondía con `PositionSend(player.X,player.Y)`, la posición vieja,
frecuentemente muy cerca de donde el personaje había aparecido originalmente. Esto también afectaba
(en menor medida, no reportado pero real) el rango de ataque de `OnAttackAsync` y el rango de visión
de `ViewportTicker`, ambos comparados contra `player.X`. Corregido: `player.X/Y` ahora se asigna a
`tx,ty` (posición de llegada), igual que `TX/TY`. Regresión completa (fase1/fase4/fase5/fase6/skills/
shop/grounditem) corrida de nuevo tras el fix -- 0 FALLO.

### GameServer — bugs de alineación de paquetes (daño/maná basura, sin experiencia) e items que desaparecían al moverlos

El usuario reportó, jugando con el cliente real tras las correcciones anteriores: el ataque seguía
mandándolo de vuelta a su sitio de aparición, los números de golpe salían ilógicos (tipo
"9998989898"), no se ganaba experiencia al matar, y los items desaparecían al moverlos en el
inventario. Se encontraron y corrigieron 4 bugs reales, ninguno relacionado con el árbol de fuente
equivocado esta vez:

**1) `PMSG_DAMAGE_SEND`/`PMSG_MANA_SEND` con 3 bytes de relleno DE MÁS (la causa real de los números
basura).** Un comentario de una pasada anterior de esta sesión asumía que estos dos paquetes usan un
header de 4 bytes (como `CharacterInfoSend`/`LevelUpSend`, que sí lo usan porque son `C1:F3:xx` con
sub-código) y agregaba 3 bytes de relleno de alineación antes de los `DWORD` "View\*" de
`GAMESERVER_EXTRA`. Pero `PMSG_DAMAGE_SEND` (`C1:D9`) y `PMSG_MANA_SEND` (`C1:27`) son opcodes
directos SIN sub-código, así que usan `PBMSG_HEAD` (`Protocol.h:22-41`: `type+size+head`, 3 campos
`BYTE`, **3 bytes**, no 4) — con ese header real, el offset tras los campos previos ya cae en
múltiplo de 4, así que el relleno correcto es **cero bytes**, no 3. Los 3 bytes de más corrían
`ViewCurHP`/`ViewDamageHP`/`ViewMP`/`ViewBP` y alargaban el paquete completo 3 bytes de más,
produciendo exactamente los números ilógicos reportados — y muy probablemente parte de la causa del
"vuelve a mi sitio de aparición", dado que `DamageSend` se manda en cada golpe y un paquete corrido
de tamaño puede desincronizar el resto del stream cifrado para ese cliente.

**2) `PMSG_REWARD_EXPERIENCE_SEND` (popup de experiencia) sin relleno de alineación (le faltaba 1
byte).** Offset real tras `index[2]+WORD experience[2](4 bytes)+damage[2]` = 3+2+4+2 = 11, no
múltiplo de 4 -- hace falta 1 byte de relleno antes de los 3 `DWORD` "View\*" que nunca se agregó.
Corregido en `MonsterDieSend`. Esto explica el "no dan experiencia": el popup llegaba con los
`DWORD` corridos 1 byte.

**3) `PMSG_ITEM_GET_SEND` (usado también para `MoneySend`) sin el relleno que el fix de
`GAMESERVER_EXTRA`/`ViewIndex` de esta misma sesión debió agregar y no agregó.** Offset real tras
`result(1)+ItemInfo[5]` = 3+1+5 = 9, no múltiplo de 4 -- hacen falta 3 bytes antes de `ViewIndex`.
Corregido en ambos (`MoneySend`/`ItemGetSend`).

**4) `PMSG_ITEM_MOVE_SEND.result` tenía la semántica invertida, y el puerto hacía un "swap" que el
protocolo real no soporta (la causa real de "los items desaparecen").** `result` NO es un booleano
genérico "0=falla/1=éxito" -- es el valor de retorno real de `MoveItemToInventoryFromInventory`
(`ItemManager.cpp:1920-1978`): `TargetFlag` (0 para Inventory) en éxito, `0xFF` en cualquier falla.
Este puerto mandaba 0=falla/1=éxito, exactamente al revés de lo que el cliente real espera (que solo
chequea `!= 0xFF`, así que un 0 de "falla" se leía como éxito). Peor todavía: `InventoryAddItem`
(`ItemManager.cpp:1052-1102`) **rechaza el move con `0xFF` si el slot destino ya tiene un item** --
no existe intercambio/swap a nivel de protocolo, `PMSG_ITEM_MOVE_SEND` solo tiene lugar para UN item
en su respuesta. Este puerto SÍ hacía swap (mover el item destino de vuelta al origen) cuando el
slot destino estaba ocupado, pero como el cliente real nunca se entera qué pasó con el segundo item
(la respuesta solo describe el slot destino), lo pierde de la UI aunque el servidor lo siga
trackeando correctamente en el slot origen -- el item "desaparece" visualmente sin haberse perdido
en el server. Corregido: mover a un slot ocupado ahora se rechaza entero (`result=0xFF`, sin tocar
ningún slot), igual que el original; el éxito manda `result=0` (`TargetFlag`).

**Regresión completa corrida de nuevo** (fase1/fase4/fase5/fase6/skills/shop/grounditem) -- 0 FALLO.
Se tuvo que actualizar una aserción de `WorldTestClient` que esperaba (incorrectamente) `result==1`
tras equipar un item; ahora espera `result==0`, coincidiendo con la semántica real recién descubierta.

### GameServer — quinto intento: `PMSG_POSITION_SEND` sin el campo `index[2]` (la causa real del "atacar me devuelve a mi sitio de aparición")

Tras los 4 fixes de arriba el usuario reportó que el bug seguía: atacar (que primero requiere
caminar para acercarse al monstruo) lo mandaba de vuelta exactamente al sitio donde aparece al
loguearse. Encontrado el bug real: `WorldPacketBuilder.PositionSend` (`C1:D0`, el paquete que el
servidor manda para corregir/forzar la posición del cliente cuando un movimiento se rechaza por
colisión o por superar el radio de 15 tiles, ver `ClientProtocolHandler.OnMoveAsync`) mandaba
**solo `x,y`** (2 bytes de payload). El struct real (`Protocol.h:339-345`) es:

```
struct PMSG_POSITION_SEND { PBMSG_HEAD header; BYTE index[2]; BYTE x; BYTE y; };
```

Le faltaba el `index[2]` inicial -- el payload real son 4 bytes, no 2. El cliente, que lee este
paquete a offset fijo esperando `index[0..1]+x+y`, terminaba interpretando los 2 bytes que sí
mandábamos (nuestro `x,y`) como si fueran `index[0..1]`, y el `x,y` real que usaba para reposicionar
al personaje salía de donde sea que cayeran los siguientes bytes del stream (basura, o los primeros
bytes del paquete siguiente) -- una corrección de posición corrompida. Como este paquete se dispara
precisamente cuando el cliente intenta moverse y el servidor rechaza ese movimiento (el caso típico
al caminar para acercarse a atacar), y como el personaje nunca había recibido una corrección de
posición válida desde que entra al mundo, el resultado visible era "vuelve siempre al mismo sitio
donde aparecí al loguearme" -- coincide exactamente con el reporte. Corregido: `PositionSend` ahora
toma también el índice del jugador y escribe los 4 bytes reales.

Regresión completa (fase1/fase4/fase5/fase6/skills/shop/grounditem) corrida de nuevo -- 0 FALLO.

### GameServer — cobertura completa de los 7 archivos `GameServerInfo - *.dat`

Hasta este punto, `ServerInfoConfig`/`CharacterBalanceConfig` solo cargaban los campos de
`Common.dat`/`Event.dat`/`Character.dat` que ya tenían un sistema portado detrás (~50 de ~845 claves
reales). El resto vivía sin leer -- ni en C#, ni disponible para un futuro sistema. Se agregó una
segunda capa de cobertura TOTAL:

- `Config/GameServerInfoCommon.cs`, `GameServerInfoCharacter.cs`, `GameServerInfoChaosMix.cs`,
  `GameServerInfoCommand.cs`, `GameServerInfoEvent.cs`, `GameServerInfoItem.cs`,
  `GameServerInfoSkill.cs`, `GameServerInfoCustom.cs` -- ocho clases (7 archivos +`Custom.dat`, que en
  el original leen 3 clases separadas: `CCustomArena`/`CCustomAttack`/`CCustomPick`) que en conjunto
  exponen los **845 campos reales** de `CServerInfo` (`ServerInfo.h`/`.cpp` del árbol correcto) como
  propiedades tipadas (`int`, `int[4]` por AccountLevel, `int[5]` por clase DW/DK/FE/MG/DL, o `int[,]`
  para las 5 matrices `[clase][AL]`/`[clase][clase]`), con el mismo nombre que el campo `m_Xxx`
  original (sin el prefijo) y la misma clave de `.ini` que usa `GetPrivateProfileInt` en el C++ real.
- Generadas con un script que parseó `ServerInfo.cpp` completo (1556 líneas, 9 funciones
  `Read*Info(section,path)`) más `CustomArena.cpp`/`CustomAttack.cpp`/`CustomPick.cpp`, extrayendo
  cada llamada `GetPrivateProfileInt(section,"Clave",0,path)` -- el default en el C++ real es
  **siempre 0** (confirmado leyendo el archivo entero), así que los valores "de fábrica" viven
  enteramente en el `.dat` shippeado, no en el código fuente. Verificado programáticamente que las 844
  de 845 claves generadas existen tal cual en los `.dat` reales (la única que falta,
  `CustomArenaDamageRate` en `Character.dat`, simplemente no está en el archivo shippeado -- cae al
  mismo default 0 que el `GetPrivateProfileInt` real haría en ese caso, no es un bug).
- Los 7 `GameServerInfo - *.dat` reales (`ChaosMix`/`Character`/`Command`/`Common`/`Custom`/`Event`/
  `Item`/`Skill`) se copiaron a `MuServer.GameServer/Data/` y el `.csproj` los copia automáticamente
  al build (`CopyToOutputDirectory`) -- a diferencia del resto de `Data/` (mapas/monstruos/items, muy
  pesado para el repo), estos 8 archivos son texto plano chico y ahora vienen listos de fábrica, sin
  que el usuario tenga que copiarlos a mano desde `GameServer/DATA/`.
- `GameServerConfig.cs` suma 5 rutas nuevas (`ServerInfoChaosMixPath`/`CommandPath`/`ItemDatPath`/
  `SkillDatPath`/`CustomPath`), todas configurables por `.ini` con el mismo default real
  (`Data/GameServerInfo - Xxx.dat`).
- `Program.cs` carga las 8 clases al arrancar (log `[GameServerInfo] Cobertura completa cargada`) y
  las deja disponibles como variables locales para que el próximo sistema que se porte (Chaos Mix,
  `/reset`, Custom Arena/Attack/Pick, Mana Shield, PK, Trade, Guild, Jewel, Fruit, Quest, Blood/Chaos
  Castle...) las enchufe directamente sin escribir el parseo del `.dat` de cero.

**Importante**: que un campo esté cargado acá NO implica que el sistema que lo consumiría en el
original ya esté portado -- ver el catálogo de sistemas faltantes (portales/gates, IA de monstruos,
Trade/Warehouse/PersonalShop, Guild, Blood/Chaos Castle, Quest, comandos GM, Duel, Custom Arena/
Attack/Pick, DarkSpirit, reset...) reportado al usuario en esta misma sesión. Esta expansión es
exclusivamente la capa de datos: cierra la brecha de "955 campos sin leer" para que ningún valor
quede hardcodeado, pero conectar cada campo a un sistema de juego real sigue siendo trabajo aparte,
sistema por sistema.

Regresión completa (fase1/fase4/fase5/fase6/skills/shop/grounditem) corrida de nuevo tras esta
expansión -- 0 FALLO. Se encontró y corrigió de paso un bug en los scripts de test (`tests/*.py`):
copiaban el contenido de `bin/Debug/net10.0` con `shutil.copy` plano, que no soporta subcarpetas --
al aparecer la carpeta `Data/` con los 8 `.dat` nuevos, todos los tests fallaban con
`IsADirectoryError`. Corregido a `shutil.copytree` para entradas que son directorios.

### GameServer — sexto bug encontrado: el progreso NUNCA se guardaba en la base de datos (causa raíz real de "todos los personajes vuelven siempre a X=182 Y=128")

El usuario reportó, tras probar con el cliente real, que el bug de "al atacar vuelvo siempre al
mismo sitio" seguía pasando SIEMPRE (con cualquier personaje), y que los personajes nuevos aparecían
en la misma posición que uno ya existente, "como si viviera en un bucle". Investigación completa:

1. **`X=182 Y=128` NO está hardcodeado de forma incorrecta.** Se verificó byte a byte contra
   `MuServer99B/DB/MuOnline.sql` (el `INSERT [dbo].[DefaultClassType]` real, UTF-16, decodificado con
   Python): la fila semilla real para DW/DK/MG/DL tiene literalmente `MapNumber=0, MapPosX=182,
   MapPosY=128` (FE/Elf sí difiere: `MapNumber=3, MapPosX=172, MapPosY=97`, Noria). Nuestro
   `db/postgres/003_default_class_seed.sql` coincide exactamente con esos valores -- no hay ningún
   literal inventado ahí, es la semilla original de MU 0.99b.
2. **La causa real: `CObjectManager::DelCharacterInfo` (ObjectManager.cpp:625) manda
   `GDCharacterInfoSaveSend` (C2:0x30, GameServer→DataServer) justo antes de desconectar** -- y
   `DataServerProtocolHandler.OnCharacterInfoSaveAsync` (recepción del lado DataServer) YA estaba
   100% implementado y conectado a `NpgsqlCharacterDataRepository.SaveCharacterAsync` desde hacía
   varias sesiones. Lo que **nunca existió** era el lado GameServer que arma y manda ese paquete --
   `DataServerCharacterPacketBuilder` solo tenía `ConnectCharacter`(0x70)/`DisconnectCharacter`(0x71),
   ninguno de los cuales lleva posición ni ningún otro stat. Resultado: la fila de `character` en
   Postgres se actualiza en la CREACIÓN (con la semilla de `DefaultClassType`) y nunca más -- cada
   login vuelve a leer para siempre esa misma fila sin tocar, sin importar cuánto caminó/subió de
   nivel/movió items el personaje en cualquier sesión anterior. Esto también explica el reporte de
   "personaje nuevo copia el primero": no hay ninguna copia real, es el mismo bug -- cualquier
   personaje, nuevo o viejo, siempre lee/relee la misma foto congelada de su fila.
3. Se agregó `DataServerCharacterPacketBuilder.CharacterInfoSaveSend` (`WorldPackets.cs`), espejo
   exacto de `SDHP_CHARACTER_INFO_SAVE_SEND`/`GDCharacterInfoSaveSend` (`DSProtocol.cpp:991-1047`,
   ~30 campos: stats, inventario, skill, quest, efectos, PK, y `Map/X/Y/Dir`), y
   `ClientProtocolHandler.SaveCharacterAsync` como único productor de ese paquete. Se agregaron a
   `PlayerObject` los campos que se leían de DataServer al entrar al mundo pero se descartaban sin
   guardar (`ChatLimitTime`, `BCCount`/`CCCount`/`DSCount`) para no pisarlos con 0 en cada guardado.
   Se replicaron los TRES disparadores reales encontrados en el C++ (cada uno con su propio
   throttle, igual que el original reparte esta lógica entre varios call sites en vez de
   centralizarla):
   - **Desconexión** (`ObjectManager.cpp:623-627`): incondicional, sin throttle -- se manda justo
     antes de `DisconnectCharacter`, en `ClientProtocolHandler.OnDisconnectAsync`.
   - **Subida de nivel** (`ObjectManager.cpp:1034-1038`): throttle de 60s (`CharSaveTime`), disparado
     dentro de `ApplyExperienceGainAsync` cuando `leveledUp` es true.
   - **Autoguardado periódico** (`User.cpp:2535-2541`): cada 10 minutos (`AutoSaveTime`), incondicional
     para cualquier jugador conectado -- nuevo método `ViewportTicker.TickAutoSaveAsync`, corre en
     cada tick del viewport (200ms) igual que el resto del ciclo periódico.
4. **Verificado con un test nuevo** (variante de `gameserver_fase5_e2e_test.py`): Hero1 se mueve de
   `(198,150)` a `(200,150)` vía `WorldTestClient`, se desconecta, y se lee la fila de Postgres
   directamente -- antes de este fix hubiera seguido en `(198,150)` (la semilla), después del fix
   queda en `(200,150)` (la posición real de la sesión). Confirmado en verde.

Regresión completa (fase1/fase4/fase5/fase6/skills/shop/grounditem) corrida de nuevo -- 0 FALLO.

### GameServer — Fase 3 (continuación): items de piso (recoger/tirar)

Puerto simplificado de `CMapItem` (`MapItem.h`/`.cpp`) + la parte de `CMap` que administra el array
de items tirados por mapa (`Map.cpp:256-431`). A diferencia de jugadores/monstruos (índice GLOBAL,
ver `MonsterRegistry`), los items de piso se indexan POR MAPA — `GroundItem`/`GroundItemRegistry`
(`World/GroundItem.cs`) replican el array real de `MAX_MAP_ITEM=300` slots por mapa (`Map.h:12`) como
un ring-buffer que reutiliza el próximo slot libre a partir de un cursor, igual que
`CMap::MonsterItemDrop`/`ItemDrop`/`MoneyItemDrop`.

- **Aparición/desaparición** (`PMSG_VIEWPORT_ITEM`, `Viewport.h:119-125`): `C2:0x20` (aparecer, bit
  `0x80` del byte alto del índice = "recién caído", anima la caída en el cliente) y `C2:0x21`
  (desaparecer — cabecera DISTINTA de la que usan jugadores/monstruos, `C1:0x14`). El dinero tirado
  reusa `CItem` con `m_Index=GET_ITEM(14,15)` pero un empaquetado de `ItemInfo` totalmente distinto
  al de un item normal (puerto byte a byte de `Viewport.cpp:1030-1046`, monto repartido en bytes NO
  contiguos: `[1]=bits16-23, [2]=bits8-15, [4]=bits0-7, [6]=bits24-31`, con relleno `0xFF` en `7-11`).
  El barrido de aparición/desaparición reusa el mismo patrón O(n) por tick (200ms) que ya existía para
  monstruos (`ViewportTicker.TickGroundItemsForObserverAsync`), con `PlayerObject.VisibleGroundItems`
  llevando la cuenta por jugador.
- **Recoger** (`PMSG_ITEM_GET_RECV/SEND`, `C1/C3:0x22`): puerto de `CGItemGetRecv`
  (`ItemManager.cpp:3289-3528`) vía `CMap::CheckItemGive` para el rango (caja de 2 tiles en cada eje,
  no distancia euclídea) y el loot-lock (dueño/grupo del dueño hasta que venza la mitad del tiempo de
  vida del item, igual que `m_LootTime` del original). `result`: `0xFF`=falló, `0xFE`=dinero (reusa
  el mismo struct que `MoneySend`), `0-234ish`=slot de inventario (éxito) — el caso `0xFD` ("se apiló
  con un item existente") NO está portado porque este puerto no tiene apilado de items.
- **Tirar** (`PMSG_ITEM_DROP_RECV/SEND`, `C1:0x23`): puerto de `CGItemDropRecv`
  (`ItemManager.cpp:3530-3718`), valida rango de slot, que el tile destino no esté bloqueado
  (`CMap::IsBlocked`) y que el mapa tenga un slot de piso libre; si el item tirado estaba equipado,
  recalcula `CharSet`/stats de combate y refresca la apariencia a los observadores (mismo camino que
  `OnItemMoveAsync`).
- **Drop al morir un monstruo**: puerto MUY simplificado de `gObjMonsterDieGiveItem`
  (`Monster.cpp:54-284`). El original es una cascada de ~13 subsistemas (`ItemBagManager` por clase
  de monstruo, drop-tables de boss/evento, drop-events programados, set-item aleatorio, etc.); esta
  pasada solo porta el camino "genérico" que cubre la mayoría de monstruos de campo: un roll de item
  (`Monster.ItemRate`, reusando `ItemBalanceTable.PickRandomDropItem` — mismos datos `DropItem`/
  `Level` de `Item.txt` que ya carga el balance de Fase 4) o, si no salió item, un roll de dinero
  (`Monster.MoneyRate`, monto derivado de la misma fórmula de nivel que ya usa el reparto de
  experiencia, ya que `lpMonster->Money` real es un subproducto dinámico del cálculo de exp, no una
  columna estática). Ambos rates se interpretan como "1 en N" (`Rng.Next(rate)==0`).
- **Fuera de esta pasada** (documentado, no portado): `ItemBagManager` (drops especiales por
  clase/evento), drop-tables de boss, sets aleatorios, opciones aleatorias de nivel/excelente/socket
  en el item dropeado (sale "de fábrica"), reparto de dinero de grupo completo (`gServerInfo.
  m_PartyMoneyDistribute`, config de servidor no portada — quien junta la plata se la queda entero),
  apilado de flechas/pociones existentes (`InventoryInsertItemStack`), reglas anti-dupe/anti-scam de
  nivel alto al tirar (bloquear +5/+6 no-alas/excelente/set/JewelOfHarmony — no relevante todavía
  porque este puerto no genera esos items), items especiales con efecto propio al tirar (Siege
  Summon, Life Stone, Lost Map, etc.).

Probado de punta a punta (`tests/gameserver_grounditem_e2e_test.py`, entorno dedicado con 2
monstruos de prueba deterministas — uno con `ItemRate=1` y otro con `MoneyRate=1` — además del
Spider real compartido de Fase 4): matar al monstruo de item → aparece en el piso con el índice/
posición/`ItemInfo` correctos y visible para ambos jugadores → un segundo jugador (no dueño) no
puede recogerlo mientras el loot-lock esté vigente → el dueño sí puede → recoger el mismo índice de
nuevo falla → tirarlo de vuelta al piso y recogerlo otra vez funciona → el segundo jugador ve el
`ViewportItemDestroy` al tick siguiente → matar al monstruo de dinero → recoger da `result=0xFE` y
el dinero total sube. De paso, corriendo la regresión completa se encontró y arregló un bug
preexistente (de la migración de formato de item de la sección anterior, no de este trabajo) en
`tests/gameserver_fase6_e2e_test.py`: el ticket "Devil's Invitation" se sembraba con el índice viejo
de 32 franjas (467) en vez del real de 512 franjas (`GET_ITEM(14,19)=7187`), y el helper Python
`item_bytes()` no escribía los bits 9-12 del índice en el byte9 (necesarios para cualquier item de
sección≥8 con el stride nuevo) — corregido, la Fase 6 vuelve a pasar limpia. Regresión completa
(fase1/fase4/fase5/fase6/skills/shop/grounditem) sin fallos.

### GameServer — Fase 3 (continuación): `CServerInfo` real (Common.dat/Event.dat, parcial)

Puerto PARCIAL de `CServerInfo` (`ServerInfo.h`/`.cpp`, ~520 campos repartidos en 7 archivos INI
reales — `GameServerInfo - {ChaosMix,Command,Common,Custom,Event,Item,Skill}.dat`, todos texto plano
pese a la extensión `.dat`, leídos con `GetPrivateProfileInt`/`String` igual que nuestro `IniFile`).
Investigado a fondo cada `Read*Info()` de `ServerInfo.cpp` (incluyendo el hallazgo de que
`Common.dat` en realidad lo parsean TRES funciones distintas — `ReadStartupInfo`, `ReadCommonInfo` y
`ReadHackInfo`, ninguna de las cuales corresponde 1:1 con el nombre del archivo), pero solo se
implementó (`Config/ServerInfoConfig.cs`) lo que tiene un consumidor real ya portado en este proyecto:

- **Fórmula de experiencia real** (`ExperienceMultiplierConstA=10`/`ConstB=1000`, puerto exacto de
  `gObjSetExperienceTable`, `User.cpp:276-297`): `WorldPacketBuilder.NextExperience` pasó de un
  placeholder `nivel²*1000` a `(n+9)*n*n*ConstA` (más un término extra con `ConstB` para niveles por
  encima de 255) — cambia la curva de experiencia real que ve el cliente (barra de exp, subida de
  nivel) para que coincida con el pack original.
- **`MaxLevel=400`**: reemplaza el tope de nivel hardcodeado en `ApplyExperienceGainAsync` (era el
  mismo valor por casualidad, ahora es configurable de verdad).
- **`ItemDropTime=30`/`MoneyDropTime=30`** (`GameServerInfo - Common.dat`): tiempo de vida de un item
  tirado en el piso. **Corregido un bug real**: el puerto tenía 60s hardcodeado (el doble de lo real)
  desde la Fase 3; ahora `GroundItemLifetime`/`GroundItemLootLock` en `ClientProtocolHandler` leen
  30s/15s (`ItemDropTime*500`ms, puerto exacto de `MapItem.cpp:29-99`) del config real.
- **`MonsterMaxLifeRate`/`DefenseRate`/`DefenseSuccessRateRate`/`PhysiDamageRate`/
  `AttackSuccessRateRate`** (todos =100 en el pack real, puerto de `MonsterManager.cpp:173-178`):
  `MonsterRegistry.SpawnAll`/`SpawnOne` ahora escalan `MaxLife`/`Defense`/`DefenseSuccessRate`/
  `DamageMin`/`DamageMax`/`AttackSuccessRate` por estos rates al spawnear. Con los valores reales
  (100=sin cambio) esto es un no-op hoy, pero el mecanismo de escalado en sí no estaba enchufado
  antes — un operador que suba `MonsterMaxLifeRate` a 150 en su `.dat` ahora sí se refleja.
- **`AddExperienceRate_AL0=1`** (puerto de `CharacterCalcExperienceAlone`, `ObjectManager.cpp:845`,
  multiplicador DIRECTO no-porcentual): enchufado en `GrantExperienceAsync`. Con el valor real (1) es
  no-op.
- **`DevilSquareMaxUser=15`** (`GameServerInfo - Event.dat`): **corregido un bug real**, el puerto
  tenía un tope arbitrario de 50 participantes por bracket desde la Fase 6; ahora usa el valor real.

Todos los `_AL0-3` (por "AccountLevel", nivel de cuenta/VIP 0-3) siempre usan el índice 0 porque este
puerto no trackea AccountLevel por jugador todavía (misma simplificación que `MaxStatPoint_AL0` de
Fase 8). **Explícitamente fuera de esta pasada** (documentado con detalle en el doc-comment de
`ServerInfoConfig.cs`) porque el sistema que lo consumiría no existe en este puerto: los ~90 campos de
`ChaosMix.dat` (mezcla de items/alas/Dinorant/Fruit/mascotas), los ~90 de `Command.dat` (`/reset` y
`/masterreset`), `Custom.dat` completo (Custom Arena/Attack/Pick — ni siquiera son `CServerInfo`, los
leen 3 clases propias de SSeMU), `Item.dat` completo (Transformation Ring, daño de items especiales,
tasas de poción por clase), `Skill.dat` completo (Mana Shield), y de `Common.dat`/`Event.dat` todo lo
de PK/Trade/PersonalShop/Duel/Guild/Jewel/Fruit/Quest, Blood Castle/Chaos Castle/Bonus Manager/Drop
Event/Invasion Manager, y `PartyGeneralExperience`/`PartySpecialExperience`/`PartyMaxGapLevel` (el
reparto de experiencia de grupo de este puerto usa una fórmula estructuralmente distinta a la real de
`CharacterCalcExperienceParty` — reconciliarla es trabajo aparte, no alcanza con enchufar esas 3
constantes).

Cargado en `Program.cs` (`ServerInfoConfig.Load(config.ServerInfoCommonPath, config.ServerInfoEventPath)`)
antes de spawnear monstruos, y expuesto vía `WorldPacketBuilder.ServerInfo` (propiedad estática
settable, con una instancia de defaults de fábrica si nunca se setea) para que `ClientProtocolHandler`,
`MonsterRegistry` y `DevilSquareManager` lean el mismo valor sin necesidad de inyectarlo por
constructor en cada uno. Si `GameServer.ini` no define `ServerInfoCommonPath`/`ServerInfoEventPath`,
o si el archivo apuntado no existe, `IniFile.Load` devuelve un INI vacío y cada `GetInt` cae en su
default — que son los mismos valores reales documentados arriba, así que un despliegue sin copiar
`GameServerInfo - Common.dat`/`Event.dat` igual se comporta como el pack original.

Regresión completa (fase1/fase4/fase5/fase6/skills/shop/grounditem) corrida de nuevo tras este cambio,
sin fallos — incluida la Fase 6 (Devil Square), que ejercita tanto la fórmula de experiencia nueva
(subida de nivel al matar) como `DevilSquareMaxUser`.

### GameServer — la tienda cobraba un precio y el cliente mostraba otro

El pack trae `Data/Item/ItemValue.txt`: 72 precios explícitos —joyas, entradas de evento, pociones
de asedio, flechas por nivel— que en 0.99B pisan el resultado de la fórmula general de
`CItem::Value()`. **Nadie lo abría**: el nombre no aparecía en ningún `.cs` ni en el `.ini`. Todos
esos objetos se cotizaban con la fórmula general, que para ellos da cualquier cosa.

| | declara `ItemValue.txt` | cobraba el servidor |
|---|---|---|
| Jewel of Bless | 9.000.000 | 18.700 |
| Jewel of Life | 45.000.000 | 72.600 |
| Fruits | 33.000.000 | 100 |
| Jewel of Chaos | 810.000 | 40.082.300 |

Las 72 filas estaban mal, ninguna coincidía. Vender una Bless pagaba 6.200 en vez de 3.000.000. Y no
era una discrepancia interna nada más: el cliente **sí** tiene los valores correctos (los trae
escritos a mano en `ItemValue`, ZzzInfomation.cpp), así que en la tienda se veía un número y se
cobraba otro.

Arreglado con `World/ItemValue.cs`, una tabla nueva que se carga en `Program.cs` y que
`ComputeShopBuyPrice`/`ComputeShopSellPrice` consultan **entre `BuyMoney` y la fórmula general**.
Ese orden importa en las dos direcciones: ninguna de las 72 filas corresponde a un item con
`BuyMoney` distinto de 0, así que el precio de alas y orbes —que ya estaba bien— no se toca; y cinco
filas (las dos Siege Potion, Ale, Bless y Soul) apuntan a items que además traen la columna `Value`,
donde es el archivo el que tiene el número bueno.

Detalles que conviene saber:

- **Nivel y grado.** Una fila puede fijar el nivel `+0..+15`, el grado, los dos o ninguno (`*` =
  cualquiera), y entre varias que apliquen gana la más específica. Sin ese orden, un Horn of
  Dinorant sin opciones tomaría el precio de cualquiera de sus siete filas según el orden del
  archivo. El grado es la máscara de opciones especiales: hoy lo usa sólo el Dinorant, cuyas tres
  opciones suman 300.000 cada una (960.000 / 1.260.000 / 1.560.000), que es exactamente lo que
  declaran esas siete filas.
- **Lo que este puerto no hace.** El original escala algunos de estos precios por la cantidad
  apilada o por la durabilidad restante, y cada item a su manera. El archivo no dice cuáles, así que
  se devuelve el valor tal cual: un stack se cotiza como una unidad. Bajo, pero del orden correcto,
  contra los factores de 300x a 6000x de antes.
- **El test afirmaba el precio equivocado.** `gameserver_shop_e2e_test.py` daba por bueno que la
  Bless se compra a 18.700 — que era exactamente lo que devolvía la implementación. Estaba escrito
  contra el código y no contra la fuente, el mismo error que ya nos costó caro con el serial de 17
  bytes. Ahora comprueba que vender pague 3.000.000, con el comentario diciendo de qué fila del
  archivo sale.

Del lado del cliente quedó `tools/gamedata/audit_shop_prices.py` (en el repo de MuMain) como
guardia: comprueba que las 72 filas apunten a items reales, que ninguna quede tapada por un
`BuyMoney`, y cuánto costaría que la tabla volviera a desconectarse.

### GameServer — preguntar la tasa de la Chaos Box cobraba como si ya hubieras combinado

El wire de 0.99B separa preguntar de combinar: `0x88` (`PMSG_CHAOS_MIX_RATE_RECV`/`_SEND`) pregunta
la tasa de éxito y el zen requerido para lo que hay en la Chaos Box, sin tocar nada; `0x86`
(`PMSG_CHAOS_MIX_RECV`/`_SEND`) combina de verdad. `OnChaosMixRateAsync` (0x88) llamaba a
`ChaosMixLogic.CalculateAndExecuteMix` — la misma función que usa `OnChaosMixRecvAsync` (0x86) para
combinar de verdad. Cada consulta de tasa cobraba el zen, vaciaba la Chaos Box y tiraba el dado,
exactamente como si el jugador hubiera confirmado combinar.

Ningún cliente de este repo dispara hoy ese camino — `MuMain` no manda `0x88` todavía, su ventana de
combinar es la de otra temporada y no está conectada a ningún archivo de 0.99B (ver la nota en
`docs/protocolo-099b.es.md` de MuMain) — pero el bug es real e independiente de eso: cualquier cliente
0.99B que sí pregunte antes de combinar, que es el flujo normal, habría perdido los items y el zen
sin haber aceptado nada.

Arreglado separando calcular de ejecutar. `ChaosMixLogic.CalculateAndExecuteMix` toma un parámetro
`execute` (default `true`, para no tocar el único call site que sí debe mutar); las seis fórmulas de
combinación calculan `(rate, zen)` igual en los dos casos, y sólo cobran/vacían la caja/tiran el dado
cuando `execute: true`. `OnChaosMixRateAsync` pasa `execute: false`; `OnChaosMixRecvAsync` sigue
usando el default. Verificado con un arnés aparte (`ChaosItem` con y sin `execute`): la consulta da
la misma tasa que la ejecución real, no cambia el dinero, no vacía la caja y no tira el dado; con la
caja vacía las dos devuelven `(0, 0)` sin tocar nada; sin plata la consulta sigue calculando la tasa
en vez de fallar (correcto: preguntar no debería depender de poder pagar).

### GameServer — las fórmulas de la Chaos Box eran inventadas, no un puerto

El doc-comment de `ChaosMixLogic.cs` decía "puerto exacto de las fórmulas de combinaciones de
`CChaosBox` (ChaosBox.cpp:1-1670)". No lo era. Comparado contra el código fuente real del emulador
—que está en el repo, en `Source/Source/Emulator 0.99 (2.1.7)/GameServer/ChaosBox.cpp`, y no se
había leído hasta ahora para este sistema— la tasa de éxito de la mezcla de armas del caos salía de
`10 + Σ(nivel×5 + Option3×2)`, una fórmula sin relación con el original, que la saca de una tabla de
configuración por nivel de cuenta (`gServerInfo.m_ChaosItemMixRate[AccountLevel]`, con `-1` como
"usar `valorTotal/20000`"). El item de éxito salía de un array de tres armas fijas en vez de la lista
real de `Data/EventItemBag/Special/*.txt`. Y **`GameServerInfoChaosMix`, la clase que ya carga los 19
campos de esa tabla, estaba cargada y sin usar** desde que se escribió (`Program.cs` la leía en una
variable local, `gsiChaosMix`, y ahí se quedaba) — exactamente el mismo patrón de "cobertura completa
cargada, cero sistemas conectados" que atraviesa este puerto.

Leer la fuente real destapó además una trampa de nombres genuina: en `ChaosBox.h`, la constante
`CHAOS_MIX_WING1` (el valor `7` que viaja por el wire) dispara la función **`Wing2Mix(tipo=0)`**, y
`CHAOS_MIX_WING2` (`11`) dispara **`Wing1Mix()`** — están cruzadas. El código anterior tenía los
ingredientes de esas dos mezclas invertidos por seguir el nombre de la constante en vez de mirar qué
función dispara de verdad.

Reescrito contra la fuente real para las seis combinaciones que un personaje de este build puede
alcanzar (Chaos Item, Plus Item +9→+10 y +10→+11, Fruit, y las dos de ala — sumando además el tipo
"Cape", que faltaba por completo): ingredientes, fórmula de tasa, fórmula de zen y de qué campo de
`GameServerInfoChaosMix`/`GameServerInfoCommon` sale cada una, verificado línea por línea. Devil
Square, Dinorant, Blood Castle y las dos mezclas de mascota siguen sin portar — necesitan estado de
eventos en vivo que este servidor no trackea, y no son alcanzables hoy de todas formas.

Dos límites, documentados en el doc-comment de la clase para que no se pierdan:

- **El item de éxito no usa el motor completo de `ItemBagEx`.** Se elige al azar entre los
  candidatos reales de cada archivo (verificados contra `Data/EventItemBag/Special/*.txt`: las tres
  armas del caos, las alas de cada generación, el Cape of Lord), pero sin el sistema de pesos por
  sección/filtro por clase del original. Y hay un motivo más fuerte que "no daba el tiempo": los
  cuatro archivos que hacían falta traen `DropRate=0` de fábrica, lo que en el motor real haría que
  la bolsa **nunca** devuelva un item aun ganando la tirada de éxito — replicarlo literal habría
  dejado la Chaos Box "gana la tirada, no pasa nada". `ItemBagEx` es un sistema compartido con Devil
  Square, Blood Castle y los drops de monstruo; merece su propio puerto, no uno apurado como
  dependencia de esto.
- **No hay sistema de nivel de cuenta.** Las fórmulas reales indexan por `AccountLevel` (0-3,
  premium/VIP). Se agregó `PlayerObject.AccountLevel`, fijo en 0 (la fila base) para todos los
  jugadores — no se inventó un sistema de cuentas que no existe.

La entrega del item también cambió: el original manda los items NUEVOS (Chaos Item, Fruit, las alas)
por `GDCreateItemSend` — un mensaje al DataServer, no el mismo paquete de la mezcla — y sólo embebe el
item en `PMSG_CHAOS_MIX_SEND` cuando es un item que YA TENÍAS y se mejoró (Plus Item Level). El puerto
sigue esa misma distinción: los items nuevos se colocan en un hueco vacío del inventario y se avisan
por `ItemMoveSend` (el mismo camino que ya usa el comando de depuración `item`); el resultado embebido
sólo se usa para la mejora in-place.

Verificado con un arnés aparte que carga los `.dat`/`.txt` reales y ejercita las seis fórmulas: 21
aserciones — la tasa configurada se respeta exacta (60% para +9→+10, 90% para Fruit), el bono de
`AddLuckSuccessRate2` se suma cuando corresponde, +10→+11 exige 2 Bless y 2 Soul (no 1), Wing1(7) exige
Feather de nivel 0 y Wing2(11) exige un arma del caos (no al revés), Cape(24) exige el Crest de nivel
1 del mismo item y no el Feather de nivel 0, una ejecución real sube el nivel del item y cobra
exactamente el zen esperado.

### MuMain — dos bugs de despacho más, y la ventana que abre pero no combina todavía

Del lado del cliente (repo de MuMain) se encontraron y corrigieron dos bugs por la misma causa
recurrente: confundir el subcódigo de un paquete con un concepto de otra temporada.

- `0x31` subcódigo 3 no es "la mezcla terminó" (lo que el receptor asumía, limpiando la ventana con
  sonidos de éxito/rotura cada vez que llegaba) — es la lista de lo que hay ahora en la Chaos Box,
  mandada al abrir la ventana. Subcódigo 5 tampoco es "resurrección fallida" — es la misma lista para
  la ventana del Entrenador/mascotas, no portada.
- El receptor genérico de mover ítems (`0x24`) no miraba el campo `result` (que dice a qué
  contenedor fue el item) para nada del lado de la Chaos Box, así que un item movido ahí quedaba
  dibujado en el inventario normal del cliente aunque el servidor sí lo hubiera movido.

Con eso arreglado la ventana abre, sincroniza lo que ya había en la caja, y refleja los items
arrastrados — pero el botón de combinar sigue sin funcionar: hace falta decidir qué tipo de mezcla
corresponde al contenido de la caja (en el 0.99B real esto lo elegía el jugador con una pestaña,
información que no está en ninguna fuente disponible acá) y pedir la tasa por `0x88` antes de
confirmar. Esa pieza queda pendiente, documentada en `docs/protocolo-099b.es.md` de MuMain, porque
necesita probarse contra el cliente real corriendo.

### Los monstruos de área se teletransportaban todos a la esquina de su rectángulo

Probando con el cliente real, los monstruos "aparecían y desaparecían caminando en bucle". El log del
cliente lo mostraba clarísimo: todos los spiders se anunciaban (`0x13`) en **exactamente la misma
casilla, (183,93)**, y después caminaban en un cuadradito de (180-183, 90-93).

El paseo pasivo del monstruo hacía esto:

```csharp
int spawnX = monster.SpawnEntry.X;          // spawn de área -> ESQUINA del rectángulo
int dist = Math.Max(1, monster.SpawnEntry.Dis);
if (dist > 4) dist = 3;                     // radio real (30) recortado a 3
int newX = Math.Clamp(monster.X + rnd, spawnX - dist, spawnX + dist);
```

En los spawns de área (Type 1), `SpawnEntry.X/Y` no es la posición de ese monstruo: es la esquina del
rectángulo que **comparten todos los monstruos de esa fila** (en Lorencia, 45 spiders y 40 budge
dragons sobre (180,90)-(226,244)). `ResolvePosition` los repartía bien por todo el área al arrancar,
pero en cuanto a uno le tocaba su primer tick de paseo, `Clamp(200, 177, 183)` lo mandaba de golpe a
183. Los 85 monstruos terminaban amontonados en un cuadrado de 7x7 sobre la esquina — y como el salto
era instantáneo, entraban y salían del viewport del jugador de a uno, que es el "bucle" que se veía.

El original hace otra cosa (`gObjMonsterMoveCheck`, Monster.cpp:430-462): cada monstruo guarda
**dónde apareció él** (`StartX`/`StartY`, seteado en Monster.cpp:199,419) y sólo se acepta el paso si
la distancia euclídea de la casilla nueva a ese punto no supera su `Dis`. Nunca se lo reubica: un paso
inválido simplemente no se da.

Ahora `Monster` tiene `StartX`/`StartY` (seteados en los cuatro caminos de spawn: el inicial, el
respawn, los NPC de tienda y el spawn dinámico de eventos) y el paseo es un paso de una casilla
validado contra ese punto con el `Dis` real, sin recortes.

### Un personaje que moría y se desconectaba quedaba en un limbo: 0 de vida para siempre

Probando con el cliente real aparecieron dos síntomas: no se veían los NPCs, y al monstruo no se le
podía pegar ni él atacaba. El segundo resultó ser un bug de verdad, y bastante malo.

El personaje estaba guardado en la base con `life = 0` (y `max_life = 110`). Con la vida en cero:

- **Ningún monstruo lo ataca**: la IA filtra los jugadores por `Life > 0` tanto al armar la lista como
  al elegir objetivo, así que el personaje era invisible para todos los mobs.
- **Y él tampoco puede pelear**, porque el cliente lo trata como muerto.

Lo que faltaba es el rescate que sí hace el original en `gObjSetCharacter`
(ObjectManager.cpp:2739-2745): si un personaje entra al mundo con `Life == 0`, lo pasa a
`OBJECT_DYING` con `DieRegen`, o sea que lo manda al flujo normal de revivir. Este puerto cargaba el
cero y dejaba al personaje "vivo pero en cero", sin ninguna forma de salir: `RespawnDyingPlayersAsync`
sólo mira `IsDying`, que es estado de runtime y se pierde al desconectarse. Así que morir y salir del
juego antes de reaparecer dejaba el personaje inservible de forma permanente.

Ahora, al entrar al mundo con la vida en cero se marca como muriendo y el respawn de siempre lo revive
en el punto de reaparición.

Los NPCs, en cambio, **no eran un bug**: el personaje estaba parado en (182,92), dentro del área de
spawn de Spider/Budge Dragon, y el NPC más cercano de Lorencia está a 28 casillas con un rango de
visión de 12. Por eso se veían mobs y ningún NPC. El pueblo está en x≈115-135, y≈110-145.

### El zen del trade viajaba en el opcode equivocado (0x3B en vez de 0x3A)

Comparando el `switch` de `Protocol.cpp` del emulador contra el dispatcher de este puerto aparecieron
21 opcodes que el original maneja y acá no (Guild, correo entre amigos, duelo, Golden Archer, etc. —
sistemas enteros sin portar). Pero uno de los que **sí** estaban portados lo estaba mal.

El original despacha `CGTradeMoneyRecv` en **0x3A** (Protocol.cpp:124), y el header que `protogen`
genera desde esas mismas fuentes lo confirma (`PMSG_TRADE_MONEY_RECV::kHead == 0x3A`). Pero el struct
en `Trade.h` del emulador lleva un comentario equivocado que dice `// C1:3B`, y ese 3B se transcribió
a mano **en los dos lados**: en este servidor (`case 0x3B`) y en el builder del cliente
(`Wire099B.cpp`, con el head escrito como literal en vez de tomarlo del struct generado).

El resultado es que cliente y servidor de este proyecto se entendían entre ellos, así que el bug no se
veía, pero ninguno de los dos hablaba el protocolo real: un cliente 0.99B de verdad manda 0x3A y acá
se le habría descartado en silencio, dejando el zen del intercambio en cero.

Ahora el servidor acepta **0x3A y 0x3B** (el segundo por compatibilidad con los clientes ya compilados
con el valor viejo) y el cliente toma el head del struct generado. Es exactamente el caso que la regla
de "el wire se genera, no se transcribe" existe para evitar.

**Seguimiento (corrido el suite completo de `ctest` para verificar sin pedir una prueba en vivo):** el
test del lado del cliente que cubre justo este paquete (`test_npc_shop_wire.cpp`, "El dinero del trade
y del baul viaja en big-endian") se había quedado con el valor viejo -- seguía comprobando
`trade.Data[2] == 0x3B` en vez de `0x3A`, así que fallaba contra el código ya arreglado. El código
estaba bien (confirmado leyendo `Wire099B.cpp:474`, que sí toma `PMSG_TRADE_MONEY_RECV::kHead`); lo
que estaba desactualizado era la aserción del test, no el fix. Corregido; el suite completo vuelve a
dar 152/152.

### GameServer — se perdían los ítems que quedaban en la Chaos Box

Mover un ítem del inventario a la Chaos Box lo **saca** del inventario: el original hace
`InventoryDelItem` en `MoveItemToChaosBoxFromInventory` (ItemManager.cpp:2381) y este puerto lo
replica. A partir de ahí el ítem vive sólo en `ChaosBoxItems`, y había dos caminos que lo borraban:

- **Cerrar la ventana con el 0x31 genérico** (`CGNpcTalkCloseRecv`) ponía `InChaosBox = false` pero no
  tocaba la caja, así que el ítem quedaba flotando fuera del inventario. El 0x87 específico de la
  Chaos Box (`OnChaosMixCloseAsync`) sí lo devolvía, pero no todos los caminos pasan por ahí.
- **Volver a hablar con el Chaos Goblin** llamaba a `ClearChaosBox()`, que vacía la caja de una: si
  había quedado algo del paso anterior, ahí se perdía definitivamente. Eso además no es lo que hace el
  original: `NpcChaosGoblin` (NpcTalk.cpp:165-187) no limpia nada al abrir — los ítems siguen en la
  caja, porque allá la caja se persiste.

Ahora los tres caminos (0x31, 0x87 y abrir la máquina) pasan por el mismo helper, que devuelve lo que
haya al inventario y sólo entonces vacía la caja. Si el inventario está lleno el ítem se queda en la
caja en vez de descartarse. Es una desviación consciente del original —allá los ítems se quedan
adentro al cerrar— y el motivo es que este puerto todavía no persiste la Chaos Box en el DataServer:
dejarlos ahí los perdería igual en cuanto el jugador se desconectara.

De paso se portó el guard de `NpcWarehouse` (NpcTalk.cpp:189-198): el baúl no abre si hay ítems en la
Chaos Box, que es lo que impide tener el mismo ítem contado en dos lados a la vez.

### GameServer — `MaxLevelUp` cargado pero nunca aplicado: un solo kill podía subir cientos de niveles

Encontrado probando con la experiencia puesta en 9999x para ver qué pasaba al subir de nivel:
`ApplyExperienceGainAsync` sumaba toda la experiencia ganada y subía de nivel en un `while` sin límite
mientras alcanzara. El original (`CObjectManager::CharacterLevelUp`, ObjectManager.cpp:983-1041) tiene
un tope explícito, `gServerInfo.m_MaxLevelUp` (`MaxLevelUp` en `Common.dat`, 1 de fábrica): un solo
evento de experiencia (una muerte) sube como máximo esa cantidad de niveles, y **lo que sobre de
experiencia se descarta** — no queda guardado para el próximo kill
(`AddExperience -= (((--MaxLevelUp)==0)?AddExperience:(NextExperience-Experience))`). El campo ya se
leía (`GameServerInfoCommon`, para el panel) pero `ServerInfoConfig` — la config que de verdad consume
el GameServer — no lo cargaba, así que no había ningún tope: con la tasa alta, un kill llevaba al
personaje de nivel 1 a nivel 400 de un solo golpe.

Se agregó `MaxLevelUp` a `ServerInfoConfig` y se re-escribió el bucle de nivel para que sea un puerto
exacto del original, incluyendo el descarte de sobrante. De paso, otra diferencia visible que apareció
mirando la misma función: si hay level-up, el paquete `MonsterDieSend` debe mandar experiencia **0**
en el popup (el aviso real lo da el paquete de subida de nivel aparte) — el puerto mandaba siempre la
experiencia real ganada, así que se veían dos números superpuestos en pantalla.

### MuMain — subir de nivel no mostraba el aura dorada

Otro reporte de la misma sesión de pruebas. `ReceiveLevelUp099B` (el receptor de `PMSG_LEVEL_UP_SEND`
que está enchufado para 0.99B) copiaba las stats nuevas pero nunca disparaba el efecto visual ni el
sonido — el receptor del dialecto posterior si lo hace (`CreateJoint(BITMAP_FLARE, ...)` x15 + un
`BITMAP_MAGIC`, o x20 si la clase ya es de segunda evolución, más `SOUND_LEVEL_UP`), pero no está
conectado para 0.99B. Se portó el mismo bloque al final de `ReceiveLevelUp099B`. Detalle completo en
[`protocolo-099b.es.md`](../../MuMain-099B/docs/protocolo-099b.es.md).

### MuMain — recoger items del piso se trababa después del primer intento

También de la misma sesión: el zen se podía recoger una sola vez, y después ningún item más (ni zen ni
objetos) se podía levantar. `ReceiveGetItem099B` nunca reseteaba la bandera `SendGetItem` que bloquea
mandar un pedido de recoger mientras se espera la respuesta del anterior — el propio código ya tenía
un comentario prediciendo el bug (`WSclient.cpp:428`). Se portó el reset que sí tenía el receptor
viejo, agregado a las cuatro salidas de la versión 099B. Detalle completo en
[`protocolo-099b.es.md`](../../MuMain-099B/docs/protocolo-099b.es.md).

### GameServer — `AccountLevel` (VIP) se calculaba bien en JoinServer y se tiraba en GameServer

Encontrado revisando por qué las tasas `_AL0`-`_AL3` (experiencia, drop de zen, tope de stats, y las
cuatro de Chaos Mix) siempre se comportaban como si todas las cuentas fueran nivel 0: JoinServer ya
tiene el sistema completo (`AccountRepository.GetAccountLevelAsync`, puerto real de
`WZ_GetAccountLevel` con expiración) y se lo manda a GameServer en el propio paquete de login
(`ConnectAccountSend`, campo `AccountLevel`) — pero `OnJoinAccountResultAsync` sólo miraba
`msg.Result` y tiraba el resto del mensaje. `PlayerObject.AccountLevel` ni siquiera era un campo: era
una propiedad que devolvía `0` fijo.

Se agregó `ClientSession.AccountLevel` (se llena en `OnJoinAccountResultAsync`, porque el valor llega
en el login, antes de que exista el `PlayerObject` — todavía no se eligió personaje) y
`PlayerObject.AccountLevel` pasó a copiarlo al entrar al mundo. Con el campo ya real, se corrigieron
los tres lugares que indexaban `[0]` a mano en vez de por el jugador: experiencia ganada por kill,
tasa de dinero tirado por monstruos, y el tope de puntos de stat al repartir level-up. El sistema de
Chaos Mix (`ChaosMixLogic`) ya leía `player.AccountLevel` desde antes — sólo estaba esperando a que el
valor dejara de ser siempre 0, así que las cuatro tasas de mezcla por nivel de cuenta empiezan a
funcionar de punta a punta sin tocar ese archivo.

Lo que sigue sin portar, a propósito, porque es una feature aparte (no un problema de wiring): el
filtro de qué NPCs son visibles según el nivel de cuenta del jugador (`Shop.cs`, columnas AL0-AL3 del
script de NPCs) y las restricciones de acceso a tienda por PK/GM level (`CheckShopAccountLevel` del
original, ver `OnNpcTalkAsync`).

### GameServer — `MaxStatPoint_AL0-3` se leía del archivo equivocado

Encontrado revisando la corrección de `AccountLevel` de arriba, buscando si había más lugares con el
mismo problema. `CharacterBalanceConfig.MaxStatPoint` (el tope de una stat individual al repartir
puntos de level-up, indexado por `AccountLevel`) tenía un doc-comment que decía la verdad a medias:
"vive en Common.dat, no en Character.dat" -- pero el código que lo leía (`Load`) usaba el `ini` de
**Character.dat** de todas formas (`config.CharacterInfoPath`, el único archivo que ese método recibía)
para buscar una clave que sólo existe en Common.dat. La búsqueda nunca encontraba nada y caía siempre
al default hardcodeado (65000) para las 4 franjas, sin importar lo que el panel escribiera en
`GameServerInfo - Common.dat`. No se notaba porque el valor de fábrica shippeado en Common.dat
también es 65000 para las 4 -- pero ya no hay forma de dar más margen de stats a una cuenta VIP vía
config, va a leer siempre 65000 pase lo que pase.

Se cambió la firma de `CharacterBalanceConfig.Load` para recibir también la ruta de Common.dat
(`config.ServerInfoCommonPath`, ya cargado en Program.cs para `ServerInfoConfig`/`GameServerInfoCommon`
en el mismo punto de arranque) y leer `MaxStatPoint_AL{0..3}` de ahí. Un solo call site, sin
receptores que migrar.

### GameServer — `0x1B` (Skill Cancel) agregado como no-op fiel, no como placeholder

De la lista de opcodes sin manejar: `0x1B` (`CGSkillCancelRecv`, SkillManager.cpp:2591-2601) en el
original cancela un efecto activo del skill vía `gEffectManager.DelEffect`. Este puerto no tiene
gestor de efectos (los duration-skills de `0x1E` son sólo la animación/proyectil, sin daño-en-el-tiempo
persistente que trackear), así que no hay nada que cancelar todavía -- se agregó el `case` explícito
como no-op para que no ensucie el log con "no implementado" por un paquete que, dado el alcance actual,
ya está correctamente atendido. El día que se porte un gestor de efectos real, este es el punto donde
engancha la cancelación.

### Lo que falta y por qué no se portó en esta pasada: Guild, Duel, Personal Shop, Golden Archer, Pet, Teleport Ally

De los ~13 opcodes restantes sin implementar, los reservé todos para una pasada aparte en vez de
portarlos ahora, y no por tamaño solamente:

- **Guild, Duel, Personal Shop y Teleport Ally requieren una SEGUNDA cuenta conectada** para probarse
  de verdad (gremio con más de un miembro, duelo 1v1, comprarle a la tienda de otro jugador,
  teletransportar a un compañero de grupo) -- con un solo personaje de prueba no hay forma de verificar
  que el flujo completo funciona antes de entregarlo.
- **Guild** (`Guild.cpp`+`GuildManager.cpp`, ~1600 líneas) además necesita esquema de base de datos
  nuevo (tabla de gremios, miembros, rangos, marca) -- un cambio de esquema no es algo para meter de
  paso en una pasada de compatibilidad sin planearlo.
- **Golden Archer** es viable en solitario (es una máquina expendedora contra un NPC), pero también
  necesita una columna nueva en la base de datos para el contador persistente por cuenta
  (`GDGoldenArcherAddCountSaveSend`) -- mismo motivo que Guild, en menor escala.
- **Pet** (ítems mascota con IA propia, nivel, hambre) es un sistema de juego completo aparte, no una
  función; ni siquiera está el registro de mascotas vivas que el resto de esto asumiría.

Si en algún momento hay una segunda cuenta para probar en pareja, Duel es el más chico y autocontenido
de los cuatro multiplayer (una máquina de estados sobre el jugador mismo, sin mapa de arena ni gremio
real involucrado) y el candidato lógico para ir primero.

### AdminPanel — panel de administración web (Blazor Server + MudBlazor)

`src/MuServer.AdminPanel` es un panel web para configurar el juego sin editar los archivos de `Data/`
a mano, en la línea de lo que ofrece el panel de OpenMU. Decisión de diseño central: **el panel lee y
escribe los archivos reales de `Data/`, no una copia ni una base de datos intermedia.** No hay
importación ni sincronización que se pueda desincronizar — lo que se ve en el panel es literalmente lo
que hay en el archivo, y lo que se guarda es lo que el GameServer va a leer.

Consecuencia importante y explícita en la UI: **el GameServer lee esos archivos una sola vez, al
arrancar**, así que cualquier cambio recién aplica cuando se lo reinicia.

Cada archivo se escribe de forma atómica (temporal + reemplazo) para que el servidor nunca pueda leer
un archivo a medio escribir, y **se preserva todo lo que el panel no edita**: comentarios, banners
`;====`, orden de las claves y espaciado quedan byte a byte iguales; sólo se reescribe la línea del
valor que realmente cambió. Cada página deja además un `.bak` la primera vez que se graba en la sesión.

Páginas:

- **Teletransportes** (`Data/Move/Move.txt`) — nombre, zen, nivel mínimo/máximo y puerta de cada
  destino. Es el mismo archivo que usan cliente y servidor, así que un cambio acá no los desincroniza.
- **Items** (`Data/Item/Item.txt`) — las 16 secciones del archivo (armas, escudos, armaduras, alas,
  joyas, orbes...), cada una con su propio layout de columnas. La grilla muestra sólo las columnas que
  aplican a la categoría elegida, porque el formato real es distinto por sección (las alas, por
  ejemplo, no tienen `SetAttr` y ordenan los requisitos distinto). Un detalle del archivo real que vale
  la pena saber: la sección 12 no es sólo "alas" — también trae orbes de combate y el Jewel of Chaos,
  compartiendo el layout de columnas de las alas, porque el parser original (`ItemManager.cpp`)
  despacha por número de sección y no por tipo de item.
- **Tasas** (`Data/GameServerInfo - Common.dat`) — el atajo a lo que se toca siempre: multiplicador de
  experiencia, probabilidad de drop, zen, tasas de joyas, puntos por nivel, multiplicadores globales de
  monstruo y durabilidad. Cada campo tiene nombre y explicación en castellano en vez del identificador
  crudo. Las claves que en el archivo están repetidas por nivel de cuenta (`_AL0`..`_AL3`) se muestran
  como un solo campo y se escriben en los cuatro. OJO: esto quedó desactualizado -- GameServer sí
  trackea `AccountLevel` por jugador desde que JoinServer empezó a mandarlo (ver la sección de
  "`AccountLevel` (VIP) se calculaba bien..." más arriba), así que hoy esas 4 tasas SÍ podrían diferir
  por nivel de cuenta y la página las está pisando a todas con el mismo valor. Pendiente: separar los 4
  campos en la UI para que el admin pueda dar tasas distintas por VIP.
- **Precios** (`Data/Item/ItemValue.txt`) — las 72 filas de precios explícitos que pisan la fórmula
  general (joyas, entradas de evento, pociones de asedio). Muestra el nombre real del item cruzando
  contra `Item.txt`.
- **Tiendas** (`Data/ShopManager.txt` + `Data/Shop/*.txt`) — qué vende cada NPC (con el nombre de cada
  item resuelto desde `Item.txt`, para no tener que descifrar `14,013`) y dónde está parado cada uno.
  Permite agregar y quitar items de cada tienda.
- **Spawns** (`Data/Monster/Spawn/*.txt`) — dónde aparece cada monstruo, un archivo por mapa. El
  archivo agrupa las filas en bloques con layouts distintos y la página los respeta: posición fija,
  área (que reparte una cantidad dentro de un rectángulo, y sólo ahí se muestran las columnas X2/Y2/
  cantidad), posición con desvío aleatorio, y las posiciones de evento que no se instancian al
  arrancar. Cada fila muestra el nombre del monstruo cruzando contra `MonsterList.txt`.
- **Skills, Puertas, Quests, Mensajes** (`Data/Skill/SkillList.txt`, `Data/Skill/SkillDamage.txt`,
  `Data/Move/Gate.txt`, `Data/Message.txt`, `Data/Quest/Quest.txt` + `QuestObjective.txt` +
  `QuestReward.txt`) — todas estas comparten la misma forma de archivo (cabecera de comentarios, una
  fila por línea, `end` al final), así que en vez de escribir siete repositorios casi iguales hay uno
  genérico (`FlatTableRepository`) y un catálogo (`FlatTableCatalog`) donde cada tabla sólo declara sus
  columnas. Agregar otra tabla de este tipo es agregar una entrada al catálogo, no escribir código
  nuevo. La sangría de las filas y los anchos de columna se toman del archivo real para que el
  resultado quede igual al original.
- **Monstruos** (`Data/Monster/MonsterList.txt`) — estadísticas de los 231 tipos de monstruo (nivel,
  vida, daño, defensa, acierto/evasión, rangos, velocidades, respawn, tasas de item/zen). Define *qué
  es* cada monstruo, no dónde aparece: los spawns están en `Data/Monster/Spawn/*.txt` y no se editan
  desde el panel. Los multiplicadores globales de `Monster Settings` (en Configuración) se aplican
  encima de estos valores al instanciar cada monstruo.
- **Chaos Machine** (`Data/GameServerInfo - ChaosMix.dat`) — las tasas de éxito por tipo de mezcla y
  por nivel de cuenta. Ojo: `-1` no es "0%", es "sin tasa fija" — esas mezclas (Caja del Caos y alas)
  calculan el porcentaje con una fórmula en base al dinero invertido, ver `ChaosMixLogic`.
- **Configuración** (los 8 `Data/GameServerInfo - *.dat`) — editor genérico de los ~845 campos. No hay
  una lista de campos mantenida a mano: se parsea el archivo y se agrupa por los banners de
  comentarios que el propio archivo ya trae, detectando por valor si el campo es numérico o de texto.
  Eso significa que sirve para los 8 archivos y que no se desactualiza si un `.dat` cambia. Tiene
  buscador por nombre de clave y un contador de cambios pendientes.
- **Eventos** (`Data/Event/*.dat`) — horarios, duraciones y recompensas de Devil Square, Blood Castle,
  Chaos Castle, Kalima, invasiones, bonus y drops de evento. Estos archivos son varias tablas numeradas
  dentro del mismo archivo, y **cada sección trae sus propios nombres de columna en un comentario**, así
  que el editor los lee de ahí en vez de tener un esquema escrito a mano: sirve para los 9 archivos sin
  una línea de código por archivo, y sigue andando si alguno cambia sus columnas. La página aclara que
  de estos eventos sólo Devil Square y Blood Castle están portados en el servidor.
- **Cuentas conectadas** — lista en vivo (cuenta, personaje, clase, nivel, reset, mapa) de sólo lectura.
- **Mensaje global** — manda un aviso a todos los jugadores conectados, como noticia dorada en pantalla
  o como mensaje azul en el chat.
- **Personajes** — a diferencia de todo lo anterior, esta página no toca `Data/`: el personaje no vive
  en un archivo, vive en la misma Postgres que usa DataServer, así que habla directo con la base
  (reusa `NpgsqlCharacterDataRepository`, el mismo repositorio que ya usa el DataServer real, en vez de
  reimplementar las consultas). Lista los personajes, y al elegir uno permite editar stats (clase,
  evolución, nivel, atributos, vida/maná/BP, posición, PK, frutas), el inventario completo (108 slots,
  equipo + mochila) y el baúl -- que es de la CUENTA, no del personaje, y lo muestra aparte con esa
  aclaración porque es fácil asumir lo contrario. Cada slot de item edita los mismos campos que
  <c>World.Item</c> (índice wire, nivel, durabilidad, suerte, habilidad, opción, excelente, set), y el
  nombre se resuelve en vivo contra `Item.txt` para poder verificar qué es cada índice sin adivinar.
  El serial del item (clave de `pet_item_info` para mascotas) se preserva SÓLO si el slot sigue
  teniendo el mismo índice que tenía al cargar -- si el admin pone un item distinto en el slot, no
  hereda el serial del anterior (evita que dos items terminen apuntando a la misma fila de mascota).
  La página avisa explícitamente que si el personaje está conectado, el GameServer tiene su propia
  copia en memoria y va a pisar este cambio en el próximo autosave -- para que un cambio pegue de
  verdad, hay que hacerlo con el personaje desconectado.

  Nota de implementación: la grilla de items **no** usa `MudDataGrid` -- con 108-120 filas editables,
  MudDataGrid en modo de edición por celda instancia un componente MudBlazor completo (con su propio
  JS interop) por celda, y varios cientos de esos a la vez volvían tan pesado el primer render que el
  circuito de Blazor Server se quedaba sin responder al keepalive de SignalR: el servidor terminaba de
  calcular todo (confirmado con logging) pero el navegador nunca llegaba a aplicar el batch de render,
  y el panel quedaba colgado en "cargando" sin ningún error visible. Una tabla HTML nativa con
  `<input>` + `@bind` no tiene ese costo y renderiza instantáneo. La página completa además está
  envuelta en un `<ErrorBoundary>` -- si algo dentro del editor tira una excepción de render, ahora se
  ve el mensaje en pantalla en vez de colgarse en silencio (el layout no tiene `blazor-error-ui`
  configurado, así que sin esto una excepción de render es indistinguible de un cuelgue).

  **La causa real del cuelgue no era (sólo) MudDataGrid.** Agregando el buscador de items (más abajo)
  el mismo síntoma volvió a aparecer -- panel colgado en "cargando", servidor completando todo bien
  (reconfirmado con logging temporal) -- a pesar de ya estar con la tabla nativa y sin ningún
  `MudIconButton` de más. La causa de fondo: `OnRowClick` de la tabla de personajes estaba escrito
  como `e => { if (e.Item is not null) _ = SelectAsync(e.Item); }` -- un lambda de bloque sin
  `return`, que el compilador infiere como `Action<T>` en vez de `Func<T, Task>`. MudTable invoca ese
  delegate, lo ve terminar (sincrónicamente, porque nunca le devuelve la `Task` real), y ahí termina
  el manejo del evento para Blazor: el auto-render que hace después de esperar el `Task` de un
  `EventCallback` nunca se dispara, porque nunca hubo una `Task` que esperar. `SelectAsync` seguía
  corriendo de fondo, mutando `_row`/`_inventario`/etc. correctamente, pero nada volvía a pedirle a
  Blazor que renderizara con esos valores nuevos -- de ahí el cuelgue silencioso. Se cambió a un
  método con firma `Task OnRowClickAsync(...)` (que si se puede enlazar como `Func<T, Task>`) y se
  agregó un `StateHasChanged()` explícito en el `finally` de `SelectAsync` como red de seguridad. La
  reescritura a tabla nativa de más arriba sigue siendo válida (menos componentes vivos es mejor de
  todas formas), pero el bug que realmente colgaba el panel era este, no el peso de la grilla.

  **Buscador de items**: cada slot tiene un botón 🔍 (nativo, no MudBlazor, mismo motivo que los
  botones de arriba) que abre un único `MudDialog` compartido (`ItemPickerDialog`, montado una sola
  vez por todo el editor, nunca por fila) con una caja de búsqueda por nombre o índice contra el
  catálogo completo de `Item.txt` (349 items) -- el equivalente, para elegir qué item va en un slot,
  al buscador visual que trae MuEditor para el mismo propósito. Elegir un resultado llena el índice del
  slot y cierra el diálogo; también tiene un botón "Vaciar slot" directo.

Las dos últimas necesitan hablar con el GameServer **corriendo**, no con un archivo de configuración, y
se resolvieron sin abrir un puerto HTTP en el GameServer (que ya tiene su propio ciclo de vida y su
socket de juego; agregarle un servidor web para esto es más riesgo del que vale):

- **Estado en vivo**: el GameServer escribe `Data/status.json` cada 3s (`StatusWriter`) con nombre,
  jugadores conectados y la lista de quiénes son. El panel lo lee por polling. Si el archivo tiene más
  de 15s, el panel lo trata como "sin conexión" en vez de mostrar datos que ya podrían ser mentira.
- **Mensaje global**: el panel deja un `Data/global-message.json` con un id único; el GameServer lo
  levanta en ≤2s (`GlobalMessagePoller`), lo emite a todos con el mismo paquete `NoticeSend` que ya usa
  el comando `/post` del chat, y borra el archivo. El id evita que un GameServer que arranca después
  reenvíe un mensaje viejo. Es un canal de un solo tipo de comando a propósito: si en algún momento
  hacen falta más, ahí sí conviene generalizarlo a una cola de verdad.

Corrección relacionada que salió de esto: `Program.cs` del GameServer salía apenas arrancaba cuando se
lo lanzaba **sin consola interactiva** (como servicio, o con la salida redirigida a un archivo). La
causa era el bucle de comandos: `Console.ReadLine()` devuelve `null` al toque si no hay una consola de
verdad detrás, y el código interpretaba ese `null` como "cerrá el servidor". Ahora ese caso deshabilita
los comandos pero deja el servidor corriendo, y se agregó apagado ordenado con Ctrl+C.

Un detalle de fidelidad que vale para todos los archivos: algunos vienen con finales de línea
mezclados (CRLF y LF en el mismo archivo) y el panel los normaliza a CRLF al guardar. Es inofensivo
--el tokenizer del servidor ignora los espacios en blanco-- pero explica por qué un `diff` crudo puede
marcar líneas que en realidad no cambiaron.

**Login.** Todo el panel está detrás de una contraseña (cookie de sesión, 7 días, deslizante). La
contraseña sale de `Admin:Password` en `appsettings.json` o de la variable de entorno
`Admin__Password`; **si no hay ninguna configurada se genera una al azar al arrancar y se imprime en
la consola**. Se eligió eso en vez de dejarlo abierto o poner una por defecto: el panel escribe la
configuración del servidor, así que "sin contraseña" no es una opción, y una contraseña por defecto
conocida es igual de mala — generarla evita las dos cosas sin poder dejar a nadie afuera, porque queda
a la vista al arrancar. La comparación es de tiempo constante, la página de login usa un layout
propio (para no filtrar el nombre del servidor ni los jugadores conectados a quien no entró todavía),
y el `returnUrl` sólo acepta rutas locales, así que el login no se puede usar como redirector a otro
sitio.

Detalle de implementación que vale la pena saber si se toca esto: el login y el logout son `POST`
normales a `/auth/login` y `/auth/logout`, no acciones de Blazor. La cookie se escribe en los headers
de la respuesta HTTP, y un circuito de Blazor ya no puede tocarlos una vez que está corriendo. Van
bajo `/auth/` y no en `/login` porque esa ruta ya la ocupa la página del formulario y dos endpoints en
la misma ruta chocan (`AmbiguousMatchException`).

Lo que el panel **no** hace todavía: no puede recargar la configuración del GameServer en caliente,
no edita los mapas en sí (`Data/Terrain`, que son binarios) ni la base de datos de cuentas/personajes
(eso vive en Postgres, no en `Data/`).

## Cómo correrlo

Requiere .NET 10 SDK (o solo el runtime para producción) y un PostgreSQL accesible (JoinServer y
DataServer lo usan).

```bash
cd src/MuServer.ConnectServer && dotnet run
cd src/MuServer.JoinServer && dotnet run
cd src/MuServer.DataServer && dotnet run
cd src/MuServer.GameServer && dotnet run
```

El panel de administración es opcional y se corre aparte (queda en <http://localhost:5281>):

```bash
cd src/MuServer.AdminPanel && dotnet run
```

Por defecto asume que está al lado del GameServer en el repo y busca su `Data/` en
`../MuServer.GameServer/bin/Debug/net10.0/Data`. Para otro despliegue, se apunta con
`GameServer:DataPath` en `appsettings.json` a la carpeta `Data/` real del GameServer. El panel pide contraseña
(`Admin:Password` en `appsettings.json`; si no hay ninguna, genera una al arrancar y la imprime en la
consola). Aun así **escribe archivos de configuración del servidor**, así que conviene tenerlo en la
red interna y no expuesto a internet.

En el directorio de salida (`bin/.../net10.0`) de cada proyecto hay que copiar los archivos de
configuración reales del servidor original: para ConnectServer `ConnectServer.ini`,
`BlackList.txt`, `ServerList.dat` (los 3 están tal cual en `MuServer99B/ConnectServer/`); para
JoinServer `JoinServer.ini` y `AllowableIpList.txt` (`MuServer99B/JoinServer/`); para DataServer
`DataServer.ini`, `AllowableIpList.txt` y `BadSyntax.txt` (`MuServer99B/DataServer/`); para
GameServer la carpeta `Hack/` con `Enc2.dat`/`Dec1.dat` y la carpeta `Data/` completa (ambas en
`MuServer99B/Data/` del paquete original — copiarla entera, sin tocar nada, es lo único que hace
falta para tener spawns/items/skills/tiendas/eventos reales, ver la nota de re-verificación en
"Próximos pasos" más arriba). En cada `.ini` de ConnectServer/JoinServer/DataServer agregar la
cadena de conexión a Postgres, por ejemplo:

```
JoinServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
DataServerPostgres = Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=...
```

GameServer es el único caso sin un `.ini` de texto real en el paquete original — el original guarda
esa configuración en archivos con extensión `.dat` que en realidad son texto plano tipo INI (dentro de
`MuServer99B/GameServer/DATA/`: `ChaosMix.dat`/`Character.dat`/`Command.dat`/`Common.dat`/
`Custom.dat`/`Event.dat`/`Item.dat`/`Skill.dat`), los ~520 campos de `CServerInfo`
(`ServerInfo.h`/`.cpp` del original) — multiplicadores de rate globales, permisos de comandos de
administrador, fórmulas de Chaos Mix, toggles de eventos custom, umbrales de detección de hacks,
toggles de logs, etc. Portados hoy: `Character.dat` completo (vía `CharacterInfoPath`, usado por
`CharacterCalcAttribute`) y una porción acotada de `Common.dat`/`Event.dat` (vía
`ServerInfoCommonPath`/`ServerInfoEventPath` — ver la sección "`CServerInfo` real" más arriba: fórmula
de experiencia, `MaxLevel`, tiempo de vida de items en el piso, rates de monstruo,
`AddExperienceRate`, `DevilSquareMaxUser`). El resto (`ChaosMix.dat`/`Command.dat`/`Custom.dat`/
`Item.dat`/`Skill.dat` completos, y la mayoría de campos de `Common.dat`/`Event.dat`) sigue sin
implementar porque el sistema de juego correspondiente no existe en este puerto todavía — es la misma
"config de servidor no portada" que se menciona repetidas veces en este documento, por ejemplo en
`gServerInfo.m_PartyMoneyDistribute` de la sección de items de piso de arriba. Este puerto usa en
cambio un `GameServer.ini` de texto plano con los mismos valores reales de `ServerCode`/`ServerName`/
`ServerPort` que ya trae `ServerList.dat` de ConnectServer:

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

Para crear la base: `createdb muonline` y aplicar en orden `db/postgres/001_accounts.sql`,
`002_characters.sql`, `003_default_class_seed.sql`.

Para publicar un binario standalone para Linux:

```bash
dotnet publish src/MuServer.ConnectServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.JoinServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.DataServer -c Release -r linux-x64 --self-contained
dotnet publish src/MuServer.GameServer -c Release -r linux-x64 --self-contained
```

## Próximos pasos (en orden sugerido)

1. ~~Spawns reales para un mapa jugable de punta a punta~~ **verificado en la Fase 8**: se corrió el
   `GameServer.dll` compilado apuntando directo a la carpeta `Data/` real de `MuServer99B` (los 29
   archivos de `Data/Monster/Spawn/*.txt`, `Data/ShopManager.txt`+`Data/Shop/`, `Data/Item/Item.txt`,
   etc., sin ningún archivo sintético) — cargó los 15 mapas, 4537 filas de spawn (tipos 0/1/2/4
   mezclados, incluyendo los de rango+cantidad de Devil Square/Blood/Chaos Castle) y 2879 monstruos
   instanciados, más 14 tiendas de NPC reales con sus items, todo sin errores ni excepciones. No hizo
   falta ningún cambio de código — `MonsterSpawnTable`/`MonsterRegistry.SpawnAll`/`ShopManagerTable`
   ya eran genéricos por formato de archivo desde que se escribieron. Para un despliegue real solo
   hace falta copiar la carpeta `Data/` completa del paquete original (`MuServer99B/Data`) al lado del
   `GameServer.dll` publicado — ningún paso manual adicional.

   **Re-verificado con el stack completo de 4 servidores** (no solo GameServer aislado): se armó un
   despliegue real con `dotnet publish -c Release -r linux-x64 --self-contained` de los 4 proyectos,
   usando los archivos de configuración REALES de `MuServer99B/` (`ConnectServer.ini`+
   `BlackList.txt`+`ServerList.dat`, `JoinServer.ini`+`AllowableIpList.txt`, `DataServer.ini`+
   `AllowableIpList.txt`+`BadSyntax.txt`, solo agregándoles la cadena de conexión Postgres) y la
   carpeta `Data/` real completa para GameServer. Log de arranque: 15 mapas, 231 tipos de monstruo,
   29 archivos de spawn con 4537 filas, **2879 monstruos instanciados** (`MonsterSetBase`/
   `MonsterSpawnTable` contra los datos reales), 349 items de balance, 58 skills, Devil Square (6
   horarios/4 brackets), 14 tiendas de NPC con 14 NPCs spawneados — los 4 procesos se conectaron
   entre sí correctamente (ConnectServer↔JoinServer↔GameServer↔DataServer). Nota: `GameServer.ini`
   no tiene un archivo de texto real equivalente en el paquete original — el original guarda esa
   configuración en el binario `GameServerInfo - Common.dat` (no portado, ver más abajo), así que
   este puerto usa un `.ini` de texto plano con los mismos valores reales de `ServerCode`/`ServerPort`
   que ya trae `ServerList.dat` (ver "Cómo correrlo" más abajo para el formato exacto).
2. **Completar GameServer Fase 3**: Trade, Warehouse y Personal Shop (las tiendas de NPC ya están,
   ver Fase 8), validación de tipo-de-item-por-slot y requisitos de nivel/stats al equipar
   (`Item.txt` ya se carga para balance de daño/defensa desde la Fase 4 segunda pasada — falta
   usarlo también para validar el equipo en sí). ~~Recoger/tirar items del suelo~~ **hecho** — ver
   la sección "GameServer — Fase 3 (continuación): items de piso" más abajo.
3. **Completar GameServer Fase 4**: IA de monstruo (patrulla/persecución/contraataque), crítico/
   excelente/set-item en el daño (dependen de `ItemOption.txt`/`SetItemOption.txt`), PvP.
4. **Completar GameServer Fase 5**: Guild, PartyMatching, correo entre amigos.
5. **Completar GameServer Fase 6 — más eventos especiales**: Blood Castle y Chaos Castle primero
   (mismo motor de estados que Devil Square, mismo `EventStageSpawnTable`/`EventEntryLevelTable` ya
   genéricos por sección, solo cambia el set de datos y las reglas de entrada/combate específicas —
   Blood Castle usa NPC + objetivo "matar al jefe", Chaos Castle es un battle royale con mapa que se
   encoge), después Kalima (mazmorra por niveles con portales). Diálogo de NPC (Charon y demás) para
   que la entrada real no dependa de mandar `C1:90` a mano. Illusion Temple/Golden Archer confirmado
   código muerto en este build — no vale la pena portarlos. Lua confirmado sin scripts reales en el
   paquete — no se va a portar.
6. **Completar GameServer Fase 7**: sistema de aprendizaje de skills (`GetSkill`/lista de skills del
   personaje, hoy sustituido por una validación directa de clase+nivel), skills de área/duración/
   combo/Teleport de aliado (`MultiSkillAttack` y las demás ramas de `RunningSkill` no portadas),
   `EffectList.txt` (buffs/debuffs de skill).
7. ~~Codificación real de índice de item (512 franjas) + wire format `ItemInfo` de 12 bytes~~
   **hecho**: ver "Fase 8 (continuación)" más arriba. Sockets/pentagrama/Muun/JewelOfHarmony/items
   periódicos como SISTEMAS DE JUEGO siguen sin portar (los bytes que ocupan en el protocolo ya
   están, pero no hay lógica de gameplay atrás).

## Estructura del código

```
SharpSSeMU/
  db/postgres/
    001_accounts.sql           # memb_info/memb_stat (cuentas) + seed test/admin
    002_characters.sql         # 13 tablas de personajes/inventario/rankings
    003_default_class_seed.sql # valores base por clase para crear personaje
  src/
    MuServer.Shared/           # utilidades compartidas entre todos los módulos
      Protocol/PacketHeader.cs   # builders/parsers de paquetes C1/C2/C3/C4
      Protocol/PacketFramer.cs   # framing de stream plano (ConnectServer/JoinServer/DataServer)
      Protocol/PacketCursor.cs   # PacketReader/PacketWriter (evita aritmética manual de offsets)
      Crypto/GameStreamCipher.cs # cifrado de flujo del socket de cliente real (GameServer)
      Crypto/PacketCipher.cs     # cifrado por bloques C3/C4 + XorData (GameServer)
      Crypto/GameClientFramer.cs # framer completo del socket de cliente real (GameServer)
      Scripting/MemScript.cs     # tokenizer de los .txt/.dat de Data/
      Config/IniFile.cs          # lector de .ini compatible con GetPrivateProfileInt/String
      Logging/Log.cs              # logger a consola + archivo
    MuServer.ConnectServer/    # ver tabla de estado arriba
    MuServer.JoinServer/       # ver tabla de estado arriba
      Db/                        # capa Npgsql (reemplaza QueryManager/ODBC + los WZ_* procs)
    MuServer.DataServer/       # ver tabla de estado arriba
      Db/                        # capa Npgsql de personajes/inventario/rankings
    MuServer.GameServer/       # ver tabla de estado arriba — Fases 1-6 (login + mundo + items + combate + social + Devil Square) implementadas
      Config/                    # subconjunto de ServerInfo.h necesario hasta ahora
      Net/                       # socket de cliente real, conexiones salientes a Join/DataServer
      Protocol/                  # paquetes y dispatcher de las Fases 1-6
      World/                     # mapas, registro de jugadores/monstruos, viewport, items, eventos especiales (Fases 2-4/6)
      StatusWriter.cs            # publica Data/status.json cada 3s (estado + quiénes están conectados)
      GlobalMessagePoller.cs     # levanta Data/global-message.json que deja el panel y lo emite a todos
    MuServer.AdminPanel/       # panel web de configuración (Blazor Server + MudBlazor) — ver sección arriba
      Components/Pages/          # una página por archivo de Data/ (warps, items, chaos mix, config, etc.)
      Auth/AdminPassword.cs      # contraseña del panel (de config, o generada al arrancar)
      Repositories/              # lectura/escritura de los archivos reales, preservando comentarios y formato
        FlatTableRepository.cs     # lector/escritor genérico de las tablas planas de Data/
        FlatTableCatalog.cs        # esquema de columnas de cada tabla plana (skills, puertas, quests, mensajes)
        SectionedTableRepository.cs # tablas numeradas de Data/Event (esquema deducido del archivo)
        IniDocument.cs             # .ini editable línea por línea (los 8 GameServerInfo - *.dat)
        ItemFileRepository.cs      # Item.txt y sus 16 secciones con columnas distintas
        ItemValueFileRepository.cs # ItemValue.txt (precios explícitos)
        MonsterFileRepository.cs   # MonsterList.txt
        MoveFileRepository.cs      # Move.txt
        RateSettings.cs            # catálogo de los ajustes "que se tocan siempre", con su explicación
        ShopFileRepository.cs      # ShopManager.txt + Shop/*.txt
        SpawnFileRepository.cs     # Monster/Spawn/*.txt (bloques por tipo de spawn)
    TestClient/                 # arnés de prueba Fase 1: cliente C# que arma un login cifrado
                                 # auténtico (mismas claves reales) contra GameServer
    WorldTestClient/            # arnés de prueba Fases 2-3: dos clientes simulados completos
                                 # (login + selección de personaje + viewport + movimiento + items)
  tests/
    gameserver_fase1_e2e_test.py # orquesta Postgres+JoinServer+DataServer+GameServer+TestClient
    gameserver_fase2_e2e_test.py # idem, con WorldTestClient (2 clientes, viewport, movimiento)
    gameserver_fase3_e2e_test.py # idem, + siembra de item real y prueba de equipar/CharSet
    full_chain_e2e_test.py       # cadena completa ConnectServer→JoinServer→DataServer→GameServer
```

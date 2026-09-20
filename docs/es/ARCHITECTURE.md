# Arquitectura

🌐 [English](../ARCHITECTURE.md) · **Español**

## Objetivos y no-objetivos

- **Objetivo:** un servidor C# legible y modificable que hable **exactamente** el protocolo de red
  de MU Online 0.99B (SSeMU 2.1.7), y un cliente que también lo hable, de modo que se puedan
  desarrollar y verificar uno contra el otro y contra el emulador original.
- **No-objetivo:** inventar un protocolo nuevo o "mejorar" el juego. El comportamiento sigue el
  código C++ del emulador original; las desviaciones son bugs y se documentan como tales.

## Componentes

```
                       ┌────────────────────────────────────────────────────┐
                       │                   PostgreSQL  (muonline)           │
                       │ memb_info · character · warehouse · guilds · …     │
                       └───────▲──────────────────▲─────────────────▲───────┘
                               │                  │                 │
                        ┌──────┴─────┐     ┌──────┴─────┐    ┌──────┴──────┐
   cliente ── 44405 ──► │ Connect    │     │ Join       │    │ Data        │
   (lista de servers)   │ Server     │     │ Server     │    │ Server      │
                        └────────────┘     └──────▲─────┘    └──────▲──────┘
                                                  │ 55970           │ 55960
   cliente ── 55900 ─────────────────────►  ┌─────┴─────────────────┴─────┐
   (juego, cifrado)                         │          GameServer         │──► lee Data/*.txt|*.dat
                                            └─────────────┬───────────────┘        ▲
                                                          │ escribe status.json    │ edita
                                                          ▼                        │
                                                   ┌──────────────┐  ┌─────────────┴─┐
                                                   │ (carpeta     │◄─┤  AdminPanel   │ :5281
                                                   │  Data/)      │  └───────────────┘
                                                   └──────────────┘
```

| Componente | C++ original | Este port | Responsabilidad |
|---|---|---|---|
| **ConnectServer** | `ConnectServer/` | `MuServer.ConnectServer` | Atiende la primera conexión del cliente con la lista de servidores; recibe por UDP los reportes de carga de los GameServers |
| **JoinServer** | `JoinServer/` | `MuServer.JoinServer` | Autenticación de cuentas (`memb_info`) y nivel de cuenta (VIP) |
| **DataServer** | `DataServer/` | `MuServer.DataServer` | Toda la persistencia: lista/creación de personajes, carga/guardado, inventarios, baúles, rankings, amigos, contadores de kills |
| **GameServer** | `GameServer/` | `MuServer.GameServer` | El juego: mundo, monstruos, combate, items, skills, parties, eventos. Habla con el cliente, JoinServer y DataServer |
| **AdminPanel** | *(nuevo)* | `MuServer.AdminPanel` | Interfaz web que edita los archivos de datos/config del GameServer y la base de personajes, y muestra el estado en vivo |
| **Shared** | — | `MuServer.Shared` | Framing de paquetes, cifrados, tokenizador de scripts, lector INI, logging |

El original habla con SQL Server por ODBC y procedimientos almacenados (`WZ_*`); este port reemplaza esa
capa por **Npgsql + PostgreSQL** (`db/postgres/*.sql`). El nombre del personaje es la clave primaria.

### Interior del GameServer (`MuServer.GameServer/`)

| Carpeta | Contenido |
|---|---|
| `Net/` | el socket de juego (descifrar → segmentar por etapas), conexiones salientes a Join/DataServer |
| `Protocol/` | `ClientProtocolHandler` (el despachador de opcodes y sus handlers), constructores/parsers de paquetes |
| `World/` | mapas, registros de jugadores/monstruos/parties, `ViewportTicker` (el tick de mundo de 200 ms), items, tablas de balance, Devil Square |
| `Config/` | vistas tipadas de `GameServerInfo - *.dat` y `GameServer.ini` |
| `Data/` | los 8 `GameServerInfo - *.dat` por defecto que se distribuyen |
| `StatusWriter.cs`, `GlobalMessagePoller.cs` | el puente por archivos hacia el AdminPanel (`status.json` de salida, `global-message.json` de entrada) |

**Tick de mundo.** `ViewportTicker` corre cada 200 ms: revive monstruos muertos, barre items de piso
vencidos, corre la IA de monstruos (elección de objetivo, movimiento, ataque — con el mismo pipeline
de acierto/defensa/daño que los jugadores), regenera maná/BP, difunde la vida del grupo, autoguarda
periódicamente y compara el conjunto de objetos visibles de cada jugador para mandar los paquetes de
*aparece* / *desaparece*.

**Persistencia.** El GameServer mantiene el estado del jugador en memoria y guarda vía DataServer al
desconectar, después de ganar experiencia (limitado a una vez por minuto) y en un autoguardado
periódico. Editar en la base un personaje conectado se pierde en el siguiente guardado.

## El protocolo de red

Los paquetes usan el framing clásico de MU — `C1`/`C2` (claro) y `C3`/`C4` (cifrados por bloque) con
largos de 1 o 2 bytes — y el socket del GameServer aplica **tres capas**, en este orden al recibir:

1. **Cifrado de flujo** (port de `HackCheck.cpp`) sobre *todo*, incluidos los bytes de tipo/largo. Su
   clave se deriva del `ServerSerial` de 17 bytes.
2. **Cifrado de bloque** (SimpleModulus, 8 bytes de claro → 11 de cifrado) para `C3`/`C4`.
3. **Ofuscado XorData** — asimétrico: el cliente ofusca lo que manda, el servidor no.

Como la capa 1 esconde los largos, ningún framer genérico puede segmentar el flujo primero; el socket
debe descifrar *antes* de segmentar. Por eso el socket de juego del cliente es C++ nativo en vez de usar
la librería de red C# (que sigue atendiendo al ConnectServer, que va en claro).

**Trampas que conviene conocer** (cada una costó depuración real; detalle en el
[documento de protocolo](../../MuMain-099B/docs/protocolo-099b.es.md)):

- *Los errores no fallan: mienten.* Un struct un byte más corto lee campos corridos y muestra números
  equivocados; uno más largo hace que el paquete se descarte en silencio. Nada se rompe a la vista.
- **El relleno de cola de los structs viaja por la red** — el emulador manda `sizeof(struct)` con layout MSVC x86.
- Conviven dos codificaciones de clase (`base<<4` en la base/pedido de creación, `base<<5` en el CharSet).
- Los items son **5 bytes** en el wire 0.99B (no 12), con índice/nivel/opciones repartidos, y dos
  numeraciones de índice (32 por sección en el servidor, 512 por grupo en el cliente).
- Un despachador que apunta a un handler de *temporada posterior* compila y conecta bien, y después
  interpreta mal cada paquete de ese opcode. Fue la clase de bug más frecuente (party, quests, tienda, skills, joyas).

## Cómo se establece la corrección

Ningún layout ni fórmula se escribe de memoria:

1. **Structs de red generados.** `tools/protogen` parsea los headers del emulador, modela el layout MSVC
   x86 (alineación, `#pragma pack`, relleno de cola) y emite `Protocol099B.generated.h` — 259 structs,
   211 con opcode — cada uno con `static_assert(sizeof)` / `offsetof`.
2. **Scripts de auditoría** (`tools/protogen/audit_*.py`, `tools/gamedata/audit_*.py`) cruzan cliente,
   servidor y tablas de datos buscando tamaños que no coinciden, opcodes sin atender y datos de
   item/skill/monstruo/tienda que discrepan.
3. **Portado, no reinventado.** La lógica del servidor cita el archivo y la línea original que porta
   (p. ej. combate: `Attack.cpp` `MissCheck` → `GetTargetDefense` → `GetAttackDamage[Wizard]` → piso de
   daño; los monstruos pasan por el *mismo* pipeline, como en el original).
4. **Tests.** 152 tests unitarios del cliente fijan los bytes exactos y el cifrado contra valores
   derivados del algoritmo original (nunca contra la propia implementación); los e2e en Python manejan
   el stack real del servidor con un cliente simulado cifrado; la verdad de "¿esto pertenece a 0.99B?"
   es el `Data/Interface` del cliente original.

El cliente parte de un código Season 5.2→6, así que contiene muchas funciones que no existen en 0.99B
(Rage Fighter, Illusion Temple, Master Skill Tree, Castle Siege…). Antes de tratar como bug un código de
cliente sin portar, confirmá que la función existe en 0.99B real.

## Archivos de datos

El GameServer se alimenta de las tablas originales: `Data/Monster/MonsterList.txt`,
`Monster/Spawn/*.txt`, `Item/Item.txt`, `Skill/SkillList.txt`, `Shop/*.txt` + `ShopManager.txt`,
`Event/*`, `Move.txt`, puertas, quests y los 8 `GameServerInfo - *.dat` (texto en formato INI con ~520
ajustes). Se leen una sola vez al arrancar.

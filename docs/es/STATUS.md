# Estado del proyecto

🌐 [English](../STATUS.md) · **Español**

Leyenda: ✅ funciona · 🔶 parcial / simplificado · ❌ no implementado · ⛔ no existe en 0.99B (fuera de alcance)

El proyecto es **jugable pero incompleto**. Se ejercitó de punta a punta con tests automáticos y con
sesiones prácticas con un cliente real; esperá asperezas fuera de los caminos marcados como
funcionales. Los arreglos encontrados jugando están registrados en el
[diario de desarrollo del servidor](../../SharpSSeMU/docs/development-log.es.md) y en el
[documento de protocolo del cliente](../../MuMain-099B/docs/protocolo-099b.es.md).

- [Servidor](#servidor) · [Cliente](#cliente) · [Limitaciones conocidas](#limitaciones-conocidas) · [Roadmap](#roadmap) · [Arreglado recientemente](#arreglado-recientemente)

## Servidor

### Infraestructura

| Módulo | Estado | Notas |
|---|---|---|
| ConnectServer | ✅ | lista de servidores, reportes UDP de carga, límites por IP, lista negra |
| JoinServer | ✅ | login de cuentas contra PostgreSQL, nivel de cuenta (VIP) |
| DataServer | ✅ | personajes, inventarios, baúles, rankings, amigos |
| Esquema PostgreSQL | ✅ | `db/postgres/001`–`004` (cuentas, personajes, semilla de clases, amigos) |
| AdminPanel | 🔶 | edita todos los archivos de datos y personajes, estado en vivo, mensajes globales. Sin recarga en caliente; una sola contraseña compartida |
| Tests end-to-end | ✅ | los 10 en verde (`full_chain`, `fase1`-`fase6`, `grounditem`, `shop`, `skills`) |
| Tests unitarios | ✅ | NUnit, `SharpSSeMU/tests/MuServer.GameServer.Tests`: opciones de items, fórmula de daño, requisitos de drops de quest (`dotnet test`) |

### GameServer

| Área | Estado | Notas |
|---|---|---|
| Login, lista / creación / selección / borrado de personaje | ✅ | qué clases se pueden crear lo decide la tabla `default_class_type` del DataServer; el borrado sigue `CharacterDeleteSwitch` / `PersonalCodeCheck` |
| Teclas rápidas de skills y opciones del cliente (`F3:30`) | ✅ | se guardan por personaje y se reenvían al entrar al mundo |
| Entrada al mundo, movimiento, viewport, warps/puertas, 15 mapas | ✅ | ~2 900 monstruos de los archivos de spawn reales |
| Stats de personaje y atributos derivados | ✅ | `CharacterCalcAttribute` real por clase; los 8 `GameServerInfo - *.dat` cargados |
| Items e inventario (equipar, mover, requisitos, reparar) | ✅ | codificación de item 0.99B de 5 bytes, balance real de `Item.txt` |
| Items de piso (tirar / recoger / vencimiento) | ✅ | |
| Uso de items: pociones, town portal, **joyas** (Bless / Soul / Life), orbes y pergaminos de skill | ✅ | |
| Tiendas de NPC (comprar / vender) | ✅ | 14 tiendas reales; el precio mostrado = el precio cobrado |
| Baúl, Trade | ✅ | |
| Chaos Machine | ✅ | fórmulas y tasas de éxito reales |
| Combate: golpes y skills contra monstruos | ✅ | tirada de acierto/esquiva, defensa, golpes crítico / excelente / ignorar defensa (con su color en el cliente), alas y mascotas, multiplicadores de skills de DK/DL, piso de daño, ratios de daño PvM — portado de `Attack.cpp` |
| Combate: monstruos atacan jugadores | ✅ | mismo pipeline que los jugadores; los hechizos usan el rango de daño propio del skill |
| IA de monstruos | 🔶 | inactivo/patrulla/persecución/ataque; la elección de hechizo es heurística (`GetMonsterAttackSkill`) |
| Skills y maná | 🔶 | ataques a un objetivo, de duración y multi-objetivo, aprendizaje por orbes, regeneración de maná/BP. Sin buffs/debuffs de `EffectList.txt`, sin combo/teleport de aliado |
| Muerte, experiencia, subir de nivel, respawn | ✅ | el cadáver permanece visible hasta el respawn (como en el original) |
| Loot | 🔶 | tirada genérica de item/zen por `ItemRate`/`MoneyRate` y drops de quests. Sin `ItemBag`, tablas de drop de jefes/eventos, opciones excelentes/set aleatorias |
| Chat, susurro (entre GameServers), party (+reparto de XP), amigos | ✅ | correo entre amigos ❌ |
| Quests | 🔶 | opcodes de info/estado de quest conectados a las tablas reales |
| Guild | 🔶 | lista / ventana del maestro / crear; falta trabajo de esquema para terminarla |
| Devil Square | ✅ | máquina de estados completa, tickets, spawns por etapa, ranking |
| Blood Castle, Chaos Castle, Kalima | ❌ | mismo motor de eventos que Devil Square, distintos datos/reglas |
| Duelo, Personal Shop, Golden Archer, Mascotas (Dark Spirit/Raven), Teleport Ally, PartyMatching | ❌ | la mayoría necesita una segunda cuenta para probarse bien |
| Opciones de items: skill, suerte, adicional, excelentes | ✅ | `ItemOption.txt`; bonus base de los excelentes, durabilidad y requisitos; reflejo y reducción de daño, vida y maná al cazar, zen, recuperación de vida, bonus de set de armadura completo |
| Items de set (ancient) | ❌ | `SetItemOption.txt` no se carga |
| Tablas de daño por mapa (`DamageTable`) | ❌ | se tratan como 100 % (sí se aplican `GeneralDamageRatePvM` y los ratios PvM por clase) |
| Sockets / pentagrama / Muun / Harmony | ⛔ | temporadas posteriores |
| Illusion Temple, Castle Siege, Crywolf | ⛔ | no son parte de 0.99B (código muerto en el build original) |
| Scripting Lua | ⛔ | el paquete no trae scripts reales |

## Cliente

El cliente es un **fork de MuMain (Season 5.2 → 6)** cuya capa de red se portó al protocolo 0.99B.

| Área | Estado | Notas |
|---|---|---|
| Socket de juego nativo, 3 capas de cifrado, framing | ✅ | verificado contra un servidor vivo |
| Paquetes servidor → cliente | ✅ | todos atendidos salvo `0x88` (que no tiene receptor por diseño) |
| Paquetes cliente → servidor | ✅ | 57 de 59 opcodes; los 2 que faltan son internos del DataServer, no paquetes de cliente |
| Librería de protocolo generada + chequeos `static_assert` de layout | ✅ | 259 structs; se regenera con `tools/protogen` |
| Tests unitarios | ✅ | 152 tests |
| Juego contra SharpSSeMU | 🔶 | login → personaje → mundo → combate → items → tiendas → skills verificado jugando; la cola larga se sigue auditando |
| **Interfaz de usuario** | ❌ | sigue con el aspecto de Season 6; pasarla al aspecto 0.99B requiere criterio visual — ver `docs/ui-099b.md` del cliente. **El mayor pedido de ayuda abierto** |
| Funciones solo de Season 6 que quedan en el código | 🔶 | Rage Fighter, Master Skill Tree, Illusion Temple, chat rooms, Castle Siege… siguen compilados; algunos caminos todavía llaman a la capa de red heredada (estilo OpenMU) en vez de la nativa 0.99B — confirmá que una función existe en 0.99B (`MuClient/Data/Interface`) antes de "arreglarla" |
| Build Linux | ❌ | el preset todavía no funciona (Windows x86 es el objetivo soportado) |

## Limitaciones conocidas

- **Cobertura de un solo probador.** Los tests automáticos cubren protocolo y lógica del servidor; el juego
  con cliente real lo hizo una sola persona. Los reportes de bugs con pasos para reproducir valen mucho.
- Los datos de personajes/spawns/monstruos se cargan **una sola vez al arrancar**; cambiarlos requiere reiniciar el GameServer.
- El barrido de viewport es O(n²) en jugadores online — bien para decenas de jugadores, no para cientos.
- Las contraseñas se guardan según `MD5Encryption` de JoinServer (texto plano por defecto para desarrollo local); el AdminPanel tiene una única contraseña compartida y no está endurecido para internet público.
- El servidor no arranca sin las tablas `Data/` originales, que no se distribuyen acá.
- El AdminPanel no tiene tests automáticos.

## Roadmap

Orden sugerido, lo más fácil primero:

1. **UI del cliente → aspecto 0.99B** (ver `docs/ui-099b.md`).
2. **Loot**: opciones excelentes y niveles sorteados como en el original (`ItemOptionRate.txt`, `ExcellentOptionRate.txt`), `ItemBag`, tablas de jefes/eventos.
3. **Blood Castle → Chaos Castle → Kalima** sobre el motor de Devil Square existente.
4. **Duelo**, luego completar Guild (esquema + rangos + marcas), Personal Shop, Golden Archer.
5. Loot de monstruos: `ItemBag`, tablas de drop de jefes y eventos.
6. Efectos de skills (buffs/debuffs de `EffectList.txt`), combo y teleport de aliado.
7. Recarga de config en caliente + endurecer el AdminPanel (autenticación por usuario).
8. Seguir la auditoría del cliente: llamadas restantes a la capa de red heredada, quitar código exclusivo de Season 6.
9. Build Linux del cliente.

## Arreglado recientemente

Bugs encontrados jugando con un cliente real en la última sesión (todos verificados con recompilación + tests):

- **Número de daño al matar:** el golpe final mostraba dos números. Como el servidor original, ahora no manda paquete de daño; su número viaja en el paquete de recompensa, solo para quien lo dio (o su party).
- **Cliente original:** no conectaba con `127.0.0.1` — el `main.exe` de Webzen rechaza esa dirección exacta. Ver [Primeros pasos](GETTING_STARTED.md#6-conectar-un-cliente).
- El GameServer no atendía el **borrado de personaje** ni el **guardado de teclas rápidas**.
- **Opciones de items:** skill y suerte estaban invertidos en drops, tiendas NPC y el AdminPanel; las tiendas vendían los items con durabilidad 0 (rotos); las armas mostraban su skill sin tener la opción de skill.
- Los **tests end-to-end** se colgaban a mitad: el harness nunca leía el pipe de consola de los servidores y uno quedaba bloqueado escribiendo su log.

- **Tienda:** solo funcionaba la primera compra — el handler 099B nunca limpiaba su bandera de "pedido en curso".
- **Combate:** los monstruos ignoraban la defensa del jugador y nunca fallaban; los hechizos usaban la fórmula física. Ahora usan el pipeline original.
- **Muerte de monstruos:** desaparecían al instante; ahora el cadáver queda hasta el timer de respawn, como en el original.
- **Skills:** las skills aprendidas (p. ej. Twisting Slash desde un orbe) nunca aparecían en el selector — el handler 099B de la lista de skills no refrescaba el contador de skills del cliente.
- **Joyas:** mejorar un item con Bless/Soul/Life lo hacía desaparecer — el paquete de modificar item se parseaba con el formato de item de una temporada posterior.
- **Party / quests / crear guild:** el cliente mandaba los pedidos por la capa heredada, así que nunca llegaban al servidor.

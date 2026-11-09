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
| Chaos Machine | ✅ | fórmulas y tasas de éxito reales; el item que da una mezcla sale de su bolsa de `EventItemBag`; las mezclas de la Devil's Invitation, la Invisibility Cloak y el Horn of Dinorant. Sin las de mascotas: las mascotas mismas (Dark Horse, Dark Raven, su nivel y comandos, la ventana del Trainer) no están portadas. El Lost Map tampoco tiene mezcla en el original: sale de drops |
| Combate: golpes y skills contra monstruos | ✅ | tirada de acierto/esquiva, defensa, golpes crítico / excelente / ignorar defensa (con su color en el cliente), alas y mascotas, multiplicadores de skills de DK/DL, piso de daño, ratios de daño PvM — portado de `Attack.cpp` |
| Combate: monstruos atacan jugadores | ✅ | mismo pipeline que los jugadores; los hechizos usan el rango de daño propio del skill |
| Combate: jugador contra jugador | 🔶 | el pipeline de PvP del original (tirada de acierto con las tasas PvP, defensa a la mitad, lo que lleva puesto el objetivo, ratios de daño PvP y de Chaos Castle), pero **solo dentro de un Chaos Castle en marcha**: en el resto del juego los jugadores no pueden dañarse, porque el sistema de PK (niveles de PK, outlaws, defensa propia, los items que suelta un jugador asesinado) no está portado. Ataques y skills de un solo objetivo; sin skills de área ni reflejo contra jugadores |
| IA de monstruos | 🔶 | inactivo/patrulla/persecución/ataque; la elección de hechizo es heurística (`GetMonsterAttackSkill`) |
| Skills y maná | 🔶 | ataques a un objetivo, de duración y multi-objetivo, aprendizaje por orbes, regeneración de maná/BP. Sin buffs/debuffs de `EffectList.txt`, sin combo/teleport de aliado |
| Muerte, experiencia, subir de nivel, respawn | ✅ | el cadáver permanece visible hasta el respawn (como en el original) |
| Loot | 🔶 | como en el original: items de quest, reglas de `ItemDrop.txt` / `ItemMoneyDrop.txt` y después la tirada genérica (`ItemRate` del monstruo contra `ItemDropRate`, tasas por mapa de `MapManager.txt`, drops excelentes desde el nivel 25), con las opciones de nivel/skill/suerte/adicional/excelente sorteadas de `ItemOptionRate.txt` / `ExcellentOptionRate.txt`; el zen es la experiencia que dio el monstruo. Las bolsas de jefes (Red Dragon, monstruos dorados, Skeleton King, White Wizard, Kundun) y las cajas, medallas y Starter Rings que se abren al tirarlos al suelo salen de `EventItemBag`. Los drop events de `DropEvent.dat` corren según su horario (el paquete lo trae vacío) |
| Chat, susurro (entre GameServers), party (+reparto de XP), amigos | ✅ | correo entre amigos ❌ |
| Quests | 🔶 | opcodes de info/estado de quest conectados a las tablas reales |
| Guild | 🔶 | lista / ventana del maestro / crear; falta trabajo de esquema para terminarla |
| Devil Square | ✅ | máquina de estados completa, tickets, spawns por etapa, ranking, item del primer puesto desde su bolsa, límite diario de entradas y límite de PK |
| Blood Castle | 🔶 | el evento completo: entrada, etapas (monstruos, puente, puerta, estatua), el arma del Arcángel y el Arcángel, recompensas y premio en bolsa. El paquete solo trae el mapa del castillo 1. Límite diario de entradas y límite de PK como en el original |
| Kalima | 🔶 | la puerta del Lost Map (se abre donde se tira, para su dueño y su grupo, cinco entradas o tres minutos) y la puerta al siguiente Kalima cuando muere un jefe. El paquete solo trae el mapa de Kalima 1 |
| Chaos Castle | 🔶 | el evento completo: entrada (Armor of Guardsman + cuota), la espera en la zona segura, la pelea con monstruos y con los otros jugadores mientras caen los anillos exteriores de la arena, el golpe que lanza a los jugadores, puntajes, experiencia, la bolsa del ganador y el ranking, el sorteo de las cuotas entre los participantes. El paquete solo trae el mapa del castillo 1. Probado de punta a punta con dos clientes simulados, todavía no en el cliente real |
| Duelo, Personal Shop, Golden Archer, Mascotas (Dark Spirit/Raven), Teleport Ally, PartyMatching | ❌ | la mayoría necesita una segunda cuenta para probarse bien |
| Opciones de items: skill, suerte, adicional, excelentes | ✅ | `ItemOption.txt`; bonus base de los excelentes, durabilidad y requisitos; reflejo y reducción de daño, vida y maná al cazar, zen, recuperación de vida, bonus de set de armadura completo |
| Items de set (ancient) | ✅ | `SetItemType.txt` / `SetItemOption.txt`: los stats de una pieza, los bonos de dos o más piezas y del set completo, el aura, drops de set aleatorios (tasa por mapa, filas de `ItemDrop.txt`, bolsas como la de Kundun). Una pieza de set no se puede tirar al suelo |
| Tablas de daño por mapa (`DamageTable`) | ❌ | se tratan como 100 % (sí se aplican `GeneralDamageRatePvM` y los ratios PvM por clase) |
| Sockets / pentagrama / Muun / Harmony | ⛔ | temporadas posteriores |
| Illusion Temple, Castle Siege, Crywolf | ⛔ | no son parte de 0.99B (código muerto en el build original) |
| Scripting Lua | ⛔ | el paquete no trae scripts reales |

## Cliente

**Experimental.** El cliente recomendado es el original 0.99B, contra el que se verifica el servidor.
El cliente open source de este repositorio es un **fork de MuMain (Season 5.2 → 6)** cuya capa de red se portó al protocolo 0.99B.

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
2. **Sistema de PK** (PvP fuera de los eventos: niveles de PK, outlaws, defensa propia, caída de items) y las **mascotas** (Dark Horse, Dark Raven, el Trainer y las mezclas de mascotas).
3. **Duelo**, luego completar Guild (esquema + rangos + marcas), Personal Shop, Golden Archer.
4. Efectos de skills (buffs/debuffs de `EffectList.txt`), combo y teleport de aliado.
5. Recarga de config en caliente + endurecer el AdminPanel (autenticación por usuario).
6. Seguir la auditoría del cliente: llamadas restantes a la capa de red heredada, quitar código exclusivo de Season 6.
7. Build Linux del cliente.

## Arreglado recientemente

Bugs encontrados jugando con un cliente real en la última sesión (todos verificados con recompilación + tests):

- **Número de daño al matar:** el golpe final mostraba dos números. Como el servidor original, ahora no manda paquete de daño; su número viaja en el paquete de recompensa, solo para quien lo dio (o su party).
- **Cliente original:** no conectaba con `127.0.0.1` — el `main.exe` de Webzen rechaza esa dirección exacta. Ver [Primeros pasos](GETTING_STARTED.md#6-conectar-un-cliente).
- El GameServer no atendía el **borrado de personaje** ni el **guardado de teclas rápidas**.
- **Chaos Castle y PvP:** el evento no existía y los jugadores no podían atacarse. Ya están los dos (ver la tabla). De paso: la Cape of Lord se ignoraba al calcular el daño, un castillo sin más horarios en su calendario se quedaba con los puntajes pendientes, y la respuesta a un comando de chat desconocido era solo el nombre del comando (una sobrecarga de `Loc.F` tomaba el formato por un idioma).
- **Límites de entrada:** Blood Castle, Chaos Castle y Devil Square ahora cuentan cada entrada del día (`BCCount` / `CCCount` / `DSCount`, que el DataServer reinicia a diario) y respetan `*MaxEntryCount_ALx` y el límite de PK.
- **Chaos Machine:** la mezcla del Horn of Dinorant está, y las mezclas nunca llegaban a las bolsas de items en un servidor en marcha (el manejador no se las pasaba), así que los resultados de alas, frutas y armas del caos salían siempre de la lista de respaldo.
- **Comandos de desarrollo:** `/item` y `/zen` le daban a cualquier jugador cualquier item y diez millones de zen. Ahora hace falta `CommandDeveloperSwitch = 1` en `GameServerInfo - Command.dat` (apagado en el archivo que se distribuye).
- **Kalima y drop events:** el Lost Map no hacía nada y `DropEvent.dat` no se leía. Ahora un Lost Map tirado al suelo abre su puerta y los drop events funcionan.
- **Blood Castle:** era solo un temporizador. Ahora es el evento completo, y el Mensajero del Arcángel de Devias (clase 233) se confundía con el Arcángel (232), así que nadie podía abrir su ventana de entrada. La respuesta de tiempo restante de Devil Square también llevaba el nivel en vez del tipo de evento.
- **Items de set:** no existían (nunca se leían los archivos). Ahora las piezas de set tienen sus propios stats, bonos y aura, caen como en el original y la bolsa de Kundun entrega de verdad sus cinco. Igual que en el original, ya no se pueden tirar al suelo los items de más de +4, los excelentes ni los de set.
- **Bolsas de items:** los jefes, la Box of Luck, las cajas de Kundun, las medallas, los Starter Ring, los resultados del Chaos Machine y el premio de Devil Square faltaban o estaban fijados en el código. Ahora salen de `EventItemBagManager.txt` y `EventItemBag/` como en el original.
- **Drops:** el ajuste *Probabilidad de drop de item* (`ItemDropRate`) se ignoraba, las opciones de los items soltados eran una estimación y el zen no seguía al original. Ahora los drops salen de los archivos originales (`MapManager.txt`, `ItemDrop.txt`, `ItemOptionRate.txt`, `ExcellentOptionRate.txt`...) y el zen de una muerte es la experiencia que dio; el valor por defecto del repo es ahora el del paquete original (`ItemDropRate` 100: cerca de la mitad de las muertes de un monstruo de nivel 47 sueltan un item).
- **Opciones de items:** skill y suerte estaban invertidos en drops, tiendas NPC y el AdminPanel; las tiendas vendían los items con durabilidad 0 (rotos); las armas mostraban su skill sin tener la opción de skill.
- Los **tests end-to-end** se colgaban a mitad: el harness nunca leía el pipe de consola de los servidores y uno quedaba bloqueado escribiendo su log.

- **Tienda:** solo funcionaba la primera compra — el handler 099B nunca limpiaba su bandera de "pedido en curso".
- **Combate:** los monstruos ignoraban la defensa del jugador y nunca fallaban; los hechizos usaban la fórmula física. Ahora usan el pipeline original.
- **Muerte de monstruos:** desaparecían al instante; ahora el cadáver queda hasta el timer de respawn, como en el original.
- **Skills:** las skills aprendidas (p. ej. Twisting Slash desde un orbe) nunca aparecían en el selector — el handler 099B de la lista de skills no refrescaba el contador de skills del cliente.
- **Joyas:** mejorar un item con Bless/Soul/Life lo hacía desaparecer — el paquete de modificar item se parseaba con el formato de item de una temporada posterior.
- **Party / quests / crear guild:** el cliente mandaba los pedidos por la capa heredada, así que nunca llegaban al servidor.

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
| Chaos Machine | ✅ | fórmulas y tasas de éxito reales; el item que da una mezcla sale de su bolsa de `EventItemBag`; las mezclas de la Devil's Invitation, la Invisibility Cloak y el Horn of Dinorant. Las mezclas de mascotas son del Trainer (abajo). Una mezcla ya no cierra la ventana, así que se puede seguir con la siguiente. El Lost Map tampoco tiene mezcla en el original: sale de drops |
| Dark Horse y Dark Raven | 🔶 | una mascota tiene nivel y experiencia propios, que guarda el DataServer bajo el serial del item: se piden al entrar el jugador (y al abrir el almacén), se guardan con el personaje, se muestran al cliente (`C1:A9`) y crecen con un décimo de la experiencia de cada muerte, con las reglas de subida del original (una mascota que superaría a su dueño devuelve el nivel); el Dark Horse suma defensa, se lleva parte de cada golpe y pide nivel 218 + 2 por nivel de mascota, el Dark Raven 185 + 15 de mando; un Dark Horse vale 2 millones por nivel y un Dark Raven 1 millón (una tienda paga un tercio). `/item` le da a una mascota un serial del DataServer. El almacén guardaba sus items en el formato de 5 bytes del cliente y perdía el serial y las opciones excelentes; ahora usa el formato de la base de datos. **El Dark Raven** de un Dark Lord (`DarkSpirit.cpp`) pelea por su dueño: el cliente le ordena (`C1:A7`) quedarse quieto, atacar al azar lo que hay cerca, atacar lo que ataca el dueño o atacar un objetivo; cada segundo o así elige un objetivo, todos los de alrededor lo ven atacar (`C1:A8`) y 600 ms después cae el golpe, contra la mitad de la defensa del objetivo, gastando al cuervo; tres de cada diez se extienden a hasta diez alrededor del objetivo al 60%. Las muertes son del dueño (experiencia, drops); sus números salen en la ventana del personaje. **Las mascotas se gastan** (`DamageSprite`): el Guardian Angel, el Imp y los cuernos de Uniria y Dinorant pierden durabilidad con los golpes que recibe su dueño (monstruos, jugadores y el cuervo por igual) y desaparecen al agotarse; el Dark Horse pierde un punto cada 1.500 puntos "chicos" y se queda en cero, nunca se quita; un Dark Lord sobre un Dark Horse llega dos casillas más lejos con sus skills. Cada guardado escribe ahora los items vivos, así que lo que se gasta en el lugar ya no se pierde. **El Trainer** (el NPC de las mascotas): su ventana mueve items con la bandera 5 sobre la misma caja que la Chaos Machine; una Jewel of Chaos, el Spirit de la mascota, una Jewel of Creation y Bless y Soul (cinco de cada una para el Dark Horse, dos para el Dark Raven) hacen una mascota de nivel 1 por 5 o 1 millón de zen (`PetMixRate`), que aparece en la caja (resultado 100) con un serial del DataServer; también es el único lugar donde se reparan las mascotas (una mascota cuesta todo su precio, el doble si está agotada), y nunca se reparan las otras mascotas del slot de ayudante, las flechas ni los anillos que no se pueden vestir. Sin portar: el efecto de la skill Earthshake del Dark Horse, los datos de mascota en comercio y cajas de caos, un serial para cada item que crea el servidor (solo las mascotas lo tienen), que el cuervo pare al acabar un duelo |
| Combate: golpes y skills contra monstruos | ✅ | tirada de acierto/esquiva, defensa, golpes crítico / excelente / ignorar defensa (con su color en el cliente), alas y mascotas, multiplicadores de skills de DK/DL, piso de daño, ratios de daño PvM — portado de `Attack.cpp` |
| Combate: monstruos atacan jugadores | ✅ | mismo pipeline que los jugadores; los hechizos usan el rango de daño propio del skill |
| Combate: jugador contra jugador, sistema de PK, duelos | 🔶 | el pipeline de PvP del original (tirada de acierto con las tasas PvP, defensa a la mitad, lo que lleva puesto el objetivo, ratios de daño PvP y de Chaos Castle) y su sistema de PK: en cualquier lugar fuera de las zonas seguras, entre jugadores de más de nivel 5, en los mapas que lo permiten (`NonPK` en `MapManager.txt` / `Common.dat`); matar a un inocente hace un asesino (niveles de PK 4-6, con el color del nombre), matar a un asesino un héroe (niveles 2-0); el tiempo de PK baja con el tiempo y con cada monstruo muerto; la defensa propia de un minuto contra a quien atacaste; un asesino que muere puede soltar un item que lleva puesto (`PKItemDrop*`); los asesinos no entran a las tiendas de NPC, a la lista de warps ni a los eventos (`PKLimit*`); `/pkclear`. El duelo: se le pide a otro jugador (30 s para responder), pelean solo entre ellos, hasta `DuelMaxScore` muertes o hasta que pase un minuto sin golpes, el daño se escala con `DuelDamageRate`, una muerte en duelo no es PK ni suelta ningún item, el perdedor vuelve a la vida donde cayó y el ganador entra al ranking de duelos; `DuelEnable` por mapa en `MapManager.txt`, `DuelSwitch` / `DuelAnnounceSwitch` / `DuelMaxScore` en `Common.dat`. Ataques y skills de un solo objetivo. Sin portar: guerras de guild, skills de área y reflejo de daño contra jugadores, la ventana de conteo de muertes, el bloqueo de 30 s para salir tras una defensa propia, los requisitos genéricos de comando de `/pkclear` |
| IA de monstruos | 🔶 | inactivo/patrulla/persecución/ataque; la elección de hechizo es heurística (`GetMonsterAttackSkill`) |
| Skills y maná | 🔶 | ataques a un objetivo, de duración y multi-objetivo, aprendizaje por orbes, regeneración de maná/BP. **Efectos** (`EffectManager`, `EffectList.txt`): un jugador lleva hasta 16 buffs y debuffs que cuentan hacia atrás cada segundo, se envían al cliente y a quien lo ve (`C1:07` y los bits de aura del paquete de viewport), cambian lo que hace un golpe (daño, crítico, defensa, daño recibido, vida máxima), terminan al reaparecer el jugador (los que no se guardan) y los marcados "save" vuelven de la base de datos con el tiempo que les quedaba. **Las skills que ayudan** están: el Mana Shield del Dark Wizard (se puede cancelar), el Heal, Greater Defense y Greater Damage de la Elfa, el Greater Life del Dark Knight y el Greater Critical Damage del Dark Lord (los dos últimos sobre el lanzador y su party dentro del radio), con los números de `GameServerInfo - Skill.dat`; el costo se paga solo si la skill hace efecto, y una zona segura solo las permite donde lo dice `SkillUseArea.txt`. **Las skills que dañan y dejan algo** (`ApplySkillEffect`): Poison y Decay envenenan al objetivo (3 % de la vida que le queda cada dos segundos durante 20 o 10 segundos, como números de daño de su propio color que ve quien lo envenenó; la muerte, con su recompensa, es suya), Ice e Ice Storm lo frenan (sus acciones llegan 800 ms después), Ice Arrow lo inmoviliza 7 segundos (un monstruo ni deambula ni persigue, y las órdenes de caminar de un jugador se ignoran; sigue pudiendo golpear lo que tiene al alcance) y Fire Slash le quita una parte de la defensa que crece con la fuerza de quien lo lanza. Funcionan sobre monstruos y sobre jugadores, y un hechizo de Poison de un monstruo envenena al jugador que golpea. La resistencia del objetivo al elemento (las columnas Resistance de `MonsterList.txt`, la resistencia por nivel de un anillo o amuleto para un jugador) deja que el efecto entre una vez de cada resistencia + 1; la skill daña igual y al cliente se le dice si hizo efecto (el `0x80` del eco de la skill). Un monstruo envenenado o congelado se ve así para quien lo ve aparecer; un Antidote cura el veneno y el hielo; un monstruo vuelve de la muerte limpio. Las inmunidades se respetan (un jugador con la física no recibe golpes, con la mágica no recibe hechizos; un monstruo tiene su propio par): el golpe es un fallo, para jugadores, monstruos y el Dark Raven. **El combo** (`ComboSkill.txt`, `CComboSkill`): una skill de inicio (para el Dark Knight Falling Slash, Lunge, Uppercut, Cyclone o Slash), una de remate en menos de tres segundos (Twisting Slash, Rageful Blow o Death Stab) y otra de remate distinta hacen que el tercer golpe sea combo: se duplica, se le suma una parte de las estadísticas del jugador (`ComboDamageConst*` del Character.dat) y al cliente se le muestra el combo (la marca de combo del número de daño y un eco `SKILL_COMBO` por objetivo); un golpe normal, cualquier otra skill o una tardía rompe la cadena. El Magic Gladiator y el Dark Lord siempre podían hacer combo, los demás después de la misión de su clase (la del Dark Knight es la tercera); el archivo que se entrega solo trae filas para el Dark Knight, como el original. Está la comprobación `CheckAutoComboHack` del Common.dat. **Las skills que mueven aliados**: el Teleport Ally del Dark Wizard (`C3:B0`) manda a un miembro del grupo (o a sí mismo) a un punto a ocho casillas como mucho de donde está el miembro, sin que ninguno de los dos extremos sea zona segura, por el costo de la skill; el Summon Party del Dark Lord llama a todo el grupo: a cada miembro se le avisa (mensaje 273) de los cinco segundos y luego de cada segundo que queda, y llega a un sitio libre a cuatro casillas como mucho del lanzador (también en otro mapa), si `MoveSummon.txt` lo permite para su nivel, reset y nivel de cuenta (Atlans no admite Uniria ni Dinorant, Icarus pide alas o un Dinorant, un asesino puede ser rechazado, y quien tiene una ventana abierta o está muerto no puede ser llamado); caminar o cruzar una puerta durante la cuenta atrás la cancela (mensaje 272). Sin portar: las invocaciones como objetivo, las skills de monstruo que lanzan las inmunidades, las comprobaciones de zona segura y maná del Teleport propio del Dark Wizard |
| Muerte, experiencia, subir de nivel, respawn | ✅ | el cadáver permanece visible hasta el respawn (como en el original) |
| Loot | 🔶 | como en el original: items de quest, reglas de `ItemDrop.txt` / `ItemMoneyDrop.txt` y después la tirada genérica (`ItemRate` del monstruo contra `ItemDropRate`, tasas por mapa de `MapManager.txt`, drops excelentes desde el nivel 25), con las opciones de nivel/skill/suerte/adicional/excelente sorteadas de `ItemOptionRate.txt` / `ExcellentOptionRate.txt`; el zen es la experiencia que dio el monstruo. Las bolsas de jefes (Red Dragon, monstruos dorados, Skeleton King, White Wizard, Kundun) y las cajas, medallas y Starter Rings que se abren al tirarlos al suelo salen de `EventItemBag`. Los drop events de `DropEvent.dat` corren según su horario (el paquete lo trae vacío) |
| Chat, susurro (entre GameServers), party (+reparto de XP), amigos | ✅ | correo entre amigos ❌ |
| Quests | 🔶 | opcodes de info/estado de quest conectados a las tablas reales |
| Guild | 🔶 | lista / ventana del maestro / crear; falta trabajo de esquema para terminarla |
| Devil Square | ✅ | máquina de estados completa, tickets, spawns por etapa, ranking, item del primer puesto desde su bolsa, límite diario de entradas y límite de PK |
| Blood Castle | 🔶 | el evento completo: entrada, etapas (monstruos, puente, puerta, estatua), el arma del Arcángel y el Arcángel, recompensas y premio en bolsa. El paquete solo trae el mapa del castillo 1. Límite diario de entradas y límite de PK como en el original |
| Kalima | 🔶 | la puerta del Lost Map (se abre donde se tira, para su dueño y su grupo, cinco entradas o tres minutos) y la puerta al siguiente Kalima cuando muere un jefe. El paquete solo trae el mapa de Kalima 1 |
| Chaos Castle | 🔶 | el evento completo: entrada (Armor of Guardsman + cuota), la espera en la zona segura, la pelea con monstruos y con los otros jugadores mientras caen los anillos exteriores de la arena, el golpe que lanza a los jugadores, puntajes, experiencia, la bolsa del ganador y el ranking, el sorteo de las cuotas entre los participantes. El paquete solo trae el mapa del castillo 1. Probado de punta a punta con dos clientes simulados, todavía no en el cliente real |
| Personal Shop, Golden Archer, PartyMatching | ❌ | la mayoría necesita una segunda cuenta para probarse bien |
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
2. El resto de las **mascotas**: los datos de mascota en comercio, el serial para todo lo que crea el servidor (`GDCreateItemSend`), el Earthshake del Dark Horse.
3. Completar Guild (esquema + rangos + marcas), Personal Shop, Golden Archer.
4. Invocaciones como objetivo y las skills de monstruo (las inmunidades que lanzan).
5. Recarga de config en caliente + endurecer el AdminPanel (autenticación por usuario).
6. Seguir la auditoría del cliente: llamadas restantes a la capa de red heredada, quitar código exclusivo de Season 6.
7. Build Linux del cliente.

## Arreglado recientemente

Bugs encontrados jugando con un cliente real en la última sesión (todos verificados con recompilación + tests):

- **Número de daño al matar:** el golpe final mostraba dos números. Como el servidor original, ahora no manda paquete de daño; su número viaja en el paquete de recompensa, solo para quien lo dio (o su party).
- **Cliente original:** no conectaba con `127.0.0.1` — el `main.exe` de Webzen rechaza esa dirección exacta. Ver [Primeros pasos](GETTING_STARTED.md#6-conectar-un-cliente).
- El GameServer no atendía el **borrado de personaje** ni el **guardado de teclas rápidas**.
- **Chaos Castle y PvP:** el evento no existía y los jugadores no podían atacarse. Ya están los dos (ver la tabla). De paso: la Cape of Lord se ignoraba al calcular el daño, un castillo sin más horarios en su calendario se quedaba con los puntajes pendientes, y la respuesta a un comando de chat desconocido era solo el nombre del comando (una sobrecarga de `Loc.F` tomaba el formato por un idioma).
- **Sistema de PK:** los jugadores no podían dañarse fuera del Chaos Castle. Ahora sí, con los niveles de PK, la defensa propia, el tiempo de PK, la caída de items y los límites del original (ver la tabla). Con `NonPK = 1` en `GameServerInfo - Common.dat` se vuelve a apagar el PvP fuera de los eventos.
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

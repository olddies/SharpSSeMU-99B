# El port al protocolo 0.99B

Este cliente nació como el fork de Season 5.2 de Sven, encaminado a Season 6.
Este documento describe el trabajo paralelo de hacerlo hablar el protocolo de
**MU Online 0.99B**, el del emulador SSeMU 2.1.7, para usarlo contra el servidor
propio (SharpSSeMU, en este mismo repositorio) sin depender del binario cerrado del
cliente original.

Si vas a tocar red, item, personaje o interfaz, leelo antes: casi todo lo que
está acá costó encontrarlo, y varios de los errores no dan síntoma hasta mucho
después.

## Por qué no alcanza con cambiar unos números

Los dos dialectos se parecen lo suficiente como para que el cliente arranque,
se conecte y muestre una pantalla de login, y lo bastante distinto como para
que después nada funcione. Los tamaños de struct difieren en uno o dos bytes,
los mismos campos están en otro orden, y varios valores viajan con otra
codificación.

El resultado es que **los errores no fallan: mienten**. Un struct más chico que
el del wire lee los campos corridos y muestra otros números. Uno más grande
hace que `safe_cast` descarte el paquete y la función entera deje de existir en
silencio. Ninguno de los dos rompe nada visible.

Por eso la regla de este port es: **nada se transcribe a mano**.

## De dónde sale la verdad

### El generador

`tools/protogen/` lee las fuentes C++ del emulador y emite
`src/source/Protocol099B/Protocol099B.generated.h`: 259 structs, 211 con su
opcode, cada uno con `static_assert` de tamaño y de offset de cada campo.

Lo importante no es que ahorre tipeo, sino que modela el layout de MSVC x86 —
alineación natural, `#pragma pack` incluido el que aparece a mitad de un
struct, y **el relleno de cola**. Ese relleno viaja por la red, porque el
servidor manda `sizeof(struct)`, no la suma de sus campos. Transcribir los
structs a ojo produce exactamente los mismos seis bugs de layout que ya
habíamos encontrado y arreglado uno por uno del lado del servidor.

Si el protocolo cambia, se regenera. No se edita el archivo generado.

### Las auditorías

Tres herramientas convierten "compatibilidad total" en una lista verificable:

| herramienta | responde |
|---|---|
| `tools/protogen/audit_client_structs.py` | ¿qué receptores castean a un struct de otro tamaño que el del wire? |
| `tools/protogen/audit_client_coverage.py` | ¿qué opcodes manda el servidor que el cliente no atiende? |
| `tools/protogen/audit_muservercs.py` | ¿qué structs del servidor no coinciden con el emulador? |
| `tools/gamedata/audit_item_table.py` | ¿qué items describe el cliente distinto de como el servidor los aplica? |
| `tools/gamedata/audit_skill_table.py` | lo mismo con las habilidades |
| `tools/gamedata/audit_move_table.py` | ¿el menú de movimiento manda al mapa que dice? |
| `tools/gamedata/audit_monster_table.py` | ¿el cliente sabe el nombre de todos los monstruos? |
| `tools/gamedata/audit_shop_prices.py` | ¿el servidor cobra lo que su propia tabla declara? |

La primera es la que más pagó: mide los structs del cliente con las mismas
reglas de layout que los del servidor y marca los dos casos —lee corrido, o
descarta el paquete— antes de que alguien los vea en pantalla.

## La tabla de items no era el problema que parecía

Durante un tiempo esto figuró acá como riesgo pendiente: la tabla del cliente es
la de Season 6, así que si alguna versión renumeró items dentro de un grupo, los
objetos se dibujarían con nombre e ícono equivocados.

Medido, resultó falso. De los **349 items que define el GameServer, el cliente
define los 349 en el mismo (grupo, índice)**, con el mismo nombre, el mismo
tamaño en inventario, la misma ranura, la misma habilidad y los mismos
requisitos. La numeración es la misma tabla; Season 6 la extendió por arriba, no
la reordenó.

Lo que sí había eran **12 valores** en los que el cliente decía algo distinto de
lo que el servidor aplica, repartidos en seis items: el daño de las tres armas
del Arcángel, la defensa del Plate Shield, un nombre mal escrito (*Red Sprit
Armor*) y las clases que pueden usar el Dark Raven. Eso no rompe nada — el
servidor calcula igual — pero el tooltip miente.

`audit_item_table.py --sincronizar` los copió del servidor al cliente, en los
tres idiomas. Vale la pena volver a correrlo cada vez que se toque `Item.txt`:
el cliente no se entera solo.

Dos detalles del formato, por si hay que volver:

- El `.bmd` es el formato *legacy* de 84 bytes por registro (nombre de 30 más
  los campos). `ItemDataLoader` elige el formato por el tamaño del archivo.
- El checksum (`GenerateCheckSum2`, clave `0xE2F1`) se calcula sobre los datos
  **ya cifrados**. La herramienta lo verifica al leer, así que un archivo
  corrupto se detecta antes de escribir nada.

### Las habilidades no se sincronizan, y es a propósito

La tabla de habilidades parece el mismo caso y no lo es: los dos lados usan las
mismas columnas para cosas distintas. `audit_skill_table.py` informa y no toca
nada.

Dos motivos concretos, los dos verificados contra el código:

- `Range` vale **0 en el servidor para las habilidades de área** (Evil Spirit,
  Nova, Twisting Slash), porque a esas el alcance se lo da `Radio`. En el
  cliente ese mismo número es la distancia desde la que te deja *lanzar*
  (`CSkillManager::GetSkillDistance`). Copiar el 0 las dejaría inlanzables.
- Las columnas de requisitos (`ReqLevel`, `ReqEnergy`, `ReqLeadership`,
  `ReqKillCount`) están en cero para **todas** las habilidades del servidor. El
  cliente sí los tiene y los usa. Copiarlos borraría todos los requisitos de la
  interfaz.

Con eso separado, de 58 habilidades quedan cuatro diferencias y **ninguna cambia
lo que ve el jugador**: dos son de un ataque de monstruo, y las otras dos son
alcances de habilidades que el cliente resuelve por otro camino
(`AT_SKILL_ADD_CRITICAL` está en la lista de excepciones de `ClassAttack`). Las
seis diferencias de `Effect` y la de clase del Ice Storm caen en campos que el
cliente carga y nunca lee — el informe las separa para que nadie las persiga.

Trece nombres difieren (*Soul Barrier* contra *Mana Shield*, *Cometfall* contra
*Blast*). El que se ve en pantalla es el del cliente; el del servidor sólo sale
en sus logs. Alinearlos es una decisión de fidelidad, no un arreglo.

### Los warps sí estaban rotos

De las tres tablas duplicadas, ésta era la única donde no coincidir **rompe el
juego**, no sólo el tooltip.

Al elegir un destino, el cliente manda el `index` de su propia tabla. El
servidor lo busca en su `Data/Move/Move.txt`. Las dos numeraciones habían
divergido: **20 de 23 destinos tenían el índice de otro**. Pedías Dungeon y
caías en Devias2, pedías Tarkan y caías en LostTower5.

Y no fallaba a la vista, por esto de `OnTeleportMoveAsync`:

```csharp
var move = _moves.Get(recv.MoveIndex);
int gateNumber = move?.GateNumber ?? recv.MoveIndex;
```

Si el índice no existe, **el número se usa como puerta** y el viaje ocurre
igual: sin cobrar el zen y sin mirar el nivel. Los tres destinos cuyo índice no
existía del lado del servidor —Devias, Devias2 y Devias3— caían por ahí.

Además el menú ofrecía 14 mapas que este servidor no tiene (Elveland, Aida,
Kanturu, Karutan, Raklion, Vulcanus, Swamp), que por ese mismo camino te movían
a cualquier lado, y no ofrecía CustomArena, que sí existe. Lorencia y Noria
cobraban 2000 en vez de 1000, así que el cliente te negaba el viaje con 1500 en
el bolsillo.

`audit_move_table.py --sincronizar` reconstruye la tabla con la numeración del
servidor. La unión entre las dos tablas se hace **por número de puerta**, no por
índice ni por nombre: la puerta es la misma a los dos lados y es única. Que los
nombres coincidan en las 23 confirma que la unión es correcta.

Dos cosas que hay que saber si se toca:

- **Los nombres se conservan, y se copian en bytes.** La ventana decide si podés
  viajar comparando el nombre del mapa contra `I18N::Game::Icarus` y
  `I18N::Game::Atlans` para exigirte alas o montura, así que pisarlos rompería
  esos chequeos. Y se copian sin decodificar porque **los archivos no están
  todos en la misma codificación**: el nombre en español de Icarus es
  `CD 63 61 72 6F`, o sea `Í` en Latin-1, aunque el cliente los lea con
  `ConvertFromUtf8`. La primera versión de la herramienta decodificaba a UTF-8 y
  volvía a codificar, y dejaba `?caro`. Lo agarró la verificación de después de
  escribir, que ahora compara los nombres byte a byte.
- **`anStrifeIndex` tiene el 42 escrito en el código** como el mapa de Gens.
  Con la numeración vieja ese índice era Vulcanus; con la del servidor no
  corresponde a nada. En este servidor no hay mapa de Gens, así que queda
  inerte — pero si algún día `Move.txt` usa el 42, se va a marcar solo.

### Monstruos: faltaba uno

De la lista de monstruos el cliente sólo usa el número y el nombre; las
estadísticas las calcula el servidor. De los 231 que define el GameServer, el
cliente nombraba 230. Faltaba el **42, Red Dragon**, en los tres idiomas:
`getMonsterName` devuelve `"()"` cuando no encuentra el número.

No se veía porque el 42 no aparece en ningún archivo de spawn y las invasiones
no están implementadas del lado del servidor — pero era cuestión de tiempo, no
de si. Agregado a los tres `NpcName_<idioma>.txt`, al lado de la familia
temática de dragones (`43 Golden Budge Dragon`, `44 Dragon`) que ya estaba ahí:
*Red Dragon* / *Dragón Rojo* / *Dragão Vermelho*. Por eso esta auditoría no
sincroniza como la de items: los nombres del cliente están traducidos y los del
servidor están en inglés, así que agregar uno es una traducción a mano, no una
copia.

### Las tiendas: el precio que se muestra no es el que se cobra

Las tiendas no mandan precios. `Data/Shop/*.txt` sólo dice qué vende cada NPC, y
cada lado calcula el número por su cuenta: el cliente para mostrarlo
(`ItemValue`, ZzzInfomation.cpp:1457), el servidor para cobrarlo
(`ComputeShopBuyPrice`).

La parte que sí coincide es la principal: el campo `BuyMoney` del servidor y el
`iZen` del cliente son iguales en los 349 items, y los dos lados lo usan como
primera opción. Alas y orbes están bien.

Lo que no coincidía eran los objetos con precio especial — joyas, entradas de
evento, pociones de asedio. El servidor **traía la tabla correcta** en
`Data/Item/ItemValue.txt`, con los mismos números que el cliente tiene escritos
a mano (Bless 9.000.000, Soul 6.000.000, Chaos 810.000, Fruits 33.000.000...).
**Y no la leía nadie**: el nombre no aparecía en ningún `.cs` ni en el `.ini`.
Caía a la fórmula general, que da otra cosa:

| | declarado | daba la fórmula |
|---|---|---|
| Jewel of Bless | 9.000.000 | 18.700 |
| Jewel of Life | 45.000.000 | 72.600 |
| Fruits | 33.000.000 | 100 |
| Jewel of Chaos | 810.000 | 40.082.300 |

Son 72 filas y no coincidía ninguna. Vender una Bless daba 6.200 en vez de
3.000.000; un Chaos se compraba a cuarenta millones.

**Ya está arreglado del lado del servidor**, que es donde correspondía: sería un
error hacer que el cliente muestre 18.700, porque el número bueno para 0.99B es
el de `ItemValue.txt` y es el que el cliente ya mostraba. El arreglo es
`SharpSSeMU/src/MuServer.GameServer/World/ItemValue.cs`, que carga la tabla y la
consulta entre `BuyMoney` y la fórmula general.

Tres cosas que dejó el episodio:

- **El test del servidor fijaba el precio equivocado.**
  `gameserver_shop_e2e_test.py` afirmaba que la Bless se compra a 18.700, que es
  exactamente lo que daba la implementación. Estaba escrito contra el código y
  no contra la fuente — el mismo error que el test del serial de 17 bytes.
  Ahora comprueba que vender pague 3.000.000, y el comentario dice de qué fila
  del archivo sale ese número.
- **Los precios que escalan por cantidad quedaron afuera.** El original
  multiplica algunos de estos valores por el stack o por la durabilidad restante
  (el Symbol of Kundun vale 30.000 por unidad, las flechas valen su precio por
  la fracción de durabilidad que les queda), y cada item lo hace a su manera. El
  archivo no dice cuáles escalan, así que el puerto devuelve el valor tal cual y
  lo documenta. Un stack se cotiza como una unidad: bajo, pero del orden
  correcto.
- **`audit_shop_prices.py` quedó de guardia.** Ya no busca la discrepancia
  —está cerrada— sino que las 72 filas apunten a items reales, que ninguna quede
  tapada por un `BuyMoney`, y cuánto costaría que la tabla volviera a
  desconectarse.

## Las tres capas de cifrado

El socket del GameServer lleva tres, en este orden al recibir:

1. **Cifrado de flujo** (`StreamCipher099B`) — puerto de `HackCheck.cpp`. Tapa
   *todo*, incluidos los bytes de tipo y de tamaño.
2. **Cifrado de bloque** (`BlockCipher099B`) — SimpleModulus, 8 bytes de claro
   por 11 de cifrado. Las claves resultaron byte por byte idénticas a las de
   OpenMU.
3. **Ofuscado XorData** — y acá hay una asimetría: **el cliente ofusca lo que
   manda, el servidor no ofusca lo que responde**.

La primera capa es la razón de que el socket de juego sea nativo en vez de
usar la librería C#: si el cifrado tapa el tamaño, ningún framer que trabaje
sobre el flujo cifrado puede separar paquetes. Hay que descifrar antes de
segmentar. El ConnectServer, que va en claro, sí sigue usando la librería.

### El serial de 17 bytes

La clave del cifrado de flujo se deriva del `ServerSerial` del `.ini`, que es un
campo de **17 bytes**: los 16 caracteres más el terminador. El servidor rellena
hasta ese largo antes de derivar.

Derivar sobre los 16 caracteres da otra `key1` y corre el flujo entero. Y esto
es lo aleccionador: **había un test que lo daba por bueno**, porque comparaba mi
implementación contra sí misma. Los dos lados tienen que coincidir con el
*original*, no entre ellos. Lo detectó el primer paquete de un servidor real.

Desde entonces los tests de este módulo comparan contra una reimplementación
del algoritmo del emulador, o contra bytes capturados de un servidor vivo, y
dicen de dónde salió cada valor esperado.

## Las codificaciones que engañan

### Dos codificaciones de clase, en el mismo protocolo

| dónde | fórmula | valores |
|---|---|---|
| base de datos, crear personaje | `base << 4` | 0, 16, 32, 48, 64 |
| CharSet (apariencia) | `base << 5` | 0, 32, 64, 96, 128 |

El CharSet corre la clase un bit más porque los bits bajos llevan el
ViewState. Y el par de paquetes de creación usa **una en cada sentido**: el
pedido va en formato de base de datos y el servidor lo convierte al del CharSet
antes de contestar.

Confundirlas es silencioso: los dos decodificadores devuelven un número válido,
sólo que el equivocado — un Dark Knight vuelve como Fairy Elf. La única que
coincide es la clase 0, y por eso crear un Dark Wizard funcionaba mientras todo
lo demás fallaba con "cuenta llena" (el servidor no encontraba la fila en
`default_class_type` y devolvía `result=2`, un mensaje que no delata la causa).

### El item son cinco bytes, no doce

`ItemInfo099B` porta `ItemByteConvert`. Tres cosas no se ven leyendo el struct:

- El **noveno bit del índice** viaja en el bit alto del byte 3. Perderlo
  convierte cualquier item de la segunda mitad de la tabla en otro 256 lugares
  antes.
- El **tercer bit del nivel de opción** pesa 4, no 1: los otros dos están en el
  byte 1 y ése en el 3.
- **Cinco ceros** significan "sin item", y hay que mirarlo antes de desarmar
  nada, porque el índice 0 es una espada de verdad.

Ojo también con los nombres del emulador: `Option3` es el nivel de opción
(+4/+8/+12/+16) y `NewOption` es la máscara de excelente. Es fácil leerlos al
revés.

### Los índices de item tienen dos numeraciones

0.99B avanza de a 32 por sección; el cliente, de a 512 por grupo. Las dieciséis
secciones son los mismos dieciséis grupos y en el mismo orden, así que la
conversión es sólo cambiar el paso. Un número por encima de 32 es un item que
0.99B no tiene: `ToWireItemIndex` lo rechaza en vez de recortarlo, porque
recortado el servidor entendería otro objeto.

### El zen del piso salía siempre en cero

El dinero tirado en el suelo **no** viaja con el formato normal de item. El
servidor manda el índice del zen (sección 14, sub 15) y mete el monto crudo en
los bytes 1, 2 y 4 del `ItemInfo` (`CViewport::GCViewportItemSend`,
Viewport.cpp:903-911), sin pasar por la codificación de nivel/durabilidad.

`ReceiveCreateItemViewport099B` no distinguía ese caso: pasaba los cinco bytes
por `DecodeItemInfo` como cualquier item y llamaba a `CreateItemDrop`. El monto
se leía como nivel y durabilidad, y el montón se dibujaba como un objeto
cualquiera, así que el zen siempre aparecía en cero.

Ahora `DecodeDroppedMoney` reconoce el índice del zen y desarma el monto (el
inverso exacto de lo que arma el servidor), y esa rama llama a `CreateMoneyDrop`,
que es la que el cliente ya usaba para el paquete 0x20 de dinero suelto.

### El número de daño salía como el cartel "HIT"

`ReceiveAttackDamage099B` mandaba todos los golpes a
`ReceiveAttackDamageCastle`, que es la variante de **asedio al castillo**. Esa
función fuerza el valor a `-2` --el sprite "HIT"-- y con el daño real sólo elige
el color del cartel:

```cpp
if (accumDamage > 0)
{
    rstDamage = -2;               // "HIT", nunca la cifra
    if (accumDamage < 1000)       { /* rojo */ }
    else if (accumDamage < 3000)  { /* naranja */ }
}
```

Esconder el número es lo que el asedio hace a propósito, pero acá se aplicaba a
todo el combate. La función normal, `ReceiveAttackDamage`, tiene la misma firma y
sí dibuja la cifra: es a la que se llama ahora.

### Un head transcrito a mano: el zen del trade iba en 0x3B

`BuildTradeMoneyRequest` tenía el head escrito como literal, `0x3B`, en vez de
tomarlo del struct generado. Ese valor sale de un comentario equivocado en
`Trade.h` del emulador (`// C1:3B`); el opcode real es **0x3A**, que es el que
despacha `Protocol.cpp` y el que `protogen` deja en
`PMSG_TRADE_MONEY_RECV::kHead`.

Lo tramposo es que el servidor de este mismo proyecto había transcrito el mismo
comentario, así que los dos lados estaban de acuerdo y el intercambio de zen
parecía andar. Contra un servidor 0.99B de verdad no andaba: el paquete se
descarta en silencio y el zen queda en cero. Es el motivo por el que los structs
se generan en vez de copiarse.

### Big-endian a mano

Varios campos declarados como enteros de dos o cuatro bytes se llenan byte por
byte, empezando por el más significativo (los `SET_NUMBER*` del original).
Asignarlos como enteros nativos los invierte. Pasa con los índices de objeto, el
dinero del trade y el del baúl.

En el caso del dinero el error es peor que un cero: da una cifra enorme, así que
no se nota hasta que alguien pierde plata.

## Cómo está armado

```
src/source/Protocol099B/
├── Protocol099B.generated.h   los structs, generados
├── Wire099B.{h,cpp}           arma paquetes, sin transporte
├── Send099B.{h,cpp}           los manda por la conexión
├── ItemInfo099B.{h,cpp}       los cinco bytes de item
├── CharSet099B.{h,cpp}        apariencia y clase
├── StreamCipher099B.{h,cpp}   capa 1
├── BlockCipher099B.{h,cpp}    capa 2 y ofuscado
├── GameFramer099B.{h,cpp}     recibir: descifrar y segmentar
├── GameEncoder099B.{h,cpp}    enviar: ofuscar y cifrar
└── GameSocket099B.{h,cpp}     el socket, no bloqueante
```

`Wire099B` no depende del transporte a propósito: los tests verifican los bytes
exactos sin linkear la red.

`tools/probe099b/` es una sonda que se ejecuta a mano contra un servidor vivo.
Cubre lo que los tests no pueden —que las claves, los tres cifrados y el
framing se combinen bien contra el servidor real— y por eso no está en CTest.

## Cuidado con el transporte

El socket de juego es nativo y **no está registrado en la tabla de conexiones de
la librería C#**. Cualquier función de envío que llame a `dotnet_Send*` con el
handle de esa conexión no llega al servidor, y en el peor caso va a parar a otro
socket.

`PacketFunctions_Base::GetHandle()` corta ese camino: cuando el transporte es
nativo devuelve un handle inválido y deja un aviso en el log, una sola vez.
**Si ves ese aviso, la función que se usó todavía no está portada.**

Para agregar un envío: constructor en `Wire099B`, función en `Send099B`, test
que fije los bytes, y recién ahí cambiar el punto de llamada.

## Dónde estamos

| | estado |
|---|---|
| recibir | todos los que el GameServer manda al cliente, salvo `0x88` |
| enviar | 57 de los 59 opcodes que el GameServer acepta |
| suite | 152 tests |

Los dos que faltan del lado de enviar (`0x04` y `0x05`) no son paquetes del
cliente: son mensajes que el GameServer recibe del DataServer y comparten el
espacio de opcodes en el mismo `switch`.

### La máquina del caos: lo que decía acá estaba mal

Esto decía que `0x88` (la tasa de éxito de la máquina del caos) no tenía
receptor porque el cliente la calcula por su cuenta con `g_MixRecipeMgr`. Es
cierto que no tiene receptor. La razón que daba era una suposición sin
verificar, y estaba mal.

Lo que hay de verdad:

- **`g_MixRecipeMgr` está muerto.** Es el sistema de recetas de Season 6
  (Goblin Points, Jerridon, Chaos Card...), y carga sus fórmulas desde
  `mix.bmd` con `CMixRecipeMgr::OpenRecipeFile`. Esa función **no la llama
  nadie**: `mix.bmd` nunca se abre. Sin recetas cargadas, `GetCurRecipe()`
  siempre da `NULL`, `IsReadyToMix()` nunca se pone en `true`, y la ventana de
  combinar de Season 6 (`INTERFACE_MIXINVENTORY`, la que abre la conversación
  con el NPC de valor 3) queda inerte: nunca hay receta que calce, así que el
  botón de combinar nunca se habilita. No calcula un número distinto al del
  servidor — no calcula nada, porque nunca llega a intentarlo.
- **El wire real de 0.99B sí separa preguntar de combinar.** El generador lo
  saca directo de `ChaosBox.cpp`: el cliente manda `0x88`
  (`PMSG_CHAOS_MIX_RATE_RECV`, sólo el tipo) para preguntar la tasa antes de
  decidir, el servidor contesta `0x88` (`PMSG_CHAOS_MIX_RATE_SEND`, tasa y zen
  requerido) sin tocar nada; recién cuando el jugador confirma, el cliente
  manda `0x86` para combinar de verdad. `Send099B` tiene
  `SendChaosMixRate` desde hace tiempo, pero no lo llama ninguna ventana: no
  hay ningún camino en este cliente que hoy pida la tasa por `0x88`.
- **Mientras tanto, el servidor tenía un bug real en `0x88`.** No es cosmético:
  `OnChaosMixRateAsync` llamaba a la misma función que ejecuta la combinación
  de verdad (`ChaosMixLogic.CalculateAndExecuteMix`) — cobraba el zen, vaciaba
  la Chaos Box y tiraba el dado **con sólo preguntar**. Ningún cliente de este
  repo llega a dispararlo hoy, porque ninguno manda `0x88` todavía, pero
  cualquier cliente 0.99B de verdad que sí pregunte antes de combinar (que es
  el flujo normal) habría perdido los items y el zen sin haber aceptado nada.
  Arreglado en `SharpSSeMU/src/MuServer.GameServer/World/ChaosMixLogic.cs`: la
  función ahora toma un parámetro `execute`, y las seis fórmulas de mezcla
  calculan la tasa igual pero sólo cobran/vacían/tiran el dado cuando
  `execute: true`. `OnChaosMixRateAsync` pasa `execute: false`.

### Las fórmulas de mezcla tampoco eran fieles

`ChaosMixLogic.cs` decía en su propio comentario ser un "puerto exacto" de
`ChaosBox.cpp`. No lo era. La tasa de éxito salía de una fórmula inventada
(`10 + Σ nivel×5`) en vez de la tabla de configuración real
(`GameServerInfoChaosMix`, cargada desde el principio pero nunca conectada a
nada — `Program.cs` la leía en una variable local y ahí se quedaba), y el item
de éxito salía de un array de tres armas fijas en vez de la lista real de
`Data/EventItemBag/Special/*.txt`.

Esto se encontró y arregló leyendo el **código fuente original del emulador**,
que está en el repo (`Source/Source/Emulator 0.99 (2.1.7)/GameServer/`) y no
se había mirado hasta ahora para este sistema. De ahí salió también una trampa
real: en `ChaosBox.h` la constante `CHAOS_MIX_WING1` (el valor `7` del wire)
dispara la función `Wing2Mix(tipo=0)`, y `CHAOS_MIX_WING2` (`11`) dispara
`Wing1Mix()` — los nombres están cruzados. El puerto anterior tenía los
ingredientes de las dos mezclas invertidos por seguir el nombre de la
constante en vez de la función que dispara de verdad.

Lo que quedó fiel, verificado línea por línea contra el original: ingredientes
requeridos, fórmulas de tasa y de zen, y de qué tabla de configuración sale
cada una — para Chaos Item, Plus Item +9→+10 y +10→+11, Fruit, y las dos
mezclas de ala (agregando además el tipo "Cape", que faltaba). Lo que no: el
item de éxito en las mezclas que crean uno nuevo se elige entre los candidatos
reales del archivo correspondiente, pero sin el motor completo de pesos por
sección de `ItemBagEx` (que además, tal como viene de fábrica, tiene la
probabilidad en 0 en los cuatro archivos que hacían falta — replicarlo literal
habría dejado la máquina sin entregar nunca nada). Ese motor es compartido con
Devil Square, Blood Castle y los drops de monstruo, y merece su propio puerto
en vez de uno apurado como dependencia de esto. El detalle completo de qué es
fiel y qué no está en el doc-comment de la clase.

Devil Square, Dinorant, Blood Castle y las dos mezclas de mascota siguen sin
portar: necesitan estado de eventos en vivo que este servidor no trackea.

### El lado del cliente: se abre la ventana correcta, el botón todavía no combina

Se corrigieron dos bugs de despacho, los dos por confundir el subcódigo de un
paquete con un concepto de otra temporada:

- **`0x31` subcódigo 3 no es "la mezcla terminó".** Es la lista de lo que hay
  AHORA en la Chaos Box (`GCChaosBoxSend`, mandada al abrir la ventana). El
  receptor lo trataba como si la mezcla ya hubiese terminado y limpiaba la
  ventana con sonidos de éxito/rotura — lo que apagaba el estado de la
  ventana justo al abrirla, antes de que el jugador pusiera nada. El
  subcódigo 5 tampoco es "resurrección fallida": es la misma lista pero para
  la ventana del Entrenador/mascotas, no portada — ahora se ignora en vez de
  mostrar un mensaje inventado.
- **Mover un item a la Chaos Box no lo mostraba ahí.** El receptor genérico de
  mover ítems (`0x24`) enruta por el número de slot nomás; nunca miraba
  `result` (que dice a qué contenedor fue el item — el servidor manda `3` para
  la Chaos Box). Un item movido a la caja entraba ahí del lado del servidor
  pero el cliente lo seguía dibujando en el inventario normal.

Con esos dos arreglados, la ventana abre, muestra lo que ya había en la caja,
y refleja los items que se arrastren ahí — pero el botón de combinar sigue
mostrando "te faltan items" siempre, porque `case 3` ya no llama al sistema de
recetas muerto y no hay nada que lo reemplace todavía: falta decidir qué tipo
de mezcla corresponde a lo que hay en la caja (no hay una receta que lo diga:
en 0.99B esto lo elegía el jugador con una pestaña, información que no está
en ninguna fuente disponible) y mandar `0x88` para mostrar el porcentaje antes
de combinar. Es la pieza que falta para que la ventana funcione de punta a
punta, y necesita probarse contra el cliente real — algo que no puedo hacer
yo mismo.

Lo que falta, y por qué:

- **La interfaz** sigue con el aspecto de Season 6. No es un intercambio de
  archivos; ver [`ui-099b.md`](ui-099b.md).
- **El medidor de AG** quedó en la posición de Season 6.
- Un test end-to-end de combate falla porque el personaje de prueba muere contra
  el monstruo de prueba. Es balance del servidor, no del port.

### Recoger items del piso: se traba después del primer intento

Reportado en pruebas reales: el zen se podía recoger una sola vez, y después
ningún item más se podía levantar (ni zen ni objetos) por el resto de la
sesión. El síntoma apuntaba al cliente, no al servidor: cada click de
"recoger" (`MOVEMENT_GET`, `ZzzInterface.cpp`) está guardado detrás de
`SendGetItem == -1` para no mandar dos pedidos del mismo item mientras se
espera la respuesta -- y ya había un comentario del propio código sobre la
variable (`WSclient.cpp:428`) prediciendo exactamente este bug: *"it may cause
the stuck client bug, so that players can't pick up anything anymore"*.

La causa: `ReceiveGetItem099B` (el receptor de `PMSG_ITEM_GET_SEND` que
realmente está enchufado para 0.99B, no el `ReceiveGetItem` viejo que sí lo
hacía bien) nunca ponía `SendGetItem = -1` de vuelta en ninguna de sus cuatro
salidas -- ni cuando fallaba (`NOT_GET_ITEM`), ni cuando era zen, ni cuando el
item no entraba en el inventario, ni en el camino de éxito normal. La primera
respuesta que llegaba, sea cual sea, dejaba la bandera trabada para siempre;
el próximo click ni siquiera llegaba a mandar el paquete al servidor. Se portó
el mismo reset que ya tenía el receptor del dialecto viejo, agregado a las
cuatro salidas de la versión 099B.

### Subir de nivel: no salía el aura dorada

Otro reporte de pruebas reales. `ReceiveLevelUp099B` (el receptor de
`PMSG_LEVEL_UP_SEND` enchufado para 0.99B) copiaba las stats nuevas -- vida,
maná, experiencia, puntos -- pero nunca disparaba el efecto visual ni el
sonido. El receptor del dialecto posterior (`ReceiveLevelUp`, todavía en el
archivo, sin usar para 0.99B) sí lo hace: crea los destellos dorados alrededor
del personaje (`CreateJoint(BITMAP_FLARE, ...)`, 15 veces, más un efecto
`BITMAP_MAGIC` -- o la versión "Master" con 20 destellos si la clase ya es de
segunda evolución) y reproduce `SOUND_LEVEL_UP`. Se portó ese mismo bloque,
sin cambios, al final de `ReceiveLevelUp099B`.

### El grupo (party) enviaba al vacío: tres botones que nunca migraron de la capa Dotnet

Encontrado auditando, sin cliente vivo, cuáles de las funciones `Mu099B::Send*` declaradas en
`Send099B.h` no tenían ningún llamador real (`grep` de cada una contra el árbol completo). De 59, doce
no aparecían nunca invocadas; la mayoría son sistemas genuinamente sin UI todavía (Golden Archer,
mascotas, hardware ID, el keepalive anti-latencia) y no ameritan nada. Pero tres SÍ tenían un botón de
verdad detrás, y ese botón seguía llamando a la capa Dotnet vieja (`SocketClient->ToGameServer()->Send*`,
el protocolo de OpenMU que este puerto viene reemplazando, ver `src/source/Dotnet/`):

- **`CommandParty`** (`NewUICommandWindow.cpp`, el menú contextual "Party" al hacer clic derecho sobre
  otro jugador) llamaba a `SendPartyInviteRequest` en vez de `Mu099B::SendPartyRequest` -- el mismo
  archivo ya tiene el patrón correcto dos líneas arriba, en `CommandTrade`, así que esta única función
  se quedó afuera de esa migración.
- **`CPartyMsgBoxLayout::OkBtnDown`/`CancelBtnDown`** (`NewUICommonMessageBox.cpp`, aceptar/rechazar
  una invitación que te llega) llamaban a `SendPartyInviteResponse` en vez de
  `Mu099B::SendPartyRequestResult`.
- **`CNewUIPartyInfoWindow::LeaveParty`** (`NewUIPartyInfoWindow.cpp`, el botón de salida en la ventana
  de grupo -- se reusa tanto para irse uno mismo como, si sos el líder, para expulsar a otro) llamaba a
  `SendPartyPlayerKickRequest` en vez de `Mu099B::SendPartyDeleteMember`.

Un GameServer 0.99B real (y este puerto: `OnPartyRequestResultAsync`/`OnPartyDelMemberAsync`, ya
implementados y esperando del lado de SharpSSeMU) no habla el protocolo de OpenMU, así que estos tres
botones no hacían nada -- ni error visible ni desconexión, el paquete se iba directo a una capa muerta
o a un formato que el servidor real no reconoce. Los tres se corrigieron para llamar a la función
`Mu099B::` correcta; recompila limpio y el suite completo (152 tests) sigue en verde -- no hay un test
que cubra "el botón llama a la función correcta" porque eso requiere un cliente corriendo, así que este
tipo de bug sólo aparece grepeando a mano, como acá.

**Seguimiento: el mismo bug, en el flujo de misiones.** Antes de tocar cualquiera de los 12 `Mu099B::
Send*` sin llamador se confirmó primero contra `MuClient/Data/Interface/` (el cliente 0.99B real, no
sólo el emulador -- ver la nota de arriba sobre no confundir Season 6 con 0.99B) que Quest es un
sistema legítimo (`quest1.OZJ`/`quest2.OZJ` existen ahí) y no invención de temporada posterior. Con
eso confirmado, `Mu099B::SendQuestState`/`SendQuestInfo` (opcodes 0xA2/0xA0, ambos ya implementados
del lado de SharpSSeMU -- `OnQuestStateAsync`/`OnQuestInfoAsync`) tampoco tenían ningún llamador real,
por el mismo motivo que Party: cuatro sitios seguían en la capa Dotnet vieja.

`CSQuest` (`GameLogic/Quests/CSQuest.cpp`) es el motor central que hace avanzar el diálogo de misión
-- `ProcessNextProgress()` es lo que corre cuando el jugador clickea "continuar" y la condición ya se
cumplió. Ahí, y en los otros tres lugares donde la ventana de misión (`NewUINPCQuest.cpp`) o el propio
diálogo de NPC (`ZzzInterface.cpp`, al hablarle a un NPC con `g_csQuest.IsInit()`) piden lo mismo,
llamaban a `SendLegacyQuestStateSetRequest`/`SendLegacyQuestStateRequest` -- pese al nombre, "Legacy"
acá se refiere al dialecto de misión más simple de **OpenMU** (todavía Dotnet, con su propio enum
`LegacyQuestState`), no a nada de 0.99B. Los cuatro se corrigieron a `Mu099B::SendQuestState`/
`SendQuestInfo`. Un detalle que vale la pena dejar escrito para no reabrirlo: el segundo campo del
paquete (`QuestState`) existe en el wire (`PMSG_QUEST_STATE_RECV`, confirmado con `protogen` contra
`Quest.h` del emulador) pero **ni el original ni este puerto lo usan** para decidir la transición --
`CQuest::CGQuestStateRecv` sólo mira `QuestIndex` y el estado que el servidor ya tiene guardado del
jugador (`OnQuestStateAsync` lo loguea pero tampoco lo usa), así que los cuatro sitios mandan un `1`
fijo a propósito, no un valor calculado.

Un quinto lugar (`UI/Legacy/UIControls.cpp:5918`, `SendQuestStateRequest(questNumber, questGroup)`) se
dejó sin tocar a propósito: es una función distinta, con una forma de parámetros que no calza con
`Mu099B::SendQuestState` (`questGroup` no tiene equivalente), vive en la carpeta `UI/Legacy/` -- la
UI vieja de antes de NewUI, no necesariamente alcanzable en este build -- y no hay evidencia de que
sea el mismo flujo que `CSQuest`. Migrarlo a ciegas sin confirmar que corresponde a algo de 0.99B real
sería exactamente el error que esta sección de arriba advierte no cometer.

### La tienda: no se podía comprar más de un item

Reportado en pruebas reales: la primera compra en una tienda de NPC funcionaba
bien, pero ningún click siguiente hacía nada -- ni mandaba el paquete, ni
mostraba error. Es exactamente el mismo bug de la bandera sin resetear que ya
se había encontrado en `SendGetItem`/`ReceiveGetItem099B` (ver la sección de
arriba): `NewUINPCShop.cpp` guarda cada click de compra detrás de
`if (BuyCost == 0) { Mu099B::SendItemBuy(...); BuyCost = ItemValue(...); }`
para no mandar dos pedidos del mismo item mientras se espera la respuesta.

`ReceiveBuy099B` (el receptor de `PMSG_ITEM_BUY_SEND` enchufado para 0.99B)
nunca devolvía `BuyCost` a `0` en ninguna de sus tres salidas -- ni cuando la
compra fallaba con mensaje (`BuyFailed`), ni cuando fallaba en silencio
(`BuyFailedSilent`), ni en el camino de éxito. Los dos `BuyCost = 0;` que ya
existían en el archivo no contaban: uno está dentro de un `ReceiveBuy` viejo
comentado (código muerto), y el otro es de `ReceiveBuyExtended`, un dialecto
distinto que 0.99B no usa. Se agregó el reset a las tres salidas de
`ReceiveBuy099B`, mismo patrón que el fix de `SendGetItem`. Verificado con
`ninja` + `ctest -C Debug` (152/152).

### Los monstruos pegaban siempre, sin importar la defensa del jugador

Reportado en pruebas reales: los monstruos hacían daño en cada golpe sin
importar cuánta defensa tuviera el jugador, y el jugador nunca esquivaba. La
fórmula de `ViewportTicker.cs` (el tick que hace atacar a los monstruos) era
inventada, no portada: no tenía chequeo de fallo/esquiva, restaba la defensa
del jugador completa (sin la mitad que le corresponde a un objetivo
`OBJECT_USER`), y el piso de daño mínimo usaba `Nivel_monstruo / 3` en vez de
la fórmula real.

El original (`Attack.cpp` del emulador) no tiene una fórmula separada para
monstruos: `gObjMonsterAttack` (`Monster.cpp:882`) arma un paquete sintético
`PMSG_ATTACK_RECV`/`PMSG_SKILL_ATTACK_RECV` y lo mete por el mismo camino
(`CAttack::Attack`) que usa un jugador atacando -- es una única función
simétrica para cualquier combinación de atacante/objetivo. Ese camino ya
estaba bien portado del lado jugador-ataca-monstruo (`OnAttackAsync` en
`ClientProtocolHandler.cs`), así que se usó como plantilla:

- **Fallo/esquiva** (`CAttack::MissCheck`, `Attack.cpp:987-1031`): se agregó el
  mismo chequeo `AttackSuccessRate` (monstruo) vs. `DefenseSuccessRate`
  (jugador), incluyendo el golpe de gracia al 5% cuando el ataque iba a
  fallar.
- **Defensa del objetivo** (`CAttack::GetTargetDefense`, `Attack.cpp:1117-
  1154`): cuando el objetivo es `OBJECT_USER` la defensa se reduce a la
  mitad -- esto aplica siempre que el objetivo es un jugador, no sólo para
  hechizos como hacía el código viejo.
- **Piso de daño mínimo** (`Attack.cpp:318-322`): `Nivel_ATACANTE / 10`
  (mínimo 1), no `Nivel_monstruo / 3` (mínimo 5); cuando el daño cae debajo
  del piso se usa `piso + rand(piso)`, no un clamp directo.

Lo que faltaba de esto -- el ataque tipo hechizo con su propia fórmula de daño
mágico, en vez de reusar la física con la defensa a medias -- se portó
después, ver la sección de abajo. Verificado con `dotnet build` (0 errores).

### Los monstruos no tenían efecto de muerte: desaparecían de golpe

Reportado en pruebas reales: al matar un monstruo, este desaparecía
instantáneamente en vez de mostrar ninguna animación de muerte. La causa
estaba en `OnMonsterDeathAsync` (`ClientProtocolHandler.cs`): además de
mandar el paquete de muerte (`PMSG_USER_DIE_SEND`, 0x17), en el mismo
instante también sacaba al monstruo del viewport de todos los que lo veían
(`ViewportDestroy`, 0x14) y le vaciaba `VisibleTo`. El cliente recibía "morí"
y "desaparecé" en el mismo paquete de tick, así que nunca llegaba a
renderizar el cadáver.

El original (`CObjectManager::CharacterLifeCheck`, `ObjectManager.cpp:2893-
2903`) NO hace eso: al morir sólo pone `Live = 0`, `State = OBJECT_DYING`,
guarda `RegenTime` y manda `GCUserDieSend` -- el objeto se queda en el
viewport de todo el que lo tenía visible, jugando su animación de muerte
normalmente. Recién saca al cadáver de la vista cuando se cumple el timer de
respawn: `gObjMonsterRegen` (`Monster.cpp:369`) llama primero
`gObjClearViewport(lpObj)` (ahí es cuando de verdad desaparece) y después
revive al monstruo en su punto de spawn.

Se portó ese mismo orden en tres lugares:

- `OnMonsterDeathAsync`: ya no saca al monstruo del viewport ni le vacía
  `VisibleTo` -- sólo manda el paquete de muerte.
- `TickMonstersForObserverAsync` (`ViewportTicker.cs`): antes trataba a
  cualquier monstruo `IsDead` como "no visible", así que el barrido normal de
  diffing de viewport lo mandaba a destruir en el siguiente tick de todos
  modos (con ~1 tick de retraso, pero el mismo síntoma). Ahora un cadáver en
  rango se sigue contando como visible -- sólo se deja de anunciar como
  "recién aparecido" (ya se avisó su muerte con el 0x17).
- `RespawnDeadMonsters`: ahora, justo antes de revivir al monstruo (mismo
  punto que `gObjClearViewport` dentro de `gObjMonsterRegen`), manda
  `ViewportDestroy` a todos los que seguían viendo el cadáver y recién ahí
  llama a `_monsters.Respawn`. El paquete de reaparición (0x13) lo manda
  naturalmente el barrido normal, porque el monstruo ya no está en
  `VisibleMonsters` de nadie.

Verificado con `dotnet build` (0 errores).

### Los hechizos de los monstruos usaban la fórmula física, no la mágica

Reportado en pruebas reales, junto con el resto de combate: los ataques a
distancia/hechizo de los monstruos (`isSpell == true` en `ViewportTicker.cs`)
usaban exactamente la misma fórmula que un golpe cuerpo a cuerpo --
`PhysiDamageMin/Max` del monstruo -- sólo cambiando la defensa a la mitad
antes del fix de arriba, y ni siquiera eso después (porque la mitad ya se
aplica siempre). No había ninguna diferencia real entre "me pegó" y "me tiró
un hechizo" más que la animación.

El original tiene dos funciones de daño separadas y `gObjMonsterAttack`
(`Monster.cpp:882+`) elige una u otra según el tipo de ataque exactamente
igual que decide `GetMonsterAttackSkill` acá: `CAttack::GetAttackDamage`
(físico) para melee, `CAttack::GetAttackDamageWizard` (`Attack.cpp:1309-
1384`) para hechizo. La diferencia clave para un monstruo atacante: la rama
mágica arranca de `lpObj->MagicDamageMin/MagicDamageMax` -- pero
`gObjSetMonster` (`Monster.cpp:206-365`), que inicializa un monstruo al
spawnear, **nunca toca esos dos campos**, así que para cualquier monstruo
quedan en cero. Todo el daño de un hechizo de monstruo sale entonces de una
sola fuente: `lpSkill->m_DamageMin`/`m_DamageMax`, la fila del propio skill en
Skill.txt (más `gSkillDamage.GetDamage` al final, un multiplicador opcional
que hoy es no-op porque el `SkillDamage.txt` real no trae filas de datos).
Ese dato YA estaba portado (`SkillInfo.cs`/`SkillInfoTable`, usado por el
ataque mágico del jugador en `ClientProtocolHandler.cs`) pero
`ViewportTicker` no tenía acceso a la tabla.

Se conectó `SkillInfoTable`/`SkillDamageTable` a `ViewportTicker` (mismas
instancias que ya carga `Program.cs` para el resto del server) y, cuando
`isSpell` es cierto y el `skillId` elegido tiene una fila real en
`SkillList.txt`, el daño crudo sale de `skill.DamageMin + rand(DamageMax -
DamageMin)` en vez de `PhysiDamageMin/Max` del monstruo -- con el mismo
`gSkillDamage.Apply` opcional al final, después del piso de daño. Si el
`skillId` no tiene fila (algunos ids de `GetMonsterAttackSkill` son
heurísticas sin skill real detrás, ver su propio comentario), cae de vuelta a
la fórmula física como antes, para no dejar el ataque sin daño. Verificado
con `dotnet build` (0 errores).

### Aprender una habilidad con orbe no la hacía aparecer en el selector

Reportado en pruebas reales: usar un orbe (ej. el de Twisting Slash para Dark
Knight) parecía andar -- el item se consumía, no había error -- pero la
habilidad nunca aparecía en la ventana donde el jugador elige qué habilidad
usar. Ni las habilidades recién aprendidas ni, en algunos casos, las que ya
traía el personaje al entrar.

Mismo patrón de bug que ya apareció dos veces antes en esta sesión (`SendGetItem`,
`BuyCost`): el receptor 099B hace bien la mitad del trabajo y se olvida del
resto. Acá el array `CharacterAttribute->Skill[]` SÍ quedaba bien escrito --
`ReceiveMagicList099B` (el receptor de `PMSG_SKILL_LIST_SEND`, C1:F3:11, que
realmente está enchufado para 0.99B) escribe cada slot correctamente, tanto en
la lista completa de login como en las altas/bajas individuales (`count`
0xFE/0xFF). El problema es que la ventana de selección
(`NewUIMainFrameWindow.cpp:1389,1914,1965`) no recorre ese array buscando
slots no vacíos -- lee directo `CharacterAttribute->SkillNumber`, un contador
aparte. Y ese contador **nunca se recalculaba** en el receptor 099B.

El dialecto viejo (`ReceiveMagicList`, mismo archivo, todavía activo para el
protocolo posterior) sí lo hace: después de tocar `Skill[]`, recorre el array
completo y reconstruye `SkillNumber` (cuántas habilidades hay) y
`SkillMasterNumber` (cuántas son de tipo Master) desde cero, y además
sanea `Hero->CurrentSkill` (la habilidad seleccionada por defecto) para que no
quede apuntando a un slot vacío. Se portó ese mismo recálculo al final de
`ReceiveMagicList099B`, junto con el aviso de caché
(`gSkillManager.InvalidateSkillAttributeRequirementsCache()`) que depende del
mismo cambio. **Lo que NO se portó**, a propósito: el bloque de ahí abajo que
reemplaza un skill base por su versión "Master" (`AT_SKILL_POWER_SLASH_STR`
pisando a `AT_SKILL_POWER_SLASH`, etc.) -- es el sistema de árbol de
habilidades Master, que no existe en 0.99B real (ver `[[ground-truth-cliente-099b]]`);
portarlo a ciegas habría sido el mismo error que esta sesión viene evitando
en Party/Quest/Guild.

Verificado con `ninja` + `ctest -C Debug` (152/152).

### Subir de nivel un item con joya (Bless/Soul/Life/Guardian) lo hacía desaparecer

Reportado en pruebas reales: usar un Jewel of Bless sobre un item lo hacía
desaparecer del inventario en vez de subirle el nivel. El mismo bug aplicaba
a Soul, Life y cualquier otra joya, porque todas comparten el mismo paquete
de respuesta.

No es el bug de "capa Dotnet nunca migrada" de costumbre en el sentido
cliente->servidor -- acá el pedido (`PMSG_ITEM_USE_RECV`, C1:26) ya viaja
bien. Es la RESPUESTA la que estaba mal enchufada: el servidor manda
`PMSG_ITEM_MODIFY_SEND` (C1:F3:14) con el formato fijo de cinco bytes de
0.99B (`Item.WireByteSize`, ver `Mu099B::PMSG_ITEM_MODIFY_SEND` generado por
protogen), pero el despachador de `0xF3:0x14` (`WSclient.cpp`) apuntaba a
`ReceiveModifyItemExtended` -- el receptor del dialecto de temporada
posterior, que interpreta el item con `CalcItemLength` (formato de largo
variable, doce-y-pico bytes). Con el offset mal leído, `InsertItem` fallaba
o insertaba basura, y el item que se estaba mejorando quedaba efectivamente
borrado: se ve exactamente como "usé la joya y el item desapareció".

Se agregó `ReceiveModifyItem099B`, mismo patrón que
`ReceiveGetItem099B`/`ReceiveBuy099B` (`Mu099B::DecodeItemInfo` +
`Mu099B::WriteClientItemBlock` sobre los cinco bytes reales) combinado con el
borrado-antes-de-insertar que ya tenía `ReceiveModifyItemExtended` (acá el
slot destino ya tiene un item, a diferencia de recoger del piso o comprar en
tienda, donde el slot está vacío). El dialecto viejo se dejó sin tocar --
sigue en el archivo, sin llamador, como el resto de los handlers de
temporada posterior de esta sesión. Verificado con `ninja` + `ctest -C Debug`
(152/152).

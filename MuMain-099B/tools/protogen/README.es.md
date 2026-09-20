🌐 [English](README.md) · **Español**

# protogen — el formato de wire 0.99B como fuente única

El protocolo 0.99B se implementa en dos lugares —el servidor C# de SharpSSeMU y
el cliente C++— y hasta ahora se transcribía a mano en cada uno. Esa
transcripción es la que produjo los bugs de layout documentados en
`SharpSSeMU/docs/development-log.es.md`: `CharSet` 13-vs-18, `ItemInfo` 5-vs-12, `PartyLife` de
1 byte por miembro, `Teleport.gate` BYTE-vs-WORD, el orden de campos de
`PMSG_VIEWPORT_PLAYER`, `MAX_DS_LEVEL` 4-vs-7.

Estas herramientas derivan el formato de las fuentes del servidor original y lo
dejan en un IR que cualquiera de los dos lados consume.

## El árbol correcto

El paquete trae **dos** copias del emulador. Solo una aplica:

| Ruta | Qué es |
|---|---|
| `Source/Source/Emulator 0.99 (2.1.7)/` | **el bueno.** `stdafx.h:7` dice `GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]"` |
| `Source/Source/Emulator/` | una temporada muy posterior (Kanturu, Raklion, MasterSkillTree, sockets). Sin relación con este proyecto |

`parse_protocol.py` verifica el marcador `0.99B CHS` y **aborta** si no lo
encuentra, en vez de adivinar. La pista delatora del árbol equivocado es la
macro `GAMESERVER_UPDATE`: no existe en el correcto.

## Flujo

```bash
GS="../../../Source/Source/Emulator 0.99 (2.1.7)/GameServer"
CS="../../../Source/Source/Emulator 0.99 (2.1.7)/ConnectServer"

# 1. layout de los structs, desde los headers
python parse_protocol.py --source-dir "$GS" --source-dir "$CS" --out protocol_099b.json

# 2. opcode, dirección y transporte, desde los .cpp
python annotate_opcodes.py --ir protocol_099b.json --source-dir "$GS" --source-dir "$CS"

# 3. header C++ para el cliente
python emit_cpp.py --ir protocol_099b.json --out ../../src/source/Protocol099B/Protocol099B.generated.h
```

Estado actual: **259 structs de wire** (254 `PMSG_*`), **211 con opcode**, **46
con relleno de alineación en el wire**.

Hacen falta los dos proyectos: el GameServer define el grueso, pero la familia
`0xF4` (lista de servidores, resolución de IP:puerto) y la lista de nombres
propia de SSeMU viven en el ConnectServer.

## Por qué el padding importa

Los headers usan `#pragma pack(1)` **solo en regiones puntuales** — a veces
abiertas desde adentro de las llaves. Fuera de ellas rige el packing por defecto
de MSVC, que inserta relleno; y el servidor original manda los structs con
`memcpy(..., sizeof(info))` o `header.set(head, sizeof(pMsg))`, así que **ese
relleno viaja en el wire**.

`PMSG_LIFE_SEND` es el caso de libro:

```
offset 0  PBMSG_HEAD header   (3 bytes)
offset 3  BYTE type
offset 4  BYTE life[2]
offset 6  BYTE flag
offset 7  <-- 1 byte de relleno: el DWORD siguiente se alinea a 4
offset 8  DWORD ViewHP        (GAMESERVER_EXTRA==1)
```

Omitir el byte del offset 7 hace que el cliente lea `ViewHP` corrido: el patrón
de "daño/maná basura" que persiguió el port durante varias pasadas.

## Verificación: el compilador es el árbitro

Ni el IR ni el header generado se creen a sí mismos. `emit_verifier.py` produce
un `.cpp` que redeclara cada struct y agrega `static_assert` de `sizeof` y de
`offsetof` por miembro; el header de `emit_cpp.py` los lleva incorporados. Si el
layout derivado se desviara del que arma MSVC, no compila.

```bash
python emit_verifier.py --ir protocol_099b.json --out verify_layout.cpp
# desde un shell con vcvars32 (x86 -- emulador y cliente son de 32 bits):
cl /nologo /c /std:c++20 /EHsc verify_layout.cpp
```

Este paso encontró un bug real en el propio emisor la primera vez que corrió
(perdía el `#pragma pack(1)` declarado dentro de las llaves, en 7 structs). Ese
es exactamente su trabajo.

## Auditoría del servidor

`audit_muservercs.py` cruza el IR contra los builders escritos a mano de
SharpSSeMU y marca los que no emiten el `sizeof` real.

```bash
python audit_muservercs.py --ir protocol_099b.json \
  --muservercs ../../../SharpSSeMU/src/MuServer.GameServer/Protocol
```

Es una herramienta de **triage, no un oráculo**: cuenta bytes, no compara offset
por offset. Separa en tres cubetas —mismatch confirmado, "verificar a mano"
(atribución ambigua o ancho no resoluble estáticamente), y consistente— para no
presentar una heurística como si fuera prueba.

## Qué queda fuera

43 structs `PMSG_` no tienen opcode propio, y está bien: son los **renglones**
que van repetidos dentro de un sobre (`PMSG_CHARACTER_LIST` dentro de
`PMSG_CHARACTER_LIST_SEND`, `PMSG_SERVER_LIST` dentro de `PMSG_SERVER_LIST_SEND`,
`PMSG_ITEM_LIST`, `PMSG_FRIEND_LIST`...). Su layout sí está, que es lo que
importa para leerlos con el stride correcto.

## Auditoría del cliente

Dos herramientas más, que miran el otro lado del cable: qué de todo esto sabe
leer MuMain.

### `audit_client_coverage.py`

Enumera los opcodes que SharpSSeMU puede mandar (sacándolos de sus builders) y
los cruza contra el `switch` del cliente. Sirve para saber si falta un `case`,
que es el fallo más silencioso de todos: el paquete llega, no lo atiende nadie,
y la funcionalidad simplemente no existe.

### `audit_client_structs.py`

Compara el **tamaño** del struct al que castea cada receptor contra el del wire.
Es la herramienta que más bugs encontró, porque distingue dos fallas que se ven
igual desde afuera:

* **El struct del cliente es más grande**: el `safe_cast` rechaza el paquete y la
  función nunca corre. Con cast crudo es peor: lee fuera del buffer.
* **El struct del cliente es más chico**: se leen los campos corridos. No falla
  nada, sólo salen otros números.

Modela el layout con las mismas reglas que `parse_protocol.py` usa del lado del
servidor, así que los dos lados se miden con la misma vara.

No ata receptor con opcode —el `switch` anidado del cliente no se deja parsear
de forma confiable—, así que el cruce final es a mano. Igual reduce el problema
de noventa y pico de opcodes a una lista corta.

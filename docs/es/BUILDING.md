# Compilar y probar

🌐 [English](../BUILDING.md) · **Español**

- [Servidor](#servidor) — C# / .NET 10
- [Cliente](#cliente) — C++ / CMake, Windows
- [Tests](#tests)
- [Regenerar la librería de protocolo](#regenerar-la-librería-de-protocolo)

## Servidor

```bash
dotnet build SharpSSeMU/SharpSSeMU.sln            # Debug, todos los proyectos
dotnet build SharpSSeMU/SharpSSeMU.sln -c Release
```

Proyectos (`SharpSSeMU/src/`):

| Proyecto | Salida | Notas |
|---|---|---|
| `MuServer.Shared` | librería | framing de paquetes, cifrados, tokenizador de scripts, lector INI, logger |
| `MuServer.ConnectServer` / `JoinServer` / `DataServer` / `GameServer` | apps de consola | los cuatro servidores |
| `MuServer.AdminPanel` | app ASP.NET Core Blazor Server | referencia a GameServer + DataServer |
| `TestClient`, `WorldTestClient` | apps de consola | clientes simulados que usan los tests e2e |

Las dependencias NuGet se restauran solas: Npgsql 8.0.3 y MudBlazor 9.9.0.

**Publicar un build standalone para Linux:**

```bash
for p in ConnectServer JoinServer DataServer GameServer; do
  dotnet publish SharpSSeMU/src/MuServer.$p -c Release -r linux-x64 --self-contained
done
```

Copiá los `.ini` de cada servidor (y, para el GameServer, las carpetas `Data/` y `Hack/`) junto a los
binarios publicados — [GETTING_STARTED](GETTING_STARTED.md#8-referencia-de-configuración) indica qué archivo va dónde.

> **Ojo en Windows:** no se puede recompilar un servidor mientras corre (el `.exe`/`.dll` queda
> bloqueado y MSBuild falla con *MSB3027*). Detenelo primero.

## Cliente

El cliente es un proyecto CMake (`MuMain-099B`), fork de
[sven-n/MuMain](https://github.com/sven-n/MuMain); las guías de compilación upstream en
[`MuMain-099B/docs/build/`](../../MuMain-099B/docs/build/README.md) (Visual Studio,
CLion, Rider, consola, WSL/MinGW) aplican sin cambios. Lo que sigue es el camino verificado en
Windows que usa este proyecto.

### Requisitos

| Herramienta | Versión |
|---|---|
| CMake | 3.25+ (probado 4.2.3) |
| Visual Studio 2022 con la carga de trabajo *Desarrollo para el escritorio con C++* | MSVC 14.44 probado |
| .NET SDK | 10.0 (compila la librería de red Native-AOT) |
| Ninja | incluido con Visual Studio 2022 |
| Git | para bajar las fuentes de terceros |

### 1. Bajar las fuentes de terceros

```powershell
scripts\setup-thirdparty.ps1               # SDL + SDL_mixer
scripts\setup-thirdparty.ps1 -WithEditor   # + Dear ImGui, solo para los presets *-mueditor
# Linux/macOS: scripts/setup-thirdparty.sh   (WITH_EDITOR=1 para imgui)
```

**El par de versiones importa:** `SDL release-3.4.x` + `SDL_mixer release-3.2.x`. SDL_mixer usa
`SDL_ALIGNED(16)`, que solo existe desde SDL 3.4; con SDL 3.2.x falla la compilación de
`SDL_mixer_spatialization.c`. El script fija el par correcto.

### 2. Aportar la carpeta `Data/` del cliente

Copiá una carpeta `Data/` de cliente MU en `MuMain-099B/src/bin/Data/` (está en
`.gitignore`). El build la copia, junto con `fonts/` y `config.ini`, al lado del ejecutable.

### 3. Configurar y compilar

Desde un **"x86 Native Tools Command Prompt for VS 2022"** (o tras correr el `vcvars32.bat` de VS 2022),
en `MuMain-099B`:

```powershell
cmake --preset windows-x86 -DBUILD_TESTING=ON
cmake --build --preset windows-x86-debug        # o windows-x86-release
```

Presets: `windows-x86`, `windows-x64` y las variantes `*-mueditor` que agregan el editor ImGui en el
juego (F12). El cliente es una aplicación de 32 bits; `windows-x86` es la configuración probada.

> **Versiones de Ninja / Visual Studio:** si tenés más de un Visual Studio instalado, `vswhere -latest`
> puede elegir una edición que no trae Ninja (VS 2026 no lo traía al momento de escribir esto).
> Configurá desde el prompt de VS 2022 y, si hace falta, pasá
> `-DCMAKE_MAKE_PROGRAM=<ruta al ninja.exe de VS2022>`.

### 4. Ejecutar

```powershell
out\build\windows-x86\src\Debug\Main.exe
```

Servidor destino: `config.ini` junto al ejecutable (`ServerIP`, `ServerPort=44405`) o
`Main.exe connect /u127.0.0.1 /p44405`.

## Tests

### Tests unitarios del cliente (C++, 152 tests)

Ejercitan la capa de protocolo 0.99B: cifrados, framing, codificación de items/charset y los bytes
exactos de cada constructor de paquetes — sin necesidad de servidor.

```powershell
cd MuMain-099B\out\build\windows-x86
ctest -C Debug --output-on-failure
```

Requiere el cliente configurado con `-DBUILD_TESTING=ON` (ver arriba). `tools/probe099b` es una
sonda manual para usar contra un servidor vivo; se compila con los tests pero no es parte de CTest.

### Tests end-to-end del servidor (Python)

Cada script levanta un **cluster PostgreSQL desechable** (`initdb` en un directorio temporal,
loopback, autenticación `trust` — tu base real nunca se toca), compila los proyectos que necesita,
lanza los servidores en puertos libres y los maneja con un cliente simulado que habla el protocolo
binario cifrado real.

```bash
python SharpSSeMU/tests/full_chain_e2e_test.py          # ConnectServer → Join → Data → Game
python SharpSSeMU/tests/gameserver_fase1_e2e_test.py    # login
python SharpSSeMU/tests/gameserver_fase4_e2e_test.py    # monstruos y combate
```

Requisitos: binarios de PostgreSQL, .NET 10 SDK y las carpetas del paquete original
(`MuClient/Data/Enc1.dat`, `MuServer99B/Data`) — ver
[GETTING_STARTED](GETTING_STARTED.md#2-traé-tus-propios-archivos-del-juego). Código de salida 0 = pasa.

**Estado conocido:** `full_chain` y `fase1` pasan completos. Las otras fases llegan lejos (16–36
aserciones en verde, incluidos todos los pasos de protocolo) y se detienen en un punto — la
secuencia de combate del `WorldTestClient` — por una razón de balance del fixture (el monstruo de
prueba mata al personaje inicial de 60 HP), no de protocolo. Detalles en `SharpSSeMU/tests/README.md`.

## Regenerar la librería de protocolo

El `Protocol099B.generated.h` del cliente se genera, nunca se edita. Con las fuentes del emulador
SSeMU 0.99B en `Source/`:

```bash
cd MuMain-099B/tools/protogen
GS="../../../Source/Source/Emulator 0.99 (2.1.7)/GameServer"
CS="../../../Source/Source/Emulator 0.99 (2.1.7)/ConnectServer"
python parse_protocol.py    --source-dir "$GS" --source-dir "$CS" --out protocol_099b.json
python annotate_opcodes.py  --ir protocol_099b.json --source-dir "$GS" --source-dir "$CS"
python emit_cpp.py          --ir protocol_099b.json --out ../../src/source/Protocol099B/Protocol099B.generated.h
```

El parser se niega a correr contra el árbol de emulador equivocado (busca el marcador `0.99B CHS`).
Los scripts de auditoría (`audit_client_coverage.py`, `audit_client_structs.py`, `audit_muservercs.py`)
cruzan cliente y servidor contra el IR. Ver `tools/protogen/README.md`.

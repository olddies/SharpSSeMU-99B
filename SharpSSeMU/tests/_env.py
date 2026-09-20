"""Entorno compartido para los tests end-to-end.

Antes, cada test traía cableadas las rutas del sandbox Linux donde se escribió
(``/tmp/pgextract``, ``/tmp/dotnet``, ``/sessions/.../mnt/...``), así que solo
corrían ahí. Este módulo descubre todo en tiempo de ejecución y funciona igual
en Windows y en Linux:

* las rutas del repo se resuelven desde ``__file__``, no desde constantes;
* PostgreSQL y .NET se buscan en las ubicaciones habituales de cada plataforma;
* el cluster de pruebas es **desechable** y vive en un directorio temporal, en
  un puerto propio -- nunca toca una instalación de PostgreSQL existente ni
  necesita sus credenciales (usa autenticación ``trust`` en loopback).

Uso típico:

    import _env
    pg = _env.Postgres()
    pg.start()
    pg.seed_schema()
    ...
    pg.stop()
"""

from __future__ import annotations

import glob
import os
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

WINDOWS = sys.platform == "win32"
EXE = ".exe" if WINDOWS else ""

# The Windows console uses cp1252 by default and the servers' logs carry accents and replacement characters, so
# printing them blows up the test with a UnicodeEncodeError that has nothing to do with what is being tested.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# tests/ lives inside the repo, so the root is its parent.
ROOT = Path(__file__).resolve().parent.parent
# The full package (MuClient/, MuServer99B/, Source/) is the repo's parent.
MU_ROOT = ROOT.parent

TFM = "net10.0"


class EnvironmentError_(RuntimeError):
    """Falta una dependencia del entorno; se aborta en vez de fallar raro."""


def _find_dotnet() -> str:
    found = shutil.which("dotnet")
    if found:
        return found
    candidates = [r"C:\Program Files\dotnet\dotnet.exe"] if WINDOWS else [
        "/usr/bin/dotnet", "/usr/share/dotnet/dotnet", "/tmp/dotnet/dotnet"]
    for c in candidates:
        if os.path.exists(c):
            return c
    raise EnvironmentError_("no se encontró el SDK de .NET (dotnet no está en PATH)")


def _find_pg_bin() -> Path:
    """Directorio bin de PostgreSQL. Se prefiere la versión más nueva instalada."""
    if (psql := shutil.which("psql")):
        return Path(psql).parent

    patterns = ([r"C:\Program Files\PostgreSQL\*\bin"] if WINDOWS else
                ["/usr/lib/postgresql/*/bin", "/usr/pgsql-*/bin", "/tmp/pgextract/root/usr/lib/postgresql/*/bin"])
    matches: list[Path] = []
    for pattern in patterns:
        matches.extend(Path(p) for p in glob.glob(pattern))
    if not matches:
        raise EnvironmentError_(
            "no se encontró PostgreSQL. En Windows, instalarlo desde "
            "https://www.postgresql.org/download/windows/ o con "
            "'winget install PostgreSQL.PostgreSQL.16'.")

    def version_key(p: Path) -> tuple:
        try:
            return (int(p.parent.name.split("-")[-1]),)
        except ValueError:
            return (0,)

    return sorted(matches, key=version_key)[-1]


DOTNET = _find_dotnet()
PG_BIN = _find_pg_bin()


def free_port(preferred: int) -> int:
    """`preferred` si está libre; si no, un puerto efímero cualquiera."""
    with socket.socket() as s:
        try:
            s.bind(("127.0.0.1", preferred))
            return preferred
        except OSError:
            pass
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def wait_for_port(port: int, timeout: float = 30.0, host: str = "127.0.0.1") -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with socket.create_connection((host, port), timeout=0.5):
                return True
        except OSError:
            time.sleep(0.15)
    return False


class Postgres:
    """Cluster PostgreSQL desechable, aislado del que pueda haber instalado.

    Se lanza ``postgres`` directamente en vez de ``pg_ctl``: bajo Git Bash en
    Windows, ``pg_ctl -w start`` no retorna porque el servidor hereda el stdout
    del shell. Lanzarlo con Popen y esperar a que el puerto acepte conexiones
    evita el problema y se comporta igual en las dos plataformas.
    """

    def __init__(self, port: int = 5433, datadir: Path | None = None):
        self.port = free_port(port)
        base = Path(tempfile.gettempdir()) / "muservercs-e2e"
        base.mkdir(parents=True, exist_ok=True)
        self.datadir = datadir or (base / f"pgdata-{self.port}")
        self.logfile = base / f"pg-{self.port}.log"
        self.proc: subprocess.Popen | None = None
        self._log = None

    # -- ciclo de vida -----------------------------------------------------

    def init(self) -> None:
        if (self.datadir / "PG_VERSION").exists():
            return
        shutil.rmtree(self.datadir, ignore_errors=True)
        self.datadir.parent.mkdir(parents=True, exist_ok=True)
        r = subprocess.run(
            [str(PG_BIN / f"initdb{EXE}"), "-D", str(self.datadir),
             "-U", "postgres", "-A", "trust", "-E", "UTF8", "--no-locale"],
            capture_output=True, text=True)
        if r.returncode != 0:
            raise EnvironmentError_(f"initdb falló:\n{r.stdout}\n{r.stderr}")

    def start(self) -> None:
        self.init()
        # Un postmaster.pid viejo de una corrida abortada impide el arranque.
        pid_file = self.datadir / "postmaster.pid"
        if pid_file.exists() and not wait_for_port(self.port, timeout=0.5):
            pid_file.unlink(missing_ok=True)

        self._log = open(self.logfile, "w", encoding="utf-8", errors="replace")
        self.proc = subprocess.Popen(
            [str(PG_BIN / f"postgres{EXE}"), "-D", str(self.datadir),
             "-p", str(self.port), "-c", "listen_addresses=127.0.0.1"],
            stdout=self._log, stderr=subprocess.STDOUT)
        if not self._wait_ready(timeout=60):
            raise EnvironmentError_(
                f"PostgreSQL no quedó listo en el puerto {self.port}.\n"
                f"{self.logfile.read_text(encoding='utf-8', errors='replace')[-2000:]}")

    def _wait_ready(self, timeout: float) -> bool:
        """Espera a que el servidor ACEPTE CONSULTAS, no solo a que abra el puerto.

        El socket empieza a aceptar conexiones mientras el sistema todavía está
        arrancando, y en esa ventana toda consulta falla con "the database system
        is starting up". pg_isready distingue los dos estados.
        """
        deadline = time.monotonic() + timeout
        is_ready = PG_BIN / f"pg_isready{EXE}"
        while time.monotonic() < deadline:
            if self.proc is not None and self.proc.poll() is not None:
                return False  # the process died; there is no point in waiting any longer
            r = subprocess.run(
                [str(is_ready), "-h", "127.0.0.1", "-p", str(self.port), "-U", "postgres"],
                capture_output=True, text=True)
            if r.returncode == 0:
                return True
            time.sleep(0.25)
        return False

    def stop(self) -> None:
        if self.proc is not None:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.proc.kill()
            self.proc = None
        if self._log is not None:
            self._log.close()
            self._log = None

    # -- consultas ---------------------------------------------------------

    def psql(self, *args: str, db: str = "postgres",
             check: bool = False) -> subprocess.CompletedProcess:
        r = subprocess.run(
            [str(PG_BIN / f"psql{EXE}"), "-h", "127.0.0.1", "-p", str(self.port),
             "-U", "postgres", "-d", db, *args],
            capture_output=True, text=True)
        if check and r.returncode != 0:
            raise EnvironmentError_(f"psql falló: {args}\n{r.stdout}\n{r.stderr}")
        return r

    def sql(self, statement: str, db: str = "muonline",
            check: bool = True) -> subprocess.CompletedProcess:
        return self.psql("-v", "ON_ERROR_STOP=1", "-c", statement, db=db, check=check)

    @property
    def connection_string(self) -> str:
        return (f"Host=127.0.0.1;Port={self.port};Database=muonline;"
                f"Username=muserver;Password=muserver")

    def seed_schema(self) -> None:
        """Recrea `muonline` desde cero y aplica los scripts de db/postgres."""
        self.psql("-c", "DROP DATABASE IF EXISTS muonline;")
        self.psql("-c", "CREATE DATABASE muonline;", check=True)
        self.psql("-c",
                  "DO $$ BEGIN IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname='muserver') "
                  "THEN CREATE ROLE muserver LOGIN PASSWORD 'muserver' SUPERUSER; END IF; END $$;",
                  check=True)
        for name in ("001_accounts.sql", "002_characters.sql",
                     "003_default_class_seed.sql", "004_friends.sql"):
            script = ROOT / "db" / "postgres" / name
            r = self.psql("-v", "ON_ERROR_STOP=1", "-f", str(script), db="muonline")
            if r.returncode != 0:
                raise EnvironmentError_(f"{name} falló:\n{r.stdout}\n{r.stderr}")


class ServerSet:
    """Lanza los servidores compilados en directorios de runtime aislados."""

    def __init__(self, tag: str):
        self.base = Path(tempfile.gettempdir()) / "muservercs-e2e" / f"rt-{tag}"
        self.procs: dict[str, subprocess.Popen] = {}

    def runtime_dir(self, name: str) -> Path:
        d = self.base / name
        shutil.rmtree(d, ignore_errors=True)
        d.mkdir(parents=True, exist_ok=True)
        return d

    @staticmethod
    def deploy(project: str, dest: Path) -> None:
        """Copia la salida de compilación de un proyecto al directorio destino."""
        src = ROOT / "src" / project / "bin" / "Debug" / TFM
        if not src.is_dir():
            raise EnvironmentError_(
                f"falta la compilación de {project} en {src}. "
                f"Correr: dotnet build SharpSSeMU.sln")
        for item in src.iterdir():
            target = dest / item.name
            if item.is_dir():
                shutil.copytree(item, target, dirs_exist_ok=True)
            else:
                shutil.copy(item, target)

    def start(self, name: str, project: str, cwd: Path) -> subprocess.Popen:
        dll = cwd / f"{project}.dll"
        # stdin MUST be a pipe that stays open: the three servers run a `Console.ReadLine()` loop for their
        # console commands and finish when it returns null. Inheriting an already closed stdin (the normal case
        # when running without a terminal) makes them exit as soon as they start, right after logging that they
        # are ready -- which looks like a network problem and is not.
        p = subprocess.Popen(
            [DOTNET, str(dll)], cwd=str(cwd),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            encoding="utf-8", errors="replace")
        self.procs[name] = p
        return p

    def report_deaths(self) -> list[str]:
        return [n for n, p in self.procs.items() if p.poll() is not None]

    def shutdown(self, print_logs: bool = True) -> None:
        for p in self.procs.values():
            try:
                p.terminate()
            except Exception:
                pass
        time.sleep(0.5)
        for name, p in self.procs.items():
            try:
                out, _ = p.communicate(timeout=5)
                if print_logs:
                    print(f"--- {name} ---")
                    print((out or "")[-4000:])
            except Exception:
                try:
                    p.kill()
                except Exception:
                    pass


# -- helpers de protocolo compartidos por los tests -------------------------

def build_c1(head: int, payload: bytes = b"") -> bytes:
    return bytes([0xC1, 3 + len(payload), head]) + payload


def fixed(s: str, n: int) -> bytes:
    b = s.encode("ascii")[:n]
    return b + b"\x00" * (n - len(b))


def u16(v: int) -> bytes:
    return struct.pack("<H", v)


INVENTORY_SLOT_BYTES = 16
INVENTORY_SLOTS = 108  # INVENTORY_SIZE; la columna `inventory` mide 108*16 = 1728 bytes


def item_bytes(index: int, level: int = 0, durability: int = 0) -> bytes:
    """Un slot de inventario de 16 bytes: puerto mínimo de Item.ToDbBytes.

    Solo los campos que necesita un item simple sin opciones/serial/set. El
    formato real usa 32 franjas (GET_ITEM(section,sub) = section*32+sub,
    MAX_ITEM=512): el índice completo (0-511) entra en byte0 más su bit alto en
    byte7. byte9 es siempre 0 y los bytes 10-15 no se escriben nunca -- en este
    build no existen sockets ni JewelOfHarmony.
    """
    b = bytearray(INVENTORY_SLOT_BYTES)
    b[0] = index & 0xFF
    b[1] = (level * 8) & 0xFF
    b[2] = durability
    b[7] = (index & 256) >> 1
    return bytes(b)



def inventory_hex(items: dict[int, bytes]) -> str:
    """Inventario completo en hex: todo vacío (0xFF) salvo los slots indicados.

    Se arma acá en vez de arrastrar un literal de 3456 caracteres en el test:
    con el literal no se ve qué slot queda ocupado ni con qué, que es
    justamente lo que el test está montando.
    """
    buf = bytearray(b"\xff" * (INVENTORY_SLOTS * INVENTORY_SLOT_BYTES))
    for slot, raw in items.items():
        if not 0 <= slot < INVENTORY_SLOTS:
            raise ValueError(f"slot {slot} fuera de rango")
        if len(raw) > INVENTORY_SLOT_BYTES:
            raise ValueError(f"el slot {slot} no entra en {INVENTORY_SLOT_BYTES} bytes")
        cell = bytearray(INVENTORY_SLOT_BYTES)          # occupied slots are filled with 0x00,
        cell[:len(raw)] = raw                            # no con 0xFF
        buf[slot * INVENTORY_SLOT_BYTES:(slot + 1) * INVENTORY_SLOT_BYTES] = cell
    return buf.hex()


def client_data(*parts: str) -> str:
    """Ruta a un archivo dentro de MuClient/Data del paquete original."""
    return str(MU_ROOT / "MuClient" / "Data" / Path(*parts))


def server_data(*parts: str) -> str:
    """Ruta a un archivo dentro de MuServer99B/Data del paquete original."""
    return str(MU_ROOT / "MuServer99B" / "Data" / Path(*parts))


SERIAL = "SharpSSeMU99B-v1"
VERSION = "1.02.00"


class Deployment:
    """Despliegue estándar de JoinServer + DataServer + GameServer.

    Los diez tests montaban el mismo árbol de archivos con las mismas rutas
    cableadas; acá se arma una sola vez y cada test agrega lo suyo (spawns
    sintéticos, .dat de eventos) sobre `game_dir` antes de llamar a
    `start_all()`.
    """

    def __init__(self, tag: str, pg: "Postgres",
                 game_port: int = 55900, join_port: int = 55970,
                 data_port: int = 55960, connect_port: int = 55557):
        self.pg = pg
        self.servers = ServerSet(tag)
        # Ports reserved dynamically: if an earlier run left a process hanging on the preferred port, with fixed
        # ports the new server cannot listen, wait_for_port sees the OLD one (which no longer has a database
        # behind it) and the test fails much later with a timeout or a ConnectionAborted that says nothing.
        # free_port() runs the whole deployment on free ports and the problem disappears.
        self.game_port = free_port(game_port)
        self.join_port = free_port(join_port)
        self.data_port = free_port(data_port)
        self.connect_port = free_port(connect_port)

        self.join_dir = self.servers.runtime_dir("join")
        self.data_dir = self.servers.runtime_dir("data")
        self.game_dir = self.servers.runtime_dir("game")

        # The build output goes FIRST and the test fixture on top: the GameServer publishes its own Data/
        # (Item.txt, SkillList.txt, the GameServerInfo - *.dat...), so deploying it afterwards would silently
        # overwrite the trimmed files the test has just written.
        self.servers.deploy("MuServer.JoinServer", self.join_dir)
        self.servers.deploy("MuServer.DataServer", self.data_dir)
        self.servers.deploy("MuServer.GameServer", self.game_dir)
        self._write_configs()

    def _write_configs(self) -> None:
        (self.join_dir / "JoinServer.ini").write_text(
            "[JoinServerInfo]\n"
            f"JoinServerPostgres = {self.pg.connection_string}\n"
            f"JoinServerPort = {self.join_port}\n"
            f"ConnectServerAddress = 127.0.0.1\nConnectServerPort = {self.connect_port}\n"
            "CaseSensitive = 0\nMD5Encryption = 0\n", encoding="utf-8")
        (self.join_dir / "AllowableIpList.txt").write_text(
            '0\n"127.0.0.1"\nend\n', encoding="utf-8")

        (self.data_dir / "DataServer.ini").write_text(
            "[DataServerInfo]\n"
            f"DataServerPostgres = {self.pg.connection_string}\n"
            f"DataServerPort = {self.data_port}\n", encoding="utf-8")
        (self.data_dir / "AllowableIpList.txt").write_text(
            '0\n"127.0.0.1"\nend\n', encoding="utf-8")
        (self.data_dir / "BadSyntax.txt").write_text(
            '"fuck"\n"admin"\nend\n', encoding="utf-8")

        (self.game_dir / "GameServer.ini").write_text(
            "[GameServerInfo]\nServerName = SSeMU GameServer_0\nServerCode = 0\n"
            f"ServerPort = {self.game_port}\n"
            f"ServerVersion = {VERSION}\nServerSerial = {SERIAL}\n"
            "ServerEncDecKey1 = 0\nServerEncDecKey2 = 0\nServerMaxUserNumber = 300\n"
            f"JoinServerAddress = 127.0.0.1\nJoinServerPort = {self.join_port}\n"
            f"DataServerAddress = 127.0.0.1\nDataServerPort = {self.data_port}\n"
            f"ConnectServerAddress = 127.0.0.1\nConnectServerPort = {self.connect_port}\n",
            encoding="utf-8")

        # Encryption keys and the Lorencia map: the minimum the GameServer starts with. Tests that need more
        # data copy it themselves.
        (self.game_dir / "Hack").mkdir(exist_ok=True)
        for name in ("Enc2.dat", "Dec1.dat"):
            shutil.copy(server_data("Hack", name), self.game_dir / "Hack" / name)
        (self.game_dir / "Data" / "Terrain").mkdir(parents=True, exist_ok=True)
        shutil.copy(server_data("Terrain", "Terrain1.att"),
                    self.game_dir / "Data" / "Terrain" / "Terrain1.att")

    def copy_server_data(self, *relative: str) -> Path:
        """Copia un archivo de MuServer99B/Data al Data/ del GameServer."""
        dest = self.game_dir / "Data" / Path(*relative)
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy(server_data(*relative), dest)
        return dest

    def start_connect(self, tcp_port: int = 44405) -> None:
        """Arranca el ConnectServer (solo lo usa la cadena completa)."""
        tcp_port = free_port(tcp_port)
        self.connect_tcp_port = tcp_port
        connect_dir = self.servers.runtime_dir("connect")
        # MaxConnectionPerIP has to be there: IpConnectionTracker.CheckIpAddress rejects the FIRST connection
        # from an IP when the limit is 0 (compatibility with the original), so without this key the
        # ConnectServer accepts the socket and cuts it right away -- it shows up as a ConnectionAborted on the
        # client side.
        (connect_dir / "ConnectServer.ini").write_text(
            "[ConnectServerInfo]\n"
            f"ConnectServerPortTCP = {tcp_port}\n"
            f"ConnectServerPortUDP = {self.connect_port}\n"
            "ConnectServerMaxUserNumber = 500\n"
            "MaxConnectionPerIP = 50\n"
            "MaxPacketPerSecond = 0\n"
            "MaxConnectionIdle = 60\n", encoding="utf-8")
        (connect_dir / "BlackList.txt").write_text("end\n", encoding="utf-8")
        (connect_dir / "ServerList.dat").write_text(
            f'   0            "GameServer_0"   "127.0.0.1"        {self.game_port}       1\nend\n',
            encoding="utf-8")
        self.servers.deploy("MuServer.ConnectServer", connect_dir)
        self._start_and_wait("connect", "MuServer.ConnectServer", connect_dir, tcp_port)

    def start_infra(self) -> None:
        """Arranca JoinServer y DataServer y espera a que escuchen."""
        self._start_and_wait("join", "MuServer.JoinServer", self.join_dir, self.join_port)
        self._start_and_wait("data", "MuServer.DataServer", self.data_dir, self.data_port)

    def _start_and_wait(self, name: str, project: str, cwd: Path, port: int,
                        timeout: float = 45.0) -> None:
        """Arranca un servidor y espera su puerto, abortando si el proceso murió.

        Se comprueba el proceso además del puerto: si muere al arrancar (config
        mala, puerto tomado), esperar solo el puerto deja el test colgado hasta
        el timeout y después falla en un lugar que no tiene que ver.
        """
        proc = self.servers.start(name, project, cwd)
        if not wait_for_port(port, timeout=timeout):
            raise EnvironmentError_(
                f"{project} no abrió el puerto {port} "
                f"({'el proceso murió' if proc.poll() is not None else 'sigue vivo'})")
        if proc.poll() is not None:
            raise EnvironmentError_(f"{project} murió justo después de abrir el puerto {port}")

    def start_game(self) -> None:
        self._start_and_wait("game", "MuServer.GameServer", self.game_dir, self.game_port)

    # -- character creation against the DataServer -----------------------

    def seed_characters(self, *specs: tuple[int, str, str], char_class: int = 0) -> None:
        """Crea personajes hablando el protocolo del DataServer.

        Cada spec es (índice, cuenta, nombre). El pedido de lista de personajes
        es obligatorio antes de crear: siembra la fila account_character que
        OnCharacterCreateAsync da por existente.
        """
        ds = socket.create_connection(("127.0.0.1", self.data_port), timeout=5)
        ds.settimeout(5)
        try:
            for index, account, name in specs:
                ds.sendall(build_c1(0x01, u16(index) + fixed(account, 11)))
                time.sleep(0.2)
                print(f"CharacterList({account}) resp: {ds.recv(4096).hex()}")
                ds.sendall(build_c1(0x02, u16(index) + fixed(account, 11)
                                    + fixed(name, 11) + bytes([char_class])))
                time.sleep(0.2)
                print(f"CharacterCreate({name}) resp: {ds.recv(4096).hex()}")
        finally:
            ds.close()

    # Test positions outside Lorencia's safe zone (attr bit 0x01, verified by reading Terrain1.att). They are
    # needed for any step that attacks: CGAttackRecv rejects attacking while standing in a safe zone. (125,125),
    # which the old phases used, IS a safe zone.
    HERO1_POS = (0, 198, 150)
    HERO2_POS = (0, 200, 152)
    MONSTER_POS = (200, 150)

    def seed_test_monster(self) -> None:
        """MonsterList.txt real + un ÚNICO spawn sintético en posición determinista.

        El Program.cs de WorldTestClient es uno solo y compartido: su bloque de
        ataque NO está gateado por argumentos, así que corre en todos los tests, y
        ataca el índice 0 dando por sentado que es el monstruo de prueba.

        Para que eso sea cierto hay que VACIAR el directorio de spawns primero. El
        GameServer publica sus 29 archivos reales en su propio Data/, y entre ellos
        está "000 - Lorencia.txt", que ordena antes que cualquier "000 - Test.txt":
        con ellos presentes el índice 0 es el primer monstruo de Lorencia, a media
        pantalla de distancia, y OnAttackAsync descarta el ataque por rango sin
        mandar nada. El test entonces confundía los golpes DEL monstruo con los
        suyos y su propia muerte con la del monstruo.

        La clase 3 (Spider: Type=0, Level=2, HP=30, Defense=1) se mata en pocos
        golpes con las stats placeholder de RecalcCombatStats, y a 2 tiles de Hero1
        queda dentro del rango de ataque de 3.
        """
        self.copy_server_data("Monster", "MonsterList.txt")
        spawn_dir = self.game_dir / "Data" / "Monster" / "Spawn"
        shutil.rmtree(spawn_dir, ignore_errors=True)
        spawn_dir.mkdir(parents=True, exist_ok=True)
        x, y = self.MONSTER_POS
        (spawn_dir / "000 - Test.txt").write_text(
            f"0\n3    0    {x}    {y}    3\nend\n", encoding="utf-8")

    def place_heroes(self, hero1: str = "Hero1", hero2: str = "Hero2") -> None:
        """Ubica a los dos héroes en las posiciones de prueba estándar."""
        self.place(hero1, *self.HERO1_POS)
        self.place(hero2, *self.HERO2_POS)

    def seed_equippable_weapon(self, name: str, slot: int = 12) -> None:
        """Prepara a `name` para el recorrido de combate compartido del test.

        Pone una espada equipable en la mochila y le sube los stats a algo que
        aguante el recorrido entero. Dos motivos, los dos verificados corriendo
        el test:

        * El item es GET_ITEM(0,0) -- un "Kris", que en el Item.txt real exige
          ReqStrength=40 y ReqDexterity=40. Un personaje recién creado tiene
          18/18, así que sin esto el servidor rechaza el equipado con 0xFF, y
          tiene razón: que CheckItemMoveToInventory valide es justamente lo que
          el test quiere ver pasar, no saltear.
        NO se le tocan la vida ni la vitalidad, aunque haga falta: el monstruo de
        prueba contraataca y con los 60 HP de fábrica Hero1 se muere entre la
        pelea de la fase 4 y el remate en grupo de la fase 5. Sembrar
        life/max_life no sirve (RecalcCombatStats los recalcula desde Vitality al
        entrar al mundo) y subir Vitality desbalancea el resto del recorrido: con
        Vitality=100 el personaje salta a nivel 26 de una sola muerte y rompen
        otros pasos. Es un tema de balance del servidor, no del fixture -- ver
        tests/README.md.
        """
        self.pg.sql("UPDATE character SET inventory = decode('"
                    + inventory_hex({slot: bytes.fromhex("00083c00")})
                    + f"','hex'), strength=50, dexterity=50 WHERE name='{name}';")

    def place(self, name: str, map_number: int, x: int, y: int) -> None:
        self.pg.sql(f"UPDATE character SET map_number={map_number}, "
                    f"map_pos_x={x}, map_pos_y={y} WHERE name='{name}';")

    # -- running the test client -----------------------------------

    def run_world_test_client(self, *extra_args: str,
                              timeout: int = 180) -> subprocess.CompletedProcess:
        dll = ROOT / "src" / "WorldTestClient" / "bin" / "Debug" / TFM / "WorldTestClient.dll"
        r = subprocess.run(
            [DOTNET, str(dll), "127.0.0.1", str(self.game_port), SERIAL, "10200",
             client_data("Enc1.dat"), client_data("Dec2.dat"), *extra_args],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            timeout=timeout)
        print(r.stdout)
        print(r.stderr)
        return r

    def shutdown(self) -> None:
        print("\n=== LOGS ===")
        self.servers.shutdown()


_BUILT: set[str] = set()


def ensure_built(*projects: str) -> None:
    """Compila los proyectos que el test necesita antes de usarlos.

    No alcanza con `dotnet build SharpSSeMU.sln`: TestClient y WorldTestClient no
    están en la solución, así que una compilación normal los saltea y el test
    termina corriendo contra un binario viejo -- un fallo que se manifiesta como
    aserciones que faltan o que pasan cuando no deberían, no como un error de
    compilación. Se compila una vez por proceso.
    """
    for project in projects:
        if project in _BUILT:
            continue
        csproj = ROOT / "src" / project / f"{project}.csproj"
        if not csproj.exists():
            raise EnvironmentError_(f"no existe el proyecto {csproj}")
        r = subprocess.run(
            [DOTNET, "build", str(csproj), "-v", "q", "--nologo"],
            capture_output=True, text=True, encoding="utf-8", errors="replace")
        if r.returncode != 0:
            raise EnvironmentError_(
                f"falló la compilación de {project}:\n{r.stdout}\n{r.stderr}")
        _BUILT.add(project)


def preflight() -> None:
    """Falla temprano y con un mensaje claro si falta algo del entorno."""
    missing = []
    if not (MU_ROOT / "MuClient" / "Data" / "Enc1.dat").exists():
        missing.append(f"MuClient/Data/Enc1.dat (buscado en {MU_ROOT})")
    if not (MU_ROOT / "MuServer99B" / "Data").is_dir():
        missing.append(f"MuServer99B/Data (buscado en {MU_ROOT})")
    if missing:
        raise EnvironmentError_("faltan archivos del paquete original:\n  - "
                                + "\n  - ".join(missing))

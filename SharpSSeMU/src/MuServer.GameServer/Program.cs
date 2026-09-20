using MuServer.GameServer;
using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using MuServer.Shared.Crypto;
using MuServer.Shared.Logging;

// Puerto funcional del GameServer original (SSeMU 0.99B) a .NET 8 -- Fase 1 (núcleo de conexión) +
// Fase 2 (entrar al mundo: selección de personaje, mapa, viewport de jugadores, movimiento).
// Acepta clientes reales (con el cifrado completo del protocolo original), reenvía el login a
// JoinServer y la carga de personaje a DataServer (ambos ya portados).

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Log.Add(LogColor.Black, "SSeMU GameServer (C# port, Fase 2) iniciando...");

var config = GameServerConfig.Load(Path.Combine(baseDir, "GameServer.ini"), baseDir);

// Puerto parcial de CServerInfo (ver Config/ServerInfoConfig.cs) -- solo los campos con consumidor
// real ya portado (fórmula de experiencia, tiempo de vida de items en el piso, rates de monstruo,
// AddExperienceRate, DevilSquareMaxUser). Seteado en WorldPacketBuilder.ServerInfo ANTES de spawnear
// monstruos (que ya leen los rates al construirse) y de armar el DevilSquareManager.
var serverInfo = ServerInfoConfig.Load(config.ServerInfoCommonPath, config.ServerInfoEventPath);
WorldPacketBuilder.ServerInfo = serverInfo;

// Cobertura COMPLETA de los 7 archivos GameServerInfo - *.dat (ver Config/GameServerInfoCommon.cs
// para la explicación completa): a diferencia de `serverInfo`/`characterBalance` de arriba (que solo
// cargan los campos con un consumidor real ya portado), estas 8 clases (7 archivos + Custom.dat que
// en realidad son 3 sub-lectores) cargan TODOS los ~845 campos reales, así que ningún valor de estos
// archivos queda hardcodeado -- si el .dat está en Data/, se lee tal cual; si falta, cada campo cae
// al mismo default 0 que usa GetPrivateProfileInt en el original. Todavía no tienen consumidor (los
// sistemas de juego que los usarían -- Chaos Mix, /reset, Custom Arena/Attack/Pick, Mana Shield, PK,
// Trade, Guild, etc. -- no están portados), así que por ahora solo se cargan y quedan disponibles acá
// (variables locales) para que el próximo sistema que se porte las enchufe sin tener que escribir el
// parseo del .dat de cero.
var gsiCommon = GameServerInfoCommon.Load(config.ServerInfoCommonPath);
var gsiCharacter = GameServerInfoCharacter.Load(config.CharacterInfoPath);
var gsiChaosMix = GameServerInfoChaosMix.Load(config.ServerInfoChaosMixPath);
var gsiCommand = GameServerInfoCommand.Load(config.ServerInfoCommandPath);
var gsiEvent = GameServerInfoEvent.Load(config.ServerInfoEventPath);
var gsiItem = GameServerInfoItem.Load(config.ServerInfoItemDatPath);
var gsiSkill = GameServerInfoSkill.Load(config.ServerInfoSkillDatPath);
var gsiCustom = GameServerInfoCustom.Load(config.ServerInfoCustomPath);
Log.Add(LogColor.Blue, "[GameServerInfo] Cobertura completa cargada: Common/Character/ChaosMix/Command/Event/Item/Skill/Custom (845 campos reales, sin hardcodear)");

PacketCipher packetCipher;

try
{
    packetCipher = PacketCipher.LoadFromFiles(config.EncryptionKeyPath, config.DecryptionKeyPath);
}
catch (Exception ex)
{
    Log.Add(LogColor.Red, "No se pudieron cargar las claves de cifrado ({0} / {1}): {2}", config.EncryptionKeyPath, config.DecryptionKeyPath, ex.Message);
    return;
}

var streamCipher = GameStreamCipher.FromServerSerial(config.ServerSerial, config.ServerEncDecKey1, config.ServerEncDecKey2);

var maps = new MapRegistry();
maps.LoadAll(config.TerrainPath);

var players = new PlayerRegistry();

// Fase 4 (primera pasada): monstruos estáticos -- ver World/Monster.cs para las limitaciones
// documentadas de esta pasada (sin IA de patrulla/persecución, sin contraataque).
var monsterInfoTable = new MonsterInfoTable();
monsterInfoTable.Load(config.MonsterListPath);
var monsterSpawnEntries = MonsterSpawnTable.LoadAll(config.MonsterSpawnPath);
var monsters = new MonsterRegistry();
monsters.SpawnAll(monsterSpawnEntries, monsterInfoTable, maps);

// Fase 5 (primera pasada): grupos -- ver World/PartyGroup.cs/PartyRegistry.cs.
var parties = new PartyRegistry();

// Fase 4 (segunda pasada): balance real de items/combate -- ver World/ItemBalance.cs,
// World/ItemCombatMath.cs, Config/CharacterBalanceConfig.cs y PlayerObject.RecalcCombatStats.
var itemBalance = new ItemBalanceTable();
itemBalance.Load(config.ItemPath);

// Precios explícitos de joyas/entradas de evento/pociones de asedio. Sin esta tabla esos objetos
// caen a la fórmula general de CItem::Value(), que para ellos da órdenes de magnitud de diferencia
// -- ver World/ItemValue.cs.
var itemValues = new ItemValueTable();
itemValues.Load(config.ItemValuePath);
var characterBalance = CharacterBalanceConfig.Load(config.CharacterInfoPath, config.ServerInfoCommonPath);

// Fase "skills": magia y maná -- ver World/SkillInfo.cs, Config/CharacterBalanceConfig.cs (consts de
// daño mágico/regeneración) y ClientProtocolHandler.OnSkillAttackAsync.
var skills = new SkillInfoTable();
skills.Load(config.SkillListPath);
var skillDamage = new SkillDamageTable();
skillDamage.Load(config.SkillDamagePath);

// Fase 6 (primera pasada): Devil Square -- ver World/DevilSquareData.cs/DevilSquareManager.cs. Los
// datos se cargan acá (no dependen de DataServerConnection), pero el DevilSquareManager en sí se
// arma más abajo, una vez que dataServer existe (necesita mandarle el guardado de ranking, head 0x3F).
var devilSquareConfig = DevilSquareConfig.Load(Path.Combine(config.EventPath, "DevilSquare.dat"));
var eventEntryLevels = EventEntryLevelTable.Load(Path.Combine(config.EventPath, "EventEntryLevel.dat"));
var eventStageSpawns = EventStageSpawnTable.Load(Path.Combine(config.EventPath, "EventStageSpawn.dat"));
var eventSpawnPool = monsterSpawnEntries.Where(e => e.Type == 4).ToList();

// NPCs y tiendas (primera pasada): ver World/Shop.cs -- se cargan acá (dependen de itemBalance ya
// cargada, arriba, para el empaquetado por Width/Height) y cada NPC se spawnea como un Monster más
// (mismo mecanismo de índices/viewport, ver Monster.ShopNumber/MonsterRegistry.SpawnNpc).
var shops = new ShopManagerTable();
shops.Load(config.ShopManagerPath, config.ShopDataPath, itemBalance);

int npcsSpawned = 0;

foreach (var shop in shops.All)
{
    if (monsters.SpawnNpc(shop, maps) != null)
    {
        npcsSpawned++;
    }
}

Log.Add(LogColor.Blue, "[ShopManagerTable] {0} NPC(s) de tienda spawneados", npcsSpawned);

// Misiones (Quest/QuestObjective/QuestReward) -- motor real data-driven (ver Config/QuestTable.cs)
// que reemplaza la versión anterior hardcodeada de "hablar con Sebina/Marlon" (causaba
// "Conversation is over" en el cliente real para cualquier jugador que no calzara exactamente el
// nivel 150 fijo que se había adivinado, en vez de consultar los requisitos reales del .txt).
var questTable = QuestTable.Load(config.QuestPath);
var questObjectiveTable = QuestObjectiveTable.Load(config.QuestObjectivePath);
var questRewardTable = QuestRewardTable.Load(config.QuestRewardPath);

using var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

ClientProtocolHandler? protocolHandler = null;

var joinServer = new JoinServerConnection(
    config.JoinServerAddress, config.JoinServerPort, config.ServerName, config.ServerPort, config.ServerCode,
    onAccountResult: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnJoinAccountResultAsync(msg, ct);
        }
    },
    onDisconnectAck: (msg, _) =>
    {
        // Ver el comentario largo en JoinServerConnection.DispatchAsync (case 0x02): este puerto ya
        // cierra la sesión de forma proactiva cuando el socket se desconecta de verdad, así que no
        // hace falta buscarla de nuevo acá para cerrarla -- solo se deja registrado el resultado.
        Log.Add(LogColor.Blue, "[JoinServer] Cuenta '{0}' liberada del lado JoinServer (index={1}, result={2})",
            msg.Account, msg.Index, msg.Result);
        return Task.CompletedTask;
    });

var dataServer = new DataServerConnection(
    config.DataServerAddress, config.DataServerPort, config.ServerName, config.ServerPort, config.ServerCode,
    onCharacterInfo: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnCharacterInfoFromDataServerAsync(msg, ct);
        }
    },
    onCharacterList: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnCharacterListFromDataServerAsync(msg, ct);
        }
    },
    onCharacterCreate: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnCharacterCreateResultFromDataServerAsync(msg, ct);
        }
    },
    onGlobalWhisperResult: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnGlobalWhisperResultFromDataServerAsync(msg, ct);
        }
    },
    onGlobalWhisperEcho: async (msg, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnGlobalWhisperEchoFromDataServerAsync(msg, ct);
        }
    },
    onFriend: async (packet, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnFriendFromDataServerAsync(packet, ct);
        }
    },
    onWarehouse: async (packet, ct) =>
    {
        if (protocolHandler != null)
        {
            await protocolHandler.OnWarehouseFromDataServerAsync(packet, ct);
        }
    });

var devilSquare = new DevilSquareManager(
    devilSquareConfig, eventEntryLevels, eventStageSpawns, eventSpawnPool, monsterInfoTable, maps, monsters, players, dataServer);

// Items tirados en el piso (muerte de monstruo + tirar/recoger del inventario) -- ver
// World/GroundItem.cs.
var groundItems = new GroundItemRegistry();

var gates = new GateTable();
string gatePath = Path.Combine(Path.GetDirectoryName(config.ItemPath) ?? "Data/Item", "..", "Move", "Gate.txt");
gates.Load(gatePath);

var moves = new MoveTable();
string movePath = Path.Combine(Path.GetDirectoryName(config.ItemPath) ?? "Data/Item", "..", "Move", "Move.txt");
moves.Load(movePath);

var messages = new MessageTable();
string messagePath = Path.Combine(Path.GetDirectoryName(config.ItemPath) ?? "Data/Item", "..", "Message.txt");
messages.Load(messagePath);

var bloodCastle = new BloodCastleManager(players);

protocolHandler = new ClientProtocolHandler(
    config, joinServer, dataServer, players, maps, monsters, parties, itemBalance, characterBalance,
    skills, skillDamage, devilSquare, shops, groundItems, gates, moves, bloodCastle, messages,
    questTable, questObjectiveTable, questRewardTable, gsiCommon, itemValues, gsiChaosMix);

joinServer.Start(cts.Token);
dataServer.Start(cts.Token);
devilSquare.Start(cts.Token);
bloodCastle.Start(cts.Token);

var clientListener = new GameClientListener(config.ServerPort, packetCipher, streamCipher, protocolHandler);
clientListener.Start(cts.Token);

// Heartbeat UDP 0xA1 hacia ConnectServer -- sin esto ConnectServer nunca muestra este GameServer
// en la lista ni puede resolver su IP:puerto para el cliente real (ver ServerList.dat).
var connectServerHeartbeat = new GameServerHeartbeatClient(
    config.ConnectServerAddress, config.ConnectServerPort, config.ServerCode, config.ServerMaxUserNumber,
    getUserCount: () => clientListener.ConnectedCount);
connectServerHeartbeat.Start(cts.Token);

// Estado en vivo para MuServer.AdminPanel -- ver StatusWriter.cs. Mismo directorio Data/ que el
// resto de la config (junto a Item.txt, no adentro de Item/).
var dataDirectory = Path.GetDirectoryName(Path.GetDirectoryName(config.ItemPath)) ?? "Data";
var statusWriter = new StatusWriter(dataDirectory, config.ServerName, config.ServerMaxUserNumber,
    getPlayerCount: () => clientListener.ConnectedCount,
    getPlayers: () => players.All
        .Select(p => new OnlinePlayerInfo(p.Account, p.Name, p.Level, (int)p.Reset, p.Class, p.Map))
        .ToList());
statusWriter.Start(cts.Token);

// Comando "mensaje global" que el panel deja tirado -- ver GlobalMessagePoller.cs.
var globalMessagePoller = new GlobalMessagePoller(dataDirectory, players);
globalMessagePoller.Start(cts.Token);

// Tick periódico de viewport (aparecer/desaparecer jugadores cercanos) -- ver World/ViewportTicker.cs.
// También dispara el broadcast periódico de vida/maná de grupo (PMSG_PARTY_LIFE_SEND, ver
// GCPartyLifeSend en el brief de investigación de la Fase 5).
var viewportTicker = new ViewportTicker(players, maps, monsters, parties, protocolHandler, characterBalance, groundItems, gates, skills, skillDamage);
viewportTicker.Start(cts.Token);

Log.Add(LogColor.Blue, "GameServer listo (Fase 2) en el puerto TCP {0}. Comandos: 'exit'", config.ServerPort);

// Ctrl+C / cierre de consola: apagado ordenado en vez de matar el proceso a lo bruto. Es la única
// forma de parar el servidor cuando corre sin consola interactiva (ver el manejo de EOF de abajo).
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // que no mate el proceso: cancelamos nosotros y salimos por el camino normal
    Log.Add(LogColor.Blue, "Ctrl+C recibido, apagando...");
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();

    if (line == null)
    {
        // Sin consola interactiva (lanzado como servicio/en background, con stdin redirigido o
        // cerrado): no hay comandos que leer, pero el servidor SÍ tiene que seguir corriendo. Antes
        // se salía acá, y eso mataba el proceso apenas arrancaba en cuanto no había una consola de
        // verdad detrás. Se espera la cancelación (Ctrl+C o cierre) en vez de terminar.
        Log.Add(LogColor.Blue, "Sin consola interactiva: los comandos quedan deshabilitados, el servidor sigue corriendo.");

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        break;
    }

    var trimmed = line.Trim();

    switch (trimmed.ToLowerInvariant())
    {
        case "exit":
        case "quit":
            cts.Cancel();
            break;

        default:
            // Puerto de IDM_EVENT_FORCEDEVILSQUARE (GameServer.cpp:280-281) -- 'ds forcestart' fuerza
            // el próximo Devil Square (todos los brackets) a abrir en unos segundos, sin esperar el
            // horario real; 'ds forcestart N' fuerza solo el bracket N (1-based, igual que los logs).
            if (trimmed.StartsWith("ds forcestart", StringComparison.OrdinalIgnoreCase))
            {
                // 'ds forcestart' | 'ds forcestart N' (bracket 1-based) | 'ds forcestart N S' (+ demora en segundos)
                var parts = trimmed["ds forcestart".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int? level = parts.Length > 0 && int.TryParse(parts[0], out var n) ? n - 1 : null;

                if (parts.Length > 1 && int.TryParse(parts[1], out var delay))
                {
                    devilSquare.ForceStart(level, delay);
                }
                else
                {
                    devilSquare.ForceStart(level);
                }
            }

            break;
    }
}

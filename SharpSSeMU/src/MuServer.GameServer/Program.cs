using MuServer.Shared.Config;
using MuServer.Shared.Localization;
using MuServer.GameServer;
using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using MuServer.Shared.Crypto;
using MuServer.Shared.Logging;

// Functional port of the original GameServer (SSeMU 0.99B) to .NET 8 -- Phase 1 (connection core) + Phase 2
// (entering the world: character selection, map, player viewport, movement). It accepts real clients (with the
// original protocol's full encryption), forwards the login to JoinServer and the character load to DataServer
// (both already ported).

var baseDir = AppContext.BaseDirectory;
Log.Configure(Path.Combine(baseDir, "LOG"));

Loc.Configure(IniFile.Load(Path.Combine(baseDir, "GameServer.ini")).GetString("GameServerInfo", "Language", "en"));  // en | es

Log.Add(LogColor.Black, "SSeMU GameServer (C# port) starting...");

var config = GameServerConfig.Load(Path.Combine(baseDir, "GameServer.ini"), baseDir);

// Partial port of CServerInfo (see Config/ServerInfoConfig.cs) -- only the fields with a real consumer already
// ported (experience formula, lifetime of items on the ground, monster rates, AddExperienceRate,
// DevilSquareMaxUser). Set in WorldPacketBuilder.ServerInfo BEFORE spawning monsters (which already read the
// rates when constructed) and building the DevilSquareManager.
var serverInfo = ServerInfoConfig.Load(config.ServerInfoCommonPath, config.ServerInfoEventPath);
WorldPacketBuilder.ServerInfo = serverInfo;

// FULL coverage of the 7 GameServerInfo - *.dat files (see Config/GameServerInfoCommon.cs for the full
// explanation): unlike `serverInfo`/`characterBalance` above (which only load the fields with a real consumer
// already ported), these 8 classes (7 files + Custom.dat which is actually 3 sub-readers) load ALL ~845 real
// fields, so no value of these files stays hardcoded -- if the .dat is in Data/, it is read as is; if it is
// missing, each field falls back to the same default 0 that GetPrivateProfileInt uses in the original. They
// have no consumer yet (the game systems that would use them -- Chaos Mix, /reset, Custom Arena/Attack/Pick,
// Mana Shield, PK, Trade, Guild, etc. -- are not ported), so for now they are only loaded and left available
// here (local variables) so that the next system ported can plug them in without having to write the .dat
// parsing from scratch.
var gsiCommon = GameServerInfoCommon.Load(config.ServerInfoCommonPath);
var gsiCharacter = GameServerInfoCharacter.Load(config.CharacterInfoPath);
var gsiChaosMix = GameServerInfoChaosMix.Load(config.ServerInfoChaosMixPath);
var gsiCommand = GameServerInfoCommand.Load(config.ServerInfoCommandPath);
var gsiEvent = GameServerInfoEvent.Load(config.ServerInfoEventPath);
var gsiItem = GameServerInfoItem.Load(config.ServerInfoItemDatPath);
var gsiSkill = GameServerInfoSkill.Load(config.ServerInfoSkillDatPath);
var gsiCustom = GameServerInfoCustom.Load(config.ServerInfoCustomPath);
Log.Add(LogColor.Blue, "[GameServerInfo] Full coverage loaded: Common/Character/ChaosMix/Command/Event/Item/Skill/Custom (845 real fields, nothing hard-coded)");

PacketCipher packetCipher;

try
{
    packetCipher = PacketCipher.LoadFromFiles(config.EncryptionKeyPath, config.DecryptionKeyPath);
}
catch (Exception ex)
{
    Log.Add(LogColor.Red, "Could not load the encryption keys ({0} / {1}): {2}", config.EncryptionKeyPath, config.DecryptionKeyPath, ex.Message);
    return;
}

var streamCipher = GameStreamCipher.FromServerSerial(config.ServerSerial, config.ServerEncDecKey1, config.ServerEncDecKey2);

var maps = new MapRegistry();
maps.LoadAll(config.TerrainPath);

var players = new PlayerRegistry();

// Phase 4 (first pass): static monsters -- see World/Monster.cs for the documented limitations of this pass (no
// patrol/chase AI, no counterattack).
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

// Explicit prices for jewels/event tickets/siege potions. Without this table those objects fall back to the
// general CItem::Value() formula, which for them gives orders of magnitude of difference -- see
// World/ItemValue.cs.
var itemValues = new ItemValueTable();
itemValues.Load(config.ItemValuePath);
var characterBalance = CharacterBalanceConfig.Load(config.CharacterInfoPath, config.ServerInfoCommonPath);

// "Skills" phase: magic and mana -- see World/SkillInfo.cs, Config/CharacterBalanceConfig.cs (magic
// damage/regeneration constants) and ClientProtocolHandler.OnSkillAttackAsync.
var skills = new SkillInfoTable();
skills.Load(config.SkillListPath);
var skillDamage = new SkillDamageTable();
skillDamage.Load(config.SkillDamagePath);

// Phase 6 (first pass): Devil Square -- see World/DevilSquareData.cs/DevilSquareManager.cs. The data is loaded
// here (it does not depend on DataServerConnection), but the DevilSquareManager itself is built further down,
// once dataServer exists (it needs to send it the ranking save, head 0x3F).
var devilSquareConfig = DevilSquareConfig.Load(Path.Combine(config.EventPath, "DevilSquare.dat"));
var eventEntryLevels = EventEntryLevelTable.Load(Path.Combine(config.EventPath, "EventEntryLevel.dat"));
var eventStageSpawns = EventStageSpawnTable.Load(Path.Combine(config.EventPath, "EventStageSpawn.dat"));
var eventSpawnPool = monsterSpawnEntries.Where(e => e.Type == 4).ToList();

// NPCs and shops (first pass): see World/Shop.cs -- they are loaded here (they depend on the already loaded
// itemBalance, above, for the Width/Height packing) and each NPC is spawned as one more Monster (same
// index/viewport mechanism, see Monster.ShopNumber/MonsterRegistry.SpawnNpc).
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

Log.Add(LogColor.Blue, "[ShopManagerTable] {0} shop NPC(s) spawned", npcsSpawned);

// Quests (Quest/QuestObjective/QuestReward) -- real data-driven engine (see Config/QuestTable.cs) that replaces
// the earlier hardcoded "talk to Sebina/Marlon" version (which caused "Conversation is over" in the real client
// for any player who did not exactly match the fixed level 150 that had been guessed, instead of consulting the
// real requirements of the .txt).
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
        // See the long comment in JoinServerConnection.DispatchAsync (case 0x02): this port already closes the
        // session proactively when the socket really disconnects, so there is no need to look it up again here
        // to close it -- only the result is recorded.
        Log.Add(LogColor.Blue, "[JoinServer] Account '{0}' released on the JoinServer side (index={1}, result={2})",
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

// UDP heartbeat 0xA1 towards ConnectServer -- without this ConnectServer never shows this GameServer in the
// list nor can it resolve its IP:port for the real client (see ServerList.dat).
var connectServerHeartbeat = new GameServerHeartbeatClient(
    config.ConnectServerAddress, config.ConnectServerPort, config.ServerCode, config.ServerMaxUserNumber,
    getUserCount: () => clientListener.ConnectedCount);
connectServerHeartbeat.Start(cts.Token);

// Live status for MuServer.AdminPanel -- see StatusWriter.cs. Same Data/ directory as the rest of the config
// (next to Item.txt, not inside Item/).
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

// Periodic viewport tick (nearby players appearing/disappearing) -- see World/ViewportTicker.cs. It also
// triggers the periodic party life/mana broadcast (PMSG_PARTY_LIFE_SEND, see GCPartyLifeSend in the Phase 5
// research brief).
var viewportTicker = new ViewportTicker(players, maps, monsters, parties, protocolHandler, characterBalance, groundItems, gates, skills, skillDamage);
viewportTicker.Start(cts.Token);

Log.Add(LogColor.Blue, "GameServer ready on TCP port {0}. Commands: 'exit'", config.ServerPort);

// Ctrl+C / console close: orderly shutdown instead of killing the process outright. It is the only way to stop
// the server when it runs without an interactive console (see the EOF handling below).
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // so that it does not kill the process: we cancel ourselves and exit through the normal path
    Log.Add(LogColor.Blue, "Ctrl+C received, shutting down...");
    cts.Cancel();
};

while (!cts.Token.IsCancellationRequested)
{
    var line = Console.ReadLine();

    if (line == null)
    {
        // No interactive console (launched as a service/in the background, with stdin redirected or closed):
        // there are no commands to read, but the server DOES have to keep running. It used to exit here, and
        // that killed the process as soon as it started whenever there was no real console behind it.
        // Cancellation (Ctrl+C or close) is awaited instead of finishing.
        Log.Add(LogColor.Blue, "No interactive console: commands are disabled, the server keeps running.");

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
            // Port of IDM_EVENT_FORCEDEVILSQUARE (GameServer.cpp:280-281) -- 'ds forcestart' forces the next
            // Devil Square (all brackets) to open in a few seconds, without waiting for the real schedule; 'ds
            // forcestart N' forces only bracket N (1-based, same as the logs).
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

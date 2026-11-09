using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

public enum ChaosCastleState
{
    Blank = 0,
    Empty = 1,
    Stand = 2,
    Start = 3,
    Clean = 4,
}

/// <summary>A player inside a Chaos Castle (CHAOS_CASTLE_USER).</summary>
public sealed class ChaosCastleUser
{
    public required int PlayerIndex { get; init; }
    public int Score { get; set; }
    public int UserKillCount { get; set; }
    public int MonsterKillCount { get; set; }
    public int RewardExperience { get; set; }
}

/// <summary>One of the (up to 6) castles (CHAOS_CASTLE_LEVEL). <see cref="Level"/> is 0-based.</summary>
public sealed class ChaosCastleLevel
{
    public required int Level { get; init; }
    public required byte Map { get; init; }

    /// <summary>False for a castle whose map is not on this server (the package has only Chaos Castle 1's).</summary>
    public bool HasData { get; init; }

    public ChaosCastleState State { get; set; } = ChaosCastleState.Blank;
    public DateTime TargetTime { get; set; } = DateTime.UtcNow;
    public bool EnterEnabled { get; set; }
    public int MinutesLeft { get; set; } = -1;
    public bool TimeCountSent { get; set; }

    /// <summary>0 while everybody has the whole arena, 1-3 as the outer rings fall away.</summary>
    public int Stage { get; set; }
    public int MaxMonster { get; set; }

    /// <summary>Who won (the one with the best score once every monster is dead), -1 for nobody.</summary>
    public int WinnerIndex { get; set; } = -1;

    public List<ChaosCastleUser> Users { get; } = new();
    public HashSet<int> MonsterIndices { get; } = new();

    /// <summary>Scores that are sent a few seconds after a player's death (CHAOS_CASTLE_DELAY_SCORE).</summary>
    public List<(DateTime At, int PlayerIndex, byte[] Packet)> DelayedScores { get; } = new();
}

/// <summary>
/// Port of CChaosCastle (ChaosCastle.cpp). The players pay to enter a walled arena, wait in its safe zone, and when
/// the fight starts they kill the monsters and each other while the outer rings of the arena fall away one after
/// another; whoever stands on a fallen tile dies. The last ones standing, and the best score once the monsters are
/// gone, win. The castles are the Chaos Castle maps (18-23); only the ones whose map exists on the server work, and
/// the package has Chaos Castle 1's only.
/// <para>Player against player combat is enabled by this event only (<see cref="IsFightOn"/>): the rest of the
/// game has no PK system yet.</para>
/// <para>Deliberate differences from the original: <c>DelCenterHollowZone</c> clears the center holes (the
/// original calls <c>SetAttr</c> there, which leaves them set), a position the client reports after being blown
/// away is only taken if it is close to where the server has the player (the original takes any), and the event
/// experience has no per-account-level multiplier (this port has no such setting).</para>
/// </summary>
public sealed class ChaosCastleManager
{
    private static readonly Random Rng = Random.Shared;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // ChaosCastle.h:215-221. Zones are (x, y, x2, y2).
    private static readonly (byte X, byte Y, byte TX, byte TY) SafeZone = (23, 75, 44, 108);

    private static readonly (byte X, byte Y, byte TX, byte TY)[][] HollowZones =
    {
        new (byte, byte, byte, byte)[] { (23, 75, 44, 76), (43, 77, 44, 108), (23, 107, 42, 108), (23, 77, 24, 106) },
        new (byte, byte, byte, byte)[] { (23, 75, 44, 76), (43, 77, 44, 108), (23, 107, 42, 108), (23, 77, 24, 106) },
        new (byte, byte, byte, byte)[] { (25, 77, 42, 78), (41, 79, 42, 106), (25, 105, 40, 106), (25, 79, 26, 104) },
        new (byte, byte, byte, byte)[] { (27, 79, 40, 80), (39, 81, 40, 104), (27, 103, 38, 104), (27, 81, 28, 102) },
    };

    private static readonly (byte X, byte Y, byte TX, byte TY)[] CenterHollowZones = { (32, 84, 35, 87), (32, 96, 35, 99) };

    /// <summary>What it costs to enter each castle.</summary>
    public static readonly int[] EnterMoney = { 25000, 80000, 150000, 250000, 400000, 650000 };

    private static readonly int[] EntryGates = { 82, 83, 84, 85, 86, 87 };
    private const int ExitGate = 22; // Devias
    private const byte SafeAttr = 1;
    private const byte HollowAttr = 8;
    public const int MaxMonsters = 100;
    private const int FirstMap = 18;
    private static readonly int ArmorOfGuardsman = Item.GetItem(13, 29);
    private static readonly int Uniria = Item.GetItem(13, 2);
    private static readonly int Dinorant = Item.GetItem(13, 3);

    private readonly ChaosCastleConfig _config;
    private readonly EventEntryLevelTable _entryLevels;
    private readonly EventStageSpawnTable _stageSpawns;
    private readonly IReadOnlyList<MonsterSpawnEntry> _spawnPool;
    private readonly MonsterInfoTable _monsterInfo;
    private readonly MapRegistry _maps;
    private readonly MonsterRegistry _monsters;
    private readonly PlayerRegistry _players;
    private readonly GateTable _gates;
    private readonly GroundItemRegistry _groundItems;
    private readonly Func<byte[], CancellationToken, Task> _sendToDataServer;
    private readonly MessageTable _messages;
    private readonly ChaosCastleLevel[] _levels;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Gives experience that counts as an event reward (set by the protocol handler).</summary>
    public Func<PlayerObject, long, CancellationToken, Task>? GrantExperience { get; set; }

    /// <summary>Drops the item bag of an event around a player: (player, special value, level, state).</summary>
    public Func<PlayerObject, int, int, int, CancellationToken, Task>? GiveItemBag { get; set; }

    /// <summary>Saves the character (set by the protocol handler); an entry is counted for the day and kept.</summary>
    public Func<PlayerObject, CancellationToken, Task>? SaveCharacter { get; set; }

    public ChaosCastleManager(
        ChaosCastleConfig config, EventEntryLevelTable entryLevels, EventStageSpawnTable stageSpawns,
        IReadOnlyList<MonsterSpawnEntry> spawnPool, MonsterInfoTable monsterInfo, MapRegistry maps, MonsterRegistry monsters,
        PlayerRegistry players, GateTable gates, GroundItemRegistry groundItems, Func<byte[], CancellationToken, Task> sendToDataServer, MessageTable messages)
    {
        _config = config;
        _entryLevels = entryLevels;
        _stageSpawns = stageSpawns;
        _spawnPool = spawnPool;
        _monsterInfo = monsterInfo;
        _maps = maps;
        _monsters = monsters;
        _players = players;
        _gates = gates;
        _groundItems = groundItems;
        _sendToDataServer = sendToDataServer;
        _messages = messages;

        _levels = Enumerable.Range(0, ChaosCastleConfig.MaxLevel).Select(level => new ChaosCastleLevel
        {
            Level = level,
            Map = (byte)(FirstMap + level), // MAP_CHAOS_CASTLE1 .. MAP_CHAOS_CASTLE6
            HasData = maps.GetMap(FirstMap + level) != null,
        }).ToArray();
    }

    private static ServerInfoConfig ServerInfo => WorldPacketBuilder.ServerInfo;

    /// <summary>The clock the event runs on (tests move it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    public IReadOnlyList<ChaosCastleLevel> Levels => _levels;

    // ---------------------------------------------------------------- start up and the tick

    /// <summary>Port of CChaosCastle::ForceStart: opens the castles in <paramref name="delaySeconds"/>.</summary>
    public async Task ForceStartAsync(int? level = null, int delaySeconds = 40)
    {
        await _lock.WaitAsync();

        try
        {
            var target = Now.AddSeconds(delaySeconds);
            _config.Schedule.Add(new DevilSquareStartTime(target.Year, target.Month, target.Day, -1, target.Hour, target.Minute, target.Second));

            foreach (var castle in _levels.Where(l => l.HasData && (!level.HasValue || l.Level == level.Value)))
            {
                await SetStateEmptyAsync(castle, CancellationToken.None);
            }
        }
        finally
        {
            _lock.Release();
        }

        Log.Add(LogColor.Blue, "[ChaosCastle] ForceStart -- next opening in ~{0}s", delaySeconds);
    }

    public void Start(CancellationToken ct)
    {
        _ = RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TickAllAsync(ct);
        }
    }

    /// <summary>Port of CChaosCastle::Init: every castle that has a map starts closed, waiting for its next start time.</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        if (!ServerInfo.ChaosCastleEvent)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            foreach (var castle in _levels.Where(l => l.HasData))
            {
                await SetStateEmptyAsync(castle, ct);
            }
        }
        finally
        {
            _lock.Release();
        }

        Log.Add(LogColor.Blue, "[ChaosCastle] {0} castle(s) with a map on this server", _levels.Count(l => l.HasData));
    }

    /// <summary>One second of the event for every castle (MainProc).</summary>
    internal async Task TickAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            foreach (var castle in _levels)
            {
                // A castle with no start time left (Blank) still sends the scores that are waiting, as the original's
                // MainProc does for every castle whatever its state.
                if (!castle.HasData)
                {
                    continue;
                }

                try
                {
                    await TickAsync(castle, ct);
                }
                catch (Exception ex)
                {
                    Log.Add(LogColor.Red, "[ChaosCastle] ({0}) Error in tick: {1}", castle.Level + 1, ex.Message);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of CChaosCastle::MainProc for one castle.</summary>
    private async Task TickAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        int remain = (int)(castle.TargetTime - Now).TotalSeconds;

        switch (castle.State)
        {
            case ChaosCastleState.Empty:
                await ProcEmptyAsync(castle, remain, ct);
                break;
            case ChaosCastleState.Stand:
                await ProcStandAsync(castle, remain, ct);
                break;
            case ChaosCastleState.Start:
                await ProcStartAsync(castle, remain, ct);
                break;
            case ChaosCastleState.Clean:
                await ProcCleanAsync(castle, remain, ct);
                break;
        }

        await SendDelayedScoresAsync(castle, ct);
    }

    private async Task ProcEmptyAsync(ChaosCastleLevel castle, int remain, CancellationToken ct)
    {
        if (remain > 0 && remain <= _config.WarningMinutes * 60)
        {
            await CheckUsersAsync(castle);

            int minutes = remain / 60;

            if (remain % 60 == 0)
            {
                minutes--;
            }

            if (castle.MinutesLeft != minutes)
            {
                castle.MinutesLeft = minutes;

                if (castle.Level == 0)
                {
                    await NoticeToAllOnlineAsync(BloodCastleManager.Fmt(_messages.GetMessage(16), castle.MinutesLeft + 1), ct);
                }
            }

            castle.EnterEnabled = true;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToAllOnlineAsync(DevilSquarePacketBuilder.TimeCountSend(10), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            if (castle.Level == 0)
            {
                await NoticeToAllOnlineAsync(_messages.GetMessage(17), ct);
            }

            await NoticeToUsersAsync(castle, 1, BloodCastleManager.Fmt(_messages.GetMessage(18), castle.Level + 1, _config.NotifyMinutes), ct);
            await SetStateStandAsync(castle, ct);
        }
    }

    private async Task ProcStandAsync(ChaosCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle);

        if (castle.Users.Count < ServerInfo.ChaosCastleMinUser)
        {
            Log.Add(LogColor.Black, "[Chaos Castle] ({0}) Not enough users", castle.Level + 1);
            await NoticeToUsersAsync(castle, 0, _messages.GetMessage(21), ct);
            await SetStateEmptyAsync(castle, ct);
            return;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(11), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await NoticeToUsersAsync(castle, 0, _messages.GetMessage(19), ct);
            await SetStateStartAsync(castle, ct);
        }
    }

    private async Task ProcStartAsync(ChaosCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle);

        if (castle.Users.Count == 0)
        {
            Log.Add(LogColor.Black, "[Chaos Castle] ({0}) Not enough users", castle.Level + 1);
            await SetStateEmptyAsync(castle, ct);
            return;
        }

        switch (castle.Stage)
        {
            case 0: await SetStageAsync(castle, 1, 40, 8, ct); break;
            case 1: await SetStageAsync(castle, 2, 30, 9, ct); break;
            case 2: await SetStageAsync(castle, 3, 20, 10, ct); break;
        }

        await CheckUserPositionAsync(castle, ct);
        CheckMonsterPosition(castle);
        CheckItemPosition(castle);

        await SendStateAsync(castle, 6, ct);

        if (castle.Users.Count == 1 && castle.MonsterIndices.Count == 0)
        {
            await SetStateCleanAsync(castle, ct);
            return;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(12), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await NoticeToUsersAsync(castle, 0, _messages.GetMessage(20), ct);
            await SetStateCleanAsync(castle, ct);
        }
    }

    private async Task ProcCleanAsync(ChaosCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle);

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(13), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await SetStateEmptyAsync(castle, ct);
        }
    }

    // ---------------------------------------------------------------- the state changes (SetState_*)

    private async Task SetStateEmptyAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        castle.State = ChaosCastleState.Empty;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.Stage = 0;
        castle.MaxMonster = 0;
        castle.WinnerIndex = -1;

        await SendSimpleStateAsync(castle, 7, ct);
        await ClearUsersAsync(castle, ct);
        ClearMonsters(castle);
        await SetSafeZoneAsync(castle, set: true, ct);
        ApplyZones(castle, HollowZones[1], HollowAttr, set: false);
        ApplyZones(castle, HollowZones[2], HollowAttr, set: false);
        ApplyZones(castle, HollowZones[3], HollowAttr, set: false);
        ApplyZones(castle, CenterHollowZones, HollowAttr, set: false);
        CheckSync(castle);

        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) SetState EMPTY", castle.Level + 1);
    }

    private async Task SetStateStandAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        castle.State = ChaosCastleState.Stand;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;

        await GiveUserRewardMoneyAsync(castle, ct);
        ApplyZones(castle, CenterHollowZones, HollowAttr, set: true);
        castle.TargetTime = Now.AddMinutes(_config.NotifyMinutes);

        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) SetState STAND", castle.Level + 1);
    }

    private async Task SetStateStartAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        castle.State = ChaosCastleState.Start;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.MaxMonster = castle.Users.Count;

        await SetSafeZoneAsync(castle, set: false, ct);
        StageSpawn(castle, 0);
        await SendSimpleStateAsync(castle, 5, ct);
        castle.TargetTime = Now.AddMinutes(_config.EventMinutes);

        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) SetState START", castle.Level + 1);
    }

    private async Task SetStateCleanAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        castle.State = ChaosCastleState.Clean;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.WinnerIndex = GetWinner(castle);

        ClearMonsters(castle);

        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player))
            {
                continue;
            }

            bool won = player.Index == castle.WinnerIndex;

            await GiveUserRewardExperienceAsync(castle, user, player, ct);

            if (won && GiveItemBag != null)
            {
                _ = GiveItemBag(player, ItemBagManager.SpecialChaosCastle, castle.Level, 1, ct);
            }

            await player.Session.SendAsync(ScorePacket(user, player, won), ct);

            await _sendToDataServer(
                EventDataServerPacketBuilder.RankingScoreSave(EventDataServerPacketBuilder.HeadChaosCastle, (ushort)player.Index, player.Account, player.Name, (uint)user.Score), ct);
        }

        await SendSimpleStateAsync(castle, 7, ct);
        castle.TargetTime = Now.AddMinutes(_config.CloseMinutes);

        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) SetState CLEAN", castle.Level + 1);
    }

    /// <summary>Port of CheckSync: the next start time from the schedule.</summary>
    private void CheckSync(ChaosCastleLevel castle)
    {
        var next = _config.Schedule.Count == 0 ? null : DevilSquareManager.ComputeNextOccurrence(_config.Schedule, Now);

        if (next == null)
        {
            castle.State = ChaosCastleState.Blank;
            return;
        }

        castle.TargetTime = next.Value;
        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) Sync Start Time. [{1}] min remain", castle.Level + 1, (int)(next.Value - Now).TotalMinutes);
    }

    // ---------------------------------------------------------------- the stages

    /// <summary>Port of SetStage0/1/2: once few enough are left (players plus monsters), the next ring of the
    /// arena falls away and the clients are told.</summary>
    private async Task SetStageAsync(ChaosCastleLevel castle, int nextStage, int fewerThan, byte clientState, CancellationToken ct)
    {
        if (castle.Users.Count + castle.MonsterIndices.Count > fewerThan)
        {
            return;
        }

        castle.Stage = nextStage;
        await SendSimpleStateAsync(castle, clientState, ct);
        ApplyZones(castle, HollowZones[nextStage], HollowAttr, set: true);
    }

    // ---------------------------------------------------------------- monsters

    /// <summary>Port of StageSpawn/SetMonster: the monsters the stage adds, at the Type 4 positions of the map. They
    /// do not come back once killed.</summary>
    private void StageSpawn(ChaosCastleLevel castle, int stage)
    {
        int spawned = 0;

        foreach (var row in _stageSpawns.GetChaosCastleEntries(castle.Level, stage))
        {
            foreach (var position in _spawnPool.Where(e => e.Map == castle.Map && e.MonsterClass == row.MonsterClass))
            {
                if (castle.MaxMonster >= MaxMonsters)
                {
                    break;
                }

                var monster = _monsters.SpawnOne(row.MonsterClass, position, _monsterInfo, _maps);

                if (monster == null)
                {
                    continue;
                }

                monster.ChaosCastleLevel = castle.Level;
                monster.NoRespawn = true;
                castle.MonsterIndices.Add(monster.Index);
                castle.MaxMonster++;
                spawned++;
            }
        }

        Log.Add(LogColor.Black, "[Chaos Castle] ({0}) Stage {1}: {2} monster(s) added", castle.Level + 1, stage, spawned);
    }

    private void ClearMonsters(ChaosCastleLevel castle)
    {
        foreach (int index in castle.MonsterIndices)
        {
            _monsters.Remove(index);
        }

        castle.MonsterIndices.Clear();
    }

    /// <summary>Port of CheckMonsterPosition: a monster standing on a fallen tile is moved to the first free tile
    /// found around it.</summary>
    private void CheckMonsterPosition(ChaosCastleLevel castle)
    {
        var map = _maps.GetMap(castle.Map);

        if (map == null)
        {
            return;
        }

        foreach (int index in castle.MonsterIndices.ToList())
        {
            if (!_monsters.TryGet(index, out var monster) || !map.CheckAttr(monster.X, monster.Y, HollowAttr))
            {
                continue;
            }

            if (!GetFreePosition(map, monster.X, monster.Y, out int x, out int y))
            {
                continue;
            }

            monster.X = (byte)x;
            monster.Y = (byte)y;

            var packet = WorldPacketBuilder.ViewportMonsterMove(monster.Index, monster.X, monster.Y, monster.Dir);

            foreach (int observer in monster.VisibleTo.ToList())
            {
                if (_players.TryGet(observer, out var watcher) && watcher.WorldEntered)
                {
                    _ = watcher.Session.SendAsync(packet, CancellationToken.None);
                }
            }
        }
    }

    /// <summary>Port of GetFreePosition: the first tile in the 16x16 square around that is not a wall, a fallen tile
    /// or otherwise unusable.</summary>
    private static bool GetFreePosition(GameMap map, int ox, int oy, out int x, out int y)
    {
        for (x = ox - 8; x < ox + 8; x++)
        {
            for (y = oy - 8; y < oy + 8; y++)
            {
                if (!map.CheckAttr(x, y, 2) && !map.CheckAttr(x, y, 4) && !map.CheckAttr(x, y, 8))
                {
                    return true;
                }
            }
        }

        x = ox;
        y = oy;
        return false;
    }

    /// <summary>Port of CheckItemPosition: what lies on a fallen tile is gone.</summary>
    private void CheckItemPosition(ChaosCastleLevel castle)
    {
        var map = _maps.GetMap(castle.Map);

        if (map == null)
        {
            return;
        }

        foreach (var item in _groundItems.AllOnMap(castle.Map).ToList())
        {
            if (item.Live && map.CheckAttr(item.X, item.Y, HollowAttr))
            {
                item.ExpireAt = DateTime.UtcNow.AddSeconds(-1); // the ticker's sweep takes it away and tells the clients
            }
        }
    }

    // ---------------------------------------------------------------- map zones

    /// <summary>Changes the attribute bits of the zones on the SERVER's map (CMap::SetAttr / DelAttr).</summary>
    private void ApplyZones(ChaosCastleLevel castle, IReadOnlyList<(byte X, byte Y, byte TX, byte TY)> zones, byte attr, bool set)
    {
        var map = _maps.GetMap(castle.Map);

        if (map == null)
        {
            return;
        }

        foreach (var (x0, y0, x1, y1) in zones)
        {
            for (int x = x0; x <= x1; x++)
            {
                for (int y = y0; y <= y1; y++)
                {
                    if (set)
                    {
                        map.SetAttr(x, y, attr);
                    }
                    else
                    {
                        map.DelAttr(x, y, attr);
                    }
                }
            }
        }
    }

    /// <summary>The safe zone of the waiting room changes on both the server's map and the clients' (GCMapAttrSend:
    /// flag 0 sets, 1 clears).</summary>
    private async Task SetSafeZoneAsync(ChaosCastleLevel castle, bool set, CancellationToken ct)
    {
        var zones = new[] { SafeZone };
        ApplyZones(castle, zones, SafeAttr, set);
        await SendToUsersAsync(castle, BloodCastlePacketBuilder.MapAttrSend(0, SafeAttr, set ? (byte)0 : (byte)1, zones), ct);
    }

    // ---------------------------------------------------------------- users

    private ChaosCastleUser? GetUser(ChaosCastleLevel castle, int playerIndex) =>
        castle.Users.FirstOrDefault(u => u.PlayerIndex == playerIndex);

    private ChaosCastleLevel? LevelOfMap(int map) =>
        map is >= FirstMap and < FirstMap + ChaosCastleConfig.MaxLevel ? _levels[map - FirstMap] : null;

    /// <summary>True while the fight of the Chaos Castle that <paramref name="map"/> belongs to is on: the one place
    /// players may attack each other.</summary>
    public bool IsFightOn(int map)
    {
        var castle = LevelOfMap(map);
        return castle is { HasData: true, State: ChaosCastleState.Start };
    }

    /// <summary>Port of CheckUser: drops whoever disconnected or left the castle's map.</summary>
    private Task CheckUsersAsync(ChaosCastleLevel castle)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player) || player.Map != castle.Map)
            {
                castle.Users.Remove(user);
            }
        }

        return Task.CompletedTask;
    }

    private async Task ClearUsersAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (_players.TryGet(user.PlayerIndex, out var player) && player.Map == castle.Map)
            {
                await MoveToGateAsync(player, ExitGate, ct);
            }
        }

        castle.Users.Clear();
    }

    /// <summary>Port of gObjMoveGate: to a random spot of the gate's box (through its target gate when it is an
    /// entrance), telling the client.</summary>
    private async Task MoveToGateAsync(PlayerObject player, int gateNumber, CancellationToken ct)
    {
        var gate = _gates.Get(gateNumber);

        if (gate == null)
        {
            return;
        }

        if (gate.TargetGate != 0 && _gates.Get(gate.TargetGate) is { } target)
        {
            gate = target;
        }

        byte x = (byte)(gate.StartX + Rng.Next(Math.Max(gate.EndX - gate.StartX, 0) + 1));
        byte y = (byte)(gate.StartY + Rng.Next(Math.Max(gate.EndY - gate.StartY, 0) + 1));

        player.Map = (byte)gate.Map;
        player.X = x;
        player.Y = y;
        player.TX = x;
        player.TY = y;
        player.OldX = x;
        player.OldY = y;
        player.VisibleTo.Clear();
        player.VisibleMonsters.Clear();

        await player.Session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(0, player.Map, player.X, player.Y, player.Dir), ct);
    }

    /// <summary>Port of gObjSetPosition: the player is put on a tile, and they and whoever sees them are told.</summary>
    private async Task SetPositionAsync(PlayerObject player, int x, int y, CancellationToken ct)
    {
        x = Math.Clamp(x, 0, 255);
        y = Math.Clamp(y, 0, 255);

        player.X = (byte)x;
        player.Y = (byte)y;
        player.TX = (byte)x;
        player.TY = (byte)y;
        player.OldX = (byte)x;
        player.OldY = (byte)y;
        player.ChaosCastleBlowTime = DateTime.UtcNow;

        var packet = WorldPacketBuilder.PositionSend(player.Index, player.X, player.Y);
        await player.Session.SendAsync(packet, ct);

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(packet, ct);
            }
        }
    }

    // ---------------------------------------------------------------- the fall of the ring and the blow

    /// <summary>Port of CheckUserPosition: whoever stands on a fallen tile dies.</summary>
    private async Task CheckUserPositionAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        var map = _maps.GetMap(castle.Map);

        if (map == null)
        {
            return;
        }

        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player) || !map.CheckAttr(player.X, player.Y, HollowAttr))
            {
                continue;
            }

            player.Life = 0;
            player.IsDying = true;
            player.DiedAt = DateTime.UtcNow;

            await player.Session.SendAsync(LifePacketBuilder.LifeSend(0xFF, 0), ct);
            var diePacket = CombatPacketBuilder.UserDieSend(player.Index, 0, 0);
            await player.Session.SendAsync(diePacket, ct);

            foreach (int observer in player.VisibleTo.ToList())
            {
                if (_players.TryGet(observer, out var watcher) && watcher.WorldEntered)
                {
                    await watcher.Session.SendAsync(diePacket, ct);
                }
            }

            await GiveUserRewardExperienceAsync(castle, user, player, ct);
            castle.DelayedScores.Add((Now.AddSeconds(8), player.Index, ScorePacket(user, player, won: false)));
            castle.Users.Remove(user);
        }
    }

    /// <summary>Port of SearchUserAndBlow: everybody within three tiles of the point is thrown away from it.</summary>
    private async Task SearchUserAndBlowAsync(ChaosCastleLevel castle, int x, int y, CancellationToken ct)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player) || player.IsDying)
            {
                continue;
            }

            if (player.X >= x - 3 && player.X <= x + 3 && player.Y >= y - 3 && player.Y <= y + 3)
            {
                await BlowUserFromPointAsync(player, x, y, ct);
            }
        }
    }

    /// <summary>Port of BlowUserFromPoint (ChaosCastle.cpp:1257-1284).</summary>
    private async Task BlowUserFromPointAsync(PlayerObject player, int x, int y, CancellationToken ct)
    {
        int px = player.X - x;
        int py = player.Y - y;

        int signX = Math.Sign(px);

        if (signX == 0)
        {
            signX = Rng.Next(2) == 0 ? 1 : -1;
        }

        int signY = Math.Sign(py);

        if (signY == 0)
        {
            signY = Rng.Next(2) == 0 ? 1 : -1;
        }

        int dis = (int)Math.Sqrt((px * px) + (py * py));
        int tx = player.X + (((4 - dis) + Rng.Next(2)) * signX);
        int ty = player.Y + (((4 - dis) + Rng.Next(2)) * signY);

        await SetPositionAsync(player, tx, ty, ct);
    }

    /// <summary>C1:AF:02: after the blow the client reports where the player landed. The original takes any
    /// position; here it has to be near where the server has the player.</summary>
    public async Task HandlePositionAsync(PlayerObject player, int x, int y, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            var castle = LevelOfMap(player.Map);

            if (castle == null || !castle.HasData || GetUser(castle, player.Index) == null)
            {
                return;
            }

            if (Math.Abs(x - player.X) > 8 || Math.Abs(y - player.Y) > 8)
            {
                return;
            }

            await SetPositionAsync(player, x, y, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------------------------------------------------------- rewards

    private static int WinnerCandidates(ChaosCastleLevel castle) => castle.Users.Count;

    /// <summary>Port of GetUserWinner: nobody wins while a monster is left; then the best score (the first one
    /// on a tie).</summary>
    private static int GetWinner(ChaosCastleLevel castle)
    {
        if (castle.MonsterIndices.Count > 0)
        {
            return -1;
        }

        int winner = -1, best = -1;

        foreach (var user in castle.Users)
        {
            if (user.Score > best)
            {
                winner = user.PlayerIndex;
                best = user.Score;
            }
        }

        return winner;
    }

    /// <summary>Port of GiveUserRewardMoney: when the wait begins one participant, picked at random, wins a share of
    /// the entry fees of all of them.</summary>
    private async Task GiveUserRewardMoneyAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        if (ServerInfo.ChaosCastleMoneyRate == 0 || WinnerCandidates(castle) == 0)
        {
            return;
        }

        var user = castle.Users[Rng.Next(castle.Users.Count)];

        if (!_players.TryGet(user.PlayerIndex, out var winner))
        {
            return;
        }

        long money = (long)castle.Users.Count * ((long)EnterMoney[castle.Level] * ServerInfo.ChaosCastleMoneyRate / 100);

        const uint maxMoney = 2_000_000_000;
        winner.Money = money > maxMoney - winner.Money ? maxMoney : winner.Money + (uint)money;
        await winner.Session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(winner.Money), ct);
        await NoticeToUsersAsync(castle, 1, BloodCastleManager.Fmt(_messages.GetMessage(724), winner.Name, money), ct);
    }

    /// <summary>Port of GiveUserRewardExperience: kills of players and of monsters, each at the castle's price. The
    /// score the ranking keeps is a fifth of it.</summary>
    private async Task GiveUserRewardExperienceAsync(ChaosCastleLevel castle, ChaosCastleUser user, PlayerObject player, CancellationToken ct)
    {
        long experience = ((long)user.UserKillCount * _config.Experience[castle.Level][0]) + ((long)user.MonsterKillCount * _config.Experience[castle.Level][1]);
        user.RewardExperience = (int)Math.Min(experience, int.MaxValue);
        user.Score = (int)(experience / 5);

        if (user.RewardExperience > 0 && GrantExperience != null)
        {
            await GrantExperience(player, user.RewardExperience, ct);
        }
    }

    private static byte[] ScorePacket(ChaosCastleUser user, PlayerObject player, bool won) =>
        ChaosCastlePacketBuilder.ScoreSend(won ? (byte)1 : (byte)0, player.Name, (uint)user.MonsterKillCount, (uint)user.RewardExperience, (uint)user.UserKillCount);

    // ---------------------------------------------------------------- entering (CGChaosCastleEnterRecv)

    private static bool IsSpecialClass(PlayerObject player) => player.Class is 3 or 4; // MG, DL

    /// <summary>Port of CGChaosCastleEnterRecv. The castle is the one the player's level allows, whatever the client
    /// asked for.</summary>
    public async Task HandleEnterAsync(ClientSession session, PlayerObject player, int rawSlot, CancellationToken ct)
    {
        if (!ServerInfo.ChaosCastleEvent)
        {
            return;
        }

        int level = _entryLevels.GetChaosCastleLevel(player.Level, IsSpecialClass(player));

        if (level is < 0 or >= ChaosCastleConfig.MaxLevel)
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(_messages.GetMessage(22)), ct);
            return;
        }

        if (ServerInfoConfig.EntryCapReached(ServerInfo.ChaosCastleMaxEntryCount, player.AccountLevel, player.CCCount))
        {
            int cap = ServerInfo.ChaosCastleMaxEntryCount[Math.Clamp(player.AccountLevel, 0, 3)];
            await session.SendAsync(ChatPacketBuilder.NoticeSend(BloodCastleManager.Fmt(_messages.GetMessage(714), cap)), ct);
            return;
        }

        var castle = _levels[level];

        if (rawSlot < 0 || rawSlot >= Item.InventorySize || !player.Items[rawSlot].IsItem() || player.Items[rawSlot].Index != ArmorOfGuardsman)
        {
            await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(1), ct);
            return;
        }

        var mount = player.Items[Item.SlotHelper];

        if (mount.IsItem() && (mount.Index == Uniria || mount.Index == Dinorant))
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(_messages.GetMessage(159)), ct);
            return;
        }

        if (player.PartyNumber != -1)
        {
            await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(1), ct);
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (!castle.HasData || !castle.EnterEnabled)
            {
                await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(2), ct);
                return;
            }

            if (player.Money < EnterMoney[level])
            {
                await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(7), ct);
                return;
            }

            if (ServerInfo.PkBlocksEventEntry(player.PKLevel))
            {
                await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(8), ct);
                return;
            }

            if (castle.Users.Count >= 70 || GetUser(castle, player.Index) != null)
            {
                await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(5), ct);
                return;
            }

            castle.Users.Add(new ChaosCastleUser { PlayerIndex = player.Index });

            // gObjEventEntrySaveSend: the entry counts for the day and the character is saved with it.
            player.CCCount++;

            player.Money -= (uint)EnterMoney[level];
            await session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);

            player.SetItem(rawSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend((byte)rawSlot, 1), ct);

            await session.SendAsync(ChaosCastlePacketBuilder.EnterSend(0), ct);
            await MoveToGateAsync(player, EntryGates[level], ct);

            if (SaveCharacter != null)
            {
                await SaveCharacter(player, ct);
            }

            Log.Add(LogColor.Blue, "[Chaos Castle] ({0}) '{1}' entered ({2} player(s))", level + 1, player.Name, castle.Users.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The Chaos Castle part of CGEventRemainTimeRecv (event type 4).</summary>
    public async Task HandleRemainTimeQueryAsync(ClientSession session, PlayerObject player, CancellationToken ct)
    {
        int level = _entryLevels.GetChaosCastleLevel(player.Level, IsSpecialClass(player));

        if (level is < 0 or >= ChaosCastleConfig.MaxLevel)
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(_messages.GetMessage(22)), ct);
            return;
        }

        var castle = _levels[level];
        byte remainMinutes = 0, entered = 0;

        if (castle.State == ChaosCastleState.Empty && castle.EnterEnabled)
        {
            entered = (byte)Math.Min(castle.Users.Count, 255);
        }
        else
        {
            // GetRemainTime: the minutes (rounded up) to the next start in the schedule, in whatever state the castle is.
            var next = _config.Schedule.Count == 0 ? null : DevilSquareManager.ComputeNextOccurrence(_config.Schedule, Now);
            remainMinutes = next == null ? (byte)0 : (byte)Math.Min(Math.Ceiling(Math.Max((next.Value - Now).TotalMinutes, 0)), 255);
        }

        // The minutes travel in the low byte here (RemainTimeL), where Devil Square and Blood Castle use the high one.
        await session.SendAsync(DevilSquarePacketBuilder.RemainTimeSend(4, 0, entered, remainMinutes), ct);
    }

    // ---------------------------------------------------------------- deaths and kills

    /// <summary>Port of UserDieProc: a player who dies in the fight is out, with the reward so far; whoever killed
    /// them, if another participant, scores.</summary>
    public async Task OnPlayerDiedAsync(PlayerObject player, int killerIndex, CancellationToken ct)
    {
        var castle = LevelOfMap(player.Map);

        if (castle == null || !castle.HasData)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            var user = GetUser(castle, player.Index);

            if (castle.State != ChaosCastleState.Start || user == null)
            {
                return;
            }

            await GiveUserRewardExperienceAsync(castle, user, player, ct);
            castle.DelayedScores.Add((Now.AddSeconds(8), player.Index, ScorePacket(user, player, won: false)));
            castle.Users.Remove(user);

            if (_players.TryGet(killerIndex, out _) && GetUser(castle, killerIndex) is { } killer)
            {
                killer.Score += 1;
                killer.UserKillCount++;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The handler calls this when a player leaves the game: they stop counting.</summary>
    public async Task OnPlayerLeftAsync(PlayerObject player, CancellationToken ct)
    {
        var castle = LevelOfMap(player.Map);

        if (castle == null || !castle.HasData)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            castle.Users.RemoveAll(u => u.PlayerIndex == player.Index);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of MonsterDieProc: a monster killed may blow the players near it away, and counts for the player
    /// who did the most damage to it.</summary>
    public async Task OnMonsterKilledAsync(Monster monster, CancellationToken ct)
    {
        if (monster.ChaosCastleLevel is not int level)
        {
            return;
        }

        var castle = _levels[level];

        await _lock.WaitAsync(ct);

        try
        {
            if (castle.State != ChaosCastleState.Start || !castle.MonsterIndices.Contains(monster.Index))
            {
                return;
            }

            if (Rng.Next(100) < ServerInfo.ChaosCastleBlowUserRate)
            {
                await SearchUserAndBlowAsync(castle, monster.X, monster.Y, ct);
            }

            castle.MonsterIndices.Remove(monster.Index);

            int topIndex = -1, topDamage = -1;

            foreach (var (attackerIndex, damage) in monster.DamageByAttacker)
            {
                if (damage > topDamage)
                {
                    topDamage = damage;
                    topIndex = attackerIndex;
                }
            }

            if (GetUser(castle, topIndex) is { } user)
            {
                user.Score += 2;
                user.MonsterKillCount++;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------------------------------------------------------- packets and notices

    private async Task SendStateAsync(ChaosCastleLevel castle, byte state, CancellationToken ct)
    {
        var packet = BloodCastlePacketBuilder.StateSend(
            state, (ushort)Math.Max((castle.TargetTime - Now).TotalSeconds, 0), (ushort)castle.MaxMonster,
            (ushort)(castle.Users.Count + castle.MonsterIndices.Count), 0, 0xFF);
        await SendToUsersAsync(castle, packet, ct);
    }

    private Task SendSimpleStateAsync(ChaosCastleLevel castle, byte state, CancellationToken ct) =>
        SendToUsersAsync(castle, BloodCastlePacketBuilder.StateSend(state, 0, 0, 0, 0, 0xFF), ct);

    private async Task SendDelayedScoresAsync(ChaosCastleLevel castle, CancellationToken ct)
    {
        foreach (var delayed in castle.DelayedScores.Where(d => d.At <= Now).ToList())
        {
            castle.DelayedScores.Remove(delayed);

            if (_players.TryGet(delayed.PlayerIndex, out var player))
            {
                await player.Session.SendAsync(delayed.Packet, ct);
            }
        }
    }

    private async Task SendToUsersAsync(ChaosCastleLevel castle, byte[] packet, CancellationToken ct)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (_players.TryGet(user.PlayerIndex, out var player))
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    private async Task NoticeToUsersAsync(ChaosCastleLevel castle, byte type, string text, CancellationToken ct) =>
        await SendToUsersAsync(castle, ChatPacketBuilder.NoticeSend(text, type), ct);

    private async Task SendToAllOnlineAsync(byte[] packet, CancellationToken ct)
    {
        foreach (var player in _players.All)
        {
            if (player.WorldEntered)
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    private Task NoticeToAllOnlineAsync(string text, CancellationToken ct) =>
        SendToAllOnlineAsync(ChatPacketBuilder.NoticeSend(text), ct);
}

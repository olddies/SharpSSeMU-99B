using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

public enum BloodCastleState
{
    Blank = 0,
    Empty = 1,
    Stand = 2,
    Start = 3,
    Clean = 4,
}

/// <summary>A player inside a Blood Castle (BLOOD_CASTLE_USER).</summary>
public sealed class BloodCastleUser
{
    public required int PlayerIndex { get; init; }
    public int Score { get; set; }
    public int RewardExperience { get; set; }
    public int RewardMoney { get; set; }
}

/// <summary>One of the (up to 7) castles (BLOOD_CASTLE_LEVEL). <see cref="Level"/> is 0-based.</summary>
public sealed class BloodCastleLevel
{
    public required int Level { get; init; }
    public required byte Map { get; init; }

    /// <summary>False for a castle whose map is not on this server (the package has only Blood Castle 1's).</summary>
    public bool HasData { get; init; }

    public BloodCastleState State { get; set; } = BloodCastleState.Blank;
    public DateTime TargetTime { get; set; } = DateTime.UtcNow;
    public bool EnterEnabled { get; set; }
    public int MinutesLeft { get; set; } = -1;
    public bool TimeCountSent { get; set; }
    public int Stage { get; set; }
    public int CurMonster { get; set; }
    public int MaxMonster { get; set; }
    public DateTime BridgeOpenAt { get; set; } = DateTime.MinValue;

    /// <summary>The Archangel's weapon that is on the ground or in someone's bag: its serial, who holds it, its
    /// level (0-2) and the ground slot while it lies on the floor. -1 for none.</summary>
    public long EventItemSerial { get; set; } = -1;
    public int EventItemOwner { get; set; } = -1;
    public int EventItemLevel { get; set; } = -1;
    public int EventItemGroundIndex { get; set; } = -1;

    /// <summary>Who delivered the weapon to the Archangel: the winner. -1 while nobody has.</summary>
    public int WinnerIndex { get; set; } = -1;

    public List<BloodCastleUser> Users { get; } = new();
    public int GateIndex { get; set; } = -1;
    public int SaintStatueIndex { get; set; } = -1;
    public HashSet<int> MonsterIndices { get; } = new();

    /// <summary>Scores that are sent a few seconds after a player's death (BLOOD_CASTLE_DELAY_SCORE).</summary>
    public List<(DateTime At, int PlayerIndex, byte[] Packet)> DelayedScores { get; } = new();
}

/// <summary>
/// Port of CBloodCastle (BloodCastle.cpp). Each castle goes Blank -> Empty (the entrance opens before the start
/// time) -> Stand (the entered players wait) -> Start (kill the monsters, open the bridge, break the gate, kill
/// the Kundun's minions, break the statue, bring the Archangel's weapon to the Archangel) -> Clean (rewards) ->
/// Empty again. The castles are the Blood Castle maps (11-17); only the ones whose map exists on the server work,
/// and the package has Blood Castle 1's only.
/// <para>Not ported: the daily entry cap (needs the DataServer's per-account counter) and the PK restriction (no PK
/// system yet).</para>
/// </summary>
public sealed class BloodCastleManager
{
    private static readonly Random Rng = Random.Shared;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    // The zones the event opens and closes (BloodCastle.h:220-224).
    private static readonly (byte X, byte Y, byte TX, byte TY) EntranceZone = (13, 15, 15, 23);
    private static readonly (byte X, byte Y, byte TX, byte TY) BridgeZone = (13, 70, 15, 75);
    private static readonly (byte X, byte Y, byte TX, byte TY)[] GateZones = { (13, 76, 15, 79), (11, 80, 25, 89), (8, 80, 10, 83) };

    private const byte BlockedAttr = 4;  // wall
    private const byte BridgeAttr = 8;   // "no walk" of the bridge
    private const int GateClass = 131;
    private const int GateX = 14, GateY = 75;
    private const int ExitGate = 22; // Devias
    private static readonly int[] EntryGates = { 66, 67, 68, 69, 70, 71, 80, 271 };
    private static readonly int CloakItem = Item.GetItem(13, 18);       // Invisibility Cloak, its level is the castle
    private static readonly int ScrollItem = Item.GetItem(13, 47);      // Blood Castle scroll, any castle
    private static readonly int WeaponItem = Item.GetItem(13, 19);      // Absolute Weapon of Archangel
    private static readonly int[] MagicSkeletons = { 89, 95, 112, 118, 124, 130, 143 };

    private static long _serialCounter = 0x0BC00000;

    private readonly BloodCastleConfig _config;
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
    private readonly BloodCastleLevel[] _levels;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Gives experience that counts as an event reward (set by the protocol handler).</summary>
    public Func<PlayerObject, long, CancellationToken, Task>? GrantExperience { get; set; }

    /// <summary>Drops the item bag of an event around a player: (player, special value, level, state).</summary>
    public Func<PlayerObject, int, int, int, CancellationToken, Task>? GiveItemBag { get; set; }

    public BloodCastleManager(
        BloodCastleConfig config, EventEntryLevelTable entryLevels, EventStageSpawnTable stageSpawns,
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

        _levels = Enumerable.Range(0, BloodCastleConfig.MaxLevel).Select(level => new BloodCastleLevel
        {
            Level = level,
            Map = (byte)(11 + level), // MAP_BLOOD_CASTLE1 .. MAP_BLOOD_CASTLE7
            HasData = maps.GetMap(11 + level) != null,
        }).ToArray();
    }

    private static ServerInfoConfig ServerInfo => WorldPacketBuilder.ServerInfo;

    /// <summary>The clock the event runs on (tests move it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    public IReadOnlyList<BloodCastleLevel> Levels => _levels;

    // ---------------------------------------------------------------- start up and the tick

    /// <summary>Port of CBloodCastle::ForceStart: opens the castles in <paramref name="delaySeconds"/>.</summary>
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

        Log.Add(LogColor.Blue, "[BloodCastle] ForceStart -- next opening in ~{0}s", delaySeconds);
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

    /// <summary>Port of CBloodCastle::Init: every castle that has a map starts closed, waiting for its next start time.</summary>
    internal async Task InitializeAsync(CancellationToken ct)
    {
        if (!ServerInfo.BloodCastleEvent)
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

        Log.Add(LogColor.Blue, "[BloodCastle] {0} castle(s) with a map on this server", _levels.Count(l => l.HasData));
    }

    /// <summary>One second of the event for every castle (MainProc).</summary>
    internal async Task TickAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            foreach (var castle in _levels)
            {
                if (!castle.HasData || castle.State == BloodCastleState.Blank)
                {
                    continue;
                }

                try
                {
                    await TickAsync(castle, ct);
                }
                catch (Exception ex)
                {
                    Log.Add(LogColor.Red, "[BloodCastle] ({0}) Error in tick: {1}", castle.Level + 1, ex.Message);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of CBloodCastle::MainProc for one castle.</summary>
    private async Task TickAsync(BloodCastleLevel castle, CancellationToken ct)
    {
        int remain = (int)(castle.TargetTime - Now).TotalSeconds;

        switch (castle.State)
        {
            case BloodCastleState.Empty:
                await ProcEmptyAsync(castle, remain, ct);
                break;
            case BloodCastleState.Stand:
                await ProcStandAsync(castle, remain, ct);
                break;
            case BloodCastleState.Start:
                await ProcStartAsync(castle, remain, ct);
                break;
            case BloodCastleState.Clean:
                await ProcCleanAsync(castle, remain, ct);
                break;
        }

        await SendDelayedScoresAsync(castle, ct);
    }

    private async Task ProcEmptyAsync(BloodCastleLevel castle, int remain, CancellationToken ct)
    {
        if (remain > 0 && remain <= _config.WarningMinutes * 60)
        {
            await CheckUsersAsync(castle, ct);

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
                    await NoticeToAllOnlineAsync(Fmt(_messages.GetMessage(0), castle.MinutesLeft + 1), ct);
                }
            }

            castle.EnterEnabled = true;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToAllOnlineAsync(DevilSquarePacketBuilder.TimeCountSend(3), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            if (castle.Level == 0)
            {
                await NoticeToAllOnlineAsync(_messages.GetMessage(1), ct);
            }

            await NoticeToUsersAsync(castle, 1, Fmt(_messages.GetMessage(2), castle.Level + 1, _config.NotifyMinutes), ct);
            await SetStateStandAsync(castle);
        }
    }

    private async Task ProcStandAsync(BloodCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle, ct);

        if (castle.Users.Count == 0)
        {
            Log.Add(LogColor.Black, "[Blood Castle] ({0}) Not enough users", castle.Level + 1);
            await SetStateEmptyAsync(castle, ct);
            return;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(4), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await NoticeToUsersAsync(castle, 0, _messages.GetMessage(3), ct);
            await SetStateStartAsync(castle, ct);
        }
    }

    private async Task ProcStartAsync(BloodCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle, ct);

        if (castle.Users.Count == 0)
        {
            Log.Add(LogColor.Black, "[Blood Castle] ({0}) Not enough users", castle.Level + 1);
            await SetStateEmptyAsync(castle, ct);
            return;
        }

        switch (castle.Stage)
        {
            case 0: await SetStage0Async(castle, ct); break;
            case 1: await SetStage1Async(castle, ct); break;
            case 2: await SetStage2Async(castle, ct); break;
            case 3: await SetStage3Async(castle, ct); break;
        }

        await SendStateAsync(castle, castle.Stage is 0 or 1 or 2 ? (byte)1 : (byte)4, ct);

        if (castle.Stage == 4 && _players.TryGet(castle.WinnerIndex, out var winner))
        {
            await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(12), winner.Name), ct);
            await SetStateCleanAsync(castle, ct);
            return;
        }

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(5), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await NoticeToUsersAsync(castle, 0, _messages.GetMessage(6), ct);
            await SetStateCleanAsync(castle, ct);
        }
    }

    private async Task ProcCleanAsync(BloodCastleLevel castle, int remain, CancellationToken ct)
    {
        await CheckUsersAsync(castle, ct);

        if (remain > 0 && remain <= 30 && !castle.TimeCountSent)
        {
            await SendToUsersAsync(castle, DevilSquarePacketBuilder.TimeCountSend(6), ct);
            castle.TimeCountSent = true;
        }

        if (remain <= 0)
        {
            await SetStateEmptyAsync(castle, ct);
        }
    }

    // ---------------------------------------------------------------- the state changes (SetState_*)

    private async Task SetStateEmptyAsync(BloodCastleLevel castle, CancellationToken ct)
    {
        castle.State = BloodCastleState.Empty;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.Stage = 0;
        castle.CurMonster = 0;
        castle.MaxMonster = 0;
        castle.BridgeOpenAt = DateTime.MinValue;
        ResetEventItem(castle);
        castle.WinnerIndex = -1;

        await SendSimpleStateAsync(castle, 2, null, ct);
        await ClearUsersAsync(castle, ct);
        RemoveGateAndStatue(castle);
        ClearMonsters(castle);
        await SetZonesAsync(castle, new[] { EntranceZone }, BlockedAttr, set: true, ct);
        ApplyZones(castle, new[] { BridgeZone }, BridgeAttr, set: true); // only the server's map: the client has its own
        await SetZonesAsync(castle, GateZones, BlockedAttr, set: true, ct);
        CheckSync(castle);

        Log.Add(LogColor.Black, "[Blood Castle] ({0}) SetState EMPTY", castle.Level + 1);
    }

    private async Task SetStateStandAsync(BloodCastleLevel castle)
    {
        castle.State = BloodCastleState.Stand;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.TargetTime = Now.AddMinutes(_config.NotifyMinutes);

        Log.Add(LogColor.Black, "[Blood Castle] ({0}) SetState STAND", castle.Level + 1);
        await Task.CompletedTask;
    }

    private async Task SetStateStartAsync(BloodCastleLevel castle, CancellationToken ct)
    {
        castle.State = BloodCastleState.Start;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;
        castle.CurMonster = 0;
        castle.MaxMonster = castle.Users.Count * _config.MonstersPerUser;

        await SetZonesAsync(castle, new[] { EntranceZone }, BlockedAttr, set: false, ct);
        SetGate(castle);
        StageSpawn(castle, 0);
        await SendSimpleStateAsync(castle, 0, null, ct);
        castle.TargetTime = Now.AddMinutes(_config.EventMinutes);

        Log.Add(LogColor.Black, "[Blood Castle] ({0}) SetState START", castle.Level + 1);
    }

    private async Task SetStateCleanAsync(BloodCastleLevel castle, CancellationToken ct)
    {
        castle.State = BloodCastleState.Clean;
        castle.EnterEnabled = false;
        castle.MinutesLeft = -1;
        castle.TimeCountSent = false;

        if (_players.TryGet(castle.EventItemOwner, out var holder))
        {
            await DeleteEventItemAsync(castle, holder, ct);
        }

        RemoveGateAndStatue(castle);
        ClearMonsters(castle);
        _players.TryGet(castle.WinnerIndex, out var winner);

        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player))
            {
                continue;
            }

            bool won = winner != null && (player.Index == winner.Index || (winner.PartyNumber != -1 && winner.PartyNumber == player.PartyNumber));

            await GiveRewardExperienceAsync(castle, user, player, won, ct);
            await GiveRewardMoneyAsync(castle, user, player, won, ct);

            if (won)
            {
                GiveItem(castle, player, ct);
            }

            await player.Session.SendAsync(
                BloodCastlePacketBuilder.ScoreSend(won ? (byte)1 : (byte)0, player.Name, (uint)user.Score, (uint)user.RewardExperience, (uint)user.RewardMoney), ct);

            await _sendToDataServer(
                EventDataServerPacketBuilder.RankingScoreSave(EventDataServerPacketBuilder.HeadBloodCastle, (ushort)player.Index, player.Account, player.Name, (uint)user.Score), ct);
        }

        await SendSimpleStateAsync(castle, 2, null, ct);
        castle.TargetTime = Now.AddMinutes(_config.CloseMinutes);

        Log.Add(LogColor.Black, "[Blood Castle] ({0}) SetState CLEAN", castle.Level + 1);
    }

    /// <summary>Port of CheckSync: the next start time from the schedule.</summary>
    private void CheckSync(BloodCastleLevel castle)
    {
        var next = _config.Schedule.Count == 0 ? null : DevilSquareManager.ComputeNextOccurrence(_config.Schedule, Now);

        if (next == null)
        {
            castle.State = BloodCastleState.Blank;
            return;
        }

        castle.TargetTime = next.Value;
        Log.Add(LogColor.Black, "[Blood Castle] ({0}) Sync Start Time. [{1}] min remain", castle.Level + 1, (int)(next.Value - Now).TotalMinutes);
    }

    // ---------------------------------------------------------------- the stages

    private async Task SetStage0Async(BloodCastleLevel castle, CancellationToken ct)
    {
        if (castle.CurMonster < castle.MaxMonster)
        {
            return;
        }

        castle.Stage = 1;
        castle.BridgeOpenAt = Now;
        await SendSimpleStateAsync(castle, 3, null, ct);
        ApplyZones(castle, new[] { BridgeZone }, BridgeAttr, set: false); // server first; the clients are told 3 s later
        StageSpawn(castle, 1);
        await NoticeToUsersAsync(castle, 0, _messages.GetMessage(4), ct);
    }

    private async Task SetStage1Async(BloodCastleLevel castle, CancellationToken ct)
    {
        if ((Now - castle.BridgeOpenAt).TotalMilliseconds < 3000)
        {
            return;
        }

        castle.Stage = 2;
        castle.BridgeOpenAt = DateTime.MinValue;
        await NotifyZonesAsync(castle, new[] { BridgeZone }, BridgeAttr, set: false, ct);

        if (_monsters.TryGet(castle.GateIndex, out var gate))
        {
            gate.Immune = false;
        }

        StageSpawn(castle, 2);
        await Task.CompletedTask;
    }

    private async Task SetStage2Async(BloodCastleLevel castle, CancellationToken ct)
    {
        if (castle.GateIndex != -1)
        {
            return; // the gate still stands
        }

        castle.Stage = 3;
        castle.CurMonster = 0;
        castle.MaxMonster = castle.Users.Count * 2;
        await SetZonesAsync(castle, GateZones, BlockedAttr, set: false, ct);
        StageSpawn(castle, 3);
    }

    private async Task SetStage3Async(BloodCastleLevel castle, CancellationToken ct)
    {
        if (castle.CurMonster < castle.MaxMonster)
        {
            return;
        }

        castle.Stage = 4;
        SetSaintStatue(castle);
        await NoticeToUsersAsync(castle, 0, _messages.GetMessage(5), ct);

        if (_monsters.TryGet(castle.SaintStatueIndex, out var statue))
        {
            statue.Immune = false;
        }

        StageSpawn(castle, 4);
    }

    // ---------------------------------------------------------------- monsters, gate and statue

    /// <summary>Port of StageSpawn/SetMonster: the monsters the stage adds, at the Type 4 positions of the map.</summary>
    private void StageSpawn(BloodCastleLevel castle, int stage)
    {
        int spawned = 0;

        foreach (var row in _stageSpawns.GetBloodCastleEntries(castle.Level, stage))
        {
            foreach (var position in _spawnPool.Where(e => e.Map == castle.Map && e.MonsterClass == row.MonsterClass))
            {
                var monster = _monsters.SpawnOne(row.MonsterClass, position, _monsterInfo, _maps);

                if (monster == null)
                {
                    continue;
                }

                monster.BloodCastleLevel = castle.Level;

                if (row.MaxRegenMs != -1)
                {
                    monster.MaxRegenMillis = row.MaxRegenMs;
                }
                else
                {
                    monster.NoRespawn = true; // PosNum = -1: does not come back
                }

                castle.MonsterIndices.Add(monster.Index);
                spawned++;
            }
        }

        Log.Add(LogColor.Black, "[Blood Castle] ({0}) Stage {1}: {2} monster(s) added", castle.Level + 1, stage, spawned);
    }

    private Monster? SpawnFixed(BloodCastleLevel castle, int monsterClass, int x, int y, int life)
    {
        var entry = new MonsterSpawnEntry { Type = 4, MonsterClass = monsterClass, Map = castle.Map, X = x, Y = y, Dir = 1 };
        var monster = _monsters.SpawnOne(monsterClass, entry, _monsterInfo, _maps);

        if (monster == null)
        {
            return null;
        }

        monster.MaxLife = life;
        monster.Life = life;
        monster.Immune = true;   // until its stage
        monster.NoRespawn = true;
        monster.BloodCastleLevel = castle.Level;
        castle.MonsterIndices.Add(monster.Index); // so the castle's closing sweeps it away even when it was killed
        return monster;
    }

    private void SetGate(BloodCastleLevel castle)
    {
        if (castle.GateIndex != -1)
        {
            return;
        }

        castle.GateIndex = SpawnFixed(castle, GateClass, GateX, GateY, _config.NpcLife[castle.Level][0])?.Index ?? -1;
    }

    private void SetSaintStatue(BloodCastleLevel castle)
    {
        if (castle.SaintStatueIndex != -1)
        {
            return;
        }

        int statueClass = 132 + Rng.Next(3);
        castle.SaintStatueIndex = SpawnFixed(castle, statueClass, _config.SaintStatue[castle.Level][0], _config.SaintStatue[castle.Level][1], _config.NpcLife[castle.Level][1])?.Index ?? -1;
    }

    private void RemoveGateAndStatue(BloodCastleLevel castle)
    {
        if (castle.GateIndex != -1)
        {
            _monsters.Remove(castle.GateIndex);
            castle.GateIndex = -1;
        }

        if (castle.SaintStatueIndex != -1)
        {
            _monsters.Remove(castle.SaintStatueIndex);
            castle.SaintStatueIndex = -1;
        }
    }

    private void ClearMonsters(BloodCastleLevel castle)
    {
        foreach (int index in castle.MonsterIndices)
        {
            _monsters.Remove(index);
        }

        castle.MonsterIndices.Clear();
    }

    // ---------------------------------------------------------------- map zones

    /// <summary>Changes the attribute bits of the zones on the SERVER's map (CMap::SetAttr / DelAttr).</summary>
    private void ApplyZones(BloodCastleLevel castle, (byte X, byte Y, byte TX, byte TY)[] zones, byte attr, bool set)
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

    /// <summary>Tells the players inside to change the zones on THEIR copy of the map (GCMapAttrSend: flag 0 sets,
    /// 1 clears).</summary>
    private async Task NotifyZonesAsync(BloodCastleLevel castle, (byte X, byte Y, byte TX, byte TY)[] zones, byte attr, bool set, CancellationToken ct)
    {
        await SendToUsersAsync(castle, BloodCastlePacketBuilder.MapAttrSend(0, attr, set ? (byte)0 : (byte)1, zones), ct);
    }

    /// <summary>The entrance and gate zones change on both the server's map and the clients'.</summary>
    private async Task SetZonesAsync(BloodCastleLevel castle, (byte X, byte Y, byte TX, byte TY)[] zones, byte attr, bool set, CancellationToken ct)
    {
        ApplyZones(castle, zones, attr, set);
        await NotifyZonesAsync(castle, zones, attr, set, ct);
    }

    // ---------------------------------------------------------------- users

    private BloodCastleUser? GetUser(BloodCastleLevel castle, int playerIndex) =>
        castle.Users.FirstOrDefault(u => u.PlayerIndex == playerIndex);

    private BloodCastleLevel? LevelOfMap(int map) =>
        map is >= 11 and <= 17 ? _levels[map - 11] : null;

    /// <summary>Port of CheckUser: drops whoever disconnected or left the castle's map.</summary>
    private async Task CheckUsersAsync(BloodCastleLevel castle, CancellationToken ct)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (!_players.TryGet(user.PlayerIndex, out var player) || player.Map != castle.Map)
            {
                await RemoveUserAsync(castle, user, ct);
            }
        }
    }

    /// <summary>Port of DelUser: who leaves during the fight drops the weapon they carry; otherwise it is deleted.</summary>
    private async Task RemoveUserAsync(BloodCastleLevel castle, BloodCastleUser user, CancellationToken ct)
    {
        _players.TryGet(user.PlayerIndex, out var player);

        if (player != null)
        {
            if (castle.State == BloodCastleState.Start)
            {
                await DropEventItemOfAsync(castle, player, ct);
                await SendSimpleStateAsync(castle, 2, player, ct);
            }
            else
            {
                await DeleteEventItemAsync(castle, player, ct);
            }
        }

        castle.Users.Remove(user);
    }

    private async Task ClearUsersAsync(BloodCastleLevel castle, CancellationToken ct)
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
        player.VisibleTo.Clear();
        player.VisibleMonsters.Clear();

        await player.Session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(0, player.Map, player.X, player.Y, player.Dir), ct);
    }

    // ---------------------------------------------------------------- rewards

    private async Task GiveRewardExperienceAsync(BloodCastleLevel castle, BloodCastleUser user, PlayerObject player, bool won, CancellationToken ct)
    {
        long experience = _config.Experience[castle.Level][won ? 0 : 1];
        user.Score = (int)(experience / 5);
        user.RewardExperience = (int)Math.Min(experience, int.MaxValue);

        if (user.RewardExperience > 0 && GrantExperience != null)
        {
            await GrantExperience(player, user.RewardExperience, ct);
        }
    }

    private async Task GiveRewardMoneyAsync(BloodCastleLevel castle, BloodCastleUser user, PlayerObject player, bool won, CancellationToken ct)
    {
        user.RewardMoney = _config.Money[castle.Level][won ? 0 : 1];

        const uint maxMoney = 2_000_000_000;
        uint money = (uint)Math.Max(user.RewardMoney, 0);
        player.Money = money > maxMoney - player.Money ? maxMoney : player.Money + money;
        await player.Session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
    }

    private void GiveItem(BloodCastleLevel castle, PlayerObject player, CancellationToken ct)
    {
        if (GiveItemBag != null)
        {
            _ = GiveItemBag(player, ItemBagManager.SpecialBloodCastle, castle.Level, 1, ct);
        }
    }

    // ---------------------------------------------------------------- the Archangel's weapon

    private static bool IsEventItem(BloodCastleLevel castle, Item item) =>
        castle.EventItemSerial != -1 && item.Index == WeaponItem && item.Serial == castle.EventItemSerial;

    private static void ResetEventItem(BloodCastleLevel castle)
    {
        castle.EventItemSerial = -1;
        castle.EventItemOwner = -1;
        castle.EventItemLevel = -1;
        castle.EventItemGroundIndex = -1;
    }

    private int FindEventItemSlot(BloodCastleLevel castle, PlayerObject player)
    {
        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            var item = player.Items[slot];

            if (item.IsItem() && item.Index == WeaponItem && item.Level == castle.EventItemLevel && IsEventItem(castle, item))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Port of SearchUserDropEventItem: the holder's weapon falls to the floor where they stand.</summary>
    private async Task DropEventItemOfAsync(BloodCastleLevel castle, PlayerObject player, CancellationToken ct)
    {
        if (castle.EventItemOwner != player.Index)
        {
            return;
        }

        int slot = FindEventItemSlot(castle, player);

        if (slot < 0)
        {
            return;
        }

        var item = player.Items[slot];
        var ground = _groundItems.Drop(player.Map, item, player.X, player.Y, player.Index, null,
            TimeSpan.FromSeconds(Math.Max((castle.TargetTime - Now).TotalSeconds, 1)), TimeSpan.Zero);

        if (ground == null)
        {
            return;
        }

        player.SetItem(slot, Item.Empty());
        await player.Session.SendAsync(ItemPacketBuilder.ItemDropSend(1, (byte)slot), ct);

        castle.EventItemOwner = -1;
        castle.EventItemGroundIndex = ground.Index;
        await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(8), player.Name), ct);
    }

    /// <summary>Port of SearchUserDeleteEventItem: the weapon is taken out of the holder's inventory.</summary>
    private async Task DeleteEventItemAsync(BloodCastleLevel castle, PlayerObject player, CancellationToken ct)
    {
        if (castle.EventItemOwner != player.Index)
        {
            return;
        }

        int slot = FindEventItemSlot(castle, player);

        if (slot < 0)
        {
            return;
        }

        player.SetItem(slot, Item.Empty());
        await player.Session.SendAsync(ItemPacketBuilder.ItemDeleteSend((byte)slot, 1), ct);
        ResetEventItem(castle);
    }

    /// <summary>The handler calls this after a player picks an item up (CheckEventItemSerial / GetEventItem).</summary>
    public async Task OnItemPickedUpAsync(PlayerObject player, Item item, CancellationToken ct)
    {
        var castle = LevelOfMap(player.Map);

        if (castle == null || !castle.HasData)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (!IsEventItem(castle, item) || GetUser(castle, player.Index) == null)
            {
                return;
            }

            castle.EventItemGroundIndex = -1;
            castle.EventItemOwner = player.Index;
            castle.EventItemLevel = item.Level;
            await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(9), player.Name), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The handler calls this after a player throws an item on the floor of a castle (Map.cpp:336).</summary>
    public async Task OnItemDroppedAsync(PlayerObject player, Item item, GroundItem ground, CancellationToken ct)
    {
        var castle = LevelOfMap(player.Map);

        if (castle == null || !castle.HasData)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (!IsEventItem(castle, item))
            {
                return;
            }

            ground.ExpireAt = Now.AddSeconds(Math.Max((castle.TargetTime - Now).TotalSeconds, 1));
            ground.LootLockUntil = Now;
            castle.EventItemGroundIndex = ground.Index;
            castle.EventItemOwner = -1;
            castle.EventItemLevel = -1;
            await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(8), player.Name), ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------------------------------------------------------- the NPCs

    /// <summary>Port of NpcAngelKing: the Archangel takes the weapon from whoever brings it, and that player wins.
    /// The client is told what happened with a stock message (server command 1).</summary>
    public async Task HandleAngelKingAsync(PlayerObject player, CancellationToken ct)
    {
        if (!ServerInfo.BloodCastleEvent)
        {
            return;
        }

        var castle = LevelOfMap(player.Map);

        if (castle == null || !castle.HasData)
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (castle.WinnerIndex != -1)
            {
                await SendCommandAsync(player, 46, ct);
                return;
            }

            if (castle.State != BloodCastleState.Start || GetUser(castle, player.Index) == null || castle.EventItemOwner != player.Index)
            {
                await SendCommandAsync(player, 24, ct);
                return;
            }

            int slot = FindEventItemSlot(castle, player);

            if (slot < 0)
            {
                await SendCommandAsync(player, 24, ct);
                return;
            }

            player.SetItem(slot, Item.Empty());
            await player.Session.SendAsync(ItemPacketBuilder.ItemDeleteSend((byte)slot, 1), ct);

            ResetEventItem(castle);
            castle.WinnerIndex = player.Index;
            await SendCommandAsync(player, 23, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of NpcAngelMessenger: opens the entry window (NPC talk result 6) for a player whose level
    /// fits a castle, who has the cloak, while the entrance is open. True when the window was opened.</summary>
    public async Task<bool> HandleMessengerAsync(PlayerObject player, CancellationToken ct)
    {
        if (!ServerInfo.BloodCastleEvent)
        {
            return false;
        }

        int level = _entryLevels.GetBloodCastleLevel(player.Level, IsSpecialClass(player));

        if (level is < 0 or >= BloodCastleConfig.MaxLevel)
        {
            await player.Session.SendAsync(ChatPacketBuilder.NoticeSend(_messages.GetMessage(7)), ct);
            return false;
        }

        bool hasCloak = Enumerable.Range(0, Item.InventorySize).Any(slot =>
            player.Items[slot].IsItem() && player.Items[slot].Index == CloakItem && player.Items[slot].Level == level + 1);

        if (!hasCloak && !ServerInfo.BloodCastleNpcWithoutEntrance)
        {
            await SendCommandAsync(player, 21, ct);
            return false;
        }

        if (!_levels[level].EnterEnabled)
        {
            await SendCommandAsync(player, 20, ct);
            return false;
        }

        await player.Session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(6), ct);
        return true;
    }

    private static bool IsSpecialClass(PlayerObject player) => player.Class is 3 or 4; // MG, DL

    private static Task SendCommandAsync(PlayerObject player, byte code, CancellationToken ct) =>
        player.Session.SendAsync(BloodCastlePacketBuilder.ServerCommandSend(1, code, 0), ct);

    // ---------------------------------------------------------------- entering (CGBloodCastleEnterRecv)

    /// <summary>Port of CGBloodCastleEnterRecv. <paramref name="level"/> is the castle's number as the client counts
    /// it (1-7).</summary>
    public async Task HandleEnterAsync(ClientSession session, PlayerObject player, int level, int rawSlot, CancellationToken ct)
    {
        if (!ServerInfo.BloodCastleEvent)
        {
            return;
        }

        level--;

        if (level < 0 || level >= BloodCastleConfig.MaxLevel || !_levels[level].HasData)
        {
            await session.SendAsync(BloodCastlePacketBuilder.EnterSend(1), ct);
            return;
        }

        var castle = _levels[level];

        if (rawSlot < 0 || rawSlot >= Item.InventorySize)
        {
            await session.SendAsync(BloodCastlePacketBuilder.EnterSend(1), ct);
            return;
        }

        var ticket = player.Items[rawSlot];

        if (!ticket.IsItem() || (ticket.Index != CloakItem && ticket.Index != ScrollItem)
            || (ticket.Index == CloakItem && ticket.Level != level + 1))
        {
            await session.SendAsync(BloodCastlePacketBuilder.EnterSend(1), ct);
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (!castle.EnterEnabled)
            {
                await session.SendAsync(BloodCastlePacketBuilder.EnterSend(2), ct);
                return;
            }

            int allowed = _entryLevels.GetBloodCastleLevel(player.Level, IsSpecialClass(player));

            if (allowed > level)
            {
                await session.SendAsync(BloodCastlePacketBuilder.EnterSend(3), ct);
                return;
            }

            if (allowed < level)
            {
                await session.SendAsync(BloodCastlePacketBuilder.EnterSend(4), ct);
                return;
            }

            if (castle.Users.Count >= ServerInfo.BloodCastleMaxUser || GetUser(castle, player.Index) != null)
            {
                await session.SendAsync(BloodCastlePacketBuilder.EnterSend(5), ct);
                return;
            }

            castle.Users.Add(new BloodCastleUser { PlayerIndex = player.Index });

            // The cloak or scroll is used up (one point of durability, like the Devil Square's ticket).
            byte durability = (byte)Math.Max(ticket.Durability - 1, 0);
            var left = durability == 0 ? Item.Empty() : new Item
            {
                Index = ticket.Index, Level = ticket.Level, Durability = durability, Serial = ticket.Serial,
                Option1 = ticket.Option1, Option2 = ticket.Option2, Option3 = ticket.Option3, NewOption = ticket.NewOption, SetOption = ticket.SetOption,
            };
            player.SetItem(rawSlot, left);
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, (byte)rawSlot, left), ct);

            await session.SendAsync(BloodCastlePacketBuilder.EnterSend(0), ct);
            await MoveToGateAsync(player, EntryGates[level], ct);

            Log.Add(LogColor.Blue, "[Blood Castle] ({0}) '{1}' entered ({2} player(s))", level + 1, player.Name, castle.Users.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The Blood Castle part of CGEventRemainTimeRecv (event type 2).</summary>
    public async Task HandleRemainTimeQueryAsync(ClientSession session, PlayerObject player, CancellationToken ct)
    {
        int level = _entryLevels.GetBloodCastleLevel(player.Level, IsSpecialClass(player));

        if (level is < 0 or >= BloodCastleConfig.MaxLevel)
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(_messages.GetMessage(7)), ct);
            return;
        }

        var castle = _levels[level];
        byte remainMinutes = 0, entered = 0;

        if (castle.State == BloodCastleState.Empty && castle.EnterEnabled)
        {
            entered = (byte)Math.Min(castle.Users.Count, 255);
        }
        else
        {
            double minutes = Math.Max((castle.TargetTime - Now).TotalMinutes, 0);
            remainMinutes = (byte)Math.Min(Math.Ceiling(minutes), 255);
        }

        await session.SendAsync(DevilSquarePacketBuilder.RemainTimeSend(2, remainMinutes, entered), ct);
    }

    // ---------------------------------------------------------------- deaths

    /// <summary>Port of UserDieProc: a player who dies in the fight is out, with the reward so far.</summary>
    public async Task OnPlayerDiedAsync(PlayerObject player, CancellationToken ct)
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

            if (castle.State != BloodCastleState.Start || user == null)
            {
                return;
            }

            await GiveRewardExperienceAsync(castle, user, player, won: false, ct);
            await GiveRewardMoneyAsync(castle, user, player, won: false, ct);

            castle.DelayedScores.Add((Now.AddSeconds(8), player.Index,
                BloodCastlePacketBuilder.ScoreSend(0, player.Name, (uint)user.Score, (uint)user.RewardExperience, (uint)user.RewardMoney)));

            await RemoveUserAsync(castle, user, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The handler calls this when a player leaves the game: whatever they carried from the castle is
    /// dealt with and they stop counting.</summary>
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
            var user = GetUser(castle, player.Index);

            if (user != null)
            {
                await RemoveUserAsync(castle, user, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of MonsterDieProc: the gate and the statue fall, and the monsters of the stage count for the
    /// player who did the most damage.</summary>
    public async Task OnMonsterKilledAsync(Monster monster, CancellationToken ct)
    {
        if (monster.BloodCastleLevel is not int level)
        {
            return;
        }

        var castle = _levels[level];

        await _lock.WaitAsync(ct);

        try
        {
            if (castle.State != BloodCastleState.Start)
            {
                return;
            }

            int topIndex = -1, topDamage = -1;

            foreach (var (attackerIndex, damage) in monster.DamageByAttacker)
            {
                if (damage > topDamage)
                {
                    topDamage = damage;
                    topIndex = attackerIndex;
                }
            }

            if (!_players.TryGet(topIndex, out var target))
            {
                return;
            }

            if (monster.MonsterClass == GateClass && monster.Index == castle.GateIndex && castle.Stage == 2)
            {
                castle.GateIndex = -1;
                await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(10), target.Name), ct);
                return;
            }

            if (monster.MonsterClass is >= 132 and <= 134 && monster.Index == castle.SaintStatueIndex && castle.Stage == 4)
            {
                DropWeapon(castle, monster, target);
                castle.SaintStatueIndex = -1;
                await NoticeToUsersAsync(castle, 0, Fmt(_messages.GetMessage(11), target.Name), ct);
                StageSpawn(castle, 5);
                return;
            }

            if (!castle.MonsterIndices.Contains(monster.Index) || GetUser(castle, target.Index) == null)
            {
                return;
            }

            if (MagicSkeletons.Contains(monster.MonsterClass))
            {
                if (castle.Stage == 3 && castle.CurMonster < castle.MaxMonster)
                {
                    castle.CurMonster++;
                }
            }
            else if (castle.Stage == 0 && castle.CurMonster < castle.MaxMonster)
            {
                castle.CurMonster++;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The statue's death leaves the Archangel's weapon, of the level given by the statue, on the floor.</summary>
    private void DropWeapon(BloodCastleLevel castle, Monster statue, PlayerObject finder)
    {
        long serial = Interlocked.Increment(ref _serialCounter);
        var weapon = new Item
        {
            Index = (short)WeaponItem,
            Level = (byte)(statue.MonsterClass - 132),
            Durability = 1,
            Serial = (uint)serial,
        };

        var ground = _groundItems.Drop(statue.Map, weapon, statue.X, statue.Y, finder.Index, null,
            TimeSpan.FromSeconds(Math.Max((castle.TargetTime - Now).TotalSeconds, 1)), TimeSpan.Zero);

        if (ground == null)
        {
            return;
        }

        castle.EventItemSerial = serial;
        castle.EventItemGroundIndex = ground.Index;
        castle.EventItemOwner = -1;
        castle.EventItemLevel = weapon.Level;
    }

    // ---------------------------------------------------------------- packets and notices

    private async Task SendStateAsync(BloodCastleLevel castle, byte state, CancellationToken ct)
    {
        var packet = BloodCastlePacketBuilder.StateSend(
            state, (ushort)Math.Max((castle.TargetTime - Now).TotalSeconds, 0), (ushort)castle.MaxMonster, (ushort)castle.CurMonster,
            (ushort)Math.Max(castle.EventItemOwner, 0), castle.EventItemLevel is -1 or 0xFF ? (byte)0xFF : (byte)(castle.EventItemLevel + 1));
        await SendToUsersAsync(castle, packet, ct);
    }

    private async Task SendSimpleStateAsync(BloodCastleLevel castle, byte state, PlayerObject? only, CancellationToken ct)
    {
        var packet = BloodCastlePacketBuilder.StateSend(state, 0, 0, 0, 0, 0xFF);

        if (only != null)
        {
            await only.Session.SendAsync(packet, ct);
        }
        else
        {
            await SendToUsersAsync(castle, packet, ct);
        }
    }

    private async Task SendDelayedScoresAsync(BloodCastleLevel castle, CancellationToken ct)
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

    private async Task SendToUsersAsync(BloodCastleLevel castle, byte[] packet, CancellationToken ct)
    {
        foreach (var user in castle.Users.ToList())
        {
            if (_players.TryGet(user.PlayerIndex, out var player))
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    private async Task NoticeToUsersAsync(BloodCastleLevel castle, byte type, string text, CancellationToken ct) =>
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

    /// <summary>The messages of Message.txt are C format strings ("%d", "%s"): fills them in order.</summary>
    internal static string Fmt(string template, params object[] args)
    {
        int next = 0;
        var result = new System.Text.StringBuilder();

        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] == '%' && i + 1 < template.Length && template[i + 1] is 'd' or 's' && next < args.Length)
            {
                result.Append(args[next++]);
                i++;
            }
            else
            {
                result.Append(template[i]);
            }
        }

        return result.ToString();
    }
}

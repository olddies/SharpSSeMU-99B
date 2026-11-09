using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>One gate to Kalima (the monster the Lost Map makes) and who it belongs to.</summary>
public sealed class KalimaGate
{
    public required int OwnerIndex { get; init; }
    public required int MonsterIndex { get; init; }

    /// <summary>0-based Kalima this gate leads to (the gate class is 152 + level).</summary>
    public required int Level { get; init; }

    public required DateTime CreatedAt { get; init; }
    public int EnterCount { get; set; }
}

/// <summary>
/// Port of CKalima (Kalima.cpp). Throwing a Lost Map on the ground in a field map opens a gate there that takes its
/// owner, and the party members who stand next to it, to Kalima. The gate closes after five entries, after three
/// minutes, or when its owner dies or leaves. Killing the boss of a Kalima opens the gate to the next one for whoever
/// did the most damage. Data/Event/Kalima.dat says which Kalimas have that second gate.
/// <para>The package only has the map of Kalima 1.</para>
/// </summary>
public sealed class KalimaManager
{
    public const int MaxEntries = 5;
    public static readonly TimeSpan GateLife = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MinimumAge = TimeSpan.FromSeconds(2);

    public const int LostMapLevels = 7;

    private static readonly int[] KalimaGates = { 88, 89, 90, 91, 92, 93, 116 };
    private static readonly Random Rng = Random.Shared;

    /// <summary>The Kalima bosses and the (1-based) Kalima whose gate they open on dying.</summary>
    private static readonly Dictionary<int, int> BossLevels = new() { [161] = 1, [181] = 2, [189] = 3, [197] = 4, [267] = 5, [338] = 6 };

    private const int IcarusMap = 10;

    private readonly bool[] _nextPortal;
    private readonly MonsterRegistry _monsters;
    private readonly MonsterInfoTable _monsterInfo;
    private readonly MapRegistry _maps;
    private readonly PlayerRegistry _players;
    private readonly GateTable _gates;
    private readonly Dictionary<int, KalimaGate> _byOwner = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    public KalimaManager(
        bool[] nextPortal, MonsterRegistry monsters, MonsterInfoTable monsterInfo, MapRegistry maps, PlayerRegistry players, GateTable gates)
    {
        _nextPortal = nextPortal;
        _monsters = monsters;
        _monsterInfo = monsterInfo;
        _maps = maps;
        _players = players;
        _gates = gates;
    }

    public IReadOnlyCollection<KalimaGate> Gates => _byOwner.Values;

    /// <summary>Port of CKalima::Load: Data/Event/Kalima.dat is "level value" per row; a value of 1 lets killing that
    /// Kalima's boss open the gate to the next. Rows outside 0-5 are ignored.</summary>
    public static bool[] LoadNextPortals(string path)
    {
        var enabled = new bool[6];
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[Kalima] {0}", script.GetLastError());
            return enabled;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int level = script.GetNumber();
            int value = script.GetAsNumber();

            if (level is >= 0 and < 6)
            {
                enabled[level] = value != 0;
            }
        }

        Log.Add(LogColor.Blue, "[Kalima] Kalima.dat loaded: {0} Kalima(s) with a gate to the next", enabled.Count(e => e));
        return enabled;
    }

    public void Start(CancellationToken ct)
    {
        _ = RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TickAsync(ct);
        }
    }

    // ---------------------------------------------------------------- opening a gate

    /// <summary>Port of CKalima::CreateKalimaPortal: the player threw a Lost Map of <paramref name="itemLevel"/>
    /// (1-7) at (x, y). False when the gate cannot be opened there, and the map is kept.</summary>
    public async Task<bool> TryCreatePortalAsync(PlayerObject player, int itemLevel, int x, int y, CancellationToken ct)
    {
        int level = itemLevel - 1;

        if (level < 0 || level >= LostMapLevels)
        {
            return false;
        }

        // Not in Icarus, Kalima, Devil Square, Blood Castle or Chaos Castle; not in a safe zone.
        int map = player.Map;

        if (map == IcarusMap || map is >= 24 and <= 29 || map is 9 or 32 || map is >= 11 and <= 17 || map is >= 18 and <= 23)
        {
            return false;
        }

        var gameMap = _maps.GetMap(map);

        if (gameMap == null || gameMap.IsSafeZone(player.X, player.Y))
        {
            return false;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (_byOwner.ContainsKey(player.Index))
            {
                return false;
            }

            return CreateGate(player, level, map, x, y) != null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of CKalima::CreateKalimaNextPortal: a Kalima's boss died, and the gate to the next one opens
    /// where it stood for the player who did the most damage.</summary>
    public async Task OnMonsterKilledAsync(Monster monster, CancellationToken ct)
    {
        if (!BossLevels.TryGetValue(monster.MonsterClass, out int level))
        {
            return;
        }

        int topIndex = -1, topDamage = -1;

        foreach (var (attacker, damage) in monster.DamageByAttacker)
        {
            if (damage > topDamage)
            {
                topDamage = damage;
                topIndex = attacker;
            }
        }

        if (!_players.TryGet(topIndex, out var player) || !_nextPortal[level - 1])
        {
            return;
        }

        await _lock.WaitAsync(ct);

        try
        {
            if (!_byOwner.ContainsKey(player.Index))
            {
                CreateGate(player, level, monster.Map, monster.X, monster.Y);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private KalimaGate? CreateGate(PlayerObject owner, int level, int map, int x, int y)
    {
        var gameMap = _maps.GetMap(map);

        if (gameMap == null)
        {
            return null;
        }

        int px = x, py = y;

        // A spot that is not free is replaced by a free one near it (gObjGetRandomFreeLocation(3, 3, 50)).
        if (gameMap.GetAttr(px, py) != 0 && !TryFreeSpot(gameMap, ref px, ref py))
        {
            return null;
        }

        var entry = new MonsterSpawnEntry { Type = 4, MonsterClass = 152 + level, Map = map, X = px, Y = py, Dir = 1 };
        var gate = _monsters.SpawnOne(152 + level, entry, _monsterInfo, _maps);

        if (gate == null)
        {
            return null; // the package has no monster for that gate
        }

        gate.NoRespawn = true;
        gate.Immune = true;
        gate.MaxRegenMillis = 0;

        var created = new KalimaGate { OwnerIndex = owner.Index, MonsterIndex = gate.Index, Level = level, CreatedAt = Now };
        _byOwner[owner.Index] = created;

        Log.Add(LogColor.Black, "[Kalima] '{0}' opened a gate to Kalima {1} at X={2} Y={3}", owner.Name, level + 1, px, py);
        return created;
    }

    private static bool TryFreeSpot(GameMap map, ref int x, ref int y)
    {
        int originX = x, originY = y;

        for (int n = 0; n < 50; n++)
        {
            int tx = (Rng.Next(4) * (Rng.Next(2) == 0 ? -1 : 1)) + originX;
            int ty = (Rng.Next(4) * (Rng.Next(2) == 0 ? -1 : 1)) + originY;

            if (map.GetAttr(tx, ty) == 0)
            {
                x = tx;
                y = ty;
                return true;
            }
        }

        x = originX;
        y = originY;
        return false;
    }

    // ---------------------------------------------------------------- the life of a gate (CKalima::MainProc)

    internal async Task TickAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            foreach (var gate in _byOwner.Values.ToList())
            {
                await TickGateAsync(gate, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task TickGateAsync(KalimaGate gate, CancellationToken ct)
    {
        if (!_monsters.TryGet(gate.MonsterIndex, out var monster))
        {
            _byOwner.Remove(gate.OwnerIndex);
            return;
        }

        if (!_players.TryGet(gate.OwnerIndex, out var owner) || !owner.WorldEntered)
        {
            Delete(gate);
            return;
        }

        var age = Now - gate.CreatedAt;

        if (age < MinimumAge)
        {
            return;
        }

        if (owner.IsDying || gate.EnterCount >= MaxEntries || age >= GateLife)
        {
            Delete(gate);
            return;
        }

        // Whoever stands within two tiles of the gate and belongs to its owner or the owner's party goes in.
        int gateNumber = KalimaGates[Math.Clamp(gate.Level, 0, KalimaGates.Length - 1)];

        foreach (var player in _players.All.ToList())
        {
            if (!player.WorldEntered || player.IsDying || player.Map != monster.Map
                || Math.Abs(player.X - monster.X) > 2 || Math.Abs(player.Y - monster.Y) > 2)
            {
                continue;
            }

            bool member = player.Index == owner.Index || (owner.PartyNumber != -1 && owner.PartyNumber == player.PartyNumber);

            if (!member)
            {
                continue;
            }

            await MoveToGateAsync(player, gateNumber, ct);

            if (gate.EnterCount++ >= MaxEntries)
            {
                return;
            }
        }
    }

    private void Delete(KalimaGate gate)
    {
        _monsters.Remove(gate.MonsterIndex);
        _byOwner.Remove(gate.OwnerIndex);
    }

    /// <summary>Port of gObjMoveGate: to a random spot of the gate's box, telling the client.</summary>
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
}

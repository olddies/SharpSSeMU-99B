using MuServer.GameServer.Protocol;

namespace MuServer.GameServer.World;

/// <summary>
/// Port of CDarkSpirit (DarkSpirit.cpp), the Dark Raven of a Dark Lord. The master gives it a command (C1:A7): stay quiet,
/// attack at random whatever is near, attack what the master attacks, or attack one target. Every second or so
/// (<see cref="DarkSpiritRules.AttackInterval"/>) it picks a target, everybody around is told (C1:A8), and 600 ms later
/// the blow lands: the damage the raven's level and its master's command give, against half the target's defense, wearing
/// the raven down. A blow in the middle of a crowd (30% of them) spreads to up to ten around the target at 60%.
/// <para>The manager decides who, when and how much; what a hit does to a monster or a player (the death, the loot, the
/// self-defense) belongs to the protocol handler and comes in through the delegates.</para>
/// </summary>
public sealed class DarkSpiritManager
{
    private static readonly TimeSpan BlowDelay = TimeSpan.FromMilliseconds(600);

    private readonly PlayerRegistry _players;
    private readonly MonsterRegistry _monsters;
    private readonly ItemBalanceTable _items;
    private readonly Func<DarkSpiritRules> _rules;
    private readonly Random _rng;
    private readonly List<PendingBlow> _pending = new();
    private readonly object _pendingLock = new();

    private readonly record struct PendingBlow(DateTime Due, int Master, int Target, bool Send, byte Action);

    public DarkSpiritManager(PlayerRegistry players, MonsterRegistry monsters, ItemBalanceTable items, Random rng, Func<DarkSpiritRules>? rules = null)
    {
        _players = players;
        _monsters = monsters;
        _items = items;
        _rng = rng;
        _rules = rules ?? (() => DarkSpiritRules.Current);
    }

    /// <summary>The clock (tests move it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    /// <summary>Whether a tile is a safe zone: the raven does not fight from one nor at one.</summary>
    public Func<int, int, int, bool> IsSafeZone { get; set; } = (_, _, _) => false;

    /// <summary>Whether the master may attack that player (the PvP rules: CheckPlayerTarget).</summary>
    public Func<PlayerObject, PlayerObject, bool> CanAttackPlayer { get; set; } = (_, _) => false;

    /// <summary>Whether the two are in a duel with each other, and whether they are inside the Chaos Castle.</summary>
    public Func<PlayerObject, PlayerObject, bool> InDuel { get; set; } = (_, _) => false;

    /// <summary>A monster the raven's blow left without life: the master killed it (the experience, the loot).</summary>
    public Func<PlayerObject, Monster, int, CancellationToken, Task>? OnMonsterKilled { get; set; }

    /// <summary>A blow of damage on a player: the PvP side effects (self-defense, the duel's clock).</summary>
    public Func<PlayerObject, PlayerObject, CancellationToken, Task>? OnPlayerHit { get; set; }

    /// <summary>A player the raven's blow left without life: the master killed them.</summary>
    public Func<PlayerObject, PlayerObject, CancellationToken, Task>? OnPlayerKilled { get; set; }

    private static bool InRange(int index) => index is >= 0 and < 0xFFFF;

    // ---------------------------------------------------------------- the stats and the commands

    /// <summary>The raven's stats if it works: the master is a Dark Lord with a Dark Raven that has durability and that
    /// they qualify for.</summary>
    public DarkSpiritStats? StatsFor(PlayerObject master) => DarkSpiritMath.StatsFor(master, _items, _rules());

    /// <summary>Port of CDarkSpirit::SetMode (DarkSpirit.cpp:362): changes the mode and tells the master. Nothing happens
    /// without a working raven, and asking for the mode it is in is ignored, except for a new target.</summary>
    public async Task SetModeAsync(PlayerObject master, DarkSpiritMode mode, int target, CancellationToken ct)
    {
        if (StatsFor(master) == null)
        {
            return;
        }

        master.SpiritWorks = true; // a raven that takes commands is one that works

        if (mode == master.SpiritMode && mode != DarkSpiritMode.AttackTarget)
        {
            return;
        }

        if (mode == DarkSpiritMode.AttackTarget)
        {
            if (!InRange(target))
            {
                return;
            }

            master.SpiritTarget = target;
        }

        master.SpiritMode = mode;
        await master.Session.SendAsync(DarkSpiritPacketBuilder.ModeSend((byte)mode, target & 0xFFFF), ct);
    }

    /// <summary>Port of CDarkSpirit::ChangeCommand: what the client asked for; anything unknown means "normal".</summary>
    public Task ChangeCommandAsync(PlayerObject master, int command, int target, CancellationToken ct) =>
        SetModeAsync(master, command is >= 0 and <= 3 ? (DarkSpiritMode)command : DarkSpiritMode.Normal, target, ct);

    /// <summary>Port of CDarkSpirit::SetTarget: the master attacked this one; the raven in "attack with the master" follows.</summary>
    public void SetTarget(PlayerObject master, int target)
    {
        if (master.Class == 4 && StatsFor(master) != null)
        {
            master.SpiritTarget = target;
        }
    }

    /// <summary>Port of CDarkSpirit::ResetTarget for everybody: <paramref name="index"/> died or left, so every raven that
    /// had it as a target forgets it, and one sent to it stands down.</summary>
    public async Task ResetTargetAsync(int index, CancellationToken ct)
    {
        foreach (var master in _players.All)
        {
            if (master.SpiritTarget != index)
            {
                continue;
            }

            master.SpiritTarget = -1;

            if (master.SpiritMode == DarkSpiritMode.AttackTarget)
            {
                await SetModeAsync(master, DarkSpiritMode.Normal, -1, ct);
            }
        }
    }

    /// <summary>The raven goes quiet (the master died, moved through a gate, entered the world or left the game).</summary>
    public Task StandDownAsync(PlayerObject master, CancellationToken ct) => SetModeAsync(master, DarkSpiritMode.Normal, -1, ct);

    // ---------------------------------------------------------------- once every tick

    /// <summary>Port of CDarkSpirit::MainProc (DarkSpirit.cpp:49): what the raven does at this moment for its master.</summary>
    public async Task TickAsync(PlayerObject master, CancellationToken ct)
    {
        var stats = StatsFor(master);

        if (stats == null)
        {
            master.SpiritWorks = false; // put on again later, it starts in the normal mode
            return;
        }

        if (!master.SpiritWorks)
        {
            master.SpiritWorks = true;
            await SetModeAsync(master, DarkSpiritMode.Normal, -1, ct);
        }

        if (master.IsDying || master.Life == 0 || IsSafeZone(master.Map, master.X, master.Y))
        {
            return;
        }

        var now = Now;

        if (master.SpiritNextAttack > now)
        {
            return;
        }

        master.SpiritNextAttack = now.AddMilliseconds(DarkSpiritRules.AttackInterval(stats.Value));

        switch (master.SpiritMode)
        {
            case DarkSpiritMode.AttackRandom:
                await AttackRandomAsync(master, ct);
                break;

            case DarkSpiritMode.AttackWithMaster:
            case DarkSpiritMode.AttackTarget:
                await AttackChosenAsync(master, ct);
                break;
        }
    }

    private async Task AttackRandomAsync(PlayerObject master, CancellationToken ct)
    {
        var candidates = Candidates(master, -1).Where(index => Distance(master, index) < 5).ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        if (_rng.Next(100) < _rules().RangeAttackRate)
        {
            await RangeAttackAsync(master, candidates[_rng.Next(candidates.Count)], ct);
        }
        else
        {
            await SendBlowAsync(master, candidates[_rng.Next(candidates.Count)], send: true, action: 0, ct);
        }
    }

    private async Task AttackChosenAsync(PlayerObject master, CancellationToken ct)
    {
        int target = master.SpiritTarget;

        if (!InRange(target))
        {
            return;
        }

        if (!TryGetObject(target, out var map, out var x, out var y, out var alive))
        {
            await ResetTargetAsync(target, ct);
            return;
        }

        if (!alive || map != master.Map)
        {
            await ResetTargetAsync(target, ct);
            return;
        }

        if (Distance(master, target) >= 7)
        {
            return;
        }

        if (_rng.Next(100) < _rules().RangeAttackRate)
        {
            await RangeAttackAsync(master, target, ct);
        }
        else
        {
            await SendBlowAsync(master, target, send: true, action: 0, ct);
        }
    }

    /// <summary>Port of CDarkSpirit::RangeAttack: the blow on the target at 60%, and the same on up to ten more within
    /// three tiles of it (without the attack animation for them).</summary>
    private async Task RangeAttackAsync(PlayerObject master, int target, CancellationToken ct)
    {
        await SendBlowAsync(master, target, send: true, action: 1, ct);

        int count = 0;

        foreach (int index in Candidates(master, target))
        {
            if (index == target || Distance(target, index) >= 4)
            {
                continue;
            }

            await SendBlowAsync(master, index, send: false, action: 1, ct);

            if (++count >= 10)
            {
                break;
            }
        }
    }

    /// <summary>Port of CDarkSpirit::SendAttackMsg: everybody around is told the raven attacks, and the blow is queued
    /// for 600 ms later.</summary>
    private async Task SendBlowAsync(PlayerObject master, int target, bool send, byte action, CancellationToken ct)
    {
        if (send)
        {
            var packet = DarkSpiritPacketBuilder.AttackSend(master.Index, target, action);
            await master.Session.SendAsync(packet, ct);

            foreach (var viewer in _players.All)
            {
                if (viewer.Index != master.Index && viewer.VisibleTo.Contains(master.Index))
                {
                    await viewer.Session.SendAsync(packet, ct);
                }
            }
        }

        lock (_pendingLock)
        {
            _pending.Add(new PendingBlow(Now + BlowDelay, master.Index, target, send, action));
        }
    }

    /// <summary>The blows whose 600 ms have gone by land.</summary>
    public async Task LandDueBlowsAsync(CancellationToken ct)
    {
        List<PendingBlow> due;

        lock (_pendingLock)
        {
            var now = Now;
            due = _pending.Where(b => b.Due <= now).ToList();
            _pending.RemoveAll(b => b.Due <= now);
        }

        foreach (var blow in due)
        {
            if (_players.TryGet(blow.Master, out var master) && master.WorldEntered && master.Life > 0 && !master.IsDying)
            {
                await AttackAsync(master, blow.Target, blow.Send, blow.Action, ct);
            }
        }
    }

    // ---------------------------------------------------------------- targets

    /// <summary>The indexes of what the master sees that the raven may attack (CheckSkillTarget): the monsters that are not
    /// merchants or traps, and the players the PvP rules let the master attack. <paramref name="except"/> is left out of
    /// the PvP rules' reach (it is the target of a range attack).</summary>
    private IEnumerable<int> Candidates(PlayerObject master, int except)
    {
        foreach (int index in master.VisibleMonsters.ToList())
        {
            if (_monsters.TryGet(index, out var monster) && IsAttackable(monster, master.Map))
            {
                yield return index;
            }
        }

        foreach (var other in _players.All)
        {
            if (other.Index != master.Index && other.WorldEntered && other.Map == master.Map && master.VisibleTo.Contains(other.Index)
                && !other.IsDying && other.Life > 0 && (other.Index == except || CanAttackPlayer(master, other)))
            {
                yield return other.Index;
            }
        }
    }

    private static bool IsAttackable(Monster monster, byte map) =>
        monster.Live && !monster.IsDead && monster.Map == map && monster.ShopNumber == null
        && !(monster.MonsterClass is >= 100 and <= 110) && monster.MonsterClass != 523; // not the traps

    private bool TryGetObject(int index, out byte map, out int x, out int y, out bool alive)
    {
        if (_players.TryGet(index, out var player))
        {
            (map, x, y, alive) = (player.Map, player.X, player.Y, player.WorldEntered && !player.IsDying && player.Life > 0);
            return true;
        }

        if (_monsters.TryGet(index, out var monster))
        {
            (map, x, y, alive) = (monster.Map, monster.X, monster.Y, IsAttackable(monster, monster.Map));
            return true;
        }

        (map, x, y, alive) = (0, 0, 0, false);
        return false;
    }

    private int Distance(PlayerObject master, int index) =>
        TryGetObject(index, out _, out int x, out int y, out _) ? (int)Math.Sqrt(Math.Pow(master.X - x, 2) + Math.Pow(master.Y - y, 2)) : int.MaxValue;

    private int Distance(int a, int b) =>
        TryGetObject(a, out _, out int ax, out int ay, out _) && TryGetObject(b, out _, out int bx, out int by, out _)
            ? (int)Math.Sqrt(Math.Pow(ax - bx, 2) + Math.Pow(ay - by, 2))
            : int.MaxValue;

    // ---------------------------------------------------------------- the blow

    /// <summary>Port of CDarkSpirit::Attack (DarkSpirit.cpp:285): a blow of the master's raven on a monster or a player.</summary>
    public async Task AttackAsync(PlayerObject master, int targetIndex, bool send, byte action, CancellationToken ct)
    {
        var stats = StatsFor(master);

        if (stats == null || targetIndex == master.Index || !TryGetObject(targetIndex, out var targetMap, out int tx, out int ty, out bool alive)
            || !alive || targetMap != master.Map || IsSafeZone(master.Map, master.X, master.Y) || IsSafeZone(targetMap, tx, ty))
        {
            return;
        }

        _players.TryGet(targetIndex, out var targetPlayer);
        _monsters.TryGet(targetIndex, out var monster);

        if (targetPlayer != null && !CanAttackPlayer(master, targetPlayer))
        {
            return;
        }

        if (targetPlayer == null && (monster == null || monster.ShopNumber != null))
        {
            return;
        }

        SetTarget(master, targetIndex);
        var now = DateTime.UtcNow;
        master.LastCombatTime = now;

        bool duel = targetPlayer != null && InDuel(master, targetPlayer);
        var rules = _rules();

        // The hit roll: the raven's success rate against a monster's, the PvP one between players.
        bool graze = false;

        if (targetPlayer == null)
        {
            int defenseRate = Math.Max(monster!.DefenseSuccessRate, 0);

            if (stats.Value.AttackSuccessRate < defenseRate)
            {
                graze = true;

                if (_rng.Next(100) >= 5)
                {
                    await master.Session.SendAsync(CombatPacketBuilder.DamageSend(targetIndex, 0, 0, missFlag: true, (uint)monster.Life), ct);
                    return;
                }
            }
            else if (_rng.Next(stats.Value.AttackSuccessRate == 0 ? 1 : stats.Value.AttackSuccessRate) < defenseRate)
            {
                await master.Session.SendAsync(CombatPacketBuilder.DamageSend(targetIndex, 0, 0, missFlag: true, (uint)monster.Life), ct);
                return;
            }
        }
        else if (!PlayerAttackMath.PvpBlowLands(master, targetPlayer, _rng))
        {
            var miss = CombatPacketBuilder.DamageSend(targetIndex, 0, 0, missFlag: true, targetPlayer.Life);
            await master.Session.SendAsync(miss, ct);
            await targetPlayer.Session.SendAsync(miss, ct);
            return;
        }

        // The damage.
        int defense = targetPlayer != null ? (int)targetPlayer.Defense * 50 / 100 : monster!.Defense * 50 / 100;
        int roll = DarkSpiritMath.Roll(master, stats.Value, _items, rules, _rng, out byte effect);
        int damage = DarkSpiritMath.AfterTarget(master, targetPlayer, roll, Math.Max(defense, 0), graze, action);

        // The raven wears with every blow, before the server's rates.
        var raven = master.Items[Item.SlotWeapon2];

        if (DarkSpiritMath.Wear(raven, damage, rules))
        {
            await master.Session.SendAsync(ItemPacketBuilder.ItemDurSend((byte)Item.SlotWeapon2, raven.Durability, 0), ct);
            await master.Session.SendAsync(QuestPacketBuilder.PetItemInfoSend(0, 0, (byte)Item.SlotWeapon2, (byte)raven.PetLevel, raven.PetExp), ct);
        }

        damage = DarkSpiritMath.ApplyRates(master, targetPlayer, damage, chaosCastle: master.Map is >= 18 and <= 23 && targetPlayer != null, duel);

        if (targetPlayer != null)
        {
            await HitPlayerAsync(master, targetPlayer, damage, effect, ct);
        }
        else
        {
            await HitMonsterAsync(master, monster!, damage, effect, ct);
        }
    }

    private async Task HitMonsterAsync(PlayerObject master, Monster monster, int damage, byte effect, CancellationToken ct)
    {
        int applied = monster.Immune ? 0 : damage; // the Blood Castle gate and statues take none until their stage
        monster.Life = Math.Max(monster.Life - applied, 0);
        monster.DamageByAttacker.AddOrUpdate(master.Index, applied, (_, previous) => previous + applied);

        if (monster.Life > 0)
        {
            await master.Session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, applied, effect, missFlag: false, (uint)monster.Life), ct);
            return;
        }

        if (OnMonsterKilled != null)
        {
            await OnMonsterKilled(master, monster, applied, ct);
        }
    }

    private async Task HitPlayerAsync(PlayerObject master, PlayerObject target, int damage, byte effect, CancellationToken ct)
    {
        target.LastCombatTime = DateTime.UtcNow;

        if (damage > 0 && OnPlayerHit != null)
        {
            await OnPlayerHit(master, target, ct);
        }

        target.Life = (uint)Math.Max(0, (long)target.Life - damage);

        if (target.Life > 0)
        {
            var packet = CombatPacketBuilder.DamageSend(target.Index, damage, effect, missFlag: false, target.Life);
            await master.Session.SendAsync(packet, ct);
            await target.Session.SendAsync(packet, ct);
            return;
        }

        if (OnPlayerKilled != null)
        {
            await OnPlayerKilled(master, target, ct);
        }
    }
}

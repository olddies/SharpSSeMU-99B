using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.World;

/// <summary>The monsters a Fairy Elf calls with her Summon skills (30-36: the monster of each is <c>SummonMonster1..7</c> of the Skill.dat),
/// and what they do (CSkillManager::SkillSummon, SkillManager.cpp:1343; gObjSummonKill, gObjSummonSetEnemy, gObjMonsterGetXYToOwner,
/// gObjMonsterFindTarget, Monster.cpp). One summon per player: it follows its master, helps itself to the monsters around, goes for
/// what its master hits, comes to him if he gets too far, and disappears when he calls another one, dies or leaves. What it kills is
/// its master's: the experience, the loot, the damage numbers.</summary>
public sealed class SummonManager
{
    /// <summary>Seconds-of-the-original constants: how far from its master a summon wanders before it is brought back to his side
    /// (<c>MoveRange = 15</c>) and the dir it appears with.</summary>
    private const int MoveRange = 15;
    private const byte SummonDir = 2;
    private const int FirstSummonSkill = 30;
    private const int LastSummonSkill = 36;

    private readonly PlayerRegistry _players;
    private readonly MonsterRegistry _monsters;
    private readonly MonsterInfoTable _infos;
    private readonly MapRegistry _maps;
    private readonly Random _rng;

    public SummonManager(PlayerRegistry players, MonsterRegistry monsters, MonsterInfoTable infos, MapRegistry maps, Random rng)
    {
        _players = players;
        _monsters = monsters;
        _infos = infos;
        _maps = maps;
        _rng = rng;
    }

    /// <summary>The monster of each Summon skill, 30 to 36 (SummonMonster1..7): the shipped Goblin, Stone Golem, Assassin... by default.</summary>
    public int[] MonsterBySkill { get; set; } = { 26, 32, 21, 20, 10, 150, 151 };

    /// <summary>The clock (tests move it).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>What a summon kills is its master's: the handler gives him the experience and the loot.</summary>
    public Func<PlayerObject, Monster, int, CancellationToken, Task>? OnMonsterKilled { get; set; }

    public static bool IsSummonSkill(int skill) => skill is >= FirstSummonSkill and <= LastSummonSkill;

    /// <summary>The monster of a Summon skill, or -1.</summary>
    public int MonsterFor(int skill) => IsSummonSkill(skill) && skill - FirstSummonSkill < MonsterBySkill.Length ? MonsterBySkill[skill - FirstSummonSkill] : -1;

    public Monster? Of(PlayerObject owner) => owner.SummonIndex >= 0 && _monsters.TryGet(owner.SummonIndex, out var summon) && summon.OwnerIndex == owner.Index ? summon : null;

    // ---------------------------------------------------------------- calling and sending away

    /// <summary>Port of CSkillManager::SkillSummon (SkillManager.cpp:1343): a master with a summon sends it away (and the cast costs
    /// nothing); otherwise the monster of the skill appears one tile behind him. False when nothing was summoned: no such monster, no
    /// room, or the cast sent the old one away.</summary>
    public async Task<bool> CastAsync(PlayerObject owner, int skill, CancellationToken ct)
    {
        int monsterClass = MonsterFor(skill);

        if (monsterClass < 0)
        {
            return false;
        }

        if (owner.SummonIndex >= 0)
        {
            await KillAsync(owner, ct);
            return false;
        }

        var entry = new MonsterSpawnEntry
        {
            Type = 4, MonsterClass = monsterClass, Map = owner.Map, X = Math.Max(owner.X - 1, 0), Y = Math.Min(owner.Y + 1, 255), Dir = SummonDir,
        };

        var summon = _monsters.SpawnOne(monsterClass, entry, _infos, _maps);

        if (summon == null)
        {
            return false;
        }

        summon.OwnerIndex = owner.Index;
        summon.JustSummoned = true;
        summon.NoRespawn = true;
        summon.Dir = SummonDir;
        summon.MaxRegenMillis = 0;
        owner.SummonIndex = summon.Index;

        await owner.Session.SendAsync(SummonPacketBuilder.SummonLifeSend((int)summon.Life, (int)summon.MaxLife), ct);

        Log.Add(LogColor.Black, "[Summon][{0}] '{1}' calls {2} (#{3})", owner.Index, owner.Name, summon.Name, summon.Index);
        return true;
    }

    /// <summary>Port of gObjSummonKill: the summon is gone, taken out of everybody's view, and its master's life bar for it is emptied.</summary>
    public async Task KillAsync(PlayerObject owner, CancellationToken ct)
    {
        if (owner.SummonIndex < 0)
        {
            return;
        }

        var summon = Of(owner);
        owner.SummonIndex = -1;

        if (summon != null)
        {
            await RemoveAsync(summon, ct);
        }

        if (owner.WorldEntered && owner.Session.Connected)
        {
            await owner.Session.SendAsync(SummonPacketBuilder.SummonLifeSend(0, 1), ct);
        }
    }

    private async Task RemoveAsync(Monster summon, CancellationToken ct)
    {
        summon.Live = false;
        var destroy = WorldPacketBuilder.ViewportDestroy(new[] { summon.Index });

        foreach (int viewerIndex in summon.VisibleTo.ToList())
        {
            if (_players.TryGet(viewerIndex, out var viewer))
            {
                viewer.VisibleMonsters.Remove(summon.Index);

                if (viewer.WorldEntered)
                {
                    await viewer.Session.SendAsync(destroy, ct);
                }
            }
        }

        summon.VisibleTo.Clear();
        _monsters.Remove(summon.Index);
    }

    /// <summary>Port of gObjSummonSetEnemy (Monster.cpp:733): what its master hits, the summon goes for -- unless it is already after something.</summary>
    public void SetEnemy(PlayerObject owner, int monsterIndex)
    {
        var summon = Of(owner);

        if (summon == null || summon.TargetIndex != -1 || monsterIndex == summon.Index || !_monsters.TryGet(monsterIndex, out var target) || !CanBeTarget(target, summon))
        {
            return;
        }

        summon.TargetIndex = monsterIndex;
    }

    // ---------------------------------------------------------------- what they do

    /// <summary>Whether a summon may go for this monster: alive, of the same map, not an NPC, not another summon, and not in a safe zone.</summary>
    private bool CanBeTarget(Monster target, Monster summon)
    {
        if (!target.Live || target.Map != summon.Map || target.ShopNumber != null || target.IsSummon || target.Immune)
        {
            return false;
        }

        return !(_maps.GetMap(target.Map)?.CheckAttr(target.X, target.Y, 1) ?? false);
    }

    /// <summary>Called by the ticker on every tick: each summon looks after itself.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = Clock();

        foreach (var summon in _monsters.All.Where(m => m.IsSummon).ToList())
        {
            if (!_players.TryGet(summon.OwnerIndex, out var owner) || !owner.WorldEntered || owner.IsDying || owner.Life == 0 || !owner.Session.Connected)
            {
                if (owner != null)
                {
                    await KillAsync(owner, ct);
                }
                else
                {
                    await RemoveAsync(summon, ct);
                }

                continue;
            }

            await ActAsync(summon, owner, now, ct);
        }
    }

    private async Task ActAsync(Monster summon, PlayerObject owner, DateTime now, CancellationToken ct)
    {
        // Too far from its master, or on another map: it comes to his side (Monster.cpp:1219).
        if (summon.Map != owner.Map || Distance(summon.X, summon.Y, owner.X, owner.Y) >= MoveRange)
        {
            await BringToAsync(summon, owner, ct);
            return;
        }

        var map = _maps.GetMap(summon.Map);

        // The target: what its master hit, or one of the monsters around it.
        Monster? target = null;

        if (summon.TargetIndex != -1)
        {
            if (!_monsters.TryGet(summon.TargetIndex, out target) || !CanBeTarget(target, summon)
                || Distance(summon.X, summon.Y, target.X, target.Y) > Math.Max(summon.ViewRange, 7))
            {
                summon.TargetIndex = -1;
                target = null;
            }
        }

        if (target == null && now >= summon.LastMoveTime.AddMilliseconds(500))
        {
            target = FindTarget(summon);
            summon.TargetIndex = target?.Index ?? -1;
        }

        int attackSpeed = summon.AttackSpeed > 0 ? summon.AttackSpeed : 1500;
        double reach = summon.AttackRange > 0 ? summon.AttackRange : 1.5;

        if (target != null)
        {
            double distance = Distance(summon.X, summon.Y, target.X, target.Y);

            if (distance <= reach)
            {
                if (now >= summon.LastAttackTime.AddMilliseconds(attackSpeed))
                {
                    summon.LastAttackTime = now;
                    await StrikeAsync(summon, owner, target, ct);
                }

                return;
            }

            // Chase it, a tile at a time.
            await StepTowardsAsync(summon, target.X, target.Y, now, map, ct);
            return;
        }

        // Nothing to do: stay by its master's side.
        if (Distance(summon.X, summon.Y, owner.X, owner.Y) > Math.Max(reach, 2))
        {
            await StepTowardsAsync(summon, owner.X, owner.Y, now, map, ct);
        }
    }

    private static double Distance(int x1, int y1, int x2, int y2) => Math.Sqrt(Math.Pow(x1 - x2, 2) + Math.Pow(y1 - y2, 2));

    /// <summary>Port of gObjMonsterFindTarget for a summon (Monster.cpp:1590): one of the monsters in its sight, at random.</summary>
    private Monster? FindTarget(Monster summon)
    {
        int sight = Math.Max(summon.ViewRange, 4);
        var candidates = _monsters.All
            .Where(m => m.Map == summon.Map && CanBeTarget(m, summon) && Distance(summon.X, summon.Y, m.X, m.Y) <= sight)
            .ToList();

        return candidates.Count == 0 ? null : candidates[_rng.Next(candidates.Count)];
    }

    private async Task StepTowardsAsync(Monster summon, int x, int y, DateTime now, GameMap? map, CancellationToken ct)
    {
        int cooldown = (int)(Math.Max(summon.MoveSpeed, 300) * 1.3);

        if (now < summon.LastMoveTime.AddMilliseconds(cooldown))
        {
            return;
        }

        summon.LastMoveTime = now;

        int signX = Math.Sign(x - summon.X);
        int signY = Math.Sign(y - summon.Y);
        int nx = summon.X + signX;
        int ny = summon.Y + signY;

        if (map != null && (map.IsBlocked(nx, ny) || map.CheckAttr(nx, ny, 1)))
        {
            // Around it: try the two tiles beside the straight line.
            if (signX != 0 && signY != 0 && !map.IsBlocked(summon.X + signX, summon.Y) && !map.CheckAttr(summon.X + signX, summon.Y, 1))
            {
                nx = summon.X + signX;
                ny = summon.Y;
            }
            else if (signX != 0 && signY != 0 && !map.IsBlocked(summon.X, summon.Y + signY) && !map.CheckAttr(summon.X, summon.Y + signY, 1))
            {
                nx = summon.X;
                ny = summon.Y + signY;
            }
            else
            {
                return;
            }
        }

        summon.Dir = Direction(signX, signY, summon.Dir);
        summon.X = (byte)nx;
        summon.Y = (byte)ny;
        summon.TX = summon.X;
        summon.TY = summon.Y;
        await ToViewersAsync(summon, WorldPacketBuilder.ViewportMonsterMove(summon.Index, summon.X, summon.Y, summon.Dir), ct);
    }

    private static byte Direction(int signX, int signY, byte keep) => (signX, signY) switch
    {
        (0, -1) => 0, (1, -1) => 1, (1, 0) => 2, (1, 1) => 3, (0, 1) => 4, (-1, 1) => 5, (-1, 0) => 6, (-1, -1) => 7,
        _ => keep,
    };

    /// <summary>Port of the teleport at Monster.cpp:1219: it appears beside its master, wherever he is.</summary>
    private async Task BringToAsync(Monster summon, PlayerObject owner, CancellationToken ct)
    {
        var map = _maps.GetMap(owner.Map);
        var (x, y) = map?.GetRandomPos(owner.X, owner.Y, 2, _rng) ?? (owner.X + 1, owner.Y);

        if (summon.Map != owner.Map)
        {
            // Another map: nobody on the old one sees it any more (the sweep announces it on the new one).
            var destroy = WorldPacketBuilder.ViewportDestroy(new[] { summon.Index });

            foreach (int viewerIndex in summon.VisibleTo.ToList())
            {
                if (_players.TryGet(viewerIndex, out var viewer))
                {
                    viewer.VisibleMonsters.Remove(summon.Index);
                    await viewer.Session.SendAsync(destroy, ct);
                }
            }

            summon.VisibleTo.Clear();
            summon.Map = owner.Map;
            summon.JustSummoned = true;
        }

        summon.X = (byte)x;
        summon.Y = (byte)y;
        summon.TX = summon.X;
        summon.TY = summon.Y;
        summon.TargetIndex = -1;

        await ToViewersAsync(summon, WorldPacketBuilder.ViewportMonsterMove(summon.Index, summon.X, summon.Y, summon.Dir), ct);
    }

    /// <summary>One blow of the summon on a monster (CAttack::Attack with a monster on both sides): the hit roll, its damage less the
    /// target's defense, and what it takes goes to its master's account -- he sees the number and, if it kills, the reward is his.</summary>
    private async Task StrikeAsync(Monster summon, PlayerObject owner, Monster target, CancellationToken ct)
    {
        summon.Dir = Direction(Math.Sign(target.X - summon.X), Math.Sign(target.Y - summon.Y), summon.Dir);
        await ToViewersAsync(summon, CombatPacketBuilder.ActionSend(summon.Index, summon.Dir, 0, target.Index), ct);

        int attackRate = Math.Max(summon.AttackSuccessRate, 0);
        int defenseRate = Math.Max(target.DefenseSuccessRate, 0);
        bool graze = false;

        if (attackRate < defenseRate)
        {
            if (_rng.Next(100) >= 5)
            {
                await owner.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, 0, 0, missFlag: true, target.Life), ct);
                return;
            }

            graze = true;
        }
        else if (_rng.Next(attackRate == 0 ? 1 : attackRate) < defenseRate)
        {
            await owner.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, 0, 0, missFlag: true, target.Life), ct);
            return;
        }

        int min = summon.DamageMin > 0 ? summon.DamageMin : 5;
        int max = summon.DamageMax >= min ? summon.DamageMax : min + 5;
        int damage = min + _rng.Next(Math.Max(max - min, 1));

        damage = Math.Max(damage - Math.Max(target.EffectiveDefense(), 0), 0);

        if (graze)
        {
            damage = damage * 30 / 100;
        }

        int floor = Math.Max(summon.Level / 10, 1);

        if (damage < floor)
        {
            damage = floor + _rng.Next(floor);
        }

        target.Life = Math.Max(target.Life - damage, 0);
        target.DamageByAttacker.AddOrUpdate(owner.Index, damage, (_, previous) => previous + damage);

        if (target.Life > 0)
        {
            await owner.Session.SendAsync(CombatPacketBuilder.DamageSend(target.Index, damage, 0, missFlag: false, target.Life), ct);
            return;
        }

        summon.TargetIndex = -1;

        if (OnMonsterKilled != null)
        {
            await OnMonsterKilled(owner, target, damage, ct);
        }
    }

    private async Task ToViewersAsync(Monster monster, byte[] packet, CancellationToken ct)
    {
        foreach (int viewerIndex in monster.VisibleTo.ToList())
        {
            if (_players.TryGet(viewerIndex, out var viewer) && viewer.WorldEntered)
            {
                await viewer.Session.SendAsync(packet, ct);
            }
        }
    }
}

/// <summary>The packets of the summons.</summary>
public static class SummonPacketBuilder
{
    /// <summary>PMSG_SUMMON_LIFE_SEND (GCSummonLifeSend, Protocol.cpp:1990), C1:F3:20: the life of a master's summon, as a percentage,
    /// for the bar the client shows. Nothing is sent when the maximum is zero.</summary>
    public static byte[] SummonLifeSend(int life, int maxLife)
    {
        int percent = maxLife <= 0 ? 0 : Math.Clamp(life * 100 / maxLife, 0, 100);
        return PacketBuilder.BuildC1Sub(0xF3, 0x20, new[] { (byte)percent });
    }
}

namespace MuServer.GameServer.World;

/// <summary>What the Dark Raven (the Dark Spirit of the original) does for its master (eDarkSpiritMode).</summary>
public enum DarkSpiritMode : byte
{
    /// <summary>It follows its master and does nothing.</summary>
    Normal = 0,

    /// <summary>It attacks whatever near its master it may attack, at random.</summary>
    AttackRandom = 1,

    /// <summary>It attacks what its master attacks.</summary>
    AttackWithMaster = 2,

    /// <summary>It attacks the one target its master chose.</summary>
    AttackTarget = 3,
}

/// <summary>What the Dark Raven hits with at the moment: damage range, attack speed and success rate, which depend on
/// its level and its master's command (CDarkSpirit::Set).</summary>
public readonly record struct DarkSpiritStats(int DamageMin, int DamageMax, int AttackSpeed, int AttackSuccessRate);

/// <summary>
/// The settings of the Dark Raven (the DarkSpirit* keys of Character.dat and PetDurabilityRate of Common.dat),
/// with the shipped values by default.
/// </summary>
public sealed class DarkSpiritRules
{
    /// <summary>The rules in force (set by Program).</summary>
    public static DarkSpiritRules Current { get; set; } = new();

    /// <summary>Of every hundred attacks, how many hit the target and the ones around it.</summary>
    public int RangeAttackRate { get; init; } = 30;

    public int CriticalDamageRate { get; init; } = 30;
    public int ExcellentDamageRate { get; init; }

    public int DamageMinConstA { get; init; } = 180;
    public int DamageMinConstB { get; init; } = 15;
    public int DamageMinConstC { get; init; } = 8;
    public int DamageMaxConstA { get; init; } = 200;
    public int DamageMaxConstB { get; init; } = 15;
    public int DamageMaxConstC { get; init; } = 4;
    public int AttackSpeedConstA { get; init; } = 20;
    public int AttackSpeedConstB { get; init; } = 4;
    public int AttackSpeedConstC { get; init; } = 5;
    public int AttackSpeedConstD { get; init; } = 50;
    public int AttackSuccessRateConstA { get; init; } = 1000;
    public int AttackSuccessRateConstB { get; init; } = 16;
    public int AttackSuccessRateConstC { get; init; } = 1;

    /// <summary>PetDurabilityRate: the percentage of the normal wear a pet gets.</summary>
    public int PetDurabilityRate { get; init; } = 100;

    private static int Div(int value, int divisor) => divisor == 0 ? 0 : value / divisor;

    /// <summary>Port of the calculation in CDarkSpirit::Set (DarkSpirit.cpp:330-340).</summary>
    public DarkSpiritStats Stats(int petLevel, int leadership) => new(
        DamageMinConstA + (petLevel * DamageMinConstB) + Div(leadership, DamageMinConstC),
        DamageMaxConstA + (petLevel * DamageMaxConstB) + Div(leadership, DamageMaxConstC),
        AttackSpeedConstA + Div(petLevel * AttackSpeedConstB, AttackSpeedConstC) + Div(leadership, AttackSpeedConstD),
        AttackSuccessRateConstA + Div(petLevel * AttackSuccessRateConstB, AttackSuccessRateConstC));

    /// <summary>The milliseconds between two of its attacks (DarkSpirit.cpp:80): a second and a half less ten for every
    /// point of attack speed.</summary>
    public static int AttackInterval(DarkSpiritStats stats) => 1500 - (stats.AttackSpeed * 10);
}

/// <summary>The Dark Raven's blow: GetAttackDamage and the rest of CDarkSpirit::Attack's damage section
/// (DarkSpirit.cpp:673-737, 910-961).</summary>
public static class DarkSpiritMath
{
    private const byte DarkLord = 4;
    private const byte EffectExcellent = 2;
    private const byte EffectCritical = 3;

    /// <summary>The Raven's stats for a master: null unless it is a Dark Lord wearing a Dark Raven that works (it has
    /// durability and the master meets what it asks), CDarkSpirit::Set.</summary>
    public static DarkSpiritStats? StatsFor(PlayerObject master, ItemBalanceTable items, DarkSpiritRules rules)
    {
        if (master.Class != DarkLord)
        {
            return null;
        }

        var raven = master.Items[Item.SlotWeapon2];

        if (!raven.IsItem() || raven.Index != Item.DarkRaven)
        {
            return null;
        }

        var info = items.Get(raven.Index);

        if (info == null || !ItemCombatMath.IsValidItem(master, raven, info))
        {
            return null;
        }

        return rules.Stats(raven.PetLevel, (int)(master.Leadership + master.AddLeadership));
    }

    /// <summary>The damage a blow rolls before the target's defense: the Raven's range, raised by a scepter's magic in the
    /// master's right hand, with the critical and excellent rolls. <paramref name="effect"/> is the colour the client
    /// draws the number with.</summary>
    public static int Roll(PlayerObject master, DarkSpiritStats stats, ItemBalanceTable items, DarkSpiritRules rules, Random rng, out byte effect)
    {
        effect = 0;
        int damageMin = stats.DamageMin;
        int damageMax = stats.DamageMax;

        (damageMin, damageMax) = ScepterRise(master, items, damageMin, damageMax);

        int range = Math.Max(damageMax - damageMin, 1);
        int damage = damageMin + rng.Next(range);

        if (rng.Next(100) < rules.CriticalDamageRate)
        {
            effect = EffectCritical;
            damage = damageMax;
        }

        if (rng.Next(100) < rules.ExcellentDamageRate)
        {
            effect = EffectExcellent;
            damage = (damageMax * 120) / 100;
        }

        return damage;
    }

    /// <summary>The scepter (section 2) with magic in the master's right hand raises both ends of the range by
    /// <c>(magic / 2 + 2 per level)</c> percent, less as it wears (DarkSpirit.cpp:915-921).</summary>
    public static (int Min, int Max) ScepterRise(PlayerObject master, ItemBalanceTable items, int damageMin, int damageMax)
    {
        var weapon = master.Items[Item.SlotWeapon1];
        var info = weapon.IsItem() ? items.Get(weapon.Index) : null;

        if (info == null || weapon.Index < Item.GetItem(2, 0) || weapon.Index >= Item.GetItem(3, 0)
            || !ItemCombatMath.IsValidItem(master, weapon, info))
        {
            return (damageMin, damageMax);
        }

        int magic = ItemCombatMath.GetMagicDamageRate(weapon, info);

        if (magic == 0)
        {
            return (damageMin, damageMax);
        }

        int rise = (int)(((magic / 2) + (weapon.Level * 2)) * PlayerAttackMath.DurabilityState(weapon, info));
        return (damageMin + (damageMin * rise / 100), damageMax + (damageMax * rise / 100));
    }

    /// <summary>The blow after the roll, as far as the target's side: the halved defense comes off (a monster's too), a
    /// graze is 30%, a range attack 60%, what a player target wears reduces it, and the master's level sets a floor.
    /// The damage rates for players or monsters come after, in <see cref="ApplyRates"/>, because the Raven's wear is
    /// counted in between.</summary>
    public static int AfterTarget(PlayerObject master, PlayerObject? targetPlayer, int roll, int targetDefense, bool graze, byte action)
    {
        int damage = Math.Max(roll - targetDefense, 0);

        if (graze)
        {
            damage = (damage * 30) / 100;
        }

        if (action != 0)
        {
            damage = (damage * 60) / 100;
        }

        if (targetPlayer != null)
        {
            damage = PlayerAttackMath.ReduceMonsterHit(targetPlayer, damage);
        }

        return Math.Max(damage, Math.Max(master.Level / 10, 1));
    }

    /// <summary>The server's damage rates for a player hitting players or monsters (Attack.cpp:381-418), and the duel's or
    /// the Chaos Castle's inside them.</summary>
    public static int ApplyRates(PlayerObject master, PlayerObject? targetPlayer, int damage, bool chaosCastle, bool duel)
    {
        var rules = CombatRules.Current;
        int own = Math.Clamp((int)master.Class, 0, 4);

        if (targetPlayer != null)
        {
            damage = damage * rules.GeneralDamageRatePvP / 100;
            damage = damage * rules.DamageRatePvP[own] / 100;
            damage = damage * rules.DamageRateTo[own][Math.Clamp((int)targetPlayer.Class, 0, 4)] / 100;

            if (duel)
            {
                damage = damage * rules.DuelDamageRate / 100;
            }
            else if (chaosCastle)
            {
                damage = damage * rules.ChaosCastleDamageRate / 100;
            }
        }
        else
        {
            damage = damage * rules.GeneralDamageRatePvM / 100;
            damage = damage * rules.DamageRatePvM[own] / 100;
        }

        return Math.Max(damage, 0);
    }

    /// <summary>The wear of the Raven (CDarkSpirit::DarkSpiritSprite): two points of durability "small" for every hundred of
    /// damage; a whole point is lost every 800 (scaled by the rates). True when a point was lost.</summary>
    public static bool Wear(Item raven, int damage, DarkSpiritRules rules)
    {
        raven.DurabilitySmall += (damage * 2) / 100;

        // The original also scales it by a rate of the player's own (lpObj->PetDurabilityRate), which nothing changes here.
        int maxSmall = (800 * rules.PetDurabilityRate) / 100;

        if (raven.DurabilitySmall < maxSmall)
        {
            return false;
        }

        raven.Durability = (byte)Math.Max(raven.Durability - 1, 0);
        raven.DurabilitySmall = 0;
        return true;
    }
}

namespace MuServer.GameServer.World;

/// <summary>The combat constants of "GameServerInfo - Character.dat" / "Item.dat" that the damage formula uses.
/// Set once at start-up from the loaded configuration (<see cref="Current"/>); the defaults are the shipped
/// values, so code that runs without the configuration (tests) gets the factory balance.</summary>
public sealed class CombatRules
{
    public static CombatRules Current { get; set; } = new();

    public int GeneralDamageRatePvM { get; init; } = 100;
    public int[] DamageRatePvM { get; init; } = { 100, 100, 100, 100, 100 };
    public int ReflectDamageRatePvM { get; init; } = 100;

    // Player against player (Attack.cpp:381-403): the general rate, the class's own and the one for the class it
    // hits, and inside the Chaos Castle its own rate on top.
    public int GeneralDamageRatePvP { get; init; } = 100;
    public int[] DamageRatePvP { get; init; } = { 100, 100, 100, 100, 100 };
    public int[][] DamageRateTo { get; init; } = Enumerable.Range(0, 5).Select(_ => new[] { 100, 100, 100, 100, 100 }).ToArray();
    public int ChaosCastleDamageRate { get; init; } = 50;
    public int DuelDamageRate { get; init; } = 60;

    /// <summary>[class][A..D] and [class][A..C]: the constants of the PvP attack and defense success rates
    /// (ObjectManager.cpp:2119-2135, 2267-2283), the shipped values by default.</summary>
    public int[][] AttackSuccessRatePvPConst { get; init; } =
    {
        new[] { 3, 1, 4, 1 }, new[] { 3, 1, 45, 10 }, new[] { 3, 1, 6, 10 }, new[] { 3, 1, 35, 10 }, new[] { 3, 1, 4, 1 },
    };

    public int[][] DefenseSuccessRatePvPConst { get; init; } =
    {
        new[] { 2, 1, 4 }, new[] { 2, 1, 2 }, new[] { 2, 1, 10 }, new[] { 2, 1, 4 }, new[] { 2, 1, 2 },
    };

    /// <summary>% of max life recovered every 5 seconds per class (always recovered on top: +5 after 5 seconds
    /// without fighting, plus the items' HP recovery option).</summary>
    public int[] HpRecoveryRate { get; init; } = new int[5];

    public int SatanIncDamageConstA { get; init; } = 10;
    public int DinorantIncDamageConstA { get; init; } = 10;
    public int AngelDecDamageConstA { get; init; } = 10;
    public int DinorantDecDamageConstA { get; init; } = 10;
    public int DinorantDecDamageConstB { get; init; } = 10;
    public int DarkHorseDecDamageConstA { get; init; } = 10;
    public int DarkHorseDecDamageConstB { get; init; } = 3;
}

/// <summary>What a hit produced: the damage and the effect the client colours it with (PMSG_DAMAGE_SEND type).
/// <see cref="AttackerLifeLost"/> is the life the wings or the Imp cost the attacker for this hit.</summary>
public readonly record struct PlayerHit(int Damage, byte Effect, int AttackerLifeLost);

/// <summary> Port of the damage part of CAttack::Attack (Attack.cpp:249-437) for a player hitting a monster,
/// with GetTargetDefense (1117-1154), GetAttackDamage (1156-1307), GetAttackDamageWizard (1309-1384) and the wing
/// and pet bonuses (WingSprite/HelperSprite, 623-726). The hit roll (MissCheck) stays with each caller; a
/// "graze" is a failed roll that still hits for 30%. Not ported: skill effects, combo, the double/triple damage
/// of buffs, the per-map DamageTable and PvP. </summary>
public static class PlayerAttackMath
{
    public const byte EffectNone = 0;
    public const byte EffectIgnoreDefense = 1;
    public const byte EffectExcellent = 2;
    public const byte EffectCritical = 3;
    public const byte EffectReflect = 4;
    public const byte EffectDoubleDamage = 0x40;

    // SkillManager.h
    private const int SkillFallingSlash = 19, SkillLunge = 20, SkillUppercut = 21, SkillCyclone = 22, SkillSlash = 23;
    private const int SkillTwistingSlash = 41, SkillRagefulBlow = 42, SkillDeathStab = 43, SkillImpale = 47;
    private const int SkillFireBreath = 49, SkillIceArrow = 51, SkillPenetration = 52, SkillFireSlash = 55, SkillPowerSlash = 56;
    private const int SkillForce = 60, SkillFireBurst = 61, SkillEarthquake = 62, SkillElectricSpark = 65;

    private const byte ClassDw = 0, ClassDk = 1, ClassMg = 3;

    private static readonly int Uniria = Item.GetItem(13, 2);
    private static readonly int Dinorant = Item.GetItem(13, 3);
    private static readonly int Satan = Item.GetItem(13, 1);

    /// <summary>The Dark Wizard and the Magic Gladiator use the magic formula for every skill except the
    /// Magic Gladiator's sword skills (Attack.cpp:285).</summary>
    public static bool UsesMagicDamage(PlayerObject player, int skill) =>
        (player.Class == ClassDw || player.Class == ClassMg) && skill != 0
        && skill is not (SkillFallingSlash or SkillLunge or SkillUppercut or SkillCyclone or SkillSlash
            or SkillTwistingSlash or SkillImpale or SkillFireSlash or SkillPowerSlash);

    /// <param name="skillDamageMin">the skill's own damage (0 for a plain attack).</param>
    /// <param name="applySkillDamage">SkillDamage.txt multiplier (gSkillDamage.GetDamage); identity when null.</param>
    public static PlayerHit HitMonster(
        PlayerObject player, int monsterDefense, int skill, int skillDamageMin, int skillDamageMax, bool graze,
        ItemBalanceTable items, Random rng, Func<int, int, int>? applySkillDamage = null) =>
        Hit(player, null, monsterDefense, skill, skillDamageMin, skillDamageMax, graze, items, rng, applySkillDamage, false);

    /// <summary>A player hitting another (CAttack::Attack with two users): the target's defense is halved, what the
    /// target wears reduces the hit and the PvP damage rates replace the PvM ones. <paramref name="chaosCastle"/> adds
    /// the Chaos Castle's own rate.</summary>
    public static PlayerHit HitPlayer(
        PlayerObject attacker, PlayerObject target, int skill, int skillDamageMin, int skillDamageMax, bool graze,
        ItemBalanceTable items, Random rng, Func<int, int, int>? applySkillDamage = null, bool chaosCastle = false, bool duel = false) =>
        Hit(attacker, target, (int)target.Defense * 50 / 100, skill, skillDamageMin, skillDamageMax, graze, items, rng, applySkillDamage, chaosCastle, duel);

    /// <summary>Port of CAttack::MissCheckPvP (Attack.cpp:1033-1062): the attacker's PvP success rate against the
    /// target's, scaled by their levels. True when the blow lands.</summary>
    public static bool PvpBlowLands(PlayerObject attacker, PlayerObject target, Random rng)
    {
        var rules = CombatRules.Current;
        double attack = PvpAttackSuccessRate(attacker, rules);
        double defense = PvpDefenseSuccessRate(target, rules);

        double rate = attack + defense == 0 ? 0 : 100 * (attack * 10000.0 / (attack + defense) / 10000.0) * 2;
        rate *= attacker.Level + target.Level == 0 ? 0 : attacker.Level * 10000.0 / (attacker.Level + target.Level) / 10000.0;
        int successRate = (int)rate;

        int levelGap = target.Level - attacker.Level;

        if (levelGap is >= 100 and < 199)
        {
            successRate -= 5;
        }
        else if (levelGap is >= 200 and < 299)
        {
            successRate -= 10;
        }
        else if (levelGap >= 300)
        {
            successRate -= 15;
        }

        return rng.Next(100) <= successRate;
    }

    /// <summary>ObjectManager.cpp:2119-2135: <c>Level * A / B + Dexterity * C / D</c>, the dexterity with its additions.</summary>
    public static int PvpAttackSuccessRate(PlayerObject player, CombatRules rules)
    {
        var c = rules.AttackSuccessRatePvPConst[Math.Clamp((int)player.Class, 0, 4)];
        return ((int)player.Level * c[0] / Math.Max(c[1], 1)) + ((int)(player.Dexterity + player.AddDexterity) * c[2] / Math.Max(c[3], 1));
    }

    /// <summary>ObjectManager.cpp:2267-2283: <c>Level * A / B + Dexterity / C</c>.</summary>
    public static int PvpDefenseSuccessRate(PlayerObject player, CombatRules rules)
    {
        var c = rules.DefenseSuccessRatePvPConst[Math.Clamp((int)player.Class, 0, 4)];
        return ((int)player.Level * c[0] / Math.Max(c[1], 1)) + ((int)(player.Dexterity + player.AddDexterity) / Math.Max(c[2], 1));
    }

    private static PlayerHit Hit(
        PlayerObject player, PlayerObject? target, int monsterDefense, int skill, int skillDamageMin, int skillDamageMax, bool graze,
        ItemBalanceTable items, Random rng, Func<int, int, int>? applySkillDamage, bool chaosCastle, bool duel = false)
    {
        var rules = CombatRules.Current;
        byte effect = EffectNone;

        // GetTargetDefense: a monster's defense (a player's, halved, when the target is one), unless the
        // attacker's ignore-defense option triggers.
        int defense = Math.Max(monsterDefense, 0);

        if (rng.Next(100) < player.IgnoreDefenseRate)
        {
            effect = EffectIgnoreDefense;
            defense = 0;
        }

        int damageMin, damageMax;

        if (UsesMagicDamage(player, skill))
        {
            // GetAttackDamageWizard: the spell's damage, then the staff or magic sword "rise" on top of both.
            damageMin = player.MagicDamageMin + skillDamageMin + player.SkillDamageBonus;
            damageMax = player.MagicDamageMax + skillDamageMax + player.SkillDamageBonus;

            var right = player.Items[Item.SlotWeapon1];
            var info = right.IsItem() ? items.Get(right.Index) : null;

            if (info != null && right.Durability > 0 && (info.Section == 0 || info.Section == 5))
            {
                int rise = (int)(((ItemCombatMath.GetMagicDamageRate(right, info) / 2) + (right.Level * 2)) * DurabilityState(right, info));
                damageMin += damageMin * rise / 100;
                damageMax += damageMax * rise / 100;
            }
        }
        else
        {
            // GetAttackDamage: the hands a plain attack uses, plus the skill's damage.
            (int handMin, int handMax) = player.SelectAttackHands();
            int skillBonus = skill != 0 ? player.SkillDamageBonus : 0;
            damageMin = handMin + skillDamageMin + skillBonus;
            damageMax = handMax + skillDamageMax + skillBonus;
        }

        int range = Math.Max(damageMax - damageMin, 1);
        int damage = damageMin + rng.Next(range);

        if (rng.Next(100) < player.CriticalDamageRate)
        {
            effect = EffectCritical;
            damage = damageMax + player.CriticalDamage;
        }

        if (rng.Next(100) < player.ExcellentDamageRate)
        {
            effect = EffectExcellent;
            damage = ((damageMax * 120) / 100) + player.ExcellentDamage;
        }

        damage = Math.Max(damage - defense, 0);

        if (applySkillDamage != null && skill != 0)
        {
            damage = applySkillDamage(skill, damage);
        }

        if (graze)
        {
            damage = (damage * 30) / 100;
        }

        int lifeLost = 0;

        // What the target player wears takes its share between the attacker's bonuses, as in WingSprite and
        // HelperSprite (Attack.cpp:299-315, 623-726): the damage reduction option first, then each sprite applies
        // the attacker's side and then the target's.
        if (target != null)
        {
            damage -= damage * target.DamageReduction / 100;
        }

        damage = WingBonus(player, damage, ref lifeLost);

        if (target != null)
        {
            damage = TargetWing(target, damage);
        }

        damage = PetBonus(player, damage, rules, ref lifeLost);

        if (target != null)
        {
            damage = TargetHelper(target, damage, rules);
        }

        int minDamage = Math.Max(player.Level / 10, 1);

        if (damage < minDamage)
        {
            damage = minDamage + rng.Next(minDamage);
        }

        if (skill is SkillFallingSlash or SkillLunge or SkillUppercut or SkillCyclone or SkillSlash or SkillTwistingSlash
            or SkillRagefulBlow or SkillDeathStab or SkillImpale or SkillFireBreath or SkillIceArrow or SkillPenetration
            or SkillFireSlash or SkillPowerSlash)
        {
            var helper = player.Items[Item.SlotHelper];

            if (skill != SkillImpale || helper.Index == Uniria || helper.Index == Dinorant)
            {
                damage = player.Class == ClassDk ? damage * player.DkDamageMultiplierRate / 100 : damage * 200 / 100;
            }
        }
        else if (skill is SkillForce or SkillFireBurst or SkillEarthquake or SkillElectricSpark)
        {
            damage = damage * player.DlDamageMultiplierRate / 100;
        }

        // The double damage of the set items (Attack.cpp:345-349).
        if (player.DoubleDamageRate > 0 && rng.Next(100) < player.DoubleDamageRate)
        {
            effect |= EffectDoubleDamage;
            damage += damage;
        }

        if (target != null)
        {
            int own = Math.Clamp((int)player.Class, 0, 4);
            damage = damage * rules.GeneralDamageRatePvP / 100;
            damage = damage * rules.DamageRatePvP[own] / 100;
            damage = damage * rules.DamageRateTo[own][Math.Clamp((int)target.Class, 0, 4)] / 100;

            // The duel's rate or, failing that, the Chaos Castle's (Attack.cpp:391-402).
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
            damage = damage * rules.DamageRatePvM[Math.Clamp((int)player.Class, 0, 4)] / 100;
        }

        return new PlayerHit(Math.Max(damage, 0), effect, lifeLost);
    }

    /// <summary>A player's reflected damage hitting the monster that attacked them (Attack.cpp:377-385 with the
    /// PvM damage rates of 391-418): the "attack" of message 10, whose damage is already decided.</summary>
    public static int ReflectOnMonster(PlayerObject player, int reflected)
    {
        var rules = CombatRules.Current;
        int damage = reflected * rules.ReflectDamageRatePvM / 100;
        damage = damage * rules.GeneralDamageRatePvM / 100;
        damage = damage * rules.DamageRatePvM[Math.Clamp((int)player.Class, 0, 4)] / 100;
        return Math.Max(damage, 0);
    }

    /// <summary>How a monster's hit is reduced by the player it lands on (Attack.cpp:299-307 and the target side
    /// of WingSprite/HelperSprite): the damage reduction option, then the wings and the guardian pets.</summary>
    public static int ReduceMonsterHit(PlayerObject target, int damage)
    {
        damage -= damage * target.DamageReduction / 100;
        damage = TargetWing(target, damage);
        damage = TargetHelper(target, damage, CombatRules.Current);
        return Math.Max(damage, 0);
    }

    /// <summary>The target side of WingSprite (Attack.cpp:657-676): first wings take 12% (-2% per level) off, second
    /// wings 25% (-2% per level) and the Cape of Lord 10% (-1% per level).</summary>
    private static int TargetWing(PlayerObject target, int damage)
    {
        var wing = target.Items[Item.SlotWing];

        if (!wing.IsItem() || wing.Durability == 0)
        {
            return damage;
        }

        if (wing.Index >= Item.GetItem(12, 0) && wing.Index <= Item.GetItem(12, 2))
        {
            return damage * (88 - (wing.Level * 2)) / 100;
        }

        if (wing.Index >= Item.GetItem(12, 3) && wing.Index <= Item.GetItem(12, 6))
        {
            return damage * (75 - (wing.Level * 2)) / 100;
        }

        if (wing.Index == Item.GetItem(13, 30)) // Cape of Lord
        {
            return damage * (90 - wing.Level) / 100;
        }

        return damage;
    }

    /// <summary>The target side of HelperSprite (Attack.cpp:706-725): the Guardian Angel and the Horn of Dinorant.</summary>
    private static int TargetHelper(PlayerObject target, int damage, CombatRules rules)
    {
        var helper = target.Items[Item.SlotHelper];

        if (!helper.IsItem() || helper.Durability == 0)
        {
            return damage;
        }

        if (helper.Index == Item.GetItem(13, 0)) // Guardian Angel
        {
            return damage * (100 - rules.AngelDecDamageConstA) / 100;
        }

        if (helper.Index == Dinorant)
        {
            int dec = rules.DinorantDecDamageConstA + ((helper.NewOption & 1) == 0 ? 0 : rules.DinorantDecDamageConstB);
            return damage * (100 - dec) / 100;
        }

        if (helper.Index == Item.DarkHorse)
        {
            // The higher its level, the more of the blow the horse takes: (A + level) / B percent.
            int dec = rules.DarkHorseDecDamageConstB == 0 ? 0 : (rules.DarkHorseDecDamageConstA + helper.PetLevel) / rules.DarkHorseDecDamageConstB;
            return damage * (100 - dec) / 100;
        }

        return damage;
    }

    /// <summary>Attacker side of WingSprite: first wings +12% (+2% per level), second wings +32% (+1% per level);
    /// each hit costs 1 life (Dark Wizard, Elf) or 3 (the others).</summary>
    private static int WingBonus(PlayerObject player, int damage, ref int lifeLost)
    {
        var wing = player.Items[Item.SlotWing];

        if (!wing.IsItem() || wing.Durability == 0)
        {
            return damage;
        }

        lifeLost += player.Class is 0 or 2 ? 1 : 3;

        if (wing.Index >= Item.GetItem(12, 0) && wing.Index <= Item.GetItem(12, 2))
        {
            return damage * (112 + (wing.Level * 2)) / 100;
        }

        if (wing.Index >= Item.GetItem(12, 3) && wing.Index <= Item.GetItem(12, 6))
        {
            return damage * (132 + wing.Level) / 100;
        }

        if (wing.Index == Item.GetItem(13, 30)) // Cape of Lord
        {
            return damage * (120 + (wing.Level * 2)) / 100;
        }

        return damage;
    }

    /// <summary>Attacker side of HelperSprite: the Imp (+SatanIncDamageConstA%, 3 life per hit) and the Horn of
    /// Dinorant (+DinorantIncDamageConstA%, 1 life per hit).</summary>
    private static int PetBonus(PlayerObject player, int damage, CombatRules rules, ref int lifeLost)
    {
        var helper = player.Items[Item.SlotHelper];

        if (!helper.IsItem() || helper.Durability == 0)
        {
            return damage;
        }

        if (helper.Index == Satan)
        {
            lifeLost += 3;
            return damage * (100 + rules.SatanIncDamageConstA) / 100;
        }

        if (helper.Index == Dinorant)
        {
            lifeLost += 1;
            return damage * (100 + rules.DinorantIncDamageConstA) / 100;
        }

        return damage;
    }

    /// <summary>CItem::m_CurrentDurabilityState (Item.cpp:198-219): a worn weapon's magic rise drops to 80%..50%
    /// as its durability goes under 50%..20% of the maximum.</summary>
    internal static double DurabilityState(Item item, ItemBalance info)
    {
        int max = ItemCombatMath.GetItemDurability(item, info);

        if (max <= 0 || item.Durability == 0)
        {
            return item.Durability == 0 ? 0.0 : 1.0;
        }

        double dur = item.Durability;

        if (dur < max * 0.2) return 0.5;
        if (dur < max * 0.3) return 0.6;
        if (dur < max * 0.4) return 0.7;
        if (dur < max * 0.5) return 0.8;
        return 1.0;
    }
}

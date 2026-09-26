using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>CharacterCalcAttribute with item options, and the damage of CAttack for a player hitting a monster.
/// Expected values are worked out by hand from ObjectManager.cpp and Attack.cpp with the factory balance.</summary>
public class CombatTests
{
    private ItemBalanceTable _items = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();
        CombatRules.Current = new CombatRules();
        _items = Items();
    }

    /// <summary>A Dark Knight (Strength 100: base damage 100/8=12 .. 100/4=25) with an excellent Kris with luck,
    /// +8 additional damage, "mana after hunting" and "excellent damage rate".</summary>
    private PlayerObject KnightWithExcellentKris()
    {
        var knight = DarkKnight();
        knight.Items[Item.SlotWeapon1] = Make(Kris, luck: 1, additional: 2, excellent: 1 | 32);
        knight.RecalcCombatStats(_items, Balance());
        return knight;
    }

    [Test]
    public void Item_options_reach_the_character()
    {
        var knight = KnightWithExcellentKris();

        // Right hand: 12+36 .. 25+41, plus the +8 additional option.
        Assert.That(knight.PhysiDamageMin, Is.EqualTo(56));
        Assert.That(knight.PhysiDamageMax, Is.EqualTo(74));
        Assert.That(knight.CriticalDamageRate, Is.EqualTo(5), "luck");
        Assert.That(knight.ExcellentDamageRate, Is.EqualTo(10));
        Assert.That(knight.HuntMp, Is.EqualTo(12));
    }

    [Test]
    public void Excellent_armour_options()
    {
        var knight = DarkKnight();
        knight.Items[Item.SlotArmor] = Make(BronzeArmor, excellent: 1 | 8 | 32);
        knight.RecalcCombatStats(_items, Balance());

        Assert.That(knight.MoneyAmountDropRate, Is.EqualTo(130));
        Assert.That(knight.DamageReduction, Is.EqualTo(4));
        // DK base life at level 1 with the class's base vitality is 110; +4% = 4.
        Assert.That(knight.AddLife, Is.EqualTo(4));
        Assert.That(knight.MaxLife, Is.EqualTo(114u));
    }

    [Test]
    public void Changing_gear_keeps_the_life_percentage()
    {
        var knight = DarkKnight();
        knight.RecalcCombatStats(_items, Balance());
        knight.Life = knight.MaxLife / 2; // 55 of 110

        knight.Items[Item.SlotArmor] = Make(BronzeArmor, excellent: 32);
        knight.RecalcCombatStats(_items, Balance());

        Assert.That(knight.MaxLife, Is.EqualTo(114u));
        Assert.That(knight.Life, Is.EqualTo(57u));
    }

    [Test]
    public void Critical_hit_is_the_maximum_damage()
    {
        var knight = KnightWithExcellentKris();
        // rolls: ignore defense (no), damage (min), critical (yes: 0 < 5), excellent (no)
        var hit = PlayerAttackMath.HitMonster(knight, 10, 0, 0, 0, false, _items, new ScriptedRandom(99, 0, 0, 99));

        Assert.That(hit.Effect, Is.EqualTo(PlayerAttackMath.EffectCritical));
        Assert.That(hit.Damage, Is.EqualTo(74 - 10));
    }

    [Test]
    public void Excellent_hit_is_120_percent_of_the_maximum()
    {
        var knight = KnightWithExcellentKris();
        var hit = PlayerAttackMath.HitMonster(knight, 10, 0, 0, 0, false, _items, new ScriptedRandom(99, 0, 99, 0));

        Assert.That(hit.Effect, Is.EqualTo(PlayerAttackMath.EffectExcellent));
        Assert.That(hit.Damage, Is.EqualTo((74 * 120 / 100) - 10));
    }

    [Test]
    public void Ignoring_defense()
    {
        var knight = KnightWithExcellentKris();
        knight.IgnoreDefenseRate = 100;
        var hit = PlayerAttackMath.HitMonster(knight, 10, 0, 0, 0, false, _items, new ScriptedRandom(0, 0, 99, 99));

        Assert.That(hit.Effect, Is.EqualTo(PlayerAttackMath.EffectIgnoreDefense));
        Assert.That(hit.Damage, Is.EqualTo(56));
    }

    [Test]
    public void A_graze_hits_for_30_percent_after_the_defense()
    {
        var knight = KnightWithExcellentKris();
        var hit = PlayerAttackMath.HitMonster(knight, 10, 0, 0, 0, true, _items, new ScriptedRandom(99, 0, 99, 99));

        Assert.That(hit.Damage, Is.EqualTo((56 - 10) * 30 / 100));
    }

    [Test]
    public void Dark_knight_skills_use_the_energy_multiplier()
    {
        var knight = KnightWithExcellentKris(); // Energy 30: 200 + 30/10 = 203%
        const int twistingSlash = 41;

        var hit = PlayerAttackMath.HitMonster(knight, 10, twistingSlash, 50, 60, false, _items, new ScriptedRandom(99, 0, 99, 99));

        Assert.That(knight.DkDamageMultiplierRate, Is.EqualTo(203));
        Assert.That(hit.Damage, Is.EqualTo((56 + 50 - 10) * 203 / 100));
    }

    [Test]
    public void Wings_add_damage_and_cost_life()
    {
        var knight = DarkKnight();
        knight.Items[Item.SlotWing] = Make(WingsOfSatan, durability: 200);
        knight.RecalcCombatStats(_items, Balance());

        var hit = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, _items, new ScriptedRandom(99, 0, 99, 99));

        Assert.That(hit.Damage, Is.EqualTo(12 * 112 / 100));
        Assert.That(hit.AttackerLifeLost, Is.EqualTo(3));
    }

    [Test]
    public void A_monster_hit_is_reduced_by_the_option_and_the_wings()
    {
        var knight = DarkKnight();
        knight.Items[Item.SlotWing] = Make(WingsOfSpirit, durability: 200);
        knight.DamageReduction = 4;

        Assert.That(PlayerAttackMath.ReduceMonsterHit(knight, 100), Is.EqualTo(96 * 75 / 100));
    }

    [Test]
    public void Full_armour_set_bonus()
    {
        var knight = DarkKnight();
        int model = 0; // the Bronze set: sub-index 0 of sections 7-11

        for (int section = 7; section <= 11; section++)
        {
            _items.Add(new ItemBalance { Index = Item.GetItem(section, model), Section = section, Sub = model, Slot = section - 5, Level = 20, Defense = 20, DefenseSuccessRate = section == 7 ? 0 : 5, Durability = 40 });
            knight.Items[section - 5] = Make(Item.GetItem(section, model), level: 10, durability: 40);
        }

        knight.RecalcCombatStats(_items, Balance());

        Assert.That(knight.ArmorSetBonus, Is.True);
        // Each piece: 20 + 10*3 + 1 = 51 defense (x5 = 255) plus Dexterity 20 / 3 = 6; five pieces at +10: +5%.
        Assert.That(knight.Defense, Is.EqualTo(261 + (261 * 5 / 100)));
    }
}

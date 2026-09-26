using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The stat part of CItem::Convert: excellent bonuses, durability and requirements. Expected values are
/// worked out by hand from Item.cpp.</summary>
public class ExcellentStatsTests
{
    private ItemBalanceTable _items = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();
        _items = Items();
    }

    [Test]
    public void Excellent_weapon_damage()
    {
        var info = _items.Get(Kris)!;

        // Kris: 6-11, item level 6. Excellent bonus = (6*25)/6 + 5 = 30 on both ends (the original uses the
        // minimum for both).
        Assert.That(ItemCombatMath.GetDamageMin(Make(Kris), info), Is.EqualTo(6));
        Assert.That(ItemCombatMath.GetDamageMin(Make(Kris, excellent: 1), info), Is.EqualTo(36));
        Assert.That(ItemCombatMath.GetDamageMax(Make(Kris, excellent: 1), info), Is.EqualTo(41));
        Assert.That(ItemCombatMath.GetDamageMax(Make(Kris, level: 3, excellent: 1), info), Is.EqualTo(50), "+3 adds 9");
        Assert.That(ItemCombatMath.GetDamageMin(Make(Kris, level: 10, excellent: 1), info), Is.EqualTo(36 + 30 + 1), "+10 adds 30 and 1 extra");
        Assert.That(ItemCombatMath.GetDamageMin(Make(Kris, excellent: 1, durability: 0), info), Is.EqualTo(0), "broken");
    }

    [Test]
    public void Excellent_armour_defense()
    {
        var info = _items.Get(BronzeArmor)!;

        // Defense 20, item level 20: + (20*12)/20 + 20/5 + 4 = 20.
        Assert.That(ItemCombatMath.GetDefense(Make(BronzeArmor), info), Is.EqualTo(20));
        Assert.That(ItemCombatMath.GetDefense(Make(BronzeArmor, excellent: 8), info), Is.EqualTo(40));
    }

    [Test]
    public void Second_wings_scale_defense_by_two_per_level_and_get_no_excellent_bonus()
    {
        var info = _items.Get(WingsOfSpirit)!;

        Assert.That(ItemCombatMath.GetDefense(Make(WingsOfSpirit, level: 3), info), Is.EqualTo(36));
        Assert.That(ItemCombatMath.GetDefense(Make(WingsOfSpirit, level: 3, excellent: 1), info), Is.EqualTo(36));
    }

    [Test]
    public void Excellent_items_last_longer_but_wings_do_not()
    {
        Assert.That(ItemCombatMath.GetItemDurability(Make(Kris), _items.Get(Kris)!), Is.EqualTo(20));
        Assert.That(ItemCombatMath.GetItemDurability(Make(Kris, excellent: 4), _items.Get(Kris)!), Is.EqualTo(35));
        Assert.That(ItemCombatMath.GetItemDurability(Make(WingsOfSpirit, excellent: 1), _items.Get(WingsOfSpirit)!), Is.EqualTo(200));
    }

    [Test]
    public void Requirements_of_excellent_items_and_of_the_additional_option()
    {
        var info = _items.Get(Kris)!;

        // (40 * (0*3 + 6) * 3) / 100 + 20 = 27; excellent counts 25 levels more: (40*31*3)/100 + 20 = 57.
        Assert.That(ItemCombatMath.GetRequireStrength(Make(Kris), info), Is.EqualTo(27));
        Assert.That(ItemCombatMath.GetRequireStrength(Make(Kris, excellent: 1), info), Is.EqualTo(57));
        // The additional option adds 4 strength per step.
        Assert.That(ItemCombatMath.GetRequireStrength(Make(Kris, additional: 2, excellent: 1), info), Is.EqualTo(65));
    }
}

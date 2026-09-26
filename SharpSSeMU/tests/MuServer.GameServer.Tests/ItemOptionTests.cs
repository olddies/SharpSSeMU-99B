using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>Data/Item/ItemOption.txt and the special options of CItem::Convert.</summary>
public class ItemOptionTests
{
    private ItemOptionTable _rules = null!;
    private ItemBalanceTable _items = null!;

    [SetUp]
    public void SetUp()
    {
        _rules = LoadRules();
        _items = Items();
        ItemCombatMath.Options = _rules;
    }

    [Test]
    public void Load_reads_every_row_including_item_ranges_and_wildcards()
    {
        Assert.That(_rules.Count, Is.EqualTo(18));

        var luck = _rules.Find(SpecialSlot.Option2, Make(Kris, luck: 1));
        Assert.That(luck, Is.Not.Null);
        Assert.That(luck!.OptionIndex, Is.EqualTo(ItemOptionIndex.AddCriticalDamageRate));
        Assert.That(luck.ItemMinIndex, Is.EqualTo(Item.GetItem(0, 0)));
        Assert.That(luck.ItemMaxIndex, Is.EqualTo(Item.GetItem(4, 31)));
        Assert.That(luck.ItemOption1, Is.EqualTo(-1));
    }

    [Test]
    public void A_rule_needs_its_flag_on_the_item()
    {
        Assert.That(_rules.Find(SpecialSlot.Option2, Make(Kris)), Is.Null, "no luck flag");
        Assert.That(_rules.Find(SpecialSlot.Excellent1, Make(Kris, excellent: 2)), Is.Null, "a different excellent bit");
        Assert.That(_rules.Find(SpecialSlot.Option2, Make(BronzeArmor, luck: 1))!.OptionIndex, Is.EqualTo(ItemOptionIndex.AddCriticalDamageRate));
    }

    [Test]
    public void Specials_of_an_excellent_weapon_with_luck_and_additional_option()
    {
        var kris = Make(Kris, skill: 1, luck: 1, additional: 2, excellent: 1 | 32);

        var specials = _rules.GetSpecials(kris, _items.Get(Kris));

        Assert.That(specials, Is.EquivalentTo(new[]
        {
            new ItemSpecial(SpecialSlot.Option1, 0, 0), // Kris teaches no skill
            new ItemSpecial(SpecialSlot.Option2, ItemOptionIndex.AddCriticalDamageRate, 5),
            new ItemSpecial(SpecialSlot.Option3, ItemOptionIndex.AddPhysiDamage, 8), // 4 per step, +8 at step 2
            new ItemSpecial(SpecialSlot.Excellent1, ItemOptionIndex.AddHuntMp, 12),
            new ItemSpecial(SpecialSlot.Excellent1 + 5, ItemOptionIndex.AddExcellentDamageRate, 10),
        }));
    }

    [Test]
    public void The_last_matching_row_wins_so_second_wings_pick_their_option_by_the_excellent_bit()
    {
        var damageWing = _rules.GetSpecials(Make(WingsOfSpirit, additional: 1), _items.Get(WingsOfSpirit));
        var lifeWing = _rules.GetSpecials(Make(WingsOfSpirit, additional: 1, excellent: 32), _items.Get(WingsOfSpirit));

        Assert.That(damageWing.Single(s => s.Slot == SpecialSlot.Option3).OptionIndex, Is.EqualTo(ItemOptionIndex.AddPhysiDamage));
        Assert.That(lifeWing.Single(s => s.Slot == SpecialSlot.Option3).OptionIndex, Is.EqualTo(ItemOptionIndex.AddHpRecoveryRate));
    }

    [Test]
    public void The_skill_needs_the_skill_flag_and_a_skill_row_for_that_item()
    {
        Assert.That(_rules.HasSkill(Make(Katana, skill: 1)), Is.True);
        Assert.That(_rules.HasSkill(Make(Katana)), Is.False, "no skill flag");
        Assert.That(_rules.HasSkill(Make(SkullStaff, skill: 1)), Is.False, "staffs have no skill row");

        var katanaSkill = _rules.GetSpecials(Make(Katana, skill: 1), _items.Get(Katana)).Single(s => s.Slot == SpecialSlot.Option1);
        Assert.That(katanaSkill.OptionIndex, Is.EqualTo(20), "the skill slot carries the item's skill");
    }
}

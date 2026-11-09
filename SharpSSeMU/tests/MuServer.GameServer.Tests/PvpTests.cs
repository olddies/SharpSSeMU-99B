using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>A player hitting another player (CAttack::Attack with two users): the defense is halved, what the target
/// wears reduces the blow, the PvP damage rates apply, and the hit roll comes from the PvP success rates. Expected
/// values are worked out by hand from Attack.cpp and ObjectManager.cpp with the factory balance.</summary>
public class PvpTests
{
    private ItemBalanceTable _items = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();
        CombatRules.Current = new CombatRules();
        _items = Items();
    }

    [TearDown]
    public void TearDown() => CombatRules.Current = new CombatRules();

    /// <summary>The bare-hand damage of the attacker at the lowest roll: Strength 400 / 8. High enough that no hit
    /// falls under the damage floor and needs one more roll.</summary>
    private const int BaseMin = 50;

    /// <summary>A Dark Knight of Strength 400: base damage 50..100, and a bare hand.</summary>
    private PlayerObject Attacker()
    {
        var knight = DarkKnight(strength: 400);
        knight.RecalcCombatStats(_items, Balance());
        return knight;
    }

    private PlayerObject Target(uint dexterity = 20, int wing = 0, int helper = 0)
    {
        var target = DarkKnight();
        target.Dexterity = dexterity;
        target.RecalcCombatStats(_items, Balance());

        // Worn after the stats are worked out, so the pieces' own defense does not change the hit being compared.
        if (wing != 0)
        {
            target.Items[Item.SlotWing] = Make(wing, durability: 200);
        }

        if (helper != 0)
        {
            target.Items[Item.SlotHelper] = Make(helper, durability: 255);
        }

        return target;
    }

    // rolls: ignore defense (no), damage (the minimum), critical (no), excellent (no)
    private static ScriptedRandom Plain() => new(99, 0, 99, 99);

    [Test]
    public void The_targets_defense_counts_for_half_as_much_as_a_monsters_would()
    {
        var attacker = Attacker();
        var target = Target();
        target.Items[Item.SlotArmor] = Make(BronzeArmor);
        target.RecalcCombatStats(_items, Balance());

        int halved = (int)target.Defense * 50 / 100;
        var vsPlayer = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain());
        var vsMonster = PlayerAttackMath.HitMonster(attacker, halved, 0, 0, 0, false, _items, Plain());

        Assert.That(vsPlayer.Damage, Is.EqualTo(vsMonster.Damage));
    }

    [Test]
    public void The_pvp_damage_rates_replace_the_pvm_ones()
    {
        // The minimum roll; the rates are 50% general, 80% for a Dark Knight and 90% against one.
        CombatRules.Current = new CombatRules
        {
            GeneralDamageRatePvP = 50,
            DamageRatePvP = new[] { 100, 80, 100, 100, 100 },
            DamageRateTo = new[] { new[] { 100, 100, 100, 100, 100 }, new[] { 100, 90, 100, 100, 100 }, new[] { 100, 100, 100, 100, 100 }, new[] { 100, 100, 100, 100, 100 }, new[] { 100, 100, 100, 100, 100 } },
            GeneralDamageRatePvM = 10,
        };

        var hit = PlayerAttackMath.HitPlayer(Attacker(), Target(), 0, 0, 0, false, _items, Plain());

        // The target's own defense (Dexterity 20 / 3 = 6) halves to 3 -> (50 - 3) * 50% * 80% * 90%.
        int afterDefense = BaseMin - (int)(Target().Defense * 50 / 100);
        Assert.That(hit.Damage, Is.EqualTo(afterDefense * 50 / 100 * 80 / 100 * 90 / 100));
    }

    [Test]
    public void Inside_the_chaos_castle_its_own_rate_applies_on_top()
    {
        CombatRules.Current = new CombatRules { ChaosCastleDamageRate = 50 };
        var attacker = Attacker();
        var target = Target();

        var outside = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain(), null, chaosCastle: false);
        var inside = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain(), null, chaosCastle: true);

        Assert.That(inside.Damage, Is.EqualTo(outside.Damage * 50 / 100));
    }

    [Test]
    public void In_a_duel_its_own_rate_applies_instead_of_the_chaos_castles()
    {
        CombatRules.Current = new CombatRules { ChaosCastleDamageRate = 50, DuelDamageRate = 60 };
        var attacker = Attacker();
        var target = Target();

        var plain = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain(), null);
        var duel = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain(), null, duel: true);
        var both = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain(), null, chaosCastle: true, duel: true);

        Assert.That(duel.Damage, Is.EqualTo(plain.Damage * 60 / 100));
        Assert.That(both.Damage, Is.EqualTo(duel.Damage), "a duel is not scaled by both rates");
    }

    [Test]
    public void The_targets_damage_reduction_option_comes_off_first()
    {
        var attacker = Attacker();
        var target = Target();
        target.DamageReduction = 50;

        var plain = PlayerAttackMath.HitPlayer(attacker, Target(), 0, 0, 0, false, _items, Plain());
        var reduced = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain());

        Assert.That(reduced.Damage, Is.EqualTo(plain.Damage - (plain.Damage * 50 / 100)));
    }

    [Test]
    public void Wings_on_the_target_soak_up_a_share_of_the_blow()
    {
        var attacker = Attacker();
        attacker.Items[Item.SlotWeapon1] = Make(Kris);
        attacker.RecalcCombatStats(_items, Balance());

        var bare = PlayerAttackMath.HitPlayer(attacker, Target(), 0, 0, 0, false, _items, Plain());
        var winged = PlayerAttackMath.HitPlayer(attacker, Target(wing: WingsOfSatan), 0, 0, 0, false, _items, Plain());
        var spirit = PlayerAttackMath.HitPlayer(attacker, Target(wing: WingsOfSpirit), 0, 0, 0, false, _items, Plain());

        Assert.That(winged.Damage, Is.EqualTo(bare.Damage * 88 / 100), "first wings take 12% off");
        Assert.That(spirit.Damage, Is.EqualTo(bare.Damage * 75 / 100), "second wings take 25% off");
    }

    [Test]
    public void The_cape_of_lord_is_worn_by_the_target_and_the_attacker()
    {
        int cape = Item.GetItem(13, 30);

        var attacker = Attacker();
        var target = Target();
        target.Items[Item.SlotWing] = Make(cape, level: 2, durability: 200);

        var bare = PlayerAttackMath.HitPlayer(attacker, Target(), 0, 0, 0, false, _items, Plain());
        var hitCape = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, _items, Plain());
        Assert.That(hitCape.Damage, Is.EqualTo(bare.Damage * (90 - 2) / 100), "10% off, 1% less per level");

        var capeAttacker = Attacker();
        capeAttacker.Items[Item.SlotWing] = Make(cape, level: 2, durability: 200);
        var boosted = PlayerAttackMath.HitPlayer(capeAttacker, Target(), 0, 0, 0, false, _items, Plain());
        Assert.That(boosted.Damage, Is.EqualTo(bare.Damage * (120 + 4) / 100), "20% more, 2% more per level");
        Assert.That(boosted.AttackerLifeLost, Is.EqualTo(3), "the cape costs life like any wings");
    }

    [Test]
    public void A_horn_of_dinorant_on_the_target_reduces_the_blow_too()
    {
        var attacker = Attacker();
        var bare = PlayerAttackMath.HitPlayer(attacker, Target(), 0, 0, 0, false, _items, Plain());
        var mounted = PlayerAttackMath.HitPlayer(attacker, Target(helper: Item.GetItem(13, 3)), 0, 0, 0, false, _items, Plain());

        Assert.That(mounted.Damage, Is.EqualTo(bare.Damage * (100 - CombatRules.Current.DinorantDecDamageConstA) / 100));
    }

    [Test]
    public void The_monsters_hit_on_a_player_still_gets_the_same_reductions()
    {
        var target = Target(wing: WingsOfSatan);
        target.DamageReduction = 10;

        // 100 -> 90 (the option) -> 79 (first wings: 88%).
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(90 * 88 / 100));

        target.Items[Item.SlotWing] = Make(Item.GetItem(13, 30), level: 3, durability: 200);
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(90 * (90 - 3) / 100), "and now the cape counts as well");
    }

    // ---------------------------------------------------------------- the hit roll

    [Test]
    public void The_pvp_success_rates_come_from_the_level_and_the_dexterity()
    {
        var knight = DarkKnight();
        knight.Level = 100;
        knight.Dexterity = 200;
        var rules = CombatRules.Current;

        // Dark Knight: attack Level*3/1 + Dexterity*45/10; defense Level*2/1 + Dexterity/2.
        Assert.That(PlayerAttackMath.PvpAttackSuccessRate(knight, rules), Is.EqualTo((100 * 3 / 1) + (200 * 45 / 10)));
        Assert.That(PlayerAttackMath.PvpDefenseSuccessRate(knight, rules), Is.EqualTo((100 * 2 / 1) + (200 / 2)));
    }

    [Test]
    public void Equal_players_land_most_of_their_blows_and_a_much_weaker_one_misses_more()
    {
        var even = DarkKnight();
        even.Level = 100;
        even.Dexterity = 100;
        var rival = DarkKnight();
        rival.Level = 100;
        rival.Dexterity = 100;

        // Attack 100*3 + 100*45/10 = 750; defense 100*2 + 100/2 = 250: 100 * (750/1000) * 2 * (100/200) = 75%.
        int hits = Enumerable.Range(0, 100).Count(roll => PlayerAttackMath.PvpBlowLands(even, rival, new ScriptedRandom(roll)));
        Assert.That(hits, Is.EqualTo(76), "a roll up to 75 lands: 0-75");

        // The same attacker against someone far stronger in level: the share drops with the level ratio.
        rival.Level = 300;
        int weaker = Enumerable.Range(0, 100).Count(roll => PlayerAttackMath.PvpBlowLands(even, rival, new ScriptedRandom(roll)));
        Assert.That(weaker, Is.LessThan(hits));
    }

    [Test]
    public void A_target_a_hundred_levels_higher_costs_five_points_of_the_hit_rate()
    {
        var low = DarkKnight();
        low.Level = 100;
        low.Dexterity = 100;
        var high = DarkKnight();
        high.Level = 200;
        high.Dexterity = 100;

        // Level ratio 100/300, defense 200*2 + 50 = 450: 100 * (750/1200) * 2 * (1/3) = 41.66 -> 41, minus 5 = 36.
        int hits = Enumerable.Range(0, 100).Count(roll => PlayerAttackMath.PvpBlowLands(low, high, new ScriptedRandom(roll)));
        Assert.That(hits, Is.EqualTo(37), "a roll up to 36 lands");
    }
}

using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The combo of the Dark Knight, the Magic Gladiator and the Dark Lord: which skills make it, how the chain is followed, who may
/// do it and what it adds to a blow. Worked out by hand from ComboSkill.cpp, CustomComboSkill.cpp, SkillManager.cpp:786 and Attack.cpp:357.</summary>
public class ComboTests
{
    private const int Falling = 19, Lunge = 20, Cyclone = 22, Twisting = 41, Rageful = 42, DeathStab = 43, Dk = 1;

    private const string File = """
        // Class   SkillIndex   Stage   Delay
           1       19           0       3000
           1       20           0       3000
           1       22           0       3000
           1       41           1       3000
           1       42           1       3000
           1       43           1       2000
        end
        """;

    private static ComboSkillTable Table()
    {
        string path = Path.Combine(Path.GetTempPath(), $"combo-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(path, File);

        try
        {
            var table = new ComboSkillTable();
            table.Load(path);
            return table;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [SetUp]
    public void Setup()
    {
        ItemCombatMath.Options = LoadRules();
        CombatRules.Current = new CombatRules();
        ComboRules.Current = new ComboRules();
    }

    // ---------------------------------------------------------------- the file

    [Test]
    public void The_file_gives_each_class_the_skills_that_start_and_finish_its_combo()
    {
        var table = Table();

        Assert.That(table.Count, Is.EqualTo(6));
        Assert.That(table.Get(Dk, Falling), Is.EqualTo((0, 3000)), "starts");
        Assert.That(table.Get(Dk, Twisting), Is.EqualTo((1, 3000)), "finishes");
        Assert.That(table.Get(Dk, DeathStab), Is.EqualTo((1, 2000)), "with its own delay");
        Assert.That(table.Get(0, Falling), Is.Null, "a Dark Wizard has no combo in this file");
        Assert.That(table.Get(Dk, 4), Is.Null, "nor does a skill outside it");
    }

    [Test]
    public void A_class_outside_the_five_is_not_loaded()
    {
        var table = new ComboSkillTable();
        table.Add(9, 19, 0, 3000);

        Assert.That(table.Count, Is.EqualTo(0));
    }

    // ---------------------------------------------------------------- the chain

    [Test]
    public void Three_skills_in_time_with_a_different_third_make_a_combo()
    {
        var table = Table();
        var state = new ComboState();

        Assert.That(state.CheckCombo(table, Dk, Falling, 1000), Is.False, "the start");
        Assert.That(state.CheckCombo(table, Dk, Twisting, 2000), Is.False, "the second");
        Assert.That(state.CheckCombo(table, Dk, Rageful, 3000), Is.True, "the third, a different finisher: the combo");
    }

    [Test]
    public void A_finisher_repeated_is_not_a_combo_and_breaks_the_chain()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);
        state.CheckCombo(table, Dk, Twisting, 2000);

        Assert.That(state.CheckCombo(table, Dk, Twisting, 3000), Is.False, "the same twice");
        Assert.That(state.CheckCombo(table, Dk, Rageful, 3500), Is.False, "and the chain is gone: this one has nothing to follow");
    }

    [Test]
    public void A_skill_that_comes_too_late_breaks_the_chain()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);                                       // until 4000
        Assert.That(state.CheckCombo(table, Dk, Twisting, 4001), Is.False, "a millisecond late");
        Assert.That(state.CheckCombo(table, Dk, Rageful, 4002), Is.False, "nothing is left of the chain");
    }

    [Test]
    public void The_delay_of_each_step_is_the_skills_own()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 0);
        state.CheckCombo(table, Dk, Rageful, 1000);      // second: until 1000 + 3000
        Assert.That(state.CheckCombo(table, Dk, DeathStab, 4000), Is.True, "in time at the edge");

        state.CheckCombo(table, Dk, Lunge, 10_000);
        state.CheckCombo(table, Dk, Twisting, 11_000);   // until 14000
        Assert.That(state.CheckCombo(table, Dk, DeathStab, 14_001), Is.False, "too late");
    }

    [Test]
    public void A_finisher_cannot_start_a_combo()
    {
        var table = Table();
        var state = new ComboState();

        Assert.That(state.CheckCombo(table, Dk, Twisting, 1000), Is.False);
        Assert.That(state.CheckCombo(table, Dk, Rageful, 1100), Is.False);
        Assert.That(state.CheckCombo(table, Dk, DeathStab, 1200), Is.False, "no start, so no combo");
    }

    [Test]
    public void Another_skill_in_the_middle_breaks_the_chain()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);
        state.CheckCombo(table, Dk, 4, 1500); // Fire Ball: not part of any combo
        Assert.That(state.CheckCombo(table, Dk, Twisting, 2000), Is.False, "the start was forgotten");
    }

    [Test]
    public void A_new_start_in_the_middle_begins_over()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);
        state.CheckCombo(table, Dk, Twisting, 1500);
        state.CheckCombo(table, Dk, Lunge, 2000);                                     // starts again
        Assert.That(state.CheckCombo(table, Dk, Rageful, 2500), Is.False, "this is the second of the new chain");
        Assert.That(state.CheckCombo(table, Dk, DeathStab, 3000), Is.True);
    }

    [Test]
    public void A_plain_blow_forgets_the_chain()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);
        state.CheckCombo(table, Dk, Twisting, 1500);
        state.Init(); // CGAttackRecv

        Assert.That(state.CheckCombo(table, Dk, Rageful, 2000), Is.False);
    }

    [Test]
    public void After_a_combo_the_chain_starts_from_nothing()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, Dk, Falling, 1000);
        state.CheckCombo(table, Dk, Twisting, 1500);
        Assert.That(state.CheckCombo(table, Dk, Rageful, 2000), Is.True);
        Assert.That(state.CheckCombo(table, Dk, DeathStab, 2500), Is.False, "the finishers do not run on");
    }

    [Test]
    public void A_class_with_no_rows_never_combos()
    {
        var table = Table();
        var state = new ComboState();

        state.CheckCombo(table, 0, Falling, 1000);
        state.CheckCombo(table, 0, Twisting, 1500);
        Assert.That(state.CheckCombo(table, 0, Rageful, 2000), Is.False);
    }

    // ---------------------------------------------------------------- who may

    private static byte[] Quests(params (int Index, int State)[] states)
    {
        var blob = new byte[50];

        foreach (var (index, state) in states)
        {
            blob[index / 4] |= (byte)(state << ((index % 4) * 2));
        }

        return blob;
    }

    [Test]
    public void The_Magic_Gladiator_and_the_Dark_Lord_may_always_the_others_after_their_quest()
    {
        var gladiator = DarkKnight();
        gladiator.Class = 3;
        var lord = DarkKnight();
        lord.Class = 4;
        var knight = DarkKnight();
        var wizard = DarkKnight();
        wizard.Class = 0;

        Assert.That(ComboRules.CanCombo(gladiator), Is.True);
        Assert.That(ComboRules.CanCombo(lord), Is.True);
        Assert.That(ComboRules.CanCombo(knight), Is.False, "a Dark Knight who has not finished his quest");

        knight.Quest = Quests((3, 2));
        Assert.That(ComboRules.CanCombo(knight), Is.True, "the Dark Knight's quest is the third");

        knight.Quest = Quests((3, 1));
        Assert.That(ComboRules.CanCombo(knight), Is.False, "accepted is not finished");

        wizard.Quest = Quests((3, 2));
        Assert.That(ComboRules.CanCombo(wizard), Is.False, "the others look at the second quest, not the third");

        wizard.Quest = Quests((2, 2));
        Assert.That(ComboRules.CanCombo(wizard), Is.True);
    }

    // ---------------------------------------------------------------- what it adds

    [Test]
    public void The_combo_doubles_the_blow_adds_a_part_of_the_stats_and_scales_it()
    {
        var knight = DarkKnight(strength: 100, energy: 30); // dexterity 20: 150 in all
        knight.RecalcCombatStats(Items(), Balance());

        var rules = new ComboRules { DamageConstA = new[] { 2, 2, 2, 2, 2 }, DamageConstB = new[] { 1, 1, 1, 1, 1 }, DamageConstC = new[] { 100, 100, 100, 100, 100 } };

        Assert.That(rules.Apply(knight, 100), Is.EqualTo(100 + 100 + (150 / 2) * 1), "275");

        var scaled = new ComboRules { DamageConstA = new[] { 2, 2, 2, 2, 2 }, DamageConstB = new[] { 3, 3, 3, 3, 3 }, DamageConstC = new[] { 50, 50, 50, 50, 50 } };
        Assert.That(scaled.Apply(knight, 100), Is.EqualTo((200 + (150 / 2) * 3) * 50 / 100), "212");
    }

    [Test]
    public void A_zero_divisor_adds_nothing_instead_of_failing()
    {
        var knight = DarkKnight();
        knight.RecalcCombatStats(Items(), Balance());
        var rules = new ComboRules { DamageConstA = new[] { 0, 0, 0, 0, 0 } };

        Assert.That(rules.Apply(knight, 100), Is.EqualTo(200));
    }

    [Test]
    public void A_combo_blow_hits_harder_and_carries_the_combo_flag()
    {
        var knight = DarkKnight(strength: 100, energy: 30);
        knight.Items[Item.SlotWeapon1] = Make(Kris);
        knight.RecalcCombatStats(Items(), Balance());

        var plain = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, Items(), new Random(5));
        var combo = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, Items(), new Random(5), combo: true);

        Assert.That(combo.Damage, Is.EqualTo(ComboRules.Current.Apply(knight, plain.Damage)), "the same blow, then the combo");
        Assert.That(combo.Damage, Is.GreaterThan(plain.Damage));
        Assert.That(combo.Effect & PlayerAttackMath.EffectCombo, Is.EqualTo(PlayerAttackMath.EffectCombo));
        Assert.That(plain.Effect & PlayerAttackMath.EffectCombo, Is.EqualTo(0));
    }
}

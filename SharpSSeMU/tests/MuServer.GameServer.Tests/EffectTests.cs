using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The buffs and debuffs (EffectManager) and the skills that cast the helpful ones: how EffectList.txt is read, what an effect
/// does while it lasts, how it ends, what the database keeps, and the numbers of the skills. Worked out by hand from EffectManager.cpp,
/// ObjectManager.cpp and SkillManager.cpp with the shipped constants.</summary>
public class EffectTests
{
    private const string List = """
        // Index   Group   ItemIndex    Name                                 Save   Type   Flag   Duration   Power1   Power2   Power3   Power4
           1       1       *            "Greater Damage"                     1      0      0      *          *        *        *        *
           2       2       *            "Greater Defense"                    1      0      0      *          *        *        *        *
           4       4       *            "Mana Shield"                        1      0      0      *          *        *        *        *
           5       5       *            "Greater Critical Damage"            1      0      0      *          *        *        *        *
           8       8       *            "Greater Life"                       1      0      0      *          *        *        *        *
          20       20      *            "Fixed"                              0      0      0      30         7        *        *        *
          21       21      *            "Clock"                              1      2      0      600        *        *        *        *
          55       27      *            "Poison"                             0      0      1      *          *        *        *        *
          56       28      *            "Ice"                                0      0      1      *          *        *        *        *
          58       30      *            "Fire Slash"                         1      0      1      *          *        *        *        *
        end
        """;

    [SetUp]
    public void Setup()
    {
        ItemCombatMath.Options = LoadRules();
        CombatRules.Current = new CombatRules();
    }

    private sealed class Harness
    {
        public PlayerRegistry Players { get; } = new();
        public EffectManager Effects { get; }
        public long Now { get; set; } = 1_000_000;
        public ItemBalanceTable Items { get; } = TestWorld.Items();
        public int StatChanges { get; private set; }

        public Harness(int overwriteMode = 0)
        {
            string path = Path.Combine(Path.GetTempPath(), $"effects-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, List);
            var table = new EffectTable();
            table.Load(path);
            File.Delete(path);

            Effects = new EffectManager(table, Players)
            {
                OverwriteMode = overwriteMode,
                Clock = () => Now,
                OnStatsChanged = (player, _) =>
                {
                    StatChanges++;
                    player.RecalcCombatStats(Items, Balance());
                    return Task.CompletedTask;
                },
            };
        }

        public PlayerObject Add(int index = 9000, int map = 0)
        {
            var player = new PlayerObject
            {
                Index = index, Session = FakeSession.Create(), Account = "acc", Name = "P" + index, Class = 1, Level = 1,
                Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, WorldEntered = true, Map = (byte)map, X = 100, Y = 100,
            };
            player.RecalcCombatStats(Items, Balance());
            Players.Add(player);
            return player;
        }

        public Task<bool> Add(PlayerObject player, int effect, int count = 60, ushort value = 5) =>
            Effects.AddAsync(player, false, effect, count, value, 0, 0, 0, CancellationToken.None);
    }

    private static FakeSession Session(PlayerObject player) => (FakeSession)player.Session;

    // ---------------------------------------------------------------- the list

    [Test]
    public void The_effect_list_is_read_as_the_file_shows_it()
    {
        var h = new Harness();
        var effects = h.Effects;
        var player = h.Add();

        Assert.That(h.Add(player, EffectIds.ManaShield).Result, Is.True);
        Assert.That(effects.Get(player, EffectIds.ManaShield)!.Group, Is.EqualTo(4));
        Assert.That(effects.Get(player, EffectIds.ManaShield)!.Save, Is.EqualTo(1));

        Assert.That(h.Add(player, EffectIds.Poison).Result, Is.True);
        Assert.That(effects.Get(player, EffectIds.Poison)!.Flag, Is.EqualTo(1), "a debuff");
        Assert.That(effects.Get(player, EffectIds.Poison)!.Save, Is.EqualTo(0));

        Assert.That(h.Add(player, 99).Result, Is.False, "no such effect");
    }

    [Test]
    public void A_fixed_duration_and_power_of_the_list_take_over_the_skills()
    {
        var h = new Harness();
        var player = h.Add();

        h.Add(player, 20, count: 5, value: 99).Wait();

        var effect = h.Effects.Get(player, 20)!;
        Assert.That(effect.Count, Is.EqualTo(30u), "the list's 30 seconds, not the skill's 5");
        Assert.That(effect.Value[0], Is.EqualTo(7), "the list's power, not the skill's 99");
    }

    // ---------------------------------------------------------------- coming and going

    [Test]
    public async Task An_effect_tells_the_player_and_those_who_see_him_when_it_starts_and_ends()
    {
        var h = new Harness();
        var player = h.Add(9000);
        var watcher = h.Add(9001);
        var stranger = h.Add(9002);
        watcher.VisibleTo.Add(player.Index);

        await h.Add(player, EffectIds.GreaterDefense, count: 2, value: 20);

        foreach (var heard in new[] { player, watcher })
        {
            var packet = Session(heard).Of(0x07).Single();
            Assert.That(packet[3], Is.EqualTo(1), "starts");
            Assert.That(BitConverter.ToUInt16(packet, 4), Is.EqualTo(1 << 3), "the bit of Greater Defense");
            Assert.That((packet[6] << 8) | packet[7], Is.EqualTo(player.Index));
        }

        Assert.That(Session(stranger).Of(0x07), Is.Empty, "someone who does not see him is not told");

        await h.Effects.TickAsync(player, CancellationToken.None);
        await h.Effects.TickAsync(player, CancellationToken.None);

        var ended = Session(player).Of(0x07).Last();
        Assert.That(ended[3], Is.EqualTo(0), "ends");
        Assert.That(BitConverter.ToUInt16(ended, 4), Is.EqualTo(1 << 3));
        Assert.That(h.Effects.Check(player, EffectIds.GreaterDefense), Is.False);
        Assert.That(player.EffectBonus.AddDefense, Is.EqualTo(0));
    }

    [Test]
    public async Task Nobody_in_the_chaos_castle_sees_anothers_effects()
    {
        var h = new Harness();
        var player = h.Add(9000, map: 20);
        var watcher = h.Add(9001, map: 20);
        watcher.VisibleTo.Add(player.Index);

        await h.Add(player, EffectIds.GreaterDefense);

        Assert.That(Session(player).Of(0x07), Has.Count.EqualTo(1));
        Assert.That(Session(watcher).Of(0x07), Is.Empty);
    }

    [Test]
    public async Task An_effect_counts_down_a_second_at_a_time_and_goes_at_the_last()
    {
        var h = new Harness();
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDamage, count: 3, value: 7);

        await h.Effects.TickAsync(player, CancellationToken.None);
        await h.Effects.TickAsync(player, CancellationToken.None);
        Assert.That(h.Effects.Get(player, EffectIds.GreaterDamage)!.Count, Is.EqualTo(1u));

        await h.Effects.TickAsync(player, CancellationToken.None);
        Assert.That(h.Effects.Check(player, EffectIds.GreaterDamage), Is.False);
        Assert.That(player.EffectBonus.AddPhysiDamage, Is.EqualTo(0), "what it added goes with it");
    }

    [Test]
    public async Task A_stronger_effect_of_the_group_replaces_a_weaker_one_but_not_the_other_way_round()
    {
        var h = new Harness();
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDefense, value: 10);

        Assert.That(await h.Add(player, EffectIds.GreaterDefense, value: 6), Is.False, "weaker");
        Assert.That(h.Effects.Get(player, EffectIds.GreaterDefense)!.Value[0], Is.EqualTo(10));

        Assert.That(await h.Add(player, EffectIds.GreaterDefense, value: 14), Is.True, "stronger");
        Assert.That(h.Effects.Get(player, EffectIds.GreaterDefense)!.Value[0], Is.EqualTo(14));
        Assert.That(player.EffectBonus.AddDefense, Is.EqualTo(14), "replaced, not stacked");
    }

    [Test]
    public async Task The_overwrite_mode_lets_any_new_effect_replace_the_old_one()
    {
        var h = new Harness(overwriteMode: 1);
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDefense, value: 10);

        Assert.That(await h.Add(player, EffectIds.GreaterDefense, value: 6), Is.True);
        Assert.That(player.EffectBonus.AddDefense, Is.EqualTo(6));
    }

    [Test]
    public async Task Sixteen_effects_fill_the_slots_and_the_next_finds_none()
    {
        var h = new Harness();
        var player = h.Add();

        for (int n = 0; n < ActiveEffect.Slots; n++)
        {
            player.ActiveEffects[n].Set((byte)(100 + n), (byte)(100 + n), 0, 0, 0, 0, 60, 0, 0, 0, 0, h.Now);
        }

        Assert.That(await h.Add(player, EffectIds.GreaterDamage), Is.False);
    }

    [Test]
    public async Task Dying_ends_the_effects_that_die_with_the_player()
    {
        var h = new Harness();
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDamage);
        await h.Add(player, EffectIds.Poison);

        await h.Effects.ClearAllAsync(player, CancellationToken.None);

        Assert.That(player.ActiveEffects.Any(e => e.IsEffect), Is.False);
        Assert.That(player.EffectBonus.AddPhysiDamage, Is.EqualTo(0));
    }

    [Test]
    public async Task An_antidote_ends_the_debuffs_but_not_the_buffs()
    {
        var h = new Harness();
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDamage);
        await h.Add(player, EffectIds.Poison);
        await h.Add(player, EffectIds.Ice);

        await h.Effects.ClearDebuffsAsync(player, 1, CancellationToken.None);

        Assert.That(h.Effects.Check(player, EffectIds.Poison), Is.False, "the first debuff goes");
        Assert.That(h.Effects.Check(player, EffectIds.Ice), Is.True, "only one was asked for");
        Assert.That(h.Effects.Check(player, EffectIds.GreaterDamage), Is.True);
    }

    // ---------------------------------------------------------------- what they do

    [Test]
    public async Task Greater_damage_adds_to_what_a_blow_does()
    {
        var h = new Harness();
        var knight = h.Add();
        var plain = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 99, 99));

        await h.Add(knight, EffectIds.GreaterDamage, value: 7);
        var boosted = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 99, 99));

        Assert.That(boosted.Damage - plain.Damage, Is.EqualTo(7));
    }

    [Test]
    public async Task Greater_critical_damage_adds_to_a_critical_blow_only()
    {
        var h = new Harness();
        var knight = h.Add();
        knight.Items[Item.SlotWeapon1] = Make(Kris, luck: 1); // luck gives a 5% critical rate
        knight.RecalcCombatStats(h.Items, Balance());
        var plain = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 0, 99));

        await h.Add(knight, EffectIds.GreaterCriticalDamage, value: 9);
        var critical = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 0, 99));
        var normal = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 99, 99));

        Assert.That(plain.Effect, Is.EqualTo(PlayerAttackMath.EffectCritical));
        Assert.That(critical.Damage - plain.Damage, Is.EqualTo(9));
        Assert.That(normal.Effect, Is.Not.EqualTo(PlayerAttackMath.EffectCritical));
    }

    [Test]
    public async Task Greater_defense_adds_to_the_defense_a_hit_meets()
    {
        var h = new Harness();
        var attacker = h.Add(9000);
        attacker.Strength = 1000; // a blow that is not swallowed by the defense
        attacker.RecalcCombatStats(h.Items, Balance());
        var target = h.Add(9001);
        int baseDefense = (int)target.Defense;
        Assert.That(target.EffectiveDefense(), Is.EqualTo(baseDefense));
        var before = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 99, 99));

        await h.Add(target, EffectIds.GreaterDefense, value: 60);

        Assert.That(target.EffectiveDefense(), Is.EqualTo(baseDefense + 60));
        var after = PlayerAttackMath.HitPlayer(attacker, target, 0, 0, 0, false, h.Items, new ScriptedRandom(99, 0, 99, 99));
        Assert.That(after.Damage, Is.LessThan(before.Damage));
    }

    [Test]
    public async Task Fire_slash_takes_a_share_of_the_defense()
    {
        var h = new Harness();
        var target = h.Add();
        target.Dexterity = 600; // a defense worth taking a share of
        target.RecalcCombatStats(h.Items, Balance());
        int baseDefense = (int)target.Defense;
        Assert.That(baseDefense, Is.GreaterThan(100));

        await h.Add(target, EffectIds.FireSlash, value: 25);

        Assert.That(target.EffectiveDefense(), Is.EqualTo(baseDefense - (baseDefense * 25 / 100)));
    }

    [Test]
    public async Task Mana_shield_lets_less_of_a_monsters_hit_through()
    {
        var h = new Harness();
        var target = h.Add();
        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(100));

        await h.Add(target, EffectIds.ManaShield, value: 20);

        Assert.That(PlayerAttackMath.ReduceMonsterHit(target, 100), Is.EqualTo(80));
    }

    [Test]
    public async Task Greater_life_raises_the_maximum_life_by_a_share_and_keeps_the_percentage()
    {
        var h = new Harness();
        var knight = h.Add();
        Assert.That(knight.MaxLife, Is.EqualTo(110u));
        knight.Life = 55;

        await h.Add(knight, EffectIds.GreaterLife, value: 10);

        Assert.That(knight.MaxLife, Is.EqualTo(121u), "110 + 10%");
        Assert.That(knight.Life, Is.EqualTo(60u), "half of 121, as the percentage is kept");

        await h.Effects.DelAsync(knight, EffectIds.GreaterLife, CancellationToken.None);

        Assert.That(knight.MaxLife, Is.EqualTo(110u));
        Assert.That(knight.Life, Is.LessThanOrEqualTo(knight.MaxLife));
    }

    [Test]
    public async Task Every_effect_that_changes_the_stats_asks_for_them_to_be_worked_out_again()
    {
        var h = new Harness();
        var knight = h.Add();

        await h.Add(knight, EffectIds.GreaterDamage);
        Assert.That(h.StatChanges, Is.EqualTo(1));

        await h.Effects.DelAsync(knight, EffectIds.GreaterDamage, CancellationToken.None);
        Assert.That(h.StatChanges, Is.EqualTo(2));
    }

    [Test]
    public async Task The_viewport_shows_the_state_bits_of_a_players_effects()
    {
        var h = new Harness();
        var player = h.Add();
        var plain = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });

        await h.Add(player, EffectIds.GreaterDefense); // 1 << 3
        await h.Add(player, EffectIds.ManaShield);     // 1 << 8
        var dressed = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });

        Assert.That(dressed, Has.Length.EqualTo(plain.Length));
        var differences = Enumerable.Range(0, plain.Length).Where(i => plain[i] != dressed[i]).ToList();
        Assert.That(differences, Has.Count.EqualTo(2), "the two bytes of the effect word");
        Assert.That(differences[1], Is.EqualTo(differences[0] + 1));
        Assert.That(dressed[differences[0]], Is.EqualTo(1 << 3), "low byte");
        Assert.That(dressed[differences[1]], Is.EqualTo(1), "high byte");
    }

    // ---------------------------------------------------------------- the database

    [Test]
    public async Task The_effects_that_are_kept_are_written_as_thirteen_byte_records()
    {
        var h = new Harness();
        var player = h.Add();
        await h.Add(player, EffectIds.GreaterDamage, count: 63, value: 7);
        await h.Add(player, EffectIds.Poison, count: 10);
        await h.Add(player, EffectIds.FireSlash, count: 20, value: 12);

        var blob = new byte[208];
        EffectManager.Encode(player, blob);

        Assert.That(blob[0], Is.EqualTo(EffectIds.GreaterDamage));
        Assert.That(blob.AsSpan(1, 4).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 63 }), "the time left, big endian");
        Assert.That(blob.AsSpan(5, 2).ToArray(), Is.EqualTo(new byte[] { 0, 7 }), "the first power, big endian");
        Assert.That(blob.AsSpan(13, 13).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)0xFF, 13).ToArray()), "poison is not kept");
        Assert.That(blob[26], Is.EqualTo(EffectIds.FireSlash));
        Assert.That(blob.AsSpan(39).ToArray(), Is.EqualTo(Enumerable.Repeat((byte)0xFF, 169).ToArray()), "the empty slots");
    }

    [Test]
    public async Task What_the_database_kept_comes_back_with_its_time_and_its_power()
    {
        var h = new Harness();
        var first = h.Add(9000);
        await h.Add(first, EffectIds.GreaterDamage, count: 63, value: 7);
        await h.Add(first, EffectIds.Poison, count: 10);
        var blob = new byte[208];
        EffectManager.Encode(first, blob);

        var second = h.Add(9001);
        await h.Effects.RestoreAsync(second, blob, CancellationToken.None);

        var restored = h.Effects.Get(second, EffectIds.GreaterDamage)!;
        Assert.That(restored.Count, Is.EqualTo(63u));
        Assert.That(restored.Value[0], Is.EqualTo(7));
        Assert.That(second.EffectBonus.AddPhysiDamage, Is.EqualTo(7), "it works again");
        Assert.That(h.Effects.Check(second, EffectIds.Poison), Is.False);
    }

    [Test]
    public async Task An_empty_row_restores_nothing()
    {
        var h = new Harness();
        var player = h.Add();

        await h.Effects.RestoreAsync(player, new byte[208], CancellationToken.None); // zero index would be a real effect...
        Assert.That(player.ActiveEffects.Count(e => e.IsEffect), Is.EqualTo(0), "...but no effect has index 0");

        var blob = Enumerable.Repeat((byte)0xFF, 208).ToArray();
        await h.Effects.RestoreAsync(player, blob, CancellationToken.None);
        Assert.That(player.ActiveEffects.Any(e => e.IsEffect), Is.False);
    }

    [Test]
    public async Task A_clock_effect_keeps_the_moment_it_ends_and_comes_back_with_what_is_left()
    {
        var h = new Harness();
        var first = h.Add(9000);
        await h.Add(first, 21, count: 0);
        Assert.That(h.Effects.Get(first, 21)!.Count, Is.EqualTo(600u));

        var blob = new byte[208];
        EffectManager.Encode(first, blob);
        uint end = ((uint)blob[1] << 24) | ((uint)blob[2] << 16) | ((uint)blob[3] << 8) | blob[4];
        Assert.That(end, Is.EqualTo((uint)(h.Now + 600)), "the moment, not the seconds");

        h.Now += 100; // the player was away for a hundred seconds
        var second = h.Add(9001);
        await h.Effects.RestoreAsync(second, blob, CancellationToken.None);

        Assert.That(h.Effects.Get(second, 21)!.Count, Is.EqualTo(500u));

        // ... and an effect that ended while he was away does not come back.
        h.Now += 1000;
        var third = h.Add(9002);
        await h.Effects.RestoreAsync(third, blob, CancellationToken.None);
        Assert.That(h.Effects.Check(third, 21), Is.False);
    }

    // ---------------------------------------------------------------- the skills

    private static PlayerObject Caster(uint energy = 30, uint dexterity = 20, uint vitality = 25, uint leadership = 0) =>
        new()
        {
            Index = 9000, Session = FakeSession.Create(), Class = 2, Level = 100,
            Strength = 100, Dexterity = dexterity, Vitality = vitality, Energy = energy, Leadership = leadership,
        };

    [Test]
    public void Heal_gives_back_five_plus_a_fifth_of_the_energy()
    {
        Assert.That(SupportSkills.HealFor(Caster(energy: 30), new SupportSkillRules()), Is.EqualTo(5 + 30 / 5));
        Assert.That(SupportSkills.HealFor(Caster(energy: 300), new SupportSkillRules()), Is.EqualTo(5 + 300 / 5));
    }

    [Test]
    public void Greater_defense_and_greater_damage_grow_with_the_energy()
    {
        var rules = new SupportSkillRules();
        var caster = Caster(energy: 80);

        var (defense, defenseSeconds) = SupportSkills.GreaterDefenseFor(caster, caster, rules);
        Assert.That(defense, Is.EqualTo((2 + 80 / 8) * 2), "the effect carries twice the value");
        Assert.That(defenseSeconds, Is.EqualTo(60));

        var (damage, damageSeconds) = SupportSkills.GreaterDamageFor(caster, caster, rules);
        Assert.That(damage, Is.EqualTo(3 + 80 / 7));
        Assert.That(damageSeconds, Is.EqualTo(60));
    }

    [Test]
    public void The_class_of_the_target_scales_the_buffs()
    {
        var rules = new SupportSkillRules { GreaterDamageRate = new[] { 100, 50, 100, 100, 100 } };
        var caster = Caster(energy: 70);
        var knight = Caster();
        knight.Class = 1;

        Assert.That(SupportSkills.GreaterDamageFor(caster, caster, rules).Value, Is.EqualTo(13), "(3 + 10) at 100%");
        Assert.That(SupportSkills.GreaterDamageFor(caster, knight, rules).Value, Is.EqualTo(13 * 50 / 100));
    }

    [Test]
    public void Mana_shield_is_capped_and_lasts_longer_with_the_energy()
    {
        var rules = new SupportSkillRules();
        var caster = Caster(energy: 400, dexterity: 5000);

        var (value, seconds) = SupportSkills.ManaShieldFor(caster, caster, rules);
        Assert.That(value, Is.EqualTo(50), "10 + 100 + 2 is capped at the maximum rate");
        Assert.That(seconds, Is.EqualTo(60 + 400 / 40));

        var small = SupportSkills.ManaShieldFor(Caster(energy: 30, dexterity: 20), caster, rules);
        Assert.That(small.Value, Is.EqualTo(10), "10 + 0 + 0");
        Assert.That(small.Seconds, Is.EqualTo(60));
    }

    [Test]
    public void Greater_life_uses_vitality_and_energy_and_is_capped()
    {
        var rules = new SupportSkillRules();

        var (value, seconds) = SupportSkills.GreaterLifeFor(Caster(energy: 200, vitality: 500), rules);
        Assert.That(value, Is.EqualTo(12 + 500 / 100 + 200 / 20));
        Assert.That(seconds, Is.EqualTo(60 + 200 / 10));

        Assert.That(SupportSkills.GreaterLifeFor(Caster(energy: 10000, vitality: 10000), rules).Value, Is.EqualTo(100), "the cap");
    }

    [Test]
    public void Greater_critical_damage_uses_energy_and_leadership()
    {
        var rules = new SupportSkillRules();

        var (value, seconds) = SupportSkills.GreaterCriticalDamageFor(Caster(energy: 250, leadership: 600), rules);

        Assert.That(value, Is.EqualTo(250 / 25 + 600 / 30));
        Assert.That(seconds, Is.EqualTo(60 + 250 / 10));
    }

    [Test]
    public void A_skill_with_a_zero_constant_does_not_divide_by_zero()
    {
        var rules = new SupportSkillRules { HealConstB = 0, GreaterDamageConstB = 0, GreaterLifeConstB = 0, ManaShieldConstB = 0 };
        var caster = Caster();

        Assert.DoesNotThrow(() => SupportSkills.HealFor(caster, rules));
        Assert.DoesNotThrow(() => SupportSkills.GreaterDamageFor(caster, caster, rules));
        Assert.DoesNotThrow(() => SupportSkills.GreaterLifeFor(caster, rules));
        Assert.DoesNotThrow(() => SupportSkills.ManaShieldFor(caster, caster, rules));
    }

    [Test]
    public void Which_skills_help_and_which_of_them_need_a_target()
    {
        foreach (int skill in new[] { 16, 26, 27, 28, 48, 64 })
        {
            Assert.That(SupportSkills.IsSupport(skill), Is.True, $"skill {skill}");
        }

        Assert.That(SupportSkills.IsSupport(1), Is.False);
        Assert.That(SupportSkills.IsSupport(17), Is.False, "Energy Ball hurts");

        Assert.That(SupportSkills.IsTargeted(SupportSkills.Heal), Is.True);
        Assert.That(SupportSkills.IsTargeted(SupportSkills.GreaterLife), Is.False, "falls on the caster and his party");
        Assert.That(SupportSkills.IsTargeted(SupportSkills.GreaterCriticalDamage), Is.False);
    }

    // ---------------------------------------------------------------- where a skill may be cast

    [Test]
    public void A_safe_zone_allows_only_the_listed_skills_and_only_at_the_listed_places()
    {
        string path = Path.Combine(Path.GetTempPath(), $"skillarea-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, """
            0
               16           //Mana Shield
               26           //Heal
            end

            1
               11          008           003           019         014         //Blood Castle 1
            end
            """);
        var areas = new SkillUseAreaTable();
        areas.Load(path);
        File.Delete(path);

        Assert.That(areas.SkillCount, Is.EqualTo(2));
        Assert.That(areas.AreaCount, Is.EqualTo(1));
        Assert.That(areas.CheckUseSkill(11, 10, 5, 26), Is.True, "Heal at the Blood Castle's start");
        Assert.That(areas.CheckUseSkill(11, 10, 5, 17), Is.False, "Energy Ball is not on the list");
        Assert.That(areas.CheckUseSkill(11, 50, 50, 26), Is.False, "outside the area");
        Assert.That(areas.CheckUseSkill(0, 10, 5, 26), Is.False, "another map");
    }

    [Test]
    public void The_shipped_files_load()
    {
        // The game files are not part of the repository: where a developer has them (the MuServer99B folder next to
        // SharpSSeMU) they are checked, anywhere else the test is skipped.
        string? dir = null;

        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null && dir == null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, "MuServer99B", "Data", "Skill");
            dir = File.Exists(Path.Combine(candidate, "EffectList.txt")) ? candidate : null;
        }

        if (dir == null)
        {
            Assert.Ignore("the game data is not part of the repository");
        }

        var table = new EffectTable();
        Assert.That(table.Load(Path.Combine(dir, "EffectList.txt")), Is.GreaterThan(10));
        Assert.That(table.Get(EffectIds.ManaShield)!.Group, Is.EqualTo(4));
        Assert.That(table.Get(EffectIds.Poison)!.Flag, Is.EqualTo(1));
        Assert.That(table.Get(EffectIds.FireSlash)!.Save, Is.EqualTo(1));

        var areas = new SkillUseAreaTable();
        areas.Load(Path.Combine(dir, "SkillUseArea.txt"));
        Assert.That(areas.CheckUseSkill(11, 10, 5, SupportSkills.Heal), Is.True);
    }
}

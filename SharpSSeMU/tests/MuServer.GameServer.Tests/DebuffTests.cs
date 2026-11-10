using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The skills that leave something on the one they hit -- the poison, the ice, the arrow that roots, the slash that weakens --
/// and the effects on monsters. Worked out by hand from CAttack::ApplySkillEffect, CEffectManager::PeriodicEffect and
/// gObjCheckResistance with the shipped constants.</summary>
public class DebuffTests
{
    private const string List = """
        // Index   Group   ItemIndex    Name                                 Save   Type   Flag   Duration   Power1   Power2   Power3   Power4
          55       27      *            "Poison"                             0      0      1      *          *        *        *        *
          56       28      *            "Ice"                                0      0      1      *          *        *        *        *
          57       29      *            "Ice Arrow"                          0      0      1      *          *        *        *        *
          58       30      *            "Fire Slash"                         1      0      1      *          *        *        *        *
          59       31      *            "Physi Damage Immunity"              0      0      0      *          *        *        *        *
          60       32      *            "Magic Damage Immunity"              0      0      0      *          *        *        *        *
          63       35      *            "Monster Physi Damage Immunity"      0      0      0      *          *        *        *        *
          64       36      *            "Monster Magic Damage Immunity"      0      0      0      *          *        *        *        *
        end
        """;

    private sealed class Harness
    {
        public PlayerRegistry Players { get; } = new();
        public EffectManager Effects { get; }
        public List<(IEffectCarrier Carrier, byte Index, uint Count)> Periodic { get; } = new();

        public Harness()
        {
            string path = Path.Combine(Path.GetTempPath(), $"debuffs-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, List);
            var table = new EffectTable();
            table.Load(path);
            File.Delete(path);

            Effects = new EffectManager(table, Players)
            {
                OnPeriodic = (carrier, effect, _) =>
                {
                    Periodic.Add((carrier, effect.Index, effect.Count));
                    return Task.CompletedTask;
                },
            };
        }

        public Monster Monster(int index = 100)
        {
            var info = new MonsterSpawnEntry { Type = 0, MonsterClass = 1, Map = 0, X = 100, Y = 100 };
            return new Monster
            {
                Index = index, MonsterClass = 1, SpawnEntry = info, Name = "Bull", Level = 6, MaxLife = 100, Life = 100,
                Map = 0, X = 100, Y = 100, Defense = 100, DefenseSuccessRate = 10,
            };
        }

        public PlayerObject Player(int index = 9000)
        {
            var player = new PlayerObject
            {
                Index = index, Session = FakeSession.Create(), Account = "acc", Name = "P" + index, Class = 0, Level = 1,
                Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, WorldEntered = true, Map = 0, X = 100, Y = 100,
            };
            Players.Add(player);
            return player;
        }
    }

    // ---------------------------------------------------------------- what the skills leave

    [Test]
    public void Poison_and_Decay_leave_the_poison_with_the_poisoner_a_hit_every_two_seconds_for_three_percent()
    {
        var rules = new SupportSkillRules();

        var poison = SkillDebuffs.EffectFor(SkillDebuffs.Poison, 9001, 100, rules)!.Value;
        Assert.That(poison, Is.EqualTo((20, (ushort)9001, (ushort)2, (ushort)3, (ushort)0)));

        var decay = SkillDebuffs.EffectFor(SkillDebuffs.Decay, 9001, 100, rules)!.Value;
        Assert.That(decay, Is.EqualTo((10, (ushort)9001, (ushort)2, (ushort)3, (ushort)0)), "the same poison, for half the time");
    }

    [Test]
    public void The_ice_and_the_arrow_last_as_long_as_the_original_makes_them()
    {
        var rules = new SupportSkillRules();

        Assert.That(SkillDebuffs.EffectFor(SkillDebuffs.Ice, 9001, 100, rules)!.Value.Seconds, Is.EqualTo(10));
        Assert.That(SkillDebuffs.EffectFor(SkillDebuffs.IceStorm, 9001, 100, rules)!.Value.Seconds, Is.EqualTo(10));
        Assert.That(SkillDebuffs.EffectFor(SkillDebuffs.IceArrow, 9001, 100, rules)!.Value.Seconds, Is.EqualTo(7));
    }

    [Test]
    public void Fire_Slash_takes_a_part_of_the_defense_that_grows_with_the_strength_up_to_the_limit()
    {
        var rules = new SupportSkillRules(); // A 10, B 50, time 10, limit 30

        var weak = SkillDebuffs.EffectFor(SkillDebuffs.FireSlash, 9001, 100, rules)!.Value;
        Assert.That(weak.Seconds, Is.EqualTo(10));
        Assert.That(weak.V1, Is.EqualTo(12), "10 + 100/50");

        var strong = SkillDebuffs.EffectFor(SkillDebuffs.FireSlash, 9001, 5000, rules)!.Value;
        Assert.That(strong.V1, Is.EqualTo(30), "10 + 5000/50 = 110, held to the limit of 30");
    }

    [Test]
    public void A_skill_without_an_effect_leaves_nothing()
    {
        Assert.That(SkillDebuffs.EffectFor(4, 9001, 100, new SupportSkillRules()), Is.Null, "Fire Ball");
        Assert.That(SkillDebuffs.HasEffect(4), Is.False);
        Assert.That(SkillDebuffs.HasEffect(SkillDebuffs.IceArrow), Is.True);
    }

    // ---------------------------------------------------------------- the resistance

    [Test]
    public void Resistance_255_always_resists_and_0_never_does()
    {
        Assert.That(SkillDebuffs.Resists(255, _ => 0), Is.True);
        Assert.That(SkillDebuffs.Resists(0, n => n - 1), Is.False, "with 0, the only roll is 0");
    }

    [Test]
    public void A_resistance_lets_the_effect_land_one_time_in_resistance_plus_one()
    {
        // 3 -> rolls 0..3; only the 0 lands the effect.
        Assert.That(SkillDebuffs.Resists(3, _ => 0), Is.False);
        Assert.That(SkillDebuffs.Resists(3, _ => 1), Is.True);
        Assert.That(SkillDebuffs.Resists(3, _ => 3), Is.True);

        int landed = 0;
        var rng = new Random(7);

        for (int n = 0; n < 4000; n++)
        {
            if (!SkillDebuffs.Resists(3, rng.Next))
            {
                landed++;
            }
        }

        Assert.That(landed, Is.InRange(900, 1100), "about one in four");
    }

    [Test]
    public void A_ring_of_ice_resists_the_ice_a_point_for_every_level()
    {
        var items = Items();
        int ring = Item.GetItem(13, 8);
        items.Add(new ItemBalance { Index = ring, Section = 13, Sub = 8, Slot = 10, Level = 20, Durability = 50, Resistance = new[] { 1, 0, 0, 0, 0, 0, 0 } });

        var player = DarkKnight();
        player.Items[Item.SlotRing1] = Make(ring, level: 4);
        player.RecalcCombatStats(items, Balance());

        Assert.That(player.Resistance[0], Is.EqualTo(4), "ice: 1 per level, +4");
        Assert.That(player.Resistance[1], Is.EqualTo(0), "the poison it does not stop");

        player.Items[Item.SlotRing2] = Make(ring, level: 9);
        player.RecalcCombatStats(items, Balance());
        Assert.That(player.Resistance[0], Is.EqualTo(9), "the best of the two rings and the amulet, not the sum");
    }

    [Test]
    public void The_resistance_of_a_ring_is_held_at_255()
    {
        var items = Items();
        int ring = Item.GetItem(13, 9);
        items.Add(new ItemBalance { Index = ring, Section = 13, Sub = 9, Slot = 10, Level = 17, Durability = 50, Resistance = new[] { 0, 100, 0, 0, 0, 0, 0 } });

        var player = DarkKnight();
        player.Items[Item.SlotRing1] = Make(ring, level: 9);
        player.RecalcCombatStats(items, Balance());

        Assert.That(player.Resistance[1], Is.EqualTo(255), "100 x 9 = 900, held to a byte");
    }

    // ---------------------------------------------------------------- the Crystal weapons

    [Test]
    public void A_Crystal_weapon_casts_Ice_on_one_blow_in_twenty()
    {
        var sword = Make(Item.GetItem(2, 5));
        var morningStar = Make(Item.GetItem(2, 4));

        Assert.That(SkillDebuffs.CrystalCastsIce(sword, _ => 0), Is.True, "the Crystal Sword, on the lucky roll");
        Assert.That(SkillDebuffs.CrystalCastsIce(morningStar, _ => 0), Is.True, "the Crystal Morning Star too");
        Assert.That(SkillDebuffs.CrystalCastsIce(sword, n => n - 1), Is.False, "any other roll does nothing");

        int cast = 0;
        var rng = new Random(11);

        for (int n = 0; n < 4000; n++)
        {
            if (SkillDebuffs.CrystalCastsIce(sword, rng.Next))
            {
                cast++;
            }
        }

        Assert.That(cast, Is.InRange(130, 270), "about one in twenty");
    }

    [Test]
    public void Only_a_Crystal_weapon_with_durability_left_casts_Ice()
    {
        Assert.That(SkillDebuffs.CrystalCastsIce(Make(Kris), _ => 0), Is.False, "an ordinary weapon");
        Assert.That(SkillDebuffs.CrystalCastsIce(Make(Item.GetItem(2, 5), durability: 0), _ => 0), Is.False, "a spent one");
        Assert.That(SkillDebuffs.CrystalCastsIce(Item.Empty(), _ => 0), Is.False, "no weapon");
    }

    // ---------------------------------------------------------------- the poison

    [Test]
    public void The_poison_takes_three_percent_of_the_life_that_is_left()
    {
        Assert.That(SkillDebuffs.PoisonDamage(1000f, 3, 0), Is.EqualTo(30));
        Assert.That(SkillDebuffs.PoisonDamage(970f, 3, 0), Is.EqualTo(29), "the life it has now, cut to a whole number");
        Assert.That(SkillDebuffs.PoisonDamage(20f, 3, 0), Is.EqualTo(0), "too little to hurt");
    }

    [Test]
    public void A_cap_on_the_poison_holds_it_back_and_a_zero_cap_means_no_limit()
    {
        Assert.That(SkillDebuffs.PoisonDamage(10_000f, 3, 50), Is.EqualTo(50), "300 held to 50");
        Assert.That(SkillDebuffs.PoisonDamage(1000f, 3, 50), Is.EqualTo(30), "under the cap, untouched");
        Assert.That(SkillDebuffs.PoisonDamage(10_000f, 3, 0), Is.EqualTo(300));
    }

    [Test]
    public void The_poison_hits_on_the_counts_that_the_gap_divides()
    {
        // 20 seconds, every 2: the count is already lowered when it is asked, 19, 18...
        var hits = Enumerable.Range(0, 20).Select(n => (uint)(19 - n)).Where(count => SkillDebuffs.PoisonHitsNow(count, 2)).ToList();

        Assert.That(hits, Has.Count.EqualTo(10), "ten hits in twenty seconds");
        Assert.That(hits.First(), Is.EqualTo(18u));
        Assert.That(hits.Last(), Is.EqualTo(0u), "and one on the last second, as the original does");
        Assert.That(SkillDebuffs.PoisonHitsNow(5, 0), Is.False, "a zero gap never hits");
    }

    [Test]
    public async Task A_monster_carries_the_poison_runs_it_down_and_loses_it()
    {
        var h = new Harness();
        var monster = h.Monster();
        var viewer = h.Player();
        monster.VisibleTo.Add(viewer.Index);

        await h.Effects.AddAsync(monster, false, EffectIds.Poison, 3, 9000, 2, 3, 0, CancellationToken.None);

        Assert.That(h.Effects.Check(monster, EffectIds.Poison), Is.True);
        var started = ((FakeSession)viewer.Session).Of(0x07).Single();
        Assert.That(started[3], Is.EqualTo(1));
        Assert.That(BitConverter.ToUInt16(started, 4), Is.EqualTo(1), "the green of the poison");
        Assert.That((started[6] << 8) | started[7], Is.EqualTo(monster.Index), "on the monster");

        for (int n = 0; n < 3; n++)
        {
            await h.Effects.TickAsync(monster, CancellationToken.None);
        }

        Assert.That(h.Periodic.Select(p => p.Count), Is.EqualTo(new uint[] { 2, 1, 0 }), "the count already lowered when the effect acts");
        Assert.That(h.Effects.Check(monster, EffectIds.Poison), Is.False, "gone at the last second");
        Assert.That(((FakeSession)viewer.Session).Of(0x07).Last()[3], Is.EqualTo(0), "and the viewers are told");
    }

    // ---------------------------------------------------------------- the ice and the arrow

    [Test]
    public async Task The_ice_slows_a_monster_and_stops_slowing_it_when_it_ends()
    {
        var h = new Harness();
        var monster = h.Monster();

        await h.Effects.AddAsync(monster, false, EffectIds.Ice, 10, 0, 0, 0, 0, CancellationToken.None);

        Assert.That(monster.EffectBonus.DelayLevel, Is.EqualTo(1));
        Assert.That(monster.EffectBonus.DelayActionTime, Is.EqualTo(800), "its actions wait 800 ms more");
        Assert.That(ClientProtocolHandler.ActionDelay(monster), Is.EqualTo(800));

        await h.Effects.DelAsync(monster, EffectIds.Ice, CancellationToken.None);

        Assert.That(monster.EffectBonus.DelayLevel, Is.EqualTo(0));
        Assert.That(ClientProtocolHandler.ActionDelay(monster), Is.EqualTo(0));
    }

    [Test]
    public async Task The_ice_arrow_roots_whoever_carries_it()
    {
        var h = new Harness();
        var monster = h.Monster();
        var player = h.Player();

        Assert.That(h.Effects.CheckImmobilize(monster), Is.False);

        await h.Effects.AddAsync(monster, false, EffectIds.IceArrow, 7, 0, 0, 0, 0, CancellationToken.None);
        await h.Effects.AddAsync(player, false, EffectIds.IceArrow, 7, 0, 0, 0, 0, CancellationToken.None);

        Assert.That(h.Effects.CheckImmobilize(monster), Is.True);
        Assert.That(h.Effects.CheckImmobilize(player), Is.True);

        for (int n = 0; n < 7; n++)
        {
            await h.Effects.TickAsync(monster, CancellationToken.None);
        }

        Assert.That(h.Effects.CheckImmobilize(monster), Is.False, "after seven seconds it walks again");
    }

    [Test]
    public async Task Ice_and_Ice_Arrow_have_groups_of_their_own_and_both_run()
    {
        var h = new Harness();
        var monster = h.Monster();

        await h.Effects.AddAsync(monster, false, EffectIds.Ice, 10, 0, 0, 0, 0, CancellationToken.None);
        await h.Effects.AddAsync(monster, false, EffectIds.IceArrow, 7, 0, 0, 0, 0, CancellationToken.None);

        Assert.That(h.Effects.Check(monster, EffectIds.Ice), Is.True, "different groups: both run");
        Assert.That(h.Effects.Check(monster, EffectIds.IceArrow), Is.True);
    }

    // ---------------------------------------------------------------- Fire Slash and the defense

    [Test]
    public async Task Fire_Slash_lowers_the_defense_of_a_monster_by_a_percent_and_gives_it_back()
    {
        var h = new Harness();
        var monster = h.Monster();

        Assert.That(monster.EffectiveDefense(), Is.EqualTo(100));

        await h.Effects.AddAsync(monster, false, EffectIds.FireSlash, 10, 20, 0, 0, 0, CancellationToken.None);
        Assert.That(monster.EffectiveDefense(), Is.EqualTo(80), "20 percent less");

        await h.Effects.DelAsync(monster, EffectIds.FireSlash, CancellationToken.None);
        Assert.That(monster.EffectiveDefense(), Is.EqualTo(100));
    }

    // ---------------------------------------------------------------- the immunities

    [Test]
    public async Task The_immunities_of_a_player_are_one_for_blows_and_one_for_skills()
    {
        var h = new Harness();
        var player = h.Player();

        Assert.That(h.Effects.IsImmune(player, bySkill: false), Is.False);

        await h.Effects.AddAsync(player, false, EffectIds.PhysiDamageImmunity, 10, 0, 0, 0, 0, CancellationToken.None);
        Assert.That(h.Effects.IsImmune(player, bySkill: false), Is.True, "no plain blow");
        Assert.That(h.Effects.IsImmune(player, bySkill: true), Is.False, "but a spell lands");

        await h.Effects.AddAsync(player, false, EffectIds.MagicDamageImmunity, 10, 0, 0, 0, 0, CancellationToken.None);
        Assert.That(h.Effects.IsImmune(player, bySkill: true), Is.True);
    }

    [Test]
    public async Task The_immunities_of_a_monster_are_its_own_pair()
    {
        var h = new Harness();
        var monster = h.Monster();

        await h.Effects.AddAsync(monster, false, EffectIds.PhysiDamageImmunity, 10, 0, 0, 0, 0, CancellationToken.None);
        Assert.That(h.Effects.IsImmune(monster, bySkill: false), Is.False, "the player's immunity is not the monster's");

        await h.Effects.AddAsync(monster, true, EffectIds.MonsterPhysiDamageImmunity, 0, 0, 0, 0, 0, CancellationToken.None);
        Assert.That(h.Effects.IsImmune(monster, bySkill: false), Is.True);
        Assert.That(h.Effects.IsImmune(monster, bySkill: true), Is.False);

        await h.Effects.AddAsync(monster, true, EffectIds.MonsterMagicDamageImmunity, 0, 0, 0, 0, 0, CancellationToken.None);
        Assert.That(h.Effects.IsImmune(monster, bySkill: true), Is.True);
    }

    [Test]
    public async Task An_immunity_with_no_count_never_ends_by_itself()
    {
        var h = new Harness();
        var monster = h.Monster();

        await h.Effects.AddAsync(monster, true, EffectIds.MonsterPhysiDamageImmunity, 0, 0, 0, 0, 0, CancellationToken.None);

        for (int n = 0; n < 100; n++)
        {
            await h.Effects.TickAsync(monster, CancellationToken.None);
        }

        Assert.That(h.Effects.IsImmune(monster, bySkill: false), Is.True);
    }

    [Test]
    public void The_gate_and_statues_of_the_events_take_nothing()
    {
        var h = new Harness();
        var gate = h.Monster();
        gate.Immune = true;

        Assert.That(h.Effects.IsImmune(gate, bySkill: false), Is.True);
        Assert.That(h.Effects.IsImmune(gate, bySkill: true), Is.True);
    }

    // ---------------------------------------------------------------- coming back

    [Test]
    public async Task A_monster_that_dies_poisoned_comes_back_clean()
    {
        var registry = new MonsterRegistry();
        var h = new Harness();
        var monster = h.Monster();

        await h.Effects.AddAsync(monster, false, EffectIds.Ice, 10, 0, 0, 0, 0, CancellationToken.None);
        await h.Effects.AddAsync(monster, false, EffectIds.FireSlash, 10, 20, 0, 0, 0, CancellationToken.None);
        monster.Live = false;

        registry.Respawn(monster, new MapRegistry());

        Assert.That(monster.ActiveEffects.Any(e => e.IsEffect), Is.False);
        Assert.That(monster.EffectBonus.DelayActionTime, Is.EqualTo(0));
        Assert.That(monster.EffectiveDefense(), Is.EqualTo(100));
    }

    [Test]
    public async Task The_viewport_shows_a_poisoned_monster_with_its_bit()
    {
        var h = new Harness();
        var monster = h.Monster();
        await h.Effects.AddAsync(monster, false, EffectIds.Poison, 20, 9000, 2, 3, 0, CancellationToken.None);

        var packet = WorldPacketBuilder.ViewportMonsterAppear(new[] { monster });

        // C2 header (4 bytes) + count (1) + index (2) + class (2) -> the effect WORD
        Assert.That(BitConverter.ToUInt16(packet, 9), Is.EqualTo(1), "the poison bit travels in the monster's record");
    }
}

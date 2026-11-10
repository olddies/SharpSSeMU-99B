using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The Dark Raven of a Dark Lord: its numbers, its commands, who it attacks and when, and what a blow does.
/// Expected values are worked out by hand from DarkSpirit.cpp with the shipped constants.</summary>
public class DarkSpiritTests
{
    private sealed class Harness
    {
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public PlayerRegistry Players { get; } = new();
        public MonsterRegistry Monsters { get; } = new();
        public ItemBalanceTable Items { get; } = PetItems();
        public DarkSpiritManager Spirits { get; }
        public PlayerObject Master { get; }
        public FakeSession Session => (FakeSession)Master.Session;
        public List<(int Master, int Monster, int Damage)> Kills { get; } = new();
        public DarkSpiritRules Rules { get; }

        private readonly MonsterInfoTable _info = new();
        private readonly MapRegistry _maps = new();

        public Harness(int rangeAttackRate = 0, int criticalRate = 0, int petLevel = 1, int masterLevel = 300, uint leadership = 500)
        {
            Rules = new DarkSpiritRules { RangeAttackRate = rangeAttackRate, CriticalDamageRate = criticalRate };

            string path = Path.Combine(Path.GetTempPath(), $"spirit-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, "3 0 \"Spider\" 2 100000 0 10 20 5 0 100 100 3 0 1 3 400 2000 10 2 100 100 3 0 4 4 4 4 0 0 0\nend\n");
            _info.Load(path);
            File.Delete(path);

            Spirits = new DarkSpiritManager(Players, Monsters, Items, new Random(7), () => Rules) { Clock = () => Now };
            Spirits.OnMonsterKilled = (master, monster, damage, _) => { Kills.Add((master.Index, monster.Index, damage)); return Task.CompletedTask; };

            Master = new PlayerObject
            {
                Index = 9000, Session = FakeSession.Create(), Account = "acc", Name = "Lord", Class = 4, Level = (ushort)masterLevel,
                Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, Leadership = leadership, WorldEntered = true, Map = 0, X = 100, Y = 100,
                Life = 1000, MaxLife = 1000,
            };

            var raven = Make(Item.DarkRaven, durability: 255);
            raven.SetPetItemInfo(petLevel, 0, new PetRules(100, 50, 10));
            Master.Items[Item.SlotWeapon2] = raven;
            Players.Add(Master);
        }

        public Monster AddMonster(int dx, int dy, float life = 100000)
        {
            var entry = new MonsterSpawnEntry { Type = 0, MonsterClass = 3, Map = 0, X = 100 + dx, Y = 100 + dy };
            var monster = Monsters.SpawnOne(3, entry, _info, _maps)!;
            monster.Life = life;
            monster.MaxLife = Math.Max(life, monster.MaxLife);
            Master.VisibleMonsters.Add(monster.Index);
            return monster;
        }
    }

    private static ItemBalanceTable PetItems()
    {
        var items = Items();
        items.Add(new ItemBalance { Index = Item.DarkHorse, Section = 13, Sub = 4, Slot = 8, Level = 218, RequireLevel = 218, Durability = 255 });
        items.Add(new ItemBalance { Index = Item.DarkRaven, Section = 13, Sub = 5, Slot = 1, Level = 0, Durability = 255 });
        return items;
    }

    // ---------------------------------------------------------------- the numbers

    [Test]
    public void The_ravens_numbers_come_from_its_level_and_its_masters_command()
    {
        var stats = new DarkSpiritRules().Stats(petLevel: 1, leadership: 200);

        Assert.That(stats.DamageMin, Is.EqualTo(180 + 15 + 200 / 8));
        Assert.That(stats.DamageMax, Is.EqualTo(200 + 15 + 200 / 4));
        Assert.That(stats.AttackSpeed, Is.EqualTo(20 + (1 * 4) / 5 + 200 / 50));
        Assert.That(stats.AttackSuccessRate, Is.EqualTo(1000 + (1 * 16) / 1));
        Assert.That(DarkSpiritRules.AttackInterval(stats), Is.EqualTo(1500 - 24 * 10));
    }

    [Test]
    public void Only_a_dark_lord_with_a_raven_that_works_has_numbers()
    {
        var h = new Harness();
        Assert.That(h.Spirits.StatsFor(h.Master), Is.Not.Null);

        h.Master.Class = 1;
        Assert.That(h.Spirits.StatsFor(h.Master), Is.Null, "not a Dark Lord");
        h.Master.Class = 4;

        h.Master.Items[Item.SlotWeapon2].Durability = 0;
        Assert.That(h.Spirits.StatsFor(h.Master), Is.Null, "no durability left");
        h.Master.Items[Item.SlotWeapon2].Durability = 255;

        h.Master.Leadership = 100;
        Assert.That(h.Spirits.StatsFor(h.Master), Is.Null, "less command than it asks (185 + 15 a level)");
        h.Master.Leadership = 500;

        h.Master.Items[Item.SlotWeapon2] = Item.Empty();
        Assert.That(h.Spirits.StatsFor(h.Master), Is.Null, "no raven");
    }

    // ---------------------------------------------------------------- the blow

    [Test]
    public void A_blow_is_the_range_of_the_ravens_numbers_with_a_critical_at_the_top()
    {
        var h = new Harness();
        var stats = h.Spirits.StatsFor(h.Master)!.Value;
        var rules = new DarkSpiritRules { CriticalDamageRate = 30 };

        int low = DarkSpiritMath.Roll(h.Master, stats, h.Items, rules, new ScriptedRandom(0, 99, 99), out byte effect);
        Assert.That((low, effect), Is.EqualTo((stats.DamageMin, (byte)0)));

        int critical = DarkSpiritMath.Roll(h.Master, stats, h.Items, rules, new ScriptedRandom(0, 0, 99), out effect);
        Assert.That((critical, effect), Is.EqualTo((stats.DamageMax, (byte)3)));

        var excellentRules = new DarkSpiritRules { CriticalDamageRate = 0, ExcellentDamageRate = 100 };
        int excellent = DarkSpiritMath.Roll(h.Master, stats, h.Items, excellentRules, new ScriptedRandom(0, 99, 0), out effect);
        Assert.That((excellent, effect), Is.EqualTo((stats.DamageMax * 120 / 100, (byte)2)));
    }

    [Test]
    public void The_defense_comes_off_and_a_graze_or_a_range_blow_is_a_fraction()
    {
        var master = new Harness(masterLevel: 100).Master; // minimum damage 10

        Assert.That(DarkSpiritMath.AfterTarget(master, null, 1000, 300, graze: false, action: 0), Is.EqualTo(700));
        Assert.That(DarkSpiritMath.AfterTarget(master, null, 1000, 300, graze: true, action: 0), Is.EqualTo(210), "30% of 700");
        Assert.That(DarkSpiritMath.AfterTarget(master, null, 1000, 300, graze: false, action: 1), Is.EqualTo(420), "60% of 700");
        Assert.That(DarkSpiritMath.AfterTarget(master, null, 100, 300, graze: false, action: 0), Is.EqualTo(10), "never under a tenth of the master's level");
    }

    [Test]
    public void What_a_player_target_wears_takes_its_share_of_the_blow()
    {
        var master = new Harness(masterLevel: 100).Master;
        var target = DarkKnight();
        target.DamageReduction = 10;

        Assert.That(DarkSpiritMath.AfterTarget(master, target, 1000, 0, graze: false, action: 0), Is.EqualTo(900));
    }

    [Test]
    public void The_raven_wears_a_point_of_durability_every_eight_hundred_small_points()
    {
        var raven = Make(Item.DarkRaven, durability: 255);
        var rules = new DarkSpiritRules();

        Assert.That(DarkSpiritMath.Wear(raven, 20_000, rules), Is.False, "400 small points");
        Assert.That(raven.DurabilitySmall, Is.EqualTo(400));
        Assert.That(DarkSpiritMath.Wear(raven, 20_000, rules), Is.True, "800: a point is lost");
        Assert.That((raven.Durability, raven.DurabilitySmall), Is.EqualTo(((byte)254, 0)));

        raven.Durability = 0;
        DarkSpiritMath.Wear(raven, 40_000, rules);
        Assert.That(raven.Durability, Is.EqualTo(0), "and it does not go under nothing");
    }

    // ---------------------------------------------------------------- the commands

    [Test]
    public async Task A_command_sets_the_mode_and_tells_the_master_but_the_same_one_twice_is_ignored()
    {
        var h = new Harness();

        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.AttackRandom));
        var mode = h.Session.Of(0xA7).Single();
        Assert.That(mode.Skip(3).ToArray(), Is.EqualTo(new byte[] { 0, 1, 0xFF, 0xFF }));

        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        Assert.That(h.Session.Of(0xA7), Has.Count.EqualTo(1), "nothing new to say");

        await h.Spirits.ChangeCommandAsync(h.Master, 9, 0xFFFF, CancellationToken.None);
        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.Normal), "an unknown command means normal");
    }

    [Test]
    public async Task Sending_the_raven_after_a_target_needs_a_target_and_can_be_repeated()
    {
        var h = new Harness();

        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, -1, CancellationToken.None);
        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.Normal), "a target is needed");

        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, 5, CancellationToken.None);
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, 6, CancellationToken.None);

        Assert.That((h.Master.SpiritMode, h.Master.SpiritTarget), Is.EqualTo((DarkSpiritMode.AttackTarget, 6)));
        Assert.That(h.Session.Of(0xA7), Has.Count.EqualTo(2), "a new target is news even in the same mode");
    }

    [Test]
    public async Task Without_a_working_raven_no_command_is_taken()
    {
        var h = new Harness();
        h.Master.Items[Item.SlotWeapon2] = Item.Empty();

        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);

        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.Normal));
        Assert.That(h.Session.Sent, Is.Empty);
    }

    [Test]
    public async Task When_the_target_dies_the_raven_sent_after_it_stands_down()
    {
        var h = new Harness();
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, 77, CancellationToken.None);

        await h.Spirits.ResetTargetAsync(77, CancellationToken.None);

        Assert.That((h.Master.SpiritMode, h.Master.SpiritTarget), Is.EqualTo((DarkSpiritMode.Normal, -1)));
        Assert.That(h.Session.Of(0xA7).Last()[4], Is.EqualTo(0));
    }

    [Test]
    public async Task When_a_duel_ends_the_ravens_of_its_two_players_forget_each_other()
    {
        var h = new Harness();
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, 77, CancellationToken.None);

        await h.Spirits.ResetTargetOfAsync(h.Master, 78, CancellationToken.None);
        Assert.That((h.Master.SpiritMode, h.Master.SpiritTarget), Is.EqualTo((DarkSpiritMode.AttackTarget, 77)), "another opponent: nothing changes");

        await h.Spirits.ResetTargetOfAsync(h.Master, 77, CancellationToken.None);
        Assert.That((h.Master.SpiritMode, h.Master.SpiritTarget), Is.EqualTo((DarkSpiritMode.Normal, -1)), "the opponent: the raven stands down");
        Assert.That(h.Session.Of(0xA7).Last()[4], Is.EqualTo(0));
    }

    [Test]
    public async Task A_raven_in_another_mode_only_loses_the_target_when_a_duel_ends()
    {
        var h = new Harness();
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackWithMaster, -1, CancellationToken.None);
        h.Master.SpiritTarget = 77;

        await h.Spirits.ResetTargetOfAsync(h.Master, 77, CancellationToken.None);

        Assert.That((h.Master.SpiritMode, h.Master.SpiritTarget), Is.EqualTo((DarkSpiritMode.AttackWithMaster, -1)), "same mode, no target");
    }

    [Test]
    public async Task Only_a_Dark_Lords_raven_is_reset()
    {
        var h = new Harness();
        h.Master.SpiritTarget = 77;
        h.Master.Class = 1;

        await h.Spirits.ResetTargetOfAsync(h.Master, 77, CancellationToken.None);

        Assert.That(h.Master.SpiritTarget, Is.EqualTo(77));
    }

    // ---------------------------------------------------------------- attacking

    [Test]
    public async Task In_the_random_mode_it_picks_a_monster_near_and_the_blow_lands_six_hundred_milliseconds_later()
    {
        var h = new Harness();
        var monster = h.AddMonster(2, 1);
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);

        var announced = h.Session.Of(0xA8).Single();
        Assert.That(announced.Skip(3).ToArray(), Is.EqualTo(new byte[] { 0, 0, (byte)(9000 >> 8), (byte)(9000 & 0xFF), (byte)(monster.Index >> 8), (byte)(monster.Index & 0xFF) }));

        h.Now += TimeSpan.FromMilliseconds(599);
        await h.Spirits.LandDueBlowsAsync(CancellationToken.None);
        Assert.That(monster.Life, Is.EqualTo(100000), "not yet");

        h.Now += TimeSpan.FromMilliseconds(1);
        await h.Spirits.LandDueBlowsAsync(CancellationToken.None);

        var stats = h.Spirits.StatsFor(h.Master)!.Value;
        float dealt = 100000 - monster.Life;
        Assert.That(dealt, Is.InRange(stats.DamageMin - 2, stats.DamageMax - 2), "the raven's range less half the spider's defense");
        Assert.That(h.Session.Of(0xD9), Has.Count.EqualTo(1), "the damage is told to the master");
    }

    [Test]
    public async Task It_waits_its_interval_between_two_attacks()
    {
        var h = new Harness();
        h.AddMonster(1, 0);
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        var interval = DarkSpiritRules.AttackInterval(h.Spirits.StatsFor(h.Master)!.Value);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        h.Now += TimeSpan.FromMilliseconds(interval - 1);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Has.Count.EqualTo(1));

        h.Now += TimeSpan.FromMilliseconds(1);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task It_does_not_fight_from_a_safe_zone_nor_at_something_far_nor_in_the_normal_mode()
    {
        var h = new Harness();
        h.AddMonster(6, 0); // five tiles or more away: too far for the random mode
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty, "too far");

        var near = h.AddMonster(1, 1);
        h.Spirits.IsSafeZone = (_, _, _) => true;
        h.Now += TimeSpan.FromSeconds(5);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty, "in a safe zone");

        h.Spirits.IsSafeZone = (_, _, _) => false;
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.Normal, -1, CancellationToken.None);
        h.Now += TimeSpan.FromSeconds(5);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty, "in the normal mode");
        Assert.That(near.Life, Is.EqualTo(100000));
    }

    [Test]
    public async Task With_the_master_it_attacks_what_the_master_attacked()
    {
        var h = new Harness();
        var first = h.AddMonster(1, 0);
        var second = h.AddMonster(0, 2);
        await h.Spirits.ChangeCommandAsync(h.Master, 2, 0xFFFF, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty, "the master has not attacked anything yet");

        h.Spirits.SetTarget(h.Master, second.Index);
        h.Now += TimeSpan.FromSeconds(5);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);

        var announced = h.Session.Of(0xA8).Single();
        Assert.That((announced[7] << 8) | announced[8], Is.EqualTo(second.Index));
        Assert.That(first.Life, Is.EqualTo(100000));
    }

    [Test]
    public async Task A_target_that_died_or_went_far_is_dropped_or_waited_for()
    {
        var h = new Harness();
        var monster = h.AddMonster(1, 0);
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, monster.Index, CancellationToken.None);

        monster.X = 120; // seven tiles or more: it waits
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty);
        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.AttackTarget));

        monster.Live = false;
        h.Now += TimeSpan.FromSeconds(5);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.Normal), "a dead target ends the order");
    }

    [Test]
    public async Task A_range_blow_spreads_to_the_monsters_around_the_target_at_sixty_percent()
    {
        var h = new Harness(rangeAttackRate: 100);
        var target = h.AddMonster(1, 0);
        var beside = h.AddMonster(2, 0);
        var far = h.AddMonster(4, 4); // more than three tiles from the target
        await h.Spirits.SetModeAsync(h.Master, DarkSpiritMode.AttackTarget, target.Index, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);

        var announced = h.Session.Of(0xA8).Single(); // only the target's blow has the animation
        Assert.That(announced[4], Is.EqualTo(1), "action 1: the range blow");

        h.Now += TimeSpan.FromMilliseconds(600);
        await h.Spirits.LandDueBlowsAsync(CancellationToken.None);

        var stats = h.Spirits.StatsFor(h.Master)!.Value;
        Assert.That(100000 - target.Life, Is.InRange((stats.DamageMin - 2) * 60 / 100 - 1, (stats.DamageMax - 2) * 60 / 100 + 1));
        Assert.That(beside.Life, Is.LessThan(100000));
        Assert.That(far.Life, Is.EqualTo(100000));
    }

    [Test]
    public async Task A_blow_that_takes_the_last_life_kills_the_monster_for_the_master()
    {
        var h = new Harness();
        var monster = h.AddMonster(1, 0, life: 5);
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        h.Now += TimeSpan.FromMilliseconds(600);
        await h.Spirits.LandDueBlowsAsync(CancellationToken.None);

        Assert.That(monster.Life, Is.EqualTo(0));
        Assert.That(h.Kills, Has.Count.EqualTo(1));
        Assert.That((h.Kills[0].Master, h.Kills[0].Monster), Is.EqualTo((9000, monster.Index)));
        Assert.That(h.Session.Of(0xD9), Is.Empty, "the killing blow's number comes with the death, not on its own");
    }

    [Test]
    public async Task The_raven_wears_with_the_blow_and_the_master_is_told_when_a_point_goes()
    {
        var h = new Harness();
        var raven = h.Master.Items[Item.SlotWeapon2];
        raven.DurabilitySmall = 799; // the next blow tips it over
        h.AddMonster(1, 0);
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        h.Now += TimeSpan.FromMilliseconds(600);
        await h.Spirits.LandDueBlowsAsync(CancellationToken.None);

        Assert.That(raven.Durability, Is.EqualTo(254));
        Assert.That(h.Session.Of(0x2A).Single()[3], Is.EqualTo(Item.SlotWeapon2), "the item durability packet, for slot 1");
        var petInfo = h.Session.Of(0xA9).Single();
        Assert.That((petInfo[3], petInfo[5]), Is.EqualTo(((byte)0, (byte)Item.SlotWeapon2)));
    }

    [Test]
    public async Task A_master_who_is_not_a_dark_lord_has_no_raven_acting()
    {
        var h = new Harness();
        h.AddMonster(1, 0);
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        h.Master.Class = 1;

        await h.Spirits.TickAsync(h.Master, CancellationToken.None);

        Assert.That(h.Session.Of(0xA8), Is.Empty);
    }

    [Test]
    public async Task A_raven_put_on_again_starts_in_the_normal_mode()
    {
        var h = new Harness();
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        var raven = h.Master.Items[Item.SlotWeapon2];

        h.Master.Items[Item.SlotWeapon2] = Item.Empty(); // taken off: the tick notices the raven does not work
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        Assert.That(h.Master.SpiritWorks, Is.False);

        h.Master.Items[Item.SlotWeapon2] = raven;
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);

        Assert.That(h.Master.SpiritMode, Is.EqualTo(DarkSpiritMode.Normal));
        Assert.That(h.Session.Of(0xA7).Last()[4], Is.EqualTo(0), "and the master is told");
    }

    [Test]
    public async Task A_raven_hurts_a_player_the_master_may_attack_and_not_one_he_may_not()
    {
        var h = new Harness();
        var target = new PlayerObject
        {
            Index = 9001, Session = FakeSession.Create(), Account = "other", Name = "Other", Class = 1, Level = 100,
            Strength = 100, Dexterity = 20, Vitality = 25, Energy = 30, WorldEntered = true, Map = 0, X = 102, Y = 100,
            Life = 1_000_000, MaxLife = 1_000_000,
        };
        h.Players.Add(target);
        h.Master.VisibleTo.Add(target.Index);
        var hits = 0;
        h.Spirits.OnPlayerHit = (_, _, _) => { hits++; return Task.CompletedTask; };

        // Not allowed: nobody is picked and a blow sent at them is not landed.
        h.Spirits.CanAttackPlayer = (_, _) => false;
        await h.Spirits.ChangeCommandAsync(h.Master, 1, 0xFFFF, CancellationToken.None);
        await h.Spirits.TickAsync(h.Master, CancellationToken.None);
        await h.Spirits.AttackAsync(h.Master, target.Index, send: false, action: 0, CancellationToken.None);
        Assert.That(h.Session.Of(0xA8), Is.Empty);
        Assert.That(target.Life, Is.EqualTo(1_000_000u));

        // Allowed: some of the blows miss (the PvP success rates), so keep hitting until one lands.
        h.Spirits.CanAttackPlayer = (_, _) => true;

        for (int blow = 0; blow < 50 && target.Life == 1_000_000; blow++)
        {
            await h.Spirits.AttackAsync(h.Master, target.Index, send: false, action: 0, CancellationToken.None);
        }

        Assert.That(target.Life, Is.LessThan(1_000_000u));
        Assert.That(hits, Is.GreaterThan(0), "the PvP side effects (self-defense) were told");
        Assert.That(((FakeSession)target.Session).Of(0xD9), Is.Not.Empty, "the target sees the damage too");
        Assert.That(h.Session.Of(0xD9), Is.Not.Empty);
    }

    // ---------------------------------------------------------------- the packets

    [Test]
    public void The_packets_are_laid_out_as_the_client_reads_them()
    {
        Assert.That(DarkSpiritPacketBuilder.ModeSend(3, 0x1234), Is.EqualTo(new byte[] { 0xC1, 7, 0xA7, 0, 3, 0x12, 0x34 }));
        Assert.That(DarkSpiritPacketBuilder.AttackSend(0x2328, 0x0005, 1), Is.EqualTo(new byte[] { 0xC1, 9, 0xA8, 0, 1, 0x23, 0x28, 0x00, 0x05 }));

        var command = PetCommandRecv.Parse(new byte[] { 0xC1, 7, 0xA7, 0, 3, 0x00, 0x2A });
        Assert.That((command.Type, command.Command, command.Index), Is.EqualTo(((byte)0, (byte)3, 42)));
    }
}

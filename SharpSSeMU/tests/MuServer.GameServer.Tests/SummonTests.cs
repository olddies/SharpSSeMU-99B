using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The monsters a Fairy Elf summons: the call, the second cast that sends it away, who it follows and what it goes for, what a
/// blow of it does and whose the kill is, and how it shows to the others. Worked out by hand from SkillManager.cpp:1343 and Monster.cpp.</summary>
public class SummonTests
{
    private const string MonsterList = """
        // Index Type Name Level MaxLife MaxMana DamageMin DamageMax Defense MagicDefense AttackRate DefenseRate MoveRange AttackType AttackRange ViewRange MoveSpeed AttackSpeed RegenTime Attribute ItemRate MoneyRate MaxItemLevel MonsterSkill R1 R2 R3 R4 R5 R6 R7
          26   0    "Goblin"       4  100  0   20  30  5   0   1000  10     3   0   1   5   400  1000  10  2  1  1  6  0  0 0 0 0 0 0 0
          3    0    "Spider"       2  300  0   4   7   10  0   8     8      2   0   1   5   400  1800  10  2  1  1  6  0  0 0 0 0 0 0 0
          21   0    "Assassin"     9  200  0   30  40  6   0   1000  10     3   0   1   5   400  1000  10  2  1  1  6  0  0 0 0 0 0 0 0
        end
        """;

    private sealed class Harness
    {
        public PlayerRegistry Players { get; } = new();
        public MonsterRegistry Monsters { get; } = new();
        public SummonManager Summons { get; }
        public DateTime Now { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public List<(PlayerObject Master, Monster Monster, int Damage)> Kills { get; } = new();

        public Harness(int seed = 1)
        {
            string path = Path.Combine(Path.GetTempPath(), $"summon-monsters-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, MonsterList);
            var infos = new MonsterInfoTable();
            infos.Load(path);
            File.Delete(path);

            Summons = new SummonManager(Players, Monsters, infos, new MapRegistry(), new Random(seed))
            {
                Clock = () => Now,
                OnMonsterKilled = (master, monster, damage, _) =>
                {
                    Kills.Add((master, monster, damage));
                    monster.Live = false;
                    return Task.CompletedTask;
                },
            };

            Infos = infos;
        }

        public MonsterInfoTable Infos { get; }

        public PlayerObject Elf(int index = 9000, int x = 100, int y = 100)
        {
            var session = FakeSession.Create();
            session.Connected = true;
            var player = new PlayerObject
            {
                Index = index, Session = session, Account = "acc", Name = "Elf" + index, Class = 2, Level = 100,
                Strength = 100, Dexterity = 100, Vitality = 100, Energy = 100, WorldEntered = true, Map = 0, X = (byte)x, Y = (byte)y, Life = 500,
            };
            Players.Add(player);
            return player;
        }

        public Monster Target(int x, int y, int monsterClass = 3)
        {
            var entry = new MonsterSpawnEntry { Type = 4, MonsterClass = monsterClass, Map = 0, X = x, Y = y, Dir = 1 };
            return Monsters.SpawnOne(monsterClass, entry, Infos, new MapRegistry())!;
        }

        public Task<bool> Cast(PlayerObject owner, int skill = 30) => Summons.CastAsync(owner, skill, CancellationToken.None);

        public async Task Tick(int times = 1, int stepMs = 200)
        {
            for (int n = 0; n < times; n++)
            {
                Now = Now.AddMilliseconds(stepMs);
                await Summons.TickAsync(CancellationToken.None);
            }
        }
    }

    private static FakeSession Sent(PlayerObject player) => (FakeSession)player.Session;

    // ---------------------------------------------------------------- the skills

    [Test]
    public void Each_Summon_skill_calls_the_monster_the_skill_dat_gives_it()
    {
        var h = new Harness();

        Assert.That(h.Summons.MonsterFor(30), Is.EqualTo(26), "Summon Goblin");
        Assert.That(h.Summons.MonsterFor(31), Is.EqualTo(32));
        Assert.That(h.Summons.MonsterFor(36), Is.EqualTo(151));
        Assert.That(h.Summons.MonsterFor(29), Is.EqualTo(-1), "not a Summon skill");
        Assert.That(h.Summons.MonsterFor(37), Is.EqualTo(-1));
        Assert.That(SummonManager.IsSummonSkill(33), Is.True);
        Assert.That(SummonManager.IsSummonSkill(26), Is.False, "Heal");
    }

    // ---------------------------------------------------------------- the call

    [Test]
    public async Task The_cast_calls_the_monster_one_tile_behind_its_master_and_tells_him_its_life()
    {
        var h = new Harness();
        var elf = h.Elf(x: 100, y: 100);

        Assert.That(await h.Cast(elf), Is.True);

        var summon = h.Summons.Of(elf)!;
        Assert.That(summon.Name, Is.EqualTo("Goblin"));
        Assert.That((summon.X, summon.Y), Is.EqualTo(((byte)99, (byte)101)), "X - 1, Y + 1");
        Assert.That(summon.Dir, Is.EqualTo(2));
        Assert.That(summon.OwnerIndex, Is.EqualTo(elf.Index));
        Assert.That(summon.IsSummon && summon.JustSummoned && summon.NoRespawn, Is.True);
        Assert.That(elf.SummonIndex, Is.EqualTo(summon.Index));

        var life = Sent(elf).Sent.Single(p => p.Length > 4 && p[2] == 0xF3 && p[3] == 0x20);
        Assert.That(life[4], Is.EqualTo(100), "the bar of the client, full");
    }

    [Test]
    public async Task A_monster_that_does_not_exist_calls_nothing()
    {
        var h = new Harness();
        var elf = h.Elf();
        h.Summons.MonsterBySkill = new[] { 999, 999, 999, 999, 999, 999, 999 };

        Assert.That(await h.Cast(elf), Is.False);
        Assert.That(elf.SummonIndex, Is.EqualTo(-1));
    }

    [Test]
    public async Task Calling_again_sends_the_summon_away_and_calls_nothing()
    {
        var h = new Harness();
        var elf = h.Elf();
        var watcher = h.Elf(9001);
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        summon.VisibleTo.Add(watcher.Index);
        watcher.VisibleMonsters.Add(summon.Index);

        Assert.That(await h.Cast(elf, skill: 31), Is.False, "the second cast only dismisses");

        Assert.That(elf.SummonIndex, Is.EqualTo(-1));
        Assert.That(h.Monsters.TryGet(summon.Index, out _), Is.False, "gone from the world");
        Assert.That(Sent(watcher).Of(0x14), Has.Count.EqualTo(1), "and from the view of whoever saw it");
        Assert.That(watcher.VisibleMonsters, Does.Not.Contain(summon.Index));

        var last = Sent(elf).Sent.Last(p => p.Length > 4 && p[2] == 0xF3 && p[3] == 0x20);
        Assert.That(last[4], Is.EqualTo(0), "the bar is emptied");
    }

    // ---------------------------------------------------------------- it follows and leaves

    [Test]
    public async Task A_summon_goes_when_its_master_dies_or_is_gone()
    {
        var h = new Harness();
        var dies = h.Elf(9000);
        var leaves = h.Elf(9001);
        await h.Cast(dies);
        await h.Cast(leaves);
        var first = h.Summons.Of(dies)!;
        var second = h.Summons.Of(leaves)!;

        dies.IsDying = true;
        leaves.Session.Connected = false;
        await h.Tick();

        Assert.That(h.Monsters.TryGet(first.Index, out _), Is.False);
        Assert.That(h.Monsters.TryGet(second.Index, out _), Is.False);
        Assert.That(dies.SummonIndex, Is.EqualTo(-1));
        Assert.That(leaves.SummonIndex, Is.EqualTo(-1));
    }

    [Test]
    public async Task A_summon_that_is_too_far_comes_to_its_masters_side()
    {
        var h = new Harness();
        var elf = h.Elf(x: 100, y: 100);
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        elf.X = 140;
        elf.Y = 140;

        await h.Tick();

        Assert.That(Math.Abs(summon.X - elf.X), Is.LessThanOrEqualTo(2));
        Assert.That(Math.Abs(summon.Y - elf.Y), Is.LessThanOrEqualTo(2));
    }

    [Test]
    public async Task A_summon_follows_a_master_who_walks_off()
    {
        var h = new Harness();
        var elf = h.Elf(x: 100, y: 100);
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        elf.X = 108;

        await h.Tick(times: 20, stepMs: 700);

        Assert.That(Math.Abs(summon.X - elf.X), Is.LessThanOrEqualTo(2), "it caught up, a tile at a time");
    }

    [Test]
    public async Task A_summon_follows_its_master_to_another_map()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        elf.Map = 2;
        elf.X = 50;
        elf.Y = 50;

        await h.Tick();

        Assert.That(summon.Map, Is.EqualTo(2));
        Assert.That(summon.JustSummoned, Is.True, "it comes into the new map with its circle");
    }

    // ---------------------------------------------------------------- what it fights

    [Test]
    public async Task What_the_master_hits_the_summon_goes_for_unless_it_is_already_after_something()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        var first = h.Target(105, 100);
        var second = h.Target(106, 100);

        h.Summons.SetEnemy(elf, first.Index);
        Assert.That(summon.TargetIndex, Is.EqualTo(first.Index));

        h.Summons.SetEnemy(elf, second.Index);
        Assert.That(summon.TargetIndex, Is.EqualTo(first.Index), "it keeps its target");
    }

    [Test]
    public async Task A_summon_does_not_go_for_a_dead_monster_an_NPC_or_another_summon()
    {
        var h = new Harness();
        var elf = h.Elf();
        var other = h.Elf(9001);
        await h.Cast(elf);
        await h.Cast(other);
        var summon = h.Summons.Of(elf)!;
        var dead = h.Target(105, 100);
        dead.Live = false;
        var npc = h.Target(106, 100);
        npc.ShopNumber = 1;

        h.Summons.SetEnemy(elf, dead.Index);
        h.Summons.SetEnemy(elf, npc.Index);
        h.Summons.SetEnemy(elf, h.Summons.Of(other)!.Index);

        Assert.That(summon.TargetIndex, Is.EqualTo(-1));
    }

    [Test]
    public async Task A_summon_with_no_orders_goes_for_a_monster_in_its_sight()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        var prey = h.Target(102, 101);

        await h.Tick(times: 4, stepMs: 300);

        Assert.That(summon.TargetIndex, Is.EqualTo(prey.Index));
    }

    [Test]
    public async Task A_summon_chases_hits_and_the_damage_is_shown_to_its_master()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        var prey = h.Target(103, 101); // three tiles away
        h.Summons.SetEnemy(elf, prey.Index);

        await h.Tick(times: 30, stepMs: 700);

        Assert.That(prey.Life, Is.LessThan(prey.MaxLife), "it was hurt");
        Assert.That(prey.DamageByAttacker.GetValueOrDefault(elf.Index), Is.GreaterThan(0), "and the damage is the master's");
        Assert.That(Sent(elf).Of(0xD9).Any(p => ((p[3] & 0x7F) << 8 | p[4]) == prey.Index), Is.True, "he sees the number");
        Assert.That(summon.X, Is.GreaterThan(99), "it walked to it");
    }

    [Test]
    public async Task What_a_summon_kills_is_its_masters()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var prey = h.Target(100, 102);
        prey.MaxLife = 10;
        prey.Life = 10;
        h.Summons.SetEnemy(elf, prey.Index);

        await h.Tick(times: 40, stepMs: 700);

        Assert.That(h.Kills, Has.Count.EqualTo(1));
        Assert.That(h.Kills[0].Master, Is.SameAs(elf), "the reward goes to the master");
        Assert.That(h.Kills[0].Monster, Is.SameAs(prey));
        Assert.That(h.Summons.Of(elf)!.TargetIndex, Is.EqualTo(-1), "and it looks for the next");
    }

    [Test]
    public async Task A_summon_does_not_attack_in_a_safe_zone_target()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var immune = h.Target(100, 102);
        immune.Immune = true; // the Blood Castle gate and the like
        h.Summons.SetEnemy(elf, immune.Index);

        Assert.That(h.Summons.Of(elf)!.TargetIndex, Is.EqualTo(-1));
    }

    // ---------------------------------------------------------------- what the others see

    [Test]
    public async Task A_summon_comes_into_view_in_a_record_of_its_own_with_its_masters_name()
    {
        var h = new Harness();
        var elf = h.Elf(x: 100, y: 100);
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;

        var packet = WorldPacketBuilder.ViewportSummonAppear(new[] { (summon, elf.Name) });

        Assert.That(packet[0], Is.EqualTo(0xC2));
        Assert.That(packet[3], Is.EqualTo(0x1F), "the summon list");
        Assert.That(packet[4], Is.EqualTo(1), "one record");
        Assert.That(packet.Length, Is.EqualTo(4 + 1 + 22), "22 bytes a record");
        Assert.That(((packet[5] & 0x7F) << 8) | packet[6], Is.EqualTo(summon.Index));
        Assert.That(packet[5] & 0x80, Is.EqualTo(0x80), "just called: the magic circle");
        Assert.That(((packet[7] << 8) | packet[8]), Is.EqualTo(26), "the class");
        Assert.That(packet[11], Is.EqualTo(99), "x");
        Assert.That(packet[12], Is.EqualTo(101), "y");
        Assert.That(System.Text.Encoding.ASCII.GetString(packet, 16, 10).TrimEnd('\0'), Is.EqualTo(elf.Name), "the name of its master");

        summon.JustSummoned = false;
        Assert.That(WorldPacketBuilder.ViewportSummonAppear(new[] { (summon, elf.Name) })[5] & 0x80, Is.EqualTo(0), "no circle the second time");
    }

    [Test]
    public void The_life_bar_of_a_summon_is_a_percentage()
    {
        Assert.That(SummonPacketBuilder.SummonLifeSend(50, 200)[4], Is.EqualTo(25));
        Assert.That(SummonPacketBuilder.SummonLifeSend(200, 200)[4], Is.EqualTo(100));
        Assert.That(SummonPacketBuilder.SummonLifeSend(0, 1)[4], Is.EqualTo(0));
        Assert.That(SummonPacketBuilder.SummonLifeSend(5, 0)[4], Is.EqualTo(0), "no maximum, no division");
    }

    [Test]
    public async Task Nobody_can_attack_a_summon_and_it_is_not_a_shop()
    {
        var h = new Harness();
        var elf = h.Elf();
        await h.Cast(elf);
        var summon = h.Summons.Of(elf)!;
        var ordinary = h.Target(105, 100);
        var npc = h.Target(106, 100);
        npc.ShopNumber = 3;

        Assert.That(summon.CanBeAttacked, Is.False);
        Assert.That(npc.CanBeAttacked, Is.False);
        Assert.That(ordinary.CanBeAttacked, Is.True);
    }
}

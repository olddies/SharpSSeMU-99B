using MuServer.GameServer.Config;
using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The Chaos Castle, played on a clock the test moves: the entrance opens, the players pay and go in, wait,
/// fight while the arena shrinks, and the rewards. The map, monsters and messages are small stand-ins in the format of
/// the real files.</summary>
public class ChaosCastleTests
{
    private const string Timing = """
        0
           5   1   10   4
        end
        1
           *   *   *   *   10   10   0
        end
        2
           0   1000   500
        end
        """;

    private const string EntryLevels = """
        2
           0   15   49    15   29
           1   50   119   30   99
        end
        """;

    private const string StageSpawns = """
        1
           0   0   162   *
           0   0   163   *
        end
        """;

    private const string Messages = """
        16  "Chaos Castle will be closed after %d minute(s)"
        17  "Chaos Castle is closed. Try again next time"
        18  "Chaos Castle %d will start in %d minute(s)"
        19  "Chaos Castle has started!"
        20  "Chaos Castle has finished"
        21  "Not enough warriors to start the Chaos Castle!"
        22  "You're not strong enough to enter Chaos Castle"
        159 "You can not enter with transformation rings or mounted on pets!"
        714 "You can only enter Chaos Castle %d times in a day"
        724 "Player %s won %d money!"
        end
        """;

    private const string Gates = """
        22   0   2    197   35   218   50   0   0   15   *   *   *
        82   0   18   30    80   32    83    0   0   0    *   *   *
        end
        """;

    private static readonly int Armor = Item.GetItem(13, 29);

    private sealed class Harness : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"cc-{Guid.NewGuid():N}");
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public MapRegistry Maps { get; } = new();
        public MonsterRegistry Monsters { get; } = new();
        public PlayerRegistry Players { get; } = new();
        public GroundItemRegistry Ground { get; } = new();
        public List<byte[]> DataServer { get; } = new();
        public List<(int Player, long Experience)> Experience { get; } = new();
        public List<(int Player, int Special, int Level, int State)> Bags { get; } = new();
        public ChaosCastleConfig Config { get; }
        public ChaosCastleManager Manager { get; }
        public List<PlayerObject> Heroes { get; } = new();
        public PlayerObject Player => Heroes[0];
        public FakeSession Session => (FakeSession)Player.Session;
        public int Saves { get; private set; }

        public Harness(int playerCount = 1, int playerLevel = 30, bool startsOnce = false)
        {
            Directory.CreateDirectory(Folder);
            string terrain = Path.Combine(Folder, "Terrain");
            Directory.CreateDirectory(terrain);

            // A flat map of 256x256 for the castle (map 18) and for Devias (map 2).
            var blank = new byte[3 + 65536];
            blank[1] = 255;
            blank[2] = 255;
            File.WriteAllBytes(Path.Combine(terrain, "Terrain19.att"), blank);
            File.WriteAllBytes(Path.Combine(terrain, "Terrain3.att"), blank);
            Maps.LoadAll(terrain);

            string Monster(int index, string name, int level, int life) =>
                $"{index} 0 \"{name}\" {level} {life} 0 10 20 5 0 100 100 3 0 1 3 400 2000 10 2 100 100 3 0 4 4 4 4 0 0 0";

            string monsterList = Path.Combine(Folder, "MonsterList.txt");
            File.WriteAllText(monsterList, string.Join("\n", Monster(162, "Chaos Castle Warrior", 20, 500), Monster(163, "Chaos Castle Mage", 20, 500), "end"));
            var monsterInfo = new MonsterInfoTable();
            monsterInfo.Load(monsterList);

            Config = Write(Timing, ChaosCastleConfig.Load);
            Config.Schedule.Clear();
            // 10:10 every day, or only once: with no start time left the castle stays shut for good
            Config.Schedule.Add(startsOnce ? new DevilSquareStartTime(2030, 1, 1, -1, 10, 10, 0) : new DevilSquareStartTime(-1, -1, -1, -1, 10, 10, 0));

            var entryLevels = Write(EntryLevels, EventEntryLevelTable.Load);
            var stageSpawns = Write(StageSpawns, EventStageSpawnTable.Load);

            var pool = new List<MonsterSpawnEntry>
            {
                new() { Type = 4, MonsterClass = 162, Map = 18, X = 30, Y = 90 },
                new() { Type = 4, MonsterClass = 162, Map = 18, X = 31, Y = 91 },
                new() { Type = 4, MonsterClass = 163, Map = 18, X = 32, Y = 92 },
            };

            var gates = new GateTable();
            WriteTo("Gate.txt", Gates, gates.Load);

            var messages = new MessageTable();
            WriteTo("Message.txt", Messages, messages.Load);

            Manager = new ChaosCastleManager(Config, entryLevels, stageSpawns, pool, monsterInfo, Maps, Monsters, Players,
                gates, Ground, (packet, _) => { DataServer.Add(packet); return Task.CompletedTask; }, messages)
            {
                Clock = () => Now,
                GrantExperience = (player, experience, _) => { Experience.Add((player.Index, experience)); return Task.CompletedTask; },
                GiveItemBag = (player, special, level, state, _) => { Bags.Add((player.Index, special, level, state)); return Task.CompletedTask; },
                SaveCharacter = (_, _) => { Saves++; return Task.CompletedTask; },
            };

            for (int n = 0; n < playerCount; n++)
            {
                var hero = new PlayerObject
                {
                    Index = 9000 + n, Session = FakeSession.Create(), Class = 1, Level = (ushort)playerLevel, Name = $"Hero{n + 1}", Account = $"acc{n + 1}",
                    Map = 2, X = 200, Y = 40, WorldEntered = true, Money = 100_000,
                };
                hero.SetItem(13, new Item { Index = (short)Armor, Durability = 1 });
                Heroes.Add(hero);
                Players.Add(hero);
            }
        }

        private T Write<T>(string text, Func<string, T> load)
        {
            string path = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(path, text);
            return load(path);
        }

        private void WriteTo(string name, string text, Action<string> load)
        {
            string path = Path.Combine(Folder, name);
            File.WriteAllText(path, text);
            load(path);
        }

        public ChaosCastleLevel Castle => Manager.Levels[0];

        public FakeSession SessionOf(PlayerObject hero) => (FakeSession)hero.Session;

        /// <summary>Moves the clock and lets one second of the event pass.</summary>
        public async Task TickAfterAsync(TimeSpan time)
        {
            Now += time;
            await Manager.TickAllAsync(CancellationToken.None);
        }

        public async Task EnterAsync(PlayerObject hero) =>
            await Manager.HandleEnterAsync(SessionOf(hero), hero, 13, CancellationToken.None);

        /// <summary>The entrance opens at 10:05; everybody goes in.</summary>
        public async Task OpenAndEnterAllAsync()
        {
            await Manager.InitializeAsync(CancellationToken.None);
            await TickAfterAsync(TimeSpan.FromMinutes(6));

            foreach (var hero in Heroes)
            {
                await EnterAsync(hero);
            }
        }

        /// <summary>From the entrance to the fight: the wait ends at 10:10, the fight starts a minute later.</summary>
        public async Task StartFightAsync()
        {
            await OpenAndEnterAllAsync();
            await TickAfterAsync(TimeSpan.FromMinutes(4));  // 10:10 -> STAND
            await TickAfterAsync(TimeSpan.FromMinutes(1));  // 10:11 -> START
        }

        public Monster? Fighter(int monsterClass) =>
            Monsters.All.FirstOrDefault(m => m.MonsterClass == monsterClass && m.ChaosCastleLevel == 0 && !m.IsDead);

        public async Task KillAsync(Monster monster, PlayerObject by)
        {
            monster.DamageByAttacker[by.Index] = 50;
            monster.Life = 0;
            monster.Live = false;
            await Manager.OnMonsterKilledAsync(monster, CancellationToken.None);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cctmp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Sets server-wide event settings for one test and puts them back.</summary>
    private static IDisposable Settings(int minUser = 1, int blowRate = 0, int moneyRate = 80, int pkFree = 0, int pkEntry = 1)
    {
        var info = WorldPacketBuilder.ServerInfo;
        var before = (info.ChaosCastleEvent, info.ChaosCastleMinUser, info.ChaosCastleBlowUserRate, info.ChaosCastleMoneyRate, info.PKLimitFree, info.PKLimitEventEntry);
        (info.ChaosCastleEvent, info.ChaosCastleMinUser, info.ChaosCastleBlowUserRate, info.ChaosCastleMoneyRate, info.PKLimitFree, info.PKLimitEventEntry) =
            (true, minUser, blowRate, moneyRate, pkFree, pkEntry);

        return new Restore(() =>
            (info.ChaosCastleEvent, info.ChaosCastleMinUser, info.ChaosCastleBlowUserRate, info.ChaosCastleMoneyRate, info.PKLimitFree, info.PKLimitEventEntry) = before);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _undo;

        public Restore(Action undo) => _undo = undo;

        public void Dispose() => _undo();
    }

    // ---------------------------------------------------------------- the files

    [Test]
    public void The_config_reads_the_timing_the_schedule_and_the_experience()
    {
        using var h = new Harness();
        var config = h.Config;

        Assert.That((config.WarningMinutes, config.NotifyMinutes, config.EventMinutes, config.CloseMinutes), Is.EqualTo((5, 1, 10, 4)));
        Assert.That(config.Experience[0], Is.EqualTo(new[] { 1000, 500 }));
        Assert.That(config.Experience[1], Is.EqualTo(new[] { 0, 0 }));
    }

    [Test]
    public void The_real_file_is_read_when_present()
    {
        string? file = null;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && file == null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "MuServer99B", "Data", "Event", "ChaosCastle.dat");
            file = File.Exists(candidate) ? candidate : null;
        }

        Assume.That(file, Is.Not.Null, "the game data package is not next to the repo");

        var config = ChaosCastleConfig.Load(file!);
        Assert.That((config.WarningMinutes, config.NotifyMinutes, config.EventMinutes, config.CloseMinutes), Is.EqualTo((5, 1, 10, 4)));
        Assert.That(config.Schedule, Has.Count.EqualTo(12), "every two hours");
        Assert.That(config.Experience[0], Is.EqualTo(new[] { 1000, 500 }));
        Assert.That(config.Experience[5], Is.EqualTo(new[] { 3500, 3000 }));
    }

    [Test]
    public void Entry_levels_and_stage_spawns_have_their_chaos_castle_section()
    {
        var levels = EventEntryLevelTable.Load(WriteTemp(EntryLevels));

        Assert.That(levels.GetChaosCastleLevel(30, false), Is.EqualTo(0));
        Assert.That(levels.GetChaosCastleLevel(60, false), Is.EqualTo(1));
        Assert.That(levels.GetChaosCastleLevel(14, false), Is.EqualTo(-1));
        Assert.That(levels.GetChaosCastleLevel(15, true), Is.EqualTo(0));
        Assert.That(levels.GetChaosCastleLevel(30, true), Is.EqualTo(1), "special classes move up sooner");

        var spawns = EventStageSpawnTable.Load(WriteTemp(StageSpawns));
        Assert.That(spawns.GetChaosCastleEntries(0, 0).Select(e => e.MonsterClass), Is.EqualTo(new[] { 162, 163 }));
        Assert.That(spawns.GetChaosCastleEntries(0, 0).All(e => e.MaxRegenMs == -1), Is.True, "the star: they do not come back");
        Assert.That(spawns.GetChaosCastleEntries(1, 0), Is.Empty);
        Assert.That(spawns.GetBloodCastleEntries(0, 0), Is.Empty, "Blood Castle's rows are another section");
    }

    // ---------------------------------------------------------------- the opening and the entry

    [Test]
    public async Task The_castle_starts_closed_and_opens_its_entrance_before_the_start_time()
    {
        using var _ = Settings();
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Empty));
        Assert.That(h.Castle.EnterEnabled, Is.False);
        Assert.That(h.Castle.TargetTime, Is.EqualTo(new DateTime(2030, 1, 1, 10, 10, 0, DateTimeKind.Utc)));
        Assert.That(h.Maps.GetMap(18)!.CheckAttr(30, 90, 1), Is.True, "the waiting room is a safe zone");

        await h.TickAfterAsync(TimeSpan.FromMinutes(1)); // 9 minutes to go: not yet
        Assert.That(h.Castle.EnterEnabled, Is.False);

        await h.TickAfterAsync(TimeSpan.FromMinutes(4)); // 4 minutes to go: the warning window of 5
        Assert.That(h.Castle.EnterEnabled, Is.True);
        Assert.That(h.Session.Notices().Last(), Is.EqualTo("Chaos Castle will be closed after 5 minute(s)"));
    }

    [Test]
    public async Task Entering_costs_the_fee_and_the_armor_and_counts_for_the_day()
    {
        using var _ = Settings();
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(6));

        await h.EnterAsync(h.Player);

        Assert.That(h.Session.Of(0xAF).Last()[3], Is.EqualTo(0x01));
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(0), "in");
        Assert.That(h.Castle.Users, Has.Count.EqualTo(1));
        Assert.That(h.Player.Money, Is.EqualTo(75_000u), "25,000 for the first castle");
        Assert.That(h.Player.Items[13].IsItem(), Is.False, "the Armor of Guardsman is used up");
        Assert.That(h.Player.Map, Is.EqualTo(18));
        Assert.That(h.Player.X, Is.InRange(30, 32));
        Assert.That(h.Player.Y, Is.InRange(80, 83));
        Assert.That(h.Player.CCCount, Is.EqualTo(1));
        Assert.That(h.Saves, Is.EqualTo(1));
    }

    [Test]
    public async Task Entry_is_refused_for_each_wrong_thing_without_taking_anything()
    {
        using var _ = Settings();
        using var h = new Harness();
        var hero = h.Player;
        await h.Manager.InitializeAsync(CancellationToken.None);

        // Closed.
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(2));

        await h.TickAfterAsync(TimeSpan.FromMinutes(6));

        // No armor in that slot.
        await h.Manager.HandleEnterAsync(h.Session, hero, 14, CancellationToken.None);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(1));

        // In a party.
        hero.PartyNumber = 3;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(1));
        hero.PartyNumber = -1;

        // In the middle of a duel.
        hero.DuelUser = 9005;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(1));
        hero.DuelUser = -1;

        // Not enough zen.
        hero.Money = 24_999;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(7));
        hero.Money = 100_000;

        // A murderer.
        hero.PKLevel = 5;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF).Last()[4], Is.EqualTo(8));
        hero.PKLevel = 3;

        // Mounted on a Horn of Dinorant: a notice, no answer.
        hero.SetItem(Item.SlotHelper, new Item { Index = (short)Item.GetItem(13, 3), Durability = 255 });
        int answers = h.Session.Of(0xAF).Count;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Of(0xAF), Has.Count.EqualTo(answers));
        Assert.That(h.Session.Notices().Last(), Is.EqualTo("You can not enter with transformation rings or mounted on pets!"));
        hero.SetItem(Item.SlotHelper, Item.Empty());

        // Too weak: a notice too.
        hero.Level = 10;
        await h.EnterAsync(hero);
        Assert.That(h.Session.Notices().Last(), Is.EqualTo("You're not strong enough to enter Chaos Castle"));
        hero.Level = 30;

        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(hero.Money, Is.EqualTo(100_000u));
        Assert.That(hero.Items[13].IsItem(), Is.True);
        Assert.That(hero.CCCount, Is.EqualTo(0));
    }

    [Test]
    public async Task The_daily_cap_stops_the_entry_with_a_notice()
    {
        using var _ = Settings();
        var caps = WorldPacketBuilder.ServerInfo.ChaosCastleMaxEntryCount;
        int[] before = caps.ToArray();

        try
        {
            caps[0] = 2;
            using var h = new Harness();
            await h.Manager.InitializeAsync(CancellationToken.None);
            await h.TickAfterAsync(TimeSpan.FromMinutes(6));

            h.Player.CCCount = 2;
            await h.EnterAsync(h.Player);

            Assert.That(h.Session.Notices().Last(), Is.EqualTo("You can only enter Chaos Castle 2 times in a day"));
            Assert.That(h.Castle.Users, Is.Empty);
            Assert.That(h.Player.Items[13].IsItem(), Is.True);
        }
        finally
        {
            before.CopyTo(caps, 0);
        }
    }

    [Test]
    public async Task Asking_the_remain_time_gives_the_next_start_or_the_players_inside()
    {
        using var _ = Settings();
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);

        await h.Manager.HandleRemainTimeQueryAsync(h.Session, h.Player, CancellationToken.None);
        var closed = h.Session.Of(0x91).Last();
        Assert.That((closed[3], closed[4], closed[5], closed[6]), Is.EqualTo(((byte)4, (byte)0, (byte)0, (byte)10)), "ten minutes to go, in the low byte");

        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        await h.EnterAsync(h.Player);
        await h.Manager.HandleRemainTimeQueryAsync(h.Session, h.Player, CancellationToken.None);
        var open = h.Session.Of(0x91).Last();
        Assert.That((open[3], open[4], open[5], open[6]), Is.EqualTo(((byte)4, (byte)0, (byte)1, (byte)0)), "one player inside");
    }

    // ---------------------------------------------------------------- the wait and the start

    [Test]
    public async Task When_the_wait_begins_a_random_participant_wins_a_share_of_the_fees()
    {
        using var _ = Settings(moneyRate: 80);
        using var h = new Harness(playerCount: 2);
        await h.OpenAndEnterAllAsync();
        long before = h.Heroes.Sum(p => (long)p.Money);

        await h.TickAfterAsync(TimeSpan.FromMinutes(4)); // the start time: STAND

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Stand));
        long pot = 2 * (25_000 * 80 / 100);
        Assert.That(h.Heroes.Sum(p => (long)p.Money), Is.EqualTo(before + pot), "exactly one of them got it");
        Assert.That(h.Session.Notices().Any(n => n.StartsWith("Player Hero") && n.EndsWith($"won {pot} money!")), Is.True);
    }

    [Test]
    public async Task Too_few_players_send_everybody_out_and_close_the_castle_again()
    {
        using var _ = Settings(minUser: 2);
        using var h = new Harness();
        await h.OpenAndEnterAllAsync();
        await h.TickAfterAsync(TimeSpan.FromMinutes(4)); // STAND
        await h.TickAfterAsync(TimeSpan.FromSeconds(1)); // the check: one is not enough

        Assert.That(h.Session.Notices().Last(), Is.EqualTo("Not enough warriors to start the Chaos Castle!"));
        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Empty));
        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(h.Player.Map, Is.EqualTo(2), "back in Devias");
    }

    [Test]
    public async Task The_fight_starts_with_the_safe_zone_gone_and_a_monster_for_every_spot()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Start));
        Assert.That(h.Session.Notices(), Does.Contain("Chaos Castle has started!"));
        Assert.That(h.Maps.GetMap(18)!.CheckAttr(30, 90, 1), Is.False, "the safe zone is gone");
        Assert.That(h.Castle.MonsterIndices, Has.Count.EqualTo(3));
        Assert.That(h.Monsters.All.Count(m => m.ChaosCastleLevel == 0 && m.NoRespawn), Is.EqualTo(3), "they do not come back");
        Assert.That(h.Castle.MaxMonster, Is.EqualTo(5), "the players plus the monsters");
        Assert.That(h.Manager.IsFightOn(18), Is.True);
        Assert.That(h.Manager.IsFightOn(2), Is.False);
    }

    [Test]
    public async Task Only_the_fight_itself_lets_players_hurt_each_other()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.OpenAndEnterAllAsync();

        Assert.That(h.Manager.IsFightOn(18), Is.False, "waiting for the start");

        await h.TickAfterAsync(TimeSpan.FromMinutes(4));
        Assert.That(h.Manager.IsFightOn(18), Is.False, "waiting in the safe zone");

        await h.TickAfterAsync(TimeSpan.FromMinutes(1));
        Assert.That(h.Manager.IsFightOn(18), Is.True);
    }

    // ---------------------------------------------------------------- the arena shrinks

    [Test]
    public async Task As_the_numbers_fall_the_rings_fall_away_and_whoever_stands_on_one_dies()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();
        var map = h.Maps.GetMap(18)!;
        var survivor = h.Heroes[0];
        var unlucky = h.Heroes[1];

        // 2 players and 3 monsters: 5 are left, fewer than 40, so the first ring falls on the first second.
        survivor.X = 33; survivor.Y = 95;   // the middle of the arena
        unlucky.X = 24; unlucky.Y = 90;     // on the first ring (the west column, x 23-24)

        await h.TickAfterAsync(TimeSpan.FromSeconds(1));

        Assert.That(h.Castle.Stage, Is.EqualTo(1));
        Assert.That(map.CheckAttr(24, 90, 8), Is.True, "the ring is down");
        Assert.That(unlucky.IsDying, Is.True, "standing on it was fatal");
        Assert.That(unlucky.Life, Is.EqualTo(0u));
        Assert.That(h.Castle.Users.Select(u => u.PlayerIndex), Is.EqualTo(new[] { survivor.Index }));
        Assert.That(survivor.IsDying, Is.False);
        Assert.That(h.SessionOf(unlucky).Of(0x17), Is.Not.Empty, "the death is told");
        Assert.That(h.SessionOf(unlucky).Of(0x93), Is.Empty, "the score comes eight seconds later");

        await h.TickAfterAsync(TimeSpan.FromSeconds(8));
        Assert.That(h.SessionOf(unlucky).Of(0x93), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task The_second_and_third_rings_fall_when_few_enough_are_left()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 1);
        await h.StartFightAsync();
        var map = h.Maps.GetMap(18)!;
        h.Player.X = 33; h.Player.Y = 95;

        await h.TickAfterAsync(TimeSpan.FromSeconds(1)); // 1 player + 3 monsters: stage 1
        Assert.That(h.Castle.Stage, Is.EqualTo(1));
        Assert.That(map.CheckAttr(26, 90, 8), Is.False, "the second ring is still there");

        await h.TickAfterAsync(TimeSpan.FromSeconds(1)); // still few: stage 2
        Assert.That(h.Castle.Stage, Is.EqualTo(2));
        Assert.That(map.CheckAttr(26, 90, 8), Is.True);

        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.Stage, Is.EqualTo(3));
        Assert.That(map.CheckAttr(28, 90, 8), Is.True);
    }

    [Test]
    public async Task A_monster_on_a_fallen_tile_is_moved_to_a_free_one_and_an_item_there_disappears()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 1);
        await h.StartFightAsync();
        h.Player.X = 33; h.Player.Y = 95;

        var monster = h.Fighter(162)!;
        monster.X = 24; monster.Y = 90;
        var lost = h.Ground.Drop(18, new Item { Index = (short)Item.GetItem(14, 0), Durability = 1 }, 24, 100, h.Player.Index, null, TimeSpan.FromMinutes(1), TimeSpan.Zero)!;

        await h.TickAfterAsync(TimeSpan.FromSeconds(1)); // the first ring falls

        Assert.That(h.Maps.GetMap(18)!.CheckAttr(monster.X, monster.Y, 8), Is.False, "it was moved off the fallen ring");
        Assert.That(lost.ExpireAt, Is.LessThan(DateTime.UtcNow), "the ticker's sweep takes the item away");
    }

    // ---------------------------------------------------------------- scoring and blows

    [Test]
    public async Task A_monster_killed_scores_two_for_whoever_hurt_it_most()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 1);
        await h.StartFightAsync();

        var monster = h.Fighter(162)!;
        await h.KillAsync(monster, h.Player);

        var user = h.Castle.Users.Single();
        Assert.That((user.Score, user.MonsterKillCount, user.UserKillCount), Is.EqualTo((2, 1, 0)));
        Assert.That(h.Castle.MonsterIndices, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task A_monster_killed_may_blow_the_players_near_it_away()
    {
        using var _ = Settings(blowRate: 100);
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();
        var near = h.Heroes[0];
        var far = h.Heroes[1];
        var monster = h.Fighter(162)!;
        monster.X = 40; monster.Y = 90;
        near.X = 41; near.Y = 90;
        far.X = 50; far.Y = 100;

        await h.KillAsync(monster, far);

        Assert.That(((int)near.X, (int)near.Y), Is.Not.EqualTo((41, 90)), "thrown away from the monster");
        Assert.That(near.X, Is.GreaterThan((byte)41), "away: to the east of it");
        Assert.That(near.OldX, Is.EqualTo(near.X));
        Assert.That(h.SessionOf(near).Of(0xD0), Is.Not.Empty, "the player is told where they landed");
        Assert.That((near.ChaosCastleBlowTime - DateTime.UtcNow).Duration().TotalSeconds, Is.LessThan(2), "and cannot walk for a second");
        Assert.That(((int)far.X, (int)far.Y), Is.EqualTo((50, 100)), "the one far away stays");
    }

    [Test]
    public async Task A_player_killed_by_another_is_out_with_the_reward_so_far_and_the_killer_scores_one()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();
        var killer = h.Heroes[0];
        var victim = h.Heroes[1];

        await h.KillAsync(h.Fighter(162)!, victim); // the victim has a monster kill: 500 experience
        await h.Manager.OnPlayerDiedAsync(victim, killer.Index, CancellationToken.None);

        Assert.That(h.Castle.Users.Select(u => u.PlayerIndex), Is.EqualTo(new[] { killer.Index }));
        var user = h.Castle.Users.Single();
        Assert.That((user.Score, user.UserKillCount), Is.EqualTo((1, 1)));
        Assert.That(h.Experience, Is.EqualTo(new[] { (victim.Index, 500L) }), "the victim gets what they earned");
    }

    [Test]
    public async Task A_monster_or_the_void_killing_a_player_scores_nothing_for_anyone()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();

        await h.Manager.OnPlayerDiedAsync(h.Heroes[1], killerIndex: 5, CancellationToken.None); // a monster's index

        Assert.That(h.Castle.Users, Has.Count.EqualTo(1));
        Assert.That(h.Castle.Users.Single().Score, Is.EqualTo(0));
    }

    [Test]
    public async Task A_castle_position_report_is_taken_only_near_where_the_player_is()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 1);
        await h.StartFightAsync();
        h.Player.X = 35; h.Player.Y = 90;

        await h.Manager.HandlePositionAsync(h.Player, 38, 92, CancellationToken.None);
        Assert.That((int)h.Player.X, Is.EqualTo(38));
        Assert.That((int)h.Player.Y, Is.EqualTo(92));

        await h.Manager.HandlePositionAsync(h.Player, 100, 100, CancellationToken.None);
        Assert.That((int)h.Player.X, Is.EqualTo(38), "a jump across the arena is not believed");

        h.Player.Map = 2;
        await h.Manager.HandlePositionAsync(h.Player, 39, 92, CancellationToken.None);
        Assert.That((int)h.Player.X, Is.EqualTo(38), "and outside a castle it means nothing");
    }

    // ---------------------------------------------------------------- the end

    [Test]
    public async Task With_every_monster_dead_the_last_player_standing_wins_and_everyone_is_paid()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();
        var winner = h.Heroes[0];
        var loser = h.Heroes[1];
        winner.X = 33; winner.Y = 95;

        foreach (var monster in h.Monsters.All.Where(m => m.ChaosCastleLevel == 0).ToList())
        {
            await h.KillAsync(monster, winner); // 3 monsters: 6 points, 1,500 experience
        }

        await h.Manager.OnPlayerDiedAsync(loser, winner.Index, CancellationToken.None); // 1 point, 1,000 experience
        h.Experience.Clear();

        await h.TickAfterAsync(TimeSpan.FromSeconds(1)); // one player left and no monsters: the end

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Clean));
        Assert.That(h.Castle.WinnerIndex, Is.EqualTo(winner.Index));
        Assert.That(h.Experience, Is.EqualTo(new[] { (winner.Index, 1000L + 1500L) }), "a kill of a player and three of monsters");
        Assert.That(h.Bags, Is.EqualTo(new[] { (winner.Index, ItemBagManager.SpecialChaosCastle, 0, 1) }), "the prize bag is the winner's");

        var score = h.SessionOf(winner).Of(0x93).Last();
        Assert.That(score[3], Is.EqualTo(1), "a win");
        Assert.That(score[4], Is.EqualTo(0xFE));

        // The ranking keeps a fifth of the experience.
        var ranking = h.DataServer.Single(p => p[2] == EventDataServerPacketBuilder.HeadChaosCastle);
        Assert.That(BitConverter.ToUInt32(ranking, ranking.Length - 4), Is.EqualTo(500u));
    }

    [Test]
    public async Task Nobody_wins_while_a_monster_is_left_when_the_time_runs_out()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();

        await h.TickAfterAsync(TimeSpan.FromMinutes(10)); // the event time

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Clean));
        Assert.That(h.Castle.WinnerIndex, Is.EqualTo(-1));
        Assert.That(h.Bags, Is.Empty);
        Assert.That(h.Session.Notices(), Does.Contain("Chaos Castle has finished"));
        Assert.That(h.Session.Of(0x93).Last()[3], Is.EqualTo(0), "no win for anyone");
        Assert.That(h.Castle.MonsterIndices, Is.Empty, "the monsters are gone");
        Assert.That(h.Monsters.All.Count(m => m.ChaosCastleLevel == 0), Is.EqualTo(0));
    }

    [Test]
    public async Task After_the_close_time_everybody_is_sent_out_and_the_castle_waits_for_the_next_start()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 1);
        await h.StartFightAsync();
        await h.TickAfterAsync(TimeSpan.FromMinutes(10)); // CLEAN

        await h.TickAfterAsync(TimeSpan.FromMinutes(4)); // the close time

        Assert.That(h.Castle.State, Is.EqualTo(ChaosCastleState.Empty));
        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(h.Player.Map, Is.EqualTo(2), "back in Devias");
        Assert.That(h.Maps.GetMap(18)!.CheckAttr(30, 90, 1), Is.True, "the waiting room is safe again");
        Assert.That(h.Maps.GetMap(18)!.CheckAttr(24, 90, 8), Is.False, "the rings are back");
        Assert.That(h.Castle.TargetTime, Is.GreaterThan(h.Now), "waiting for the next start");
    }

    [Test]
    public async Task A_castle_with_no_start_time_left_still_sends_the_scores_that_are_waiting()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.StartFightAsync();
        var winner = h.Heroes[0];
        var loser = h.Heroes[1];

        await h.Manager.OnPlayerDiedAsync(loser, winner.Index, CancellationToken.None);

        // The castle has just been closed for good: its schedule has no next start (what CheckSync does then).
        h.Castle.State = ChaosCastleState.Blank;

        await h.TickAfterAsync(TimeSpan.FromSeconds(7));
        Assert.That(h.SessionOf(loser).Of(0x93), Is.Empty, "not yet: eight seconds after the death");

        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.SessionOf(loser).Of(0x93), Has.Count.EqualTo(1), "it comes anyway");
    }

    [Test]
    public async Task A_player_who_leaves_the_game_stops_counting()
    {
        using var _ = Settings();
        using var h = new Harness(playerCount: 2);
        await h.OpenAndEnterAllAsync();

        await h.Manager.OnPlayerLeftAsync(h.Heroes[1], CancellationToken.None);

        Assert.That(h.Castle.Users.Select(u => u.PlayerIndex), Is.EqualTo(new[] { h.Heroes[0].Index }));
    }
}

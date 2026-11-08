using System.Runtime.CompilerServices;
using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>A client session that keeps what the server sends instead of writing it to a socket.</summary>
internal sealed class FakeSession : ClientSession
{
    public List<byte[]> Sent { get; private set; } = null!;

    public static FakeSession Create()
    {
        var session = (FakeSession)RuntimeHelpers.GetUninitializedObject(typeof(FakeSession));
        session.Sent = new List<byte[]>();
        return session;
    }

    public override Task SendAsync(byte[] logicalPacket, CancellationToken ct)
    {
        Sent.Add(logicalPacket);
        return Task.CompletedTask;
    }

    public override Task SendEncryptedAsync(byte[] logicalC3Packet, CancellationToken ct)
    {
        Sent.Add(logicalC3Packet);
        return Task.CompletedTask;
    }

    /// <summary>The C1 packets with this head, e.g. 0x9A (entry answer), 0x9B (state), 0x93 (score).</summary>
    public List<byte[]> Of(byte head) => Sent.Where(p => p.Length > 2 && p[0] == 0xC1 && p[2] == head).ToList();

    /// <summary>The server commands (C1:F3:40) sent, as (type, x).</summary>
    public List<(byte Type, byte X)> Commands() =>
        Sent.Where(p => p.Length >= 7 && p[0] == 0xC1 && p[2] == 0xF3 && p[3] == 0x40).Select(p => (p[4], p[5])).ToList();
}

/// <summary>The Blood Castle, played start to finish on a clock the test moves: the entrance opens, the player goes
/// in, the monsters, the bridge, the gate, the statue and the Archangel's weapon, and the rewards. The map, monsters
/// and messages are small stand-ins in the format of the real files.</summary>
public class BloodCastleTests
{
    private const string Timing = """
        0
           5   1   1   1   1
        end
        1
           *   *   *   *   10   10   0
        end
        2
           0   1000   500
        end
        3
           0   2000   1000
        end
        4
           0   100    100
        end
        5
           0   14   95
        end
        """;

    private const string EntryLevels = """
        0
           0   15   80    10   60
           1   81   130   61   110
        end
        """;

    private const string StageSpawns = """
        0
           0   0   84   1000
           0   3   89   1000
        end
        """;

    private const string Messages = """
        0   "Blood Castle will be closed after %d minute(s)"
        1   "Blood Castle is closed. Try again next time"
        2   "Blood Castle %d will start in %d minute(s)"
        3   "Blood Castle has started!"
        4   "Monsters eliminated!! Destroy the castle gates!"
        5   "Kundun's minions have been subdued! Destroy the crystal statue!"
        6   "Blood Castle has finished"
        7   "You're not strong enough to enter Blood Castle"
        8   "%s has dropped the Absolute Weapon of Archangel!"
        9   "%s has acquired the Absolute Weapon of Archangel!"
        10  "%s has demolished the castle gates!"
        11  "%s has demolished the crystal statue!"
        12  "Blood Castle has been successfully completed by %s!"
        end
        """;

    private const string Gates = """
        22   0   2    197   35   218   50   0   0   15   *   *   *
        66   0   11   12    5    14    10   0   0   0    *   *   *
        end
        """;

    private static readonly int Cloak = Item.GetItem(13, 18);
    private static readonly int Weapon = Item.GetItem(13, 19);

    private sealed class Harness : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"bc-{Guid.NewGuid():N}");
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public MapRegistry Maps { get; } = new();
        public MonsterRegistry Monsters { get; } = new();
        public PlayerRegistry Players { get; } = new();
        public GroundItemRegistry Ground { get; } = new();
        public List<byte[]> DataServer { get; } = new();
        public List<(int Player, long Experience)> Experience { get; } = new();
        public List<(int Player, int Special, int Level, int State)> Bags { get; } = new();
        public BloodCastleConfig Config { get; }
        public BloodCastleManager Manager { get; }
        public FakeSession Session { get; } = FakeSession.Create();
        public PlayerObject Player { get; }

        public Harness(int playerLevel = 30)
        {
            Directory.CreateDirectory(Folder);
            string terrain = Path.Combine(Folder, "Terrain");
            Directory.CreateDirectory(terrain);

            // A flat map of 256x256 for the castle (map 11) and for Devias (map 2).
            var blank = new byte[3 + 65536];
            blank[1] = 255;
            blank[2] = 255;
            File.WriteAllBytes(Path.Combine(terrain, "Terrain12.att"), blank);
            File.WriteAllBytes(Path.Combine(terrain, "Terrain3.att"), blank);
            Maps.LoadAll(terrain);

            string Monster(int index, string name, int level, int life) =>
                $"{index} 0 \"{name}\" {level} {life} 0 10 20 5 0 100 100 3 0 1 3 400 2000 10 2 100 100 3 0 4 4 4 4 0 0 0";

            string monsterList = Path.Combine(Folder, "MonsterList.txt");
            File.WriteAllText(monsterList, string.Join("\n",
                Monster(84, "Chief Skeleton Warrior", 64, 7500), Monster(89, "Magic Skeleton", 84, 24000),
                Monster(131, "Castle Gate", 2, 2000), Monster(132, "Statue of Saint", 2, 3000),
                Monster(133, "Statue of Saint", 2, 3000), Monster(134, "Statue of Saint", 2, 3000), "end"));
            var monsterInfo = new MonsterInfoTable();
            monsterInfo.Load(monsterList);

            Config = LoadConfig(Timing);
            Config.Schedule.Clear();
            Config.Schedule.Add(new DevilSquareStartTime(-1, -1, -1, -1, 10, 10, 0)); // 10:10 every day

            var entryLevels = Write(EntryLevels, EventEntryLevelTable.Load);
            var stageSpawns = Write(StageSpawns, EventStageSpawnTable.Load);

            var pool = new List<MonsterSpawnEntry>
            {
                new() { Type = 4, MonsterClass = 84, Map = 11, X = 13, Y = 30 },
                new() { Type = 4, MonsterClass = 84, Map = 11, X = 14, Y = 31 },
                new() { Type = 4, MonsterClass = 89, Map = 11, X = 14, Y = 82 },
                new() { Type = 4, MonsterClass = 89, Map = 11, X = 14, Y = 83 },
                new() { Type = 4, MonsterClass = 89, Map = 11, X = 14, Y = 84 },
            };

            var gates = new GateTable();
            WriteTo("Gate.txt", Gates, gates.Load);

            var messages = new MessageTable();
            WriteTo("Message.txt", Messages, messages.Load);

            Manager = new BloodCastleManager(Config, entryLevels, stageSpawns, pool, monsterInfo, Maps, Monsters, Players,
                gates, Ground, (packet, _) => { DataServer.Add(packet); return Task.CompletedTask; }, messages)
            {
                Clock = () => Now,
                GrantExperience = (player, experience, _) => { Experience.Add((player.Index, experience)); return Task.CompletedTask; },
                GiveItemBag = (player, special, level, state, _) => { Bags.Add((player.Index, special, level, state)); return Task.CompletedTask; },
            };

            Player = new PlayerObject
            {
                Index = 9000, Session = Session, Class = 1, Level = (ushort)playerLevel, Name = "Hero", Account = "test",
                Map = 2, X = 200, Y = 40, WorldEntered = true,
            };
            Player.SetItem(13, new Item { Index = (short)Cloak, Level = 1, Durability = 1 });
            Players.Add(Player);
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

        private BloodCastleConfig LoadConfig(string text) => Write(text, BloodCastleConfig.Load);

        public BloodCastleLevel Castle => Manager.Levels[0];

        /// <summary>Moves the clock and lets one second of the event pass.</summary>
        public async Task TickAfterAsync(TimeSpan time)
        {
            Now += time;
            await Manager.TickAllAsync(CancellationToken.None);
        }

        public Monster? FirstOfClass(int monsterClass) =>
            Monsters.All.FirstOrDefault(m => m.MonsterClass == monsterClass && m.BloodCastleLevel == 0 && !m.IsDead);

        public async Task KillAsync(Monster monster)
        {
            monster.DamageByAttacker[Player.Index] = 50;
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

    // ---------------------------------------------------------------- the files

    [Test]
    public void The_config_reads_the_timing_the_schedule_and_the_tables()
    {
        using var h = new Harness();
        var config = h.Config;

        Assert.That((config.WarningMinutes, config.NotifyMinutes, config.EventMinutes, config.CloseMinutes), Is.EqualTo((5, 1, 1, 1)));
        Assert.That(config.MonstersPerUser, Is.EqualTo(1), "the optional fifth number");
        Assert.That(config.Experience[0], Is.EqualTo(new[] { 1000, 500 }));
        Assert.That(config.Money[0], Is.EqualTo(new[] { 2000, 1000 }));
        Assert.That(config.NpcLife[0], Is.EqualTo(new[] { 100, 100 }));
        Assert.That(config.SaintStatue[0], Is.EqualTo(new[] { 14, 95 }));
    }

    [Test]
    public void Without_the_fifth_number_a_player_asks_for_forty_monsters()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bcdat-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "0\n 5 1 15 4\nend\n\n1\n * * * * 0 0 0\nend\n");

        try
        {
            var config = BloodCastleConfig.Load(path);
            Assert.That((config.WarningMinutes, config.NotifyMinutes, config.EventMinutes, config.CloseMinutes), Is.EqualTo((5, 1, 15, 4)));
            Assert.That(config.MonstersPerUser, Is.EqualTo(40));
            Assert.That(config.Schedule, Has.Count.EqualTo(1), "the section after the timing is read too");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void The_real_file_is_read_when_present()
    {
        string? dir = null;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && dir == null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "MuServer99B", "Data", "Event", "BloodCastle.dat");
            dir = File.Exists(candidate) ? candidate : null;
        }

        Assume.That(dir, Is.Not.Null, "the game data package is not next to the repo");

        var config = BloodCastleConfig.Load(dir!);
        Assert.That((config.WarningMinutes, config.NotifyMinutes, config.EventMinutes, config.CloseMinutes), Is.EqualTo((5, 1, 15, 4)));
        Assert.That(config.Schedule, Has.Count.EqualTo(12), "every two hours");
        Assert.That(config.Experience[0], Is.EqualTo(new[] { 20000, 5000 }));
        Assert.That(config.NpcLife[0], Is.EqualTo(new[] { 65000, 150000 }));
        Assert.That(config.SaintStatue[6], Is.EqualTo(new[] { 14, 95 }));
    }

    [Test]
    public void Messages_are_filled_in_like_a_printf()
    {
        Assert.That(BloodCastleManager.Fmt("Blood Castle %d will start in %d minute(s)", 1, 5), Is.EqualTo("Blood Castle 1 will start in 5 minute(s)"));
        Assert.That(BloodCastleManager.Fmt("%s has acquired it!", "Hero"), Is.EqualTo("Hero has acquired it!"));
        Assert.That(BloodCastleManager.Fmt("100% done"), Is.EqualTo("100% done"));
    }

    [Test]
    public void Entry_levels_and_stage_spawns_have_their_blood_castle_section()
    {
        using var h = new Harness();
        var levels = EventEntryLevelTable.Load(WriteTemp(EntryLevels));

        Assert.That(levels.GetBloodCastleLevel(30, false), Is.EqualTo(0));
        Assert.That(levels.GetBloodCastleLevel(100, false), Is.EqualTo(1));
        Assert.That(levels.GetBloodCastleLevel(14, false), Is.EqualTo(-1));
        Assert.That(levels.GetBloodCastleLevel(10, true), Is.EqualTo(0), "special classes get in earlier");

        var spawns = EventStageSpawnTable.Load(WriteTemp(StageSpawns));
        Assert.That(spawns.GetBloodCastleEntries(0, 0).Select(e => e.MonsterClass), Is.EqualTo(new[] { 84 }));
        Assert.That(spawns.GetBloodCastleEntries(0, 3).Select(e => e.MonsterClass), Is.EqualTo(new[] { 89 }));
        Assert.That(spawns.GetBloodCastleEntries(0, 1), Is.Empty);
        Assert.That(spawns.GetMonsterClasses(0, 0), Is.Empty, "Devil Square's rows are another section");
    }

    private static string WriteTemp(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"bctmp-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    // ---------------------------------------------------------------- the opening and the entry

    [Test]
    public async Task The_castle_starts_closed_and_opens_its_entrance_before_the_start_time()
    {
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);

        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Empty));
        Assert.That(h.Castle.EnterEnabled, Is.False);
        Assert.That(h.Castle.TargetTime, Is.EqualTo(new DateTime(2030, 1, 1, 10, 10, 0, DateTimeKind.Utc)));

        var map = h.Maps.GetMap(11)!;
        Assert.That(map.CheckAttr(14, 20, 4), Is.True, "the entrance zone is walled off");
        Assert.That(map.CheckAttr(14, 72, 8), Is.True, "so is the bridge");
        Assert.That(map.CheckAttr(14, 78, 4), Is.True, "and the way behind the gate");

        await h.TickAfterAsync(TimeSpan.FromMinutes(1)); // 9 minutes to go: not yet
        Assert.That(h.Castle.EnterEnabled, Is.False);

        await h.TickAfterAsync(TimeSpan.FromMinutes(4)); // 4 minutes to go: the warning window of 5
        Assert.That(h.Castle.EnterEnabled, Is.True);
    }

    [Test]
    public async Task Entry_is_checked_against_the_ticket_the_level_and_the_window()
    {
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);

        // Closed.
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(2));

        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        Assert.That(h.Castle.EnterEnabled, Is.True);

        // Not a ticket, a ticket of another castle, a castle that has no map here.
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 14, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(1));
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 2, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(1));
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 9, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(1));

        // Too strong / too weak for castle 1.
        h.Player.Level = 100;
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(3));
        h.Player.Level = 10;
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(4));

        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(h.Player.Items[13].IsItem(), Is.True, "nothing was used up");

        // In.
        h.Player.Level = 30;
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        Assert.That(h.Session.Of(0x9A).Last()[3], Is.EqualTo(0));
        Assert.That(h.Castle.Users, Has.Count.EqualTo(1));
        Assert.That(h.Player.Items[13].IsItem(), Is.False, "the cloak is used up");
        Assert.That(h.Player.Map, Is.EqualTo(11));
        Assert.That(h.Player.X, Is.InRange(12, 14));
        Assert.That(h.Player.Y, Is.InRange(5, 10));
    }

    [Test]
    public async Task A_castle_nobody_entered_goes_back_to_waiting()
    {
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        await h.TickAfterAsync(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1)); // the start time passes with nobody inside

        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Stand));
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Empty), "not enough players");
    }

    // ---------------------------------------------------------------- the whole event

    [Test]
    public async Task The_castle_can_be_won()
    {
        using var h = new Harness();
        var map = h.Maps.GetMap(11)!;

        await h.Manager.InitializeAsync(CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        Assert.That(h.Castle.Users, Has.Count.EqualTo(1));

        // 10:10 -- the entrance closes behind the player and the fight begins a minute later.
        await h.TickAfterAsync(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Stand));
        await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Start));
        Assert.That(h.Castle.MaxMonster, Is.EqualTo(1));
        Assert.That(map.CheckAttr(14, 20, 4), Is.False, "the entrance is open");
        Assert.That(h.Session.Of(0x46).Any(p => p[4] == 4 && p[5] == 1), Is.True, "and the player was told");

        var gate = h.FirstOfClass(131);
        Assert.That(gate, Is.Not.Null, "the castle gate stands");
        Assert.That(gate!.Immune, Is.True);
        Assert.That(gate.Life, Is.EqualTo(100));
        Assert.That(h.Monsters.All.Count(m => m.MonsterClass == 84), Is.EqualTo(2), "stage 0 monsters");

        // Stage 0: kill the monsters asked for.
        await h.KillAsync(h.FirstOfClass(84)!);
        Assert.That(h.Castle.CurMonster, Is.EqualTo(1));
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.Stage, Is.EqualTo(1));
        Assert.That(map.CheckAttr(14, 72, 8), Is.False, "the bridge opens on the server");

        // Stage 1 -> 2: three seconds later the clients get the bridge and the gate can be hurt.
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.Stage, Is.EqualTo(1), "not yet");
        await h.TickAfterAsync(TimeSpan.FromSeconds(3));
        Assert.That(h.Castle.Stage, Is.EqualTo(2));
        Assert.That(gate.Immune, Is.False);
        Assert.That(h.Session.Of(0x46).Any(p => p[4] == 8 && p[5] == 1), Is.True, "the clients' bridge");

        // Stage 2: break the gate.
        await h.KillAsync(gate);
        Assert.That(h.Castle.GateIndex, Is.EqualTo(-1));
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.Stage, Is.EqualTo(3));
        Assert.That(h.Castle.MaxMonster, Is.EqualTo(2), "two magic skeletons per player");
        Assert.That(map.CheckAttr(14, 78, 4), Is.False, "the way behind the gate is open");

        // Stage 3: the magic skeletons (and only them count now).
        var skeletons = h.Monsters.All.Where(m => m.MonsterClass == 89).ToList();
        Assert.That(skeletons, Has.Count.EqualTo(3));
        await h.KillAsync(h.FirstOfClass(84)!);
        Assert.That(h.Castle.CurMonster, Is.EqualTo(0), "a stage 0 monster no longer counts");
        await h.KillAsync(skeletons[0]);
        await h.KillAsync(skeletons[1]);
        Assert.That(h.Castle.CurMonster, Is.EqualTo(2));
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));

        // Stage 4: the statue.
        Assert.That(h.Castle.Stage, Is.EqualTo(4));
        var statue = h.Monsters.All.Single(m => m.MonsterClass is >= 132 and <= 134);
        Assert.That(statue.Immune, Is.False);
        Assert.That(statue.Life, Is.EqualTo(100));
        await h.KillAsync(statue);

        var onGround = h.Ground.AllOnMap(11).Single(g => g.Item.Index == Weapon);
        Assert.That(onGround.Item.Level, Is.EqualTo(statue.MonsterClass - 132), "the statue decides the weapon's level");
        Assert.That(h.Castle.EventItemSerial, Is.EqualTo(onGround.Item.Serial));

        // Pick it up and bring it to the Archangel.
        h.Ground.Remove(onGround);
        h.Player.SetItem(14, onGround.Item);
        await h.Manager.OnItemPickedUpAsync(h.Player, onGround.Item, CancellationToken.None);
        Assert.That(h.Castle.EventItemOwner, Is.EqualTo(h.Player.Index));

        await h.Manager.HandleAngelKingAsync(h.Player, CancellationToken.None);
        Assert.That(h.Castle.WinnerIndex, Is.EqualTo(h.Player.Index));
        Assert.That(h.Player.Items[14].IsItem(), Is.False, "the Archangel took the weapon");
        Assert.That(h.Session.Commands(), Does.Contain(((byte)1, (byte)23)));

        // Won: the castle closes and pays.
        uint zenBefore = h.Player.Money;
        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Clean));
        Assert.That(h.Player.Money - zenBefore, Is.EqualTo(2000u), "the winners' zen");
        Assert.That(h.Experience, Is.EqualTo(new[] { (9000, 1000L) }), "the winners' experience");
        Assert.That(h.Bags, Is.EqualTo(new[] { (9000, ItemBagManager.SpecialBloodCastle, 0, 1) }), "the prize bag");

        var score = h.Session.Of(0x93).Single();
        Assert.That(score[3], Is.EqualTo(1), "a winner's score window");
        Assert.That(h.DataServer.Single()[2], Is.EqualTo(0x3D), "ranking saved");
        Assert.That(h.Monsters.All.Where(m => m.BloodCastleLevel == 0), Is.Empty, "everything is cleared");

        // A minute later everybody is back in Devias and the castle waits for the next time.
        await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Empty));
        Assert.That(h.Player.Map, Is.EqualTo(2));
        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(map.CheckAttr(14, 20, 4), Is.True, "the entrance is closed again");
    }

    [Test]
    public async Task A_castle_that_runs_out_of_time_pays_the_losers_share()
    {
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));
        await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Start));

        uint zenBefore = h.Player.Money;
        await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)); // the event minute is over
        Assert.That(h.Castle.State, Is.EqualTo(BloodCastleState.Clean));

        Assert.That(h.Player.Money - zenBefore, Is.EqualTo(1000u));
        Assert.That(h.Experience.Single().Experience, Is.EqualTo(500));
        Assert.That(h.Session.Of(0x93).Single()[3], Is.EqualTo(0), "no winner");
        Assert.That(h.Bags, Is.Empty, "only the winners get the bag");
    }

    // ---------------------------------------------------------------- the weapon, deaths, leaving

    private static async Task<Harness> InFightAsync(bool holdingWeapon)
    {
        var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        await h.Manager.HandleEnterAsync(h.Session, h.Player, 1, 13, CancellationToken.None);
        await h.TickAfterAsync(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(1));
        await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));

        if (holdingWeapon)
        {
            var weapon = new Item { Index = (short)Weapon, Level = 1, Durability = 1, Serial = 777 };
            h.Castle.EventItemSerial = 777;
            h.Castle.EventItemLevel = 1;
            h.Player.SetItem(20, weapon);
            await h.Manager.OnItemPickedUpAsync(h.Player, weapon, CancellationToken.None);
        }

        return h;
    }

    [Test]
    public async Task Whoever_leaves_with_the_weapon_drops_it_where_they_stand()
    {
        using var h = await InFightAsync(holdingWeapon: true);
        Assert.That(h.Castle.EventItemOwner, Is.EqualTo(h.Player.Index));

        h.Player.X = 14;
        h.Player.Y = 40;
        await h.Manager.OnPlayerLeftAsync(h.Player, CancellationToken.None);

        var dropped = h.Ground.AllOnMap(11).Single();
        Assert.That(dropped.Item.Index, Is.EqualTo(Weapon));
        Assert.That((dropped.X, dropped.Y), Is.EqualTo(((byte)14, (byte)40)));
        Assert.That(h.Player.Items[20].IsItem(), Is.False);
        Assert.That(h.Castle.EventItemOwner, Is.EqualTo(-1));
        Assert.That(h.Castle.Users, Is.Empty);
    }

    [Test]
    public async Task A_player_who_dies_is_out_with_the_reward_so_far_and_sees_the_score_later()
    {
        using var h = await InFightAsync(holdingWeapon: true);
        uint zenBefore = h.Player.Money;

        await h.Manager.OnPlayerDiedAsync(h.Player, CancellationToken.None);

        Assert.That(h.Castle.Users, Is.Empty);
        Assert.That(h.Player.Money - zenBefore, Is.EqualTo(1000u), "the losers' share");
        Assert.That(h.Session.Of(0x93), Is.Empty, "the score comes a few seconds later");
        Assert.That(h.Ground.AllOnMap(11).Any(g => g.Item.Index == Weapon), Is.True, "the weapon fell");

        await h.TickAfterAsync(TimeSpan.FromSeconds(9));
        Assert.That(h.Session.Of(0x93), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Only_the_holder_of_the_weapon_can_hand_it_to_the_Archangel()
    {
        using var h = await InFightAsync(holdingWeapon: false);

        await h.Manager.HandleAngelKingAsync(h.Player, CancellationToken.None);
        Assert.That(h.Castle.WinnerIndex, Is.EqualTo(-1));
        Assert.That(h.Session.Commands(), Does.Contain(((byte)1, (byte)24)), "nothing to hand over");
    }

    [Test]
    public async Task The_Archangels_messenger_talks_only_to_a_player_with_a_cloak_while_the_entrance_is_open()
    {
        using var h = new Harness();
        await h.Manager.InitializeAsync(CancellationToken.None);

        Assert.That(await h.Manager.HandleMessengerAsync(h.Player, CancellationToken.None), Is.False, "closed");
        Assert.That(h.Session.Commands(), Does.Contain(((byte)1, (byte)20)));

        await h.TickAfterAsync(TimeSpan.FromMinutes(6));
        h.Player.SetItem(13, Item.Empty());
        Assert.That(await h.Manager.HandleMessengerAsync(h.Player, CancellationToken.None), Is.False, "no cloak");
        Assert.That(h.Session.Commands(), Does.Contain(((byte)1, (byte)21)));

        h.Player.SetItem(13, new Item { Index = (short)Cloak, Level = 1, Durability = 1 });
        Assert.That(await h.Manager.HandleMessengerAsync(h.Player, CancellationToken.None), Is.True);

        h.Player.Level = 5;
        Assert.That(await h.Manager.HandleMessengerAsync(h.Player, CancellationToken.None), Is.False, "too weak");
    }
}

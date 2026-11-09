using MuServer.GameServer.World;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The Lost Map's gate to Kalima: opening it, who goes through, how long it lasts, and the boss of a Kalima
/// opening the way to the next one. Maps, monsters and gates are small stand-ins in the format of the real files.</summary>
public class KalimaTests
{
    private sealed class Harness : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"kalima-{Guid.NewGuid():N}");
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public MapRegistry Maps { get; } = new();
        public MonsterRegistry Monsters { get; } = new();
        public PlayerRegistry Players { get; } = new();
        public KalimaManager Manager { get; }
        public PlayerObject Owner { get; }
        public FakeSession OwnerSession { get; } = FakeSession.Create();

        public Harness(bool[]? nextPortal = null)
        {
            Directory.CreateDirectory(Folder);
            string terrain = Path.Combine(Folder, "Terrain");
            Directory.CreateDirectory(terrain);

            var blank = new byte[3 + 65536];
            blank[1] = 255;
            blank[2] = 255;

            // Lorencia (map 0), Icarus (10) and Kalima 1 (24).
            foreach (string name in new[] { "Terrain1.att", "Terrain11.att", "Terrain25.att" })
            {
                File.WriteAllBytes(Path.Combine(terrain, name), blank);
            }

            Maps.LoadAll(terrain);
            Maps.GetMap(0)!.SetAttr(50, 50, 1); // a safe-zone tile

            string Monster(int index, string name) =>
                $"{index} 0 \"{name}\" 10 50 0 0 0 0 0 0 0 0 0 0 0 0 0 10 0 0 0 0 0 0 0 0 0 0 0 0";

            string monsterList = Path.Combine(Folder, "MonsterList.txt");
            File.WriteAllText(monsterList, string.Join("\n", Monster(152, "Magic stone"), Monster(153, "Magic stone"), Monster(161, "Illusion of Kundun"), "end"));
            var monsterInfo = new MonsterInfoTable();
            monsterInfo.Load(monsterList);

            var gates = new GateTable();
            string gatePath = Path.Combine(Folder, "Gate.txt");
            File.WriteAllText(gatePath, "88 0 24 10 16 17 22 0 0 0 * * *\n89 0 25 10 16 17 22 0 0 0 * * *\nend\n");
            gates.Load(gatePath);

            Manager = new KalimaManager(nextPortal ?? new bool[6], Monsters, monsterInfo, Maps, Players, gates) { Clock = () => Now };
            Owner = NewPlayer(9000, OwnerSession, 100, 100);
        }

        public PlayerObject NewPlayer(int index, FakeSession session, byte x, byte y)
        {
            var player = new PlayerObject
            {
                Index = index, Session = session, Class = 1, Level = 100, Name = $"Hero{index}", Map = 0, X = x, Y = y, WorldEntered = true,
            };
            Players.Add(player);
            return player;
        }

        public async Task TickAfterAsync(TimeSpan time)
        {
            Now += time;
            await Manager.TickAsync(CancellationToken.None);
        }

        public Monster GateMonster => Monsters.All.Single();

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

    [Test]
    public async Task A_lost_map_opens_a_gate_where_it_is_thrown()
    {
        using var h = new Harness();

        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None), Is.True);

        var gate = h.GateMonster;
        Assert.That(gate.MonsterClass, Is.EqualTo(152), "the gate to Kalima 1");
        Assert.That((gate.X, gate.Y), Is.EqualTo(((byte)102, (byte)100)));
        Assert.That(gate.Immune, Is.True);
        Assert.That(gate.NoRespawn, Is.True);

        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 103, 100, CancellationToken.None), Is.False, "one gate per player");
        Assert.That(h.Monsters.All.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task A_gate_cannot_be_opened_everywhere_or_with_any_map()
    {
        using var h = new Harness();

        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 0, 102, 100, CancellationToken.None), Is.False, "no such Lost Map");
        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 8, 102, 100, CancellationToken.None), Is.False, "no such Lost Map");
        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 7, 102, 100, CancellationToken.None), Is.False, "the package has no monster for that gate");

        h.Owner.X = 50;
        h.Owner.Y = 50;
        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None), Is.False, "not in a safe zone");

        h.Owner.X = 100;
        h.Owner.Map = 10;
        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None), Is.False, "not in Icarus");
        h.Owner.Map = 24;
        Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None), Is.False, "not in Kalima");

        Assert.That(h.Monsters.All, Is.Empty);
    }

    [Test]
    public async Task The_owner_and_the_party_go_through_but_a_stranger_does_not()
    {
        using var h = new Harness();
        var friend = h.NewPlayer(9001, FakeSession.Create(), 101, 101);
        var stranger = h.NewPlayer(9002, FakeSession.Create(), 100, 101);
        var farAway = h.NewPlayer(9003, FakeSession.Create(), 140, 140);
        h.Owner.PartyNumber = 5;
        friend.PartyNumber = 5;
        farAway.PartyNumber = 5;

        await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None);

        await h.TickAfterAsync(TimeSpan.FromSeconds(1));
        Assert.That(h.Owner.Map, Is.EqualTo(0), "a gate waits two seconds before it works");

        await h.TickAfterAsync(TimeSpan.FromSeconds(2));
        Assert.That(h.Owner.Map, Is.EqualTo(24));
        Assert.That(friend.Map, Is.EqualTo(24));
        Assert.That(stranger.Map, Is.EqualTo(0));
        Assert.That(farAway.Map, Is.EqualTo(0), "too far from the gate");
        Assert.That(h.Owner.X, Is.InRange(10, 17), "inside the gate's box");
        Assert.That(h.Owner.Y, Is.InRange(16, 22));
        Assert.That(h.OwnerSession.Sent, Is.Not.Empty, "the client was told");
        Assert.That(h.Manager.Gates.Single().EnterCount, Is.EqualTo(2));
    }

    [Test]
    public async Task A_gate_closes_after_three_minutes_after_five_entries_or_when_its_owner_leaves()
    {
        // Three minutes.
        using (var h = new Harness())
        {
            await h.Manager.TryCreatePortalAsync(h.Owner, 1, 140, 140, CancellationToken.None);
            await h.TickAfterAsync(TimeSpan.FromMinutes(2));
            Assert.That(h.Monsters.All, Has.Exactly(1).Items);
            await h.TickAfterAsync(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
            Assert.That(h.Monsters.All, Is.Empty);
            Assert.That(h.Manager.Gates, Is.Empty);
            Assert.That(await h.Manager.TryCreatePortalAsync(h.Owner, 1, 140, 140, CancellationToken.None), Is.True, "and the owner can open another");
        }

        // Five entries.
        using (var h = new Harness())
        {
            await h.Manager.TryCreatePortalAsync(h.Owner, 1, 102, 100, CancellationToken.None);
            h.Manager.Gates.Single().EnterCount = KalimaManager.MaxEntries;
            await h.TickAfterAsync(TimeSpan.FromSeconds(3));
            Assert.That(h.Monsters.All, Is.Empty);
            Assert.That(h.Owner.Map, Is.EqualTo(0), "nobody goes through a gate that is used up");
        }

        // The owner dies, or leaves.
        using (var h = new Harness())
        {
            await h.Manager.TryCreatePortalAsync(h.Owner, 1, 140, 140, CancellationToken.None);
            h.Owner.IsDying = true;
            await h.TickAfterAsync(TimeSpan.FromSeconds(3));
            Assert.That(h.Monsters.All, Is.Empty);
        }

        using (var h = new Harness())
        {
            await h.Manager.TryCreatePortalAsync(h.Owner, 1, 140, 140, CancellationToken.None);
            h.Players.Remove(h.Owner.Index);
            await h.TickAfterAsync(TimeSpan.FromSeconds(3));
            Assert.That(h.Monsters.All, Is.Empty);
        }
    }

    [Test]
    public async Task The_boss_of_a_kalima_opens_the_way_to_the_next_one_for_who_hurt_it_most()
    {
        using var h = new Harness(nextPortal: new[] { true, false, false, false, false, false });

        var boss = h.Monsters.SpawnOne(161, new MonsterSpawnEntry { Type = 4, MonsterClass = 161, Map = 0, X = 120, Y = 120 },
            LoadInfo(h), h.Maps)!;
        boss.DamageByAttacker[h.Owner.Index] = 900;
        boss.DamageByAttacker[12345] = 5;

        await h.Manager.OnMonsterKilledAsync(boss, CancellationToken.None);

        var gate = h.Monsters.All.Single(m => m.MonsterClass == 153);
        Assert.That(gate.X, Is.InRange(117, 123), "where the boss stood, or the nearest free tile (the boss itself occupies its own)");
        Assert.That(gate.Y, Is.InRange(117, 123));
        Assert.That(h.Manager.Gates.Single().OwnerIndex, Is.EqualTo(h.Owner.Index));
        Assert.That(h.Manager.Gates.Single().Level, Is.EqualTo(1), "leads to Kalima 2");

        // It does not open a second one for the same player.
        await h.Manager.OnMonsterKilledAsync(boss, CancellationToken.None);
        Assert.That(h.Monsters.All.Count(m => m.MonsterClass == 153), Is.EqualTo(1));
    }

    [Test]
    public async Task Kalima_dat_decides_which_bosses_open_a_gate()
    {
        using var h = new Harness(); // nothing enabled
        var boss = h.Monsters.SpawnOne(161, new MonsterSpawnEntry { Type = 4, MonsterClass = 161, Map = 0, X = 120, Y = 120 },
            LoadInfo(h), h.Maps)!;
        boss.DamageByAttacker[h.Owner.Index] = 900;

        await h.Manager.OnMonsterKilledAsync(boss, CancellationToken.None);

        Assert.That(h.Manager.Gates, Is.Empty);
    }

    [Test]
    public void The_dat_file_is_read()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kalima-{Guid.NewGuid():N}.dat");
        File.WriteAllText(path, "0 1\n1 0\n2 1\n9 1\nend\n");

        try
        {
            var enabled = KalimaManager.LoadNextPortals(path);
            Assert.That(enabled, Is.EqualTo(new[] { true, false, true, false, false, false }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MonsterInfoTable LoadInfo(Harness h)
    {
        var info = new MonsterInfoTable();
        info.Load(Path.Combine(h.Folder, "MonsterList.txt"));
        return info;
    }
}

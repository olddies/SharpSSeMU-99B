using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The drop events: the file, the schedule and the window in which monsters drop the event's items.</summary>
public class DropEventTests
{
    // Event 0: every day at 10:10, announced with messages 1 and 2, lasts two minutes, drops a Kris from Spiders.
    private const string EventFile = """
        0
           0   *   *   *   *   10   10   0
        end
        1
           0   1   2   120
        end
        2
           0   00,000   3   25   *   *   *   *   *   *   0   3   10   30   *   1000000
           0   00,000   0   0    *   *   *   *   *   *   5   *   *    *    *   500000
        end
        """;

    private const string Messages = """
        1   "Drop event starts!"
        2   "Drop event is over."
        end
        """;

    private sealed class Harness : IDisposable
    {
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), $"dropevent-{Guid.NewGuid():N}");
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public PlayerRegistry Players { get; } = new();
        public FakeSession Session { get; } = FakeSession.Create();
        public DropEventManager Manager { get; }

        public Harness(string eventFile = EventFile, bool enabled = true)
        {
            Directory.CreateDirectory(Folder);
            ItemCombatMath.Options = LoadRules();

            string path = Path.Combine(Folder, "DropEvent.dat");
            File.WriteAllText(path, eventFile);
            var events = DropEventManager.Load(path);

            string messagePath = Path.Combine(Folder, "Message.txt");
            File.WriteAllText(messagePath, Messages);
            var messages = new MessageTable();
            messages.Load(messagePath);

            var maps = new MapDropTable();
            maps.Add(0, new MapDropInfo(100, 100, 0, 0, 1));
            maps.Add(5, new MapDropInfo(100, 100, 0, 0, 0)); // items do not drop here

            var items = new ItemBalanceTable();
            items.Add(new ItemBalance { Index = Kris, Section = 0, Sub = 0, Slot = 0, Level = 6, DamageMin = 6, DamageMax = 11, Durability = 20 });

            var loot = new LootRoller(items, new ItemOptionRateTable(), new ExcellentOptionRateTable(), maps, new MapRateInfoTable(),
                new List<ItemDropRule>(), new List<MoneyDropRule>(), new SetItemRules());

            Manager = new DropEventManager(events, loot, Players, messages, enabled) { Clock = () => Now };

            Players.Add(new PlayerObject { Index = 9000, Session = Session, Name = "Hero", WorldEntered = true });
        }

        public async Task TickAfterAsync(TimeSpan time)
        {
            Now += time;
            await Manager.TickAllAsync(CancellationToken.None);
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

    private static LootContext Kill(int monsterClass = 3, int level = 20, int map = 0, int accountLevel = 0) => new()
    {
        MonsterClass = monsterClass, MonsterLevel = level, Map = map, ItemRate = 120, MoneyRate = 1, Money = 0,
        AccountLevel = accountLevel, ServerItemDropRate = 0,
    };

    [Test]
    public void The_file_is_read()
    {
        using var h = new Harness();
        var info = h.Manager.Events[0];

        Assert.That(info.StartTimes, Has.Count.EqualTo(1));
        Assert.That((info.StartMessage, info.FinalMessage, info.DurationSeconds), Is.EqualTo((1, 2, 120)));
        Assert.That(info.Items, Has.Count.EqualTo(2));
        Assert.That(info.Items[0].ItemIndex, Is.EqualTo(Kris));
        Assert.That((info.Items[0].Level, info.Items[0].Durability, info.Items[0].DropRate), Is.EqualTo((3, 25, 1000000)));
        Assert.That((info.Items[0].Map, info.Items[0].MonsterClass, info.Items[0].MonsterLevelMin, info.Items[0].MonsterLevelMax), Is.EqualTo((0, 3, 10, 30)));
        Assert.That(h.Manager.Events[1].Items, Is.Empty);
    }

    [Test]
    public async Task An_event_runs_on_its_schedule_and_announces_its_start_and_end()
    {
        using var h = new Harness();
        h.Manager.Initialize();

        Assert.That(h.Manager.Events[0].State, Is.EqualTo(DropEventState.Empty));
        Assert.That(h.Manager.Events[1].State, Is.EqualTo(DropEventState.Blank), "an event without a schedule never runs");

        await h.TickAfterAsync(TimeSpan.FromMinutes(5));
        Assert.That(h.Manager.Events[0].State, Is.EqualTo(DropEventState.Empty));
        Assert.That(h.Session.Sent, Is.Empty);

        await h.TickAfterAsync(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // 10:10:01
        Assert.That(h.Manager.Events[0].State, Is.EqualTo(DropEventState.Start));
        Assert.That(h.Session.Sent, Has.Count.EqualTo(1), "the start message went to everybody");

        await h.TickAfterAsync(TimeSpan.FromSeconds(60));
        Assert.That(h.Manager.Events[0].State, Is.EqualTo(DropEventState.Start));

        await h.TickAfterAsync(TimeSpan.FromSeconds(61));
        Assert.That(h.Manager.Events[0].State, Is.EqualTo(DropEventState.Empty), "over, waiting for tomorrow");
        Assert.That(h.Session.Sent, Has.Count.EqualTo(2), "and the final message too");
        Assert.That(h.Manager.Events[0].TargetTime, Is.GreaterThan(h.Now.AddHours(20)));
    }

    [Test]
    public async Task Monsters_drop_the_events_items_only_while_it_runs_and_only_when_they_match()
    {
        using var h = new Harness();
        h.Manager.Initialize();
        var rng = new Random(1);

        Assert.That(h.Manager.Roll(Kill(), rng), Is.Null, "not running yet");

        await h.TickAfterAsync(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var item = h.Manager.Roll(Kill(), rng);
        Assert.That(item, Is.Not.Null);
        Assert.That(item!.Index, Is.EqualTo(Kris));
        Assert.That(item.Level, Is.EqualTo(3), "the level of the row");
        Assert.That(item.Durability, Is.EqualTo(25), "and its durability");

        Assert.That(h.Manager.Roll(Kill(map: 5), rng), Is.Null, "items do not drop on that map");

        // The first row is only for class 3, levels 10-30; the second is for map 5 where nothing drops.
        Assert.That(h.Manager.Roll(Kill(monsterClass: 4, level: 20), rng), Is.Null);
        Assert.That(h.Manager.Roll(Kill(monsterClass: 3, level: 31), rng), Is.Null);
        Assert.That(h.Manager.Roll(Kill(monsterClass: 3, level: 9), rng), Is.Null);

        await h.TickAfterAsync(TimeSpan.FromMinutes(3));
        Assert.That(h.Manager.Roll(Kill(), rng), Is.Null, "the event is over");
    }

    [Test]
    public async Task The_chance_of_a_row_is_a_roll_out_of_a_million()
    {
        const string rare = """
            0
               0   *   *   *   *   10   10   0
            end
            1
               0   -1  -1  600
            end
            2
               0   00,000   0   0   *   *   *   *   *   *   *   *   *   *   *   100000
            end
            """;

        using var h = new Harness(rare);
        h.Manager.Initialize();
        await h.TickAfterAsync(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        var rng = new Random(7);
        int drops = Enumerable.Range(0, 4000).Count(_ => h.Manager.Roll(Kill(), rng) != null);
        Assert.That(drops, Is.InRange(300, 520), "100000 of a million is about 10%");
    }

    [Test]
    public void With_the_switch_off_no_event_runs()
    {
        using var h = new Harness(enabled: false);
        h.Manager.Initialize();

        Assert.That(h.Manager.Events.All(e => e.State == DropEventState.Blank), Is.True);
    }
}

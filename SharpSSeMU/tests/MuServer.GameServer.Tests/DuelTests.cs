using MuServer.GameServer.Protocol;
using MuServer.GameServer.World;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The duel (Duel.cpp): the request and its answer, the refusals, the timeouts, the score and the end.</summary>
public class DuelTests
{
    private const string Messages = """
        160 "Target player is not online"
        161 "You can not duel at this time"
        163 "Have you ever asked or been asked to duel"
        164 "Target player ever asked or been asked to duel"
        167 "You can not duel in this map"
        169 "Target player name is invalid"
        170 "You can not duel with Self Defense"
        171 "Player can not target duel Self Defense"
        172 "Target player refused his request duel"
        720 "Duel between %s and %s started!"
        721 "Duel [%s] (%d) VS (%d) [%s]"
        722 "Player %s won %s duel!"
        end
        """;

    private sealed class Harness
    {
        public DateTime Now { get; set; } = new DateTime(2030, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        public PlayerRegistry Players { get; } = new();
        public List<byte[]> DataServer { get; } = new();
        public DuelManager Duel { get; }
        public PlayerObject Alice { get; }
        public PlayerObject Bob { get; }
        public FakeSession AliceSession => (FakeSession)Alice.Session;
        public FakeSession BobSession => (FakeSession)Bob.Session;

        public Harness()
        {
            var maps = new MapDropTable();
            maps.Add(0, new MapDropInfo(100, 100, 0, 0, 1));
            maps.Add(6, new MapDropInfo(100, 100, 0, 0, 1, DuelEnable: 0)); // a map that forbids duels

            var messages = new MessageTable();
            string path = Path.Combine(Path.GetTempPath(), $"duelmsg-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, Messages);
            messages.Load(path);
            File.Delete(path);

            Duel = new DuelManager(Players, maps, messages, (packet, _) => { DataServer.Add(packet); return Task.CompletedTask; })
            {
                Clock = () => Now,
            };

            Alice = AddPlayer(9000, "Alice");
            Bob = AddPlayer(9001, "Bob");
        }

        public PlayerObject AddPlayer(int index, string name, byte map = 0)
        {
            var player = new PlayerObject
            {
                Index = index, Session = FakeSession.Create(), Name = name, Account = name.ToLower(), Level = 30, Map = map, WorldEntered = true,
            };
            Players.Add(player);
            return player;
        }

        public Task Ask() => Duel.HandleStartAsync(Alice, Bob.Index, "Bob", CancellationToken.None);

        public Task Answer(bool yes) => Duel.HandleAnswerAsync(Bob, yes, Alice.Index, "Alice", CancellationToken.None);

        public async Task StartDuelAsync()
        {
            await Ask();
            await Answer(true);
        }
    }

    private int _maxScore;
    private int _switch;
    private int _announce;

    [SetUp]
    public void SetUp()
    {
        var info = WorldPacketBuilder.ServerInfo;
        (_switch, _maxScore, _announce) = (info.DuelSwitch, info.DuelMaxScore, info.DuelAnnounceSwitch);
        (info.DuelSwitch, info.DuelMaxScore, info.DuelAnnounceSwitch) = (1, 3, 0);
    }

    [TearDown]
    public void TearDown()
    {
        var info = WorldPacketBuilder.ServerInfo;
        (info.DuelSwitch, info.DuelMaxScore, info.DuelAnnounceSwitch) = (_switch, _maxScore, _announce);
    }

    private static string LastNotice(FakeSession session) => session.Notices().Last();

    // ---------------------------------------------------------------- asking and answering

    [Test]
    public async Task A_request_reaches_the_other_player_with_the_askers_index_and_name()
    {
        var h = new Harness();
        await h.Ask();

        var ok = h.BobSession.Of(0xAC).Single();
        Assert.That((ok[3] << 8) | ok[4], Is.EqualTo(h.Alice.Index));
        Assert.That(System.Text.Encoding.ASCII.GetString(ok, 5, 10).TrimEnd('\0'), Is.EqualTo("Alice"));
        Assert.That((h.Alice.DuelUserReserved, h.Alice.DuelUserRequested), Is.EqualTo((h.Bob.Index, -1)));
        Assert.That((h.Bob.DuelUserReserved, h.Bob.DuelUserRequested), Is.EqualTo((-1, h.Alice.Index)));
    }

    [Test]
    public async Task Accepting_starts_the_duel_for_both_and_tells_them_the_score()
    {
        var h = new Harness();
        await h.StartDuelAsync();

        Assert.That((h.Alice.DuelUser, h.Bob.DuelUser), Is.EqualTo((h.Bob.Index, h.Alice.Index)));
        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.True);

        var forAlice = h.AliceSession.Of(0xAA).Single();
        Assert.That(forAlice[3], Is.EqualTo(1), "result: started");
        Assert.That((forAlice[4] << 8) | forAlice[5], Is.EqualTo(h.Bob.Index), "against Bob");

        var forBob = h.BobSession.Of(0xAA).Single();
        Assert.That((forBob[4] << 8) | forBob[5], Is.EqualTo(h.Alice.Index));

        // The score names the one who answered first: Bob (9001 = 0x2329), then Alice (9000 = 0x2328).
        var score = h.AliceSession.Of(0xAD).Single();
        Assert.That(score.Skip(3).Take(6).ToArray(), Is.EqualTo(new byte[] { 0x23, 0x29, 0x23, 0x28, 0, 0 }));
    }

    [Test]
    public async Task Refusing_ends_the_request_and_tells_the_asker()
    {
        var h = new Harness();
        await h.Ask();
        await h.Answer(false);

        Assert.That(LastNotice(h.BobSession), Is.EqualTo("Target player refused his request duel"));
        Assert.That(h.AliceSession.Of(0xAA).Single()[3], Is.EqualTo(0), "result: it did not happen");
        Assert.That(h.Alice.DuelUserReserved, Is.EqualTo(-1));
        Assert.That(h.Bob.DuelUserRequested, Is.EqualTo(-1));
    }

    [Test]
    public async Task A_request_is_refused_where_duels_are_not_allowed_or_the_target_is_not_there()
    {
        var h = new Harness();
        var elsewhere = h.AddPlayer(9002, "Carol", map: 6);

        await h.Duel.HandleStartAsync(elsewhere, h.Alice.Index, "Alice", CancellationToken.None);
        Assert.That(LastNotice((FakeSession)elsewhere.Session), Is.EqualTo("You can not duel in this map"));

        await h.Duel.HandleStartAsync(h.Alice, 9500, "Nobody", CancellationToken.None);
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Target player is not online"));

        await h.Duel.HandleStartAsync(h.Alice, h.Alice.Index, "Alice", CancellationToken.None);
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Target player is not online"), "nor can you duel yourself");

        await h.Duel.HandleStartAsync(h.Alice, h.Bob.Index, "Rob", CancellationToken.None);
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Target player name is invalid"));
        Assert.That(h.Alice.DuelUserReserved, Is.EqualTo(-1));
    }

    [Test]
    public async Task A_request_is_refused_when_either_is_busy_or_already_in_one()
    {
        var h = new Harness();

        h.Bob.InChaosBox = true;
        await h.Ask();
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("You can not duel at this time"));
        h.Bob.InChaosBox = false;

        h.Bob.DuelUserRequested = 9002;
        await h.Ask();
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Target player ever asked or been asked to duel"));
        h.Bob.DuelUserRequested = -1;

        h.Alice.DuelUser = 9002;
        await h.Ask();
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Have you ever asked or been asked to duel"));
        h.Alice.DuelUser = -1;

        h.Alice.SelfDefense[2] = 9005;
        await h.Ask();
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("You can not duel with Self Defense"));
        h.Alice.SelfDefense[2] = -1;

        h.Bob.SelfDefense[0] = 9005;
        await h.Ask();
        Assert.That(LastNotice(h.AliceSession), Is.EqualTo("Player can not target duel Self Defense"));

        Assert.That(h.BobSession.Of(0xAC), Is.Empty, "nothing was ever asked of Bob");
    }

    // ---------------------------------------------------------------- time

    [Test]
    public async Task A_request_nobody_answers_lapses_after_thirty_seconds_for_both()
    {
        var h = new Harness();
        await h.Ask();

        h.Now += TimeSpan.FromSeconds(29);
        await h.Duel.OnSecondAsync(h.Alice, CancellationToken.None);
        Assert.That(h.Alice.DuelUserReserved, Is.EqualTo(h.Bob.Index), "still waiting");

        h.Now += TimeSpan.FromSeconds(2);
        await h.Duel.OnSecondAsync(h.Alice, CancellationToken.None);

        Assert.That(h.AliceSession.Of(0xAA).Single()[3], Is.EqualTo(0));
        Assert.That((h.Alice.DuelUserReserved, h.Bob.DuelUserRequested), Is.EqualTo((-1, -1)));
    }

    [Test]
    public async Task The_asked_player_can_let_the_request_lapse_too()
    {
        var h = new Harness();
        await h.Ask();

        h.Now += TimeSpan.FromSeconds(31);
        await h.Duel.OnSecondAsync(h.Bob, CancellationToken.None);

        Assert.That(h.AliceSession.Of(0xAA).Single()[3], Is.EqualTo(0), "the asker is told");
        Assert.That((h.Alice.DuelUserReserved, h.Bob.DuelUserRequested), Is.EqualTo((-1, -1)));
    }

    [Test]
    public async Task A_duel_with_no_blow_for_a_minute_ends_and_a_blow_keeps_it_alive()
    {
        var h = new Harness();
        await h.StartDuelAsync();

        h.Now += TimeSpan.FromSeconds(50);
        h.Duel.Touch(h.Alice, h.Bob);

        h.Now += TimeSpan.FromSeconds(20);
        await h.Duel.OnSecondAsync(h.Alice, CancellationToken.None);
        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.True, "a blow 20 seconds ago");

        h.Now += TimeSpan.FromSeconds(41);
        await h.Duel.OnSecondAsync(h.Alice, CancellationToken.None);

        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.False);
        Assert.That(h.AliceSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.BobSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.Alice.DuelUser, Is.EqualTo(-1));
    }

    // ---------------------------------------------------------------- the fight

    [Test]
    public async Task A_kill_is_a_point_for_the_killer_and_the_loser_comes_back_where_they_fell()
    {
        var h = new Harness();
        await h.StartDuelAsync();

        bool wasDuel = await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);

        Assert.That(wasDuel, Is.True);
        Assert.That(h.Alice.DuelScore, Is.EqualTo(1));
        Assert.That(h.Bob.RespawnInPlace, Is.True);
        Assert.That(h.Alice.RespawnInPlace, Is.False);

        // The score packet names the dead one first.
        var score = h.AliceSession.Of(0xAD).Last();
        Assert.That(score.Skip(3).Take(6).ToArray(), Is.EqualTo(new byte[] { 0x23, 0x29, 0x23, 0x28, 0, 1 }));
        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.True, "the duel goes on");
    }

    [Test]
    public async Task At_the_top_score_the_duel_ends_and_the_ranking_keeps_the_winner()
    {
        var h = new Harness(); // three points win
        var onlooker = h.AddPlayer(9002, "Eve");
        h.Alice.VisibleTo.Add(onlooker.Index);
        await h.StartDuelAsync();

        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);
        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);
        Assert.That(h.DataServer, Is.Empty);
        Assert.That(((FakeSession)onlooker.Session).Commands(), Is.Empty, "no effect before the end");

        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);

        Assert.That(h.AliceSession.Commands(), Has.Count.EqualTo(1), "the winner's effect");
        Assert.That(((FakeSession)onlooker.Session).Commands(), Has.Count.EqualTo(1), "and whoever sees the winner sees it too");

        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.False);
        Assert.That(h.AliceSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.BobSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That((h.Alice.DuelScore, h.Bob.DuelScore, h.Alice.DuelUser), Is.EqualTo((0, 0, -1)));

        // Two saves of head 0x3C: Alice with a win, Bob with a loss.
        Assert.That(h.DataServer, Has.Count.EqualTo(2));
        Assert.That(h.DataServer.All(p => p[2] == 0x3C), Is.True);
        string first = System.Text.Encoding.ASCII.GetString(h.DataServer[0], 16, 10).TrimEnd('\0');
        Assert.That(first, Is.EqualTo("Alice"));
        Assert.That((BitConverter.ToUInt32(h.DataServer[0], h.DataServer[0].Length - 8), BitConverter.ToUInt32(h.DataServer[0], h.DataServer[0].Length - 4)), Is.EqualTo((1u, 0u)));
        Assert.That((BitConverter.ToUInt32(h.DataServer[1], h.DataServer[1].Length - 8), BitConverter.ToUInt32(h.DataServer[1], h.DataServer[1].Length - 4)), Is.EqualTo((0u, 1u)));
    }

    [Test]
    public async Task A_death_outside_a_duel_is_not_a_duel_kill()
    {
        var h = new Harness();
        var stranger = h.AddPlayer(9002, "Dan");

        Assert.That(await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None), Is.False, "no duel started");

        await h.StartDuelAsync();
        Assert.That(await h.Duel.OnKilledAsync(h.Bob, stranger, CancellationToken.None), Is.False, "killed by someone else");
        Assert.That(h.Bob.RespawnInPlace, Is.False);
        Assert.That(stranger.DuelScore, Is.EqualTo(0));
    }

    [Test]
    public async Task The_server_can_announce_the_start_the_score_and_the_winner()
    {
        WorldPacketBuilder.ServerInfo.DuelAnnounceSwitch = 2;
        var h = new Harness();
        var onlooker = h.AddPlayer(9002, "Eve");
        await h.StartDuelAsync();
        var seen = (FakeSession)onlooker.Session;

        Assert.That(seen.Notices(), Is.EqualTo(new[] { "Duel between Bob and Alice started!" }), "the one who answered is named first");

        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);
        Assert.That(seen.Notices().Last(), Is.EqualTo("Duel [Alice] (1) VS (0) [Bob]"));

        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);
        await h.Duel.OnKilledAsync(h.Bob, h.Alice, CancellationToken.None);
        Assert.That(seen.Notices().Last(), Is.EqualTo("Player Alice won Bob duel!"));
    }

    // ---------------------------------------------------------------- ending

    [Test]
    public async Task A_player_can_end_the_duel_or_give_up_a_request()
    {
        var h = new Harness();
        await h.Ask();
        await h.Duel.HandleEndAsync(h.Alice, CancellationToken.None);
        Assert.That(h.AliceSession.Of(0xAA).Last()[3], Is.EqualTo(0));
        Assert.That(h.Bob.DuelUserRequested, Is.EqualTo(-1));

        await h.StartDuelAsync();
        await h.Duel.HandleEndAsync(h.Bob, CancellationToken.None);
        Assert.That(h.AliceSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.BobSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.Duel.CheckDuel(h.Alice, h.Bob), Is.False);
    }

    [Test]
    public async Task A_player_who_leaves_the_game_ends_the_duel_for_the_other()
    {
        var h = new Harness();
        await h.StartDuelAsync();

        await h.Duel.OnPlayerLeftAsync(h.Alice, CancellationToken.None);

        Assert.That(h.BobSession.Of(0xAB), Has.Count.EqualTo(1));
        Assert.That(h.AliceSession.Of(0xAB), Is.Empty, "nothing is sent to the one leaving");
        Assert.That((h.Alice.DuelUser, h.Bob.DuelUser), Is.EqualTo((-1, -1)));
    }

    // ---------------------------------------------------------------- the map column

    [Test]
    public async Task Ending_a_duel_tells_the_ravens_to_forget_the_opponent()
    {
        var h = new Harness();
        var told = new List<(int Player, int Opponent)>();
        h.Duel.OnDuelReset = (player, opponent, _) =>
        {
            told.Add((player.Index, opponent));
            return Task.CompletedTask;
        };

        await h.StartDuelAsync();
        told.Clear();
        await h.Duel.HandleEndAsync(h.Alice, CancellationToken.None);

        Assert.That(told, Does.Contain((h.Alice.Index, h.Bob.Index)), "Alice's raven forgets Bob");
        Assert.That(told, Does.Contain((h.Bob.Index, h.Alice.Index)), "and Bob's forgets Alice");
    }

    [Test]
    public void The_map_table_reads_the_duel_column()
    {
        string path = Path.Combine(Path.GetTempPath(), $"maps-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, """
            0    *   12   100   100   100   1000   0   1   1   *   *   *   1   1   1   1   *   *   "Lorencia"
            6    *   12   100   100   100   1000   0   1   1   *   *   *   1   1   0   1   *   *   "Arena"
            7    *   12   100   100   100   1000   0   1   1   *   *   *   1   1   1   1   *   *   "Atlans"
            8    *   12   100   100   100   1000   0   1   1   *   *   *   1   1   *   1   *   *   "Tarkan"
            end
            """);

        try
        {
            var table = new MapDropTable();
            Assert.That(table.Load(path), Is.EqualTo(4));

            Assert.That(table.DuelEnabled(6, serverDuelSwitch: 1), Is.False, "0: no duels here");
            Assert.That(table.DuelEnabled(7, serverDuelSwitch: 0), Is.True, "1: duels here whatever the server says");
            Assert.That(table.DuelEnabled(8, serverDuelSwitch: 1), Is.True, "the star follows the server");
            Assert.That(table.DuelEnabled(8, serverDuelSwitch: 0), Is.False);
            Assert.That(table.ItemDropRate(6), Is.EqualTo(100));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

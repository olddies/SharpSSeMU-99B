using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>Where the Dark Lord's Summon Party may take a player (MoveSummon.txt): the places and their limits, the Atlans and Icarus
/// conditions, the murderers and the windows. Worked out by hand from CMoveSummon::CheckMoveSummon.</summary>
public class MoveSummonTests
{
    private const string File = """
        // Map X Y TX TY MinLevel MaxLevel MinReset MaxReset Enable0..3
           0   1 1 255 255 1   *   *   *   1 1 1 1
           2   1 1 255 255 50  *   *   *   1 1 1 1
           3   1 1 100 100 1   200 *   *   1 1 1 1
           4   1 1 255 255 1   *   2   5   1 1 1 1
           6   1 1 255 255 1   *   *   *   0 1 1 1
           7   1 1 255 255 1   *   *   *   1 1 1 1
           10  1 1 255 255 1   *   *   *   1 1 1 1
        end
        """;

    private static MoveSummonTable Table(int pkLimitFree = 0, int pkLimitMoveSummon = 0)
    {
        string path = Path.Combine(Path.GetTempPath(), $"movesummon-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(path, File);

        try
        {
            var table = new MoveSummonTable { PkLimitFree = pkLimitFree, PkLimitMoveSummon = pkLimitMoveSummon };
            table.Load(path);
            return table;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static PlayerObject Player(int level = 100, int cls = 0)
    {
        var player = DarkKnight();
        player.Level = (ushort)level;
        player.Class = (byte)cls;
        return player;
    }

    [Test]
    public void The_file_is_read_row_by_row()
    {
        Assert.That(Table().Count, Is.EqualTo(7));
    }

    [Test]
    public void A_listed_place_lets_a_player_in_and_an_unlisted_map_does_not()
    {
        var table = Table();

        Assert.That(table.Check(Player(), 0, 120, 120), Is.True);
        Assert.That(table.Check(Player(), 1, 120, 120), Is.False, "the dungeon is not in the file");
    }

    [Test]
    public void Only_the_listed_square_of_a_map_counts()
    {
        var table = Table();

        Assert.That(table.Check(Player(), 3, 50, 50), Is.True, "inside the 1,1 to 100,100 square");
        Assert.That(table.Check(Player(), 3, 150, 50), Is.False, "outside it");
    }

    [Test]
    public void The_level_must_be_in_the_places_range()
    {
        var table = Table();

        Assert.That(table.Check(Player(level: 49), 2, 100, 100), Is.False, "a level short");
        Assert.That(table.Check(Player(level: 50), 2, 100, 100), Is.True);
        Assert.That(table.Check(Player(level: 201), 3, 50, 50), Is.False, "over the top level");
    }

    [Test]
    public void A_Magic_Gladiator_or_a_Dark_Lord_needs_two_thirds_of_the_level()
    {
        var table = Table();

        Assert.That(MoveSummonTable.MoveLevel(Player(cls: 3), 60), Is.EqualTo(40));
        Assert.That(MoveSummonTable.MoveLevel(Player(cls: 4), 60), Is.EqualTo(40));
        Assert.That(MoveSummonTable.MoveLevel(Player(cls: 0), 60), Is.EqualTo(60));

        Assert.That(table.Check(Player(level: 33, cls: 4), 2, 100, 100), Is.True, "the file asks 50: two thirds of it is 33");
        Assert.That(table.Check(Player(level: 32, cls: 4), 2, 100, 100), Is.False, "one level short");
        Assert.That(table.Check(Player(level: 33, cls: 0), 2, 100, 100), Is.False, "a Dark Wizard needs the whole 50");
    }

    [Test]
    public void The_reset_must_be_in_the_places_range()
    {
        var table = Table();
        var low = Player();
        low.Reset = 1;
        var inside = Player();
        inside.Reset = 3;
        var high = Player();
        high.Reset = 6;

        Assert.That(table.Check(low, 4, 100, 100), Is.False);
        Assert.That(table.Check(inside, 4, 100, 100), Is.True);
        Assert.That(table.Check(high, 4, 100, 100), Is.False);
    }

    [Test]
    public void The_account_level_may_be_shut_out()
    {
        var table = Table();
        var free = Player();
        free.AccountLevel = 0;
        var paying = Player();
        paying.AccountLevel = 1;

        Assert.That(table.Check(free, 6, 100, 100), Is.False, "Arena is closed to the free accounts");
        Assert.That(table.Check(paying, 6, 100, 100), Is.True);
    }

    [Test]
    public void Atlans_does_not_take_a_Uniria_or_a_Dinorant()
    {
        var table = Table();
        var rider = Player();
        rider.Items[Item.SlotHelper] = Make(Item.GetItem(13, 3));

        Assert.That(table.Check(rider, 7, 100, 100), Is.False);
        Assert.That(table.Check(Player(), 7, 100, 100), Is.True, "on foot");
    }

    [Test]
    public void Icarus_asks_for_wings_or_a_Dinorant()
    {
        var table = Table();
        var onFoot = Player();
        var winged = Player();
        winged.Items[Item.SlotWing] = Make(WingsOfSatan);
        var rider = Player();
        rider.Items[Item.SlotHelper] = Make(Item.GetItem(13, 3));

        Assert.That(table.Check(onFoot, 10, 100, 100), Is.False);
        Assert.That(table.Check(winged, 10, 100, 100), Is.True);
        Assert.That(table.Check(rider, 10, 100, 100), Is.True, "the Dinorant flies");
    }

    [Test]
    public void A_murderer_is_refused_when_the_server_limits_him()
    {
        var murderer = Player();
        murderer.PKLevel = 5;

        Assert.That(Table(pkLimitFree: 0, pkLimitMoveSummon: 1).Check(murderer, 0, 120, 120), Is.False);
        Assert.That(Table(pkLimitFree: 1, pkLimitMoveSummon: 1).Check(murderer, 0, 120, 120), Is.True, "PKLimitFree lets him");
        Assert.That(Table(pkLimitFree: 0, pkLimitMoveSummon: 0).Check(murderer, 0, 120, 120), Is.True, "and so does the limit off");
    }

    [Test]
    public void A_player_with_a_window_open_or_dead_cannot_be_summoned()
    {
        var table = Table();
        var trading = Player();
        trading.InTrade = true;
        var dead = Player();
        dead.IsDying = true;

        Assert.That(table.Check(trading, 0, 120, 120), Is.False);
        Assert.That(table.Check(dead, 0, 120, 120), Is.False);
    }
}

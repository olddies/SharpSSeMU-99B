using MuServer.GameServer.World;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The blow that pushes: Lightning and the Dark Knight's skills move the target a tile, the Earthquake throws it three. Worked out by
/// hand from gObjBackSpring, gObjBackSpring2 and BackSpringCheck (User.cpp:2594-2736).</summary>
public class KnockbackTests
{
    /// <summary>A 256 x 256 map, all free, with the tiles <paramref name="marks"/> set to an attribute.</summary>
    private static GameMap Map(params (int X, int Y, byte Attr)[] marks)
    {
        var bytes = new byte[3 + (256 * 256)];
        bytes[1] = 255;
        bytes[2] = 255;

        foreach (var (x, y, attr) in marks)
        {
            bytes[3 + (y * 256) + x] = attr;
        }

        string path = Path.Combine(Path.GetTempPath(), $"knockback-{Guid.NewGuid():N}.att");
        File.WriteAllBytes(path, bytes);

        try
        {
            return GameMap.Load(0, path)!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the eight directions

    [TestCase(-1, -1, 0)]
    [TestCase(0, -1, 1)]
    [TestCase(1, -1, 2)]
    [TestCase(1, 0, 3)]
    [TestCase(1, 1, 4)]
    [TestCase(0, 1, 5)]
    [TestCase(-1, 1, 6)]
    [TestCase(-1, 0, 7)]
    [TestCase(0, 0, 0)]
    [TestCase(-7, -3, 0)]
    [TestCase(9, 0, 3)]
    public void A_vector_points_to_one_of_the_eight_directions(int dx, int dy, int expected)
    {
        Assert.That(Knockback.DirectionOf(dx, dy), Is.EqualTo(expected));
    }

    [Test]
    public void The_steps_of_the_directions_turn_clockwise_from_up_left()
    {
        Assert.That(Knockback.Road, Is.EqualTo(new[] { (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0) }));

        for (int dir = 0; dir < 8; dir++)
        {
            var (dx, dy) = Knockback.Road[dir];
            Assert.That(Knockback.DirectionOf(dx, dy), Is.EqualTo(dir), $"the step of {dir} points back to it");
        }
    }

    // ---------------------------------------------------------------- the push of one tile

    [Test]
    public void A_push_moves_the_target_one_tile_in_the_direction_the_attacker_faces()
    {
        var map = Map();

        Assert.That(Knockback.Push(map, 100, 100, 3, _ => 1), Is.EqualTo((101, 100)), "facing right");
        Assert.That(Knockback.Push(map, 100, 100, 0, _ => 2), Is.EqualTo((99, 99)), "facing up-left");
        Assert.That(Knockback.Push(map, 100, 100, 5, _ => 1), Is.EqualTo((100, 101)), "facing down");
    }

    [Test]
    public void One_time_in_three_the_push_goes_the_other_way()
    {
        var map = Map();

        Assert.That(Knockback.Push(map, 100, 100, 3, _ => 0), Is.EqualTo((99, 100)), "right turned into left");
        Assert.That(Knockback.Push(map, 100, 100, 6, _ => 0), Is.EqualTo((101, 99)), "down-left turned into up-right");

        int back = 0;
        var rng = new Random(3);

        for (int n = 0; n < 3000; n++)
        {
            if (Knockback.Push(map, 100, 100, 3, k => rng.Next(k)) == (99, 100))
            {
                back++;
            }
        }

        Assert.That(back, Is.InRange(900, 1100), "about a third");
    }

    [TestCase((byte)1, TestName = "A_push_does_not_enter_a_safe_zone")]
    [TestCase((byte)2, TestName = "A_push_does_not_enter_a_tile_with_attribute_2")]
    [TestCase((byte)4, TestName = "A_push_does_not_enter_a_wall")]
    [TestCase((byte)8, TestName = "A_push_does_not_enter_a_tile_that_cannot_be_walked")]
    [TestCase((byte)16, TestName = "A_push_does_not_enter_a_tile_with_a_creature_on_it")]
    public void A_push_stops_at_what_is_in_the_way(byte attr)
    {
        var map = Map((101, 100, attr));

        Assert.That(Knockback.Push(map, 100, 100, 3, _ => 1), Is.Null);
        Assert.That(Knockback.Push(map, 100, 100, 7, _ => 1), Is.EqualTo((99, 100)), "the other side is free");
    }

    [Test]
    public void A_push_off_the_edge_of_the_map_does_not_move()
    {
        var map = Map();

        Assert.That(Knockback.Push(map, 255, 100, 3, _ => 1), Is.Null, "off the map counts as a wall");
    }

    // ---------------------------------------------------------------- the throw of the Earthquake

    [Test]
    public void The_Earthquake_throws_a_target_straight_away_from_its_caster()
    {
        var map = Map();

        Assert.That(Knockback.Throw(map, 110, 100, 100, 100, 3, strong: true), Is.EqualTo((113, 100)), "to the right: three tiles");
        Assert.That(Knockback.Throw(map, 100, 110, 100, 100, 3, strong: true), Is.EqualTo((100, 113)), "downwards");
        Assert.That(Knockback.Throw(map, 110, 110, 100, 100, 3, strong: true), Is.EqualTo((113, 113)), "diagonally");
    }

    [Test]
    public void Only_the_Dark_Knight_the_Gladiator_and_the_Lord_are_thrown_the_third_tile()
    {
        var map = Map();

        Assert.That(Knockback.Throw(map, 110, 100, 100, 100, 3, strong: false), Is.EqualTo((112, 100)), "a monster or a lighter class: two tiles");
        Assert.That(Knockback.Throw(map, 110, 100, 100, 100, 3, strong: true), Is.EqualTo((113, 100)));
    }

    [Test]
    public void A_tile_in_the_way_stops_the_step_and_turns_the_target_around()
    {
        var map = Map((111, 100, 4));

        // Away from the caster (to the right): the wall stops the first step and turns it; the next two go back to the left.
        Assert.That(Knockback.Throw(map, 110, 100, 100, 100, 3, strong: true), Is.EqualTo((108, 100)));
        Assert.That(Knockback.Throw(map, 110, 100, 100, 100, 2, strong: false), Is.EqualTo((109, 100)), "with two steps only one is left to take");
    }

    [Test]
    public void A_target_on_the_caster_is_thrown_up_and_to_the_left()
    {
        var map = Map();

        Assert.That(Knockback.Throw(map, 100, 100, 100, 100, 3, strong: true), Is.EqualTo((97, 97)), "direction 0, as the original has it");
    }

    // ---------------------------------------------------------------- which skills

    [Test]
    public void Lightning_and_the_five_basic_skills_of_the_Knight_push_and_the_Earthquake_throws()
    {
        foreach (int skill in new[] { 3, 19, 20, 21, 22, 23 })
        {
            Assert.That(Knockback.PushesOnHit(skill), Is.True, $"skill {skill}");
            Assert.That(Knockback.ThrowsOnHit(skill), Is.False);
        }

        Assert.That(Knockback.ThrowsOnHit(62), Is.True);
        Assert.That(Knockback.PushesOnHit(62), Is.False);

        foreach (int skill in new[] { 1, 4, 7, 24, 38, 41, 42, 55 })
        {
            Assert.That(Knockback.PushesOnHit(skill) || Knockback.ThrowsOnHit(skill), Is.False, $"skill {skill}");
        }
    }
}

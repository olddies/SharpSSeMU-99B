namespace MuServer.GameServer.World;

/// <summary>The blow that pushes: where a creature ends up when a skill throws it back. Port of gObjBackSpring, gObjBackSpring2 and
/// BackSpringCheck (User.cpp:2594-2736) over the original's eight directions (RoadPathTable, Util.cpp:13).</summary>
public static class Knockback
{
    /// <summary>The step of each direction, in the order of RoadPathTable: 0 is up-left and they turn clockwise.</summary>
    public static readonly (int Dx, int Dy)[] Road =
    {
        (-1, -1), (0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0),
    };

    /// <summary>Port of GetPathPacketDirPos (Util.cpp:47): the direction (0-7) that a vector points to; (0, 0) is 0.</summary>
    public static int DirectionOf(int dx, int dy)
    {
        if (dx <= -1 && dy <= -1) return 0;
        if (dx <= -1 && dy == 0) return 7;
        if (dx <= -1 && dy >= 1) return 6;
        if (dx == 0 && dy <= -1) return 1;
        if (dx == 0 && dy >= 1) return 5;
        if (dx >= 1 && dy <= -1) return 2;
        if (dx >= 1 && dy == 0) return 3;
        if (dx >= 1 && dy >= 1) return 4;
        return 0;
    }

    /// <summary>The attribute bits that stop a thrown creature: a safe zone (1), a wall (4), no walking (8) and a standing creature (16).
    /// The first push (<see cref="Push"/>) stops on bit 2 as well.</summary>
    public const byte StopBits = 1 | 4 | 8 | 16;

    /// <summary>Whether a tile stops a creature thrown onto it (BackSpringCheck's test).</summary>
    public static bool Stops(GameMap? map, int x, int y) => map != null && (map.GetAttr(x, y) & StopBits) != 0;

    /// <summary>Port of gObjBackSpring (User.cpp:2594), the push of Lightning and the Dark Knight's skills: one tile in the direction the
    /// attacker faces -- one time in three, the opposite one -- unless the tile is a safe zone or in the way (attribute bits 1, 2, 4, 8
    /// or 16). Null when it does not move. <paramref name="roll"/> gives a number from 0 up to but not including its argument.</summary>
    public static (int X, int Y)? Push(GameMap? map, int x, int y, int attackerDir, Func<int, int> roll)
    {
        int dir = attackerDir & 7;

        if (roll(3) == 0)
        {
            dir = dir < 4 ? dir + 4 : dir - 4;
        }

        int nx = x + Road[dir].Dx;
        int ny = y + Road[dir].Dy;

        if (map != null && (map.GetAttr(nx, ny) & (StopBits | 2)) != 0)
        {
            return null;
        }

        return (nx, ny);
    }

    /// <summary>Port of gObjBackSpring2 (User.cpp:2693), the Earthquake's throw: up to <paramref name="steps"/> tiles straight away from the
    /// attacker. A tile in the way is not entered and turns the creature around for the next step. After the second step only the
    /// Dark Knight, the Magic Gladiator and the Dark Lord keep going (<paramref name="strong"/>).</summary>
    public static (int X, int Y) Throw(GameMap? map, int x, int y, int fromX, int fromY, int steps, bool strong)
    {
        int dir = DirectionOf(x - fromX, y - fromY);

        for (int n = 0; n < steps; n++)
        {
            if (n >= 2 && !strong)
            {
                continue;
            }

            int nx = x + Road[dir].Dx;
            int ny = y + Road[dir].Dy;

            if (Stops(map, nx, ny))
            {
                dir = dir < 4 ? dir + 4 : dir - 4;
                continue;
            }

            x = nx;
            y = ny;
        }

        return (x, y);
    }

    /// <summary>The skills whose hit pushes the target one tile 150 ms later (ApplySkillEffect, Attack.cpp:1076-1096): Lightning and the
    /// Dark Knight's five basic skills.</summary>
    public static bool PushesOnHit(int skill) => skill is 3 or 19 or 20 or 21 or 22 or 23;

    /// <summary>The Earthquake throws its targets three tiles at once.</summary>
    public static bool ThrowsOnHit(int skill) => skill == 62;
}

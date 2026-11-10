using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>Port of CMoveSummon (MoveSummon.cpp): Data/Move/MoveSummon.txt. Where a player may be summoned or teleported to by an ally
/// (the Dark Lord's Summon Party): the places, the levels, the resets and the account levels that are allowed.</summary>
public sealed class MoveSummonTable
{
    private sealed record Entry(int Map, int X, int Y, int TX, int TY, int MinLevel, int MaxLevel, int MinReset, int MaxReset, int[] Enable);

    private const int MapAtlans = 7;
    private const int MapIcarus = 10;

    private readonly List<Entry> _entries = new();

    /// <summary>PKLimitFree and PKLimitMoveSummon of the Common.dat: with the first off, a murderer is refused when the second is on.</summary>
    public int PkLimitFree { get; set; }
    public int PkLimitMoveSummon { get; set; }

    public int Count => _entries.Count;

    public void Add(int map, int x, int y, int tx, int ty, int minLevel, int maxLevel, int minReset, int maxReset, int[] enable) =>
        _entries.Add(new Entry(map, x, y, tx, ty, minLevel, maxLevel, minReset, maxReset, enable));

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MoveSummonTable] {0}", script.GetLastError());
            return 0;
        }

        _entries.Clear();

        while (script.GetToken() != TokenResult.End)
        {
            if (script.GetString() == "end")
            {
                break;
            }

            int map = script.GetNumber();
            int x = script.GetAsNumber();
            int y = script.GetAsNumber();
            int tx = script.GetAsNumber();
            int ty = script.GetAsNumber();
            int minLevel = script.GetAsNumber();
            int maxLevel = script.GetAsNumber();
            int minReset = script.GetAsNumber();
            int maxReset = script.GetAsNumber();
            var enable = new[] { script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber() };
            Add(map, x, y, tx, ty, minLevel, maxLevel, minReset, maxReset, enable);
        }

        Log.Add(LogColor.Blue, "[MoveSummonTable] {0} place(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>The level a player needs to go where a gate asks <paramref name="level"/> (CGate::GetMoveLevel): a Magic Gladiator or a
    /// Dark Lord, two thirds of it.</summary>
    public static int MoveLevel(PlayerObject player, int level) => player.Class is 3 or 4 ? level * 2 / 3 : level;

    /// <summary>Port of CMoveSummon::CheckMoveSummon (MoveSummon.cpp:90): whether this player can be taken to (map, x, y) by an ally.</summary>
    public bool Check(PlayerObject player, int map, int x, int y)
    {
        if (player.InTrade || player.InWarehouse || player.InChaosBox || player.InTrainer || player.IsDying)
        {
            return false; // a window open, or dead
        }

        if (PkLimitFree == 0 && player.PKLevel > 4 && PkLimitMoveSummon != 0)
        {
            return false;
        }

        var helper = player.Items[Item.SlotHelper];

        if (map == MapAtlans && helper.IsItem() && (helper.Index == Item.GetItem(13, 2) || helper.Index == Item.GetItem(13, 3)))
        {
            return false; // no Uniria nor Dinorant under the water
        }

        if (map == MapIcarus && !player.Items[Item.SlotWing].IsItem() && helper.Index != Item.GetItem(13, 3))
        {
            return false; // Icarus asks for wings or a Dinorant
        }

        foreach (var entry in _entries)
        {
            if (entry.Map != map || entry.X > x || entry.TX < x || entry.Y > y || entry.TY < y)
            {
                continue;
            }

            if (entry.MinLevel != -1 && player.Level < MoveLevel(player, entry.MinLevel))
            {
                return false;
            }

            if (entry.MaxLevel != -1 && player.Level > entry.MaxLevel)
            {
                return false;
            }

            if (entry.MinReset != -1 && player.Reset < entry.MinReset)
            {
                return false;
            }

            if (entry.MaxReset != -1 && player.Reset > entry.MaxReset)
            {
                return false;
            }

            return entry.Enable[Math.Clamp(player.AccountLevel, 0, 3)] != 0;
        }

        return false;
    }
}

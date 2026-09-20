using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of MONSTER_SET_BASE_INFO (MonsterSetBase.h:9-20) -- a row of a map's spawn table. <see
/// cref="Type"/> is the file's section (0=fixed point, 1=box+count, 2=fixed point with ±3 jitter, already
/// resolved to X/Y in <see cref="Load"/>). Types 3/4 (NPC variants, see MonsterSetBase.cpp) are read but not
/// instantiated (MonsterRegistry.SpawnAll only builds real combat monsters, MONSTER_INFO.Type==0). </summary>
public sealed class MonsterSpawnEntry
{
    public required int Type { get; init; }
    public required int MonsterClass { get; init; }
    public required int Map { get; init; }
    public int Dis { get; init; } // patrol/leash radius -- AI not ported yet in this phase (Phase 4 first pass = static monsters), it is stored for when it is added.
    public int X { get; init; }
    public int Y { get; init; }
    public int TX { get; init; } // esquina opuesta de la caja (Type==1)
    public int TY { get; init; }
    public int Dir { get; init; }
}

/// <summary> Port of CMonsterSetBase::LoadSpawn/GetPosition/GetBoxPosition (MonsterSetBase.cpp:28-217) -- reads
/// Data/Monster/Spawn/"NNN - MapName.txt" (the map number comes from the file name, not from inside). MemScript
/// format: each file has one or more sections (header = type number), each section ends in "end", the whole
/// file ends at EOF. </summary>
public static class MonsterSpawnTable
{
    public static List<MonsterSpawnEntry> LoadAll(string spawnDir)
    {
        var entries = new List<MonsterSpawnEntry>();

        if (!Directory.Exists(spawnDir))
        {
            Log.Add(LogColor.Red, "[MonsterSpawnTable] Spawn folder does not exist: {0}", spawnDir);
            return entries;
        }

        int filesLoaded = 0;

        foreach (var file in Directory.GetFiles(spawnDir, "*.txt"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            // Port of MonsterSetBase.cpp:50-52: the first 3 chars must be digits, followed by " - " -- the map
            // number is the prefix, the rest of the name is only descriptive.
            if (name.Length < 6 || !char.IsDigit(name[0]) || !char.IsDigit(name[1]) || !char.IsDigit(name[2])
                || name[3] != ' ' || name[4] != '-' || name[5] != ' ')
            {
                continue;
            }

            if (!int.TryParse(name[..3], out var map))
            {
                continue;
            }

            LoadFile(file, map, entries);
            filesLoaded++;
        }

        Log.Add(LogColor.Blue, "[MonsterSpawnTable] {0} spawn file(s) loaded ({1} spawn row(s) in total)", filesLoaded, entries.Count);
        return entries;
    }

    private static void LoadFile(string path, int map, List<MonsterSpawnEntry> entries)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MonsterSpawnTable] {0}", script.GetLastError());
            return;
        }

        var rand = Random.Shared;

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                if (script.GetString() == "end")
                {
                    break;
                }

                int monsterClass = script.GetNumber();

                switch (section)
                {
                    case 0:
                    {
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        entries.Add(new MonsterSpawnEntry { Type = 0, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, Dir = dir });
                        break;
                    }

                    case 2:
                    {
                        // Puerto de MonsterSetBase.cpp:126-128: jitter ±3 resuelto en el momento de carga.
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        int jx = (x - 3) + rand.Next(7);
                        int jy = (y - 3) + rand.Next(7);
                        entries.Add(new MonsterSpawnEntry { Type = 0, MonsterClass = monsterClass, Map = map, Dis = dis, X = jx, Y = jy, Dir = dir });
                        break;
                    }

                    case 1:
                    {
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int tx = script.GetAsNumber();
                        int ty = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        int count = script.GetAsNumber();

                        for (int n = 0; n < count; n++)
                        {
                            entries.Add(new MonsterSpawnEntry { Type = 1, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, TX = tx, TY = ty, Dir = dir });
                        }

                        break;
                    }

                    case 4:
                    {
                        // Port of MonsterSetBase.cpp Type==4 ("event position", MonsterSetBase.h) -- same
                        // column layout as type 0 (radius, x, y, direction), but these rows are NOT
                        // instantiated on starting the server: they are the POOL of candidate positions from
                        // which CDevilSquare::SetMonster (and Blood/Chaos Castle equivalents) picks at random
                        // to spawn event monsters at runtime -- see World/DevilSquare.cs (Phase 6, first pass)
                        // and Monster.SpawnEntry.Type==4.
                        int dis = script.GetAsNumber();
                        int x = script.GetAsNumber();
                        int y = script.GetAsNumber();
                        int dir = script.GetAsNumber();
                        dir = dir == -1 ? rand.Next(8) : dir;
                        entries.Add(new MonsterSpawnEntry { Type = 4, MonsterClass = monsterClass, Map = map, Dis = dis, X = x, Y = y, Dir = dir });
                        break;
                    }

                    default:
                        // Sections 3/4 (NPC variants) or other unrecognised ones: the remaining tokens of the
                        // row are discarded token by token (without assuming a number of fields) so as not to
                        // desynchronise the rest of the file.
                        while (true)
                        {
                            var s = script.GetAsString();

                            if (s == "end" || s.Length == 0)
                            {
                                break;
                            }
                        }

                        break;
                }
            }
        }
    }

    /// <summary>Port of CMonsterSetBase::GetPosition/GetBoxPosition (MonsterSetBase.cpp:168-217) -- for
    /// box-type spawns (Type==1), it tries up to 100 random points inside the rectangle and rejects tiles with
    /// a blocked attribute (1=safe zone, 4/8=block); if all fail, it falls back to the box's starting point
    /// (better than not spawning anything).</summary>
    public static (int X, int Y) ResolvePosition(MonsterSpawnEntry entry, GameMap? map)
    {
        if (entry.Type != 1)
        {
            return (entry.X, entry.Y);
        }

        var rand = Random.Shared;
        int minX = Math.Min(entry.X, entry.TX);
        int maxX = Math.Max(entry.X, entry.TX);
        int minY = Math.Min(entry.Y, entry.TY);
        int maxY = Math.Max(entry.Y, entry.TY);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            int x = minX + rand.Next(maxX - minX + 1);
            int y = minY + rand.Next(maxY - minY + 1);

            if (map == null || !(map.CheckAttr(x, y, 1) || map.IsBlocked(x, y)))
            {
                return (x, y);
            }
        }

        return (minX, minY);
    }
}

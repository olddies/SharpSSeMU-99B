using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of the data CChaosCastle::Load reads (ChaosCastle.cpp:77-177), Data/Event/ChaosCastle.dat: section
/// 0 the timing in minutes (warning, notification, event, close), section 1 the start times and section 2 the
/// experience a kill of another player and a kill of a monster are worth, per castle. </summary>
public sealed class ChaosCastleConfig
{
    public const int MaxLevel = 6;

    /// <summary>Minutes: how long before the opening it is announced and the entrance stays open, how long the
    /// players wait inside for the start, how long the fight lasts and how long it takes to close.</summary>
    public int WarningMinutes { get; private set; } = 5;
    public int NotifyMinutes { get; private set; } = 1;
    public int EventMinutes { get; private set; } = 10;
    public int CloseMinutes { get; private set; } = 4;

    public List<DevilSquareStartTime> Schedule { get; } = new();

    /// <summary>[level][0] experience of a player killed, [level][1] experience of a monster killed.</summary>
    public int[][] Experience { get; } = Enumerable.Range(0, MaxLevel).Select(_ => new int[2]).ToArray();

    public static ChaosCastleConfig Load(string path)
    {
        var config = new ChaosCastleConfig();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ChaosCastle] {0}", script.GetLastError());
            return config;
        }

        while (script.GetToken() != TokenResult.End)
        {
            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End || script.GetString() == "end")
                {
                    break;
                }

                switch (section)
                {
                    case 0:
                        config.WarningMinutes = script.GetNumber();
                        config.NotifyMinutes = script.GetAsNumber();
                        config.EventMinutes = script.GetAsNumber();
                        config.CloseMinutes = script.GetAsNumber();
                        break;

                    case 1:
                        {
                            int year = script.GetNumber();
                            int month = script.GetAsNumber();
                            int day = script.GetAsNumber();
                            int dayOfWeek = script.GetAsNumber();
                            int hour = script.GetAsNumber();
                            int minute = script.GetAsNumber();
                            int second = script.GetAsNumber();
                            config.Schedule.Add(new DevilSquareStartTime(year, month, day, dayOfWeek, hour, minute, second));
                            break;
                        }

                    case 2:
                        {
                            int level = script.GetNumber();
                            int userKill = script.GetAsNumber();
                            int monsterKill = script.GetAsNumber();

                            if (level is >= 0 and < MaxLevel)
                            {
                                config.Experience[level][0] = userKill;
                                config.Experience[level][1] = monsterKill;
                            }

                            break;
                        }

                    default:
                        break; // unknown section: its rows are skipped up to the "end"
                }
            }
        }

        Log.Add(LogColor.Blue, "[ChaosCastle] ChaosCastle.dat loaded: {0} start time(s), {1} level(s) with rewards",
            config.Schedule.Count, config.Experience.Count(r => r.Any(v => v != 0)));
        return config;
    }
}

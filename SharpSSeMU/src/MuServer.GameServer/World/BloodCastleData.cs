using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of the data CBloodCastle::Load reads (BloodCastle.cpp:92-236), Data/Event/BloodCastle.dat:
/// section 0 the timing in minutes (warning, notification, event, close), section 1 the start times, section 2 and
/// 3 the experience and zen of the winners and of everyone else per level, section 4 the life of the castle gate and
/// of the statue and section 5 where the statue stands. </summary>
public sealed class BloodCastleConfig
{
    public const int MaxLevel = 7;

    /// <summary>Minutes: how long before the opening it is announced, how long the entrance stays open, how long the
    /// event lasts and how long it takes to close.</summary>
    public int WarningMinutes { get; private set; } = 5;
    public int NotifyMinutes { get; private set; } = 1;
    public int EventMinutes { get; private set; } = 15;
    public int CloseMinutes { get; private set; } = 4;

    /// <summary>How many monsters per player the first stage asks for (the original's fixed 40). An optional fifth
    /// number of the timing row, so a test or a small server can ask for less.</summary>
    public int MonstersPerUser { get; private set; } = 40;

    public List<DevilSquareStartTime> Schedule { get; } = new();

    /// <summary>[level][0] for the winners (the one who delivers the weapon and their party), [level][1] for the rest.</summary>
    public int[][] Experience { get; } = CreateTable();
    public int[][] Money { get; } = CreateTable();

    /// <summary>[level][0] life of the castle gate, [level][1] life of the statue.</summary>
    public int[][] NpcLife { get; } = CreateTable();

    /// <summary>[level] = (x, y) of the statue.</summary>
    public int[][] SaintStatue { get; } = CreateTable();

    private static int[][] CreateTable() => Enumerable.Range(0, MaxLevel).Select(_ => new int[2]).ToArray();

    public static BloodCastleConfig Load(string path)
    {
        var config = new BloodCastleConfig();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[BloodCastle] {0}", script.GetLastError());
            return config;
        }

        while (script.GetToken() != TokenResult.End)
        {
            int section = script.GetNumber();
            bool sectionEnded = false;

            while (!sectionEnded)
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

                        // The optional fifth number is the monsters per player; if the next token is not a
                        // number it was the "end" of the section, which must not be read twice.
                        if (script.GetToken() == TokenResult.Number)
                        {
                            config.MonstersPerUser = Math.Max(script.GetNumber(), 1);
                        }
                        else
                        {
                            sectionEnded = true;
                        }

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
                    case 3:
                    case 4:
                    case 5:
                        {
                            int level = script.GetNumber();
                            int first = script.GetAsNumber();
                            int second = script.GetAsNumber();

                            if (level is >= 0 and < MaxLevel)
                            {
                                var table = section switch { 2 => config.Experience, 3 => config.Money, 4 => config.NpcLife, _ => config.SaintStatue };
                                table[level][0] = first;
                                table[level][1] = second;
                            }

                            break;
                        }

                    default:
                        break; // unknown section: its rows are skipped up to the "end"
                }
            }
        }

        Log.Add(LogColor.Blue, "[BloodCastle] BloodCastle.dat loaded: {0} start time(s), {1} level(s) with rewards",
            config.Schedule.Count, config.Experience.Count(r => r.Any(v => v != 0)));
        return config;
    }
}

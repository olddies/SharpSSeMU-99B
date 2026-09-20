using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

// Port of the "special events" data files read by CDevilSquare::Load, CEventEntryLevel::Load and
// CEventSpawnStage::Load (DevilSquare.cpp/EventEntryLevel.cpp/ EventSpawnStage.cpp) -- Phase 6, first pass
// (Devil Square). The loaders of EventEntryLevel.dat and EventStageSpawn.dat are generic per "section" on
// purpose (the same file shared by Blood Castle/Chaos Castle/Kalima/Devil Square in the original) so as not to
// have to rewrite them when those other events are ported -- for now only section 1 (Devil Square) of each is
// consumed.

/// <summary>Port of section 0 of DevilSquare.dat (minutes). Port of DEVIL_SQUARE_LEVEL's shared configuration
/// fields (DevilSquare.h) -- WarningTime/NotifyTime/EventTime/CloseTime are in MINUTES in the file, they are
/// used *60 as seconds in the state engine.</summary>
public sealed record DevilSquareTiming(int WarningMinutes, int NotifyMinutes, int EventMinutes, int CloseMinutes);

/// <summary>Port of a row of section 1 of DevilSquare.dat (DEVIL_SQUARE_START_TIME) -- cron-like trigger
/// schedule; -1 (read from "*" by MemScript) = wildcard in that field.</summary>
public sealed record DevilSquareStartTime(int Year, int Month, int Day, int DayOfWeek, int Hour, int Minute, int Second);

/// <summary> Port of CDevilSquare::Load (DevilSquare.cpp:79-196) -- Data/Event/DevilSquare.dat. The reward
/// tables are indexed [bracket 0-3][rank 0-9] (rank 0 = 1st place). REVERTED: an earlier porting pass had set
/// <c>MaxLevel=7</c> citing "GAMESERVER_UPDATE=803, DevilSquare.h:10-14" -- that source tree (without a version
/// suffix) is the WRONG one for this project (see the doc-comment of <see cref="World.Item"/> for the full
/// explanation). The real one, <c>DevilSquare.h:10</c> of the correct tree ("Emulator 0.99
/// (2.1.7)/GameServer"), defines <c>#define MAX_DS_LEVEL 4</c> -- also confirmed in
/// <c>EventEntryLevel.cpp:187-227</c> (<c>CEventEntryLevel::GetDSLevel</c>), which iterates
/// <c>for(n=0;n&lt;4;n++)</c> over both bracket tables (MG/DL and the other classes). Only 4 brackets exist in
/// this build, not 7. </summary>
public sealed class DevilSquareConfig
{
    public const int MaxLevel = 4; // MAX_DS_LEVEL (ver nota arriba)
    public const int MaxRank = 10; // MAX_DS_RANK

    public DevilSquareTiming Timing { get; private set; } = new(5, 1, 20, 4);
    public List<DevilSquareStartTime> Schedule { get; } = new();
    public int[][] RewardExperience { get; } = CreateTable();
    public int[][] RewardMoney { get; } = CreateTable();

    private static int[][] CreateTable()
    {
        var t = new int[MaxLevel][];
        for (int n = 0; n < MaxLevel; n++) t[n] = new int[MaxRank];
        return t;
    }

    public static DevilSquareConfig Load(string path)
    {
        var config = new DevilSquareConfig();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[DevilSquare] {0}", script.GetLastError());
            return config;
        }

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

                switch (section)
                {
                    case 0:
                    {
                        int warning = script.GetNumber();
                        int notify = script.GetAsNumber();
                        int eventTime = script.GetAsNumber();
                        int close = script.GetAsNumber();
                        config.Timing = new DevilSquareTiming(warning, notify, eventTime, close);
                        break;
                    }

                    case 1:
                    {
                        int year = script.GetNumber();
                        int month = script.GetAsNumber();
                        int day = script.GetAsNumber();
                        int dow = script.GetAsNumber();
                        int hour = script.GetAsNumber();
                        int minute = script.GetAsNumber();
                        int second = script.GetAsNumber();
                        config.Schedule.Add(new DevilSquareStartTime(year, month, day, dow, hour, minute, second));
                        break;
                    }

                    case 2:
                    case 3:
                    {
                        int eventIndex = script.GetNumber();
                        var target = section == 2 ? config.RewardExperience : config.RewardMoney;

                        for (int rank = 0; rank < MaxRank; rank++)
                        {
                            int value = script.GetAsNumber();

                            if (eventIndex >= 0 && eventIndex < MaxLevel)
                            {
                                target[eventIndex][rank] = value;
                            }
                        }

                        break;
                    }

                    default:
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

        Log.Add(LogColor.Blue, "[DevilSquare] DevilSquare.dat loaded: {0} start time(s), {1} bracket(s) with rewards", config.Schedule.Count, config.RewardExperience.Count(r => r.Any(v => v != 0)));
        return config;
    }
}

/// <summary>Port of a row of EventEntryLevel.dat (EventEntryLevel.cpp) -- CommonMin/Max applies to normal
/// classes, SpecialMin/Max to MG/DL/RF (earlier unlock). -1 ("*") = no limit.</summary>
public sealed record EventEntryLevelBracket(int Index, int CommonMinLevel, int CommonMaxLevel, int SpecialMinLevel, int SpecialMaxLevel)
{
    public bool InRange(int level, bool special)
    {
        int min = special ? SpecialMinLevel : CommonMinLevel;
        int max = special ? SpecialMaxLevel : CommonMaxLevel;
        return level >= min && (max == -1 || level <= max);
    }
}

/// <summary> Generic port of CEventEntryLevel::Load (EventEntryLevel.cpp) -- Data/Event/EventEntryLevel.dat,
/// sections 0=Blood Castle, 1=Devil Square, 2=Chaos Castle, 3=Kalima (all share the same file/format in the
/// original). This phase only consumes section 1 via <see cref="GetDevilSquareLevel"/>. </summary>
public sealed class EventEntryLevelTable
{
    private readonly Dictionary<int, List<EventEntryLevelBracket>> _sections = new();

    public const int SectionDevilSquare = 1;

    public static EventEntryLevelTable Load(string path)
    {
        var table = new EventEntryLevelTable();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[EventEntryLevel] {0}", script.GetLastError());
            return table;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            int section = script.GetNumber();
            var rows = table._sections.TryGetValue(section, out var existing) ? existing : table._sections[section] = new List<EventEntryLevelBracket>();

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

                int index = script.GetNumber();
                int commonMin = script.GetAsNumber();
                int commonMax = script.GetAsNumber();
                int specialMin = script.GetAsNumber();
                int specialMax = script.GetAsNumber();
                rows.Add(new EventEntryLevelBracket(index, commonMin, commonMax, specialMin, specialMax));
            }
        }

        Log.Add(LogColor.Blue, "[EventEntryLevel] {0} section(s) loaded from {1}", table._sections.Count, path);
        return table;
    }

    /// <summary> Port of CEventEntryLevel::GetDSLevel (EventEntryLevel.cpp:187-227 of the correct source tree)
    /// -- iterates over the MG/DL tables and the rest of the classes for the 4 real brackets (see <see
    /// cref="DevilSquareConfig.MaxLevel"/>). It walks ALL the rows of section 1 and keeps the LAST one that
    /// matches (like the original, it does not stop at the first match) -- returns -1 if none applies.
    /// </summary>
    public int GetDevilSquareLevel(int level, bool specialClass)
    {
        if (!_sections.TryGetValue(SectionDevilSquare, out var rows))
        {
            return -1;
        }

        int result = -1;

        foreach (var row in rows)
        {
            if (row.InRange(level, specialClass))
            {
                result = row.Index;
            }
        }

        return result;
    }
}

/// <summary>Port of a row of EventStageSpawn.dat (EventSpawnStage.h/.cpp) -- which monster class is added from
/// which stage (Stage 0-3) for a given bracket. MaxRegenMs=-1 ("*") means "does not respawn"
/// (CDevilSquare::SetMonster leaves PosNum=-1 in that case); for Devil Square in this data package it is always
/// 1000ms.</summary>
public sealed record EventStageSpawnEntry(int Bracket, int Stage, int MonsterClass, int MaxRegenMs);

/// <summary> Generic port of CEventSpawnStage::Load -- Data/Event/EventStageSpawn.dat, sections 0=Blood Castle,
/// 1=Chaos Castle, 2=Devil Square (confirmed against this package's real file, which only has 3 sections). This
/// phase only consumes section 2. </summary>
public sealed class EventStageSpawnTable
{
    public const int SectionDevilSquare = 2;

    private readonly List<EventStageSpawnEntry> _entries = new();

    public static EventStageSpawnTable Load(string path)
    {
        var table = new EventStageSpawnTable();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[EventStageSpawn] {0}", script.GetLastError());
            return table;
        }

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

                int bracket = script.GetNumber();
                int stage = script.GetAsNumber();
                int monsterClass = script.GetAsNumber();
                int maxRegen = script.GetAsNumber();
                table._entries.Add(new EventStageSpawnEntry(bracket, stage, monsterClass, maxRegen) with { });
                // It is stored with the section implicitly embedded in the load order -- since it is only
                // queried by (bracket,stage) inside GetDevilSquareMonsterClasses, it is enough to filter by
                // section here before adding if it is not 2, so as not to mix BC/CC.
                if (section != SectionDevilSquare)
                {
                    table._entries.RemoveAt(table._entries.Count - 1);
                }
            }
        }

        Log.Add(LogColor.Blue, "[EventStageSpawn] {0} Devil Square row(s) loaded from {1}", table._entries.Count, path);
        return table;
    }

    /// <summary>Monster classes that are added on reaching <paramref name="stage"/> (0-3) of the given bracket
    /// -- port of the filter part of CDevilSquare::StageSpawn (DevilSquare.cpp).</summary>
    public IEnumerable<int> GetMonsterClasses(int bracket, int stage) =>
        _entries.Where(e => e.Bracket == bracket && e.Stage == stage).Select(e => e.MonsterClass);
}

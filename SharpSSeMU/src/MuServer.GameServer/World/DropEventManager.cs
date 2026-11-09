using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

public enum DropEventState
{
    Blank = 0,
    Empty = 1,
    Start = 2,
}

/// <summary>One item row of a drop event (section 2 of DropEvent.dat): the item, the option rows its options are rolled
/// from (-1 for none), and the monsters and players it drops for (-1 for any).</summary>
public sealed record DropEventItem(
    int ItemIndex, int Level, int Durability, int[] Options, int Map, int MonsterClass,
    int MonsterLevelMin, int MonsterLevelMax, int AccountLevel, int DropRate);

/// <summary>One of the (up to 30) drop events: when it starts, how long it lasts, what is announced and what drops.</summary>
public sealed class DropEventInfo
{
    public required int Index { get; init; }
    public DropEventState State { get; set; } = DropEventState.Blank;
    public DateTime TargetTime { get; set; }
    public int StartMessage { get; set; } = -1;
    public int FinalMessage { get; set; } = -1;

    /// <summary>How long the event lasts, in seconds.</summary>
    public int DurationSeconds { get; set; }

    public List<DevilSquareStartTime> StartTimes { get; } = new();
    public List<DropEventItem> Items { get; } = new();
}

/// <summary> Port of CDropEvent (DropEvent.cpp). Data/Event/DropEvent.dat describes events (section 0 their start
/// times, section 1 their announcements and length, section 2 the items that drop) that run on a schedule: while one
/// is on, monsters that match one of its rows can drop its items instead of what they would normally drop. The
/// package ships the file empty, so nothing happens until the owner of the server writes an event. </summary>
public sealed class DropEventManager
{
    public const int MaxEvents = 30;

    private readonly DropEventInfo[] _events;
    private readonly LootRoller _loot;
    private readonly PlayerRegistry _players;
    private readonly MessageTable _messages;
    private readonly bool _enabled;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private DateTime Now => Clock();

    public DropEventManager(DropEventInfo[] events, LootRoller loot, PlayerRegistry players, MessageTable messages, bool enabled)
    {
        _events = events;
        _loot = loot;
        _players = players;
        _messages = messages;
        _enabled = enabled;
    }

    public IReadOnlyList<DropEventInfo> Events => _events;

    /// <summary>Port of CDropEvent::Load.</summary>
    public static DropEventInfo[] Load(string path)
    {
        var events = Enumerable.Range(0, MaxEvents).Select(n => new DropEventInfo { Index = n }).ToArray();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[DropEvent] {0}", script.GetLastError());
            return events;
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

                int index = script.GetNumber();
                bool valid = index is >= 0 and < MaxEvents;

                switch (section)
                {
                    case 0:
                        {
                            var time = new DevilSquareStartTime(
                                script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber(),
                                script.GetAsNumber(), script.GetAsNumber(), script.GetAsNumber());

                            // The record is (Year, Month, Day, DayOfWeek, Hour, Minute, Second), in the file's order.
                            if (valid)
                            {
                                events[index].StartTimes.Add(time);
                            }

                            break;
                        }

                    case 1:
                        {
                            int start = script.GetAsNumber();
                            int final = script.GetAsNumber();
                            int seconds = script.GetAsNumber();

                            if (valid)
                            {
                                events[index].StartMessage = start;
                                events[index].FinalMessage = final;
                                events[index].DurationSeconds = seconds;
                            }

                            break;
                        }

                    case 2:
                        {
                            int itemSection = script.GetAsNumber();
                            script.GetToken(); // ','
                            int itemSub = script.GetAsNumber();
                            int level = script.GetAsNumber();
                            int durability = script.GetAsNumber();
                            var options = new int[6];

                            for (int n = 0; n < options.Length; n++)
                            {
                                options[n] = script.GetAsNumber();
                            }

                            int map = script.GetAsNumber();
                            int monsterClass = script.GetAsNumber();
                            int levelMin = script.GetAsNumber();
                            int levelMax = script.GetAsNumber();
                            int accountLevel = script.GetAsNumber();
                            int dropRate = script.GetAsNumber();

                            if (valid)
                            {
                                events[index].Items.Add(new DropEventItem(
                                    Item.GetItem(itemSection, itemSub), level, durability, options, map, monsterClass, levelMin, levelMax, accountLevel, dropRate));
                            }

                            break;
                        }

                    default:
                        break;
                }
            }
        }

        Log.Add(LogColor.Blue, "[DropEvent] DropEvent.dat loaded: {0} event(s) with a schedule, {1} item row(s)",
            events.Count(e => e.StartTimes.Count > 0), events.Sum(e => e.Items.Count));
        return events;
    }

    public void Start(CancellationToken ct)
    {
        _ = RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Initialize();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TickAllAsync(ct);
        }
    }

    /// <summary>Port of CDropEvent::Init: with the switch off every event is blank, otherwise each waits for its next start.</summary>
    internal void Initialize()
    {
        foreach (var info in _events)
        {
            if (_enabled)
            {
                SetEmpty(info);
            }
            else
            {
                info.State = DropEventState.Blank;
            }
        }
    }

    /// <summary>One second of every event (MainProc).</summary>
    internal async Task TickAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            foreach (var info in _events)
            {
                double remain = (info.TargetTime - Now).TotalSeconds;

                if (info.State == DropEventState.Empty && remain <= 0)
                {
                    if (info.StartMessage != -1)
                    {
                        await NoticeToAllAsync(_messages.GetMessage(info.StartMessage), ct);
                    }

                    info.State = DropEventState.Start;
                    info.TargetTime = Now.AddSeconds(info.DurationSeconds);
                }
                else if (info.State == DropEventState.Start && remain <= 0)
                {
                    if (info.FinalMessage != -1)
                    {
                        await NoticeToAllAsync(_messages.GetMessage(info.FinalMessage), ct);
                    }

                    SetEmpty(info);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Port of SetState_EMPTY / CheckSync: waits for the next start time, or stays blank without one.</summary>
    private void SetEmpty(DropEventInfo info)
    {
        var next = info.StartTimes.Count == 0 ? null : DevilSquareManager.ComputeNextOccurrence(info.StartTimes, Now);

        if (next == null)
        {
            info.State = DropEventState.Blank;
            return;
        }

        info.State = DropEventState.Empty;
        info.TargetTime = next.Value;
        Log.Add(LogColor.Black, "[Drop Event] ({0}) Sync Start Time. [{1}] min remain", info.Index, (int)(next.Value - Now).TotalMinutes);
    }

    private async Task NoticeToAllAsync(string text, CancellationToken ct)
    {
        var packet = ChatPacketBuilder.NoticeSend(text);

        foreach (var player in _players.All)
        {
            if (player.WorldEntered)
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    /// <summary>Port of CDropEvent::DropItem: among the rows of the events that are on that fit the monster and the
    /// player, the ones whose chance hits are weighed by how rare they are and one is picked. Null when none.</summary>
    public Item? Roll(LootContext ctx, Random rng)
    {
        if (!_loot.ItemDropEnabled(ctx.Map))
        {
            return null;
        }

        var candidates = new List<(DropEventItem Row, int Weight)>();

        foreach (var info in _events.Where(e => e.State == DropEventState.Start))
        {
            foreach (var row in info.Items)
            {
                if (!_loot.KnowsItem(row.ItemIndex)
                    || (row.Map != -1 && row.Map != ctx.Map)
                    || (row.MonsterClass != -1 && row.MonsterClass != ctx.MonsterClass)
                    || (row.MonsterLevelMin != -1 && row.MonsterLevelMin > ctx.MonsterLevel)
                    || (row.MonsterLevelMax != -1 && row.MonsterLevelMax < ctx.MonsterLevel)
                    || (row.AccountLevel != -1 && row.AccountLevel != ctx.AccountLevel))
                {
                    continue;
                }

                if (row.DropRate == -1 || (row.DropRate > 0 && rng.Next(1000000) < row.DropRate))
                {
                    candidates.Add((row, 1000000 / (row.DropRate == -1 ? 1000000 : row.DropRate)));
                }
            }
        }

        if (WeightedPick.Pick(candidates.Select((c, n) => (n, c.Weight)), rng) is not int picked)
        {
            return null;
        }

        var chosen = candidates[picked].Row;
        return _loot.BuildRuleItem(chosen.ItemIndex, chosen.Level, chosen.Durability, chosen.Options, rng);
    }
}

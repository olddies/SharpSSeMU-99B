using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

/// <summary>Puerto de eDevilSquareState (DevilSquare.h:21-28).</summary>
public enum DevilSquareState
{
    Blank = 0,
    Empty = 1,
    Stand = 2,
    Start = 3,
    Clean = 4,
}

/// <summary>Simplified port of a slot of DEVIL_SQUARE_LEVEL.User[MAX_DS_USER] (DevilSquare.h) -- unlike the
/// fixed array of 50 slots with gaps/index -1, here it is an entry of a List&lt;&gt; that preserves insertion
/// order on purpose: the tie-break of <see cref="CalcRanks"/> depends on that order (a quirk documented in this
/// phase's research: the original breaks ties by lowest slot index = order of entry, something a Dictionary
/// does not guarantee but a List does).</summary>
public sealed class DevilSquareParticipant
{
    public required int PlayerIndex { get; init; }
    public uint Score { get; set; }
    public int Rank { get; set; } = -1; // 0-based, calculado en CalcRanks
}

/// <summary>Port of DEVIL_SQUARE_LEVEL (DevilSquare.h) -- runtime state of ONE bracket (0-6, although only 0-3
/// have real data in this package, see README). MonsterIndices accumulates ALL the monsters spawned in the 4
/// stages of the current run (it is not emptied between stages -- exact port of the original behaviour, see the
/// StageSpawn quirk in this phase's research).</summary>
public sealed class DevilSquareBracket
{
    public required int Level { get; init; } // 0-based
    public required byte Map { get; init; }
    public required (byte MinX, byte MinY, byte MaxX, byte MaxY) EntranceBox { get; init; }
    public bool HasData { get; init; }

    public DevilSquareState State { get; set; } = DevilSquareState.Blank;
    public DateTime TargetTime { get; set; }
    public int Stage { get; set; }
    public bool EnterEnabled { get; set; }
    public bool TimeCountSent { get; set; }

    public List<DevilSquareParticipant> Participants { get; } = new();
    public HashSet<int> MonsterIndices { get; } = new();
}

/// <summary> Port of CDevilSquare (DevilSquare.h/.cpp) -- Phase 6, first pass. State engine +
/// entry/score/reward of Devil Square. Explicitly documented simplifications (same criterion as the rest of the
/// project -- visible technical debt, not hidden): - No integration with the NPC dialog system
/// (CNpcTalk::NpcCharon) -- that subsystem is not ported yet (see ClientProtocolHandler, head 0x30/0x31 remain
/// unimplemented). The real client would need to talk to the NPC Charon to open the selection window before
/// being able to send C1:90 -- here it is assumed that the client (or a test tool) sends C1:90 directly, the
/// rest of the flow (validation, teleport, score, reward, ranking) is faithful. - No text message catalogue
/// (Message.txt/gMessage) -- the notices "Devil Square opens in N minutes", "you do not have enough level",
/// etc. are not sent (there is no ported text notification system yet). The C1:92 packet (30-second klaxon,
/// without text) IS sent. - ITEM reward (only 1st place, see EventItemBagManager.txt) is not ported -- it
/// requires the recursive "bag" system of ItemBagManager which does not exist yet in this port. XP and Zen are
/// granted in full and byte-exact against the DevilSquare.dat tables. - Daily entry limit per account
/// (DSCount/m_DevilSquareMaxEntryCount) not ported -- one can enter as many times as wanted while the window is
/// open. - Spawn position selection: the original picks ONE free position at random per instance of
/// CDevilSquare::SetMonster; here a monster is instantiated at EACH candidate position of the Type==4 pool for
/// the class/map (the exact number of instances per call was not found in the research) -- it is the simplest
/// interpretation and the most faithful to the position pool as defined in the real data file. </summary>
public sealed class DevilSquareManager
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly Random Rng = Random.Shared;

    /// <summary>Real port of MAX_DS_USER (<c>gServerInfo.m_DevilSquareMaxUser</c>, <c>GameServerInfo -
    /// Event.dat</c> -- see <see cref="MuServer.GameServer.Config.ServerInfoConfig"/>). FIXED: before porting
    /// that file this port used a hardcoded cap of 50 (arbitrary); the real one is 15.</summary>
    private static int MaxDsUser => WorldPacketBuilder.ServerInfo.DevilSquareMaxUser;
    // Item.GetItem(section,sub) = section*32+sub (this build's real MaxItemType, see Item.cs). GetItem(14,19) =
    // 467 ("Devil's Invitation").
    private static readonly int TicketItemLeveled = Item.GetItem(14, 19); // "Devil's Invitation"
    private static readonly int TicketItemUniversal = Item.GetItem(13, 46); // no level -- see the quirk documented in the research
    private const int ClassMg = 3; // "clase especial" (MG/DL/RF en el original) -- solo MG existe en este paquete de datos

    private readonly DevilSquareConfig _config;
    private readonly EventEntryLevelTable _entryLevels;
    private readonly EventStageSpawnTable _stageSpawns;
    private readonly IReadOnlyList<MonsterSpawnEntry> _eventSpawnPool;
    private readonly MonsterInfoTable _monsterInfo;
    private readonly MapRegistry _maps;
    private readonly MonsterRegistry _monsters;
    private readonly PlayerRegistry _players;
    private readonly DataServerConnection _dataServer;
    private readonly Dictionary<int, DevilSquareBracket> _brackets = new();

    /// <summary>Puerto de gObjMoveGate(aIndex,27) -- caja de salida en Noria, usada por ClearUser al
    /// volver a EMPTY (ver Gate.txt fila 27).</summary>
    private static readonly (byte Map, byte MinX, byte MinY, byte MaxX, byte MaxY) ExitBox = (3, 171, 108, 177, 117);

    /// <summary>Callback towards ClientProtocolHandler to give event experience reusing the same level-up
    /// pipeline as normal combat (see ClientProtocolHandler.GrantEventExperienceAsync) -- resolved as a
    /// delegate instead of a direct reference so as not to create a circular dependency in construction
    /// (DevilSquareManager is built before ClientProtocolHandler, like ViewportTicker/protocolHandler in
    /// Program.cs).</summary>
    public Func<PlayerObject, long, CancellationToken, Task>? GrantExperience { get; set; }

    public DevilSquareManager(
        DevilSquareConfig config, EventEntryLevelTable entryLevels, EventStageSpawnTable stageSpawns,
        IReadOnlyList<MonsterSpawnEntry> eventSpawnPool, MonsterInfoTable monsterInfo, MapRegistry maps,
        MonsterRegistry monsters, PlayerRegistry players, DataServerConnection dataServer)
    {
        _config = config;
        _entryLevels = entryLevels;
        _stageSpawns = stageSpawns;
        _eventSpawnPool = eventSpawnPool;
        _monsterInfo = monsterInfo;
        _maps = maps;
        _monsters = monsters;
        _players = players;
        _dataServer = dataServer;

        // Port of rows 58-61 of Gate.txt (entry) -- brackets 0-3, the only ones with real data in
        // DevilSquare.dat/EventEntryLevel.dat/EventStageSpawn.dat in this package (see README). Brackets 4-6
        // (MAX_DS_LEVEL=7) are left without data (HasData=false, they never leave BLANK) -- documented as a
        // quirk faithful to the original, GetDevilSquareLevel never returns them with these real files anyway
        // (bracket 3 has no upper level cap).
        var boxes = new (byte MinX, byte MinY, byte MaxX, byte MaxY)[]
        {
            ((byte)133, (byte)91, (byte)141, (byte)99),
            ((byte)135, (byte)162, (byte)142, (byte)170),
            ((byte)62, (byte)150, (byte)70, (byte)158),
            ((byte)66, (byte)84, (byte)74, (byte)92),
        };

        for (int level = 0; level < DevilSquareConfig.MaxLevel; level++)
        {
            bool hasData = level < boxes.Length;
            _brackets[level] = new DevilSquareBracket
            {
                Level = level,
                Map = (byte)(level < 4 ? 9 : 32), // MAP_DEVIL_SQUARE1 / MAP_DEVIL_SQUARE2 (Map.h:51,74)
                EntranceBox = hasData ? boxes[level] : ((byte)0, (byte)0, (byte)0, (byte)0),
                HasData = hasData,
            };
        }
    }

    /// <summary> Port of CDevilSquare::ForceStart (DevilSquare.cpp, triggered by the admin menu
    /// IDM_EVENT_FORCEDEVILSQUARE in GameServer.cpp:280-281) -- inserts a synthetic schedule entry "now + N
    /// seconds" (concrete, not a wildcard, like the original which uses "+1 minute") and resets the indicated
    /// bracket(s) to EMPTY so that CheckSync picks it up immediately. Exposed here as a console command ('ds
    /// forcestart[ N]', see Program.cs) -- it serves both for deterministic testing (without waiting for the
    /// real schedule every 4h) and for the same administrative use case the original had. </summary>
    public void ForceStart(int? level = null, int delaySeconds = 40)
    {
        var target = DateTime.UtcNow.AddSeconds(delaySeconds);
        _config.Schedule.Add(new DevilSquareStartTime(target.Year, target.Month, target.Day, -1, target.Hour, target.Minute, target.Second));

        foreach (var bracket in _brackets.Values)
        {
            if (!bracket.HasData || (level.HasValue && bracket.Level != level.Value))
            {
                continue;
            }

            SetStateEmpty(bracket);
        }

        Log.Add(LogColor.Blue, "[Devil Square] ForceStart -- next opening in ~{0}s ({1})", delaySeconds, level.HasValue ? $"bracket {level + 1}" : "todos los brackets");
    }

    public void Start(CancellationToken ct)
    {
        // Port of CDevilSquare::Init (DevilSquare.cpp) -- starts each bracket with data in EMPTY,
        // re-synchronised against the configured schedule. Without the global flag m_DevilSquareEvent (unported
        // server config) -- it is assumed always enabled, equivalent to having it at 1.
        foreach (var bracket in _brackets.Values)
        {
            if (bracket.HasData)
            {
                SetStateEmpty(bracket);
            }
        }

        _ = RunAsync(ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var bracket in _brackets.Values)
            {
                if (!bracket.HasData || bracket.State == DevilSquareState.Blank)
                {
                    continue;
                }

                try
                {
                    await TickBracketAsync(bracket, ct);
                }
                catch (Exception ex)
                {
                    Log.Add(LogColor.Red, "[DevilSquare] ({0}) Error in tick: {1}", bracket.Level + 1, ex.Message);
                }
            }
        }
    }

    // ---------------------------------------------------------------- Motor de estados (CDevilSquare::MainProc)

    private async Task TickBracketAsync(DevilSquareBracket bracket, CancellationToken ct)
    {
        CheckUser(bracket);
        double remainSec = (bracket.TargetTime - DateTime.UtcNow).TotalSeconds;

        switch (bracket.State)
        {
            case DevilSquareState.Empty:
                await ProcEmptyAsync(bracket, remainSec, ct);
                break;

            case DevilSquareState.Stand:
                await ProcStandAsync(bracket, remainSec, ct);
                break;

            case DevilSquareState.Start:
                await ProcStartAsync(bracket, remainSec, ct);
                break;

            case DevilSquareState.Clean:
                if (remainSec <= 0)
                {
                    SetStateEmpty(bracket);
                }

                break;
        }
    }

    /// <summary>Port of CheckUser (called from each ProcState_*) -- removes from the list whoever disconnected
    /// or is no longer standing on the bracket's map (walked away, or the process removed them for another
    /// reason). Up to 1s of latency like the original (1s ticker).</summary>
    private void CheckUser(DevilSquareBracket bracket)
    {
        bracket.Participants.RemoveAll(p => !_players.TryGet(p.PlayerIndex, out var player) || player.Map != bracket.Map);
    }

    private async Task ProcEmptyAsync(DevilSquareBracket bracket, double remainSec, CancellationToken ct)
    {
        if (remainSec > 0 && remainSec <= _config.Timing.WarningMinutes * 60)
        {
            bracket.EnterEnabled = true;
        }

        if (remainSec > 0 && remainSec <= 30 && !bracket.TimeCountSent)
        {
            // Port of DataSendAll (server-wide, not only to the participants -- unlike the rest of the notices,
            // which are bracket-scoped, see this phase's research).
            await BroadcastToAllOnlineAsync(DevilSquarePacketBuilder.TimeCountSend(0), ct);
            bracket.TimeCountSent = true;
        }

        if (remainSec <= 0)
        {
            SetStateStand(bracket);
        }
    }

    private async Task ProcStandAsync(DevilSquareBracket bracket, double remainSec, CancellationToken ct)
    {
        if (bracket.Participants.Count == 0)
        {
            Log.Add(LogColor.Black, "[Devil Square] ({0}) Not enough users -- back to EMPTY", bracket.Level + 1);
            SetStateEmpty(bracket);
            return;
        }

        if (remainSec > 0 && remainSec <= 30 && !bracket.TimeCountSent)
        {
            await BroadcastToParticipantsAsync(bracket, DevilSquarePacketBuilder.TimeCountSend(1), ct);
            bracket.TimeCountSent = true;
        }

        if (remainSec <= 0)
        {
            await SetStateStartAsync(bracket, ct);
        }
    }

    private async Task ProcStartAsync(DevilSquareBracket bracket, double remainSec, CancellationToken ct)
    {
        if (bracket.Participants.Count == 0)
        {
            Log.Add(LogColor.Black, "[Devil Square] ({0}) Not enough users -- back to EMPTY", bracket.Level + 1);
            SetStateEmpty(bracket);
            return;
        }

        // EXACT port of SetStage0/1/2/3 (DevilSquare.cpp:1120-1147): integer division truncating BEFORE
        // dividing by EventTime*60 -- do not simplify the order of operations, it changes the exact stage
        // transition instants.
        int totalSec = _config.Timing.EventMinutes * 60;
        int remainSecInt = Math.Max(0, (int)remainSec);
        int pct = totalSec <= 0 ? 0 : (remainSecInt * 100) / totalSec;

        if (bracket.Stage == 0 && pct <= 75)
        {
            bracket.Stage = 1;
            await StageSpawnAsync(bracket, 1, ct);
        }
        else if (bracket.Stage == 1 && pct <= 50)
        {
            bracket.Stage = 2;
            await StageSpawnAsync(bracket, 2, ct);
        }
        else if (bracket.Stage == 2 && pct <= 25)
        {
            bracket.Stage = 3;
            await StageSpawnAsync(bracket, 3, ct);
        }

        if (remainSec > 0 && remainSec <= 30 && !bracket.TimeCountSent)
        {
            await BroadcastToParticipantsAsync(bracket, DevilSquarePacketBuilder.TimeCountSend(2), ct);
            bracket.TimeCountSent = true;
        }

        if (remainSec <= 0)
        {
            await SetStateCleanAsync(bracket, ct);
        }
    }

    // ---------------------------------------------------------------- Transiciones (CDevilSquare::SetState_*)

    private void SetStateEmpty(DevilSquareBracket bracket)
    {
        bracket.EnterEnabled = false;
        bracket.TimeCountSent = false;
        bracket.Stage = 0;

        // Port of ClearUser (DevilSquare.cpp) -- teleports everyone who remained registered (if the bracket is
        // recycled with people still inside) back to Noria.
        foreach (var participant in bracket.Participants)
        {
            if (_players.TryGet(participant.PlayerIndex, out var player))
            {
                _ = TeleportOutAsync(player, CancellationToken.None);
            }
        }

        bracket.Participants.Clear();
        ClearMonsters(bracket);

        // Port of CheckSync (DevilSquare.cpp:474-509) -- recomputes from scratch the next schedule from the
        // WHOLE configured list (it does not advance a saved cursor), see the documented quirk.
        var next = ComputeNextOccurrence(_config.Schedule, DateTime.UtcNow);

        if (next == null)
        {
            bracket.State = DevilSquareState.Blank;
            return;
        }

        bracket.TargetTime = next.Value;
        bracket.State = DevilSquareState.Empty;
    }

    private void SetStateStand(DevilSquareBracket bracket)
    {
        bracket.EnterEnabled = false;
        bracket.TimeCountSent = false;
        bracket.TargetTime = DateTime.UtcNow.AddSeconds(_config.Timing.NotifyMinutes * 60);
        bracket.State = DevilSquareState.Stand;
    }

    private async Task SetStateStartAsync(DevilSquareBracket bracket, CancellationToken ct)
    {
        bracket.TimeCountSent = false;
        bracket.Stage = 0;
        bracket.TargetTime = DateTime.UtcNow.AddSeconds(_config.Timing.EventMinutes * 60);
        bracket.State = DevilSquareState.Start;
        await StageSpawnAsync(bracket, 0, ct);
    }

    private async Task SetStateCleanAsync(DevilSquareBracket bracket, CancellationToken ct)
    {
        bracket.TimeCountSent = false;
        ClearMonsters(bracket);
        CalcRanks(bracket);
        await FinishRunAsync(bracket, ct);
        bracket.TargetTime = DateTime.UtcNow.AddSeconds(_config.Timing.CloseMinutes * 60);
        bracket.State = DevilSquareState.Clean;
    }

    /// <summary>Port of CDevilSquare::CalcUserRank (DevilSquare.cpp:744-784) -- tie-break by order of entry
    /// (lowest slot index in the original, here directly the list order, see the comment of <see
    /// cref="DevilSquareParticipant"/>).</summary>
    private static void CalcRanks(DevilSquareBracket bracket)
    {
        var ranked = bracket.Participants
            .Select((p, insertOrder) => (p, insertOrder))
            .OrderByDescending(t => t.p.Score)
            .ThenBy(t => t.insertOrder)
            .ToList();

        for (int n = 0; n < ranked.Count; n++)
        {
            ranked[n].p.Rank = n;
        }
    }

    /// <summary>Puerto de GiveUserRewardExperience/GiveUserRewardMoney/GCDevilSquareScoreSend
    /// (DevilSquare.cpp:786-1370) -- recompensa (top 10 solamente) + paquete de puntaje (a TODOS los
    /// participantes) + guardado de ranking en DataServer (a TODOS los participantes, head 0x3F).</summary>
    private async Task FinishRunAsync(DevilSquareBracket bracket, CancellationToken ct)
    {
        var ranked = bracket.Participants.OrderBy(p => p.Rank).ToList();

        foreach (var participant in ranked)
        {
            if (!_players.TryGet(participant.PlayerIndex, out var player))
            {
                continue;
            }

            if (participant.Rank >= 0 && participant.Rank < DevilSquareConfig.MaxRank)
            {
                await ApplyRewardAsync(bracket, player, participant, ct);
            }

            await SendScoreListAsync(bracket, ranked, player, participant, ct);

            await _dataServer.SendAsync(
                EventDataServerPacketBuilder.RankingScoreSave(
                    EventDataServerPacketBuilder.HeadDevilSquare, (ushort)player.Index, player.Account, player.Name, participant.Score),
                ct);
        }

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) Event finished -- {1} participant(s) scored", bracket.Level + 1, ranked.Count);
    }

    private async Task ApplyRewardAsync(DevilSquareBracket bracket, PlayerObject player, DevilSquareParticipant participant, CancellationToken ct)
    {
        int rank = participant.Rank;
        long experience = _config.RewardExperience[bracket.Level][rank];
        uint money = (uint)_config.RewardMoney[bracket.Level][rank];

        if (experience > 0 && GrantExperience != null)
        {
            await GrantExperience(player, experience, ct);
        }

        if (money > 0)
        {
            // Port of gObjCheckMaxMoney -- simple cap so as not to overflow the uint (the original uses
            // MAX_MONEY=2000000000, the same value documented in several places of the original code).
            const uint maxMoney = 2_000_000_000;
            player.Money = money > maxMoney - player.Money ? maxMoney : player.Money + money;
            await player.Session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        }

        // ITEM reward (only rank 0) NOT ported -- see the technical debt documented in this class's header (it
        // requires a recursive ItemBagManager, outside this first pass).
    }

    private async Task SendScoreListAsync(DevilSquareBracket bracket, List<DevilSquareParticipant> ranked, PlayerObject recipient, DevilSquareParticipant self, CancellationToken ct)
    {
        var entries = new List<DevilSquareScoreEntry>();

        // EXACT port of GCDevilSquareScoreSend (DevilSquare.cpp:1292-1370): entry #0 is ALWAYS the recipient
        // themselves (even if repeated further down at their real sorted position) -- see the quirk documented
        // in this phase's research.
        entries.Add(BuildScoreEntry(bracket, self));

        foreach (var p in ranked)
        {
            if (entries.Count >= DevilSquareConfig.MaxRank)
            {
                break;
            }

            entries.Add(BuildScoreEntry(bracket, p));
        }

        var packet = DevilSquarePacketBuilder.ScoreSend((byte)(self.Rank + 1), entries);
        await recipient.Session.SendEncryptedAsync(packet, ct);
    }

    private DevilSquareScoreEntry BuildScoreEntry(DevilSquareBracket bracket, DevilSquareParticipant p)
    {
        string name = _players.TryGet(p.PlayerIndex, out var player) ? player.Name : string.Empty;
        int rank = Math.Max(p.Rank, 0);
        uint exp = rank < DevilSquareConfig.MaxRank ? (uint)Math.Max(_config.RewardExperience[bracket.Level][rank], 0) : 0;
        uint money = rank < DevilSquareConfig.MaxRank ? (uint)Math.Max(_config.RewardMoney[bracket.Level][rank], 0) : 0;
        return new DevilSquareScoreEntry(name, p.Score, exp, money);
    }

    // ---------------------------------------------------------------- Spawn de monstruos (CDevilSquare::StageSpawn/SetMonster)

    private async Task StageSpawnAsync(DevilSquareBracket bracket, int stage, CancellationToken ct)
    {
        var classes = _stageSpawns.GetMonsterClasses(bracket.Level, stage).Distinct().ToList();
        int spawned = 0;

        foreach (var monsterClass in classes)
        {
            var positions = _eventSpawnPool.Where(e => e.Map == bracket.Map && e.MonsterClass == monsterClass).ToList();

            foreach (var pos in positions)
            {
                var monster = _monsters.SpawnOne(monsterClass, pos, _monsterInfo, _maps);

                if (monster != null)
                {
                    monster.DevilSquareBracket = bracket.Level;
                    bracket.MonsterIndices.Add(monster.Index);
                    spawned++;
                }
            }
        }

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) Stage {1}: {2} monster(s) added", bracket.Level + 1, stage, spawned);
        await Task.CompletedTask;
    }

    private void ClearMonsters(DevilSquareBracket bracket)
    {
        foreach (var index in bracket.MonsterIndices)
        {
            _monsters.Remove(index);
        }

        bracket.MonsterIndices.Clear();
    }

    // ---------------------------------------------------------------- Entrada (CGDevilSquareEnterRecv)

    /// <summary> Port of CGDevilSquareEnterRecv (DevilSquare.cpp:1149-1290) -- full validation chain (without
    /// the daily entry cap check or PK, not ported, see the technical debt documented in the class header). It
    /// handles both the C1:90 reply and the entry teleport. </summary>
    public async Task HandleEnterAsync(ClientSession session, PlayerObject player, int level, int rawSlot, CancellationToken ct)
    {
        if (level < 0 || level >= DevilSquareConfig.MaxLevel || !_brackets.TryGetValue(level, out var bracket) || !bracket.HasData)
        {
            await SendEnterResultAsync(session, 1, ct);
            return;
        }

        // Port of "lpMsg->slot -= INVENTORY_WEAR_SIZE" + the later INVENTORY_FULL_RANGE check
        // (DevilSquare.cpp:1149-1290) -- the original subtracts 12 and uses the result as an index, which only
        // makes sense if it is an index ALREADY relative to the backpack that is later added back elsewhere not
        // visible in the available excerpt; to avoid reproducing a one-line ambiguity without the full context,
        // here the same single-index convention 0-107 as the rest of this port is used (ItemMoveAsync, SetItem,
        // etc. -- see Item.cs) validating directly against the backpack range (12-107).
        int realSlot = rawSlot;

        if (realSlot < Item.InventoryWearSize || realSlot >= Item.InventorySize)
        {
            await SendEnterResultAsync(session, 1, ct);
            return;
        }

        var item = player.Items[realSlot];

        bool isLeveled = item.IsItem() && item.Index == TicketItemLeveled;
        bool isUniversal = item.IsItem() && item.Index == TicketItemUniversal;

        if (!isLeveled && !isUniversal)
        {
            await SendEnterResultAsync(session, 1, ct);
            return;
        }

        if (isLeveled && item.Level != level + 1)
        {
            await SendEnterResultAsync(session, 1, ct);
            return;
        }

        if (!bracket.EnterEnabled)
        {
            await SendEnterResultAsync(session, 2, ct);
            return;
        }

        bool specialClass = player.Class == ClassMg;
        int computedLevel = _entryLevels.GetDevilSquareLevel(player.Level, specialClass);

        if (computedLevel > level)
        {
            await SendEnterResultAsync(session, 3, ct);
            return;
        }

        if (computedLevel < level)
        {
            await SendEnterResultAsync(session, 4, ct);
            return;
        }

        if (bracket.Participants.Count >= MaxDsUser)
        {
            await SendEnterResultAsync(session, 5, ct);
            return;
        }

        // Success -- consume the ticket (1 unit of "durability", like the rest of this port's stackable
        // consumables), notify the client of the change, register participant and teleport.
        byte newDurability = (byte)Math.Max(item.Durability - 1, 0);
        var finalItem = newDurability == 0
            ? Item.Empty()
            : new Item
            {
                Index = item.Index,
                Level = item.Level,
                Durability = newDurability,
                Serial = item.Serial,
                Option1 = item.Option1,
                Option2 = item.Option2,
                Option3 = item.Option3,
                NewOption = item.NewOption,
                SetOption = item.SetOption,
            };
        player.SetItem(realSlot, finalItem);
        // result = TargetFlag = 0 (Inventory), no un booleano -- ver doc-comment corregido de
        // ItemPacketBuilder.ItemMoveSend/ClientProtocolHandler.OnItemMoveAsync.
        await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, (byte)realSlot, finalItem), ct);

        bracket.Participants.Add(new DevilSquareParticipant { PlayerIndex = player.Index });

        await SendEnterResultAsync(session, 0, ct);
        await TeleportInAsync(player, bracket, ct);

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) '{1}' entered at X={2} Y={3} ({4} participant(s))",
            level + 1, player.Name, player.X, player.Y, bracket.Participants.Count);
    }

    private static Task SendEnterResultAsync(ClientSession session, byte result, CancellationToken ct) =>
        session.SendAsync(DevilSquarePacketBuilder.EnterSend(result), ct);

    /// <summary>Port of the portion of gObjMoveGate that applies when it DOES change map (User.cpp:222-247) --
    /// random position inside the entry box (same mechanism as CGate::GetGate), without the tile-block check
    /// (Type==1 of MonsterSpawnTable does do it, but the original gate does not do it either for event boxes --
    /// confirmed that gObjMoveGate does not call CheckAttr).</summary>
    private async Task TeleportInAsync(PlayerObject player, DevilSquareBracket bracket, CancellationToken ct)
    {
        var (minX, minY, maxX, maxY) = bracket.EntranceBox;
        byte x = (byte)(minX + Rng.Next(maxX - minX + 1));
        byte y = (byte)(minY + Rng.Next(maxY - minY + 1));

        player.Map = bracket.Map;
        player.X = x;
        player.Y = y;
        player.TX = x;
        player.TY = y;
        player.VisibleTo.Clear();
        player.VisibleMonsters.Clear();

        await player.Session.SendEncryptedAsync(
            WorldPacketBuilder.TeleportSend(0, player.Map, player.X, player.Y, player.Dir), ct);
    }

    private async Task TeleportOutAsync(PlayerObject player, CancellationToken ct)
    {
        byte x = (byte)(ExitBox.MinX + Rng.Next(ExitBox.MaxX - ExitBox.MinX + 1));
        byte y = (byte)(ExitBox.MinY + Rng.Next(ExitBox.MaxY - ExitBox.MinY + 1));

        player.Map = ExitBox.Map;
        player.X = x;
        player.Y = y;
        player.TX = x;
        player.TY = y;
        player.VisibleTo.Clear();
        player.VisibleMonsters.Clear();

        await player.Session.SendEncryptedAsync(
            WorldPacketBuilder.TeleportSend(0, player.Map, player.X, player.Y, player.Dir), ct);
    }

    // ---------------------------------------------------------------- Consulta de tiempo restante (C1:91)

    /// <summary> Port of the Devil Square branch of CGEventRemainTimeRecv (Protocol.cpp:1565-1590) -- ignores
    /// the ItemLevel the client sends and uses the bracket computed for the player themselves (like the
    /// original). Other EventTypes (Blood/Chaos/Kalima) are not ported -- they are silently ignored. </summary>
    public async Task HandleRemainTimeQueryAsync(ClientSession session, PlayerObject player, byte eventType, CancellationToken ct)
    {
        if (eventType != 1)
        {
            return;
        }

        bool specialClass = player.Class == ClassMg;
        int level = _entryLevels.GetDevilSquareLevel(player.Level, specialClass);

        if (level < 0 || level >= DevilSquareConfig.MaxLevel || !_brackets.TryGetValue(level, out var bracket) || !bracket.HasData)
        {
            return;
        }

        byte remainH;
        byte enteredUser;

        if (bracket.State == DevilSquareState.Empty)
        {
            if (!bracket.EnterEnabled)
            {
                var next = ComputeNextOccurrence(_config.Schedule, DateTime.UtcNow);
                int minutesLeft = next.HasValue ? (int)Math.Max(0, (next.Value - DateTime.UtcNow).TotalMinutes) : 0;
                remainH = (byte)Math.Min(minutesLeft, 255);
                enteredUser = 0;
            }
            else
            {
                remainH = 0;
                enteredUser = (byte)Math.Min(bracket.Participants.Count, 255);
            }
        }
        else
        {
            int minutesLeft = (int)Math.Max(0, (bracket.TargetTime - DateTime.UtcNow).TotalMinutes);
            remainH = (byte)Math.Min(minutesLeft, 255);
            enteredUser = 0;
        }

        await session.SendAsync(DevilSquarePacketBuilder.RemainTimeSend((byte)level, remainH, enteredUser), ct);
    }

    // ---------------------------------------------------------------- Puntaje (CDevilSquare::MonsterDieProc)

    /// <summary> Port of CDevilSquare::MonsterDieProc (DevilSquare.cpp:1077-1118) -- it is called AFTER
    /// ClientProtocolHandler already processed the normal experience (the two systems are independent in the
    /// original, they are not mutually exclusive). Credit goes to the attacker with the MOST accumulated damage
    /// (not whoever dealt the final blow -- gObjMonsterGetTopHitDamageUser), not to the caller's "killer"
    /// parameter. </summary>
    public async Task OnMonsterKilledAsync(Monster monster, CancellationToken ct)
    {
        if (monster.DevilSquareBracket is not int bracketIdx)
        {
            return;
        }

        if (!_brackets.TryGetValue(bracketIdx, out var bracket) || bracket.State != DevilSquareState.Start)
        {
            return;
        }

        if (!bracket.MonsterIndices.Contains(monster.Index))
        {
            return;
        }

        int topIndex = -1;
        int topDamage = -1;

        foreach (var (attackerIndex, damage) in monster.DamageByAttacker)
        {
            if (damage > topDamage)
            {
                topDamage = damage;
                topIndex = attackerIndex;
            }
        }

        var participant = bracket.Participants.FirstOrDefault(p => p.PlayerIndex == topIndex);

        if (participant == null)
        {
            return;
        }

        participant.Score += (uint)(monster.Level * (bracketIdx + 1));
        await Task.CompletedTask;
    }

    // ---------------------------------------------------------------- Utilidades

    private async Task BroadcastToAllOnlineAsync(byte[] packet, CancellationToken ct)
    {
        foreach (var player in _players.All)
        {
            if (player.WorldEntered)
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    private async Task BroadcastToParticipantsAsync(DevilSquareBracket bracket, byte[] packet, CancellationToken ct)
    {
        foreach (var participant in bracket.Participants)
        {
            if (_players.TryGet(participant.PlayerIndex, out var player))
            {
                await player.Session.SendAsync(packet, ct);
            }
        }
    }

    /// <summary> Port of CScheduleManager::GetSchedule as CheckSync uses it -- soonest future occurrence among
    /// ALL the configured rows, recomputed from scratch every time (it does not advance a persistent cursor).
    /// It supports with full fidelity the real case of this data package (Year/Month/Day/DayOfWeek at "*", only
    /// Hour/Minute/Second fixed); for combinations with fixed Year/Month/Day it does a bounded day-by-day
    /// search up to 2 years ahead (more than enough for any realistic schedule, and the real data package never
    /// uses them). The StartDoW convention (1~7) is not confirmed against the original -- 1=Sunday (.NET's
    /// DayOfWeek + 1) is assumed, documented because this package's real data always uses "*" here. </summary>
    internal static DateTime? ComputeNextOccurrence(IReadOnlyList<DevilSquareStartTime> schedule, DateTime fromUtc)
    {
        DateTime? best = null;

        foreach (var row in schedule)
        {
            var candidate = ComputeNextForRow(row, fromUtc);

            if (candidate.HasValue && (!best.HasValue || candidate.Value < best.Value))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static DateTime? ComputeNextForRow(DevilSquareStartTime row, DateTime fromUtc)
    {
        int hour = row.Hour == -1 ? 0 : row.Hour;
        int minute = row.Minute == -1 ? 0 : row.Minute;
        int second = row.Second == -1 ? 0 : row.Second;

        for (int dayOffset = 0; dayOffset < 731; dayOffset++)
        {
            var date = fromUtc.Date.AddDays(dayOffset);

            if (row.Year != -1 && date.Year != row.Year) continue;
            if (row.Month != -1 && date.Month != row.Month) continue;
            if (row.Day != -1 && date.Day != row.Day) continue;

            if (row.DayOfWeek != -1)
            {
                int dotnetDow = (int)date.DayOfWeek + 1;

                if (dotnetDow != row.DayOfWeek)
                {
                    continue;
                }
            }

            var candidate = new DateTime(date.Year, date.Month, date.Day, hour, minute, second, DateTimeKind.Utc);

            if (candidate > fromUtc)
            {
                return candidate;
            }
        }

        return null;
    }
}

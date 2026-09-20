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

/// <summary>Puerto simplificado de un slot de DEVIL_SQUARE_LEVEL.User[MAX_DS_USER] (DevilSquare.h) --
/// a diferencia del arreglo fijo de 50 slots con hueco/índice -1, acá es una entrada de una List&lt;&gt;
/// que preserva el orden de inserción a propósito: el desempate de <see cref="CalcRanks"/> depende de
/// ese orden (quirk documentado en la investigación de esta fase: el original desempata por índice de
/// slot más bajo = orden de ingreso, algo que un Dictionary no garantiza pero una List sí).</summary>
public sealed class DevilSquareParticipant
{
    public required int PlayerIndex { get; init; }
    public uint Score { get; set; }
    public int Rank { get; set; } = -1; // 0-based, calculado en CalcRanks
}

/// <summary>Puerto de DEVIL_SQUARE_LEVEL (DevilSquare.h) -- estado en runtime de UN bracket (0-6,
/// aunque solo 0-3 tienen datos reales en este paquete, ver README). MonsterIndices acumula TODOS los
/// monstruos spawneados en las 4 etapas de la corrida actual (no se vacía entre etapas -- puerto
/// exacto del comportamiento original, ver quirk de StageSpawn en la investigación de esta fase).</summary>
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

/// <summary>
/// Puerto de CDevilSquare (DevilSquare.h/.cpp) -- Fase 6, primera pasada. Motor de estados +
/// entrada/puntaje/recompensa de Devil Square. Simplificaciones documentadas explícitamente (mismo
/// criterio que el resto del proyecto -- deuda técnica visible, no oculta):
///
///  - Sin integración con el sistema de diálogo de NPC (CNpcTalk::NpcCharon) -- ese subsistema no
///    está portado todavía (ver ClientProtocolHandler, head 0x30/0x31 siguen sin implementar). El
///    cliente real necesitaría hablar con el NPC Charon para abrir la ventana de selección antes de
///    poder mandar C1:90 -- acá se asume que el cliente (o una herramienta de prueba) manda C1:90
///    directamente, el resto del flujo (validación, teleport, puntaje, recompensa, ranking) es fiel.
///  - Sin catálogo de mensajes de texto (Message.txt/gMessage) -- los avisos "Devil Square abre en N
///    minutos", "no tenés el nivel suficiente", etc. no se mandan (no hay sistema de notificaciones
///    de texto portado todavía). El paquete C1:92 (klaxon de 30 segundos, sin texto) SÍ se manda.
///  - Recompensa de ITEM (solo 1er puesto, ver EventItemBagManager.txt) no está portada -- requiere
///    el sistema recursivo de "bolsas" de ItemBagManager que no existe todavía en este puerto. XP y
///    Zen sí se otorgan completos y byte-exactos contra las tablas de DevilSquare.dat.
///  - Límite diario de entradas por cuenta (DSCount/m_DevilSquareMaxEntryCount) no portado -- se
///    puede entrar cuantas veces se quiera mientras la ventana esté abierta.
///  - Selección de posición de spawn: el original elige UNA posición libre al azar por instancia de
///    CDevilSquare::SetMonster; acá se instancia un monstruo en CADA posición candidata del pool
///    Type==4 para la clase/mapa (no se encontró en la investigación el número exacto de instancias
///    por llamada) -- es la interpretación más simple y fiel al pool de posiciones tal como está
///    definido en el archivo de datos real.
/// </summary>
public sealed class DevilSquareManager
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly Random Rng = Random.Shared;

    /// <summary>Puerto real de MAX_DS_USER (<c>gServerInfo.m_DevilSquareMaxUser</c>,
    /// <c>GameServerInfo - Event.dat</c> -- ver <see cref="MuServer.GameServer.Config.ServerInfoConfig"/>).
    /// CORREGIDO: antes de portar ese archivo este puerto usaba un tope hardcodeado de 50
    /// (arbitrario); el real es 15.</summary>
    private static int MaxDsUser => WorldPacketBuilder.ServerInfo.DevilSquareMaxUser;
    // Item.GetItem(sección,sub) = sección*32+sub (MaxItemType real de este build, ver Item.cs).
    // GetItem(14,19) = 467 ("Devil's Invitation").
    private static readonly int TicketItemLeveled = Item.GetItem(14, 19); // "Devil's Invitation"
    private static readonly int TicketItemUniversal = Item.GetItem(13, 46); // sin nivel -- ver quirk documentado en investigación
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

    /// <summary>Callback hacia ClientProtocolHandler para dar experiencia de evento reusando el mismo
    /// pipeline de subida de nivel que el combate normal (ver ClientProtocolHandler.GrantEventExperienceAsync)
    /// -- se resuelve como delegate en vez de referencia directa para no crear una dependencia circular
    /// en la construcción (DevilSquareManager se arma antes que ClientProtocolHandler, igual que
    /// ViewportTicker/protocolHandler en Program.cs).</summary>
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

        // Puerto de las filas 58-61 de Gate.txt (entrada) -- brackets 0-3, únicos con datos reales
        // en DevilSquare.dat/EventEntryLevel.dat/EventStageSpawn.dat en este paquete (ver README).
        // Brackets 4-6 (MAX_DS_LEVEL=7) quedan sin datos (HasData=false, nunca salen de BLANK) --
        // documentado como quirk fiel al original, GetDevilSquareLevel jamás los devuelve con estos
        // archivos reales de todas formas (bracket 3 no tiene tope superior de nivel).
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

    /// <summary>
    /// Puerto de CDevilSquare::ForceStart (DevilSquare.cpp, disparado por el menú de admin
    /// IDM_EVENT_FORCEDEVILSQUARE en GameServer.cpp:280-281) -- inserta una entrada de horario
    /// sintética "ahora + N segundos" (concreta, no comodín, igual que el original que usa "+1
    /// minuto") y reinicia el/los bracket(s) indicado(s) a EMPTY para que CheckSync la recoja de
    /// inmediato. Expuesto acá como comando de consola ('ds forcestart[ N]', ver Program.cs) --
    /// sirve tanto para testing determinístico (sin esperar el horario real de cada 4hs) como para
    /// el mismo caso de uso administrativo que tenía el original.
    /// </summary>
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

        Log.Add(LogColor.Blue, "[Devil Square] ForceStart -- próxima apertura en ~{0}s ({1})", delaySeconds, level.HasValue ? $"bracket {level + 1}" : "todos los brackets");
    }

    public void Start(CancellationToken ct)
    {
        // Puerto de CDevilSquare::Init (DevilSquare.cpp) -- arranca cada bracket con datos en EMPTY,
        // re-sincronizado contra el horario configurado. Sin el flag global m_DevilSquareEvent (config
        // de servidor no portada) -- se asume siempre habilitado, equivalente a tenerlo en 1.
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
                    Log.Add(LogColor.Red, "[DevilSquare] ({0}) Error en el tick: {1}", bracket.Level + 1, ex.Message);
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

    /// <summary>Puerto de CheckUser (llamado desde cada ProcState_*) -- saca de la lista a quien se
    /// desconectó o ya no está parado en el mapa del bracket (se fue caminando, o el proceso lo sacó
    /// por otro motivo). Hasta 1s de latencia como el original (ticker de 1s).</summary>
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
            // Puerto de DataSendAll (server-wide, no solo a los participantes -- distinto del resto
            // de los avisos, que son bracket-scoped, ver investigación de esta fase).
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
            Log.Add(LogColor.Black, "[Devil Square] ({0}) No hay suficientes usuarios -- vuelve a EMPTY", bracket.Level + 1);
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
            Log.Add(LogColor.Black, "[Devil Square] ({0}) No hay suficientes usuarios -- vuelve a EMPTY", bracket.Level + 1);
            SetStateEmpty(bracket);
            return;
        }

        // Puerto EXACTO de SetStage0/1/2/3 (DevilSquare.cpp:1120-1147): división entera truncando
        // ANTES de dividir por EventTime*60 -- no simplificar el orden de operaciones, cambia los
        // instantes exactos de transición de etapa.
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

        // Puerto de ClearUser (DevilSquare.cpp) -- teletransporta a todo el que haya quedado
        // registrado (si el bracket se recicla con gente todavía adentro) de vuelta a Noria.
        foreach (var participant in bracket.Participants)
        {
            if (_players.TryGet(participant.PlayerIndex, out var player))
            {
                _ = TeleportOutAsync(player, CancellationToken.None);
            }
        }

        bracket.Participants.Clear();
        ClearMonsters(bracket);

        // Puerto de CheckSync (DevilSquare.cpp:474-509) -- recalcula desde cero el próximo horario a
        // partir de TODA la lista configurada (no avanza un cursor guardado), ver quirk documentado.
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

    /// <summary>Puerto de CDevilSquare::CalcUserRank (DevilSquare.cpp:744-784) -- desempate por orden
    /// de ingreso (índice de slot más bajo en el original, acá directamente el orden de la lista, ver
    /// comentario de <see cref="DevilSquareParticipant"/>).</summary>
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

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) Evento terminado -- {1} participante(s) puntuados", bracket.Level + 1, ranked.Count);
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
            // Puerto de gObjCheckMaxMoney -- tope simple para no desbordar el uint (el original usa
            // MAX_MONEY=2000000000, mismo valor documentado en varios lados del código original).
            const uint maxMoney = 2_000_000_000;
            player.Money = money > maxMoney - player.Money ? maxMoney : player.Money + money;
            await player.Session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        }

        // Recompensa de ITEM (solo rank 0) NO portada -- ver deuda técnica documentada en el
        // encabezado de esta clase (requiere ItemBagManager recursivo, fuera de esta primera pasada).
    }

    private async Task SendScoreListAsync(DevilSquareBracket bracket, List<DevilSquareParticipant> ranked, PlayerObject recipient, DevilSquareParticipant self, CancellationToken ct)
    {
        var entries = new List<DevilSquareScoreEntry>();

        // Puerto EXACTO de GCDevilSquareScoreSend (DevilSquare.cpp:1292-1370): la entrada #0 es
        // SIEMPRE el propio recipiente (aunque se repita más abajo en su posición ordenada real) --
        // ver quirk documentado en la investigación de esta fase.
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

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) Etapa {1}: {2} monstruo(s) agregados", bracket.Level + 1, stage, spawned);
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

    /// <summary>
    /// Puerto de CGDevilSquareEnterRecv (DevilSquare.cpp:1149-1290) -- cadena de validación completa
    /// (sin el chequeo de tope diario de entradas ni PK, no portados, ver deuda técnica documentada
    /// en el encabezado de la clase). Maneja tanto la respuesta C1:90 como el teleport de entrada.
    /// </summary>
    public async Task HandleEnterAsync(ClientSession session, PlayerObject player, int level, int rawSlot, CancellationToken ct)
    {
        if (level < 0 || level >= DevilSquareConfig.MaxLevel || !_brackets.TryGetValue(level, out var bracket) || !bracket.HasData)
        {
            await SendEnterResultAsync(session, 1, ct);
            return;
        }

        // Puerto de "lpMsg->slot -= INVENTORY_WEAR_SIZE" + el chequeo INVENTORY_FULL_RANGE posterior
        // (DevilSquare.cpp:1149-1290) -- el original resta 12 y usa el resultado como índice, lo cual
        // solo tiene sentido si es un índice YA relativo a mochila que luego se vuelve a sumar en
        // otro lado no visible en el extracto disponible; para evitar reproducir una ambigüedad de
        // una sola línea sin el contexto completo, acá se usa la misma convención de índice único
        // 0-107 que el resto de este puerto (ItemMoveAsync, SetItem, etc. -- ver Item.cs) validando
        // directamente contra el rango de mochila (12-107).
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

        // Éxito -- consumir el ticket (1 unidad de "durabilidad", igual que el resto de consumibles
        // apilables de este puerto), avisar al cliente del cambio, registrar participante y
        // teletransportar.
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

        Log.Add(LogColor.Blue, "[Devil Square] ({0}) '{1}' entró en X={2} Y={3} ({4} participante(s))",
            level + 1, player.Name, player.X, player.Y, bracket.Participants.Count);
    }

    private static Task SendEnterResultAsync(ClientSession session, byte result, CancellationToken ct) =>
        session.SendAsync(DevilSquarePacketBuilder.EnterSend(result), ct);

    /// <summary>Puerto de la porción de gObjMoveGate que aplica cuando SÍ cambia de mapa
    /// (User.cpp:222-247) -- posición al azar dentro de la caja de entrada (mismo mecanismo que
    /// CGate::GetGate), sin el chequeo de bloqueo de tile (Type==1 de MonsterSpawnTable sí lo hace,
    /// pero el gate original tampoco lo hace para cajas de evento -- confirmado que gObjMoveGate no
    /// llama CheckAttr).</summary>
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

    /// <summary>
    /// Puerto de la rama Devil Square de CGEventRemainTimeRecv (Protocol.cpp:1565-1590) -- ignora el
    /// ItemLevel que manda el cliente y usa el bracket calculado del propio jugador (igual que el
    /// original). Otros EventType (Blood/Chaos/Kalima) no están portados -- se ignoran en silencio.
    /// </summary>
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

    /// <summary>
    /// Puerto de CDevilSquare::MonsterDieProc (DevilSquare.cpp:1077-1118) -- se llama DESPUÉS de que
    /// ClientProtocolHandler ya procesó la experiencia normal (los dos sistemas son independientes en
    /// el original, no se excluyen). Crédito al atacante con MÁS daño acumulado (no al que dio el
    /// golpe final -- gObjMonsterGetTopHitDamageUser), no al parámetro "killer" del llamador.
    /// </summary>
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

    /// <summary>
    /// Puerto de CScheduleManager::GetSchedule tal como lo usa CheckSync -- soonest future occurrence
    /// entre TODAS las filas configuradas, recalculado desde cero cada vez (no avanza un cursor
    /// persistente). Soporta con fidelidad completa el caso real de este paquete de datos
    /// (Year/Month/Day/DayOfWeek en "*", solo Hour/Minute/Second fijos); para combinaciones con
    /// Year/Month/Day fijos hace una búsqueda acotada día por día hasta 2 años adelante (más que
    /// suficiente para cualquier horario realista, y el paquete de datos real nunca los usa).
    /// La convención de StartDoW (1~7) no está confirmada contra el original -- se asume 1=Domingo
    /// (DayOfWeek de .NET + 1), documentado porque el dato real de este paquete siempre usa "*" acá.
    /// </summary>
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

using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

// Puerto de los archivos de datos de "eventos especiales" leídos por CDevilSquare::Load,
// CEventEntryLevel::Load y CEventSpawnStage::Load (DevilSquare.cpp/EventEntryLevel.cpp/
// EventSpawnStage.cpp) -- Fase 6, primera pasada (Devil Square). Los loaders de EventEntryLevel.dat
// y EventStageSpawn.dat son genéricos por "sección" a propósito (mismo archivo compartido por Blood
// Castle/Chaos Castle/Kalima/Devil Square en el original) para no tener que reescribirlos cuando se
// porten esos otros eventos -- por ahora solo se consume la sección 1 (Devil Square) de cada uno.

/// <summary>Puerto de la sección 0 de DevilSquare.dat (minutos). Puerto de DEVIL_SQUARE_LEVEL's
/// campos de configuración compartidos (DevilSquare.h) -- WarningTime/NotifyTime/EventTime/CloseTime
/// están en MINUTOS en el archivo, se usan *60 como segundos en el motor de estados.</summary>
public sealed record DevilSquareTiming(int WarningMinutes, int NotifyMinutes, int EventMinutes, int CloseMinutes);

/// <summary>Puerto de una fila de la sección 1 de DevilSquare.dat (DEVIL_SQUARE_START_TIME) --
/// horario cron-like de disparo; -1 (leído de "*" por MemScript) = comodín en ese campo.</summary>
public sealed record DevilSquareStartTime(int Year, int Month, int Day, int DayOfWeek, int Hour, int Minute, int Second);

/// <summary>
/// Puerto de CDevilSquare::Load (DevilSquare.cpp:79-196) -- Data/Event/DevilSquare.dat. Las tablas
/// de recompensa están indexadas [bracket 0-3][rank 0-9] (rank 0 = 1er puesto).
///
/// REVERTIDO: una pasada de porting anterior había puesto <c>MaxLevel=7</c> citando
/// "GAMESERVER_UPDATE=803, DevilSquare.h:10-14" -- ese árbol de fuente (sin sufijo de versión) es el
/// EQUIVOCADO para este proyecto (ver el doc-comment de <see cref="World.Item"/> para la explicación
/// completa). El real, <c>DevilSquare.h:10</c> del árbol correcto ("Emulator 0.99 (2.1.7)/GameServer"),
/// define <c>#define MAX_DS_LEVEL 4</c> -- confirmado también en <c>EventEntryLevel.cpp:187-227</c>
/// (<c>CEventEntryLevel::GetDSLevel</c>), que itera <c>for(n=0;n&lt;4;n++)</c> sobre ambas tablas de
/// bracket (MG/DL y el resto de clases). Solo existen 4 brackets en este build, no 7.
/// </summary>
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

        Log.Add(LogColor.Blue, "[DevilSquare] DevilSquare.dat cargado: {0} horario(s) de inicio, {1} bracket(s) con recompensa", config.Schedule.Count, config.RewardExperience.Count(r => r.Any(v => v != 0)));
        return config;
    }
}

/// <summary>Puerto de una fila de EventEntryLevel.dat (EventEntryLevel.cpp) -- CommonMin/Max aplica a
/// clases normales, SpecialMin/Max a MG/DL/RF (desbloqueo más temprano). -1 ("*") = sin límite.</summary>
public sealed record EventEntryLevelBracket(int Index, int CommonMinLevel, int CommonMaxLevel, int SpecialMinLevel, int SpecialMaxLevel)
{
    public bool InRange(int level, bool special)
    {
        int min = special ? SpecialMinLevel : CommonMinLevel;
        int max = special ? SpecialMaxLevel : CommonMaxLevel;
        return level >= min && (max == -1 || level <= max);
    }
}

/// <summary>
/// Puerto genérico de CEventEntryLevel::Load (EventEntryLevel.cpp) -- Data/Event/EventEntryLevel.dat,
/// secciones 0=Blood Castle, 1=Devil Square, 2=Chaos Castle, 3=Kalima (todas comparten el mismo
/// archivo/formato en el original). Esta fase solo consume la sección 1 vía
/// <see cref="GetDevilSquareLevel"/>.
/// </summary>
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

        Log.Add(LogColor.Blue, "[EventEntryLevel] {0} sección(es) cargadas desde {1}", table._sections.Count, path);
        return table;
    }

    /// <summary>
    /// Puerto de CEventEntryLevel::GetDSLevel (EventEntryLevel.cpp:187-227 del árbol fuente correcto)
    /// -- itera sobre las tablas MG/DL y el resto de clases para los 4 brackets reales (ver
    /// <see cref="DevilSquareConfig.MaxLevel"/>). Recorre TODAS las filas de la sección 1 y se queda
    /// con la ÚLTIMA que matchea (igual que el original, no corta en el primer match) -- devuelve -1
    /// si ninguna aplica.
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

/// <summary>Puerto de una fila de EventStageSpawn.dat (EventSpawnStage.h/.cpp) -- qué clase de
/// monstruo se agrega a partir de qué etapa (Stage 0-3) para un bracket dado. MaxRegenMs=-1 ("*")
/// significa "no respawnea" (CDevilSquare::SetMonster deja PosNum=-1 en ese caso); para Devil Square
/// en este paquete de datos siempre es 1000ms.</summary>
public sealed record EventStageSpawnEntry(int Bracket, int Stage, int MonsterClass, int MaxRegenMs);

/// <summary>
/// Puerto genérico de CEventSpawnStage::Load -- Data/Event/EventStageSpawn.dat, secciones
/// 0=Blood Castle, 1=Chaos Castle, 2=Devil Square (confirmado contra el archivo real de este paquete,
/// que solo tiene 3 secciones). Esta fase solo consume la sección 2.
/// </summary>
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
                // Se guarda con la sección embebida implícitamente en el orden de carga -- como solo
                // se consulta por (bracket,stage) dentro de GetDevilSquareMonsterClasses, alcanza con
                // filtrar por sección acá antes de agregar si no es la 2, para no mezclar BC/CC.
                if (section != SectionDevilSquare)
                {
                    table._entries.RemoveAt(table._entries.Count - 1);
                }
            }
        }

        Log.Add(LogColor.Blue, "[EventStageSpawn] {0} fila(s) de Devil Square cargadas desde {1}", table._entries.Count, path);
        return table;
    }

    /// <summary>Clases de monstruo que se agregan al llegar a <paramref name="stage"/> (0-3) del
    /// bracket dado -- puerto de la parte de filtro de CDevilSquare::StageSpawn (DevilSquare.cpp).</summary>
    public IEnumerable<int> GetMonsterClasses(int bracket, int stage) =>
        _entries.Where(e => e.Bracket == bracket && e.Stage == stage).Select(e => e.MonsterClass);
}

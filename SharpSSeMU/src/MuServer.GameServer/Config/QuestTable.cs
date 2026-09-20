using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.Config;

/// <summary>
/// Puerto de QUEST_INFO (Quest.h:122-132) -- una fila de <c>Data/Quest/Quest.txt</c>. Cada índice de
/// misión (<see cref="Index"/>) aparece MÚLTIPLES veces en el archivo, una por cada
/// <see cref="CurrentState"/> posible (0=NORMAL,1=ACCEPT,2=FINISH,3=CANCEL) -- el motor real
/// (<c>CQuest::GetInfoByIndex</c>/<c>NpcTalk</c>) busca la fila cuyo <see cref="CurrentState"/>
/// coincide con el estado ACTUAL guardado del jugador para ese índice, y ahí aplica el resto de los
/// requisitos (nivel, clase, prerequisito). <c>-1</c> en <see cref="RequireIndex"/>/
/// <see cref="RequireMinLevel"/>/<see cref="RequireMaxLevel"/> es el <c>"*"</c> del archivo ("sin
/// restricción"), tokenizado por <see cref="MemScript"/> igual que el resto de los .txt de Data/.
/// </summary>
public sealed record QuestInfo(
    int Index, int MonsterClass, int CurrentState, int RequireIndex, int RequireState,
    int RequireMinLevel, int RequireMaxLevel, int[] RequireClass);

/// <summary>
/// Puerto de CQuest (Quest.h/.cpp) -- SOLO la parte de datos + los chequeos de elegibilidad
/// (<see cref="CheckQuestRequisite"/>/<see cref="CheckQuestListState"/>/<see cref="GetInfoByIndex"/>/
/// <see cref="NpcTalk"/>), que es lo que <c>ClientProtocolHandler.OnNpcTalkAsync</c>/
/// <c>OnQuestStateAsync</c> necesitan. El resto de <c>CQuest</c> (envío de paquetes,
/// <c>CGQuestNpcWarewolfRecv</c>/<c>CGQuestNpcKeeperRecv</c> -- gates de otras misiones no
/// relacionadas al cambio de 2da clase) sigue en <c>ClientProtocolHandler</c>/sin portar, como el
/// resto de los sistemas de paquetes de este proyecto.
///
/// Reemplaza una implementación anterior que hardcodeaba "Sebina"/"Marlon" con un nivel mínimo fijo
/// (150) y dos slots de misión ad-hoc en C# -- esta clase carga el <c>Quest.txt</c> REAL (16 filas,
/// misiones 0-1 = Sebina/235 = cambio a 2da clase, misiones 2-3 = Marlon/229 = otra recompensa a
/// nivel 220) en vez de adivinar los valores, cumpliendo el mismo estándar de "nada hardcodeado" que
/// el resto de <c>GameServerInfo - *.dat</c>.
/// </summary>
public sealed class QuestTable
{
    public IReadOnlyList<QuestInfo> Entries { get; }

    private QuestTable(List<QuestInfo> entries) => Entries = entries;

    public static QuestTable Empty { get; } = new(new List<QuestInfo>());

    public static QuestTable Load(string path)
    {
        var entries = new List<QuestInfo>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[QuestTable] {0}", script.GetLastError());
            return new QuestTable(entries);
        }

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
            int monsterClass = script.GetAsNumber();
            int currentState = script.GetAsNumber();
            int requireIndex = script.GetAsNumber();
            int requireState = script.GetAsNumber();
            int requireMinLevel = script.GetAsNumber();
            int requireMaxLevel = script.GetAsNumber();
            var requireClass = new int[5];

            for (int n = 0; n < 5; n++)
            {
                requireClass[n] = script.GetAsNumber();
            }

            entries.Add(new QuestInfo(index, monsterClass, currentState, requireIndex, requireState, requireMinLevel, requireMaxLevel, requireClass));
        }

        Log.Add(LogColor.Blue, "[QuestTable] {0} entradas cargadas desde {1}", entries.Count, path);
        return new QuestTable(entries);
    }

    /// <summary>Puerto de CQuest::CheckQuestListState (Quest.cpp:165-178): ¿el estado guardado del
    /// jugador para <paramref name="questIndex"/> es exactamente <paramref name="state"/>? (empaquetado
    /// 2 bits por índice, 4 índices por byte -- mismo layout que
    /// ClientProtocolHandler.GetQuestState/SetQuestState, reutilizados acá).</summary>
    public static bool CheckQuestListState(byte[] questBlob, int questIndex, int state)
    {
        if (questIndex < 0 || questIndex >= 200)
        {
            return false;
        }

        return GetQuestState(questBlob, questIndex) == state;
    }

    public static byte GetQuestState(byte[] questBlob, int questIndex)
    {
        if (questIndex < 0 || questIndex >= 200 || questBlob == null || questBlob.Length <= questIndex / 4)
        {
            return 0;
        }

        return (byte)((questBlob[questIndex / 4] >> ((questIndex % 4) * 2)) & 3);
    }

    public static void SetQuestState(byte[] questBlob, int questIndex, byte state)
    {
        if (questIndex < 0 || questIndex >= 200 || questBlob == null || questBlob.Length <= questIndex / 4)
        {
            return;
        }

        int byteIdx = questIndex / 4;
        int bitShift = (questIndex % 4) * 2;
        questBlob[byteIdx] = (byte)((questBlob[byteIdx] & ~(3 << bitShift)) | ((state & 3) << bitShift));
    }

    /// <summary>Puerto EXACTO de CQuest::CheckQuestRequisite (Quest.cpp:135-163).</summary>
    public static bool CheckQuestRequisite(QuestInfo info, byte[] quest, int level, int playerClass, int changeUp)
    {
        if (!CheckQuestListState(quest, info.Index, info.CurrentState))
        {
            return false;
        }

        if (info.RequireIndex != -1 && !CheckQuestListState(quest, info.RequireIndex, info.RequireState))
        {
            return false;
        }

        if (info.RequireMinLevel != -1 && info.RequireMinLevel > level)
        {
            return false;
        }

        if (info.RequireMaxLevel != -1 && info.RequireMaxLevel < level)
        {
            return false;
        }

        if (playerClass < 0 || playerClass >= info.RequireClass.Length)
        {
            return false;
        }

        if (info.RequireClass[playerClass] == 0 || info.RequireClass[playerClass] > changeUp + 1)
        {
            return false;
        }

        return true;
    }

    /// <summary>Puerto EXACTO de CQuest::GetInfoByIndex (Quest.cpp:91-111): la fila de
    /// <paramref name="questIndex"/> cuyo <see cref="QuestInfo.CurrentState"/> coincide con el
    /// estado actual del jugador Y cuyos demás requisitos se cumplen, o null si ninguna.</summary>
    public QuestInfo? GetInfoByIndex(int questIndex, byte[] quest, int level, int playerClass, int changeUp)
    {
        foreach (var info in Entries)
        {
            if (info.Index != questIndex)
            {
                continue;
            }

            if (CheckQuestRequisite(info, quest, level, playerClass, changeUp))
            {
                return info;
            }
        }

        return null;
    }

    /// <summary>Puerto EXACTO de CQuest::NpcTalk (Quest.cpp:195-219): la PRIMERA fila (en orden del
    /// archivo) cuyo <see cref="QuestInfo.MonsterClass"/> coincide con el NPC Y cuyos requisitos se
    /// cumplen para el estado ACTUAL del jugador. Si no hay ninguna, devuelve null -- el llamador NO
    /// debe mandar ningún paquete en ese caso (ver doc-comment de
    /// ClientProtocolHandler.OnNpcTalkAsync: el original no contesta nada cuando no hay misión
    /// disponible, a diferencia de la versión anterior de este puerto que mandaba QuestResultSend con
    /// 0xFF y el cliente real lo mostraba como "Conversation is over").</summary>
    public QuestInfo? NpcTalk(int npcMonsterClass, byte[] quest, int level, int playerClass, int changeUp)
    {
        foreach (var info in Entries)
        {
            if (info.MonsterClass != npcMonsterClass)
            {
                continue;
            }

            if (CheckQuestRequisite(info, quest, level, playerClass, changeUp))
            {
                return info;
            }
        }

        return null;
    }
}

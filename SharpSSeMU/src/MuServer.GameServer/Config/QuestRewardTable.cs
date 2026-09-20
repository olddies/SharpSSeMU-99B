using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.Config;

[Flags]
public enum QuestRewardType
{
    None = 0,
    Point = 1,
    Change1 = 2, // cambio de 1ra -> 2da clase (DBClass%16 pasa de 0 a 1)
    Hero = 4,
    Combo = 8,
}

/// <summary>Puerto de QUEST_REWARD_INFO (QuestReward.h:19-33) -- una fila de
/// <c>Data/Quest/QuestReward.txt</c>. La fila con <see cref="Type"/>==Change1 es literalmente el
/// cambio de 2da clase real (QuestReward.cpp:137-153): sube <c>ChangeUp</c> de 0 a 1.</summary>
public sealed record QuestRewardInfo(
    int Sort, QuestRewardType Type, int Index, int Quantity, int Level, int Option1, int Option2, int Option3,
    int NewOption, int RequireIndex, int RequireState, int[] RequireClass);

/// <summary>Puerto de CQuestReward (QuestReward.h/.cpp) -- SOLO datos + el chequeo de elegibilidad;
/// aplicar la recompensa (<c>InsertQuestReward</c>) vive en
/// <c>ClientProtocolHandler.OnQuestStateAsync</c> porque necesita tocar <c>PlayerObject</c>/mandar
/// paquetes, igual que el resto de este proyecto separa "datos" de "protocolo".</summary>
public sealed class QuestRewardTable
{
    public IReadOnlyList<QuestRewardInfo> Entries { get; }

    private QuestRewardTable(List<QuestRewardInfo> entries) => Entries = entries;

    public static QuestRewardTable Empty { get; } = new(new List<QuestRewardInfo>());

    public static QuestRewardTable Load(string path)
    {
        var entries = new List<QuestRewardInfo>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[QuestRewardTable] {0}", script.GetLastError());
            return new QuestRewardTable(entries);
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

            int sort = script.GetNumber();
            var type = (QuestRewardType)script.GetAsNumber();
            int index = script.GetAsNumber();
            int quantity = script.GetAsNumber();
            int level = script.GetAsNumber();
            int option1 = script.GetAsNumber();
            int option2 = script.GetAsNumber();
            int option3 = script.GetAsNumber();
            int newOption = script.GetAsNumber();
            int requireIndex = script.GetAsNumber();
            int requireState = script.GetAsNumber();
            var requireClass = new int[5];

            for (int n = 0; n < 5; n++)
            {
                requireClass[n] = script.GetAsNumber();
            }

            entries.Add(new QuestRewardInfo(sort, type, index, quantity, level, option1, option2, option3,
                newOption, requireIndex, requireState, requireClass));
        }

        Log.Add(LogColor.Blue, "[QuestRewardTable] {0} entradas cargadas desde {1}", entries.Count, path);
        return new QuestRewardTable(entries);
    }

    /// <summary>Puerto EXACTO de CQuestReward::CheckQuestRewardRequisite (QuestReward.cpp:101-114).</summary>
    public static bool CheckRequisite(QuestRewardInfo info, byte[] quest, int playerClass, int changeUp)
    {
        if (info.RequireIndex != -1 && !QuestTable.CheckQuestListState(quest, info.RequireIndex, info.RequireState))
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
}

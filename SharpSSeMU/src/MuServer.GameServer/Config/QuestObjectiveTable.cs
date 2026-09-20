using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.Config;

public enum QuestObjectiveType
{
    None = 0,
    Item = 1,
    Money = 2,
}

/// <summary>Puerto de QUEST_OBJECTIVE_INFO (QuestObjective.h:29-47) -- una fila de
/// <c>Data/Quest/QuestObjective.txt</c>. <see cref="RequireIndex"/>/<see cref="RequireState"/> es lo
/// que ata este objetivo a un ÍNDICE Y ESTADO concretos de <see cref="QuestTable"/> (ej: "para
/// completar la misión 0 en estado ACCEPT hace falta 1x item 471").</summary>
public sealed record QuestObjectiveInfo(
    int Sort, QuestObjectiveType Type, int Index, int Quantity, int Level, int Option1, int Option2, int Option3,
    int NewOption, int MapNumber, int DropMinLevel, int DropMaxLevel, int ItemDropRate,
    int RequireIndex, int RequireState, int[] RequireClass);

/// <summary>
/// Puerto de CQuestObjective (QuestObjective.h/.cpp) -- los objetivos (costo en Zen o item requerido)
/// que hay que cumplir para avanzar cada paso de una misión, y el drop de items de misión al matar
/// monstruos del nivel/mapa correctos (<see cref="MonsterItemDrop"/>, puerto de
/// QuestObjective.cpp:213-269, enganchado en el cascada de loot real justo después de
/// ItemBagManager y antes de DropEvent/ItemDrop/MoneyDrop -- ver Monster.cpp:59-79).
/// </summary>
public sealed class QuestObjectiveTable
{
    public IReadOnlyList<QuestObjectiveInfo> Entries { get; }

    private QuestObjectiveTable(List<QuestObjectiveInfo> entries) => Entries = entries;

    public static QuestObjectiveTable Empty { get; } = new(new List<QuestObjectiveInfo>());

    public static QuestObjectiveTable Load(string path)
    {
        var entries = new List<QuestObjectiveInfo>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[QuestObjectiveTable] {0}", script.GetLastError());
            return new QuestObjectiveTable(entries);
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
            var type = (QuestObjectiveType)script.GetAsNumber();
            int index = script.GetAsNumber();
            int quantity = script.GetAsNumber();
            int level = script.GetAsNumber();
            int option1 = script.GetAsNumber();
            int option2 = script.GetAsNumber();
            int option3 = script.GetAsNumber();
            int newOption = script.GetAsNumber();
            int mapNumber = script.GetAsNumber();
            int dropMinLevel = script.GetAsNumber();
            int dropMaxLevel = script.GetAsNumber();
            int itemDropRate = script.GetAsNumber();
            int requireIndex = script.GetAsNumber();
            int requireState = script.GetAsNumber();
            var requireClass = new int[5];

            for (int n = 0; n < 5; n++)
            {
                requireClass[n] = script.GetAsNumber();
            }

            entries.Add(new QuestObjectiveInfo(sort, type, index, quantity, level, option1, option2, option3,
                newOption, mapNumber, dropMinLevel, dropMaxLevel, itemDropRate, requireIndex, requireState, requireClass));
        }

        Log.Add(LogColor.Blue, "[QuestObjectiveTable] {0} entradas cargadas desde {1}", entries.Count, path);
        return new QuestObjectiveTable(entries);
    }

    /// <summary>Puerto EXACTO de CQuestObjective::CheckQuestObjectiveRequisite (QuestObjective.cpp:123-136).</summary>
    public static bool CheckRequisite(QuestObjectiveInfo info, byte[] quest, int playerClass, int changeUp)
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

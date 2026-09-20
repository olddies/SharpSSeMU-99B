using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.Config;

/// <summary> Port of QUEST_INFO (Quest.h:122-132) -- a row of <c>Data/Quest/Quest.txt</c>. Each quest index
/// (<see cref="Index"/>) appears MULTIPLE times in the file, once for each possible <see cref="CurrentState"/>
/// (0=NORMAL,1=ACCEPT,2=FINISH,3=CANCEL) -- the real engine (<c>CQuest::GetInfoByIndex</c>/<c>NpcTalk</c>)
/// looks for the row whose <see cref="CurrentState"/> matches the player's stored CURRENT state for that index,
/// and there applies the rest of the requirements (level, class, prerequisite). <c>-1</c> in <see
/// cref="RequireIndex"/>/ <see cref="RequireMinLevel"/>/<see cref="RequireMaxLevel"/> is the file's <c>"*"</c>
/// ("no restriction"), tokenised by <see cref="MemScript"/> like the rest of the Data/ .txt files. </summary>
public sealed record QuestInfo(
    int Index, int MonsterClass, int CurrentState, int RequireIndex, int RequireState,
    int RequireMinLevel, int RequireMaxLevel, int[] RequireClass);

/// <summary> Port of CQuest (Quest.h/.cpp) -- ONLY the data part + the eligibility checks (<see
/// cref="CheckQuestRequisite"/>/<see cref="CheckQuestListState"/>/<see cref="GetInfoByIndex"/>/ <see
/// cref="NpcTalk"/>), which is what <c>ClientProtocolHandler.OnNpcTalkAsync</c>/ <c>OnQuestStateAsync</c> need.
/// The rest of <c>CQuest</c> (packet sending, <c>CGQuestNpcWarewolfRecv</c>/<c>CGQuestNpcKeeperRecv</c> --
/// gates of other quests unrelated to the 2nd-class change) stays in <c>ClientProtocolHandler</c>/unported,
/// like the rest of this project's packet systems. It replaces an earlier implementation that hardcoded
/// "Sebina"/"Marlon" with a fixed minimum level (150) and two ad-hoc quest slots in C# -- this class loads the
/// REAL <c>Quest.txt</c> (16 rows, quests 0-1 = Sebina/235 = change to 2nd class, quests 2-3 = Marlon/229 =
/// another reward at level 220) instead of guessing the values, meeting the same "nothing hardcoded" standard
/// as the rest of <c>GameServerInfo - *.dat</c>. </summary>
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

        Log.Add(LogColor.Blue, "[QuestTable] {0} entries loaded from {1}", entries.Count, path);
        return new QuestTable(entries);
    }

    /// <summary>Port of CQuest::CheckQuestListState (Quest.cpp:165-178): is the player's stored state for
    /// <paramref name="questIndex"/> exactly <paramref name="state"/>? (packed 2 bits per index, 4 indices per
    /// byte -- same layout as ClientProtocolHandler.GetQuestState/SetQuestState, reused here).</summary>
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

    /// <summary>EXACT port of CQuest::GetInfoByIndex (Quest.cpp:91-111): the row of <paramref
    /// name="questIndex"/> whose <see cref="QuestInfo.CurrentState"/> matches the player's current state AND
    /// whose other requirements are met, or null if none.</summary>
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

    /// <summary>EXACT port of CQuest::NpcTalk (Quest.cpp:195-219): the FIRST row (in file order) whose <see
    /// cref="QuestInfo.MonsterClass"/> matches the NPC AND whose requirements are met for the player's CURRENT
    /// state. If there is none, it returns null -- the caller must NOT send any packet in that case (see the
    /// doc-comment of ClientProtocolHandler.OnNpcTalkAsync: the original answers nothing when there is no quest
    /// available, unlike this port's earlier version that sent QuestResultSend with 0xFF and the real client
    /// showed it as "Conversation is over").</summary>
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

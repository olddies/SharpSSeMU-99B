using MuServer.GameServer.Config;
using NUnit.Framework;

namespace MuServer.GameServer.Tests;

/// <summary>The quest item drop requisites (CQuestObjective::CheckQuestObjectiveRequisite): who can get the second
/// class items. The rows are the shipped QuestObjective.txt ones for the first two quests.</summary>
public class QuestDropTests
{
    // Sort Type Index Quantity Level Option1 Option2 Option3 NewOption Map DropMin DropMax Rate RequireIndex RequireState DW DK FE MG DL
    private static readonly QuestObjectiveInfo ScrollOfEmperor = new(0, QuestObjectiveType.Item, 471, 1, 0, 0, 0, 0, 0, -1, 45, 60, 10, 0, 1, new[] { 1, 1, 1, 0, 0 });
    private static readonly QuestObjectiveInfo BrokenSword = new(0, QuestObjectiveType.Item, 472, 1, 0, 0, 0, 0, 0, -1, 62, 76, 10, 1, 1, new[] { 0, 1, 0, 0, 0 });

    /// <summary>Quest bytes as a real character has them: 2 bits per quest, 0xFF = nothing started.</summary>
    private static byte[] Quests(byte first)
    {
        var quest = Enumerable.Repeat((byte)0xFF, 50).ToArray();
        quest[0] = first;
        return quest;
    }

    [Test]
    public void The_scroll_drops_for_a_dark_knight_who_accepted_the_first_quest()
    {
        // 0xFD: quest 0 = 1 (accepted), quest 1 = 3 (not started) -- the byte of a real character in that state.
        Assert.That(QuestObjectiveTable.CheckRequisite(ScrollOfEmperor, Quests(0xFD), playerClass: 1, changeUp: 0), Is.True);
    }

    [Test]
    public void Nothing_drops_before_the_quest_is_accepted_or_after_it_is_finished()
    {
        Assert.That(QuestObjectiveTable.CheckRequisite(ScrollOfEmperor, Quests(0xFF), 1, 0), Is.False, "not started");
        Assert.That(QuestObjectiveTable.CheckRequisite(ScrollOfEmperor, Quests(0xFE), 1, 0), Is.False, "finished");
    }

    [Test]
    public void The_second_quest_item_is_per_class()
    {
        // quest 0 finished (2), quest 1 accepted (1): 0b1111_0110
        var quests = Quests(0xF6);

        Assert.That(QuestObjectiveTable.CheckRequisite(BrokenSword, quests, playerClass: 1, changeUp: 0), Is.True, "Dark Knight");
        Assert.That(QuestObjectiveTable.CheckRequisite(BrokenSword, quests, playerClass: 0, changeUp: 0), Is.False, "Dark Wizard gets its own item");
    }
}

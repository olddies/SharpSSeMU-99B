namespace MuServer.AdminPanel.Repositories;

/// <summary>A Data/ table editable with the generic editor: where it is, what it is called and what columns it
/// has.</summary>
public sealed record FlatTableDefinition(
    string Id,
    string Title,
    string Description,
    string[] PathParts,
    FlatColumn[] Columns);

/// <summary>Schemas of the flat Data/ tables. Each one is copied from the column comment of the file itself and
/// verified against the GameServer loader that reads it.</summary>
public static class FlatTableCatalog
{
    private static FlatColumn Num(string title, int width = 12) => new(title, FlatColumnKind.Number, width);
    private static FlatColumn Text(string title, int width = 38) => new(title, FlatColumnKind.QuotedText, width);

    /// <summary>The 5 per-class usage columns that close several of these tables. The width is not the same in
    /// all files (SkillList separates them more than the quest ones do), so it is passed in.</summary>
    private static FlatColumn[] ClassColumns(int width) =>
    [
        Num("DW", width), Num("DK", width), Num("FE", width), Num("MG", width), Num("DL", width),
    ];

    private static FlatColumn[] With(params object[] parts)
    {
        var list = new List<FlatColumn>();
        foreach (var part in parts)
        {
            if (part is FlatColumn column)
            {
                list.Add(column);
            }
            else if (part is FlatColumn[] many)
            {
                list.AddRange(many);
            }
        }
        return [.. list];
    }

    public static readonly FlatTableDefinition[] Tables =
    [
        new("skills", "Skills", "Data/Skill/SkillList.txt -- damage, mana, range and requirements of each skill.",
            ["Skill", "SkillList.txt"],
            With(
                Num("Index", 10), Text("Name", 37), Num("Damage", 9), Num("Mana", 8), Num("BP", 8),
                Num("Range", 8), Num("Radius", 8), Num("Delay", 8), Num("Type", 7), Num("Effect", 9),
                Num("Req. level", 11), Num("Req. energy", 12), Num("Req. command", 16),
                Num("Req. kills", 15), Num("Req. guild status", 17), ClassColumns(8))),

        new("skill-damage", "Skill damage", "Data/Skill/SkillDamage.txt -- damage multiplier per skill (empty by default).",
            ["Skill", "SkillDamage.txt"],
            With(Num("Skill", 15), Num("% damage", 12))),

        new("gates", "Gates", "Data/Move/Gate.txt -- the zones that teleport, and which gate they lead to. Used by warps and portals between maps.",
            ["Move", "Gate.txt"],
            With(
                Num("Index", 12), Num("Type", 11), Num("Map", 12),
                Num("Start X", 14), Num("Start Y", 14), Num("End X", 12), Num("End Y", 12),
                Num("Target gate", 13), Num("Direction", 12),
                Num("Min. level", 11), Num("Max. level", 11), Num("Min. reset", 11), Num("Max. reset", 11))),

        new("messages", "Messages", "Data/Message.txt -- the texts the server sends. The %d and %s are values the server fills in: leave them where they are.",
            ["Message.txt"],
            With(Num("Index", 8), Text("Message", 70))),

        new("quests", "Quests", "Data/Quest/Quest.txt -- which quest each NPC gives and what is needed to take it.",
            ["Quest", "Quest.txt"],
            With(
                Num("Index", 10), Num("NPC", 15), Num("State", 15),
                Num("Req. quest", 15), Num("Req. state", 15),
                Num("Min. level", 18), Num("Max. level", 18), ClassColumns(5))),

        new("quest-objectives", "Quest objectives", "Data/Quest/QuestObjective.txt -- what has to be collected or killed in each quest.",
            ["Quest", "QuestObjective.txt"],
            With(
                Num("Order", 9), Num("Type", 7), Num("Index", 8), Num("Quantity", 11), Num("Level", 8),
                Num("Option 1", 10), Num("Option 2", 10), Num("Option 3", 10), Num("New option", 12),
                Num("Map", 12), Num("Min. drop level", 15), Num("Max. drop level", 15), Num("% drop", 15),
                Num("Req. quest", 15), Num("Req. state", 15), ClassColumns(5))),

        new("quest-rewards", "Quest rewards", "Data/Quest/QuestReward.txt -- what each quest gives on completion.",
            ["Quest", "QuestReward.txt"],
            With(
                Num("Order", 9), Num("Type", 7), Num("Index", 8), Num("Quantity", 11), Num("Level", 8),
                Num("Option 1", 10), Num("Option 2", 10), Num("Option 3", 10), Num("New option", 12),
                Num("Req. quest", 15), Num("Req. state", 15), ClassColumns(5))),
    ];

    public static FlatTableDefinition? ById(string id) => Tables.FirstOrDefault(t => t.Id == id);
}

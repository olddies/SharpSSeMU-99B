namespace MuServer.AdminPanel.Repositories;

/// <summary>An editable success rate, one per AccountLevel (AL0-3) -- same layout as
/// <c>MuServer.GameServer.Config.GameServerInfoChaosMix</c>, mutable for the panel.</summary>
public sealed class RateSet
{
    public int Al0 { get; set; }
    public int Al1 { get; set; }
    public int Al2 { get; set; }
    public int Al3 { get; set; }
}

/// <summary>Grouping metadata for the Chaos Machine page -- it reflects the "==========" blocks that
/// Data/GameServerInfo - ChaosMix.dat already carries, so the UI does not reorder anything relative to the real
/// file.</summary>
public static class ChaosMixLayout
{
    public sealed record SingleGroup(string Title, string Prefix);
    public sealed record NumberedGroup(string Title, string Prefix, int Count);
    public sealed record PlusLevelGroup(string Title, string CommonPrefix, string ExcSetPrefix);

    public static readonly SingleGroup[] Singles =
    [
        new("Chaos Box", "ChaosItemMixRate"),
        new("Dinorant", "DinorantMixRate"),
        new("Fruit (raises/lowers stat)", "FruitMixRate"),
        new("Wings -- real Wing2Mix function (invoked by wire type \"Wing1\"/\"Wing3\")", "Wing2MixRate"),
        new("Wings -- real Wing1Mix function (invoked by wire type \"Wing2\")", "Wing1MixRate"),
        new("Pet", "PetMixRate"),
    ];

    public static readonly NumberedGroup[] Numbered =
    [
        new("Devil Square", "DevilSquareMixRate", 4),
        new("Blood Castle", "BloodCastleMixRate", 7),
    ];

    public static readonly PlusLevelGroup[] PlusLevels =
    [
        new("Upgrade +10", "PlusCommonItemLevelMixRate1", "PlusExcSetItemLevelMixRate1"),
        new("Upgrade +11", "PlusCommonItemLevelMixRate2", "PlusExcSetItemLevelMixRate2"),
        new("Upgrade +12", "PlusCommonItemLevelMixRate3", "PlusExcSetItemLevelMixRate3"),
        new("Upgrade +13", "PlusCommonItemLevelMixRate4", "PlusExcSetItemLevelMixRate4"),
    ];
}

/// <summary>Reads and writes Data/GameServerInfo - ChaosMix.dat -- the success rates that
/// <c>ChaosMixLogic.CalculateAndExecuteMix</c> actually uses (via <c>GameServerInfoChaosMix</c>) to compute the
/// success % of each Chaos Box combination.</summary>
public static class ChaosMixConfigRepository
{
    private const string Section = "GameServerInfo";

    public static (IniDocument Doc, Dictionary<string, RateSet> Rates) Load(string path)
    {
        var doc = IniDocument.Load(path);
        var rates = new Dictionary<string, RateSet>(StringComparer.OrdinalIgnoreCase);

        void LoadPrefix(string prefix)
        {
            rates[prefix] = new RateSet
            {
                Al0 = doc.GetInt(Section, prefix + "_AL0"),
                Al1 = doc.GetInt(Section, prefix + "_AL1"),
                Al2 = doc.GetInt(Section, prefix + "_AL2"),
                Al3 = doc.GetInt(Section, prefix + "_AL3"),
            };
        }

        foreach (var g in ChaosMixLayout.Singles)
        {
            LoadPrefix(g.Prefix);
        }

        foreach (var g in ChaosMixLayout.Numbered)
        {
            for (int n = 1; n <= g.Count; n++)
            {
                LoadPrefix(g.Prefix + n);
            }
        }

        foreach (var g in ChaosMixLayout.PlusLevels)
        {
            LoadPrefix(g.CommonPrefix);
            LoadPrefix(g.ExcSetPrefix);
        }

        return (doc, rates);
    }

    public static void Save(string path, IniDocument doc, Dictionary<string, RateSet> rates)
    {
        foreach (var (prefix, rate) in rates)
        {
            doc.SetInt(Section, prefix + "_AL0", rate.Al0);
            doc.SetInt(Section, prefix + "_AL1", rate.Al1);
            doc.SetInt(Section, prefix + "_AL2", rate.Al2);
            doc.SetInt(Section, prefix + "_AL3", rate.Al3);
        }

        doc.Save(path);
    }
}

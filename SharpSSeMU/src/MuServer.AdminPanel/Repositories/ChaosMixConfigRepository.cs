namespace MuServer.AdminPanel.Repositories;

/// <summary>Una tasa de éxito editable, una por AccountLevel (AL0-3) -- mismo layout que
/// <c>MuServer.GameServer.Config.GameServerInfoChaosMix</c>, mutable para el panel.</summary>
public sealed class RateSet
{
    public int Al0 { get; set; }
    public int Al1 { get; set; }
    public int Al2 { get; set; }
    public int Al3 { get; set; }
}

/// <summary>Metadata de agrupamiento para la página de Chaos Machine -- refleja los bloques
/// "==========" que ya trae Data/GameServerInfo - ChaosMix.dat, así la UI no reordena nada respecto
/// al archivo real.</summary>
public static class ChaosMixLayout
{
    public sealed record SingleGroup(string Title, string Prefix);
    public sealed record NumberedGroup(string Title, string Prefix, int Count);
    public sealed record PlusLevelGroup(string Title, string CommonPrefix, string ExcSetPrefix);

    public static readonly SingleGroup[] Singles =
    [
        new("Caja del Caos (Chaos Box)", "ChaosItemMixRate"),
        new("Dinorant", "DinorantMixRate"),
        new("Fruta (sube/baja stat)", "FruitMixRate"),
        new("Alas -- función real Wing2Mix (invocada por el tipo de wire \"Wing1\"/\"Wing3\")", "Wing2MixRate"),
        new("Alas -- función real Wing1Mix (invocada por el tipo de wire \"Wing2\")", "Wing1MixRate"),
        new("Mascota", "PetMixRate"),
    ];

    public static readonly NumberedGroup[] Numbered =
    [
        new("Cuadrado del Diablo", "DevilSquareMixRate", 4),
        new("Castillo de Sangre", "BloodCastleMixRate", 7),
    ];

    public static readonly PlusLevelGroup[] PlusLevels =
    [
        new("Mejora +10", "PlusCommonItemLevelMixRate1", "PlusExcSetItemLevelMixRate1"),
        new("Mejora +11", "PlusCommonItemLevelMixRate2", "PlusExcSetItemLevelMixRate2"),
        new("Mejora +12", "PlusCommonItemLevelMixRate3", "PlusExcSetItemLevelMixRate3"),
        new("Mejora +13", "PlusCommonItemLevelMixRate4", "PlusExcSetItemLevelMixRate4"),
    ];
}

/// <summary>Lee y escribe Data/GameServerInfo - ChaosMix.dat -- las tasas de éxito que
/// <c>ChaosMixLogic.CalculateAndExecuteMix</c> usa de verdad (vía <c>GameServerInfoChaosMix</c>) para
/// calcular el % de éxito de cada combinación de la Caja del Caos.</summary>
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

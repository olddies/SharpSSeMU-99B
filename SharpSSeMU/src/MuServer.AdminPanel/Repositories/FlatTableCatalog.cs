namespace MuServer.AdminPanel.Repositories;

/// <summary>Una tabla de Data/ editable con el editor genérico: dónde está, cómo se llama y qué
/// columnas tiene.</summary>
public sealed record FlatTableDefinition(
    string Id,
    string Title,
    string Description,
    string[] PathParts,
    FlatColumn[] Columns);

/// <summary>Esquemas de las tablas planas de Data/. Cada uno está copiado del comentario de columnas
/// del propio archivo y verificado contra el loader del GameServer que lo lee.</summary>
public static class FlatTableCatalog
{
    private static FlatColumn Num(string title, int width = 12) => new(title, FlatColumnKind.Number, width);
    private static FlatColumn Text(string title, int width = 38) => new(title, FlatColumnKind.QuotedText, width);

    /// <summary>Las 5 columnas de uso por clase que cierran varias de estas tablas. El ancho no es el
    /// mismo en todos los archivos (SkillList las separa más que los de quest), así que se pasa.</summary>
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
        new("skills", "Skills", "Data/Skill/SkillList.txt -- daño, maná, alcance y requisitos de cada habilidad.",
            ["Skill", "SkillList.txt"],
            With(
                Num("Índice", 10), Text("Nombre", 37), Num("Daño", 9), Num("Maná", 8), Num("BP", 8),
                Num("Alcance", 8), Num("Radio", 8), Num("Demora", 8), Num("Tipo", 7), Num("Efecto", 9),
                Num("Nivel req.", 11), Num("Energía req.", 12), Num("Mando req.", 16),
                Num("Muertes req.", 15), Num("Estado guild req.", 17), ClassColumns(8))),

        new("skill-damage", "Daño de skills", "Data/Skill/SkillDamage.txt -- multiplicador de daño por skill (viene vacío de fábrica).",
            ["Skill", "SkillDamage.txt"],
            With(Num("Skill", 15), Num("% de daño", 12))),

        new("gates", "Puertas", "Data/Move/Gate.txt -- las zonas que teletransportan, y a qué puerta llevan. Es lo que usan los teletransportes y los portales entre mapas.",
            ["Move", "Gate.txt"],
            With(
                Num("Índice", 12), Num("Tipo", 11), Num("Mapa", 12),
                Num("X inicio", 14), Num("Y inicio", 14), Num("X fin", 12), Num("Y fin", 12),
                Num("Puerta destino", 13), Num("Dirección", 12),
                Num("Nivel mín.", 11), Num("Nivel máx.", 11), Num("Reset mín.", 11), Num("Reset máx.", 11))),

        new("messages", "Mensajes", "Data/Message.txt -- los textos que manda el servidor. Los %d y %s son valores que el servidor rellena: hay que dejarlos donde están.",
            ["Message.txt"],
            With(Num("Índice", 8), Text("Mensaje", 70))),

        new("quests", "Quests", "Data/Quest/Quest.txt -- qué quest da cada NPC y qué hace falta para tomarla.",
            ["Quest", "Quest.txt"],
            With(
                Num("Índice", 10), Num("NPC", 15), Num("Estado", 15),
                Num("Quest req.", 15), Num("Estado req.", 15),
                Num("Nivel mín.", 18), Num("Nivel máx.", 18), ClassColumns(5))),

        new("quest-objectives", "Objetivos de quest", "Data/Quest/QuestObjective.txt -- qué hay que juntar o matar en cada quest.",
            ["Quest", "QuestObjective.txt"],
            With(
                Num("Orden", 9), Num("Tipo", 7), Num("Índice", 8), Num("Cantidad", 11), Num("Nivel", 8),
                Num("Opción 1", 10), Num("Opción 2", 10), Num("Opción 3", 10), Num("Opción nueva", 12),
                Num("Mapa", 12), Num("Nivel drop mín.", 15), Num("Nivel drop máx.", 15), Num("% drop", 15),
                Num("Quest req.", 15), Num("Estado req.", 15), ClassColumns(5))),

        new("quest-rewards", "Recompensas de quest", "Data/Quest/QuestReward.txt -- qué da cada quest al completarse.",
            ["Quest", "QuestReward.txt"],
            With(
                Num("Orden", 9), Num("Tipo", 7), Num("Índice", 8), Num("Cantidad", 11), Num("Nivel", 8),
                Num("Opción 1", 10), Num("Opción 2", 10), Num("Opción 3", 10), Num("Opción nueva", 12),
                Num("Quest req.", 15), Num("Estado req.", 15), ClassColumns(5))),
    ];

    public static FlatTableDefinition? ById(string id) => Tables.FirstOrDefault(t => t.Id == id);
}

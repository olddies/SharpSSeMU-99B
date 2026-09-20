using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una fila de Data/Item/ItemValue.txt: el precio explícito de un item, que pisa el que
/// saldría de la fórmula general de precios. <see cref="Level"/> y <see cref="Grade"/> valen null
/// cuando el archivo trae "*" ("cualquiera").</summary>
public sealed class ItemValueRow
{
    public required int Section { get; set; }
    public required int Sub { get; set; }
    public int? Level { get; set; }
    public int? Grade { get; set; }
    public int MoneyValue { get; set; }
    public string Comment { get; set; } = string.Empty;

    public int Index => Sub + Section * 512;
}

/// <summary>Lee y escribe Data/Item/ItemValue.txt. Es la tabla que hace que el Jewel of Bless valga
/// 9.000.000 y no lo que daría la fórmula general -- si un item no está acá, su precio sale de esa
/// fórmula.</summary>
public static class ItemValueFileRepository
{
    public static (string Header, List<ItemValueRow> Rows) Load(string path)
    {
        var header = new StringBuilder();
        var rows = new List<ItemValueRow>();
        bool inHeader = true;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || (trimmed.StartsWith("//") && inHeader))
            {
                if (inHeader)
                {
                    header.AppendLine(line);
                }
                continue;
            }

            if (trimmed == "end")
            {
                break;
            }

            if (trimmed.StartsWith("//"))
            {
                continue;
            }

            inHeader = false;

            string comment = string.Empty;
            var commentAt = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentAt >= 0)
            {
                comment = trimmed[(commentAt + 2)..].Trim();
                trimmed = trimmed[..commentAt].TrimEnd();
            }

            // La primera columna viene como "seccion,sub".
            var t = trimmed.Replace(',', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int Num(int i) => i < t.Length && int.TryParse(t[i], out var n) ? n : 0;
            int? Opt(int i) => i < t.Length && t[i] != "*" && int.TryParse(t[i], out var n) ? n : null;

            rows.Add(new ItemValueRow
            {
                Section = Num(0),
                Sub = Num(1),
                Level = Opt(2),
                Grade = Opt(3),
                MoneyValue = Num(4),
                Comment = comment,
            });
        }

        return (header.ToString(), rows);
    }

    public static void Save(string path, string header, IEnumerable<ItemValueRow> rows)
    {
        static string Col(object? value, int width) =>
            (value is null ? "*" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "*").PadRight(width);

        var sb = new StringBuilder(header);

        foreach (var row in rows)
        {
            sb.Append("   ");
            sb.Append($"{row.Section:00},{row.Sub:000}".PadRight(12));
            sb.Append(Col(row.Level, 12));
            sb.Append(Col(row.Grade, 12));
            sb.Append(Col(row.MoneyValue, 15));

            if (row.Comment.Length > 0)
            {
                sb.Append("//" + row.Comment);
            }
            else
            {
                while (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("end");

        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A shop NPC of Data/ShopManager.txt: where it stands and which Data/Shop/ file has its item
/// list.</summary>
public sealed class ShopNpcRow
{
    public required int NpcIndex { get; set; }
    public required int MapNumber { get; set; }
    public int LocationX { get; set; }
    public int LocationY { get; set; }
    public int Direction { get; set; }
    public int Al0 { get; set; }
    public int Al1 { get; set; }
    public int Al2 { get; set; }
    public int Al3 { get; set; }

    /// <summary>'*' in the file = no GM restriction; stored as null.</summary>
    public int? GmLevel { get; set; }

    public required string ShopPath { get; set; }
}

/// <summary>An item for sale, from Data/Shop/&lt;name&gt;.txt. The index goes in the file as "section,sub"
/// (e.g. <c>14,013</c> = Jewel of Bless), which is how <c>ShopItemTable</c> reads it.</summary>
public sealed class ShopItemRow
{
    public required int Section { get; set; }
    public required int Sub { get; set; }
    public int Level { get; set; }
    public int Durability { get; set; }
    public int Skill { get; set; }
    public int Luck { get; set; }
    public int Additional { get; set; }
    public int Excellent { get; set; }
    public int SetOption { get; set; }

    /// <summary>The file's end-of-line comment (normally the item's name). It is kept as is so as not to lose
    /// it on save.</summary>
    public string Comment { get; set; } = string.Empty;
}

/// <summary>Reads and writes Data/ShopManager.txt and the Data/Shop/*.txt files, keeping each file's header
/// comment block.</summary>
public static class ShopFileRepository
{
    public static (string Header, List<ShopNpcRow> Rows) LoadManager(string path)
    {
        var header = new StringBuilder();
        var rows = new List<ShopNpcRow>();
        bool inHeader = true;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//") || trimmed.Length == 0)
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

            inHeader = false;
            var t = Tokenize(trimmed);
            int Num(int i) => i < t.Count && int.TryParse(t[i], out var n) ? n : 0;

            rows.Add(new ShopNpcRow
            {
                NpcIndex = Num(0),
                MapNumber = Num(1),
                LocationX = Num(2),
                LocationY = Num(3),
                Direction = Num(4),
                Al0 = Num(5),
                Al1 = Num(6),
                Al2 = Num(7),
                Al3 = Num(8),
                GmLevel = t.Count > 9 && t[9] != "*" && int.TryParse(t[9], out var gm) ? gm : null,
                ShopPath = t.Count > 10 ? t[10] : string.Empty,
            });
        }

        return (header.ToString(), rows);
    }

    public static void SaveManager(string path, string header, IEnumerable<ShopNpcRow> rows)
    {
        static string Col(object? value, int width) =>
            (value is null ? "*" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "*").PadRight(width);

        var sb = new StringBuilder(header);

        foreach (var row in rows.OrderBy(r => r.NpcIndex))
        {
            sb.Append("   ");
            sb.Append(Col(row.NpcIndex, 11));
            sb.Append(Col(row.MapNumber, 12));
            // X/Y go with 3 digits in the original file ("062"), that format is respected.
            sb.Append(Col(row.LocationX.ToString("000", CultureInfo.InvariantCulture), 12));
            sb.Append(Col(row.LocationY.ToString("000", CultureInfo.InvariantCulture), 12));
            sb.Append(Col(row.Direction, 12));
            sb.Append(Col(row.Al0, 6));
            sb.Append(Col(row.Al1, 6));
            sb.Append(Col(row.Al2, 6));
            sb.Append(Col(row.Al3, 6));
            sb.Append(Col(row.GmLevel, 10));
            sb.Append('"' + row.ShopPath + '"');
            sb.AppendLine();
        }

        sb.AppendLine("end");
        WriteAtomic(path, sb.ToString());
    }

    public static (string Header, List<ShopItemRow> Rows) LoadShop(string path)
    {
        var header = new StringBuilder();
        var rows = new List<ShopItemRow>();
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

            // The end-of-line comment ("//Apple") is the item's name: it is split off before tokenising so as
            // not to confuse it with a column.
            string comment = string.Empty;
            var commentAt = trimmed.IndexOf("//", StringComparison.Ordinal);
            if (commentAt >= 0)
            {
                comment = trimmed[(commentAt + 2)..].Trim();
                trimmed = trimmed[..commentAt].TrimEnd();
            }

            // The first column is "section,sub": the comma is treated as a separator.
            var t = Tokenize(trimmed.Replace(',', ' '));
            int Num(int i) => i < t.Count && int.TryParse(t[i], out var n) ? n : 0;

            rows.Add(new ShopItemRow
            {
                Section = Num(0),
                Sub = Num(1),
                Level = Num(2),
                Durability = Num(3),
                Skill = Num(4),
                Luck = Num(5),
                Additional = Num(6),
                Excellent = Num(7),
                SetOption = Num(8),
                Comment = comment,
            });
        }

        return (header.ToString(), rows);
    }

    public static void SaveShop(string path, string header, IEnumerable<ShopItemRow> rows)
    {
        static string Col(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadRight(width);

        var sb = new StringBuilder(header);

        foreach (var row in rows)
        {
            sb.Append("   ");
            // Same format as the original file: section with 2 digits and sub-index with 3 ("00,010").
            sb.Append($"{row.Section:00},{row.Sub:000}".PadRight(12));
            sb.Append(Col(row.Level, 12));
            sb.Append(Col(row.Durability, 13));
            sb.Append(Col(row.Skill, 14));
            sb.Append(Col(row.Luck, 13));
            sb.Append(Col(row.Additional, 12));
            sb.Append(Col(row.Excellent, 12));
            sb.Append(Col(row.SetOption, 12));

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
        WriteAtomic(path, sb.ToString());
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, content);
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        int n = line.Length;
        int pos = 0;

        while (pos < n)
        {
            while (pos < n && char.IsWhiteSpace(line[pos]))
            {
                pos++;
            }

            if (pos >= n)
            {
                break;
            }

            if (line[pos] == '"')
            {
                int end = line.IndexOf('"', pos + 1);
                if (end < 0)
                {
                    end = n;
                }
                tokens.Add(line.Substring(pos + 1, end - pos - 1));
                pos = end + 1;
            }
            else
            {
                int start = pos;
                while (pos < n && !char.IsWhiteSpace(line[pos]))
                {
                    pos++;
                }
                tokens.Add(line.Substring(start, pos - start));
            }
        }

        return tokens;
    }
}

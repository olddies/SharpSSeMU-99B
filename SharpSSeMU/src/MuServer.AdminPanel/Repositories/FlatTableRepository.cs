using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Column type of a flat Data/ table. Almost everything is numeric; the only thing that really has to
/// be distinguished is whether the value is quoted, because then it can have spaces inside and cannot be split
/// on spaces.</summary>
public enum FlatColumnKind
{
    /// <summary>Number, or "*" (which in these files means "any"/"no limit"). It is stored as raw text so as
    /// not to lose the "*" or turn it into a -1 that would then have to be translated back.</summary>
    Number,

    /// <summary>Texto entre comillas dobles en el archivo.</summary>
    QuotedText,
}

public sealed record FlatColumn(string Title, FlatColumnKind Kind = FlatColumnKind.Number, int Width = 12);

/// <summary>A row, as a list of raw values in the same order as the schema's columns.</summary>
public sealed class FlatRow
{
    public required string[] Values { get; init; }

    public string this[int i]
    {
        get => i >= 0 && i < Values.Length ? Values[i] : string.Empty;
        set
        {
            if (i >= 0 && i < Values.Length)
            {
                Values[i] = value ?? string.Empty;
            }
        }
    }
}

/// <summary>Generic reader/writer of the flat Data/ tables (SkillList, Gate, Message, Quest, QuestObjective,
/// QuestReward...). They all share the same shape: a header comment block, one row per line with
/// space-separated columns, and "end" at the bottom. Instead of writing one repository per file --which is what
/// was done for Item.txt/MonsterList.txt, where the layout is special-- here it is enough to declare the column
/// schema.</summary> <summary>The content of a table plus what is needed to write it back the same: the header
/// comment block and the indentation the file uses for its rows (some files use 3 spaces and others start at
/// column 0; the file's own is taken instead of configuring it by hand table by table).</summary>
public sealed record FlatTable(string Header, string RowIndent, List<FlatRow> Rows);

public static class FlatTableRepository
{
    public static FlatTable Load(string path, IReadOnlyList<FlatColumn> columns)
    {
        var header = new StringBuilder();
        var rows = new List<FlatRow>();
        string rowIndent = string.Empty;
        bool inHeader = true;
        bool indentTaken = false;

        if (!File.Exists(path))
        {
            return new FlatTable(string.Empty, string.Empty, rows);
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
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

            if (!indentTaken)
            {
                rowIndent = line[..(line.Length - line.TrimStart().Length)];
                indentTaken = true;
            }

            var tokens = Tokenize(trimmed);
            var values = new string[columns.Count];

            for (int i = 0; i < columns.Count; i++)
            {
                values[i] = i < tokens.Count ? tokens[i] : string.Empty;
            }

            rows.Add(new FlatRow { Values = values });
        }

        return new FlatTable(header.ToString(), rowIndent, rows);
    }

    public static void Save(string path, FlatTable table, IReadOnlyList<FlatColumn> columns)
    {
        var sb = new StringBuilder(table.Header);

        foreach (var row in table.Rows)
        {
            sb.Append(table.RowIndent);

            for (int i = 0; i < columns.Count; i++)
            {
                var value = row[i];
                var text = columns[i].Kind == FlatColumnKind.QuotedText ? '"' + value + '"' : value;
                sb.Append(text.PadRight(columns[i].Width));
            }

            while (sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.AppendLine();
        }

        sb.AppendLine("end");

        // Atomic write -- the GameServer reads these files at start-up.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
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

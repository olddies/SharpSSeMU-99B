using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A numbered section of a Data/Event file: its number, the comment block that heads it (which is kept
/// as is) and its rows.</summary>
public sealed class TableSection
{
    public required int Number { get; init; }

    /// <summary>El bloque de comentarios completo, verbatim, para reescribirlo igual.</summary>
    public required string Comment { get; init; }

    /// <summary>Nombres de columna deducidos del propio comentario del archivo.</summary>
    public required string[] ColumnTitles { get; init; }

    /// <summary>Width of each column, also deduced from the comment, so that the file comes out with the same
    /// alignment it went in with.</summary>
    public required int[] ColumnWidths { get; init; }

    public List<FlatRow> Rows { get; } = new();
}

/// <summary>Reads and writes the Data/Event files (DevilSquare.dat, BloodCastle.dat, Kalima.dat...), which are
/// several numbered tables inside the same file. <para>Unlike the other tables, here <b>there is no need to
/// declare the schema</b>: each section carries its own column names in the comment above ("// WarningTime
/// NotifyTime EventTime CloseTime"), so they are read from there. That makes the editor work for the 9 event
/// files without writing a line per file, and keep working if one changes its columns.</para> </summary>
public static class SectionedTableRepository
{
    public static IReadOnlyList<string> ListFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.dat")
                .Select(System.IO.Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public static (string Header, List<TableSection> Sections) Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new StringBuilder();
        var sections = new List<TableSection>();
        TableSection? current = null;
        bool inHeader = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                if (inHeader)
                {
                    header.AppendLine(line);
                }
                continue;
            }

            if (trimmed.StartsWith("//"))
            {
                if (inHeader)
                {
                    header.AppendLine(line);
                }
                continue;
            }

            if (trimmed == "end")
            {
                current = null;
                continue;
            }

            var tokens = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

            if (current is null && tokens.Length == 1 && int.TryParse(tokens[0], out var sectionNumber))
            {
                inHeader = false;

                // The comment block that follows the number describes the columns of this section.
                var comment = new StringBuilder();
                var commentLines = new List<string>();
                for (int j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("//"); j++)
                {
                    comment.AppendLine(lines[j]);
                    commentLines.Add(lines[j]);
                }

                var (titles, widths) = ParseColumnComment(commentLines);

                current = new TableSection
                {
                    Number = sectionNumber,
                    Comment = comment.ToString(),
                    ColumnTitles = titles,
                    ColumnWidths = widths,
                };
                sections.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var values = new string[current.ColumnTitles.Length];
            for (int c = 0; c < values.Length; c++)
            {
                values[c] = c < tokens.Length ? tokens[c] : string.Empty;
            }

            current.Rows.Add(new FlatRow { Values = values });
        }

        return (header.ToString(), sections);
    }

    /// <summary>From a section's comment lines, picks the one carrying the column names (the one that is not a
    /// row of dashes nor the ranges one like "[0~50000000]") and takes from it both the names and the width of
    /// each column, according to where each word starts.</summary>
    private static (string[] Titles, int[] Widths) ParseColumnComment(List<string> commentLines)
    {
        foreach (var raw in Enumerable.Reverse(commentLines))
        {
            var body = raw.TrimStart();
            body = body.StartsWith("//") ? body[2..] : body;

            var matches = System.Text.RegularExpressions.Regex.Matches(body, @"\S+");

            if (matches.Count == 0)
            {
                continue;
            }

            // Discards separators ("-----") and the ranges line ("[0~59]").
            bool isSeparator = matches.All(m => m.Value.Trim('-').Length == 0);
            bool isRangeLine = matches.All(m => m.Value.StartsWith('['));

            if (isSeparator || isRangeLine)
            {
                continue;
            }

            var titles = matches.Select(m => m.Value).ToArray();
            var widths = new int[titles.Length];

            for (int i = 0; i < titles.Length; i++)
            {
                widths[i] = i < titles.Length - 1
                    ? matches[i + 1].Index - matches[i].Index
                    : titles[i].Length;
            }

            return (titles, widths);
        }

        return ([], []);
    }

    public static void Save(string path, string header, IEnumerable<TableSection> sections)
    {
        var sb = new StringBuilder(header);

        foreach (var section in sections)
        {
            sb.AppendLine(section.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(section.Comment);

            foreach (var row in section.Rows)
            {
                sb.Append("   ");

                for (int i = 0; i < section.ColumnTitles.Length; i++)
                {
                    sb.Append(row[i].PadRight(i < section.ColumnWidths.Length ? section.ColumnWidths[i] : 12));
                }

                while (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }

                sb.AppendLine();
            }

            sb.AppendLine("end");
            sb.AppendLine();
        }

        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

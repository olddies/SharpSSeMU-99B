using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A row of a Data/Monster/Spawn/*.txt. The column layout depends on the block "type" it is in (the
/// loose number that opens each section of the file), verified against <c>MonsterSpawnTable.Load</c>: <list
/// type="bullet"> <item>0 -- fixed position: radius, X, Y, direction.</item> <item>1 -- area: radius, X, Y, X2,
/// Y2, direction and how many monsters to place inside.</item> <item>2 -- same as 0, but the server adds a
/// random offset of ±3 on load.</item> <item>4 -- same columns as 0, but NOT spawned at start-up: they are the
/// candidate positions that events (Devil Square, Blood Castle) use to place their monsters at runtime.</item>
/// </list> Direction <c>-1</c> is the file's "*": the server picks one at random.</summary>
public sealed class SpawnRow
{
    public required int SpawnType { get; set; }
    public required int MonsterClass { get; set; }
    public int Distance { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public int Direction { get; set; }
    public int Count { get; set; }

    public bool IsArea => SpawnType == 1;
}

public sealed class SpawnBlock
{
    public required int SpawnType { get; init; }
    public string HeaderComment { get; init; } = string.Empty;
    public List<SpawnRow> Rows { get; } = new();

    /// <summary>Only type 1 uses the X2/Y2/Count columns.</summary>
    public bool IsArea => SpawnType == 1;
}

/// <summary>Reads and writes the Data/Monster/Spawn/*.txt files (where each monster appears on each map). It
/// keeps the header comment block and the column comment of each section.</summary>
public static class SpawnFileRepository
{
    public static IReadOnlyList<string> ListFiles(string spawnDirectory) =>
        Directory.Exists(spawnDirectory)
            ? Directory.GetFiles(spawnDirectory, "*.txt")
                .Select(System.IO.Path.GetFileName)
                .Where(f => f is not null)
                .Select(f => f!)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public static (string Header, List<SpawnBlock> Blocks) Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new StringBuilder();
        var blocks = new List<SpawnBlock>();
        SpawnBlock? current = null;
        bool inHeader = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                // Blank lines of the header are kept so that the file comes out with the same spacing it had.
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

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // A number alone on its line opens a new block and says what type its rows are.
            if (current is null && tokens.Length == 1 && int.TryParse(tokens[0], out var blockType))
            {
                inHeader = false;

                // The column comment comes right after the number; it is saved to be rewritten the same.
                var comment = new StringBuilder();
                for (int j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("//"); j++)
                {
                    // No TrimEnd: the line is saved as is, trailing spaces included.
                    comment.AppendLine(lines[j]);
                }

                current = new SpawnBlock { SpawnType = blockType, HeaderComment = comment.ToString() };
                blocks.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            int Num(int idx) => idx < tokens.Length
                ? (tokens[idx] == "*" ? -1 : int.TryParse(tokens[idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                : 0;

            var row = new SpawnRow
            {
                SpawnType = current.SpawnType,
                MonsterClass = Num(0),
                Distance = Num(1),
                X = Num(2),
                Y = Num(3),
            };

            if (current.SpawnType == 1)
            {
                row.X2 = Num(4);
                row.Y2 = Num(5);
                row.Direction = Num(6);
                row.Count = Num(7);
            }
            else
            {
                row.Direction = Num(4);
            }

            current.Rows.Add(row);
        }

        return (header.ToString(), blocks);
    }

    public static void Save(string path, string header, IEnumerable<SpawnBlock> blocks)
    {
        static string Col(int value, int width) =>
            (value == -1 ? "*" : value.ToString(CultureInfo.InvariantCulture)).PadRight(width);

        var sb = new StringBuilder(header);

        foreach (var block in blocks)
        {
            sb.AppendLine(block.SpawnType.ToString(CultureInfo.InvariantCulture));

            if (block.HeaderComment.Length > 0)
            {
                sb.Append(block.HeaderComment);
            }

            foreach (var row in block.Rows)
            {
                sb.Append("   ");
                sb.Append(Col(row.MonsterClass, 15));
                sb.Append(Col(row.Distance, 14));

                if (block.SpawnType == 1)
                {
                    // Area blocks use wider columns in the original file.
                    sb.Append(Col(row.X, 14));
                    sb.Append(Col(row.Y, 14));
                    sb.Append(Col(row.X2, 12));
                    sb.Append(Col(row.Y2, 12));
                    sb.Append(Col(row.Direction, 12));
                    sb.Append(Col(row.Count, 8));
                }
                else
                {
                    sb.Append(Col(row.X, 12));
                    sb.Append(Col(row.Y, 12));
                    sb.Append(Col(row.Direction, 10));
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

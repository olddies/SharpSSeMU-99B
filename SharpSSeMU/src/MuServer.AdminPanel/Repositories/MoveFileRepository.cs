using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A complete row of Data/Move/Move.txt -- unlike <c>MoveEntry</c> (the GameServer's in-memory model,
/// which discards AL0-3 because nothing uses them yet), this one keeps the 4 account-level columns as they are,
/// so that editing and saving from here does not wipe them. <c>null</c> in
/// MinLevel/MaxLevel/MinReset/MaxReset/AL0-3 is the file's "*" (no restriction).</summary>
public sealed class MoveRow
{
    public required int Index { get; set; }
    public required string Name { get; set; }
    public required long RequireMoney { get; set; }
    public int? MinLevel { get; set; }
    public int? MaxLevel { get; set; }
    public int? MinReset { get; set; }
    public int? MaxReset { get; set; }
    public int? Al0 { get; set; }
    public int? Al1 { get; set; }
    public int? Al2 { get; set; }
    public int? Al3 { get; set; }
    public required int GateNumber { get; set; }
}

/// <summary>Reads and writes Data/Move/Move.txt end to end -- it does not reuse the GameServer's
/// <c>MoveTable</c> because that is a read-only reader, meant to load once at start-up; this panel needs a
/// byte-for-byte faithful round trip of the columns that exist in the file, AL0-3 included.</summary>
public static class MoveFileRepository
{
    public static (string Header, List<MoveRow> Rows) Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new StringBuilder();
        var rows = new List<MoveRow>();
        bool inHeader = true;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("//") || string.IsNullOrWhiteSpace(trimmed))
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
            rows.Add(ParseRow(trimmed));
        }

        return (header.ToString(), rows);
    }

    private static MoveRow ParseRow(string line)
    {
        // The name may have spaces if it came quoted (not the case in the real file, but it is supported just
        // in case) or not -- Move.txt writes it without quotes and without spaces ("Lorencia", not "New
        // Lorencia"), so a simple split is enough.
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int Int(int i) => int.Parse(tokens[i], CultureInfo.InvariantCulture);
        long Long(int i) => long.Parse(tokens[i], CultureInfo.InvariantCulture);
        int? OptInt(int i) => tokens[i] == "*" ? null : int.Parse(tokens[i], CultureInfo.InvariantCulture);

        return new MoveRow
        {
            Index = Int(0),
            Name = tokens[1].Trim('"'),
            RequireMoney = Long(2),
            MinLevel = OptInt(3),
            MaxLevel = OptInt(4),
            MinReset = OptInt(5),
            MaxReset = OptInt(6),
            Al0 = OptInt(7),
            Al1 = OptInt(8),
            Al2 = OptInt(9),
            Al3 = OptInt(10),
            GateNumber = Int(11),
        };
    }

    public static void Save(string path, string header, IEnumerable<MoveRow> rows)
    {
        static string Col(object? value, int width)
        {
            var text = value switch
            {
                null => "*",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "*",
            };
            return text.PadRight(width);
        }

        var sb = new StringBuilder(header);
        foreach (var row in rows.OrderBy(r => r.Index))
        {
            sb.Append(Col(row.Index, 8));
            sb.Append(Col(row.Name, 24));
            sb.Append(Col(row.RequireMoney, 16));
            sb.Append(Col(row.MinLevel, 11));
            sb.Append(Col(row.MaxLevel, 11));
            sb.Append(Col(row.MinReset, 11));
            sb.Append(Col(row.MaxReset, 11));
            sb.Append(Col(row.Al0, 6));
            sb.Append(Col(row.Al1, 6));
            sb.Append(Col(row.Al2, 6));
            sb.Append(Col(row.Al3, 6));
            sb.Append(Col(row.GateNumber, 4));
            sb.AppendLine();
        }
        sb.AppendLine("end");

        // Atomic write (temp + move) so as not to leave the file half-written if something fails -- this file
        // is read by the GameServer at start-up, and a broken Move.txt takes it down.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

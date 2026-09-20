using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>An editable key of an .ini. <see cref="Value"/> is what the panel modifies; <see
/// cref="OriginalValue"/> is what the file said when loaded, and is used to rewrite ONLY the lines that really
/// changed (those not touched stay byte for byte identical, with their original spacing and tabs).</summary>
public sealed class IniEntry
{
    public required string Key { get; init; }
    public required int LineIndex { get; init; }
    public required string OriginalValue { get; init; }
    public required string Value { get; set; }

    /// <summary>The GameServerInfo .dat files are almost all numeric, but Common.dat also carries text
    /// (ServerName, ServerSerial, IP addresses) -- the UI decides the field type from this.</summary>
    public bool IsNumeric => int.TryParse(OriginalValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    public bool IsDirty => Value != OriginalValue;
}

/// <summary>A block of keys, grouped by the comment banner (";===" / "; Title" / ";===") that these files
/// already carry -- so the panel shows the same groups as the file, without having to maintain a hand-written
/// list of the ~845 fields.</summary>
public sealed class IniGroup
{
    public required string Title { get; init; }
    public List<IniEntry> Entries { get; } = new();
}

/// <summary>Reads and writes .ini files in the GetPrivateProfileInt style (same format as
/// <c>MuServer.Shared.Config.IniFile</c>, which the GameServer uses to load GameServerInfo - *.dat) but, unlike
/// that read-only reader, keeps the file line by line -- comments, "====" groupings and order -- so that only
/// the value of the keys the panel edits is written back, without reordering or losing the comments that
/// document each configuration block.</summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;

    // key "section\key" (case-insensitive) -> line index in _lines.
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);

    private IniDocument(List<string> lines)
    {
        _lines = lines;
    }

    /// <summary>Grupos en el mismo orden que aparecen en el archivo.</summary>
    public List<IniGroup> Groups { get; } = new();

    public static IniDocument Load(string path)
    {
        var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        var doc = new IniDocument(lines);

        string? currentSection = null;
        IniGroup? currentGroup = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                // Only the middle line of the banner (";  Text") gives a title; the "====" ones are ignored.
                var text = trimmed[1..].Trim();
                if (text.Length > 0 && text.Trim('=').Length > 0)
                {
                    currentGroup = new IniGroup { Title = text };
                    doc.Groups.Add(currentGroup);
                }
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (currentSection is null)
            {
                continue;
            }

            var eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = trimmed[..eq].Trim();
            // It is stored trimmed: that way the UI field does not carry the file's space/tab, and the "changed
            // or not" comparison is not confused by spacing. Keys that are not edited are never rewritten, so
            // their original line stays intact anyway.
            var value = trimmed[(eq + 1)..].Trim();
            doc._index[currentSection + "\\" + key] = i;

            if (currentGroup is null)
            {
                currentGroup = new IniGroup { Title = currentSection };
                doc.Groups.Add(currentGroup);
            }

            currentGroup.Entries.Add(new IniEntry
            {
                Key = key,
                LineIndex = i,
                OriginalValue = value,
                Value = value,
            });
        }

        // Un banner sin claves debajo (separador decorativo) no aporta nada en la UI.
        doc.Groups.RemoveAll(g => g.Entries.Count == 0);

        return doc;
    }

    public int GetInt(string section, string key, int defaultValue = 0)
    {
        if (!_index.TryGetValue(section + "\\" + key, out var lineIdx))
        {
            return defaultValue;
        }

        var line = _lines[lineIdx];
        var eq = line.IndexOf('=');
        var value = eq >= 0 ? line[(eq + 1)..].Trim() : string.Empty;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : defaultValue;
    }

    /// <summary>Replaces the value of an existing key, preserving the key and the rest of the line as is. It
    /// does not add new keys -- this panel only edits configuration that the real file already
    /// carries.</summary>
    public void SetInt(string section, string key, int value)
    {
        if (!_index.TryGetValue(section + "\\" + key, out var lineIdx))
        {
            return;
        }

        WriteLineValue(lineIdx, " " + value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Dumps the edited values of <see cref="Groups"/> into the file's lines, touching only those that
    /// changed.</summary>
    public void ApplyGroupEdits()
    {
        foreach (var entry in Groups.SelectMany(g => g.Entries).Where(e => e.IsDirty))
        {
            WriteLineValue(entry.LineIndex, " " + entry.Value.Trim());
        }
    }

    private void WriteLineValue(int lineIdx, string value)
    {
        var line = _lines[lineIdx];
        var eq = line.IndexOf('=');
        var keyPart = eq >= 0 ? line[..(eq + 1)] : line + " =";
        _lines[lineIdx] = keyPart + value;
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            sb.AppendLine(line);
        }

        // Atomic write (temp + copy) -- the GameServer reads this .dat at start-up.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

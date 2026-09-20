using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una clave editable de un .ini. <see cref="Value"/> es lo que el panel modifica;
/// <see cref="OriginalValue"/> es lo que decía el archivo al cargarlo, y sirve para reescribir SÓLO
/// las líneas que de verdad cambiaron (las que no se tocan quedan byte a byte idénticas, con su
/// espaciado y sus tabs originales).</summary>
public sealed class IniEntry
{
    public required string Key { get; init; }
    public required int LineIndex { get; init; }
    public required string OriginalValue { get; init; }
    public required string Value { get; set; }

    /// <summary>Los .dat de GameServerInfo son casi todos numéricos, pero Common.dat trae también
    /// texto (ServerName, ServerSerial, direcciones IP) -- la UI decide el tipo de campo con esto.</summary>
    public bool IsNumeric => int.TryParse(OriginalValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    public bool IsDirty => Value != OriginalValue;
}

/// <summary>Un bloque de claves, agrupado por el banner de comentarios (";===" / "; Título" / ";===")
/// que estos archivos ya traen -- así el panel muestra los mismos grupos que el archivo, sin tener
/// que mantener a mano una lista de los ~845 campos.</summary>
public sealed class IniGroup
{
    public required string Title { get; init; }
    public List<IniEntry> Entries { get; } = new();
}

/// <summary>Lee y escribe archivos .ini al estilo GetPrivateProfileInt (mismo formato que
/// <c>MuServer.Shared.Config.IniFile</c>, que el GameServer usa para cargar
/// GameServerInfo - *.dat) pero, a diferencia de ese lector de sólo lectura, conserva el archivo
/// línea por línea -- comentarios, agrupamientos con "====" y orden -- para poder escribir de vuelta
/// sólo el valor de las claves que el panel edita, sin reordenar ni perder los comentarios que
/// documentan cada bloque de configuración.</summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;

    // clave "seccion\clave" (case-insensitive) -> índice de línea en _lines.
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
                // Sólo la línea del medio del banner (";  Texto") da título; las de "====" se ignoran.
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
            // Se guarda recortado: así el campo de la UI no arrastra el espacio/tab del archivo, y la
            // comparación de "cambió o no" no se confunde por espaciado. Las claves que no se editan
            // no se reescriben nunca, así que su línea original queda intacta igual.
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

    /// <summary>Reemplaza el valor de una clave existente, preservando la clave y el resto de la
    /// línea tal cual. No agrega claves nuevas -- este panel sólo edita configuración que el archivo
    /// real ya trae.</summary>
    public void SetInt(string section, string key, int value)
    {
        if (!_index.TryGetValue(section + "\\" + key, out var lineIdx))
        {
            return;
        }

        WriteLineValue(lineIdx, " " + value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Vuelca a las líneas del archivo los valores editados de <see cref="Groups"/>, tocando
    /// sólo los que cambiaron.</summary>
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

        // Escritura atómica (temp + copy) -- el GameServer lee este .dat al arrancar.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

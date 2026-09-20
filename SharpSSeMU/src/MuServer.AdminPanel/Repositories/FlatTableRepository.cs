using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Tipo de columna de una tabla plana de Data/. Casi todo es numérico; lo único que hay que
/// distinguir de verdad es si el valor va entre comillas, porque ahí puede tener espacios adentro y
/// no se puede partir por espacios.</summary>
public enum FlatColumnKind
{
    /// <summary>Número, o "*" (que en estos archivos significa "cualquiera"/"sin límite"). Se guarda
    /// como texto crudo para no perder el "*" ni convertirlo a un -1 que después habría que traducir
    /// de vuelta.</summary>
    Number,

    /// <summary>Texto entre comillas dobles en el archivo.</summary>
    QuotedText,
}

public sealed record FlatColumn(string Title, FlatColumnKind Kind = FlatColumnKind.Number, int Width = 12);

/// <summary>Una fila, como lista de valores crudos en el mismo orden que las columnas del esquema.</summary>
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

/// <summary>Lector/escritor genérico de las tablas planas de Data/ (SkillList, Gate, Message, Quest,
/// QuestObjective, QuestReward...). Todas comparten la misma forma: un bloque de comentarios de
/// cabecera, una fila por línea con columnas separadas por espacios, y "end" al final. En vez de
/// escribir un repositorio por archivo --que fue lo que se hizo para Item.txt/MonsterList.txt, donde
/// el layout sí es especial-- acá alcanza con declarar el esquema de columnas.</summary>
/// <summary>El contenido de una tabla más lo que hace falta para volver a escribirla igual: el
/// bloque de comentarios de cabecera y la sangría con la que el archivo indenta sus filas (unos
/// archivos usan 3 espacios y otros arrancan en la columna 0; se toma la del propio archivo en vez
/// de configurarla a mano tabla por tabla).</summary>
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

        // Escritura atómica -- el GameServer lee estos archivos al arrancar.
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

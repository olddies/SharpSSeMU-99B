using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una fila de un Data/Monster/Spawn/*.txt. El layout de columnas depende del "tipo" de
/// bloque en que está (el número suelto que abre cada sección del archivo), verificado contra
/// <c>MonsterSpawnTable.Load</c>:
/// <list type="bullet">
/// <item>0 -- posición fija: radio, X, Y, dirección.</item>
/// <item>1 -- área: radio, X, Y, X2, Y2, dirección y cuántos monstruos poner adentro.</item>
/// <item>2 -- igual que 0, pero el servidor le suma un desvío aleatorio de ±3 al cargar.</item>
/// <item>4 -- mismas columnas que 0, pero NO se spawnea al arrancar: son las posiciones candidatas
/// que usan los eventos (Devil Square, Blood Castle) para poner sus monstruos en runtime.</item>
/// </list>
/// Dirección <c>-1</c> es el "*" del archivo: el servidor elige una al azar.</summary>
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

    /// <summary>Sólo el tipo 1 usa las columnas X2/Y2/Cantidad.</summary>
    public bool IsArea => SpawnType == 1;
}

/// <summary>Lee y escribe los Data/Monster/Spawn/*.txt (dónde aparece cada monstruo en cada mapa).
/// Conserva el bloque de comentarios de cabecera y el comentario de columnas de cada sección.</summary>
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
                // Las líneas en blanco del encabezado se conservan para que el archivo vuelva a
                // salir con el mismo espaciado que tenía.
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

            // Un número solo en su línea abre un bloque nuevo y dice de qué tipo son sus filas.
            if (current is null && tokens.Length == 1 && int.TryParse(tokens[0], out var blockType))
            {
                inHeader = false;

                // El comentario de columnas viene justo después del número; se guarda para reescribirlo igual.
                var comment = new StringBuilder();
                for (int j = i + 1; j < lines.Length && lines[j].TrimStart().StartsWith("//"); j++)
                {
                    // Sin TrimEnd: se guarda la línea tal cual, espacios finales incluidos.
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
                    // Los bloques de área usan columnas más anchas en el archivo original.
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

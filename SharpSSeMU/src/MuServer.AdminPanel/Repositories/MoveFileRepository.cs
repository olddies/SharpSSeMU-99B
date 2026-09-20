using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una fila de Data/Move/Move.txt, completa -- a diferencia de <c>MoveEntry</c> (el modelo
/// en memoria del GameServer, que descarta AL0-3 porque nada los usa todavía), esta guarda las 4
/// columnas de nivel de cuenta tal cual, para que editar y guardar desde acá no las borre.
/// <c>null</c> en MinLevel/MaxLevel/MinReset/MaxReset/AL0-3 es el "*" del archivo (sin restricción).</summary>
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

/// <summary>Lee y escribe Data/Move/Move.txt de punta a punta -- no reutiliza el <c>MoveTable</c> del
/// GameServer porque ese es un lector de sólo lectura, pensado para cargar una vez al arrancar; este
/// panel necesita ida y vuelta fiel byte a byte de las columnas que existen en el archivo, AL0-3
/// incluidas.</summary>
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
        // El nombre puede tener espacios si viniera entre comillas (no es el caso en el archivo
        // real, pero se soporta por si acaso) o no -- Move.txt lo escribe sin comillas y sin
        // espacios ("Lorencia", no "Nueva Lorencia"), así que un split simple alcanza.
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

        // Escritura atómica (temp + move) para no dejar el archivo a medio escribir si algo falla
        // -- este archivo lo lee el GameServer al arrancar, y un Move.txt roto lo tumba.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

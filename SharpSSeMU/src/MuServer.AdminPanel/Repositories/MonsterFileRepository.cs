using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una fila de Data/Monster/MonsterList.txt, mutable -- la versión editable de
/// <c>MuServer.GameServer.World.MonsterInfo</c> (que es de sólo lectura, con todo <c>init</c>).
/// El orden de columnas está verificado contra <c>MonsterInfoTable.Load</c>, que es lo que el
/// GameServer usa de verdad.</summary>
public sealed class MonsterRow
{
    public required int Index { get; set; }
    public required int Type { get; set; }
    public required string Name { get; set; }
    public int Level { get; set; }
    public int MaxLife { get; set; }
    public int MaxMana { get; set; }
    public int DamageMin { get; set; }
    public int DamageMax { get; set; }
    public int Defense { get; set; }
    public int MagicDefense { get; set; }
    public int AttackRate { get; set; }
    public int DefenseRate { get; set; }
    public int MoveRange { get; set; }
    public int AttackType { get; set; }
    public int AttackRange { get; set; }
    public int ViewRange { get; set; }
    public int MoveSpeed { get; set; }
    public int AttackSpeed { get; set; }
    public int RegenTime { get; set; }
    public int Attribute { get; set; }
    public int ItemRate { get; set; }
    public int MoneyRate { get; set; }
    public int MaxItemLevel { get; set; }
    public int MonsterSkill { get; set; }
    public int[] Resistance { get; set; } = new int[7];
}

/// <summary>Lee y escribe Data/Monster/MonsterList.txt entero, conservando el bloque de comentarios
/// de cabecera tal cual (incluye los créditos del emulador original y la línea de nombres de
/// columna).</summary>
public static class MonsterFileRepository
{
    public static (string Header, List<MonsterRow> Rows) Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var header = new StringBuilder();
        var rows = new List<MonsterRow>();
        bool inHeader = true;

        foreach (var raw in lines)
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
            rows.Add(ParseRow(trimmed));
        }

        return (header.ToString(), rows);
    }

    private static MonsterRow ParseRow(string line)
    {
        var t = Tokenize(line);
        int Num(int i) => i < t.Count && int.TryParse(t[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        var row = new MonsterRow
        {
            Index = Num(0),
            Type = Num(1),
            Name = t.Count > 2 ? t[2] : string.Empty,
            Level = Num(3),
            MaxLife = Num(4),
            MaxMana = Num(5),
            DamageMin = Num(6),
            DamageMax = Num(7),
            Defense = Num(8),
            MagicDefense = Num(9),
            AttackRate = Num(10),
            DefenseRate = Num(11),
            MoveRange = Num(12),
            AttackType = Num(13),
            AttackRange = Num(14),
            ViewRange = Num(15),
            MoveSpeed = Num(16),
            AttackSpeed = Num(17),
            RegenTime = Num(18),
            Attribute = Num(19),
            ItemRate = Num(20),
            MoneyRate = Num(21),
            MaxItemLevel = Num(22),
            MonsterSkill = Num(23),
        };

        for (int n = 0; n < 7; n++)
        {
            row.Resistance[n] = Num(24 + n);
        }

        return row;
    }

    /// <summary>Igual que en Item.txt: el nombre va entre comillas y puede tener espacios
    /// ("Bull Fighter"), así que no alcanza con partir por espacios.</summary>
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

    public static void Save(string path, string header, IEnumerable<MonsterRow> rows)
    {
        static string Col(object value, int width) =>
            (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0").PadRight(width);

        var sb = new StringBuilder(header);

        foreach (var row in rows.OrderBy(r => r.Index))
        {
            sb.Append(Col(row.Index, 10));
            sb.Append(Col(row.Type, 7));
            sb.Append(('"' + row.Name + '"').PadRight(37));
            sb.Append(Col(row.Level, 8));
            sb.Append(Col(row.MaxLife, 10));
            sb.Append(Col(row.MaxMana, 10));
            sb.Append(Col(row.DamageMin, 12));
            sb.Append(Col(row.DamageMax, 12));
            sb.Append(Col(row.Defense, 10));
            sb.Append(Col(row.MagicDefense, 15));
            sb.Append(Col(row.AttackRate, 13));
            sb.Append(Col(row.DefenseRate, 14));
            sb.Append(Col(row.MoveRange, 12));
            sb.Append(Col(row.AttackType, 13));
            sb.Append(Col(row.AttackRange, 14));
            sb.Append(Col(row.ViewRange, 12));
            sb.Append(Col(row.MoveSpeed, 12));
            sb.Append(Col(row.AttackSpeed, 14));
            sb.Append(Col(row.RegenTime, 12));
            sb.Append(Col(row.Attribute, 12));
            sb.Append(Col(row.ItemRate, 11));
            sb.Append(Col(row.MoneyRate, 12));
            sb.Append(Col(row.MaxItemLevel, 15));
            sb.Append(Col(row.MonsterSkill, 15));

            for (int n = 0; n < 7; n++)
            {
                sb.Append(Col(row.Resistance[n], 14));
            }

            while (sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }
            sb.AppendLine();
        }

        sb.AppendLine("end");

        // Escritura atómica -- el GameServer lee este archivo al arrancar.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }
}

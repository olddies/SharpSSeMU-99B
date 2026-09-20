using System.Globalization;
using System.Text;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Reads and writes Data/Item/Item.txt end to end -- it does not reuse <c>MemScript</c> (the tokenizer
/// the GameServer uses to load <c>ItemBalanceTable</c>) because that tokenizer discards "//" comments when
/// reading, and this panel needs to preserve each section's header line as is so as not to desynchronise the
/// file from its own column comments. The real format (confirmed line by line against Data/Item/Item.txt): 16
/// sections (0-15), each one as "&lt;section number&gt;" on its own line, followed by a "//column comment"
/// line, then one row per line, and "end" to close the section. The per-section column layout is the same as
/// the one <c>MuServer.GameServer.World.ItemBalanceTable.Load</c> already has verified.</summary>
public static class ItemFileRepository
{
    public static readonly IReadOnlyList<(int Section, string Label)> Sections = new List<(int, string)>
    {
        (0, "Swords"), (1, "Axes"), (2, "Maces/Scepters"), (3, "Spears"), (4, "Bows/Crossbows"),
        (5, "Staffs"), (6, "Shields"), (7, "Helms"), (8, "Armors"), (9, "Pants"),
        (10, "Gloves"), (11, "Boots"), (12, "Wings/Orbs/Jewel of Chaos"), (13, "Pets/Rings/Pendants/Misc"),
        (14, "Jewels/Potions/Consumables"), (15, "Orbs/Scrolls"),
    };

    public static (Dictionary<int, string> Headers, Dictionary<int, List<ItemRow>> RowsBySection) Load(string path)
    {
        var lines = File.ReadAllLines(path);
        var headers = new Dictionary<int, string>();
        var rowsBySection = new Dictionary<int, List<ItemRow>>();

        int i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith("//"))
            {
                i++;
                continue;
            }

            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int section))
            {
                i++;
                continue;
            }

            i++;

            // The column header line always comes right after the section number.
            if (i < lines.Length && lines[i].TrimStart().StartsWith("//"))
            {
                headers[section] = lines[i];
                i++;
            }

            var rows = new List<ItemRow>();
            while (i < lines.Length)
            {
                var rowTrimmed = lines[i].Trim();
                i++;

                if (rowTrimmed.Length == 0)
                {
                    continue;
                }

                if (rowTrimmed == "end")
                {
                    break;
                }

                if (rowTrimmed.StartsWith("//"))
                {
                    continue;
                }

                rows.Add(ParseRow(section, rowTrimmed));
            }

            rowsBySection[section] = rows;
        }

        return (headers, rowsBySection);
    }

    /// <summary>Tokenises a row respecting quotes -- unlike Move.txt, item names DO carry spaces inside the
    /// quotes (e.g. "Sword of Assassin"), so a simple Split(' ') would split the name into several
    /// tokens.</summary>
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

    private static ItemRow ParseRow(int section, string line)
    {
        var t = Tokenize(line);

        // "*" == -1 (no restriction/any), the same as MemScript.GetTokenNumber.
        int Num(int idx) => idx >= t.Count ? 0 : (t[idx] == "*" ? -1 : int.Parse(t[idx], CultureInfo.InvariantCulture));
        bool Bool(int idx) => Num(idx) != 0;

        var row = new ItemRow
        {
            Section = section,
            Sub = Num(0),
            Slot = Num(1),
            Skill = Num(2),
            Width = Num(3),
            Height = Num(4),
            HaveSerial = Bool(5),
            HaveOption = Bool(6),
            DropItem = Bool(7),
            Name = t.Count > 8 ? t[8] : string.Empty,
        };

        // From here on the layout depends on the section -- same switch as ItemBalanceTable.Load.
        switch (section)
        {
            case >= 0 and <= 5: // armas
                row.Level = Num(9); row.DamageMin = Num(10); row.DamageMax = Num(11); row.AttackSpeed = Num(12);
                row.Durability = Num(13); row.MagicDurability = Num(14); row.MagicDamageRate = Num(15);
                row.RequireLevel = Num(16); row.RequireStrength = Num(17); row.RequireDexterity = Num(18);
                row.RequireEnergy = Num(19); row.RequireVitality = Num(20); row.RequireLeadership = Num(21);
                row.SetAttr = Num(22);
                ReadClass(row, t, 23);
                break;

            case 6: // escudo
                row.Level = Num(9); row.Defense = Num(10); row.DefenseSuccessRate = Num(11); row.Durability = Num(12);
                row.RequireLevel = Num(13); row.RequireStrength = Num(14); row.RequireDexterity = Num(15);
                row.RequireEnergy = Num(16); row.RequireVitality = Num(17); row.RequireLeadership = Num(18);
                row.SetAttr = Num(19);
                ReadClass(row, t, 20);
                break;

            case 7 or 8 or 9: // helm/armor/pants
                row.Level = Num(9); row.Defense = Num(10); row.MagicDefense = Num(11); row.Durability = Num(12);
                row.RequireLevel = Num(13); row.RequireStrength = Num(14); row.RequireDexterity = Num(15);
                row.RequireEnergy = Num(16); row.RequireVitality = Num(17); row.RequireLeadership = Num(18);
                row.SetAttr = Num(19);
                ReadClass(row, t, 20);
                break;

            case 10: // guantes
                row.Level = Num(9); row.Defense = Num(10); row.AttackSpeed = Num(11); row.Durability = Num(12);
                row.RequireLevel = Num(13); row.RequireStrength = Num(14); row.RequireDexterity = Num(15);
                row.RequireEnergy = Num(16); row.RequireVitality = Num(17); row.RequireLeadership = Num(18);
                row.SetAttr = Num(19);
                ReadClass(row, t, 20);
                break;

            case 11: // botas
                row.Level = Num(9); row.Defense = Num(10); row.WalkSpeed = Num(11); row.Durability = Num(12);
                row.RequireLevel = Num(13); row.RequireStrength = Num(14); row.RequireDexterity = Num(15);
                row.RequireEnergy = Num(16); row.RequireVitality = Num(17); row.RequireLeadership = Num(18);
                row.SetAttr = Num(19);
                ReadClass(row, t, 20);
                break;

            case 12: // alas -- orden distinto (ReqEnergy antes que ReqStr/ReqDex), sin SetAttr
                row.Level = Num(9); row.Defense = Num(10); row.Durability = Num(11);
                row.RequireLevel = Num(12); row.RequireEnergy = Num(13); row.RequireStrength = Num(14);
                row.RequireDexterity = Num(15); row.RequireLeadership = Num(16); row.BuyMoney = Num(17);
                ReadClass(row, t, 18);
                break;

            case 13: // pets/ring-pendant jewels/miscellaneous
                row.Level = Num(9); row.Durability = Num(10);
                for (int n = 0; n < 7; n++)
                {
                    row.Resistance[n] = Num(11 + n);
                }
                row.SetAttr = Num(18);
                ReadClass(row, t, 19);
                break;

            case 14: // jewels/potions/consumables -- only Value and Level
                row.Value = Num(9); row.Level = Num(10);
                break;

            case 15: // orbes/pergaminos
                row.Level = Num(9); row.RequireLevel = Num(10); row.RequireEnergy = Num(11); row.BuyMoney = Num(12);
                ReadClass(row, t, 13);
                break;
        }

        return row;
    }

    private static void ReadClass(ItemRow row, List<string> t, int startIdx)
    {
        for (int n = 0; n < 5; n++)
        {
            int idx = startIdx + n;
            row.RequireClass[n] = idx >= t.Count ? 0 : (t[idx] == "*" ? -1 : int.Parse(t[idx], CultureInfo.InvariantCulture));
        }
    }

    private static string Col(object value, int width)
    {
        var text = value is int i && i == -1 ? "*" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
        return text.PadRight(width);
    }

    public static void Save(string path, Dictionary<int, string> headers, Dictionary<int, List<ItemRow>> rowsBySection)
    {
        var sb = new StringBuilder();

        foreach (var (section, _) in Sections)
        {
            sb.AppendLine(section.ToString(CultureInfo.InvariantCulture));

            if (headers.TryGetValue(section, out var header))
            {
                sb.AppendLine(header);
            }

            var rows = rowsBySection.TryGetValue(section, out var list) ? list : new List<ItemRow>();

            foreach (var row in rows.OrderBy(r => r.Sub))
            {
                sb.Append(Col(row.Sub, 9));
                sb.Append(Col(row.Slot, 7));
                sb.Append(Col(row.Skill, 8));
                sb.Append(Col(row.Width, 8));
                sb.Append(Col(row.Height, 9));
                sb.Append(Col(row.HaveSerial ? 1 : 0, 13));
                sb.Append(Col(row.HaveOption ? 1 : 0, 13));
                sb.Append(Col(row.DropItem ? 1 : 0, 11));
                sb.Append(('"' + row.Name + '"').PadRight(38));

                switch (section)
                {
                    case >= 0 and <= 5:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.DamageMin, 12)); sb.Append(Col(row.DamageMax, 12));
                        sb.Append(Col(row.AttackSpeed, 14)); sb.Append(Col(row.Durability, 13)); sb.Append(Col(row.MagicDurability, 18));
                        sb.Append(Col(row.MagicDamageRate, 18)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireVitality, 14));
                        sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 6:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Defense, 10)); sb.Append(Col(row.DefenseSuccessRate, 21));
                        sb.Append(Col(row.Durability, 13)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireVitality, 14));
                        sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 7 or 8 or 9:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Defense, 10)); sb.Append(Col(row.MagicDefense, 15));
                        sb.Append(Col(row.Durability, 13)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireVitality, 14));
                        sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 10:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Defense, 10)); sb.Append(Col(row.AttackSpeed, 14));
                        sb.Append(Col(row.Durability, 13)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireVitality, 14));
                        sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 11:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Defense, 10)); sb.Append(Col(row.WalkSpeed, 12));
                        sb.Append(Col(row.Durability, 13)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireVitality, 14));
                        sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 12:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Defense, 10)); sb.Append(Col(row.Durability, 13));
                        sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireEnergy, 12)); sb.Append(Col(row.RequireStrength, 14));
                        sb.Append(Col(row.RequireDexterity, 15)); sb.Append(Col(row.RequireLeadership, 16)); sb.Append(Col(row.BuyMoney, 11));
                        AppendClass(sb, row);
                        break;

                    case 13:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.Durability, 13));
                        foreach (var res in row.Resistance)
                        {
                            sb.Append(Col(res, 11));
                        }
                        sb.Append(Col(row.SetAttr, 10));
                        AppendClass(sb, row);
                        break;

                    case 14:
                        sb.Append(Col(row.Value, 8)); sb.Append(Col(row.Level, 7));
                        break;

                    case 15:
                        sb.Append(Col(row.Level, 8)); sb.Append(Col(row.RequireLevel, 11)); sb.Append(Col(row.RequireEnergy, 12));
                        sb.Append(Col(row.BuyMoney, 11));
                        AppendClass(sb, row);
                        break;
                }

                // Removes the trailing padding of the last column so as not to leave lines with dangling spaces.
                while (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }
                sb.AppendLine();
            }

            sb.AppendLine("end");
            sb.AppendLine();
        }

        // Atomic write (temp + copy) -- this file is read by the GameServer at start-up, and a broken
        // half-written Item.txt takes it down.
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, sb.ToString());
        File.Copy(tmpPath, path, overwrite: true);
        File.Delete(tmpPath);
    }

    private static void AppendClass(StringBuilder sb, ItemRow row)
    {
        for (int n = 0; n < 5; n++)
        {
            sb.Append(Col(row.RequireClass[n], 5));
        }
    }
}

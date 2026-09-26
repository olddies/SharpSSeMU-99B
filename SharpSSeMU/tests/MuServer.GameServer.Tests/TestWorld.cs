using System.Runtime.CompilerServices;
using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.World;

namespace MuServer.GameServer.Tests;

/// <summary>Items, option rules and players built by hand for the tests -- the real game data is not part of the
/// repository. The option rules copy the shape of the shipped ItemOption.txt rows they stand for.</summary>
internal static class TestWorld
{
    public static readonly int Kris = Item.GetItem(0, 0);
    public static readonly int Katana = Item.GetItem(0, 3);
    public static readonly int SkullStaff = Item.GetItem(5, 0);
    public static readonly int BronzeArmor = Item.GetItem(8, 0);
    public static readonly int WingsOfSatan = Item.GetItem(12, 2);
    public static readonly int WingsOfSpirit = Item.GetItem(12, 3);

    public const string OptionRules = """
        // Index OptionIndex OptionValue ItemIndexMin ItemIndexMax SkillSwitch LuckSwitch Aditional Excellent
           0     0     0     00,000   04,031   1   *   *   *     //General Weapon
           1     84    5     00,000   04,031   *   1   *   *     //General Weapon
           2     80    4     00,000   04,031   *   *   1   *     //General Weapon
           3     99    12    00,000   04,031   *   *   *   1     //General Weapon
           5     97    7     00,000   04,031   *   *   *   4     //General Weapon
           6     94    2     00,000   04,031   *   *   *   8     //General Weapon
           7     93    20    00,000   04,031   *   *   *   16    //General Weapon
           8     92    10    00,000   04,031   *   *   *   32    //General Weapon
           1     84    5     05,000   05,019   *   1   *   *     //General Staff
           2     81    4     05,000   05,019   *   *   1   *     //General Staff
           1     84    5     07,000   11,031   *   1   *   *     //General Armor
           2     83    4     07,000   11,031   *   *   1   *     //General Armor
           3     86    30    07,000   11,031   *   *   *   1     //General Armor
           6     89    4     07,000   11,031   *   *   *   8     //General Armor
           8     91    4     07,000   11,031   *   *   *   32    //General Armor
           2     80    4     12,002   12,002   *   *   1   *     //Wings of Satan
           2     80    4     12,003   12,003   *   *   1   *     //Wings of Spirit
           2     85    1     12,003   12,003   *   *   1   32    //Wings of Spirit
        end
        """;

    public static ItemOptionTable LoadRules()
    {
        string path = Path.Combine(Path.GetTempPath(), $"itemoption-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, OptionRules);

        try
        {
            var table = new ItemOptionTable();
            table.Load(path);
            return table;
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static ItemBalanceTable Items()
    {
        var items = new ItemBalanceTable();
        items.Add(new ItemBalance { Index = Kris, Section = 0, Sub = 0, Slot = 0, Level = 6, DamageMin = 6, DamageMax = 11, AttackSpeed = 50, Durability = 20, RequireStrength = 40 });
        items.Add(new ItemBalance { Index = Katana, Section = 0, Sub = 3, Slot = 0, Skill = 20, Level = 16, DamageMin = 16, DamageMax = 26, AttackSpeed = 35, Durability = 30 });
        items.Add(new ItemBalance { Index = SkullStaff, Section = 5, Sub = 0, Slot = 0, Skill = 20, Level = 6, DamageMin = 3, DamageMax = 4, MagicDamageRate = 6, Durability = 20 });
        items.Add(new ItemBalance { Index = BronzeArmor, Section = 8, Sub = 0, Slot = 3, Level = 20, Defense = 20, Durability = 40 });
        items.Add(new ItemBalance { Index = WingsOfSatan, Section = 12, Sub = 2, Slot = 7, Level = 100, Defense = 20, Durability = 200 });
        items.Add(new ItemBalance { Index = WingsOfSpirit, Section = 12, Sub = 3, Slot = 7, Level = 150, Defense = 30, Durability = 200 });
        return items;
    }

    /// <summary>The factory balance: the configuration files do not exist, so every constant takes its default.</summary>
    public static CharacterBalanceConfig Balance() => CharacterBalanceConfig.Load("missing-character.dat", "missing-common.dat");

    public static PlayerObject DarkKnight(uint strength = 100, uint energy = 30)
    {
        // The session is never used by the stat or damage code.
        var session = (ClientSession)RuntimeHelpers.GetUninitializedObject(typeof(ClientSession));
        return new PlayerObject
        {
            Index = 9000,
            Session = session,
            Class = 1,
            Level = 1,
            Strength = strength,
            Dexterity = 20,
            Vitality = 25,
            Energy = energy,
        };
    }

    public static Item Make(int index, byte level = 0, byte skill = 0, byte luck = 0, byte additional = 0, byte excellent = 0, byte durability = 20) =>
        new()
        {
            Index = (short)index,
            Level = level,
            Durability = durability,
            Option1 = skill,
            Option2 = luck,
            Option3 = additional,
            NewOption = excellent,
        };
}

/// <summary>A Random that returns the queued values, in order, so a hit's rolls can be chosen.</summary>
internal sealed class ScriptedRandom : Random
{
    private readonly Queue<int> _values;

    public ScriptedRandom(params int[] values) => _values = new Queue<int>(values);

    public override int Next(int maxValue) => Math.Min(_values.Dequeue(), Math.Max(maxValue - 1, 0));

    public override int Next(int minValue, int maxValue) => minValue + Next(maxValue - minValue);
}

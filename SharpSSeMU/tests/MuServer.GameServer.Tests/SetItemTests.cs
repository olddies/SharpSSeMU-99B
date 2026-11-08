using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>Set (ancient) items: the type and option files, the set option byte, the bonuses of the worn pieces, the
/// stats of a set item, and where set items come from (random drops and the item bags).</summary>
public class SetItemTests
{
    private static readonly int BronzeHelm = Item.GetItem(7, 0);

    // Item, StatType, set of the first variant, set of the second variant, drop rate.
    private const string TypeFile = """
        00,000      0       1       0       10000       //Kris: strength, only the first variant
        08,000      2       1       2       5000        //Bronze Armor: energy, two variants
        07,000      1       1       0       5000        //Bronze Helm: agility
        end
        """;

    // Set 1 "Test": two tiers of bonus, then the full-set bonus. Six tiers of two pairs, then five full-set pairs.
    private const string OptionFile = """
        1   "Test"
            15 5   *  *      10 20  *  *      *  *   *  *      *  *   *  *      *  *   *  *      *  *   *  *
            17 3   *  *      *  *   *  *      *  *
        end
        """;

    private SetItemRules _rules = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();
        _rules = new SetItemRules();
        WithFile(TypeFile, path => _rules.Types.Load(path));
        WithFile(OptionFile, path => _rules.LoadSets(path));
        SetItemRules.Current = _rules;
    }

    [TearDown]
    public void TearDown() => SetItemRules.Current = new SetItemRules();

    private static void WithFile(string text, Action<string> use)
    {
        string path = Path.Combine(Path.GetTempPath(), $"set-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);

        try
        {
            use(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- the files

    [Test]
    public void The_type_and_option_files_are_read()
    {
        Assert.That(_rules.Types.Count, Is.EqualTo(3));
        Assert.That(_rules.Types.GetStatType(BronzeArmor), Is.EqualTo(2));
        Assert.That(_rules.Types.GetOptionIndex(BronzeArmor, 0), Is.EqualTo(1));
        Assert.That(_rules.Types.GetOptionIndex(BronzeArmor, 1), Is.EqualTo(2));
        Assert.That(_rules.Types.GetOptionIndex(Kris, 1), Is.EqualTo(0), "no second variant");
        Assert.That(_rules.Types.GetOptionIndex(Katana, 0), Is.EqualTo(-1), "not a set piece");
        Assert.That(_rules.Types.GetStatType(Katana), Is.EqualTo(-1));

        var set = _rules.GetSet(1)!;
        Assert.That(set.Name, Is.EqualTo("Test"));
        Assert.That(set.OptionTable[0, 0], Is.EqualTo(new SetItemOptionPair(15, 5)));
        Assert.That(set.OptionTable[0, 1].Index, Is.EqualTo(-1));
        Assert.That(set.OptionTable[1, 0], Is.EqualTo(new SetItemOptionPair(10, 20)));
        Assert.That(set.FullOptionTable[0], Is.EqualTo(new SetItemOptionPair(17, 3)));
        Assert.That(set.MaxOptionCount, Is.EqualTo(2));
    }

    [Test]
    public void The_real_shipped_files_are_read_when_present()
    {
        string data = FindDataFolder();
        Assume.That(data, Is.Not.Null, "the game data package is not next to the repo");

        var rules = new SetItemRules();
        Assert.That(rules.Types.Load(Path.Combine(data!, "Item", "SetItemType.txt")), Is.EqualTo(78));
        Assert.That(rules.LoadSets(Path.Combine(data!, "Item", "SetItemOption.txt")), Is.EqualTo(16));
        Assert.That(rules.GetSet(1)!.Name, Is.EqualTo("Warrior"));
        Assert.That(rules.GetSet(1)!.OptionTable[0, 0], Is.EqualTo(new SetItemOptionPair(0, 10)));
    }

    private static string? FindDataFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "MuServer99B", "Data");

            if (File.Exists(Path.Combine(candidate, "Item", "SetItemType.txt")))
            {
                return candidate;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- the set option byte

    [Test]
    public void The_set_option_value_picks_the_variant_and_the_tier()
    {
        var types = _rules.Types;

        // A piece with two variants: 1 is tier 1 of the first or tier 2 of the second, 2 the other way round.
        Assert.That(types.MakeSetOption(BronzeArmor, 1, new ScriptedRandom(0)), Is.EqualTo(5));
        Assert.That(types.MakeSetOption(BronzeArmor, 1, new ScriptedRandom(1)), Is.EqualTo(10));
        Assert.That(types.MakeSetOption(BronzeArmor, 2, new ScriptedRandom(0)), Is.EqualTo(9));
        Assert.That(types.MakeSetOption(BronzeArmor, 2, new ScriptedRandom(1)), Is.EqualTo(6));

        // 3 and 4 force the first variant, 5 and 6 the second.
        Assert.That(types.MakeSetOption(BronzeArmor, 3, new Random(1)), Is.EqualTo(5));
        Assert.That(types.MakeSetOption(BronzeArmor, 4, new Random(1)), Is.EqualTo(9));
        Assert.That(types.MakeSetOption(BronzeArmor, 5, new Random(1)), Is.EqualTo(6));
        Assert.That(types.MakeSetOption(BronzeArmor, 6, new Random(1)), Is.EqualTo(10));

        // A piece with one variant only gets that one, and nothing for the missing one.
        Assert.That(types.MakeSetOption(Kris, 1, new ScriptedRandom(0)), Is.EqualTo(5));
        Assert.That(types.MakeSetOption(Kris, 5, new Random(1)), Is.EqualTo(0));

        Assert.That(types.MakeSetOption(BronzeArmor, 0, new Random(1)), Is.EqualTo(0));
        Assert.That(types.MakeSetOption(Katana, 1, new Random(1)), Is.EqualTo(0), "not a set piece");
    }

    [Test]
    public void An_invalid_set_option_is_cleared_when_an_item_is_made()
    {
        foreach (var (given, expected) in new[] { (5, 5), (6, 6), (9, 9), (10, 10), (0, 0), (1, 0), (4, 0), (7, 0), (13, 0), (14, 0) })
        {
            var item = Make(Kris);
            item.SetOption = (byte)given;
            ItemCombatMath.Options.Normalize(item);
            Assert.That(item.SetOption, Is.EqualTo(expected), $"set option {given}");
        }
    }

    [Test]
    public void A_random_set_piece_follows_the_drop_rates_and_skips_what_cannot_drop()
    {
        // The Bronze Helm is not in the table of items at all: it cannot drop.
        var items = Droppable(Kris, BronzeArmor);

        var rng = new Random(3);
        var counts = new Dictionary<int, int>();

        for (int i = 0; i < 3000; i++)
        {
            int piece = _rules.Types.GetRandomSetItem(items, rng);
            counts[piece] = counts.GetValueOrDefault(piece) + 1;
        }

        Assert.That(counts.Keys, Is.EquivalentTo(new[] { Kris, BronzeArmor }));
        Assert.That(counts[Kris], Is.InRange(1850, 2150), "10000 of 15000");

        Assert.That(_rules.Types.GetRandomSetItem(Droppable(), rng), Is.EqualTo(-1), "nothing can drop");
        Assert.That(_rules.Types.GetRandomSetItem(Items(), rng), Is.EqualTo(-1), "no item of the table is marked as droppable");
    }

    /// <summary>An item table in which only the given items can drop.</summary>
    private static ItemBalanceTable Droppable(params int[] indexes)
    {
        var table = new ItemBalanceTable();

        foreach (int index in indexes)
        {
            table.Add(new ItemBalance { Index = index, Section = index / 32, Sub = index % 32, Slot = 0, Level = 6, Durability = 20, DropItem = true });
        }

        return table;
    }

    // ---------------------------------------------------------------- the bonuses of the worn pieces

    private static Item SetPiece(int index, byte setOption)
    {
        var item = Make(index, durability: 40);
        item.SetOption = setOption;
        return item;
    }

    private static Item[] Worn(params (int Slot, Item Item)[] pieces)
    {
        var inventory = Enumerable.Range(0, Item.InventorySize).Select(_ => Item.Empty()).ToArray();

        foreach (var (slot, item) in pieces)
        {
            inventory[slot] = item;
        }

        return inventory;
    }

    [Test]
    public void A_piece_adds_five_per_tier_to_its_stat_and_a_lone_piece_gives_no_set_bonus()
    {
        var lone = _rules.Calculate(Worn((Item.SlotWeapon1, SetPiece(Kris, 5))));
        Assert.That(lone.AddStrength, Is.EqualTo(5));
        Assert.That(lone.Options, Is.Empty);
        Assert.That(lone.IsFullSet, Is.False);

        var tierTwo = _rules.Calculate(Worn((Item.SlotArmor, SetPiece(BronzeArmor, 9))));
        Assert.That(tierTwo.AddEnergy, Is.EqualTo(10), "tier 2 is worth 10");
    }

    [Test]
    public void Wearing_more_pieces_of_a_set_unlocks_its_tiers_and_the_full_bonus()
    {
        var two = _rules.Calculate(Worn((Item.SlotWeapon1, SetPiece(Kris, 5)), (Item.SlotArmor, SetPiece(BronzeArmor, 5))));
        Assert.That(two.Options, Is.EqualTo(new[] { new SetItemOptionPair(15, 5) }), "the first tier");
        Assert.That(two.IsFullSet, Is.False);
        Assert.That((two.AddStrength, two.AddEnergy), Is.EqualTo((5, 5)));

        var all = _rules.Calculate(Worn(
            (Item.SlotWeapon1, SetPiece(Kris, 5)), (Item.SlotArmor, SetPiece(BronzeArmor, 5)), (Item.SlotHelm, SetPiece(BronzeHelm, 5))));
        Assert.That(all.Options, Is.EqualTo(new[] { new SetItemOptionPair(15, 5), new SetItemOptionPair(10, 20), new SetItemOptionPair(17, 3) }));
        Assert.That(all.IsFullSet, Is.True);
        Assert.That(all.AddDexterity, Is.EqualTo(5), "the helm's agility");
    }

    [Test]
    public void Pieces_of_another_variant_do_not_count_for_the_set()
    {
        // The Bronze Armor's second variant belongs to set 2, which does not exist here.
        var mixed = _rules.Calculate(Worn((Item.SlotWeapon1, SetPiece(Kris, 5)), (Item.SlotArmor, SetPiece(BronzeArmor, 6))));
        Assert.That(mixed.Options, Is.Empty);
    }

    [Test]
    public void Two_identical_weapons_or_rings_count_once()
    {
        var twice = _rules.Calculate(Worn((Item.SlotWeapon1, SetPiece(Kris, 5)), (Item.SlotWeapon2, SetPiece(Kris, 5))));
        Assert.That(twice.Options, Is.Empty, "the second Kris does not make a second piece");
        Assert.That(twice.AddStrength, Is.EqualTo(10), "but each still adds its own strength");
    }

    [Test]
    public void Without_the_files_nothing_is_added()
    {
        var empty = new SetItemRules().Calculate(Worn((Item.SlotWeapon1, SetPiece(Kris, 5))));
        Assert.That(empty.AddStrength, Is.EqualTo(0));
        Assert.That(empty.Options, Is.Empty);
    }

    // ---------------------------------------------------------------- the item itself

    [Test]
    public void A_set_item_has_better_stats_and_more_durability_than_an_excellent_one()
    {
        var info = Items().Get(Kris)!;
        var plain = Make(Kris);
        var excellent = Make(Kris, excellent: 1);
        var set = SetPiece(Kris, 5);

        Assert.That(ItemCombatMath.GetDamageMin(plain, info), Is.EqualTo(6));
        Assert.That(ItemCombatMath.GetDamageMin(excellent, info), Is.EqualTo(36), "6 + 6*25/6 + 5");
        Assert.That(ItemCombatMath.GetDamageMin(set, info), Is.EqualTo(41), "36 + (6+30)/40 + 5");
        Assert.That(ItemCombatMath.GetDamageMax(set, info), Is.EqualTo(46), "11 + 6*25/6 + 5 + 5");

        Assert.That(ItemCombatMath.GetItemDurability(plain, info), Is.EqualTo(20));
        Assert.That(ItemCombatMath.GetItemDurability(excellent, info), Is.EqualTo(35));
        Assert.That(ItemCombatMath.GetItemDurability(set, info), Is.EqualTo(40), "+20");

        // A set item that is also excellent gets the set bonus, not both.
        var both = SetPiece(Kris, 5);
        both.NewOption = 1;
        Assert.That(ItemCombatMath.GetDamageMin(both, info), Is.EqualTo(41));
        Assert.That(ItemCombatMath.GetItemDurability(both, info), Is.EqualTo(40));
    }

    [Test]
    public void A_set_armour_piece_defends_more()
    {
        var info = Items().Get(BronzeArmor)!;
        var plain = Make(BronzeArmor, durability: 40);
        var set = SetPiece(BronzeArmor, 5);

        int baseDefense = ItemCombatMath.GetDefense(plain, info);
        int setDefense = ItemCombatMath.GetDefense(set, info);

        // 20 + (20*12/20 + 20/5 + 4) = 40, then + (40*3/50 + 50/30 + 2) = 45
        Assert.That(baseDefense, Is.EqualTo(20));
        Assert.That(setDefense, Is.EqualTo(45));
    }

    // ---------------------------------------------------------------- the player

    [Test]
    public void The_set_stats_count_in_the_players_formulas_and_the_other_bonuses_apply()
    {
        var items = Items();
        var balance = Balance();

        var knight = DarkKnight(strength: 100);
        knight.SetItem(Item.SlotWeapon1, SetPiece(Kris, 5));
        knight.SetItem(Item.SlotArmor, SetPiece(BronzeArmor, 5));
        knight.RecalcCombatStats(items, balance);

        Assert.That(knight.AddStrength, Is.EqualTo(5));
        Assert.That(knight.AddEnergy, Is.EqualTo(5));
        Assert.That(knight.CriticalDamageRate, Is.EqualTo(5), "the first tier of the set");
        Assert.That(knight.IsFullSetItem, Is.False);

        // The same character with the strength already in its base and no set rules gets the same damage.
        SetItemRules.Current = new SetItemRules();
        var reference = DarkKnight(strength: 105);
        reference.SetItem(Item.SlotWeapon1, SetPiece(Kris, 5));
        reference.SetItem(Item.SlotArmor, SetPiece(BronzeArmor, 5));
        reference.RecalcCombatStats(items, balance);

        Assert.That(reference.AddStrength, Is.EqualTo(0));
        Assert.That(knight.PhysiDamageMinRight, Is.EqualTo(reference.PhysiDamageMinRight));
        Assert.That(knight.PhysiDamageMaxRight, Is.EqualTo(reference.PhysiDamageMaxRight));
    }

    [Test]
    public void A_full_set_lights_up_the_character_and_adds_its_bonuses()
    {
        var items = Items();
        var knight = DarkKnight();
        knight.SetItem(Item.SlotWeapon1, SetPiece(Kris, 5));
        knight.SetItem(Item.SlotArmor, SetPiece(BronzeArmor, 5));
        knight.SetItem(Item.SlotHelm, SetPiece(BronzeHelm, 5));
        knight.RecalcCombatStats(items, Balance());

        Assert.That(knight.IsFullSetItem, Is.True);
        Assert.That(knight.ExcellentDamageRate, Is.EqualTo(3), "the full-set bonus");

        knight.RebuildCharSet();
        Assert.That(knight.CharSet[11] & 1, Is.EqualTo(1), "the full-set aura bit");
    }

    [Test]
    public void The_double_damage_of_a_set_doubles_the_hit_and_flags_it()
    {
        var items = Items();
        var knight = DarkKnight();
        knight.SetItem(Item.SlotWeapon1, Make(Kris));
        knight.RecalcCombatStats(items, Balance());

        var normal = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, graze: false, items, new Random(1));
        Assert.That(normal.Effect & PlayerAttackMath.EffectDoubleDamage, Is.EqualTo(0));

        knight.DoubleDamageRate = 100;
        var doubled = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, graze: false, items, new Random(1));
        Assert.That(doubled.Effect & PlayerAttackMath.EffectDoubleDamage, Is.EqualTo(PlayerAttackMath.EffectDoubleDamage));
        Assert.That(doubled.Damage, Is.EqualTo(normal.Damage * 2));
    }

    [Test]
    public void Critical_and_excellent_hits_get_the_flat_damage_of_the_set()
    {
        var items = Items();
        var knight = DarkKnight();
        knight.SetItem(Item.SlotWeapon1, Make(Kris));
        knight.RecalcCombatStats(items, Balance());
        knight.CriticalDamageRate = 100;

        int plainCritical = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, items, new Random(1)).Damage;
        knight.CriticalDamage = 10;
        int boosted = PlayerAttackMath.HitMonster(knight, 0, 0, 0, 0, false, items, new Random(1)).Damage;
        Assert.That(boosted, Is.EqualTo(plainCritical + 10));
    }

    // ---------------------------------------------------------------- where set items come from

    private LootRoller Roller(int setItemDropRate)
    {
        var items = Droppable(Kris);

        var optionRates = new ItemOptionRateTable();
        WithFile("""
            5
               2   0 10000 0 0 0 0 0     //Set option: always 1
            end
            """, path => optionRates.Load(path));

        var maps = new MapDropTable();
        maps.Add(0, new MapDropInfo(100, 100, 0, setItemDropRate, 1));

        var mapRates = new MapRateInfoTable();
        mapRates.Add(-1, MapRateInfoTable.DropSet, new[] { -1, -1, -1, -1, -1, 2 });

        return new LootRoller(items, optionRates, new ExcellentOptionRateTable(), maps, mapRates,
            new List<ItemDropRule>(), new List<MoneyDropRule>(), _rules);
    }

    private static LootContext Kill(int level = 30) => new()
    {
        MonsterClass = 3, MonsterLevel = level, Map = 0, ItemRate = 120, MoneyRate = 1, Money = 0, AccountLevel = 0, ServerItemDropRate = 0,
    };

    [Test]
    public void A_kill_can_drop_a_random_set_piece()
    {
        var roller = Roller(setItemDropRate: 1_000_000);
        var drop = roller.Roll(Kill(), new Random(1));

        Assert.That(drop?.Item, Is.Not.Null);
        Assert.That((int)drop!.Item!.Index, Is.AnyOf(Kris), "the only piece that can drop");
        Assert.That(drop.Item.SetOption, Is.EqualTo(5), "set option row 2 always rolls 1, and the Kris only has a first variant");
        Assert.That(drop.Item.Durability, Is.EqualTo(40), "20 and 20 for a set item");

        Assert.That(roller.Roll(Kill(level: 24), new Random(1))?.Item, Is.Null, "set items drop from monster level 25");
        Assert.That(Roller(setItemDropRate: 0).Roll(Kill(), new Random(1))?.Item, Is.Null, "the map has no set drops");
    }

    [Test]
    public void Without_set_files_nothing_set_is_made()
    {
        var roller = new LootRoller(Items(), new ItemOptionRateTable(), new ExcellentOptionRateTable(), new MapDropTable(),
            new MapRateInfoTable(), new List<ItemDropRule>(), new List<MoneyDropRule>(), new SetItemRules());
        Assert.That(roller.MakeRandomSetItem(0, new Random(1)), Is.Null);
    }

    [Test]
    public void A_bag_item_with_a_set_option_row_becomes_a_set_piece()
    {
        var items = Items();
        var optionRates = new ItemOptionRateTable();
        WithFile("""
            5
               2   0 10000 0 0 0 0 0
            end
            """, path => optionRates.Load(path));

        var maker = new ItemBagItemMaker(items, optionRates, new ExcellentOptionRateTable(), _rules);
        var made = maker.Make(new ItemBagItem(Kris, 0, 0, new[] { -1, -1, -1, -1, -1, 2 }), new Random(1));

        Assert.That(made.SetOption, Is.EqualTo(5));
        Assert.That(made.Durability, Is.EqualTo(40));
    }
}

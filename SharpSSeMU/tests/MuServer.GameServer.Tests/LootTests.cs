using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The drop system: the option rate files, excellent options, per-map rates, the ItemDrop/ItemMoneyDrop
/// rules and the roll itself. Files are written here in the format of the originals, much shortened.</summary>
public class LootTests
{
    private const string OptionRates = """
        0
           0   7000   1500   1000   500   0   0   0   0   0   0   0   0   0   0     //Level Option (Common)
           1   10000  0   0   0   0   0   0   0   0   0   0   0   0   0     //Level Option (Exc)
           0   1   1   1   1   1   1   1   1   1   1   1   1   1   1   //ignored: the first row of an index wins
        end

        1
           0   9400   600     //Skill Option (Common)
           1   0      10000   //Skill Option (Exc)
        end

        4
           0   10000  0     0   0   0   0   0     //Exc Option (Common)
           1   0      7500  2500 0   0   0   0     //Exc Option (Exc)
        end
        """;

    private static string Write(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"loot-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text);
        return path;
    }

    private static T WithFile<T>(string text, Func<string, T> use)
    {
        string path = Write(text);

        try
        {
            return use(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- weighted pick and the files

    [Test]
    public void Weighted_pick_follows_the_weights_and_ignores_zeros()
    {
        var options = new[] { (10, 3), (20, 0), (30, 1) };

        Assert.That(WeightedPick.Pick(options, new ScriptedRandom(0)), Is.EqualTo(10));
        Assert.That(WeightedPick.Pick(options, new ScriptedRandom(2)), Is.EqualTo(10));
        Assert.That(WeightedPick.Pick(options, new ScriptedRandom(3)), Is.EqualTo(30));
        Assert.That(WeightedPick.Pick(new[] { (1, 0) }, new Random(1)), Is.Null);
        Assert.That(WeightedPick.Pick(Array.Empty<(int, int)>(), new Random(1)), Is.Null);
    }

    [Test]
    public void Option_rates_are_read_per_section_and_the_first_row_of_an_index_wins()
    {
        var table = new ItemOptionRateTable();
        WithFile(OptionRates, path => table.Load(path));

        Assert.That(table.Count, Is.EqualTo(6));
        Assert.That(table.Pick(ItemOptionRateTable.LevelOption, 1, new Random(1)), Is.EqualTo(0));
        Assert.That(table.Pick(ItemOptionRateTable.LevelOption, 0, new ScriptedRandom(6999)), Is.EqualTo(0));
        Assert.That(table.Pick(ItemOptionRateTable.LevelOption, 0, new ScriptedRandom(7000)), Is.EqualTo(1), "7000 of 10000 are level +0");
        Assert.That(table.Pick(ItemOptionRateTable.ExcellentCount, 1, new ScriptedRandom(0)), Is.EqualTo(1));
        Assert.That(table.Pick(ItemOptionRateTable.ExcellentCount, 1, new ScriptedRandom(9999)), Is.EqualTo(2));
        Assert.That(table.Pick(ItemOptionRateTable.LevelOption, 99, new Random(1)), Is.Null, "no such row");
        Assert.That(table.Pick(ItemOptionRateTable.LevelOption, -1, new Random(1)), Is.Null, "'*' means none");
    }

    private static ExcellentOptionRateTable ExcellentRows()
    {
        var table = new ExcellentOptionRateTable();

        // One option per bit for weapons; the second wings and the Horn of Dinorant have all six rows too.
        foreach (int bit in new[] { 1, 2, 4, 8, 16, 32 })
        {
            table.Add(new ExcellentOptionRow(bit, Item.GetItem(0, 0), Item.GetItem(4, 31), "weapon", 50));
            table.Add(new ExcellentOptionRow(bit, WingsOfSpirit, WingsOfSpirit, "wing", 50));
            table.Add(new ExcellentOptionRow(bit, Item.GetItem(13, 3), Item.GetItem(13, 3), "dinorant", 50));
            table.Add(new ExcellentOptionRow(bit, Item.GetItem(13, 30), Item.GetItem(13, 30), "cape", 50));
            table.Add(new ExcellentOptionRow(bit, Item.GetItem(7, 0), Item.GetItem(11, 31), "armour", 50));
        }

        return table;
    }

    [Test]
    public void Excellent_option_rows_are_read_with_their_item_range()
    {
        const string text = """
            // Index ItemMin ItemMax Name Rate
               1   00,000   04,031   "Add Hunt MP"          60   //General Weapon
               32  05,000   05,031   "Add Excellent Damage Rate"   10   //General Staff
            end
            """;

        var table = new ExcellentOptionRateTable();
        WithFile(text, path => table.Load(path));

        Assert.That(table.Count, Is.EqualTo(2));
        Assert.That(table.GetNewOption(Kris, new Random(1)), Is.EqualTo(1));
        Assert.That(table.GetNewOption(SkullStaff, new Random(1)), Is.EqualTo(32));
        Assert.That(table.GetNewOption(BronzeArmor, new Random(1)), Is.EqualTo(0), "no row covers armour in this file");
    }

    [Test]
    public void The_requested_number_of_distinct_excellent_options_is_rolled()
    {
        var table = ExcellentRows();
        var rng = new Random(7);

        for (int i = 0; i < 50; i++)
        {
            byte option = table.MakeNewOption(Kris, 1, 3, rng);
            Assert.That(System.Numerics.BitOperations.PopCount(option), Is.EqualTo(3));
        }

        Assert.That(table.MakeNewOption(Kris, 1, 0, rng), Is.EqualTo(0));
        Assert.That(table.MakeNewOption(Kris, 0, 40, rng), Is.EqualTo(40), "mode 0: the bits themselves");
    }

    [Test]
    public void Second_wings_dinorant_and_the_cape_cap_the_number_of_options()
    {
        var table = ExcellentRows();
        var rng = new Random(3);

        Assert.That(System.Numerics.BitOperations.PopCount(table.MakeNewOption(WingsOfSpirit, 1, 6, rng)), Is.EqualTo(3));
        Assert.That(System.Numerics.BitOperations.PopCount(table.MakeNewOption(Item.GetItem(13, 3), 1, 6, rng)), Is.EqualTo(3));
        Assert.That(System.Numerics.BitOperations.PopCount(table.MakeNewOption(Item.GetItem(13, 30), 1, 6, rng)), Is.EqualTo(4));
        Assert.That(ExcellentOptionRateTable.MaxOptionsFor(Kris), Is.EqualTo(6));
    }

    [Test]
    public void A_file_with_fewer_options_than_asked_for_cannot_hang_the_roll()
    {
        var table = new ExcellentOptionRateTable();
        table.Add(new ExcellentOptionRow(1, Item.GetItem(0, 0), Item.GetItem(4, 31), "only", 100));

        Assert.That(table.MakeNewOption(Kris, 1, 4, new Random(1)), Is.EqualTo(1));
    }

    [Test]
    public void Map_files_are_read_and_unknown_maps_use_the_defaults()
    {
        const string mapManager = """
               0   *   12   100   100   100   1000   0   1   1   *   *   *   1   1   1   1   *   *   "Lorencia"
               9   *   10   100   200   300   0      0   0   0   *   *   *   1   1   0   0   *   *   "Devil Square 1"
            end
            """;
        const string mapRates = """
               *   0   0   0   0   0   *   *    // Drop Common
               *   1   1   1   1   1   1   *    // Drop Excellent
            end
            """;

        var drops = new MapDropTable();
        var rates = new MapRateInfoTable();
        WithFile(mapManager, path => drops.Load(path));
        WithFile(mapRates, path => rates.Load(path));

        Assert.That(drops.Count, Is.EqualTo(2));
        Assert.That(drops.ExcItemDropRate(0), Is.EqualTo(1000));
        Assert.That(drops.ItemDropEnabled(0), Is.True);
        Assert.That(drops.ItemDropRate(9), Is.EqualTo(200));
        Assert.That(drops.MoneyDropRate(9), Is.EqualTo(300));
        Assert.That(drops.ItemDropEnabled(9), Is.False);
        Assert.That((drops.ItemDropRate(77), drops.MoneyDropRate(77), drops.ExcItemDropRate(77), drops.ItemDropEnabled(77)), Is.EqualTo((100, 100, 0, false)));

        Assert.That(rates.Count, Is.EqualTo(2));
        Assert.That(rates.GetOption(1, 3, MapRateInfoTable.DropExcellent), Is.EqualTo(1));
        Assert.That(rates.GetOption(4, 3, MapRateInfoTable.DropCommon), Is.EqualTo(-1), "'*' is read as none");
        Assert.That(rates.GetOption(0, 3, MapRateInfoTable.DropSet), Is.EqualTo(0), "no row: 0");
    }

    // ---------------------------------------------------------------- the roll

    private ItemBalanceTable _items = null!;
    private LootRoller _roller = null!;
    private MapDropTable _maps = null!;
    private MapRateInfoTable _mapRates = null!;
    private List<ItemDropRule> _itemRules = null!;
    private List<MoneyDropRule> _moneyRules = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();

        _items = new ItemBalanceTable();
        _items.Add(new ItemBalance { Index = Kris, Section = 0, Sub = 0, Slot = 0, Level = 6, DamageMin = 6, DamageMax = 11, Durability = 20, DropItem = true });
        _items.Add(new ItemBalance { Index = Katana, Section = 0, Sub = 3, Slot = 0, Level = 16, DamageMin = 16, DamageMax = 26, Durability = 30, DropItem = true });
        _items.Add(new ItemBalance { Index = BronzeArmor, Section = 8, Sub = 0, Slot = 3, Level = 30, Defense = 20, Durability = 40, DropItem = true });
        _items.Add(new ItemBalance { Index = WingsOfSatan, Section = 12, Sub = 2, Slot = 7, Level = 30, Defense = 20, Durability = 200, DropItem = true });
        _items.Add(new ItemBalance { Index = Item.GetItem(12, 15), Section = 12, Sub = 15, Slot = -1, Level = 12, Durability = 1 }); // Jewel of Chaos: not in the general pool

        var optionRates = new ItemOptionRateTable();
        WithFile(OptionRates, path => optionRates.Load(path));

        _maps = new MapDropTable();
        _maps.Add(0, new MapDropInfo(100, 100, 0, 0, 1));

        _mapRates = new MapRateInfoTable();
        _mapRates.Add(-1, MapRateInfoTable.DropCommon, new[] { 0, 0, -1, -1, -1, -1 });
        _mapRates.Add(-1, MapRateInfoTable.DropExcellent, new[] { 1, 1, -1, -1, 1, -1 });

        _itemRules = new List<ItemDropRule>();
        _moneyRules = new List<MoneyDropRule>();
        _roller = new LootRoller(_items, optionRates, ExcellentRows(), _maps, _mapRates, _itemRules, _moneyRules);
    }

    private static LootContext Kill(int level = 10, int itemRate = 120, long money = 0, int serverItemDropRate = 300, int moneyRate = 1) => new()
    {
        MonsterClass = 3,
        MonsterLevel = level,
        Map = 0,
        ItemRate = itemRate,
        MoneyRate = moneyRate,
        Money = money,
        AccountLevel = 0,
        ServerItemDropRate = serverItemDropRate,
    };

    [Test]
    public void The_item_pool_of_a_level_and_its_limits()
    {
        // Level 10: items of level 6 (10 <= 6+4 and 4 <= 10) qualify, the level 16 (14 > 10) and 30 ones do not.
        Assert.That(_roller.GetMonsterItem(10, false, new Random(1)), Is.EqualTo(Kris));
        Assert.That(_roller.GetMonsterItem(14, false, new Random(1)), Is.EqualTo(Katana));
        Assert.That(_roller.GetMonsterItem(60, false, new Random(1)), Is.EqualTo(-1));
        Assert.That(_roller.GetMonsterItem(-1, false, new Random(1)), Is.EqualTo(-1));
        Assert.That(_roller.GetMonsterItem(150, false, new Random(1)), Is.EqualTo(-1));
    }

    [Test]
    public void Excellent_drops_leave_out_wings_and_everything_after_them()
    {
        var rng = new Random(5);

        var normal = Enumerable.Range(0, 100).Select(_ => _roller.GetMonsterItem(30, false, rng)).ToHashSet();
        var excellent = Enumerable.Range(0, 100).Select(_ => _roller.GetMonsterItem(30, true, rng)).ToHashSet();

        Assert.That(normal, Does.Contain(WingsOfSatan));
        Assert.That(excellent, Does.Not.Contain(WingsOfSatan));
        Assert.That(excellent, Does.Contain(BronzeArmor));
    }

    [Test]
    public void Only_the_first_hundred_items_of_a_level_are_in_the_pool()
    {
        for (int i = 0; i < 150; i++)
        {
            _items.Add(new ItemBalance { Index = 100 + i, Section = 3, Sub = i, Slot = 0, Level = 50, DropItem = true });
        }

        var rng = new Random(11);
        var picked = Enumerable.Range(0, 400).Select(_ => _roller.GetMonsterItem(50, false, rng)).ToList();

        Assert.That(picked.Max(), Is.LessThan(100 + 100), "the original keeps 100 entries per level, by item number");
    }

    [Test]
    public void A_common_drop_comes_with_the_options_of_its_map_row()
    {
        var drop = _roller.Roll(Kill(), new Random(2));

        Assert.That(drop?.Item, Is.Not.Null);
        Assert.That(drop!.Item!.Index, Is.EqualTo(Kris));
        Assert.That(drop.Item.NewOption, Is.EqualTo(0), "the common row has no excellent options");
        Assert.That(drop.Item.Durability, Is.EqualTo(20 + drop.Item.Level));
        Assert.That(drop.Item.Level, Is.LessThanOrEqualTo(3));
    }

    [Test]
    public void The_server_item_drop_rate_decides_how_often_an_item_drops()
    {
        int items = 0;
        var rng = new Random(9);

        for (int i = 0; i < 2000; i++)
        {
            items += _roller.Roll(Kill(itemRate: 1000, serverItemDropRate: 100), rng)?.Item != null ? 1 : 0;
        }

        Assert.That(items, Is.InRange(120, 280), "100 of 1000, about 10%");

        for (int i = 0; i < 200; i++)
        {
            Assert.That(_roller.Roll(Kill(itemRate: 120, serverItemDropRate: 300), rng)?.Item, Is.Not.Null, "300 >= 120: always");
            Assert.That(_roller.Roll(Kill(serverItemDropRate: 0), rng)?.Item, Is.Null);
        }
    }

    [Test]
    public void An_excellent_drop_takes_an_item_25_levels_lower_and_carries_excellent_options()
    {
        _maps = new MapDropTable();
        _maps.Add(0, new MapDropInfo(100, 100, 1_000_000, 0, 1)); // every drop from level 25 on is excellent
        var optionRates = new ItemOptionRateTable();
        WithFile(OptionRates, path => optionRates.Load(path));
        var roller = new LootRoller(_items, optionRates, ExcellentRows(), _maps, _mapRates, _itemRules, _moneyRules);

        // A level 55 monster drops from level 30 items; the common Kris would not qualify.
        var drop = roller.Roll(Kill(level: 55), new Random(4));

        Assert.That(drop?.Item?.Index, Is.EqualTo(BronzeArmor));
        Assert.That(drop!.Item!.NewOption, Is.Not.EqualTo(0));
        Assert.That(drop.Item.Durability, Is.EqualTo(40 + 15), "excellent items last 15 longer");

        // Under level 25 nothing is excellent even at a 100% chance.
        Assert.That(roller.Roll(Kill(level: 10), new Random(4))?.Item?.NewOption, Is.EqualTo(0));
    }

    [Test]
    public void Without_an_item_the_monsters_zen_drops_scaled_by_the_server_and_the_player_option()
    {
        var kill = Kill(level: 60, money: 500); // nothing in the pool for level 60, so the roll falls to zen

        Assert.That(_roller.Roll(kill, new Random(1))?.Money, Is.EqualTo(500));

        var rich = new LootContext
        {
            MonsterClass = 3, MonsterLevel = 60, Map = 0, ItemRate = 120, MoneyRate = 1, Money = 500, AccountLevel = 0,
            ServerItemDropRate = 300, ServerMoneyAmountDropRate = 200, PlayerMoneyAmountDropRate = 140,
        };

        Assert.That(_roller.Roll(rich, new Random(1))?.Money, Is.EqualTo(500 * 200 / 100 * 140 / 100));
        Assert.That(_roller.Roll(Kill(level: 60, money: 0), new Random(1)), Is.Null, "a monster that gave no experience has no zen");
    }

    [Test]
    public void Guards_and_statues_drop_nothing()
    {
        foreach (int cls in new[] { 132, 133, 134, 247, 249 })
        {
            var kill = new LootContext { MonsterClass = cls, MonsterLevel = 10, Map = 0, ItemRate = 1, MoneyRate = 1, Money = 99, AccountLevel = 0, ServerItemDropRate = 1000 };
            Assert.That(_roller.Roll(kill, new Random(1)), Is.Null);
        }
    }

    [Test]
    public void Chaos_castle_maps_drop_nothing_beyond_the_rules()
    {
        var kill = new LootContext { MonsterClass = 3, MonsterLevel = 10, Map = 20, ItemRate = 1, MoneyRate = 1, Money = 99, AccountLevel = 0, ServerItemDropRate = 1000 };

        Assert.That(_roller.Roll(kill, new Random(1)), Is.Null);
    }

    [Test]
    public void An_item_rule_drops_its_item_for_the_monsters_it_matches()
    {
        // Jewel of Chaos: any monster of level 12 to 150, always (-1 = every kill), on maps where items drop.
        const string rules = """
               12,015   0   0   *   *   *   *   *   *   *   *   12   150   *   -1   //Jewel of Chaos
               13,014   0   0   *   *   *   *   *   *   10  *   25   150   *   1000 //Loch's Feather (Icarus only)
            end
            """;

        _itemRules.AddRange(WithFile(rules, path => LootRoller.LoadItemRules(path)));
        Assert.That(_itemRules, Has.Count.EqualTo(2));
        Assert.That(_itemRules[0].MonsterLevelMin, Is.EqualTo(12));
        Assert.That(_itemRules[1].Map, Is.EqualTo(10));

        var drop = _roller.Roll(Kill(level: 20), new Random(1));
        Assert.That(drop?.Item?.Index, Is.EqualTo(Item.GetItem(12, 15)));
        Assert.That(drop!.Item!.Durability, Is.EqualTo(1));

        // Below the rule's level range the generic roll runs instead.
        Assert.That(_roller.Roll(Kill(level: 10), new Random(1))?.Item?.Index, Is.EqualTo(Kris));

        // On a map where items do not drop, the rules are skipped.
        var elsewhere = new LootContext { MonsterClass = 3, MonsterLevel = 20, Map = 5, ItemRate = 120, MoneyRate = 1, Money = 0, AccountLevel = 0, ServerItemDropRate = 0 };
        Assert.That(_roller.Roll(elsewhere, new Random(1)), Is.Null);
    }

    [Test]
    public void A_zen_rule_with_an_amount_of_minus_one_drops_the_monsters_own_zen()
    {
        const string rules = """
               *   *   *   *   *   *   1000000    // Money Drop Zen (Global)
            end
            """;

        _moneyRules.AddRange(WithFile(rules, path => LootRoller.LoadMoneyRules(path)));
        Assert.That(_moneyRules, Has.Count.EqualTo(1));
        Assert.That(_moneyRules[0].MoneyAmount, Is.EqualTo(-1));

        // Even where an item would otherwise drop, the zen rule is looked at first (CItemMoneyDrop::DropMoney).
        var drop = _roller.Roll(Kill(level: 10, money: 321), new Random(1));
        Assert.That(drop?.Item, Is.Null);
        Assert.That(drop?.Money, Is.EqualTo(321));
    }
}

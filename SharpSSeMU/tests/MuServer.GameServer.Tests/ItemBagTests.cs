using MuServer.GameServer.Config;
using MuServer.GameServer.World;
using NUnit.Framework;
using static MuServer.GameServer.Tests.TestWorld;

namespace MuServer.GameServer.Tests;

/// <summary>The item bags (Data/EventItemBag): bosses, boxes, events and the Chaos Machine. Files are written here
/// in the format of the originals, much shortened.</summary>
public class ItemBagTests
{
    private const string OptionRates = """
        0
           3   0 0 10000 0 0 0 0 0 0 0 0 0 0 0     //Level +2
        end

        1
           0   0  10000     //Skill
        end

        2
           1   0  10000     //Luck
        end

        3
           2   0 0 0 10000 0 0 0 0     //Additional +12 (eight values)
        end

        4
           4   0 0 10000 0 0 0 0     //Two excellent options
        end
        """;

    /// <summary>A bag with a single roll that always hits and a single item section, in the shipped format.</summary>
    private const string SimpleBag = """
        2
           100        0           1
        end

        3
           0          10000      *
        end

        4
           0          5         10000         0             1              1              1    1    1    1    1
        end

        5
           14,013      0             0            *         *         *         *         *         *         //Jewel of Bless
           14,014      0             0            *         *         *         *         *         *         //Jewel of Soul
        end
        """;

    private string _folder = null!;
    private ItemBalanceTable _items = null!;
    private ItemBagItemMaker _maker = null!;

    [SetUp]
    public void SetUp()
    {
        ItemCombatMath.Options = LoadRules();
        _folder = Path.Combine(Path.GetTempPath(), $"bags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);

        _items = Items();
        _items.Add(new ItemBalance { Index = Item.GetItem(14, 13), Section = 14, Sub = 13, Slot = -1, Durability = 1 });
        _items.Add(new ItemBalance { Index = Item.GetItem(14, 14), Section = 14, Sub = 14, Slot = -1, Durability = 1 });

        var optionRates = new ItemOptionRateTable();
        WriteTemp(OptionRates, path => optionRates.Load(path));

        var excellent = new ExcellentOptionRateTable();

        foreach (int bit in new[] { 1, 2, 4, 8, 16, 32 })
        {
            excellent.Add(new ExcellentOptionRow(bit, Item.GetItem(0, 0), Item.GetItem(4, 31), "weapon", 50));
        }

        _maker = new ItemBagItemMaker(_items, optionRates, excellent);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static void WriteTemp(string text, Action<string> use)
    {
        string path = Path.Combine(Path.GetTempPath(), $"bag-{Guid.NewGuid():N}.txt");
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

    private ItemBag LoadBag(string text)
    {
        ItemBag? bag = null;
        WriteTemp(text, path => bag = ItemBag.Load(path));
        Assert.That(bag, Is.Not.Null);
        return bag!;
    }

    // ---------------------------------------------------------------- reading and rolling one bag

    [Test]
    public void A_bag_file_is_read_in_the_shipped_format()
    {
        var bag = LoadBag(SimpleBag);

        Assert.That(bag.ItemRate, Is.EqualTo(100));
        Assert.That(bag.MoneyDrop, Is.EqualTo(0));
        Assert.That(bag.BagUseEffect, Is.EqualTo(1));
        Assert.That(bag.GroupCount, Is.EqualTo(1));
        Assert.That(bag.ItemCount, Is.EqualTo(2));

        var roll = bag.Drop(playerClass: 1, changeUp: 0, map: 0, _maker, new Random(1));

        Assert.That(roll.UseEffect, Is.EqualTo(1));
        Assert.That(roll.Loot, Has.Count.EqualTo(1));
        Assert.That(roll.Loot[0].Item!.Index, Is.AnyOf(Item.GetItem(14, 13), Item.GetItem(14, 14)));
        Assert.That(roll.Loot[0].Scatter, Is.False, "the only drop of the first roll lands on the monster");
        Assert.That(roll.Loot[0].Item!.Durability, Is.EqualTo(1));
    }

    [Test]
    public void The_drop_rows_are_picked_by_the_class_and_its_evolution()
    {
        var bag = LoadBag("""
            2
               100   0   0
            end
            3
               0     10000   *
            end
            4
               0     5   10000   0   1   1     0   1   0   0   0     // Dark Knight only
               0     6   10000   0   1   1     1   0   0   0   0     // Dark Wizard, any evolution
               0     7   10000   0   1   1     2   0   0   0   0     // Dark Wizard, second evolution only
            end
            5
               00,000   0   0   *   *   *   *   *   *    // Kris
            end
            6
               08,000   0   0   *   *   *   *   *   *    // Bronze Armor
            end
            7
               12,003   0   0   *   *   *   *   *   *    // Wings of Spirit
            end
            """);

        var knight = bag.Drop(playerClass: 1, changeUp: 0, map: 0, _maker, new Random(3));
        Assert.That(knight.Loot.Select(l => (int)l.Item!.Index), Is.EquivalentTo(new[] { Kris }));

        for (int i = 0; i < 50; i++)
        {
            var wizard = bag.Drop(playerClass: 0, changeUp: 0, map: 0, _maker, new Random(i));
            Assert.That((int)wizard.Loot.Single().Item!.Index, Is.EqualTo(BronzeArmor));

            var evolved = bag.Drop(playerClass: 0, changeUp: 1, map: 0, _maker, new Random(i)).Loot.Single().Item!;
            Assert.That((int)evolved.Index, Is.AnyOf(BronzeArmor, WingsOfSpirit));
        }

        var nobody = bag.Drop(playerClass: 2, changeUp: 0, map: 0, _maker, new Random(1));
        Assert.That(nobody.Loot, Is.Empty, "no row fits an Elf");
    }

    [Test]
    public void A_roll_that_misses_or_is_for_another_map_drops_nothing()
    {
        var bag = LoadBag("""
            2
               100   0   0
            end
            3
               0     0       *
               1     10000   3
            end
            4
               0     5   10000   0   1   1     1   1   1   1   1
               1     5   10000   0   1   1     1   1   1   1   1
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            """);

        Assert.That(bag.Drop(1, 0, map: 0, _maker, new Random(1)).Loot, Is.Empty, "roll 0 never hits and roll 1 is for map 3");

        var onMap3 = bag.Drop(1, 0, map: 3, _maker, new Random(1));
        Assert.That(onMap3.Loot, Has.Count.EqualTo(1));
        Assert.That(onMap3.Loot[0].Scatter, Is.True, "a drop of a roll that is not the first is scattered");
    }

    [Test]
    public void Several_drops_are_all_scattered_and_each_takes_its_own_item()
    {
        var bag = LoadBag("""
            2
               100   0   0
            end
            3
               0     10000   *
            end
            4
               0     5   10000   0   2   3     1   1   1   1   1
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            """);

        // group roll, drop row, count (2 + 1 = 3), then per drop: set roll, item-or-zen roll, item
        var roll = bag.Drop(1, 0, 0, _maker, new ScriptedRandom(0, 0, 1, 9999, 0, 0, 9999, 0, 0, 9999, 0, 0));

        Assert.That(roll.Loot, Has.Count.EqualTo(3));
        Assert.That(roll.Loot.All(l => l.Scatter && l.Item != null), Is.True);
    }

    [Test]
    public void The_item_rate_decides_between_an_item_and_zen()
    {
        var bag = LoadBag("""
            2
               50   500   0
            end
            3
               0     10000   *
            end
            4
               0     5   10000   0   1   1     1   1   1   1   1
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            """);

        // group roll, drop row, count, set roll, then the 0-99 roll: 49 is an item, 50 is zen
        var item = bag.Drop(1, 0, 0, _maker, new ScriptedRandom(0, 0, 0, 9999, 49, 0));
        Assert.That(item.Loot.Single().Item, Is.Not.Null);

        var zen = bag.Drop(1, 0, 0, _maker, new ScriptedRandom(0, 0, 0, 9999, 50));
        Assert.That(zen.Loot.Single().Item, Is.Null);
        Assert.That(zen.Loot.Single().Money, Is.EqualTo(500));
    }

    [Test]
    public void A_set_drop_is_marked_and_makes_no_item()
    {
        var bag = LoadBag("""
            2
               100   0   0
            end
            3
               0     10000   *
            end
            4
               0     5    10000   10000   1   1     1   1   1   1   1     // always a set item
               0     -1   10000   0       1   1     0   0   0   0   0     // a section of -1 is a set item too
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            """);

        var first = bag.Drop(1, 0, 0, _maker, new ScriptedRandom(0, 0, 0, 0));
        var loot = first.Loot.Single();
        Assert.That(loot.SetItem, Is.True);
        Assert.That(loot.Item, Is.Null);
        Assert.That(loot.Money, Is.EqualTo(0));
    }

    [Test]
    public void Get_item_takes_the_first_roll_that_hits_and_ignores_the_map()
    {
        var bag = LoadBag("""
            2
               100   0   0
            end
            3
               0     0       3
               1     10000   3
            end
            4
               0     5   10000   0   1   1     1   1   1   1   1
               1     6   10000   0   1   1     1   1   1   1   1
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            6
               08,000   0   0   *   *   *   *   *   *
            end
            """);

        Assert.That((int?)bag.GetItem(1, 0, _maker, new Random(1))?.Index, Is.EqualTo(BronzeArmor), "roll 0 misses, roll 1 gives section 6");

        var none = LoadBag("""
            2
               100   0   0
            end
            3
               0     10000   *
            end
            4
               0     5   10000   0   1   1     0   0   0   0   0
            end
            5
               00,000   0   0   *   *   *   *   *   *
            end
            """);

        Assert.That(none.GetItem(1, 0, _maker, new Random(1)), Is.Null, "the roll hits but no drop row fits the class");
    }

    // ---------------------------------------------------------------- the items a bag makes

    [Test]
    public void Bag_items_get_their_options_from_the_option_rate_rows()
    {
        // Level row 3 (+2), skill row 0, luck row 1, additional row 2 (+12), two excellent options (row 4).
        var entry = new ItemBagItem(Kris, 0, 0, new[] { 3, 0, 1, 2, 4, -1 });
        var item = _maker.Make(entry, new Random(1));

        Assert.That(item.Level, Is.EqualTo(2));
        Assert.That(item.Option1, Is.EqualTo(1));
        Assert.That(item.Option2, Is.EqualTo(1));
        Assert.That(item.Option3, Is.EqualTo(3));
        Assert.That(System.Numerics.BitOperations.PopCount(item.NewOption), Is.EqualTo(2));
        Assert.That(item.Durability, Is.EqualTo(37), "full durability: the item's 20, one per level and 15 for an excellent item");
    }

    [Test]
    public void A_star_leaves_the_base_level_and_no_options()
    {
        var item = _maker.Make(new ItemBagItem(Kris, 4, 0, new[] { -1, -1, -1, -1, -1, -1 }), new Random(1));

        Assert.That(item.Level, Is.EqualTo(4));
        Assert.That(item.Option1 + item.Option2 + item.Option3 + item.NewOption, Is.EqualTo(0));
    }

    // ---------------------------------------------------------------- the manager

    private ItemBagManager LoadManager(string managerText, params (string Path, string Text)[] bags)
    {
        foreach (var (path, text) in bags)
        {
            string file = Path.Combine(_folder, path.Replace('\\', Path.DirectorySeparatorChar) + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }

        string managerFile = Path.Combine(_folder, "EventItemBagManager.txt");
        File.WriteAllText(managerFile, managerText);

        var manager = new ItemBagManager(_maker);
        manager.Load(managerFile, _folder);
        return manager;
    }

    private const string ManagerText = """
        //ItemIndex   ItemLevel   MonsterClass   SpecialValue   SpecialLevel   SpecialState   BagPath
        459           0           *              *              *              *              "Item\Box"
        459           3           *              *              *              *              "Item\Heart"
        *             *           42             *              *              *              "Monster\Red Dragon"
        *             *           42             *              *              *              "Monster\Second Row"
        *             *           *              0              1              *              "Special\Wing 1 Mix"
        *             *           *              1              *              1              "Special\Devil Square"
        *             *           77             *              *              *              "Monster\Missing"
        end
        """;

    [Test]
    public void The_manager_finds_bags_by_monster_item_and_event_and_the_first_row_wins()
    {
        var manager = LoadManager(ManagerText,
            ("Item\\Box", SimpleBag), ("Item\\Heart", SimpleBag), ("Monster\\Red Dragon", SimpleBag),
            ("Monster\\Second Row", SimpleBag.Replace("14,013", "14,016")), ("Special\\Wing 1 Mix", SimpleBag),
            ("Special\\Devil Square", SimpleBag));

        Assert.That(manager.Count, Is.EqualTo(7));

        Assert.That(manager.DropByMonsterClass(42, 1, 0, 0, new Random(1))!.Loot, Has.Count.EqualTo(1));
        Assert.That(manager.DropByMonsterClass(43, 1, 0, 0, new Random(1)), Is.Null, "no bag for that monster");

        Assert.That(manager.HasItemBag(459, 0), Is.True);
        Assert.That(manager.HasItemBag(459, 3), Is.True);
        Assert.That(manager.HasItemBag(459, 4), Is.False, "the rows name levels 0 and 3");
        Assert.That(manager.DropByItemIndex(459, 4, 1, 0, 0, new Random(1)), Is.Null);
        Assert.That(manager.DropByItemIndex(459, 0, 1, 0, 0, new Random(1))!.Loot, Has.Count.EqualTo(1));

        Assert.That(manager.HasSpecial(ItemBagManager.SpecialChaosBox, ItemBagManager.ChaosWing1Mix), Is.True);
        Assert.That(manager.HasSpecial(ItemBagManager.SpecialChaosBox, ItemBagManager.ChaosWing2Mix), Is.False);
        Assert.That(manager.GetItemBySpecialValue(0, 1, -1, 1, 0, new Random(1)), Is.Not.Null);
        Assert.That(manager.GetItemBySpecialValue(0, 2, -1, 1, 0, new Random(1)), Is.Null);

        // A special level of * and a state of 1: any Devil Square level, but only a state of 1.
        Assert.That(manager.DropBySpecialValue(1, 3, 1, 1, 0, 0, new Random(1))!.Loot, Has.Count.EqualTo(1));
        Assert.That(manager.DropBySpecialValue(1, 3, 0, 1, 0, 0, new Random(1)), Is.Null);
    }

    [Test]
    public void A_bag_listed_but_missing_on_disk_gives_nothing_without_failing()
    {
        var manager = LoadManager(ManagerText, ("Item\\Box", SimpleBag));

        var roll = manager.DropByMonsterClass(77, 1, 0, 0, new Random(1));
        Assert.That(roll, Is.Not.Null, "the original still counts the monster as having a bag");
        Assert.That(roll!.Loot, Is.Empty);

        Assert.That(manager.DropByMonsterClass(42, 1, 0, 0, new Random(1))!.Loot, Is.Empty, "its file is missing as well");
    }

    // ---------------------------------------------------------------- the Chaos Machine

    private static PlayerObject Knight()
    {
        var player = DarkKnight();
        player.Money = 5_000_000;
        return player;
    }

    [Test]
    public void The_chaos_item_mix_takes_its_result_from_the_bag()
    {
        var manager = LoadManager("""
            *   *   *   0   0   *   "Special\Chaos Item Mix"
            end
            """,
            ("Special\\Chaos Item Mix", """
                2
                   100   0   0
                end
                3
                   0     10000   *
                end
                4
                   0     5   10000   0   1   1     1   1   1   1   1
                end
                5
                   05,000   0   0   *   *   *   *   *   *   // a Skull Staff: not one of the built-in results
                end
                """));

        var rates = new GameServerInfoChaosMix();
        rates.ChaosItemMixRate[0] = 100;

        foreach (var withBags in new ItemBagManager?[] { manager, null })
        {
            var player = Knight();
            player.ChaosBoxItems[0] = Make(Item.GetItem(12, 15), durability: 1);
            player.ChaosBoxItems[1] = Make(Kris, level: 4, additional: 1);
            player.InChaosBox = true;

            var result = ChaosMixLogic.CalculateAndExecuteMix(
                player, ChaosMixType.ChaosItem, rates, new int[4], _ => 1_000_000, execute: true, bags: withBags);

            Assert.That(result.Success, Is.True);

            if (withBags != null)
            {
                Assert.That((int)result.Item!.Index, Is.EqualTo(SkullStaff), "the bag's item");
            }
            else
            {
                Assert.That((int)result.Item!.Index, Is.AnyOf(Item.GetItem(2, 6), Item.GetItem(4, 6), Item.GetItem(5, 7)), "the built-in list");
            }
        }
    }
}

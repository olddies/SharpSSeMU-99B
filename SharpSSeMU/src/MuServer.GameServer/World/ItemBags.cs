using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>One item of a bag section (a row of section 5 and up of a bag file): the item, its base level and the
/// rows of ItemOptionRate.txt (level, skill, luck, additional, excellent count, set) its options are rolled from;
/// -1 (the file's "*") means "no row", the base value is kept.</summary>
public sealed record ItemBagItem(int ItemIndex, int Level, int Grade, int[] Options);

/// <summary>One row of section 4 of a bag file: which item section a drop takes from, how likely that is among the
/// rows that fit, how many items it drops and which classes it applies to.</summary>
public sealed record ItemBagDropInfo(
    int Section, int SectionRate, int SetDropRate, int MinDropCount, int MaxDropCount, int[] RequireClass);

/// <summary>One row of section 3 of a bag file: a roll of the bag (out of 10000), the map it works on (-1 = all) and
/// the drop rows it picks from.</summary>
public sealed class ItemBagGroup
{
    public required int Index { get; init; }
    public required int DropRate { get; init; }
    public required int MapNumber { get; init; }
    public List<ItemBagDropInfo> DropInfo { get; } = new();
}

/// <summary>One thing that came out of a bag.</summary>
/// <param name="Item">The item, or null for zen and for a random set item (the caller makes that one).</param>
/// <param name="Money">The zen of a zen drop.</param>
/// <param name="SetItem">True for a drop that is a random set item (CSetItemType::MakeRandomSetItem).</param>
/// <param name="Scatter">True when the original looks for a free spot around the monster instead of dropping on it.</param>
public sealed record BagLoot(Item? Item, long Money, bool SetItem, bool Scatter);

/// <summary>What a bag gave: the things to drop and the visual effect of the bag (0 none, 1/2/3 server commands).</summary>
public sealed class BagRoll
{
    public List<BagLoot> Loot { get; } = new();
    public int UseEffect { get; init; }
}

/// <summary>Builds the items a bag hands out: options rolled from ItemOptionRate.txt, excellent options from
/// ExcellentOptionRate.txt. Shared by the bags.</summary>
public sealed class ItemBagItemMaker
{
    private readonly ItemBalanceTable _items;
    private readonly ItemOptionRateTable _optionRates;
    private readonly ExcellentOptionRateTable _excellent;
    private readonly SetItemRules _sets;

    public ItemBagItemMaker(
        ItemBalanceTable items, ItemOptionRateTable optionRates, ExcellentOptionRateTable excellent, SetItemRules? sets = null)
    {
        _items = items;
        _optionRates = optionRates;
        _excellent = excellent;
        _sets = sets ?? SetItemRules.Current;
    }

    /// <summary>The excellent options a mix gives its item: <c>GetItemOption4(row, &amp;n)</c> picks how many from
    /// that row of the excellent-count rates, then <c>MakeNewOption(item, 1, n)</c> picks which. 0 when the row
    /// is missing.</summary>
    public byte MakeExcellentFromRow(int itemIndex, int row, Random rng)
    {
        int count = _optionRates.Pick(ItemOptionRateTable.ExcellentCount, row, rng) ?? 0;
        return _excellent.MakeNewOption(itemIndex, 1, count, rng);
    }

    /// <summary>The body of CItemBagEx::DropItem / GetItem that turns a bag row into an item.</summary>
    public Item Make(ItemBagItem entry, Random rng)
    {
        int level = entry.Level;

        if (_optionRates.Pick(ItemOptionRateTable.LevelOption, entry.Options[0], rng) is int l) level = l;
        int skill = _optionRates.Pick(ItemOptionRateTable.SkillOption, entry.Options[1], rng) ?? 0;
        int luck = _optionRates.Pick(ItemOptionRateTable.LuckOption, entry.Options[2], rng) ?? 0;
        int additional = _optionRates.Pick(ItemOptionRateTable.AdditionalOption, entry.Options[3], rng) ?? 0;
        int excellentCount = _optionRates.Pick(ItemOptionRateTable.ExcellentCount, entry.Options[4], rng) ?? 0;

        int setValue = _optionRates.Pick(ItemOptionRateTable.SetOption, entry.Options[5], rng) ?? 0;
        int excellent = _excellent.MakeNewOption(entry.ItemIndex, 1, excellentCount, rng);

        var item = new Item
        {
            Index = (short)entry.ItemIndex,
            Level = (byte)level,
            Option1 = (byte)skill,
            Option2 = (byte)luck,
            Option3 = (byte)additional,
            NewOption = (byte)(excellent == 0 ? entry.Grade : excellent),
            SetOption = _sets.Types.MakeSetOption(entry.ItemIndex, setValue, rng),
        };

        ItemCombatMath.Options.Normalize(item);
        item.Durability = LootRoller.FullDurability(_items, item, 0);
        return item;
    }
}

/// <summary> Port of CItemBagEx (ItemBagEx.cpp): one bag file of Data/EventItemBag. A bag has a few rolls
/// (section 3); each picks, among the drop rows that fit the player's class, an item section (section 4) and drops
/// a random number of items from it (sections 5 and up). Section 2 gives the chance of an item against zen and the
/// effect shown. The classic format of ItemBag.cpp (sections 0 and 1) is not read: none of the shipped bags use it.
/// </summary>
public sealed class ItemBag
{
    private const int ClassCount = 5;

    private readonly SortedDictionary<int, ItemBagGroup> _groups = new();
    private readonly Dictionary<int, List<ItemBagItem>> _items = new();

    /// <summary>Of 100: how many drops are an item; the rest are <see cref="MoneyDrop"/> zen.</summary>
    public int ItemRate { get; private set; }
    public int MoneyDrop { get; private set; }

    /// <summary>1 and 2: a server command at the player (fireworks); 3: another one (see ClientProtocolHandler).</summary>
    public int BagUseEffect { get; private set; }

    public int GroupCount => _groups.Count;
    public int ItemCount => _items.Values.Sum(list => list.Count);

    public static ItemBag? Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemBag] {0}", script.GetLastError());
            return null;
        }

        var bag = new ItemBag();

        while (script.GetToken() != TokenResult.End)
        {
            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End || script.GetString() == "end")
                {
                    break;
                }

                if (section == 2)
                {
                    bag.ItemRate = script.GetNumber();
                    bag.MoneyDrop = script.GetAsNumber();
                    bag.BagUseEffect = script.GetAsNumber();
                }
                else if (section == 3)
                {
                    int index = script.GetNumber();
                    int dropRate = script.GetAsNumber();
                    int map = script.GetAsNumber();

                    // Like std::map::insert, the first row of an index wins.
                    bag._groups.TryAdd(index, new ItemBagGroup { Index = index, DropRate = dropRate, MapNumber = map });
                }
                else if (section == 4)
                {
                    int index = script.GetNumber();
                    int itemSection = script.GetAsNumber();
                    int sectionRate = script.GetAsNumber();
                    int setDropRate = script.GetAsNumber();
                    int min = script.GetAsNumber();
                    int max = script.GetAsNumber();
                    var classes = new int[ClassCount];

                    for (int n = 0; n < classes.Length; n++)
                    {
                        classes[n] = script.GetAsNumber();
                    }

                    // A row of a bag number that has no section 3 row is ignored.
                    if (bag._groups.TryGetValue(index, out var group))
                    {
                        group.DropInfo.Add(new ItemBagDropInfo(itemSection, sectionRate, setDropRate, min, max, classes));
                    }
                }
                else if (section >= 5)
                {
                    int itemSection = script.GetNumber();
                    script.GetToken(); // ','
                    int itemSub = script.GetAsNumber();
                    int level = script.GetAsNumber();
                    int grade = script.GetAsNumber();
                    var options = new int[6];

                    for (int n = 0; n < options.Length; n++)
                    {
                        options[n] = script.GetAsNumber();
                    }

                    if (!bag._items.TryGetValue(section, out var list))
                    {
                        bag._items[section] = list = new List<ItemBagItem>();
                    }

                    list.Add(new ItemBagItem(Item.GetItem(itemSection, itemSub), level, grade, options));
                }
                // Sections 0 and 1 (the classic format) are skipped up to their "end".
            }
        }

        return bag;
    }

    /// <summary>The drop rows of <paramref name="group"/> that apply to this class and evolution.</summary>
    private static List<ItemBagDropInfo> EligibleDrops(ItemBagGroup group, int playerClass, int changeUp)
    {
        var list = new List<ItemBagDropInfo>();

        if (playerClass is < 0 or >= ClassCount)
        {
            return list;
        }

        foreach (var info in group.DropInfo)
        {
            int required = info.RequireClass[playerClass];

            if (required != 0 && required <= changeUp + 1)
            {
                list.Add(info);
            }
        }

        return list;
    }

    private static ItemBagDropInfo? PickDrop(List<ItemBagDropInfo> eligible, Random rng)
    {
        return WeightedPick.Pick(eligible.Select((info, n) => (n, info.SectionRate)), rng) is int picked ? eligible[picked] : null;
    }

    /// <summary>Port of CItemBagEx::GetItem: one item from the first roll that hits, no map check (used by the
    /// Chaos Machine and similar). Null when no roll hits or the hit has nothing to give.</summary>
    public Item? GetItem(int playerClass, int changeUp, ItemBagItemMaker maker, Random rng)
    {
        foreach (var group in _groups.Values)
        {
            if (rng.Next(10000) >= group.DropRate)
            {
                continue;
            }

            var chosen = PickDrop(EligibleDrops(group, playerClass, changeUp), rng);

            if (chosen == null || !_items.TryGetValue(chosen.Section, out var list) || list.Count == 0)
            {
                return null;
            }

            return maker.Make(list[rng.Next(list.Count)], rng);
        }

        return null;
    }

    /// <summary>Port of CItemBagEx::DropItem: everything this bag drops for a player on <paramref name="map"/>.</summary>
    public BagRoll Drop(int playerClass, int changeUp, int map, ItemBagItemMaker maker, Random rng)
    {
        var roll = new BagRoll { UseEffect = BagUseEffect };
        int firstGroup = _groups.Count == 0 ? -1 : _groups.Keys.First();

        foreach (var group in _groups.Values)
        {
            if (group.MapNumber != -1 && group.MapNumber != map)
            {
                continue;
            }

            if (rng.Next(10000) >= group.DropRate)
            {
                continue;
            }

            var chosen = PickDrop(EligibleDrops(group, playerClass, changeUp), rng);

            if (chosen == null)
            {
                continue;
            }

            int count = chosen.MinDropCount + rng.Next(Math.Max(chosen.MaxDropCount - chosen.MinDropCount + 1, 1));

            for (int n = 0; n < count; n++)
            {
                // The first drop of the first roll lands on the monster (unless that spot cannot hold an item);
                // everything else is scattered around it.
                bool scatter = count > 1 || group.Index != firstGroup;

                if (rng.Next(10000) < chosen.SetDropRate || chosen.Section == -1)
                {
                    roll.Loot.Add(new BagLoot(null, 0, SetItem: true, scatter));
                    continue;
                }

                if (rng.Next(100) >= ItemRate)
                {
                    roll.Loot.Add(new BagLoot(null, MoneyDrop, SetItem: false, scatter));
                    continue;
                }

                if (!_items.TryGetValue(chosen.Section, out var list) || list.Count == 0)
                {
                    continue;
                }

                roll.Loot.Add(new BagLoot(maker.Make(list[rng.Next(list.Count)], rng), 0, SetItem: false, scatter));
            }
        }

        return roll;
    }
}

/// <summary>One row of EventItemBagManager.txt: which bag a monster, an item or an event uses.</summary>
public sealed record ItemBagManagerRow(
    int ItemIndex, int ItemLevel, int MonsterClass, int SpecialValue, int SpecialLevel, int SpecialState, string BagPath, ItemBag? Bag);

/// <summary> Port of CItemBagManager (ItemBagManager.cpp): Data/EventItemBagManager.txt says which bag file in
/// Data/EventItemBag a monster class, a dropped/opened item or a special event (the Chaos Machine, Devil Square,
/// Blood Castle, Chaos Castle) uses. The first row that matches wins. </summary>
public sealed class ItemBagManager
{
    /// <summary>The special values of ITEM_BAG_SPECIAL_*.</summary>
    public const int SpecialChaosBox = 0, SpecialDevilSquare = 1, SpecialBloodCastle = 2, SpecialChaosCastle = 3;

    /// <summary>The special levels of the Chaos Machine bags.</summary>
    public const int ChaosItemMix = 0, ChaosWing1Mix = 1, ChaosWing2Mix = 2, ChaosCapeMix = 3;

    private readonly List<ItemBagManagerRow> _rows = new();
    private readonly ItemBagItemMaker _maker;

    /// <summary>The item maker the bags use; the mixes that roll excellent options borrow it.</summary>
    public ItemBagItemMaker Maker => _maker;

    public ItemBagManager(ItemBagItemMaker maker)
    {
        _maker = maker;
    }

    public int Count => _rows.Count;

    /// <summary>Adds a row (used by tests and by <see cref="Load"/>).</summary>
    public void Add(ItemBagManagerRow row) => _rows.Add(row);

    /// <summary>Reads <paramref name="managerPath"/> and every bag file it names under <paramref name="bagFolder"/>.
    /// Returns the number of rows read.</summary>
    public int Load(string managerPath, string bagFolder)
    {
        var script = new MemScript();

        if (!script.SetBuffer(managerPath))
        {
            Log.Add(LogColor.Red, "[ItemBagManager] {0}", script.GetLastError());
            return 0;
        }

        int missing = 0;

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int itemIndex = script.GetNumber();
            int itemLevel = script.GetAsNumber();
            int monsterClass = script.GetAsNumber();
            int specialValue = script.GetAsNumber();
            int specialLevel = script.GetAsNumber();
            int specialState = script.GetAsNumber();
            string bagPath = script.GetAsString();

            string file = Path.Combine(bagFolder, bagPath.Replace('\\', Path.DirectorySeparatorChar) + ".txt");
            ItemBag? bag = File.Exists(file) ? ItemBag.Load(file) : null;

            if (bag == null)
            {
                missing++;
                Log.Add(LogColor.Red, "[ItemBagManager] bag file {0} not found", file);
            }

            _rows.Add(new ItemBagManagerRow(itemIndex, itemLevel, monsterClass, specialValue, specialLevel, specialState, bagPath, bag));
        }

        Log.Add(LogColor.Blue, "[ItemBagManager] {0} bag(s) listed in {1}, {2} missing", _rows.Count, managerPath, missing);
        return _rows.Count;
    }

    /// <summary>Port of DropItemByMonsterClass: the bag of a boss. Null when the monster has none.</summary>
    public BagRoll? DropByMonsterClass(int monsterClass, int playerClass, int changeUp, int map, Random rng)
    {
        var row = _rows.FirstOrDefault(r => r.MonsterClass == monsterClass);
        return row == null ? null : DropOrEmpty(row, playerClass, changeUp, map, rng);
    }

    /// <summary>Port of DropItemByItemIndex: the bag an item opens when it is thrown on the ground (Box of Luck,
    /// the Kundun boxes, the medals, the Starter Rings). Null when the item has none.</summary>
    public BagRoll? DropByItemIndex(int itemIndex, int itemLevel, int playerClass, int changeUp, int map, Random rng)
    {
        var row = _rows.FirstOrDefault(r => r.ItemIndex == itemIndex && (r.ItemLevel == -1 || r.ItemLevel == itemLevel));
        return row == null ? null : DropOrEmpty(row, playerClass, changeUp, map, rng);
    }

    /// <summary>Port of DropItemBySpecialValue: the bag of an event (Devil Square, Blood Castle, Chaos Castle).</summary>
    public BagRoll? DropBySpecialValue(int value, int level, int state, int playerClass, int changeUp, int map, Random rng)
    {
        var row = FindSpecial(value, level, state);
        return row == null ? null : DropOrEmpty(row, playerClass, changeUp, map, rng);
    }

    /// <summary>Port of GetItemBySpecialValue: one item from the bag of a Chaos Machine mix. Null when there is no
    /// such bag or nothing came out.</summary>
    public Item? GetItemBySpecialValue(int value, int level, int state, int playerClass, int changeUp, Random rng)
    {
        return FindSpecial(value, level, state)?.Bag?.GetItem(playerClass, changeUp, _maker, rng);
    }

    /// <summary>True when the manager has a bag for this special value and level.</summary>
    public bool HasSpecial(int value, int level, int state = -1) => FindSpecial(value, level, state)?.Bag != null;

    /// <summary>True when an item thrown on the ground opens a bag.</summary>
    public bool HasItemBag(int itemIndex, int itemLevel) =>
        _rows.Any(r => r.ItemIndex == itemIndex && (r.ItemLevel == -1 || r.ItemLevel == itemLevel));

    private ItemBagManagerRow? FindSpecial(int value, int level, int state) =>
        _rows.FirstOrDefault(r => r.SpecialValue == value
            && (r.SpecialLevel == -1 || r.SpecialLevel == level)
            && (r.SpecialState == -1 || r.SpecialState == state));

    private BagRoll DropOrEmpty(ItemBagManagerRow row, int playerClass, int changeUp, int map, Random rng) =>
        row.Bag?.Drop(playerClass, changeUp, map, _maker, rng) ?? new BagRoll();
}

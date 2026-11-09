using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>Port of CRandomManager: a weighted pick. Each option takes a slice of the total proportional to its
/// weight; options with a weight of 0 are never picked and nothing is picked when every weight is 0.</summary>
public static class WeightedPick
{
    public static int? Pick(IEnumerable<(int Value, int Weight)> options, Random rng)
    {
        int total = 0;
        var slices = new List<(int Value, int End)>();

        foreach (var (value, weight) in options)
        {
            if (weight <= 0)
            {
                continue;
            }

            total += weight;
            slices.Add((value, total));
        }

        if (total == 0)
        {
            return null;
        }

        int roll = rng.Next(total);

        foreach (var (value, end) in slices)
        {
            if (roll < end)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary> Port of CItemOptionRate (ItemOptionRate.cpp): Data/Item/ItemOptionRate.txt holds, for each kind of
/// option, numbered rate rows ("Index  Rate0 Rate1 ..."): how likely each value of the option is. The kinds are
/// 0 = item level (14 values), 1 = skill (2), 2 = luck (2), 3 = additional option (8), 4 = how many excellent
/// options (7) and 5 = set option (7). A drop or a mix names the row it uses; picking from it returns the
/// position that came out, which is the value itself (level +2, two excellent options...). </summary>
public sealed class ItemOptionRateTable
{
    public const int LevelOption = 0, SkillOption = 1, LuckOption = 2, AdditionalOption = 3, ExcellentCount = 4, SetOption = 5;

    private static readonly int[] RateCounts = { 14, 2, 2, 8, 7, 7 };

    private readonly Dictionary<int, int[]>[] _sections =
        Enumerable.Range(0, 6).Select(_ => new Dictionary<int, int[]>()).ToArray();

    public int Count => _sections.Sum(s => s.Count);

    /// <summary>Adds a row; like the original's std::map::insert, the first row of an index wins.</summary>
    public void Add(int section, int index, int[] rates)
    {
        if (section is >= 0 and < 6)
        {
            _sections[section].TryAdd(index, rates);
        }
    }

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemOptionRateTable] {0}", script.GetLastError());
            return 0;
        }

        while (script.GetToken() != TokenResult.End)
        {
            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End || script.GetString() == "end")
                {
                    break;
                }

                if (section is < 0 or > 5)
                {
                    continue; // unknown section: skip its rows up to the "end"
                }

                int index = script.GetNumber();
                var rates = new int[RateCounts[section]];

                for (int n = 0; n < rates.Length; n++)
                {
                    rates[n] = script.GetAsNumber();
                }

                Add(section, index, rates);
            }
        }

        Log.Add(LogColor.Blue, "[ItemOptionRateTable] {0} option rate row(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>The value picked from row <paramref name="index"/> of <paramref name="section"/>, or null when the
    /// row does not exist (the caller keeps its default) or has no rate at all.</summary>
    public int? Pick(int section, int index, Random rng)
    {
        if (section is < 0 or > 5 || !_sections[section].TryGetValue(index, out var rates))
        {
            return null;
        }

        return WeightedPick.Pick(rates.Select((rate, value) => (value, rate)), rng);
    }
}

/// <summary>One row of Data/Item/ExcellentOptionRate.txt: an excellent option (one bit of the item's excellent
/// byte), the items it can appear on and how likely it is among them.</summary>
public sealed record ExcellentOptionRow(int Bit, int MinItem, int MaxItem, string Name, int Rate);

/// <summary> Port of CExcellentOptionRate (ExcellentOptionRate.cpp) and CItemOptionRate::MakeNewOption: picks which
/// excellent options an item gets, given how many it should have. The options of an item are the rows whose item
/// range covers it, weighted by their rate. </summary>
public sealed class ExcellentOptionRateTable
{
    private const int MaxExcellentOptions = 6;
    private readonly List<ExcellentOptionRow> _rows = new();

    public int Count => _rows.Count;

    public void Add(ExcellentOptionRow row) => _rows.Add(row);

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ExcellentOptionRateTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int bit = script.GetNumber();
            int min = ReadItemIndex(script);
            int max = ReadItemIndex(script);
            string name = script.GetAsString();
            int rate = script.GetAsNumber();
            _rows.Add(new ExcellentOptionRow(bit, min, max, name, rate));
        }

        Log.Add(LogColor.Blue, "[ExcellentOptionRateTable] {0} excellent option row(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>"section,sub" as one item index; "*" gives -1. The comma is a token of its own.</summary>
    private static int ReadItemIndex(MemScript script)
    {
        int section = script.GetAsNumber();

        if (section < 0)
        {
            return -1;
        }

        script.GetToken();
        int sub = script.GetAsNumber();
        return sub < 0 ? -1 : Item.GetItem(section, sub);
    }

    /// <summary>One excellent option for the item (its bit), or 0 when none of the rows covers it.</summary>
    public int GetNewOption(int itemIndex, Random rng)
    {
        var candidates = _rows.Where(r => (r.MinItem == -1 || r.MinItem <= itemIndex) && (r.MaxItem == -1 || r.MaxItem >= itemIndex))
            .Select(r => (r.Bit, r.Rate));

        return WeightedPick.Pick(candidates, rng) ?? 0;
    }

    /// <summary>The most excellent options an item can carry: all six, three for the second wings and the Horn of
    /// Dinorant, four for the Cape of Lord.</summary>
    public static int MaxOptionsFor(int itemIndex)
    {
        if ((itemIndex >= Item.GetItem(12, 3) && itemIndex <= Item.GetItem(12, 6)) || itemIndex == Item.GetItem(13, 30))
        {
            return itemIndex == Item.GetItem(13, 30) ? 4 : 3;
        }

        return itemIndex == Item.GetItem(13, 3) ? 3 : MaxExcellentOptions;
    }

    /// <summary>Port of CItemOptionRate::MakeNewOption. <paramref name="mode"/> 0: <paramref name="value"/> is the
    /// option bits themselves; 1: <paramref name="value"/> is how many options to roll; 2: a random number of them,
    /// from 0 to value-1.</summary>
    public byte MakeNewOption(int itemIndex, int mode, int value, Random rng)
    {
        int option = 0;

        if (mode == 0)
        {
            return (byte)value;
        }

        if (mode == 2)
        {
            value = value > 0 ? rng.Next(value) : 0;
        }

        int max = MaxOptionsFor(itemIndex);
        int count = 0;

        // The original loops until it has the count; here a row set with fewer distinct options than asked for
        // (a hand-edited file) cannot spin the server forever.
        for (int attempts = 0; count < value && count < max && attempts < 1000; attempts++)
        {
            int bit = GetNewOption(itemIndex, rng);

            if ((option & bit) == 0)
            {
                option |= bit;
                count++;
            }
        }

        return (byte)option;
    }
}

/// <summary>The drop-related columns of one map in Data/MapManager.txt.</summary>
/// <param name="NonPk">The NonPK column: -1 (the star) follows the server setting, 0 allows PK, 1 forbids it, 2 allows it
/// without outlaws (a kill makes no murderer).</param>
/// <param name="PkItemDrop">Whether a dying murderer may drop a worn item on this map.</param>
public sealed record MapDropInfo(int ItemDropRate, int MoneyDropRate, int ExcItemDropRate, int SetItemDropRate, int ItemDrop, int NonPk = -1, int PkItemDrop = 0);

/// <summary> Port of the drop part of CMapManager (MapManager.cpp): per map, the item drop rate (percent), the zen
/// drop rate, the chance per million that a drop is excellent or a set item, and whether items drop at all.
/// A map the file does not mention drops at 100%, with no excellent or set items and no item rules. </summary>
public sealed class MapDropTable
{
    private readonly Dictionary<int, MapDropInfo> _maps = new();

    public int Count => _maps.Count;

    public void Add(int map, MapDropInfo info) => _maps.TryAdd(map, info);

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MapDropTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int map = script.GetNumber();
            int nonPk = script.GetAsNumber();
            script.GetAsNumber(); // ViewRange
            script.GetAsNumber(); // ExperienceRate
            int itemDropRate = script.GetAsNumber();
            int moneyDropRate = script.GetAsNumber();
            int excItemDropRate = script.GetAsNumber();
            int setItemDropRate = script.GetAsNumber();
            int itemDrop = script.GetAsNumber();

            int pkItemDrop = script.GetAsNumber();

            // The rest of the row: HP bar, trade / personal shop / chat / party / duel / guild war switches,
            // spawn gate and respawn gate (9 columns), then the name.
            for (int n = 0; n < 9; n++)
            {
                script.GetAsNumber();
            }

            script.GetAsString(); // Name

            Add(map, new MapDropInfo(itemDropRate, moneyDropRate, excItemDropRate, setItemDropRate, itemDrop, nonPk, pkItemDrop));
        }

        Log.Add(LogColor.Blue, "[MapDropTable] {0} map(s) loaded from {1}", Count, path);
        return Count;
    }

    public int ItemDropRate(int map) => _maps.TryGetValue(map, out var m) ? m.ItemDropRate : 100;
    public int MoneyDropRate(int map) => _maps.TryGetValue(map, out var m) ? m.MoneyDropRate : 100;
    public int ExcItemDropRate(int map) => _maps.TryGetValue(map, out var m) ? m.ExcItemDropRate : 0;
    public int SetItemDropRate(int map) => _maps.TryGetValue(map, out var m) ? m.SetItemDropRate : 0;
    public bool ItemDropEnabled(int map) => _maps.TryGetValue(map, out var m) && m.ItemDrop != 0;

    /// <summary>GetMapNonPK: players cannot hurt each other here. The star in the table follows <paramref name="serverNonPk"/>.</summary>
    public bool NonPk(int map, int serverNonPk) =>
        _maps.TryGetValue(map, out var m) ? (m.NonPk == -1 ? serverNonPk != 0 : m.NonPk == 1) : serverNonPk != 0;

    /// <summary>GetMapNonOutlaw: players may fight here but killing makes no murderer.</summary>
    public bool NonOutlaw(int map) => _maps.TryGetValue(map, out var m) && m.NonPk == 2;

    /// <summary>GetMapPKItemDrop: a dying murderer may drop a worn item here.</summary>
    public bool PkItemDrop(int map) => _maps.TryGetValue(map, out var m) && m.PkItemDrop != 0;
}

/// <summary> Port of CMapRateInfo (MapRateInfo.cpp): per map and kind of drop (0 = common, 1 = excellent, 2 = set),
/// which row of ItemOptionRate.txt each option of a dropped item is rolled from. "*" as the map means every map and
/// "*" as an option means none (the item keeps its default). The first row that fits wins. </summary>
public sealed class MapRateInfoTable
{
    public const int DropCommon = 0, DropExcellent = 1, DropSet = 2;

    private readonly List<(int Map, int Type, int[] Options)> _rows = new();

    public int Count => _rows.Count;

    public void Add(int map, int dropType, int[] options) => _rows.Add((map, dropType, options));

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MapRateInfoTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int map = script.GetNumber();
            int type = script.GetAsNumber();
            var options = new int[6];

            for (int n = 0; n < 6; n++)
            {
                options[n] = script.GetAsNumber();
            }

            Add(map, type, options);
        }

        Log.Add(LogColor.Blue, "[MapRateInfoTable] {0} row(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>The ItemOptionRate row that option <paramref name="option"/> (0 level, 1 skill, 2 luck, 3 additional,
    /// 4 excellent count, 5 set) is rolled from on this map for this kind of drop; 0 when no row fits.</summary>
    public int GetOption(int option, int map, int dropType)
    {
        foreach (var (rowMap, type, options) in _rows)
        {
            if (type == dropType && (rowMap == -1 || rowMap == map))
            {
                return options[option];
            }
        }

        return 0;
    }
}

/// <summary>One row of Data/Item/ItemDrop.txt: a specific item that drops from the monsters it matches (-1 = any).
/// The Option columns are rows of ItemOptionRate.txt, -1 for none.</summary>
public sealed record ItemDropRule(
    int ItemIndex, int Level, int Durability, int[] Options, int Map, int MonsterClass,
    int MonsterLevelMin, int MonsterLevelMax, int AccountLevel, int DropRate);

/// <summary>One row of Data/Item/ItemMoneyDrop.txt: zen that drops from the monsters it matches (-1 = any). An
/// amount of -1 is the monster's own reward.</summary>
public sealed record MoneyDropRule(int Map, int MonsterClass, int MonsterLevelMin, int MonsterLevelMax, int AccountLevel, int MoneyAmount, int DropRate);

/// <summary>What a kill produced for the player who did the most damage.</summary>
public sealed record LootDrop(Item? Item, long Money);

/// <summary>What the loot roll needs to know about the kill.</summary>
public sealed class LootContext
{
    public required int MonsterClass { get; init; }
    public required int MonsterLevel { get; init; }
    public required int Map { get; init; }
    public required int ItemRate { get; init; }
    public required int MoneyRate { get; init; }

    /// <summary>The monster's zen: the experience it gave, summed over everyone who hit it (CItem money drops are
    /// based on it).</summary>
    public required long Money { get; init; }

    /// <summary>The player with the highest damage, who receives the drop and whose rates count.</summary>
    public required int AccountLevel { get; init; }
    public int PlayerMoneyAmountDropRate { get; init; } = 100;

    /// <summary>ItemDropRate_ALx and MoneyAmountDropRate_ALx of the GameServerInfo - Common.dat.</summary>
    public required int ServerItemDropRate { get; init; }
    public int ServerMoneyAmountDropRate { get; init; } = 100;
}

/// <summary> Port of the loot decision of gObjMonsterDieGiveItem (Monster.cpp:35-175) after the quest item: the
/// ItemDrop.txt rules, the ItemMoneyDrop.txt rules and then the generic roll -- an item of the monster's level
/// (excellent on a small chance, or a set item on another) with its options rolled from ItemOptionRate.txt, or
/// zen. The monster item bags (EventItemBag) are rolled before this, by the ItemBagManager. Not ported: the drop
/// events. </summary>
public sealed class LootRoller
{
    private const int MaxMonsterLevel = 150;
    private const int MaxMonsterItems = 100;

    private readonly ItemBalanceTable _items;
    private readonly ItemOptionRateTable _optionRates;
    private readonly ExcellentOptionRateTable _excellent;
    private readonly MapDropTable _mapDrops;
    private readonly MapRateInfoTable _mapRates;
    private readonly List<ItemDropRule> _itemRules;
    private readonly List<MoneyDropRule> _moneyRules;
    private readonly SetItemRules _sets;

    public LootRoller(
        ItemBalanceTable items, ItemOptionRateTable optionRates, ExcellentOptionRateTable excellent, MapDropTable mapDrops,
        MapRateInfoTable mapRates, List<ItemDropRule> itemRules, List<MoneyDropRule> moneyRules, SetItemRules? sets = null)
    {
        _sets = sets ?? SetItemRules.Current;
        _items = items;
        _optionRates = optionRates;
        _excellent = excellent;
        _mapDrops = mapDrops;
        _mapRates = mapRates;
        _itemRules = itemRules;
        _moneyRules = moneyRules;
    }

    // ---------------------------------------------------------------- loading the two rule files

    public static List<ItemDropRule> LoadItemRules(string path)
    {
        var rules = new List<ItemDropRule>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemDropRules] {0}", script.GetLastError());
            return rules;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int section = script.GetNumber();
            script.GetToken(); // ','
            int sub = script.GetAsNumber();
            int level = script.GetAsNumber();
            int durability = script.GetAsNumber();
            var options = new int[6];

            for (int n = 0; n < 6; n++)
            {
                options[n] = script.GetAsNumber();
            }

            int map = script.GetAsNumber();
            int monsterClass = script.GetAsNumber();
            int levelMin = script.GetAsNumber();
            int levelMax = script.GetAsNumber();
            int accountLevel = script.GetAsNumber();
            int dropRate = script.GetAsNumber();

            rules.Add(new ItemDropRule(Item.GetItem(section, sub), level, durability, options, map, monsterClass, levelMin, levelMax, accountLevel, dropRate));
        }

        Log.Add(LogColor.Blue, "[ItemDropRules] {0} item drop rule(s) loaded from {1}", rules.Count, path);
        return rules;
    }

    public static List<MoneyDropRule> LoadMoneyRules(string path)
    {
        var rules = new List<MoneyDropRule>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[MoneyDropRules] {0}", script.GetLastError());
            return rules;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int map = script.GetNumber();
            int monsterClass = script.GetAsNumber();
            int levelMin = script.GetAsNumber();
            int levelMax = script.GetAsNumber();
            int accountLevel = script.GetAsNumber();
            int amount = script.GetAsNumber();
            int dropRate = script.GetAsNumber();

            rules.Add(new MoneyDropRule(map, monsterClass, levelMin, levelMax, accountLevel, amount, dropRate));
        }

        Log.Add(LogColor.Blue, "[MoneyDropRules] {0} zen drop rule(s) loaded from {1}", rules.Count, path);
        return rules;
    }

    // ---------------------------------------------------------------- the decision

    public LootDrop? Roll(LootContext ctx, Random rng)
    {
        // Statue of Saint and the Guards drop nothing.
        if (ctx.MonsterClass is 132 or 133 or 134 or 247 or 249)
        {
            return null;
        }

        // ItemDrop.txt (CItemDrop::DropItem) and ItemMoneyDrop.txt (CItemMoneyDrop::DropMoney), only on maps where
        // items drop.
        if (_mapDrops.ItemDropEnabled(ctx.Map))
        {
            var fromRule = RollItemRules(ctx, rng);

            if (fromRule != null)
            {
                return fromRule;
            }

            var money = RollMoneyRules(ctx, rng);

            if (money != null)
            {
                return money;
            }
        }

        // Chaos Castle drops nothing else.
        if (ctx.Map is >= 18 and <= 23)
        {
            return null;
        }

        return RollGeneric(ctx, rng);
    }

    private LootDrop? RollItemRules(LootContext ctx, Random rng)
    {
        var candidates = new List<(int Value, int Weight)>();

        for (int n = 0; n < _itemRules.Count; n++)
        {
            var rule = _itemRules[n];

            if (_items.Get(rule.ItemIndex) == null
                || (rule.Map != -1 && rule.Map != ctx.Map)
                || (rule.MonsterClass != -1 && rule.MonsterClass != ctx.MonsterClass)
                || (rule.MonsterLevelMin != -1 && rule.MonsterLevelMin > ctx.MonsterLevel)
                || (rule.MonsterLevelMax != -1 && rule.MonsterLevelMax < ctx.MonsterLevel)
                || (rule.AccountLevel != -1 && rule.AccountLevel != ctx.AccountLevel))
            {
                continue;
            }

            if (rule.DropRate == -1 || (rule.DropRate > 0 && rng.Next(1000000) < rule.DropRate))
            {
                // Rarer rules weigh more among the ones that fired: 1000000 / rate (CItemDrop::DropItem).
                candidates.Add((n, 1000000 / (rule.DropRate == -1 ? 1000000 : rule.DropRate)));
            }
        }

        if (WeightedPick.Pick(candidates, rng) is not int picked)
        {
            return null;
        }

        var chosen = _itemRules[picked];
        return new LootDrop(BuildRuleItem(chosen.ItemIndex, chosen.Level, chosen.Durability, chosen.Options, rng), 0);
    }

    /// <summary>The item a rule (an ItemDrop.txt row or a drop event's) hands out: the given level and durability, or
    /// the ones rolled from the rows of ItemOptionRate.txt that its six option columns name (-1 for none), with the
    /// excellent and set options made the way a drop makes them.</summary>
    public Item BuildRuleItem(int itemIndex, int baseLevel, int durability, int[] options, Random rng)
    {
        var item = new Item { Index = (short)itemIndex };
        int level = baseLevel;

        if (_optionRates.Pick(ItemOptionRateTable.LevelOption, options[0], rng) is int l) level = l;
        item.Level = (byte)level;
        item.Option1 = (byte)(_optionRates.Pick(ItemOptionRateTable.SkillOption, options[1], rng) ?? 0);
        item.Option2 = (byte)(_optionRates.Pick(ItemOptionRateTable.LuckOption, options[2], rng) ?? 0);
        item.Option3 = (byte)(_optionRates.Pick(ItemOptionRateTable.AdditionalOption, options[3], rng) ?? 0);
        int excellentCount = _optionRates.Pick(ItemOptionRateTable.ExcellentCount, options[4], rng) ?? 0;
        item.NewOption = _excellent.MakeNewOption(itemIndex, 1, excellentCount, rng);
        int setValue = _optionRates.Pick(ItemOptionRateTable.SetOption, options[5], rng) ?? 0;
        item.SetOption = _sets.Types.MakeSetOption(itemIndex, setValue, rng);
        ItemCombatMath.Options.Normalize(item);
        item.Durability = FullDurability(item, durability);
        return item;
    }

    /// <summary>Whether items drop on this map at all (MapManager.txt ItemDrop).</summary>
    public bool ItemDropEnabled(int map) => _mapDrops.ItemDropEnabled(map);

    /// <summary>The per-map table, for the rules that read its PK columns.</summary>
    public MapDropTable MapDrops => _mapDrops;

    /// <summary>Whether the server knows this item (Item.txt).</summary>
    public bool KnowsItem(int itemIndex) => _items.Get(itemIndex) != null;

    private LootDrop? RollMoneyRules(LootContext ctx, Random rng)
    {
        var candidates = new List<(int Value, int Weight)>();

        for (int n = 0; n < _moneyRules.Count; n++)
        {
            var rule = _moneyRules[n];

            if ((rule.Map != -1 && rule.Map != ctx.Map)
                || (rule.MonsterClass != -1 && rule.MonsterClass != ctx.MonsterClass)
                || (rule.MonsterLevelMin != -1 && rule.MonsterLevelMin > ctx.MonsterLevel)
                || (rule.MonsterLevelMax != -1 && rule.MonsterLevelMax < ctx.MonsterLevel)
                || (rule.AccountLevel != -1 && rule.AccountLevel != ctx.AccountLevel))
            {
                continue;
            }

            if (rule.DropRate == -1 || (rule.DropRate > 0 && rng.Next(1000000) < rule.DropRate))
            {
                candidates.Add((n, 1000000 / (rule.DropRate == -1 ? 1000000 : rule.DropRate)));
            }
        }

        if (WeightedPick.Pick(candidates, rng) is not int picked)
        {
            return null;
        }

        var chosen = _moneyRules[picked];
        long amount = chosen.MoneyAmount == -1 ? (ctx.Money == 0 ? 1 : ctx.Money) : chosen.MoneyAmount;
        amount = amount * ctx.PlayerMoneyAmountDropRate / 100;

        return new LootDrop(null, Math.Max(amount, 1));
    }

    private LootDrop? RollGeneric(LootContext ctx, Random rng)
    {
        int itemDropRate = ctx.ServerItemDropRate * _mapDrops.ItemDropRate(ctx.Map) / 100;
        int dropLevel = ctx.MonsterLevel;
        bool excellent = false;

        // Set items: a chance per million per map, from level 25, and then nothing else drops (Monster.cpp:104-108).
        if (ctx.MonsterLevel >= 25 && rng.Next(1000000) < _mapDrops.SetItemDropRate(ctx.Map))
        {
            var setItem = MakeRandomSetItem(ctx.Map, rng);
            return setItem == null ? null : new LootDrop(setItem, 0);
        }

        // Excellent drops: a chance per million per map, from level 25, taken from items 25 levels lower.
        if (ctx.MonsterLevel >= 25 && rng.Next(1000000) < _mapDrops.ExcItemDropRate(ctx.Map))
        {
            dropLevel -= 25;
            excellent = true;
        }

        int dropIndex = GetMonsterItem(dropLevel, excellent, rng);

        if (dropIndex >= 0 && rng.Next(ctx.ItemRate == 0 ? 1 : ctx.ItemRate) < itemDropRate)
        {
            return new LootDrop(MakeDroppedItem(dropIndex, excellent, ctx.Map, rng), 0);
        }

        if (ctx.Money > 0 && rng.Next(ctx.MoneyRate == 0 ? 1 : ctx.MoneyRate) < _mapDrops.MoneyDropRate(ctx.Map) / 10)
        {
            long money = ctx.Money * ctx.ServerMoneyAmountDropRate / 100;
            money = money * ctx.PlayerMoneyAmountDropRate / 100;
            return new LootDrop(null, Math.Max(money, 1));
        }

        return null;
    }

    /// <summary>Port of CMonsterManager::GetMonsterItem: of the items that can drop (Item.txt DropItem), those whose
    /// level is from 2 above to 4 below the monster's, at most the first 100 by item number; excellent drops skip
    /// everything from the wings on. -1 when there is none.</summary>
    public int GetMonsterItem(int level, bool excellent, Random rng)
    {
        if (level < 0 || level >= MaxMonsterLevel)
        {
            return -1;
        }

        var table = new List<int>();

        foreach (var item in _items.All.OrderBy(i => i.Index))
        {
            if (!item.DropItem || item.Level < 0 || table.Count >= MaxMonsterItems)
            {
                continue;
            }

            if (item.Level + 4 >= level && item.Level - 2 <= level && !(excellent && item.Index >= Item.GetItem(12, 0)))
            {
                table.Add(item.Index);
            }
        }

        return table.Count == 0 ? -1 : table[rng.Next(table.Count)];
    }

    /// <summary>The dropped item with its options rolled from the map's rows of ItemOptionRate.txt. From the wings on
    /// (section 12) there is no level, skill, luck or excellent option.</summary>
    private Item MakeDroppedItem(int itemIndex, bool excellent, int map, Random rng)
    {
        int type = excellent ? MapRateInfoTable.DropExcellent : MapRateInfoTable.DropCommon;
        bool plain = itemIndex >= Item.GetItem(12, 0);

        int Row(int option) => _mapRates.GetOption(option, map, type);

        int level = _optionRates.Pick(ItemOptionRateTable.LevelOption, Row(0), rng) ?? 0;
        int skill = _optionRates.Pick(ItemOptionRateTable.SkillOption, Row(1), rng) ?? 0;
        int luck = _optionRates.Pick(ItemOptionRateTable.LuckOption, Row(2), rng) ?? 0;
        int additional = _optionRates.Pick(ItemOptionRateTable.AdditionalOption, Row(3), rng) ?? 0;
        int excellentCount = plain ? 0 : _optionRates.Pick(ItemOptionRateTable.ExcellentCount, Row(4), rng) ?? 0;
        int setValue = _optionRates.Pick(ItemOptionRateTable.SetOption, Row(5), rng) ?? 0;

        var item = new Item
        {
            Index = (short)itemIndex,
            Level = (byte)(plain ? 0 : level),
            Option1 = (byte)(plain ? 0 : skill),
            Option2 = (byte)(plain ? 0 : luck),
            Option3 = (byte)additional,
            NewOption = _excellent.MakeNewOption(itemIndex, 1, excellentCount, rng),
            SetOption = _sets.Types.MakeSetOption(itemIndex, setValue, rng),
        };

        ItemCombatMath.Options.Normalize(item);
        item.Durability = FullDurability(item, 0);
        return item;
    }

    /// <summary>Port of CSetItemType::MakeRandomSetItem: a random set piece (weighted by its drop rate) with its
    /// options rolled from the rows the map uses for set drops. Null when the server has no set item to drop.</summary>
    public Item? MakeRandomSetItem(int map, Random rng)
    {
        int index = _sets.Types.GetRandomSetItem(_items, rng);

        if (index < 0)
        {
            return null;
        }

        int Row(int option) => _mapRates.GetOption(option, map, MapRateInfoTable.DropSet);

        int level = _optionRates.Pick(ItemOptionRateTable.LevelOption, Row(0), rng) ?? 0;
        int skill = _optionRates.Pick(ItemOptionRateTable.SkillOption, Row(1), rng) ?? 0;
        int luck = _optionRates.Pick(ItemOptionRateTable.LuckOption, Row(2), rng) ?? 0;
        int additional = _optionRates.Pick(ItemOptionRateTable.AdditionalOption, Row(3), rng) ?? 0;
        int excellentCount = _optionRates.Pick(ItemOptionRateTable.ExcellentCount, Row(4), rng) ?? 0;
        int setValue = _optionRates.Pick(ItemOptionRateTable.SetOption, Row(5), rng) ?? 0;

        var item = new Item
        {
            Index = (short)index,
            Level = (byte)level,
            Option1 = (byte)skill,
            Option2 = (byte)luck,
            Option3 = (byte)additional,
            NewOption = _excellent.MakeNewOption(index, 1, excellentCount, rng),
            SetOption = _sets.Types.MakeSetOption(index, setValue, rng),
        };

        ItemCombatMath.Options.Normalize(item);
        item.Durability = FullDurability(item, 0);
        return item;
    }

    /// <summary>A given durability, or the item's full one when it is 0 (as GDCreateItemSend does).</summary>
    private byte FullDurability(Item item, int given) => FullDurability(_items, item, given);

    /// <summary>A given durability, or the item's full one when it is 0; shared with the item bags.</summary>
    public static byte FullDurability(ItemBalanceTable items, Item item, int given)
    {
        if (given > 0)
        {
            return (byte)Math.Clamp(given, 1, 255);
        }

        var info = items.Get(item.Index);
        return (byte)Math.Max(info == null ? 1 : ItemCombatMath.GetItemDurability(item, info), 1);
    }
}

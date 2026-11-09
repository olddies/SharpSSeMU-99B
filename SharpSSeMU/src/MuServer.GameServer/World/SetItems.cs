using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>The effects a set bonus can have (SET_ITEM_OPTION_* of SetItemOption.h). Strength, agility and energy
/// are added before the player's stats are used; the rest after the items' own options.</summary>
public static class SetItemOptionIndex
{
    public const int AddStrength = 0, AddDexterity = 1, AddEnergy = 2;
    public const int AddMinPhysiDamage = 5, AddMaxPhysiDamage = 6, MulMagicDamage = 7, MulAttackSuccessRate = 9;
    public const int AddDefense = 10, AddMaxLife = 11, AddMaxMana = 12, AddMaxBp = 13, AddBpRecovery = 14;
    public const int AddCriticalDamageRate = 15, AddCriticalDamage = 16, AddExcellentDamageRate = 17, AddExcellentDamage = 18;
    public const int AddSkillDamage = 19, AddDoubleDamageRate = 20, AddIgnoreDefenseRate = 21, MulShieldDefense = 22;
}

/// <summary>One row of SetItemType.txt: a piece that belongs to a set. <see cref="StatType"/> is the stat it adds
/// (0 strength, 1 agility, 2 energy; 5 per tier), <see cref="OptionIndex"/> the sets it can belong to (the two
/// variants of the piece) and <see cref="DropRate"/> its weight when a random set item is made.</summary>
public sealed record SetItemTypeInfo(int Index, int StatType, int[] OptionIndex, int DropRate);

/// <summary> Port of CSetItemType (SetItemType.cpp): Data/Item/SetItemType.txt. The set option byte of an item holds
/// the variant in its low two bits (1 or 2, an index into <see cref="SetItemTypeInfo.OptionIndex"/>) and the tier of
/// the piece in the next two (1 or 2): 5 and 9 are the first variant, 6 and 10 the second. </summary>
public sealed class SetItemTypeTable
{
    private const int OptionIndexCount = 2;

    private readonly SortedDictionary<int, SetItemTypeInfo> _info = new();

    public int Count => _info.Count;

    /// <summary>Adds a row; like the original's std::map::insert, the first row of an item wins.</summary>
    public void Add(SetItemTypeInfo info) => _info.TryAdd(info.Index, info);

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[SetItemTypeTable] {0}", script.GetLastError());
            return 0;
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
            int statType = script.GetAsNumber();
            var optionIndex = new int[OptionIndexCount];

            for (int n = 0; n < optionIndex.Length; n++)
            {
                optionIndex[n] = script.GetAsNumber();
            }

            int dropRate = script.GetAsNumber();
            Add(new SetItemTypeInfo(Item.GetItem(section, sub), statType, optionIndex, dropRate));
        }

        Log.Add(LogColor.Blue, "[SetItemTypeTable] {0} set item type(s) loaded from {1}", Count, path);
        return Count;
    }

    public SetItemTypeInfo? Get(int itemIndex) => _info.GetValueOrDefault(itemIndex);

    public bool Contains(int itemIndex) => _info.ContainsKey(itemIndex);

    /// <summary>The set (an index of SetItemOption.txt) the item belongs to in its first (<paramref name="number"/> 0)
    /// or second (1) variant; -1 when the item is not a set piece or there is no such variant.</summary>
    public int GetOptionIndex(int itemIndex, int number)
    {
        var info = Get(itemIndex);
        return info == null || number < 0 || number >= OptionIndexCount ? -1 : info.OptionIndex[number];
    }

    public int GetStatType(int itemIndex) => Get(itemIndex)?.StatType ?? -1;

    /// <summary>Port of CSetItemType::GetRandomSetItem: a set piece that can drop (Item.txt DropItem), weighted by
    /// its drop rate. -1 when there is none.</summary>
    public int GetRandomSetItem(ItemBalanceTable items, Random rng)
    {
        var candidates = _info.Values
            .Where(i => items.Get(i.Index) is { DropItem: true })
            .Select(i => (i.Index, i.DropRate));

        return WeightedPick.Pick(candidates, rng) ?? -1;
    }

    /// <summary>Port of CItemOptionRate::MakeSetOption: the set option byte for a piece from the value rolled in
    /// ItemOptionRate.txt (section 5). 1 and 2 pick one of the piece's variants at random (1 favours the first tier
    /// of the first variant, 2 that of the second), 3 and 4 force the first variant (tier 1 or 2), 5 and 6 the
    /// second. 0 when the item is not a set piece or the value is 0.</summary>
    public byte MakeSetOption(int itemIndex, int value, Random rng)
    {
        if (!Contains(itemIndex) || value == 0)
        {
            return 0;
        }

        if (value is 1 or 2)
        {
            Span<byte> table = stackalloc byte[OptionIndexCount];
            int count = 0;

            for (int n = 0; n < OptionIndexCount; n++)
            {
                if (GetOptionIndex(itemIndex, n) > 0)
                {
                    table[count++] = (byte)(n == 0 ? (value == 1 ? 5 : 9) : (value == 2 ? 6 : 10));
                }
            }

            return count == 0 ? (byte)0 : table[rng.Next(count)];
        }

        if (value is 3 or 4)
        {
            return GetOptionIndex(itemIndex, 0) > 0 ? (byte)(value == 3 ? 5 : 9) : (byte)0;
        }

        if (value is 5 or 6)
        {
            return GetOptionIndex(itemIndex, 1) > 0 ? (byte)(value == 5 ? 6 : 10) : (byte)0;
        }

        return 0;
    }
}

public readonly record struct SetItemOptionPair(int Index, int Value);

/// <summary>A set of SetItemOption.txt: <see cref="OptionTable"/> holds, for 2, 3, ... pieces worn, the (up to two)
/// bonuses each extra piece gives, and <see cref="FullOptionTable"/> what wearing every piece adds.</summary>
public sealed class SetItemOptionInfo
{
    public const int TierCount = 6, PairsPerTier = 2, FullCount = 5;

    public required int Index { get; init; }
    public required string Name { get; init; }
    public SetItemOptionPair[,] OptionTable { get; } = new SetItemOptionPair[TierCount, PairsPerTier];
    public SetItemOptionPair[] FullOptionTable { get; } = new SetItemOptionPair[FullCount];

    /// <summary>How many tiers have a bonus: the number of pieces beyond the first that make the set complete.</summary>
    public int MaxOptionCount
    {
        get
        {
            int count = 0;

            for (int n = 0; n < TierCount; n++)
            {
                for (int i = 0; i < PairsPerTier; i++)
                {
                    if (OptionTable[n, i].Index != -1)
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }
    }
}

/// <summary>What the set pieces a player wears add up to.</summary>
public sealed class SetItemBonus
{
    public int AddStrength { get; set; }
    public int AddDexterity { get; set; }
    public int AddEnergy { get; set; }

    /// <summary>The other bonuses, in the order they apply (<see cref="SetItemOptionIndex"/>).</summary>
    public List<SetItemOptionPair> Options { get; } = new();

    /// <summary>Every piece of a set is worn: the character shows the full-set aura.</summary>
    public bool IsFullSet { get; set; }
}

/// <summary> Port of CSetItemOption (SetItemOption.cpp) together with the SetItemType table: the set items of the
/// game. <see cref="Current"/> is the loaded data (the port's equivalent of gSetItemType / gSetItemOption), an empty
/// one until the start-up loads the files. </summary>
public sealed class SetItemRules
{
    private const int MaxSetItemOption = 100;

    public static SetItemRules Current { get; set; } = new();

    public SetItemTypeTable Types { get; } = new();

    private readonly SortedDictionary<int, SetItemOptionInfo> _sets = new();

    public int SetCount => _sets.Count;

    /// <summary>CItem::IsSetItem: the item has a set variant.</summary>
    public static bool IsSetItem(Item item) => (item.SetOption & 3) != 0;

    public void AddSet(SetItemOptionInfo info)
    {
        if (info.Index is >= 0 and < MaxSetItemOption)
        {
            _sets[info.Index] = info;
        }
    }

    public SetItemOptionInfo? GetSet(int index) => _sets.GetValueOrDefault(index);

    public int LoadSets(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[SetItemOptionTable] {0}", script.GetLastError());
            return 0;
        }

        _sets.Clear();

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            var info = new SetItemOptionInfo { Index = script.GetNumber(), Name = script.GetAsString() };

            for (int n = 0; n < SetItemOptionInfo.TierCount; n++)
            {
                for (int i = 0; i < SetItemOptionInfo.PairsPerTier; i++)
                {
                    int index = script.GetAsNumber();
                    info.OptionTable[n, i] = new SetItemOptionPair(index, script.GetAsNumber());
                }
            }

            for (int o = 0; o < SetItemOptionInfo.FullCount; o++)
            {
                int index = script.GetAsNumber();
                info.FullOptionTable[o] = new SetItemOptionPair(index, script.GetAsNumber());
            }

            AddSet(info);
        }

        Log.Add(LogColor.Blue, "[SetItemOptionTable] {0} set(s) loaded from {1}", SetCount, path);
        return SetCount;
    }

    /// <summary>Port of CSetItemOption::GetInventorySetItemOptionCount: how many worn pieces count towards the set
    /// <paramref name="setIndex"/>. Two identical weapons, or two identical rings, count once.</summary>
    private int CountWornPieces(IReadOnlyList<Item> inventory, int setIndex)
    {
        int count = 0;
        int weaponOption = 0;
        int ringOption = 0;

        for (int n = 0; n < Item.InventoryWearSize; n++)
        {
            var piece = inventory[n];

            if (!piece.IsItem() || !IsSetItem(piece) || !Types.Contains(piece.Index))
            {
                continue;
            }

            int option = Types.GetOptionIndex(piece.Index, (piece.SetOption & 3) - 1);

            if (option == 0 || option != setIndex)
            {
                continue;
            }

            if (n is Item.SlotWeapon1 or Item.SlotWeapon2)
            {
                if (weaponOption == 0)
                {
                    weaponOption = option;
                }
                else if (weaponOption == option && inventory[Item.SlotWeapon1].Index == inventory[Item.SlotWeapon2].Index)
                {
                    continue;
                }
            }
            else if (n is Item.SlotRing1 or Item.SlotRing2)
            {
                if (ringOption == 0)
                {
                    ringOption = option;
                }
                else if (ringOption == option && inventory[Item.SlotRing1].Index == inventory[Item.SlotRing2].Index)
                {
                    continue;
                }
            }

            count++;
        }

        return count;
    }

    /// <summary>Ports of CalcSetItemStat and CalcSetItemOption: every worn piece adds 5 per tier to its stat, every
    /// set with two or more pieces worn adds the bonuses of its tiers, and a complete set adds its full bonuses.
    /// Not ported: the original skips pieces whose stat requirements the character no longer meets
    /// (m_IsValidItem); this port never lets such a piece be worn.</summary>
    public SetItemBonus Calculate(IReadOnlyList<Item> inventory)
    {
        var bonus = new SetItemBonus();

        if (Types.Count == 0)
        {
            return bonus;
        }

        for (int n = 0; n < Item.InventoryWearSize; n++)
        {
            var piece = inventory[n];

            if (!piece.IsItem() || !IsSetItem(piece) || !Types.Contains(piece.Index))
            {
                continue;
            }

            // The piece's own stat: only strength, agility and energy count here (CalcSetItemStat -> InsertOption).
            int stat = Types.GetStatType(piece.Index);

            if (stat is >= SetItemOptionIndex.AddStrength and <= SetItemOptionIndex.AddEnergy)
            {
                AddToBonus(bonus, stat, ((piece.SetOption >> 2) & 3) * 5);
            }
        }

        foreach (var set in _sets.Values)
        {
            int count = CountWornPieces(inventory, set.Index);

            if (count == 0)
            {
                continue;
            }

            for (int tier = 0; tier < count - 1 && tier < SetItemOptionInfo.TierCount; tier++)
            {
                for (int i = 0; i < SetItemOptionInfo.PairsPerTier; i++)
                {
                    var pair = set.OptionTable[tier, i];

                    if (pair.Index != -1)
                    {
                        AddToBonus(bonus, pair.Index, pair.Value);
                    }
                }
            }

            if (count - 1 == set.MaxOptionCount)
            {
                foreach (var pair in set.FullOptionTable)
                {
                    if (pair.Index != -1)
                    {
                        AddToBonus(bonus, pair.Index, pair.Value);
                    }
                }

                bonus.IsFullSet = true;
            }
        }

        return bonus;
    }

    private static void AddToBonus(SetItemBonus bonus, int index, int value)
    {
        switch (index)
        {
            case SetItemOptionIndex.AddStrength:
                bonus.AddStrength += value;
                break;
            case SetItemOptionIndex.AddDexterity:
                bonus.AddDexterity += value;
                break;
            case SetItemOptionIndex.AddEnergy:
                bonus.AddEnergy += value;
                break;
            case 3: // indexes InsertOption has no case for: they do nothing in the original either
            case 4:
            case 8:
                break;
            default:
                bonus.Options.Add(new SetItemOptionPair(index, value));
                break;
        }
    }
}

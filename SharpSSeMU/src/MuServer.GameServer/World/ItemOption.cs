using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>Port of eItemOption (ItemOption.h): what each option of Data/Item/ItemOption.txt does to the character
/// that wears the item. The numbers are the file's "OptionIndex" column.</summary>
public static class ItemOptionIndex
{
    public const int AddPhysiDamage = 80;
    public const int AddMagicDamage = 81;
    public const int AddDefenseSuccessRate = 82;
    public const int AddDefense = 83;
    public const int AddCriticalDamageRate = 84;
    public const int AddHpRecoveryRate = 85;
    public const int AddMoneyAmountDropRate = 86;
    public const int MulDefenseSuccessRate = 87;
    public const int AddDamageReflect = 88;
    public const int AddDamageReduction = 89;
    public const int MulMp = 90;
    public const int MulHp = 91;
    public const int AddExcellentDamageRate = 92;
    public const int AddPhysiDamageByLevel = 93;
    public const int MulPhysiDamage = 94;
    public const int AddMagicDamageByLevel = 95;
    public const int MulMagicDamage = 96;
    public const int AddSpeed = 97;
    public const int AddHuntHp = 98;
    public const int AddHuntMp = 99;
    public const int AddWingHp = 100;
    public const int AddWingMp = 101;
    public const int AddIgnoreDefenseRate = 102;
    public const int AddBp = 103;
    public const int MulBp = 104;
    public const int AddWingLeadership = 105;
    public const int AddHp = 119;
    public const int MulDamage = 125;
}

/// <summary>The 14 special-option slots of an item (Item.h: SPECIAL_OPTION1..SPECIAL_COMMON5). The file's "Index"
/// column says which slot a row fills: 0 = skill, 1 = luck, 2 = additional option (+4/+8/+12/+16), 3-8 = the six
/// excellent options, 9-13 = options every matching item gets.</summary>
public static class SpecialSlot
{
    public const int Option1 = 0; // skill
    public const int Option2 = 1; // luck
    public const int Option3 = 2; // additional option
    public const int Excellent1 = 3;
    public const int Common1 = 9;
    public const int Count = 14;
}

/// <summary>One row of Data/Item/ItemOption.txt. -1 means "*" (any).</summary>
public sealed record ItemOptionRule(
    int Index, int OptionIndex, int OptionValue, int ItemMinIndex, int ItemMaxIndex,
    int ItemOption1, int ItemOption2, int ItemOption3, int ItemNewOption);

/// <summary>An option an item grants: which slot it fills, what it does (<see cref="ItemOptionIndex"/>) and how
/// much. For the skill slot, <see cref="OptionIndex"/> is the skill the item teaches.</summary>
public readonly record struct ItemSpecial(int Slot, int OptionIndex, int Value);

/// <summary> Port of CItemOption (ItemOption.cpp) plus the special-option part of CItem::Convert (Item.cpp:
/// 503-700): the rules that turn an item's skill/luck/additional/excellent flags into concrete bonuses. The
/// server keeps the flags on <see cref="Item"/> as they travel on the wire and in the database, and computes the
/// bonuses from them when needed, instead of caching them on the item like the original's m_SpecialIndex/Value.
/// </summary>
public sealed class ItemOptionTable
{
    private const int SkillNone = 0;
    private const int SkillForceWave = 66;

    private readonly Dictionary<int, List<ItemOptionRule>> _rules = new();

    public int Count { get; private set; }

    public void Add(ItemOptionRule rule)
    {
        if (!_rules.TryGetValue(rule.Index, out var list))
        {
            list = new List<ItemOptionRule>();
            _rules[rule.Index] = list;
        }

        list.Add(rule);
        Count++;
    }

    public int Load(string path)
    {
        _rules.Clear();
        Count = 0;

        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemOptionTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End || script.GetString() == "end")
            {
                break;
            }

            int index = script.GetNumber();
            int optionIndex = script.GetAsNumber();
            int optionValue = script.GetAsNumber();
            int itemMin = ReadItemIndex(script);
            int itemMax = ReadItemIndex(script);
            int option1 = script.GetAsNumber();
            int option2 = script.GetAsNumber();
            int option3 = script.GetAsNumber();
            int newOption = script.GetAsNumber();

            if (index < 0 || index >= SpecialSlot.Count)
            {
                continue;
            }

            Add(new ItemOptionRule(index, optionIndex, optionValue, itemMin, itemMax, option1, option2, option3, newOption));
        }

        Log.Add(LogColor.Blue, "[ItemOptionTable] {0} item option rule(s) loaded from {1}", Count, path);
        return Count;
    }

    /// <summary>"section,sub" as one item index (SafeGetItem(GET_ITEM(a,b))); "*" gives -1. This tokenizer returns
    /// the comma as a token of its own, so it is skipped the same way ItemValueTable does.</summary>
    private static int ReadItemIndex(MemScript script)
    {
        int section = script.GetAsNumber();

        if (section < 0)
        {
            return -1;
        }

        script.GetToken(); // ','
        int sub = script.GetAsNumber();
        return sub < 0 ? -1 : Item.GetItem(section, sub);
    }

    /// <summary>Port of CItemOption::GetItemOption: the last matching row of that slot wins (that is how the
    /// second wings pick HP recovery instead of damage when their excellent bit 32 is set).</summary>
    public ItemOptionRule? Find(int slot, Item item)
    {
        if (!_rules.TryGetValue(slot, out var list))
        {
            return null;
        }

        ItemOptionRule? found = null;

        foreach (var rule in list)
        {
            if (rule.ItemMinIndex != -1 && rule.ItemMinIndex > item.Index) continue;
            if (rule.ItemMaxIndex != -1 && rule.ItemMaxIndex < item.Index) continue;
            if (rule.ItemOption1 != -1 && rule.ItemOption1 > item.Option1) continue;
            if (rule.ItemOption2 != -1 && rule.ItemOption2 > item.Option2) continue;
            if (rule.ItemOption3 != -1 && rule.ItemOption3 > item.Option3) continue;
            if (rule.ItemNewOption != -1 && (item.NewOption & rule.ItemNewOption) == 0) continue;

            found = rule;
        }

        return found;
    }

    /// <summary>True when the item really carries its skill (the skill flag set AND a skill row covering it) --
    /// in the original, Convert clears m_Option1 otherwise, so the skill never reaches the skill list.</summary>
    public bool HasSkill(Item item) => item.IsItem() && item.Option1 != 0 && Find(SpecialSlot.Option1, item) != null;

    /// <summary>Every option the item grants, in slot order (CItem::Convert, Item.cpp:503-700). The additional
    /// option's value is multiplied by the item's additional level (+4 per step for the usual 4-point rows).
    /// </summary>
    public List<ItemSpecial> GetSpecials(Item item, ItemBalance? info)
    {
        var result = new List<ItemSpecial>();

        if (!item.IsItem())
        {
            return result;
        }

        for (int slot = 0; slot < SpecialSlot.Count; slot++)
        {
            var rule = Find(slot, item);

            if (rule == null)
            {
                continue;
            }

            int optionIndex = rule.OptionIndex;
            int value = rule.OptionValue;

            if (slot == SpecialSlot.Option1)
            {
                int skill = info?.Skill ?? SkillNone;
                optionIndex = skill == SkillNone ? optionIndex : skill == SkillForceWave ? SkillNone : skill;
            }
            else if (slot == SpecialSlot.Option3)
            {
                value *= Math.Max((int)item.Option3, 1);
            }

            result.Add(new ItemSpecial(slot, optionIndex, value));
        }

        return result;
    }
}

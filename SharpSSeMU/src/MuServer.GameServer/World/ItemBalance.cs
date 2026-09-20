using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of ITEM_INFO (ItemManager.h) -- a balance row of Data/Item/Item.txt (Phase 4, second pass:
/// real combat balance). The column layout varies by section (0-15 = item category, see the header comment of
/// <see cref="ItemBalanceTable.Load"/>); this class gathers all possible fields of any section into a single
/// type (like MonsterInfo), leaving at 0/empty those that do not apply to that particular row's section. WATCH
/// OUT for <see cref="Level"/>: it is the "Level" column of the balance ROW (a kind of fixed "power level" of
/// the item type, e.g. "Kris" Level=6), completely different from <see cref="Item.Level"/> (the +0..+15 upgrade
/// level of the concrete instance of the item in a player's inventory). </summary>
public sealed class ItemBalance
{
    public required int Index { get; init; } // Item.GetItem(Section, Sub)
    public required int Section { get; init; }
    public required int Sub { get; init; }
    public int Slot { get; init; } // -1 = cualquier slot ('*' en el archivo, ej. pociones/joyas)
    public int Skill { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool HaveSerial { get; init; }
    public bool HaveOption { get; init; }
    public bool DropItem { get; init; }
    public string Name { get; init; } = string.Empty;

    public int Level { get; init; }
    public int DamageMin { get; init; }
    public int DamageMax { get; init; }
    public int AttackSpeed { get; init; }
    public int Durability { get; init; } // factory durability (not to be confused with Item.Durability, the current one)
    public int MagicDurability { get; init; }
    public int MagicDamageRate { get; init; }
    public int Defense { get; init; }
    public int DefenseSuccessRate { get; init; }
    public int MagicDefense { get; init; }
    public int WalkSpeed { get; init; }
    public int Value { get; init; } // section 14 (jewels/potions): shop price

    public int RequireLevel { get; init; }
    public int RequireStrength { get; init; }
    public int RequireDexterity { get; init; }
    public int RequireEnergy { get; init; }
    public int RequireVitality { get; init; }
    public int RequireLeadership { get; init; }
    public int BuyMoney { get; init; }
    public int SetAttr { get; init; }
    public int[] Resistance { get; init; } = new int[7]; // section 13: ice/poison/lightning/fire/earth/wind/water

    /// <summary>Usage per class (DW,DK,FE,MG,DL, in that order) -- the file carries values 0/1/2 (0=cannot use
    /// it, 1/2=can, the 2 appears on 2nd-generation wings/jewels). It is stored raw as is for when equipment
    /// validation is really ported; this pass's damage/defense balance does not need it.</summary>
    public int[] RequireClass { get; init; } = new int[5];

    public bool IsWeapon => Section is >= 0 and <= 5;
    public bool IsShield => Section == 6;

    /// <summary>Puerto de TwoHand (derivado, ItemManager.cpp: TwoHand = Width>=2) -- solo tiene
    /// sentido para armas (secciones 0-5).</summary>
    public bool TwoHand => IsWeapon && Width >= 2;

    /// <summary>Ammunition (arrow/bolt, section 4 but with no damage of its own: DamageMin=DamageMax=0 in
    /// Item.txt) -- it is equipped in the left hand together with a bow/crossbow and does NOT count as a
    /// "weapon" for the dual-wield/left-hand damage rules; its upgrade level does contribute a percentage bonus
    /// to the bow's damage (see PlayerObject.RecalcCombatStats).</summary>
    public bool IsAmmo => Section == 4 && DamageMin == 0 && DamageMax == 0;
}

/// <summary> Port of CItemManager::Load (ItemManager.cpp:52-259) -- reads Data/Item/Item.txt (MemScript format,
/// 16 numbered sections 0-15, each with its own column layout after the 9 common columns; layout confirmed line
/// by line against the real file's header comments). It does not include the excellent levels/options system
/// (ItemOption.txt/SetItemOption.txt) nor the +0..+15 level scaling (that lives in <see
/// cref="ItemCombatMath"/>, applied over this table). </summary>
public sealed class ItemBalanceTable
{
    private readonly Dictionary<int, ItemBalance> _byIndex = new();

    public int Count => _byIndex.Count;

    public ItemBalance? Get(int index) => _byIndex.GetValueOrDefault(index);

    public IEnumerable<ItemBalance> All => _byIndex.Values;

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemBalanceTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            int section = script.GetNumber();

            while (true)
            {
                if (script.GetToken() == TokenResult.End)
                {
                    break;
                }

                if (script.GetString() == "end")
                {
                    break;
                }

                int sub = script.GetNumber();
                int slot = script.GetAsNumber();
                int skill = script.GetAsNumber();
                int width = script.GetAsNumber();
                int height = script.GetAsNumber();
                int haveSerial = script.GetAsNumber();
                int haveOption = script.GetAsNumber();
                int dropItem = script.GetAsNumber();
                string name = script.GetAsString();

                var common = new
                {
                    Index = Item.GetItem(section, sub), Section = section, Sub = sub, Slot = slot, Skill = skill,
                    Width = width, Height = height, HaveSerial = haveSerial != 0, HaveOption = haveOption != 0,
                    DropItem = dropItem != 0, Name = name,
                };

                switch (section)
                {
                    case >= 0 and <= 5: // weapons: sword/axe/mace/spear/bow/staff
                    {
                        int level = script.GetAsNumber();
                        int damageMin = script.GetAsNumber();
                        int damageMax = script.GetAsNumber();
                        int attackSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int magicDurability = script.GetAsNumber();
                        int magicDamageRate = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, DamageMin = damageMin, DamageMax = damageMax,
                            AttackSpeed = attackSpeed, Durability = durability > 0 ? durability : magicDurability, MagicDurability = magicDurability,
                            MagicDamageRate = magicDamageRate, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 6: // escudo
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int defenseSuccessRate = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, DefenseSuccessRate = defenseSuccessRate,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 7 or 8 or 9: // helm/armor/pants
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int magicDefense = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, MagicDefense = magicDefense,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 10: // guantes
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int attackSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, AttackSpeed = attackSpeed,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 11: // botas
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int walkSpeed = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqVit = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, WalkSpeed = walkSpeed,
                            Durability = durability, RequireLevel = reqLevel, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireEnergy = reqEne, RequireVitality = reqVit,
                            RequireLeadership = reqLead, SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 12: // alas -- OJO: orden de columnas distinto (ReqEnergy antes que ReqStr/ReqDex), sin SetAttr
                    {
                        int level = script.GetAsNumber();
                        int defense = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int reqStr = script.GetAsNumber();
                        int reqDex = script.GetAsNumber();
                        int reqLead = script.GetAsNumber();
                        int buyMoney = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Defense = defense, Durability = durability,
                            RequireLevel = reqLevel, RequireEnergy = reqEne, RequireStrength = reqStr,
                            RequireDexterity = reqDex, RequireLeadership = reqLead, BuyMoney = buyMoney,
                            RequireClass = reqClass,
                        };
                        break;
                    }

                    case 13: // pets/ring-pendant jewels/miscellaneous
                    {
                        int level = script.GetAsNumber();
                        int durability = script.GetAsNumber();
                        var resistance = new int[7];
                        for (int n = 0; n < 7; n++)
                        {
                            resistance[n] = script.GetAsNumber();
                        }

                        int setAttr = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, Durability = durability, Resistance = resistance,
                            SetAttr = setAttr, RequireClass = reqClass,
                        };
                        break;
                    }

                    case 14: // joyas/pociones/consumibles -- SOLO Value y Level, sin requisitos ni ReqClass
                    {
                        int value = script.GetAsNumber();
                        int level = script.GetAsNumber();

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Value = value, Level = level,
                        };
                        break;
                    }

                    case 15: // orbes/pergaminos
                    {
                        int level = script.GetAsNumber();
                        int reqLevel = script.GetAsNumber();
                        int reqEne = script.GetAsNumber();
                        int buyMoney = script.GetAsNumber();
                        var reqClass = ReadClassRow(script);

                        _byIndex[common.Index] = new ItemBalance
                        {
                            Index = common.Index, Section = common.Section, Sub = common.Sub, Slot = common.Slot,
                            Skill = common.Skill, Width = common.Width, Height = common.Height,
                            HaveSerial = common.HaveSerial, HaveOption = common.HaveOption, DropItem = common.DropItem,
                            Name = common.Name, Level = level, RequireLevel = reqLevel, RequireEnergy = reqEne,
                            BuyMoney = buyMoney, RequireClass = reqClass,
                        };
                        break;
                    }

                    default:
                        // Unknown section (the real file only carries 0-15) -- the rest of the row is discarded
                        // token by token so as not to desynchronise the rest of the parsing.
                        while (true)
                        {
                            var s = script.GetAsString();
                            if (s.Length == 0)
                            {
                                break;
                            }
                        }
                        break;
                }
            }
        }

        Log.Add(LogColor.Blue, "[ItemBalanceTable] {0} balance items loaded from {1}", _byIndex.Count, path);
        return _byIndex.Count;
    }

    /// <summary> Simplified port of CMonsterManager::GetMonsterItem (MonsterManager.cpp:284-320): among all the
    /// items with <c>DropItem=true</c> whose <see cref="ItemBalance.Level"/> "fits" the monster's level
    /// (<c>(ItemLevel+4) >= MonsterLevel &amp;&amp; (ItemLevel-2) &lt;= MonsterLevel</c>, exact formula of the
    /// original), it picks one at random uniformly. Simplified: the original pre-builds a table of candidates
    /// per monster level on loading Item.txt (m_MonsterItemInfo, up to 100 candidates/level) and filters
    /// excellent/socket eligibility at roll time; here the candidate list is recomputed on each roll with a
    /// linear pass over all the loaded items (a few hundred in a real Item.txt, negligible cost) and it does
    /// NOT filter by excellent/socket eligibility (that extra-rarity system is not ported in this pass, see
    /// README) -- any candidate item can come out as a normal drop. </summary>
    public ItemBalance? PickRandomDropItem(int monsterLevel, Random rng)
    {
        var candidates = new List<ItemBalance>();

        foreach (var item in _byIndex.Values)
        {
            if (!item.DropItem)
            {
                continue;
            }

            if (item.Level + 4 >= monsterLevel && item.Level - 2 <= monsterLevel)
            {
                candidates.Add(item);
            }
        }

        if (candidates.Count == 0)
        {
            foreach (var item in _byIndex.Values)
            {
                if (item.DropItem && item.Level <= monsterLevel + 5)
                {
                    candidates.Add(item);
                }
            }
        }

        return candidates.Count == 0 ? null : candidates[rng.Next(candidates.Count)];
    }

    private static int[] ReadClassRow(MemScript script)
    {
        var reqClass = new int[5];
        for (int n = 0; n < 5; n++)
        {
            reqClass[n] = script.GetAsNumber();
        }
        return reqClass;
    }
}

namespace MuServer.GameServer.World;

/// <summary> Port of the stat part of CItem::Convert (Item.cpp:122-503) and of the getters CItem::GetDamageMin/
/// Max/GetDefense/GetDefenseSuccessRate (Item.cpp:1007-1053). Every value starts from the Item.txt row and gets:
/// the excellent-item bonus when the item has excellent options (<see cref="HasExcellentStats"/>), then the
/// per-level scaling <c>+level*3</c> plus <c>((level-9)*(level-8))/2</c> from +10 to +15 (+1,+3,+6,+10,+15,+21).
/// Shields scale Defense by a flat <c>+level</c> and get no excellent bonus; second wings and the Dark Horse scale
/// it by <c>+level*2</c>. All the getters return 0 if the item is broken (<see cref="Item.Durability"/>==0),
/// like the original. Set items (SetOption) do not exist in this build and are not handled. </summary>
public static class ItemCombatMath
{
    /// <summary>The loaded Data/Item/ItemOption.txt -- the port's equivalent of the global gItemOption. Set once at
    /// start-up; an empty table (no options at all) until then.</summary>
    public static ItemOptionTable Options { get; set; } = new();

    private static readonly int SwordOfArchangel = Item.GetItem(0, 19);
    private static readonly int ScepterOfArchangel = Item.GetItem(2, 13);
    private static readonly int CrossbowOfArchangel = Item.GetItem(4, 18);
    private static readonly int StaffOfArchangel = Item.GetItem(5, 10);
    private static readonly int Dinorant = Item.GetItem(13, 3);
    private static readonly int DarkHorse = Item.GetItem(13, 4);
    private static readonly int FirstWing = Item.GetItem(12, 0);

    private static bool IsArchangelWeapon(int index) =>
        index == SwordOfArchangel || index == ScepterOfArchangel || index == CrossbowOfArchangel || index == StaffOfArchangel;

    /// <summary>Whether the excellent flags count for the item's own stats (CItem::Convert, Item.cpp:147-162): the
    /// Archangel weapons, anything that goes in the wing slot (Item.txt Slot 7) and the Horn of Dinorant carry
    /// the flags but get no excellent stat bonus, extra durability or higher requirements from them.</summary>
    public static bool HasExcellentStats(Item item, ItemBalance info) =>
        item.NewOption != 0 && !IsArchangelWeapon(item.Index) && info.Slot != 7 && item.Index != Dinorant;

    /// <summary>The Chaos weapons get a fixed excellent bonus instead of the formula (Item.cpp:338-351).</summary>
    private static int ChaosItemBonus(int index)
    {
        if (index == Item.GetItem(2, 6)) return 15;  // Chaos Dragon Axe
        if (index == Item.GetItem(5, 7)) return 25;  // Chaos Lightning Staff
        if (index == Item.GetItem(4, 6)) return 30;  // Chaos Nature Bow
        return 0;
    }

    /// <summary>The excellent bonus applied to damage/magic rate (Item.cpp:353-429): the Chaos weapon's fixed value,
    /// or <c>(baseMin*25)/ItemLevel + 5</c>. Note that the original uses the item's base MINIMUM damage for both
    /// the minimum and the maximum bonus.</summary>
    private static int ExcellentAttackBonus(Item item, ItemBalance info, int baseValue)
    {
        int chaos = ChaosItemBonus(item.Index);

        if (chaos != 0)
        {
            return chaos;
        }

        return info.Level != 0 ? ((baseValue * 25) / info.Level) + 5 : 0;
    }

    private static int LevelScaled(int value, int level)
    {
        value += level * 3;

        if (level >= 10)
        {
            value += ((level - 9) * (level - 8)) / 2;
        }

        return value;
    }

    public static int GetDamageMin(Item item, ItemBalance info)
    {
        if (item.Durability == 0 || info.DamageMin <= 0) return 0;
        int value = info.DamageMin;
        if (HasExcellentStats(item, info)) value += ExcellentAttackBonus(item, info, info.DamageMin);
        return LevelScaled(value, item.Level);
    }

    public static int GetDamageMax(Item item, ItemBalance info)
    {
        if (item.Durability == 0 || info.DamageMax <= 0) return 0;
        int value = info.DamageMax;
        if (HasExcellentStats(item, info)) value += ExcellentAttackBonus(item, info, info.DamageMin);
        return LevelScaled(value, item.Level);
    }

    /// <summary>m_MagicDamageRate after Convert (Item.cpp:405-429): the staff/magic-sword "rise" percentage source.</summary>
    public static int GetMagicDamageRate(Item item, ItemBalance info)
    {
        if (item.Durability == 0 || info.MagicDamageRate <= 0) return 0;
        int value = info.MagicDamageRate;
        if (HasExcellentStats(item, info)) value += ExcellentAttackBonus(item, info, info.MagicDamageRate);
        return LevelScaled(value, item.Level);
    }

    public static int GetDefense(Item item, ItemBalance info)
    {
        if (item.Durability == 0 || info.Defense <= 0) return 0;
        int value = info.Defense;

        if (info.IsShield)
        {
            return value + item.Level; // Item.cpp:457-462 -- no excellent bonus, no quadratic extra
        }

        if (HasExcellentStats(item, info) && info.Level != 0)
        {
            value += ((value * 12) / info.Level) + (info.Level / 5) + 4;
        }

        bool secondWingOrHorse = (item.Index >= Item.GetItem(12, 3) && item.Index <= Item.GetItem(12, 6)) || item.Index == DarkHorse;
        value += item.Level * (secondWingOrHorse ? 2 : 3);

        if (item.Level >= 10)
        {
            value += ((item.Level - 9) * (item.Level - 8)) / 2;
        }

        return value;
    }

    public static int GetDefenseSuccessRate(Item item, ItemBalance info)
    {
        if (item.Durability == 0 || info.DefenseSuccessRate <= 0) return 0;
        int value = info.DefenseSuccessRate;

        if (HasExcellentStats(item, info) && info.Level != 0)
        {
            value += ((value * 25) / info.Level) + 5;
        }

        return LevelScaled(value, item.Level);
    }

    public static int GetMagicDefense(Item item, ItemBalance info) =>
        item.Durability == 0 || info.MagicDefense <= 0 ? 0 : LevelScaled(info.MagicDefense, item.Level);

    // AttackSpeed/WalkSpeed are NOT scaled by level in the original (they are fixed values of the Item.txt row)
    // -- they are exposed here anyway with the same durability=0 check for consistent use.
    public static int GetAttackSpeed(Item item, ItemBalance info) => item.Durability == 0 ? 0 : info.AttackSpeed;
    public static int GetWalkSpeed(Item item, ItemBalance info) => item.Durability == 0 ? 0 : info.WalkSpeed;

    /// <summary>Puerto de CItemManager::GetItemDurability (ItemManager.cpp:393-450).</summary>
    public static int GetItemDurability(Item item, ItemBalance info)
    {
        if (!item.IsItem()) return 0;

        // Rena, Symbol of Kundun, Invisibility Cloak, Armor of Guardsman, Devil's Invitation (ItemManager.cpp:402-410)
        if (item.Index == Item.GetItem(14, 21) || item.Index == Item.GetItem(14, 29) || item.Index == Item.GetItem(13, 18)
            || item.Index == Item.GetItem(13, 29) || item.Index == Item.GetItem(14, 19))
        {
            return 1;
        }

        int baseDur = info.Durability > 0 ? info.Durability : info.MagicDurability;
        if (baseDur == 0) return 0;

        int level = item.Level;
        int dur;
        if (level >= 5)
        {
            dur = level switch
            {
                10 => baseDur + ((level * 2) - 3),
                11 => baseDur + ((level * 2) - 1),
                12 => baseDur + ((level * 2) + 2),
                13 => baseDur + ((level * 2) + 6),
                14 => baseDur + ((level * 2) + 11),
                15 => baseDur + ((level * 2) + 17),
                _ => baseDur + ((level * 2) - 4),
            };
        }
        else
        {
            dur = baseDur + level;
        }

        // Excellent items last 15 points longer (ItemManager.cpp:451-461) -- not the Archangel weapons nor wings.
        if (!IsArchangelWeapon(item.Index) && info.Slot != 7 && item.NewOption != 0)
        {
            dur += 15;
        }

        return Math.Clamp(dur, 0, 255);
    }

    /// <summary>Puerto de CItemManager::GetItemRepairMoney (ItemManager.cpp:465-530).</summary>
    public static int GetItemRepairMoney(Item item, byte type, ItemBalance info)
    {
        if (!item.IsItem()) return 0;
        int maxDur = GetItemDurability(item, info);
        if (maxDur <= 0 || item.Durability >= maxDur) return 0;

        int buyMoney = info.BuyMoney > 0 ? info.BuyMoney : (info.Level * info.Level * 100);
        int repairMoney = (item.Index == 3332 || item.Index == 3333) ? buyMoney : buyMoney / 3;

        repairMoney = Math.Min(repairMoney, 2_000_000_000);
        if (repairMoney >= 100) repairMoney = (repairMoney / 10) * 10;
        if (repairMoney >= 1000) repairMoney = (repairMoney / 100) * 100;

        double sq1 = Math.Sqrt(repairMoney);
        double sq2 = Math.Sqrt(sq1);
        double durFactor = 1.0 - ((double)item.Durability / maxDur);
        double value = ((3.0 * sq1 * sq2) * durFactor) + 1.0;

        if (item.Durability == 0)
        {
            value *= (item.Index == 3332 || item.Index == 3333) ? 2.0 : 1.4;
        }

        int money = (int)(type == 1 ? value * 2.5 : value);
        if (money >= 100) money = (money / 10) * 10;
        if (money >= 1000) money = (money / 100) * 100;

        return Math.Max(money, 1);
    }

    /// <summary>The level Convert uses for the requirements (Item.cpp:229-234): excellent items count as 25 levels
    /// higher. The local "NewOption" the original tests there is the one already cleared for the Archangel
    /// weapons, wings and the Dinorant.</summary>
    private static int RequirementItemLevel(Item item, ItemBalance info) =>
        info.Level + (HasExcellentStats(item, info) ? 25 : 0);

    /// <summary>Port of CItem::Convert (Item.cpp:236-280) -- stat requirements scaled by the item's level. Strength
    /// also grows by 4 per step of the additional option when that option applies to the item (Item.cpp:566-593):
    /// weapons, shields and armour, the wings from Angel to Darkness, not the Wings of Fairy nor jewellery.</summary>
    public static int GetRequireStrength(Item item, ItemBalance info)
    {
        int value = 0;

        if (info.RequireStrength != 0)
        {
            value = (((info.RequireStrength * ((item.Level * 3) + RequirementItemLevel(item, info))) * 3) / 100) + 20;
        }

        if (item.Option3 != 0 && Options.Find(SpecialSlot.Option3, item) != null)
        {
            bool wings = item.Index >= Item.GetItem(12, 1) && item.Index <= Item.GetItem(12, 6);
            bool cloakOfLord = item.Index == Item.GetItem(13, 30);

            if (wings || cloakOfLord || item.Index < FirstWing)
            {
                value += item.Option3 * 4;
            }
        }

        return value;
    }

    public static int GetRequireDexterity(Item item, ItemBalance info)
    {
        if (info.RequireDexterity == 0) return 0;
        int itemLevel = RequirementItemLevel(item, info);
        return (((info.RequireDexterity * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireVitality(Item item, ItemBalance info)
    {
        if (info.RequireVitality == 0) return 0;
        int itemLevel = RequirementItemLevel(item, info);
        return (((info.RequireVitality * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireEnergy(Item item, ItemBalance info)
    {
        if (info.RequireEnergy == 0) return 0;
        int itemLevel = RequirementItemLevel(item, info);
        if (info.Section == 5 && info.Slot == 1) // Staffs
        {
            return (((info.RequireEnergy * (item.Level + itemLevel)) * 3) / 100) + 20;
        }

        return (((info.RequireEnergy * ((item.Level * 3) + itemLevel)) * 4) / 100) + 20;
    }

    public static int GetRequireLeadership(Item item, ItemBalance info)
    {
        if (info.RequireLeadership == 0) return 0;
        int itemLevel = RequirementItemLevel(item, info);
        return (((info.RequireLeadership * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireLevel(Item item, ItemBalance info)
    {
        int value = GetBaseRequireLevel(item, info);

        // Excellent wings, pets and jewellery need 20 more levels (Item.cpp:307-313); weapons and armour do not.
        if (value > 0 && item.Index >= FirstWing && HasExcellentStats(item, info))
        {
            value += 20;
        }

        return value;
    }

    private static int GetBaseRequireLevel(Item item, ItemBalance info)
    {
        if (info.RequireLevel == 0) return 0;
        if (item.Index < 384) // Secciones 0 a 11 (Armas y Armaduras)
        {
            return info.RequireLevel;
        }

        if (item.Index is >= 387 and <= 390) // GET_ITEM(12,3) .. GET_ITEM(12,6) Alas
        {
            return info.RequireLevel + (item.Level * 5);
        }

        if (item.Index is >= 391 and <= 408 && item.Index != 399) // Orbes
        {
            return info.RequireLevel;
        }

        return info.RequireLevel + (item.Level * 4);
    }

    /// <summary>Puerto de CItemManager::CheckItemRequireClass (ItemManager.cpp:682-709).</summary>
    public static bool CanEquipClass(byte playerClass, byte changeUp, ItemBalance info)
    {
        if (playerClass >= info.RequireClass.Length) return false;
        int reqClass = info.RequireClass[playerClass];
        if (reqClass == 0) return false;
        return (changeUp + 1) >= reqClass;
    }

    /// <summary>Puerto de CItemManager::CheckItemMoveToInventory (ItemManager.cpp:758-779) -- compatibilidad de slot.</summary>
    public static bool CanEquipSlot(Item item, int targetSlot, ItemBalance info, ItemBalanceTable itemBalance, PlayerObject player)
    {
        if (info.Slot < 0 || targetSlot < 0 || targetSlot >= Item.InventoryWearSize)
        {
            return false;
        }

        // Weapon/shield/bow/staff slots (0 and 1)
        if (targetSlot is 0 or 1)
        {
            if (info.Slot is not (0 or 1))
            {
                return false;
            }

            if (targetSlot == 0)
            {
                var weapon2 = player.Items[1];
                if (weapon2.IsItem())
                {
                    // Flechas/Pernos (GET_ITEM(4,7)=135, GET_ITEM(4,15)=143) permitidos con arma de 2 manos
                    if (info.TwoHand && weapon2.Index != 135 && weapon2.Index != 143)
                    {
                        return false;
                    }
                }
            }
            else if (targetSlot == 1)
            {
                var weapon1 = player.Items[0];
                if (weapon1.IsItem())
                {
                    var weapon1Info = itemBalance.Get(weapon1.Index);
                    if (weapon1Info != null && weapon1Info.TwoHand && item.Index != 135 && item.Index != 143)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // Slots de anillos (10 y 11)
        if (targetSlot is 10 or 11)
        {
            return info.Slot is 10 or 11;
        }

        // Resto de slots (2=Casco, 3=Armadura, 4=Pantalones, 5=Guantes, 6=Botas, 7=Alas, 8=Mascota, 9=Colgante)
        return info.Slot == targetSlot;
    }

    /// <summary>Port of CItemManager::CheckItemMoveToInventory (ItemManager.cpp:711-780) -- full validation for equipping an item.</summary>
    public static bool CheckItemMoveToInventory(PlayerObject player, Item item, int targetSlot, ItemBalance info, ItemBalanceTable itemBalance)
    {
        if (!item.IsItem()) return false;

        // If the destination slot is in the main inventory (12-107), moving is always allowed
        if (targetSlot >= Item.InventoryWearSize)
        {
            return true;
        }

        if (player.Level < GetRequireLevel(item, info)) return false;
        if (player.Strength < GetRequireStrength(item, info)) return false;
        if (player.Dexterity < GetRequireDexterity(item, info)) return false;
        if (player.Vitality < GetRequireVitality(item, info)) return false;
        if (player.Energy < GetRequireEnergy(item, info)) return false;
        if (player.Leadership < GetRequireLeadership(item, info)) return false;
        if (!CanEquipClass(player.Class, player.ChangeUp, info)) return false;
        if (!CanEquipSlot(item, targetSlot, info, itemBalance, player)) return false;

        return true;
    }
}

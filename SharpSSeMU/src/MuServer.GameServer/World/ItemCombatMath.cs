namespace MuServer.GameServer.World;

/// <summary>
/// Puerto PARCIAL de CItem::Convert (Item.cpp:122-620) y de los getters CItem::GetDamageMin/Max/
/// GetDefense/GetDefenseSuccessRate (Item.cpp:1007-1053) -- solo la parte de escalado LINEAL por nivel
/// de mejora (+0..+15) que aplica siempre a cualquier item, sin las ramas de item excelente
/// (<c>NewOption</c>) ni de set-item (<c>SetOption</c>+bonus de ItemInfo.Level), que dependen de
/// ItemOption.txt/SetItemOption.txt (no portados todavía -- ver World/ItemBalance.cs). Como ningún
/// item generado por este puerto trae esas opciones activadas hoy (siempre NewOption=SetOption=0),
/// esas ramas del original nunca se ejecutarían igual de todas formas.
///
/// Fórmula (Item.cpp:351-403 para daño, 432-450 para DefenseSuccessRate/MagicDamageRate, 457-504 para
/// Defense): <c>valor = base + (nivel*3)</c>, más un "extra" cuadrático (números triangulares) para
/// niveles +10 a +15: <c>+((nivel-9)*(nivel-8))/2</c> -- da +1,+3,+6,+10,+15,+21 en +10..+15. Los
/// escudos (sección 6) escalan la Defensa distinto: flat <c>+nivel</c>, sin el "extra" cuadrático
/// (Item.cpp:457-462).
///
/// Todos los getters devuelven 0 si el item está roto (<see cref="Item.Durability"/>==0), igual que el
/// original.
/// </summary>
public static class ItemCombatMath
{
    private static int LevelScaled(int baseValue, int level)
    {
        int value = baseValue + (level * 3);

        if (level >= 10)
        {
            value += ((level - 9) * (level - 8)) / 2;
        }

        return value;
    }

    private static int ShieldLevelScaled(int baseValue, int level) => baseValue + level;

    public static int GetDamageMin(Item item, ItemBalance info) =>
        item.Durability == 0 ? 0 : LevelScaled(info.DamageMin, item.Level);

    public static int GetDamageMax(Item item, ItemBalance info) =>
        item.Durability == 0 ? 0 : LevelScaled(info.DamageMax, item.Level);

    public static int GetDefense(Item item, ItemBalance info) =>
        item.Durability == 0 ? 0 : info.IsShield ? ShieldLevelScaled(info.Defense, item.Level) : LevelScaled(info.Defense, item.Level);

    public static int GetDefenseSuccessRate(Item item, ItemBalance info) =>
        item.Durability == 0 ? 0 : LevelScaled(info.DefenseSuccessRate, item.Level);

    public static int GetMagicDefense(Item item, ItemBalance info) =>
        item.Durability == 0 ? 0 : LevelScaled(info.MagicDefense, item.Level);

    // AttackSpeed/WalkSpeed NO se escalan por nivel en el original (son valores fijos de la fila de
    // Item.txt) -- se exponen igual acá con el mismo chequeo de durabilidad=0 para uso consistente.
    public static int GetAttackSpeed(Item item, ItemBalance info) => item.Durability == 0 ? 0 : info.AttackSpeed;
    public static int GetWalkSpeed(Item item, ItemBalance info) => item.Durability == 0 ? 0 : info.WalkSpeed;

    /// <summary>Puerto de CItemManager::GetItemDurability (ItemManager.cpp:393-450).</summary>
    public static int GetItemDurability(Item item, ItemBalance info)
    {
        if (!item.IsItem()) return 0;
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

    /// <summary>Puerto de CItem::Convert (Item.cpp:247-280) -- cálculo de requerimientos de stats escalados por nivel del item.</summary>
    public static int GetRequireStrength(Item item, ItemBalance info)
    {
        if (info.RequireStrength == 0) return 0;
        int itemLevel = info.Level;
        return (((info.RequireStrength * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireDexterity(Item item, ItemBalance info)
    {
        if (info.RequireDexterity == 0) return 0;
        int itemLevel = info.Level;
        return (((info.RequireDexterity * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireVitality(Item item, ItemBalance info)
    {
        if (info.RequireVitality == 0) return 0;
        int itemLevel = info.Level;
        return (((info.RequireVitality * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireEnergy(Item item, ItemBalance info)
    {
        if (info.RequireEnergy == 0) return 0;
        int itemLevel = info.Level;
        if (info.Section == 5 && info.Slot == 1) // Báculos (Staffs)
        {
            return (((info.RequireEnergy * (item.Level + itemLevel)) * 3) / 100) + 20;
        }

        return (((info.RequireEnergy * ((item.Level * 3) + itemLevel)) * 4) / 100) + 20;
    }

    public static int GetRequireLeadership(Item item, ItemBalance info)
    {
        if (info.RequireLeadership == 0) return 0;
        int itemLevel = info.Level;
        return (((info.RequireLeadership * ((item.Level * 3) + itemLevel)) * 3) / 100) + 20;
    }

    public static int GetRequireLevel(Item item, ItemBalance info)
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

        // Slots de armas/escudos/arcos/báculos (0 y 1)
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

    /// <summary>Puerto de CItemManager::CheckItemMoveToInventory (ItemManager.cpp:711-780) -- validación completa para equipar un ítem.</summary>
    public static bool CheckItemMoveToInventory(PlayerObject player, Item item, int targetSlot, ItemBalance info, ItemBalanceTable itemBalance)
    {
        if (!item.IsItem()) return false;

        // Si el slot destino es en el inventario principal (12-107), siempre se permite mover
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

using MuServer.GameServer.Config;

namespace MuServer.GameServer.World;

/// <summary> The values as they travel in <c>PMSG_CHAOS_MIX_RECV</c>/<c>_RATE_RECV</c> (<c>type</c>). Named as
/// in the real emulator's <c>ChaosBox.h</c> -- <b>watch out</b>, there is a naming trap there: the constant
/// <c>CHAOS_MIX_WING1</c> (7) triggers the function <c>Wing2Mix(type=0)</c>, and <c>CHAOS_MIX_WING2</c> (11)
/// triggers <c>Wing1Mix()</c> -- they are crossed. Here the enum value is the WIRE one (what the client sends),
/// and the comment on each case in the switch below says which function of the original really runs. </summary>
public enum ChaosMixType
{
    None = 0,
    ChaosItem = 1,
    DevilSquare = 2,
    PlusItem10 = 3,
    PlusItem11 = 4,
    Dinorant = 5,
    Fruit = 6,
    Wing1 = 7,
    BloodCastle = 8,
    Wing2 = 11,
    Wing3 = 24, // "Cape" in the original -- it was not in the previous port, it is added here.
}

/// <param name="Item">The new or upgraded item, if it succeeded.</param> <param name="DeliverViaInventory">true
/// if <see cref="Item"/> is delivered by putting it in an empty inventory slot (the original does it through
/// <c>GDCreateItemSend</c>, a message to the DataServer, not through the same mix packet); false if it goes
/// embedded in the combine reply (<c>PMSG_CHAOS_MIX_SEND.ItemInfo</c>), which is how the original delivers the
/// Plus Item Level upgrade -- there the item is the SAME one you put in, not a new one.</param>
public sealed record ChaosMixResult(bool Success, byte ResultCode, Item? Item, bool DeliverViaInventory,
    int SuccessRate, int RequiredZen);

/// <summary> Port of the <c>CChaosBox</c> combination formulas (ChaosBox.cpp of <c>Source/Emulator 0.99
/// (2.1.7)/GameServer</c>, the real source tree of the emulator -- not a reconstruction). The previous version
/// of this class claimed to be an exact port and was not: the success rate came from an invented formula (`10 +
/// Σ level×5`) instead of the real configuration table (<see cref="GameServerInfoChaosMix"/>, loaded for a long
/// time but never connected to anything), the success item came from an array of 3 fixed weapons instead of the
/// real list of <c>Data/EventItemBag/Special/*.txt</c>, and the success result was packed differently depending
/// on the mix type and this class always did it the same way. <para><b>What IS faithful:</b> the ingredient
/// conditions, the rate and required-zen formulas, and which configuration table each one comes from --
/// verified line by line against the original, including the Wing1/Wing2 naming trap above.</para>
/// <para><b>What is NOT faithful, and why:</b> the item that comes out in the mixes that create a NEW item
/// (Chaos Item, Wing1/2/3, Fruit) is chosen at random among the real candidates of
/// <c>Data/EventItemBag/Special/*.txt</c> -- those ARE the real candidates, verified against the file -- but
/// the <c>DropRate</c> gate of the item bag engine (<c>ItemBagEx</c>) is ignored: in the four files needed for
/// this, that field comes as 0 from the factory, which in the original would make the bag NEVER return anything
/// even with the success roll already won. Replicating that behaviour literally would leave the Chaos Box as
/// "win the roll, nothing happens" just as the file comes -- almost certainly not the real behaviour of a
/// production server (nobody ships a chaos machine that never delivers anything), and the full <c>ItemBagEx</c>
/// engine (per-section weights, class filter, drop rate) is a separate system, shared with Devil Square/Blood
/// Castle/monster drops, that deserves its own port instead of a rushed one as a dependency of this.</para>
/// <para>Devil Square, Dinorant, Blood Castle and the two pet mixes remain unported (as before): they require
/// live event state that this server does not track yet.</para> </summary>
public static class ChaosMixLogic
{
    private static readonly int ItemChaos = Item.GetItem(12, 15);   // Jewel of Chaos
    private static readonly int ItemBless = Item.GetItem(14, 13);   // Jewel of Bless
    private static readonly int ItemSoul = Item.GetItem(14, 14);    // Jewel of Soul
    private static readonly int ItemCreation = Item.GetItem(14, 22); // Jewel of Creation
    private static readonly int ItemLife = Item.GetItem(14, 16);    // Jewel of Life
    private static readonly int ItemDinorant = Item.GetItem(13, 3);
    private static readonly int ItemFeatherLevel0 = Item.GetItem(13, 14); // Loch's Feather / Crest of Monarch (Level tells which)

    private static readonly int[] ChaosWeapons = { Item.GetItem(2, 6), Item.GetItem(4, 6), Item.GetItem(5, 7) };
    private static readonly int[] Wing1Candidates = { Item.GetItem(12, 0), Item.GetItem(12, 1), Item.GetItem(12, 2) };
    private static readonly int[] Wing2Candidates = { Item.GetItem(12, 3), Item.GetItem(12, 4), Item.GetItem(12, 5), Item.GetItem(12, 6) };
    private static readonly int[] CapeCandidates = { Item.GetItem(13, 30) };

    /// <param name="getBuyMoney">CURRENT purchase price of the item (the one <c>ComputeShopBuyPrice</c> already
    /// computes for the shop) -- passed in by the caller so as not to duplicate that formula here.</param>
    /// <param name="execute">true to really combine (0x86). false to only compute the rate (0x88): same
    /// formula, it does not charge, nor empty the box, nor roll the dice.</param>
    public static ChaosMixResult CalculateAndExecuteMix(PlayerObject player, ChaosMixType mixType,
        GameServerInfoChaosMix rates, int[] addLuckSuccessRate2, Func<Item, int> getBuyMoney, bool execute = true)
    {
        var box = player.ChaosBoxItems;
        var items = box.Where(i => i.IsItem()).ToList();

        if (items.Count == 0)
        {
            return new ChaosMixResult(false, 0, null, false, 0, 0);
        }

        return mixType switch
        {
            // CHAOS_MIX_CHAOS_ITEM -> CChaosBox::ChaosItemMix
            ChaosMixType.ChaosItem => MixChaosItem(player, items, rates, getBuyMoney, execute),

            // CHAOS_MIX_PLUS_ITEM_LEVEL1 (3) -> PlusItemLevelMix(tipo=0): tira +9 a +10
            ChaosMixType.PlusItem10 => MixPlusItemLevel(player, items, rates, addLuckSuccessRate2, getBuyMoney, 0, execute),

            // CHAOS_MIX_PLUS_ITEM_LEVEL2 (4) -> PlusItemLevelMix(tipo=1): tira +10 a +11
            ChaosMixType.PlusItem11 => MixPlusItemLevel(player, items, rates, addLuckSuccessRate2, getBuyMoney, 1, execute),

            // CHAOS_MIX_FRUIT -> CChaosBox::FruitMix
            ChaosMixType.Fruit => MixFruit(player, items, rates, execute),

            // CHAOS_MIX_WING1 (7) -> WATCH OUT! in the original this runs Wing2Mix(type=0): Chaos + Loch's
            // Feather (level 0) + a 1st-generation wing -> 2nd-gen wing (Spirits/Soul/ Dragon/Darkness).
            ChaosMixType.Wing1 => MixWing2Family(player, items, rates, getBuyMoney, wingTier: 0, execute),

            // CHAOS_MIX_WING2 (11) -> WATCH OUT! this runs Wing1Mix(): Chaos + an excellent chaos weapon ->
            // 1st-generation wing (Elf/Heaven/Satan).
            ChaosMixType.Wing2 => MixWing1(player, items, rates, getBuyMoney, execute),

            // "Cape" in the original -- Wing2Mix(type=1): Chaos + Crest of Monarch (level 1 of the same item as
            // Loch's Feather) + a 2nd-gen wing -> Cape of Lord.
            ChaosMixType.Wing3 => MixWing2Family(player, items, rates, getBuyMoney, wingTier: 1, execute),

            _ => new ChaosMixResult(false, 0, null, false, 0, 0), // DevilSquare/Dinorant/BloodCastle: no portado
        };
    }

    /// <summary>Port of <c>CItem::OldValue()</c> (Item.cpp:925-951): the Chaos Box rate formula uses these
    /// "old" prices for five specific jewels, NOT the current price from <c>ItemValue.txt</c> -- they are
    /// deliberately different (lower). Using the current price here would make any combination with a
    /// Chaos/Bless/Soul reach 100% in one go, because those prices went up a lot since this formula was
    /// written.</summary>
    private static int OldBuyMoney(Item item, Func<Item, int> getBuyMoney)
    {
        if (item.Index == ItemBless) return 100_000;
        if (item.Index == ItemSoul) return 70_000;
        if (item.Index == ItemChaos) return 40_000;
        if (item.Index == ItemCreation) return 450_000;
        if (item.Index == ItemLife) return 450_000;
        return getBuyMoney(item);
    }

    /// <summary>Puerto de <c>CItem::IsExcItem()</c> (Item.cpp:76-90).</summary>
    private static bool IsExcItem(Item item) => item.Index != ItemDinorant && item.NewOption != 0;

    /// <summary>Approximation of <c>CItem::IsSetItem()</c>, which delegates to <c>CSetItemOption::IsSetItem</c>
    /// (SetItemOption.txt, not ported). A <see cref="Item.SetOption"/> other than 0 is exactly what that field
    /// represents on this wire (low nibble = set-item), so it is enough without needing the full
    /// table.</summary>
    private static bool IsSetItem(Item item) => item.SetOption != 0;

    /// <summary>After any executed attempt (success or failure) the Chaos Box ends up empty. The original only
    /// calls <c>ChaosBoxInit</c> explicitly in the FAILURE branch -- in the success branch of
    /// <c>ChaosItemMix</c>/<c>FruitMix</c>/etc. the consumed ingredients are not cleared right there. It could
    /// not be confirmed what other path clears them in that case (possibly a 20-year-old bug in the original,
    /// possibly something that happens elsewhere that could not be read); it was decided to always clear, which
    /// is the safe option -- the opposite would risk the player recovering their ingredients for free on
    /// closing the window.</summary>
    private static void ClearBox(PlayerObject player) => player.ClearChaosBox();

    private static ChaosMixResult MixChaosItem(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, bool execute)
    {
        int chaosCount = 0, itemCount = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Index == ItemBless || it.Index == ItemSoul)
            {
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Level >= 4 && it.Option3 >= 1)
            {
                itemCount++;
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount == 0 || itemCount == 0)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.ChaosItemMixRate[player.AccountLevel];
        int rate = Math.Min(configured == -1 ? itemMoney / 20_000 : configured, 100);
        int zen = rate * 10_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            int index = ChaosWeapons[Random.Shared.Next(ChaosWeapons.Length)];
            bool luck = Random.Shared.Next(100) < 50;
            byte level = (byte)Random.Shared.Next(0, 3);
            var newItem = new Item { Index = (short)index, Level = level, Durability = 255, Option1 = (byte)(luck ? 1 : 0), Option3 = 1 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>type=0 -> +9 a +10 (CHAOS_MIX_PLUS_ITEM_LEVEL1); type=1 -> +10 a +11
    /// (CHAOS_MIX_PLUS_ITEM_LEVEL2). Puerto de <c>PlusItemLevelMix</c> (ChaosBox.cpp:276-377).</summary>
    private static ChaosMixResult MixPlusItemLevel(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        int[] addLuckSuccessRate2, Func<Item, int> getBuyMoney, int type, bool execute)
    {
        int chaosCount = 0, blessCount = 0, soulCount = 0, itemCount = 0;
        Item? targetItem = null;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos) chaosCount++;
            else if (it.Index == ItemBless) blessCount++;
            else if (it.Index == ItemSoul) soulCount++;
            else if (it.Level == 9 + type) { itemCount++; targetItem = it; }
        }

        if (chaosCount != 1 || soulCount < type + 1 || blessCount < type + 1 || itemCount != 1 || targetItem == null)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int rate = (IsExcItem(targetItem) || IsSetItem(targetItem))
            ? rates.PlusExcSetItemLevelMixRate[type, player.AccountLevel]
            : rates.PlusCommonItemLevelMixRate[type, player.AccountLevel];

        if (targetItem.Option2 != 0) // Skill/Luck option del item -- ver comentario de Item.Option1/2
        {
            rate += addLuckSuccessRate2[player.AccountLevel];
        }

        rate = Math.Min(rate, 100);
        int zen = 2_000_000 * (type + 1);

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            var upgraded = new Item
            {
                Index = targetItem.Index,
                Level = (byte)(targetItem.Level + 1),
                Durability = targetItem.Durability,
                Serial = targetItem.Serial,
                Option1 = targetItem.Option1,
                Option2 = targetItem.Option2,
                Option3 = targetItem.Option3,
                NewOption = targetItem.NewOption,
                SetOption = targetItem.SetOption,
            };
            ClearBox(player);
            return new ChaosMixResult(true, 1, upgraded, false, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    private static ChaosMixResult MixFruit(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates, bool execute)
    {
        int chaosCount = items.Count(i => i.Index == ItemChaos);
        int creationCount = items.Count(i => i.Index == ItemCreation);

        if (chaosCount != 1 || creationCount != 1)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int rate = Math.Min(rates.FruitMixRate[player.AccountLevel], 100);
        int zen = 3_000_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            byte level = (byte)Random.Shared.Next(0, 4); // 0-3, no 0-4: GetLargeRand()%4 en el original
            var newItem = new Item { Index = (short)Item.GetItem(13, 15), Level = level, Durability = 0 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>Port of <c>Wing1Mix</c> (ChaosBox.cpp:744-839) -- Chaos + an excellent chaos weapon (level>=4,
    /// Option3>=1) -> 1st-generation wing (Elf/Heaven/Satan). On the wire this is triggered with <see
    /// cref="ChaosMixType.Wing2"/> (11), not Wing1 -- see the comment of <see cref="ChaosMixType"/>.</summary>
    private static ChaosMixResult MixWing1(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, bool execute)
    {
        int chaosCount = 0, chaosWeaponCount = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Index == ItemBless || it.Index == ItemSoul)
            {
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (Array.IndexOf(ChaosWeapons, (int)it.Index) >= 0 && it.Level >= 4 && it.Option3 >= 1)
            {
                chaosWeaponCount++;
                itemMoney += getBuyMoney(it);
            }
            else if (it.Level >= 4 && it.Option3 >= 1)
            {
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount == 0 || chaosWeaponCount == 0)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.Wing1MixRate[player.AccountLevel];
        int rate = Math.Min(configured == -1 ? itemMoney / 20_000 : configured, 100);
        int zen = rate * 10_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            int index = Wing1Candidates[Random.Shared.Next(Wing1Candidates.Length)];
            var newItem = new Item { Index = (short)index, Level = 0, Durability = 255 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>Port of <c>Wing2Mix</c> (ChaosBox.cpp:515-636). <paramref name="wingTier"/>=0 -> Chaos + Loch's
    /// Feather (level 0) + 1st-gen wing -> 2nd-gen wing (triggered with <see cref="ChaosMixType.Wing1"/>=7 on
    /// the wire); =1 -> Chaos + Crest of Monarch (same item index, level 1) + 2nd-gen wing -> Cape of Lord
    /// (triggered with <see cref="ChaosMixType.Wing3"/> =24).</summary>
    private static ChaosMixResult MixWing2Family(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, int wingTier, bool execute)
    {
        int chaosCount = 0, featherCount = 0, wingItemCount = 0, wingItemMoney = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
            }
            else if (it.Index == ItemFeatherLevel0 && it.Level == wingTier)
            {
                featherCount++;
            }
            // The "wing" ingredient this mix asks for is ALWAYS 1st generation (12,0-12,2), for both tiers --
            // the original does not condition it on `type` here (Wing2Mix, the WingItemCount counting block
            // uses that fixed range). What changes with `wingTier` is the Feather/Crest above and the result:
            // 2nd-generation wings for tier=0, Cape of Lord for tier=1.
            else if (Array.IndexOf(Wing1Candidates, (int)it.Index) >= 0)
            {
                wingItemCount++;
                wingItemMoney += getBuyMoney(it);
            }
            else if (IsExcItem(it) && it.Level >= 4)
            {
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount != 1 || featherCount != 1 || wingItemCount != 1)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.Wing2MixRate[player.AccountLevel];
        int rate;
        int cap;
        if (configured == -1)
        {
            rate = (wingItemMoney / 4_000_000) + (itemMoney / 40_000);
            cap = 90;
        }
        else
        {
            rate = configured;
            cap = 100;
        }
        rate = Math.Min(rate, cap);
        int zen = 5_000_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            var candidates = wingTier == 0 ? Wing2Candidates : CapeCandidates;
            int index = candidates[Random.Shared.Next(candidates.Length)];
            var newItem = new Item { Index = (short)index, Level = 0, Durability = 255 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }
}

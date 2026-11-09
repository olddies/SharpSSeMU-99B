using MuServer.GameServer.Protocol;

namespace MuServer.GameServer.World;

/// <summary>
/// The Dark Horse and the Dark Raven as items that grow: what a kill gives them (CObjectManager::CharacterPetLevelUp),
/// what the DataServer tells them when the player enters (DGPetItemInfoRecv) and which of them are written back
/// (GDPetItemInfoSaveSend). The horse is worn in the helper slot and the raven in the left hand.
/// </summary>
public sealed class PetManager
{
    private readonly ItemBalanceTable _items;
    private readonly Func<PetRules> _rules;

    public PetManager(ItemBalanceTable items, Func<PetRules>? rules = null)
    {
        _items = items;
        _rules = rules ?? (() => PetRules.Current);
    }

    /// <summary>Port of CharacterPetLevelUp (ObjectManager.cpp:1092): the pets the player wears gain a share of the
    /// experience a kill gave them, and the client is told. A pet that went up a level the player no longer qualifies
    /// for gives the level back.</summary>
    public async Task GrantExperienceAsync(PlayerObject player, long experience, CancellationToken ct)
    {
        int amount = (int)Math.Clamp(experience, 0, int.MaxValue);

        await GrowAsync(player, Item.SlotHelper, 1, amount, ct);   // the Dark Horse: type 1
        await GrowAsync(player, Item.SlotWeapon2, 0, amount, ct);  // the Dark Raven: type 0
    }

    private async Task GrowAsync(PlayerObject player, int slot, byte clientType, int amount, CancellationToken ct)
    {
        var pet = player.Items[slot];
        int expected = clientType == 1 ? Item.DarkHorse : Item.DarkRaven;

        if (!pet.IsItem() || pet.Index != expected)
        {
            return;
        }

        var rules = _rules();

        if (!pet.AddPetExp(amount, rules))
        {
            await SendInfoAsync(player, clientType, 0, slot, pet, ct);
            return;
        }

        var info = _items.Get(pet.Index);

        if (info == null || !ItemCombatMath.IsValidItem(player, pet, info))
        {
            if (pet.DecPetExp(amount, rules))
            {
                await SendInfoAsync(player, clientType, 0, slot, pet, ct);
            }
        }
        else
        {
            await SendInfoAsync(player, clientType, 0xFE, slot, pet, ct);
        }
    }

    private static Task SendInfoAsync(PlayerObject player, byte clientType, byte flag, int slot, Item pet, CancellationToken ct) =>
        player.Session.SendAsync(QuestPacketBuilder.PetItemInfoSend(clientType, flag, (byte)slot, (byte)pet.PetLevel, pet.PetExp), ct);

    /// <summary>What the wear of the pet on the helper slot did to its wearer.</summary>
    [Flags]
    public enum HelperWearOutcome
    {
        None = 0,

        /// <summary>The pet lost a whole point: the wearer's stats may change (the Dark Horse at nothing gives no defense).</summary>
        StatsChanged = 1,

        /// <summary>The pet is spent and gone: the wearer's appearance must be sent again.</summary>
        Vanished = 2,
    }

    /// <summary>Port of CAttack::DamageSprite (Attack.cpp:728): the pet on the helper slot wears down with the blows its
    /// wearer takes -- the Guardian Angel three points for every hundred of damage, the Imp two, the horns of Uniria and
    /// Dinorant one (scaled by <c>GuardianDurabilityRate</c>) -- and goes when it is spent. The Dark Horse wears slower, by
    /// <c>DarkHorseSprite</c> (a thousand and a half "small" points to a whole one), and is never taken away: it stays
    /// at nothing, a pet that gives no defense. The wearer is told the durability.</summary>
    public async Task<HelperWearOutcome> WearHelperAsync(PlayerObject target, int damage, CancellationToken ct)
    {
        var helper = target.Items[Item.SlotHelper];

        if (!helper.IsItem())
        {
            return HelperWearOutcome.None;
        }

        var rules = _rules();

        if (helper.Index == Item.DarkHorse)
        {
            return await WearHorseAsync(target, helper, damage, rules, ct);
        }

        // 1/GuardianDurabilityRate in hundredths; the player's own rate (lpObj->GuardianDurabilityRate) is 100 here.
        float value = (1.0f / rules.GuardianDurabilityRate) * 100;
        float points = helper.Index switch
        {
            var i when i == Item.GetItem(13, 0) => 3.0f, // Guardian Angel
            var i when i == Item.GetItem(13, 1) => 2.0f, // Imp
            var i when i == Item.GetItem(13, 2) => 1.0f, // Horn of Uniria
            var i when i == Item.GetItem(13, 3) => 1.0f, // Horn of Dinorant
            _ => 0.0f,
        };

        if (points == 0)
        {
            return HelperWearOutcome.None;
        }

        bool spent = helper.WearDown((damage * (points * value)) / 100);
        await target.Session.SendAsync(ItemPacketBuilder.ItemDurSend((byte)Item.SlotHelper, helper.Durability, 0), ct);

        if (!spent)
        {
            return HelperWearOutcome.None;
        }

        target.Items[Item.SlotHelper] = Item.Empty();
        await target.Session.SendAsync(ItemPacketBuilder.ItemDeleteSend((byte)Item.SlotHelper, 0), ct);
        return HelperWearOutcome.Vanished | HelperWearOutcome.StatsChanged;
    }

    private static async Task<HelperWearOutcome> WearHorseAsync(PlayerObject target, Item horse, int damage, PetRules rules, CancellationToken ct)
    {
        if (horse.Durability < 1)
        {
            return HelperWearOutcome.None;
        }

        horse.DurabilitySmall += ((damage * 2) / 100) + 1;

        if (horse.DurabilitySmall <= (1500 * rules.PetDurabilityRate) / 100)
        {
            return HelperWearOutcome.None;
        }

        horse.Durability--;
        horse.DurabilitySmall = 0;

        await target.Session.SendAsync(ItemPacketBuilder.ItemDurSend((byte)Item.SlotHelper, horse.Durability, 0), ct);
        await SendInfoAsync(target, 1, 0, Item.SlotHelper, horse, ct);
        return HelperWearOutcome.StatsChanged;
    }

    /// <summary>Port of CItem::PetValue (Item.cpp:958): the Dark Horse is worth two million for every level it has and
    /// the Dark Raven one million; a shop pays a third of that (before the rounding every price gets).</summary>
    public static long SellValue(Item pet) => (pet.Index == Item.DarkHorse ? 2_000_000L : 1_000_000L) * pet.PetLevel / 3;

    /// <summary>The slot and serial of every pet in a container, for GDPetItemInfoSend.</summary>
    public static List<(byte Slot, uint Serial)> PetsToRequest(Item[] container)
    {
        var pets = new List<(byte, uint)>();

        for (int slot = 0; slot < container.Length; slot++)
        {
            if (container[slot].IsItem() && container[slot].IsPetItem)
            {
                pets.Add(((byte)slot, container[slot].Serial));
            }
        }

        return pets;
    }

    /// <summary>The serial, level and experience of the pets whose data the DataServer has given, for
    /// GDPetItemInfoSaveSend; one it never answered for is not written back over what it keeps.</summary>
    public static List<(uint Serial, byte Level, uint Experience)> PetsToSave(Item[] container)
    {
        var pets = new List<(uint, byte, uint)>();

        foreach (var item in container)
        {
            if (item.IsItem() && item.IsPetItem && item.PetInfoLoaded)
            {
                pets.Add((item.Serial, (byte)item.PetLevel, item.PetExp));
            }
        }

        return pets;
    }

    /// <summary>Port of DGPetItemInfoRecv (DSProtocol.cpp:611): each pet takes its level and experience if the item in
    /// that slot is still the one with that serial. True when one of those worn changed, so the stats must be worked
    /// out again (the Dark Horse's defense).</summary>
    public bool ApplyInfo(PlayerObject player, byte type, IEnumerable<(byte Slot, uint Serial, byte Level, uint Experience)> pets)
    {
        var container = type == 0 ? player.Items : player.WarehouseItems;
        bool wornChanged = false;

        foreach (var (slot, serial, level, experience) in pets)
        {
            if (slot >= container.Length || !container[slot].IsItem() || container[slot].Serial != serial)
            {
                continue;
            }

            container[slot].SetPetItemInfo(level, experience, _rules());
            wornChanged |= type == 0 && slot < Item.InventoryWearSize;
        }

        return wornChanged;
    }
}

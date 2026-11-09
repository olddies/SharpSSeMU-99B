namespace MuServer.GameServer.World;

/// <summary> Reduced port of CItem (Item.h/.cpp) — only the fields that travel over the network and are
/// persisted in DataServer (not the original's full struct, which also loads ~30 balance fields from Item.txt
/// at runtime; that is left for when Phase 3 needs to really compute damage/defense). Identification constants
/// (ItemManager.h:11-16, correct source tree "Emulator 0.99 (2.1.7)/GameServer" -- confirmed by stdafx.h:7,
/// GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]", which matches this project's name): an item is
/// identified by a single WORD "Index" packed as section*32 + sub-index (section = category:
/// sword/axe/.../helm/armor/etc., 0-15; sub-index = item within the category, 0-31) -- GET_ITEM(section,sub) =
/// section*MAX_ITEM_TYPE+sub with MAX_ITEM_TYPE=32, MAX_ITEM=MAX_ITEM_SECTION*MAX_ITEM_TYPE=512 (16*32),
/// MAX_ITEM_INFO=5. HISTORICAL NOTE: an earlier pass of this port migrated these constants to MaxItemType=512 /
/// 12-byte ItemInfo based on `Source/Source/Emulator/GameServer/` (without a version suffix), which is a tree
/// of a MUCH later season (it has GAMESERVER_UPDATE, sockets, JewelOfHarmony, MAX_ITEM_TYPE=512,
/// MAX_ITEM_INFO=12) and is NOT this server's real code. That tree has absolutely no relation to "0.99B CHS
/// SSeMU_2.1.7" -- it was a research mistake. This class was reverted to the real 32-stride/5-byte format,
/// verified line by line against ItemManager.h/.cpp and Viewport.cpp of the correct tree. There are no sockets,
/// JewelOfHarmony, pentagram, Muun nor periodic items in this build -- those fields and their logic were
/// removed. </summary>
public sealed class Item
{
    public const int MaxItemSection = 16;
    public const int MaxItemType = 32;
    public const int MaxItem = MaxItemSection * MaxItemType; // 512

    public const int WireByteSize = 5; // MAX_ITEM_INFO
    public const int DbByteSize = 16; // slot completo en DataServer (solo bytes 0-8 con datos)

    public const int InventoryWearSize = 12; // slots 0-11: equipo puesto
    public const int InventoryMainSize = 76;
    public const int InventorySize = InventoryWearSize + InventoryMainSize + 20; // 108 (INVENTORY_FULL_SIZE)

    public static int GetItem(int section, int sub) => (section * MaxItemType) + sub;

    // eInventorySlot (ItemManager.h:29-43)
    public const int SlotWeapon1 = 0;
    public const int SlotWeapon2 = 1;
    public const int SlotHelm = 2;
    public const int SlotArmor = 3;
    public const int SlotPants = 4;
    public const int SlotGloves = 5;
    public const int SlotBoots = 6;
    public const int SlotWing = 7;
    public const int SlotHelper = 8;
    public const int SlotAmulet = 9;
    public const int SlotRing1 = 10;
    public const int SlotRing2 = 11;

    public short Index { get; set; } = -1; // -1 = empty slot (equivalent to IsItem()==false)
    public byte Level { get; set; } // 0-15
    public byte Durability { get; set; }
    public uint Serial { get; set; }
    public byte Option1 { get; set; } // skill (0/1) -- bit 7 of byte 1, CItem::m_Option1
    public byte Option2 { get; set; } // luck (0/1) -- bit 2 of byte 1, CItem::m_Option2
    public byte Option3 { get; set; } // additional option 0-7 (+4 each): bits 0-1 of byte 1 plus bit 6 of byte 3
    public byte NewOption { get; set; } // excellent options, one bit each (bits 0-5)
    public byte SetOption { get; set; } // set-item, nibble bajo

    public bool IsItem() => Index >= 0 && Index < MaxItem;

    public static Item Empty() => new();

    // ---- Dark Horse and Dark Raven: a pet has a level and experience of its own (CItem::m_PetItemLevel and
    // m_PetItemExp), kept by the DataServer under the item's serial. ----
    public static readonly int DarkHorse = GetItem(13, 4);
    public static readonly int DarkRaven = GetItem(13, 5);

    public bool IsPetItem => Index == DarkHorse || Index == DarkRaven;

    /// <summary>Whether the DataServer has told this pet its level and experience (m_LoadPetItemInfo). Until then
    /// a pet gains nothing and is not saved, so a failed load cannot overwrite what the database has.</summary>
    public bool PetInfoLoaded { get; set; }

    public int PetLevel { get; set; } = 1;

    /// <summary>The wear a Dark Raven or Dark Horse has taken that is not yet a whole point of durability
    /// (m_DurabilitySmall).</summary>
    public int DurabilitySmall { get; set; }

    /// <summary>The part of a point that the guardian pets' wear has left: the original keeps the durability as a float and
    /// shows the whole part, so 254.5 shows 254 and the next half point takes it to 254.0.</summary>
    public float DurabilityFraction { get; set; }

    /// <summary>Takes <paramref name="amount"/> points of durability off, keeping the fraction (m_Durability -= ...). True
    /// when what is left is under one point: the item is spent.</summary>
    public bool WearDown(float amount)
    {
        float exact = Durability + DurabilityFraction - amount;

        if (exact < 1)
        {
            Durability = 0;
            DurabilityFraction = 0;
            return true;
        }

        Durability = (byte)Math.Floor(exact);
        DurabilityFraction = exact - Durability;
        return false;
    }

    public uint PetExp { get; set; }

    /// <summary>Port of CItem::SetPetItemInfo (Item.cpp:985): the level is capped at the server's maximum, and the
    /// experience kept inside the level.</summary>
    public void SetPetItemInfo(int level, uint experience, PetRules rules)
    {
        if (!IsPetItem)
        {
            return;
        }

        PetInfoLoaded = true;
        PetLevel = Math.Min(level, rules.MaxLevel);
        PetExp = Math.Max(experience, rules.Experience(PetLevel));

        if (PetExp >= rules.Experience(PetLevel + 1))
        {
            PetExp = rules.Experience(PetLevel);
        }
    }

    /// <summary>Port of CItem::AddPetItemExp (Item.cpp:1320): a share of the player's experience goes to the pet. True
    /// when the pet went up a level. A pet with no durability, or whose data has not arrived, gains nothing.</summary>
    public bool AddPetExp(int amount, PetRules rules)
    {
        if (Durability == 0 || !PetInfoLoaded)
        {
            return false;
        }

        if (PetLevel < 1)
        {
            PetLevel = 1;
            PetExp = 0;
            return true;
        }

        amount /= rules.RateDivisor;

        if (IsPetItem)
        {
            if (PetLevel >= rules.MaxLevel)
            {
                return false;
            }

            if ((long)PetExp + amount >= rules.Experience(PetLevel + 1))
            {
                PetLevel++;
                PetExp = rules.Experience(PetLevel);
                return true;
            }
        }

        PetExp += (uint)amount;
        return false;
    }

    /// <summary>Port of CItem::DecPetItemExp (Item.cpp:1359): takes back what <see cref="AddPetExp"/> gave, going down
    /// a level when it has to. True when the pet went down a level.</summary>
    public bool DecPetExp(int amount, PetRules rules)
    {
        if (Durability == 0 || !PetInfoLoaded)
        {
            return false;
        }

        if (PetLevel < 1)
        {
            PetLevel = 1;
            PetExp = 0;
            return true;
        }

        amount /= rules.RateDivisor;

        if (IsPetItem && (long)PetExp - amount < rules.Experience(PetLevel))
        {
            PetLevel--;
            PetExp = rules.Experience(PetLevel + 1) - 1;
            return true;
        }

        PetExp -= (uint)amount;
        return false;
    }

    /// <summary> Port of CItemManager::ItemByteConvert (ItemManager.cpp:1593-1612) -- 5-byte format
    /// (MAX_ITEM_INFO=5) that travels in every client-server packet (inventory, item on the ground,
    /// get/move/buy, shop, etc.). The real C++ also writes a phantom byte lpMsg[5]=0 one byte beyond the
    /// declared 5-byte array (overwriting the next field of the struct, which in our packet builders is already
    /// written explicitly separately) -- that is why only the 5 real bytes are emitted here. </summary>
    public void ToWireBytes(Span<byte> dst)
    {
        dst.Clear();

        if (!IsItem())
        {
            return; // 5 bytes en 0 = "sin item"
        }

        dst[0] = (byte)(Index & 0xFF);

        dst[1] = 0;
        dst[1] |= (byte)(Level * 8);
        dst[1] |= (byte)(Option1 * 128);
        dst[1] |= (byte)(Option2 * 4);
        dst[1] |= (byte)(Option3 & 3);

        dst[2] = Durability;

        dst[3] = 0;
        dst[3] |= (byte)((Index & 256) >> 1);
        dst[3] |= (byte)(Option3 > 3 ? 64 : 0);
        dst[3] |= NewOption;

        dst[4] = SetOption;
    }

    /// <summary>Port of CItemManager::DBItemByteConvert (ItemManager.cpp:1615-1650) -- 16-byte/slot format that
    /// DataServer uses for persistence (Inventory[INVENTORY_SIZE][16] in DSProtocol.h), of which only bytes 0-8
    /// carry real data; byte9 is always 0 (with MAX_ITEM_TYPE=32 the full index (0-511) already fits in 9 bits
    /// -- the whole byte0 + 1 more bit in byte7 -- so no more index bits are needed) and bytes10-15 are never
    /// written (there are no sockets/JewelOfHarmony/etc. in this build). Empty slot = 16 bytes at 0xFF
    /// (memset(lpMsg,0xFF,16) in the original). It also replicates the special case `m_Index==GET_ITEM(13,19)`
    /// (ItemManager.cpp:1622) that forces the slot to be empty in the DB even if the item exists in
    /// memory.</summary>
    public void ToDbBytes(Span<byte> dst)
    {
        if (!IsItem() || Index == GetItem(13, 19))
        {
            dst.Slice(0, 16).Fill(0xFF);
            return;
        }

        dst.Slice(0, 16).Clear();

        dst[0] = (byte)(Index & 0xFF);
        dst[1] = (byte)((Level * 8) | (Option1 * 128) | (Option2 * 4) | (Option3 & 3));
        dst[2] = Durability;

        dst[3] = (byte)((Serial >> 24) & 0xFF);
        dst[4] = (byte)((Serial >> 16) & 0xFF);
        dst[5] = (byte)((Serial >> 8) & 0xFF);
        dst[6] = (byte)(Serial & 0xFF);

        dst[7] = (byte)(((Index & 256) >> 1) | (Option3 > 3 ? 64 : 0) | NewOption);
        dst[8] = (byte)(SetOption & 15);

        dst[9] = 0;
        // bytes 10-15 stay at 0 (Clear() above) -- there are no real fields to write there.
    }

    /// <summary>Port of CItemManager::ConvertItemByte (ItemManager.cpp:1653-1688). An empty slot is detected
    /// the same as the original: byte0==0xFF && (byte7&0x80)==0x80 && (byte9&0xF0)==0xF0. The index is rebuilt
    /// ONLY from byte0 + the high bit in byte7 (Index = b0 | ((b7&0x80)&lt;&lt;1)); the term `(byte9&0xF0)*32`
    /// that appears in the original C++ is always 0 in this build (DBItemByteConvert never writes anything
    /// other than 0 in byte9), so it is omitted directly instead of reading it.</summary>
    public static Item FromDbBytes(ReadOnlySpan<byte> src)
    {
        byte b0 = src[0], b1 = src[1], b2 = src[2], b7 = src[7], b8 = src[8], b9 = src[9];

        if (b0 == 0xFF && (b7 & 0x80) == 0x80 && (b9 & 0xF0) == 0xF0)
        {
            return Empty();
        }

        int index = b0 | ((b7 & 0x80) << 1);
        uint serial = ((uint)src[3] << 24) | ((uint)src[4] << 16) | ((uint)src[5] << 8) | src[6];

        var item = new Item
        {
            Index = (short)index,
            Level = (byte)((b1 / 8) & 0x0F),
            Option1 = (byte)((b1 / 128) & 1),
            Option2 = (byte)((b1 / 4) & 1),
            Option3 = (byte)((b1 & 3) | (((b7 & 64) != 0) ? 4 : 0)),
            Durability = b2,
            Serial = serial,
            NewOption = (byte)(b7 & 0x3F),
            SetOption = (byte)(b8 & 15),
        };

        return item;
    }

    /// <summary> Rebuilds an item from the compact 5-byte format {b0,b1,b7,b8,b9} used by
    /// SDHP_CHARACTER_LIST.Inventory (DataServerProtocol.cpp::GDCharacterListRecv, and the C# port in
    /// DataServerProtocolHandler.CompactInventory) for the character selection preview -- they are exactly the
    /// same 5 bytes that <see cref="FromDbBytes"/> reads from the full 16-byte format (the other bytes do not
    /// affect index/level/glow, so a "synthetic" 16-byte buffer is built with the rest at 0 and the same
    /// reading is reused.</summary>
    public static Item FromCompactPreviewBytes(byte b0, byte b1, byte b7, byte b8, byte b9)
    {
        Span<byte> synthetic = stackalloc byte[16];
        synthetic.Clear();
        synthetic[0] = b0;
        synthetic[1] = b1;
        synthetic[7] = b7;
        synthetic[8] = b8;
        synthetic[9] = b9;
        return FromDbBytes(synthetic);
    }
}

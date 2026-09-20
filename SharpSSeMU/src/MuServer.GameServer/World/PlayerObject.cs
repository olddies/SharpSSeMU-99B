using MuServer.GameServer.Config;
using MuServer.GameServer.Net;

namespace MuServer.GameServer.World;

/// <summary> Partial port of OBJECTSTRUCT (User.h) for a player -- only the fields needed to "enter the world"
/// (Phase 2): identity/stats for the character info packet, position for map/movement/viewport, and the raw
/// inventory/skill/quest/effect bytes that DataServer already sends complete (stored as opaque blobs until
/// Phase 3 really interprets them). Unlike the network packets (which are ported byte by byte), this is an
/// idiomatic C# class -- the original's global gObj[10000] is replaced here by PlayerRegistry + this class per
/// player. </summary>
public sealed class PlayerObject
{
    public required int Index { get; init; }
    public required ClientSession Session { get; init; }

    public string Account { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GuildName { get; set; } = string.Empty;
    public byte GuildStatus { get; set; } = 0; // 0x80 = Master, 0x00 = Member
    public byte Class { get; set; }
    public ushort Level { get; set; }
    public uint LevelUpPoint { get; set; }
    public uint Experience { get; set; }
    public uint Money { get; set; }

    /// <summary>Account level (0-3, AL0..AL3 in the GameServerInfo .dat files) -- in the original it
    /// distinguishes normal/premium accounts for several rates (ChaosMixRate, drop rate, MaxStatPoint, etc.).
    /// Port of <c>gObj[index].AccountLevel = lpMsg->AccountLevel</c> (JSProtocol.cpp:85): JoinServer already
    /// computes this value for real (WZ_GetAccountLevel, with expiry) and sends it to GameServer when the
    /// account connects -- see <see cref="ClientSession.AccountLevel"/>, stored there because it arrives before
    /// this object exists. It is copied only once on entering the world, like the rest of the identity fields
    /// (it does not change while the session stays connected).</summary>
    public int AccountLevel { get; set; }
    public uint Strength { get; set; }
    public uint Dexterity { get; set; }
    public uint Vitality { get; set; }
    public uint Energy { get; set; }
    public uint Leadership { get; set; }
    public uint Life { get; set; }
    public uint MaxLife { get; set; }
    public uint Mana { get; set; }
    public uint MaxMana { get; set; }
    public uint BP { get; set; }
    public uint MaxBP { get; set; }
    public byte PKLevel { get; set; }
    public uint PKCount { get; set; }
    public uint PKTime { get; set; }
    public byte CtlCode { get; set; }
    public ushort FruitAddPoint { get; set; }
    public ushort FruitSubPoint { get; set; }
    public uint Reset { get; set; }
    public uint MasterReset { get; set; }

    /// <summary>Port of lpObj->ChatLimitTime -- not consumed yet (no mute system), but it is stored as received
    /// from DataServer so it can be returned unchanged in <see
    /// cref="ClientProtocolHandler.SaveCharacterAsync"/> (see the doc-comment there: without this field, every
    /// save would overwrite the row's real value with 0).</summary>
    public uint ChatLimitTime { get; set; }

    /// <summary>Event entry counters (Blood/Chaos/Devil Square) as DataServer sends them on entering the world
    /// -- none of the three systems is ported yet (Devil Square has its own ticket limit in <see
    /// cref="World.DevilSquareManager"/>, separate from this historical counter), so they are stored untouched
    /// for the same reason as <see cref="ChatLimitTime"/>.</summary>
    public ushort BCCount { get; set; }
    public ushort CCCount { get; set; }
    public ushort DSCount { get; set; }

    /// <summary>Port of lpObj->CharSaveTime (ObjectManager.cpp:1034-1038): 60s throttle for the save triggered
    /// by gaining experience/levelling up. <see cref="DateTime.MinValue"/> = "never saved" (equivalent to the
    /// field at 0 in a freshly zeroed OBJECTSTRUCT), which makes the first check after entering the world able
    /// to trigger a save.</summary>
    public DateTime CharSaveTime { get; set; } = DateTime.MinValue;

    /// <summary>Port of lpObj->AutoSaveTime (User.cpp:2537-2541): 10-minute throttle for the unconditional
    /// periodic autosave (it runs for any connected player, not only after combat) -- this is the real
    /// mechanism that guarantees position/progress is persisted even in a session without killing monsters.
    /// Same sentinel as <see cref="CharSaveTime"/>.</summary>
    public DateTime AutoSaveTime { get; set; } = DateTime.MinValue;

    // Opaque blobs as DataServer sends them -- Skill/Quest/Effect are only interpreted in later phases (combat
    // = skills, quest/events = social/special phases). Inventory is kept as the raw 1728-byte blob (source of
    // truth for saving to DataServer) BUT it is also decoded into <see cref="Items"/> as soon as it arrives
    // (Phase 3) -- the two are kept in sync by <see cref="SetItem"/>, which writes on both sides at once
    // instead of having to re-serialise all 108 slots every time a single one changes.
    public byte[] Inventory { get; set; } = new byte[1728];
    public byte[] Skill { get; set; } = new byte[180];
    public byte[] Quest { get; set; } = Enumerable.Repeat((byte)0xFF, 50).ToArray();
    public byte[] Effect { get; set; } = new byte[208];

    /// <summary>Simplified port of lpObj->Interface (use/type/state, User.h) + TargetShopNumber -- non-null
    /// while the player has an NPC's buy window open (between CGNpcTalkRecv and
    /// CGNpcTalkCloseRecv/CGItemBuyRecv/CGItemSellRecv). Only INTERFACE_SHOP is ported in this pass
    /// (Trade/Warehouse/PersonalShop are left for later, see README).</summary>
    public int? TargetShopNumber { get; set; }

    /// <summary>Puerto de lpObj->Inventory[INVENTORY_SIZE] (CItem por slot) -- Fase 3. Se llena
    /// decodificando <see cref="Inventory"/> (ver <see cref="DecodeInventory"/>) apenas se recibe
    /// SDHP_CHARACTER_INFO_RECV.</summary>
    public Item[] Items { get; } = CreateEmptyItems();

    private static Item[] CreateEmptyItems()
    {
        var items = new Item[Item.InventorySize];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public bool InTrade { get; set; }
    public int TradeTargetIndex { get; set; } = -1;
    public bool TradeOk { get; set; }
    public uint TradeMoney { get; set; }
    public Item[] TradeItems { get; } = CreateEmptyTradeItems();

    private static Item[] CreateEmptyTradeItems()
    {
        var items = new Item[32];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearTrade()
    {
        InTrade = false;
        TradeTargetIndex = -1;
        TradeOk = false;
        TradeMoney = 0;
        for (int n = 0; n < TradeItems.Length; n++)
        {
            TradeItems[n] = Item.Empty();
        }
    }

    public bool InWarehouse { get; set; }
    public byte WarehouseLock { get; set; }
    public ushort WarehousePassword { get; set; }
    public uint WarehouseMoney { get; set; }
    public Item[] WarehouseItems { get; } = CreateEmptyWarehouseItems();

    private static Item[] CreateEmptyWarehouseItems()
    {
        var items = new Item[120];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearWarehouse()
    {
        InWarehouse = false;
        WarehouseLock = 0;
        WarehousePassword = 0;
        WarehouseMoney = 0;
        for (int n = 0; n < WarehouseItems.Length; n++)
        {
            WarehouseItems[n] = Item.Empty();
        }
    }

    public bool InChaosBox { get; set; }
    public Item[] ChaosBoxItems { get; } = CreateEmptyChaosBoxItems();
    public byte[] ChaosBoxMap { get; } = new byte[32];

    private static Item[] CreateEmptyChaosBoxItems()
    {
        var items = new Item[32];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearChaosBox()
    {
        InChaosBox = false;
        for (int n = 0; n < ChaosBoxItems.Length; n++)
        {
            ChaosBoxItems[n] = Item.Empty();
        }
        Array.Fill(ChaosBoxMap, (byte)0xFF);
    }

    public bool HasSkill(short skillId)
    {
        for (int i = 0; i < 60; i++)
        {
            int s = Skill[i * 3] | ((Skill[i * 3 + 1] & 7) * 255);
            if (s == skillId && Skill[i * 3] != 0xFF) return true;
        }
        return false;
    }

    public int AddSkill(short skillId, byte level = 0)
    {
        if (HasSkill(skillId)) return -1;

        for (int i = 0; i < 60; i++)
        {
            int s = Skill[i * 3] | ((Skill[i * 3 + 1] & 7) * 255);
            if (s <= 0 || Skill[i * 3] == 0xFF)
            {
                Skill[i * 3] = (byte)(skillId & 0xFF);
                Skill[i * 3 + 1] = (byte)((level << 3) | ((skillId / 255) & 7));
                Skill[i * 3 + 2] = 0;
                return i;
            }
        }

        return -1;
    }

    /// <summary>Decodes <see cref="Inventory"/> (DataServer's raw blob, 16 bytes/slot) into <see cref="Items"/>
    /// -- port of the CharacterInfoSet loop that calls ConvertItemByte for each slot (ObjectManager.cpp, right
    /// before CharacterMakePreviewCharSet).</summary>
    public void DecodeInventory()
    {
        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            int offset = slot * 16;

            if (offset + 16 > Inventory.Length)
            {
                Items[slot] = Item.Empty();
                continue;
            }

            Items[slot] = Item.FromDbBytes(Inventory.AsSpan(offset, 16));
        }
    }

    /// <summary>Writes an item into a slot, keeping <see cref="Items"/> and the raw <see cref="Inventory"/>
    /// blob in sync (so that a later save to DataServer reflects the change without having to re-decode
    /// everything).</summary>
    public void SetItem(int slot, Item item)
    {
        Items[slot] = item;

        int offset = slot * 16;

        if (offset + 16 <= Inventory.Length)
        {
            item.ToDbBytes(Inventory.AsSpan(offset, 16));
        }
    }

    // Position / world
    public byte Map { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte TX { get; set; }
    public byte TY { get; set; }
    public byte OldX { get; set; }
    public byte OldY { get; set; }
    public byte Dir { get; set; }

    public bool IsDying { get; set; }
    public DateTime? DiedAt { get; set; }

    public int PhysiSpeed { get; set; }
    public int MagicSpeed { get; set; }

    /// <summary>Port of lpObj->ActionNumber (User.h, set in CGActionRecv, Protocol.cpp:611-666) -- last action
    /// code received (120=attack, 128=sit, 129=greeting/pose, 130=heal). The original also uses it to replay
    /// the pose when building the viewport packet of a player who has just come into range of another
    /// (VpPlayer2[]/CharacterMakePreviewCharSet) -- that replay is NOT ported yet (this port's "player
    /// appeared" packet carries no pose), so for now this field is only stored for future use; the immediate
    /// visible effect (sit/greet animation) already works via the real-time ActionSend broadcast.</summary>
    public byte ActionNumber { get; set; }

    /// <summary> Port of CharSet[13] (see CObjectManager::CharacterMakePreviewCharSet, ObjectManager.cpp:1139-
    /// 1269 of the correct source tree, "Emulator 0.99 (2.1.7)/GameServer" -- see the doc-comment of <see
    /// cref="World.Item"/> for the explanation of why this repo has two C++ trees and which one is the real
    /// one). REVERTED from a fabricated size of 18 bytes (with extension bits at indices 12-17 and wing/pet
    /// branches of later seasons) that an earlier porting pass investigated against the WRONG source tree
    /// (`Source/Source/Emulator/GameServer` without a version suffix, a much later season) -- this build's real
    /// CharSet is 13 bytes, without those extension bits nor those wings/pets (they do not exist in 0.99B).
    /// </summary>
    public byte[] CharSet { get; } = new byte[13];

    /// <summary>ChangeUp (2nd/3rd class evolution) -- DataServer sends the class in raw DB format (<c>Class*16
    /// + ChangeUp</c>, e.g. 0/16/32/48/64 for 1st-class DW/DK/FE/MG/DL); it is decomposed into <see
    /// cref="Class"/> (compact index 0-4) and this field at the single real entry point (see
    /// ClientProtocolHandler.OnCharacterInfoFromDataServerAsync). No path of this port yet allows a real class
    /// change (2nd/3rd change), so in practice it is always 0 with the current seed data, but the field is
    /// already correctly derived if some real character had a different value stored.</summary>
    public byte ChangeUp { get; set; }

    /// <summary>Full port (Phase 3) of CObjectManager::CharacterMakePreviewCharSet
    /// (ObjectManager.cpp:1139-1268) -- builds the 13 appearance bytes from the real equipment worn in <see
    /// cref="Items"/> (slots 0-11, see the Slot* constants of <see cref="Item"/>). Without the sit/pose part
    /// (there are no actions yet) nor the "full-set" bit (it depends on CharacterCalcAttribute, which is
    /// attribute calculation -- Phase 4).</summary>
    public void RebuildCharSet()
    {
        var built = BuildCharSet(Class, ChangeUp, Items);

        for (int n = 0; n < 13; n++)
        {
            CharSet[n] = built[n];
        }
    }

    /// <summary> EXACT port of CObjectManager::CharacterMakePreviewCharSet (ObjectManager.cpp:1139-1269 of the
    /// correct source tree, "Emulator 0.99 (2.1.7)/GameServer" -- see the doc-comment of <see
    /// cref="World.Item"/> for the full explanation of why this repo has two C++ trees and why an earlier
    /// porting pass investigated this method against the WRONG tree (a much later season with CharSet[18], that
    /// season's wings/pets and extension bits that do not exist in 0.99B). Factored as a static method so it
    /// can be reused both from <see cref="RebuildCharSet"/> (player already in the world, with real <see
    /// cref="Items"/>) and from the character selection screen (compact format sent by DataServer -- see
    /// ClientProtocolHandler.OnCharacterListFromDataServerAsync, which rebuilds equivalent Item[9] with
    /// Item.FromCompactPreviewBytes before calling here; the original does the same in DSProtocol.cpp,
    /// DGCharacterListRecv, with the SAME logic byte for byte). Without the sit/pose part (CharSet[0] bits 0-1
    /// with ActionNumber==ACTION_SIT1/POSE1 -- there are no actions with viewport echo yet) nor the "full set"
    /// bit (CharSet[11] bit0, depends on CharacterCalcAttribute -- Phase 4, without the "same visual set" bonus
    /// documented there). </summary>
    public static byte[] BuildCharSet(byte cls, byte changeUp, IReadOnlyList<Item> wear)
    {
        var charSet = new byte[13];

        byte b0 = (byte)(changeUp * 16);
        b0 -= (byte)(b0 / 32);
        b0 += (byte)(cls * 32);
        charSet[0] = b0;

        // TempInventory: weapons (slots 0-1) store the full index (m_Index, 0-511), "no weapon" = 0xFF; the
        // rest of the equipment slots (2-11) store only the sub-index within their section
        // (m_Index%MAX_ITEM_TYPE), "empty slot" = MAX_ITEM_TYPE-1 = 0x1F -- exact port of
        // ObjectManager.cpp:1159-1185. Item.MaxItemType = 32 (this build's real MAX_ITEM_TYPE,
        // ItemManager.h:12).
        const int noWeapon = 0xFF;
        int noItem = Item.MaxItemType - 1; // 0x1F

        Span<int> temp = stackalloc int[9];

        for (int n = 0; n < 9; n++)
        {
            var item = wear[n];

            if (n == Item.SlotWeapon1 || n == Item.SlotWeapon2)
            {
                temp[n] = item.IsItem() ? item.Index : noWeapon;
            }
            else
            {
                temp[n] = item.IsItem() ? (item.Index % Item.MaxItemType) : noItem;
            }
        }

        charSet[1] = (byte)(temp[Item.SlotWeapon1] % 256);
        charSet[2] = (byte)(temp[Item.SlotWeapon2] % 256);

        charSet[3] |= (byte)((temp[Item.SlotHelm] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotHelm] & 0x10) << 3);

        charSet[3] |= (byte)(temp[Item.SlotArmor] & 0x0F);
        charSet[9] |= (byte)((temp[Item.SlotArmor] & 0x10) << 2);

        charSet[4] |= (byte)((temp[Item.SlotPants] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotPants] & 0x10) << 1);

        charSet[4] |= (byte)(temp[Item.SlotGloves] & 0x0F);
        charSet[9] |= (byte)(temp[Item.SlotGloves] & 0x10);

        charSet[5] |= (byte)((temp[Item.SlotBoots] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotBoots] & 0x10) >> 1);

        // Nivel empacado en 3 bits/slot (weapon1,weapon2,helm,armor,pants,gloves,boots) + flags de
        // brillo excelente/set -- puerto exacto de ObjectManager.cpp:1206-1219.
        int level = 0;
        int[] table = { 1, 0, 6, 5, 4, 3, 2 };

        for (int n = 0; n < 7; n++)
        {
            if (temp[n] == noItem || temp[n] == noWeapon)
            {
                continue;
            }

            var item = wear[n];
            level |= (((item.Level - 1) / 2) & 7) << (n * 3);
            charSet[10] |= (byte)((((item.NewOption & 0x3F) != 0) ? 2 : 0) << table[n]);
            charSet[11] |= (byte)((((item.SetOption & 0x03) != 0) ? 2 : 0) << table[n]);
        }

        // "Full set" bit (CharacterCalcAttribute) deferred to Phase 4 (attribute calculation) -- charSet[11]
        // bit0 is not turned on yet.

        charSet[6] = (byte)(level >> 16);
        charSet[7] = (byte)(level >> 8);
        charSet[8] = (byte)level;

        // Wings (slot 7) -- exact port of ObjectManager.cpp:1232-1249. Only these 3 cases exist in this build
        // (0.99B); without wings or any index outside this list no bit is turned on.
        int wing = temp[Item.SlotWing];

        if (wing is >= 0 and <= 2)
        {
            charSet[5] |= (byte)(wing << 2);
        }
        else if (wing is >= 3 and <= 6)
        {
            charSet[5] |= 12;
            charSet[9] |= (byte)(wing - 2);
        }
        else if (wing == 30)
        {
            charSet[5] |= 12;
            charSet[9] |= 5;
        }

        // Mascota/montura (slot 8, "helper") -- puerto exacto de ObjectManager.cpp:1251-1268. Solo
        // estos 5 casos existen en este build.
        int helper = temp[Item.SlotHelper];

        if (helper == noItem)
        {
            charSet[5] |= 3;
        }
        else if (helper is >= 0 and <= 2)
        {
            charSet[5] |= (byte)helper;
        }
        else if (helper == 3)
        {
            charSet[5] |= 3;
            charSet[10] |= 1;
        }
        else if (helper == 4)
        {
            charSet[5] |= 3;
            charSet[12] |= 1;
        }

        return charSet;
    }

    // World connection state
    /// <summary> Port of <c>char RegenOk</c> (User.h:509). The original is a 4-state counter (0=normal/visible,
    /// 1=just-teleported, 2/3=transition) but for this build a boolean is enough because the only place that
    /// sets it to 1 is <c>gObjMoveGate</c>/<c>gObjTeleport</c>/ <c>gObjSummonAlly</c>
    /// (User.cpp:2114,2144,2168,2220,2259) -- and NONE of those paths (map portals, teleport spell) is ported
    /// yet in this build (the only emitter of <see cref="Protocol.WorldPackets.TeleportSend"/> is
    /// World/DevilSquareManager.cs, which does not block either). The real initial value is 0 (=false, NOT
    /// blocked), set by <c>gObjCharZeroSet</c> (User.cpp:368) when accepting the connection, and it is NOT
    /// touched at any point of the login/character selection/world entry flow (confirmed by reading the whole
    /// function that builds the character from DataServer, ObjectManager.cpp:2733-2736: it only touches
    /// Live/Type/ State/Connected). A default of "true" (blocked until the client sends 0xF3:0x12) was a bug of
    /// this port: <see cref="MuServer.GameServer.WorldTestClient"/> sends that packet unconditionally and that
    /// is why the regression test never detected it, but a real MU client probably only sends it as an ack of
    /// having loaded the map AFTER a real teleport/gate -- if it never does one (as when entering the world for
    /// the first time), it never sends it, and with the old default (true) the player stayed blocked forever
    /// seeing the empty map (no players NOR monsters NOR NPCs, since <see cref="ViewportTicker"/> skips an
    /// observer's sweep entirely when RegenOk==true). Fixed to false to match the real behaviour. </summary>
    public bool RegenOk { get; set; } // false = visible/normal (default real), true = bloqueado tras teleport
    public bool WorldEntered { get; set; }

    /// <summary>Port of the <c>lpObj->SendQuestInfo</c> flag (User.h) -- CQuest::GCQuestInfoSend
    /// (Quest.cpp:397-417) only sends the C1:A0 packet (full 50-byte blob of quest state) the FIRST time per
    /// session; all later calls (including the one CQuest::NpcTalk triggers on every dialog with a quest NPC)
    /// are a no-op for this part and only send the C1:A1 state packet. See DSProtocol.cpp:503 -- it is sent
    /// proactively on entering the world, together with ItemListSend/SkillListSend.</summary>
    public bool SendQuestInfo { get; set; }

    /// <summary>Indices of other players currently visible to this player (simplified equivalent of VpPlayer[]
    /// -- see ViewportTicker).</summary>
    public HashSet<int> VisibleTo { get; } = new();

    /// <summary>Indices of monsters currently visible to this player (same mechanism as <see cref="VisibleTo"/>
    /// but for the monster registry -- see ViewportTicker).</summary>
    public HashSet<int> VisibleMonsters { get; } = new();

    /// <summary>Indices (within the player's current map, see <see cref="GroundItem.Index"/>) of ground items
    /// currently visible -- same mechanism as <see cref="VisibleMonsters"/>. Since this port has no runtime map
    /// change (no portals/teleport yet), there is no need to clear this set on changing map.</summary>
    public HashSet<int> VisibleGroundItems { get; } = new();

    // ---------------------------------------------------------------- Fase 4: combate (primera pasada)

    /// <summary>Port of OBJECTSTRUCT's combat fields (PhysiDamageMin/Max, Defense, AttackSuccessRate,
    /// DefenseSuccessRate) -- see <see cref="RecalcCombatStats"/> for the real port of
    /// CObjectManager::CharacterCalcAttribute (Phase 4, second pass, real item balance).</summary>
    public int PhysiDamageMin { get; set; }
    public int PhysiDamageMax { get; set; }
    public int Defense { get; set; }
    public int AttackSuccessRate { get; set; }
    public int DefenseSuccessRate { get; set; }

    /// <summary>Base magic damage (Energy/const, see <see cref="RecalcCombatStats"/>) -- used by the attack
    /// skills ("skills" phase), see ClientProtocolHandler.OnSkillAttackAsync.</summary>
    public int MagicDamageMin { get; set; }
    public int MagicDamageMax { get; set; }

    /// <summary>Class index 0-4 = DW/DK/FE/MG/DL (same order as the raw <see cref="Class"/>, see
    /// ClientProtocolHandler.ClassFe=2 and CharacterBalanceConfig).</summary>
    private const int ClassDw = 0, ClassDk = 1, ClassFe = 2, ClassMg = 3, ClassDl = 4;

    /// <summary> Port of CObjectManager::CharacterCalcAttribute (ObjectManager.cpp:1887-2523) -- Phase 4,
    /// second pass (real combat balance, replaces the first pass's Str/Level placeholder). It covers base
    /// physical damage per class, contribution of the equipped weapon(s) (with the +0..+15 level scaling of
    /// <see cref="ItemCombatMath"/>), arrow/bolt bonus, dual-wield penalty, attack success rate and
    /// defense/defense rate (dexterity + equipped armor/shield/ wing pieces). Explicitly documented
    /// simplifications compared to the original: 1) No critical/excellent/set-item (they depend on
    /// ItemOption.txt/SetItemOption.txt, not ported -- see the header comment of ItemCombatMath). 2) No
    /// +5%..+30% Defense bonus for "5 armor pieces at the same high level" nor the +10% DefenseSuccessRate for
    /// "same visual set" (ObjectManager.cpp:2314-2421) -- both depend on comparing SetItemOption/visual-index
    /// between pieces, outside the scope of this pass. 3) No PhysiSpeed/MagicSpeed (attack speed) nor HP/MP/BP
    /// from Vitality/Energy -- there is no server-side attack cooldown yet (the client already limits itself)
    /// and Life/MaxLife come from DataServer, so they are not needed for combat to work. 4) No magic damage
    /// (DW/MG/DL with spells) -- there is no skill system ported yet. 5) No PvP variants of hit/defense
    /// (Attack.cpp: MissCheckPvP/GetTargetDefense at 50% against players) -- this combat phase only covers
    /// player-versus-monster. </summary>
    public void RecalcCombatStats(ItemBalanceTable items, CharacterBalanceConfig cfg)
    {
        int cls = Class switch
        {
            0 => ClassDw, 1 => ClassDk, 2 => ClassFe, 3 => ClassMg, _ => ClassDl,
        };

        var right = Items[Item.SlotWeapon1];
        var left = Items[Item.SlotWeapon2];
        var rightInfo = right.IsItem() ? items.Get(right.Index) : null;
        var leftInfo = left.IsItem() ? items.Get(left.Index) : null;

        // Ammunition (arrow/bolt) does NOT count as a "weapon" for dual-wield/damage sum -- it only contributes
        // the percentage bonus of ObjectManager.cpp:2444-2459 (see below).
        bool rightIsWeapon = rightInfo is { IsWeapon: true } && !rightInfo.IsAmmo;
        bool leftIsWeapon = leftInfo is { IsWeapon: true } && !leftInfo.IsAmmo;

        // ---- Step 1: base physical damage per class (ObjectManager.cpp:1974-2053) ----
        int baseMin, baseMax;

        switch (cls)
        {
            case ClassDw:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDw]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDw]);
                break;

            case ClassDk:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDk]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDk]);
                break;

            case ClassFe:
                // Port of ObjectManager.cpp:1999-2012: alternative formula if the right-hand weapon is a
                // bow/crossbow (section 4 of Item.txt), whichever slot it is in (some bows go in Weapon2
                // according to Item.txt, see the comment of ItemBalance).
                bool bow = rightInfo is { Section: 4 };

                if (bow)
                {
                    baseMin = SafeDiv(Strength, cfg.FePhysiDamageMinBowConstA) + SafeDiv(Dexterity, cfg.FePhysiDamageMinBowConstB);
                    baseMax = SafeDiv(Strength, cfg.FePhysiDamageMaxBowConstA) + SafeDiv(Dexterity, cfg.FePhysiDamageMaxBowConstB);
                }
                else
                {
                    baseMin = SafeDiv(Strength + Dexterity, cfg.PhysiDamageMinConstA[ClassFe]);
                    baseMax = SafeDiv(Strength + Dexterity, cfg.PhysiDamageMaxConstA[ClassFe]);
                }
                break;

            case ClassMg:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassMg]) + SafeDiv(Energy, cfg.PhysiDamageMinConstB[ClassMg]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassMg]) + SafeDiv(Energy, cfg.PhysiDamageMaxConstB[ClassMg]);
                break;

            default: // ClassDl
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDl]) + SafeDiv(Energy, cfg.PhysiDamageMinConstB[ClassDl]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDl]) + SafeDiv(Energy, cfg.PhysiDamageMaxConstB[ClassDl]);
                break;
        }

        // ---- Step 2: contribution of each equipped hand (ObjectManager.cpp:2055-2085) ---- The staff (section
        // 5) only adds HALF of its damage to the physical branch (it is a "magic" weapon).
        int minRight = baseMin, maxRight = baseMax, minLeft = baseMin, maxLeft = baseMax;

        if (rightInfo is { IsWeapon: true })
        {
            int wMin = ItemCombatMath.GetDamageMin(right, rightInfo);
            int wMax = ItemCombatMath.GetDamageMax(right, rightInfo);
            bool staff = rightInfo.Section == 5;
            minRight += staff ? wMin / 2 : wMin;
            maxRight += staff ? wMax / 2 : wMax;
        }

        if (leftInfo is { IsWeapon: true })
        {
            int wMin = ItemCombatMath.GetDamageMin(left, leftInfo);
            int wMax = ItemCombatMath.GetDamageMax(left, leftInfo);
            bool staff = leftInfo.Section == 5;
            minLeft += staff ? wMin / 2 : wMin;
            maxLeft += staff ? wMax / 2 : wMax;
        }

        // ---- Step 3: arrow/bolt bonus (ObjectManager.cpp:2444-2459) ---- Bow/crossbow in the right hand +
        // ammunition with an upgrade level in the left -- the bonus uses the ammunition's RAW LEVEL
        // (Item.Level), not its scaled damage (which is 0).
        if (rightIsWeapon && rightInfo!.Section == 4 && leftInfo is { IsAmmo: true })
        {
            int rate = (left.Level * 2) + 1;
            minRight += (minRight * rate / 100) + 1;
            maxRight += (maxRight * rate / 100) + 1;
        }

        // ---- Step 4: dual-wield penalty (ObjectManager.cpp:2461-2473) ---- DK/MG/DL with two melee weapons
        // (sections 0-3) at once -- both hands at 55%.
        bool meleeDual = rightIsWeapon && leftIsWeapon && rightInfo!.Section <= 3 && leftInfo!.Section <= 3
            && cls is ClassDk or ClassMg or ClassDl;

        if (meleeDual)
        {
            minRight = minRight * 55 / 100;
            maxRight = maxRight * 55 / 100;
            minLeft = minLeft * 55 / 100;
            maxLeft = maxLeft * 55 / 100;
        }

        // ---- Paso 5: total (Attack.cpp:1191-1258, simplificado a un solo golpe combinado) ----
        int totalMin, totalMax;

        if (rightIsWeapon && leftIsWeapon)
        {
            totalMin = minRight + minLeft;
            totalMax = maxRight + maxLeft;
        }
        else if (rightIsWeapon)
        {
            totalMin = minRight;
            totalMax = maxRight;
        }
        else if (leftIsWeapon)
        {
            totalMin = minLeft;
            totalMax = maxLeft;
        }
        else
        {
            totalMin = baseMin;
            totalMax = baseMax;
        }

        PhysiDamageMin = Math.Max(totalMin, 0);
        PhysiDamageMax = Math.Max(totalMax, PhysiDamageMin + 1);

        // ---- Paso 6: acierto de ataque (ObjectManager.cpp:2096-2136, rama PvM) ----
        int asr = ((int)Level * cfg.AttackSuccessRateConstA[cls])
            + SafeDiv(Dexterity * (uint)cfg.AttackSuccessRateConstB[cls], cfg.AttackSuccessRateConstC[cls])
            + SafeDiv(Strength, cfg.AttackSuccessRateConstD[cls]);

        if (cls == ClassDl)
        {
            asr += SafeDiv(Leadership, cfg.DlAttackSuccessRateConstE);
        }

        AttackSuccessRate = Math.Max(asr, 0);

        // ---- Step 7: defense and defense rate (ObjectManager.cpp:2230-2422) ---- Dexterity/const + sum of
        // GetDefense()/GetDefenseSuccessRate() of Weapon2 (if it is a shield), Helm, Armor, Pants, Gloves,
        // Boots and Wing -- each piece returns 0 if it is broken or if that section does not have the
        // corresponding column (e.g. a weapon in Weapon2 has no DefenseSuccessRate, see World/ItemBalance.cs).
        int def = SafeDiv(Dexterity, cfg.DefenseConstA[cls]);
        int dsr = SafeDiv(Dexterity, cfg.DefenseSuccessRateConstA[cls]);

        Span<int> armorSlots = stackalloc[]
        {
            Item.SlotWeapon2, Item.SlotHelm, Item.SlotArmor, Item.SlotPants, Item.SlotGloves, Item.SlotBoots, Item.SlotWing,
        };

        foreach (var slot in armorSlots)
        {
            var piece = Items[slot];

            if (!piece.IsItem())
            {
                continue;
            }

            var info = items.Get(piece.Index);

            if (info == null)
            {
                continue;
            }

            def += ItemCombatMath.GetDefense(piece, info);
            dsr += ItemCombatMath.GetDefenseSuccessRate(piece, info);
        }

        Defense = Math.Max(def, 0);
        DefenseSuccessRate = Math.Max(dsr, 0);

        // ---- Step 8: base magic damage (ObjectManager.cpp:1980-1994 etc, "skills" phase) ---- Energy/const,
        // identical for the 5 classes with the real values (9,4) but loaded per class anyway (see
        // CharacterBalanceConfig). Magic weapon bonus (sword/staff with a MagicDamageRate column in Item.txt,
        // e.g. "Dark Reign Blade"/"Rune Blade"/any staff) -- port of Attack.cpp:1341-1345, without the
        // original's "fractional current durability" factor (1.0 is used if the weapon is not broken, the same
        // kind of simplification as the rest of this port).
        int magicMin = SafeDiv(Energy, cfg.MagicDamageMinConstA[cls]);
        int magicMax = SafeDiv(Energy, cfg.MagicDamageMaxConstA[cls]);

        if (rightInfo != null && right.Durability > 0 && (rightInfo.Section == 0 || rightInfo.Section == 5) && rightInfo.MagicDamageRate > 0)
        {
            int rise = (rightInfo.MagicDamageRate / 2) + (right.Level * 2);
            magicMin += (magicMin * rise) / 100;
            magicMax += (magicMax * rise) / 100;
        }

        MagicDamageMin = Math.Max(magicMin, 0);
        MagicDamageMax = Math.Max(magicMax, MagicDamageMin + 1);

        // ---- Attack Speed y Magic Speed (ObjectManager.cpp:2138-2228) ----
        int basePhysiSpeed = cls switch
        {
            ClassDw => SafeDiv(Dexterity, 20),
            ClassDk => SafeDiv(Dexterity, 15),
            ClassFe => SafeDiv(Dexterity, 50),
            ClassMg => SafeDiv(Dexterity, 15),
            _ => SafeDiv(Dexterity, 10),
        };

        int baseMagicSpeed = cls switch
        {
            ClassDw => SafeDiv(Dexterity, 10),
            ClassDk => SafeDiv(Dexterity, 20),
            ClassFe => SafeDiv(Dexterity, 50),
            ClassMg => SafeDiv(Dexterity, 20),
            _ => SafeDiv(Dexterity, 10),
        };

        int bonusSpeed = 0;

        int rightSpeed = (rightIsWeapon && rightInfo != null && right.Durability > 0) ? rightInfo.AttackSpeed : 0;
        int leftSpeed = (leftIsWeapon && leftInfo != null && left.Durability > 0) ? leftInfo.AttackSpeed : 0;

        if (rightIsWeapon && leftIsWeapon)
        {
            bonusSpeed += (rightSpeed + leftSpeed) / 2;
        }
        else if (rightIsWeapon)
        {
            bonusSpeed += rightSpeed;
        }
        else if (leftIsWeapon)
        {
            bonusSpeed += leftSpeed;
        }

        if (rightIsWeapon && (right.NewOption & 8) != 0) bonusSpeed += 7;
        if (leftIsWeapon && (left.NewOption & 8) != 0) bonusSpeed += 7;

        var gloves = Items[Item.SlotGloves];
        if (gloves.IsItem() && gloves.Durability > 0)
        {
            var gInfo = items.Get(gloves.Index);
            if (gInfo != null) bonusSpeed += gInfo.AttackSpeed;
        }

        var helper = Items[Item.SlotHelper];
        if (helper.IsItem() && helper.Durability > 0)
        {
            var hInfo = items.Get(helper.Index);
            if (hInfo != null) bonusSpeed += hInfo.AttackSpeed;
        }

        var amulet = Items[Item.SlotRing1];
        if (amulet.IsItem() && amulet.Durability > 0)
        {
            var aInfo = items.Get(amulet.Index);
            if (aInfo != null)
            {
                bonusSpeed += aInfo.AttackSpeed;
                if ((amulet.NewOption & 8) != 0) bonusSpeed += 7;
            }
        }

        PhysiSpeed = basePhysiSpeed + bonusSpeed;
        MagicSpeed = baseMagicSpeed + bonusSpeed;

        // ---- Step 9: recalculation of MaxLife and MaxMana (ObjectManager.cpp:2475-2508) ---- Base defaults
        // and multipliers from DefaultClassInfo.txt: DW (0): BaseHP=60, LevelHP=1.0, VitHP=2.0; BaseMP=60,
        // LevelMP=2.0, EneMP=2.0 DK (1): BaseHP=110, LevelHP=2.0, VitHP=3.0; BaseMP=20, LevelMP=0.5, EneMP=1.0
        // FE (2): BaseHP=80, LevelHP=1.0, VitHP=2.0; BaseMP=30, LevelMP=1.5, EneMP=1.5 MG (3): BaseHP=110,
        // LevelHP=1.0, VitHP=2.0; BaseMP=60, LevelMP=1.0, EneMP=2.0 DL (4): BaseHP=90, LevelHP=1.5, VitHP=2.0;
        // BaseMP=40, LevelMP=1.0, EneMP=1.5
        float[] baseHp = { 60f, 110f, 80f, 110f, 90f };
        float[] levelHp = { 1.0f, 2.0f, 1.0f, 1.0f, 1.5f };
        float[] vitHp = { 2.0f, 3.0f, 2.0f, 2.0f, 2.0f };
        uint[] baseVit = { 15, 25, 20, 26, 20 };

        float[] baseMp = { 60f, 20f, 30f, 60f, 40f };
        float[] levelMp = { 2.0f, 0.5f, 1.5f, 1.0f, 1.0f };
        float[] eneMp = { 2.0f, 1.0f, 1.5f, 2.0f, 1.5f };
        uint[] baseEne = { 30, 10, 15, 26, 15 };

        float maxLife = baseHp[cls] + (levelHp[cls] * Math.Max((int)Level - 1, 0)) + ((float)Math.Max((int)Vitality - (int)baseVit[cls], 0) * vitHp[cls]);
        float maxMana = baseMp[cls] + (levelMp[cls] * Math.Max((int)Level - 1, 0)) + ((float)Math.Max((int)Energy - (int)baseEne[cls], 0) * eneMp[cls]);

        MaxLife = (uint)Math.Max(maxLife, 1f);
        MaxMana = (uint)Math.Max(maxMana, 1f);

        // ---- Step 10: recalculation of MaxBP / AG (CharacterCalcBP, ObjectManager.cpp:1865-1884) ----
        double maxBp = cls switch
        {
            ClassDw => (Strength * 0.20) + (Dexterity * 0.40) + (Vitality * 0.30) + (Energy * 0.20),
            ClassDk => (Strength * 0.15) + (Dexterity * 0.20) + (Vitality * 0.30) + (Energy * 1.00),
            ClassFe => (Strength * 0.30) + (Dexterity * 0.20) + (Vitality * 0.30) + (Energy * 0.20),
            ClassMg => (Strength * 0.20) + (Dexterity * 0.25) + (Vitality * 0.30) + (Energy * 0.15),
            _ => (Strength * 0.30) + (Dexterity * 0.20) + (Vitality * 0.10) + (Energy * 0.15) + (Leadership * 0.30),
        };

        MaxBP = (uint)Math.Max(maxBp, 1.0);

        Life = Math.Min(Life, MaxLife);
        Mana = Math.Min(Mana, MaxMana);
        BP = Math.Min(BP, MaxBP);
    }

    private static int SafeDiv(uint value, int div) => div <= 0 ? 0 : (int)(value / (uint)div);

    // ---------------------------------------------------------------- "Skills" phase: magic and mana

    /// <summary>Port of lpObj->SkillDelay[MAX_SKILL] (CheckSkillDelay, SkillManager.cpp:440-454) -- last
    /// instant each skill was cast (by index), for the per-skill cooldown of the "Delay" column (milliseconds)
    /// of SkillList.txt.</summary>
    public Dictionary<int, DateTime> SkillDelay { get; } = new();

    // ---------------------------------------------------------------- Fase 5: party (primera pasada)

    /// <summary>Port of lpObj->PartyNumber -- party ID in PartyRegistry, -1 = no party (same convention as the
    /// original).</summary>
    public int PartyNumber { get; set; } = -1;

    /// <summary>VERY simplified port of Interface.type==INTERFACE_PARTY/TargetNumber (Party.cpp): only these
    /// two fields (one per side of the invitation) are needed to validate that the "accept/reject" reply
    /// corresponds to a really pending invitation -- the original also blocks other interfaces (shop, NPC
    /// dialog, etc.) while a party invitation is open, which does not apply yet because those interfaces are
    /// not ported. -1 = no pending invitation in that role.</summary>
    public int PartyInviteTargetIndex { get; set; } = -1; // I invited this index, waiting for their reply
    public int PartyInviterIndex { get; set; } = -1;      // this index invited me, waiting for my reply
}

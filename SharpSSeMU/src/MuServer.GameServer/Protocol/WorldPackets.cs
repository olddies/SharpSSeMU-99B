using System.Text;
using MuServer.GameServer.Config;
using MuServer.GameServer.World;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

// Port of the Phase 2 packets (Protocol.h/DSProtocol.h/Viewport.h) -- character selection, entering the world,
// viewport (OTHER PLAYERS appearing/disappearing, not monsters/NPCs yet) and movement. Layouts confirmed byte
// by byte against the original .h.

// ---------------------------------------------------------------- GameServer -> DataServer (0x04)

public static class DataServerCharacterPacketBuilder
{
    /// <summary>SDHP_CHARACTER_INFO_SEND (GS->DS), C1:04: index+account+name -- espejo exacto de lo
    /// que MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterInfoRecv.Parse espera leer.</summary>
    public static byte[] CharacterInfoRequest(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x04, w.ToArray());
    }

    /// <summary>SDHP_WAREHOUSE_ITEM_SEND (GS->DS), C1:05:00: index+account+WarehouseNumber.</summary>
    public static byte[] WarehouseInfoRequest(ushort index, string account)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        Span<byte> accountBytes = stackalloc byte[10];
        accountBytes.Clear();
        Encoding.ASCII.GetBytes(account, accountBytes);
        w.WriteBytes(accountBytes.ToArray(), 10);
        w.WriteUInt32(0);
        return PacketBuilder.BuildC1Sub(0x05, 0x00, w.ToArray());
    }

    /// <summary>SDHP_WAREHOUSE_ITEM_SAVE_SEND (GS->DS), C2:05:30: index+account+1920bytes+money+password+WarehouseNumber.</summary>
    public static byte[] WarehouseSaveRequest(ushort index, string account, PlayerObject player)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        Span<byte> accountBytes = stackalloc byte[10];
        accountBytes.Clear();
        Encoding.ASCII.GetBytes(account, accountBytes);
        w.WriteBytes(accountBytes.ToArray(), 10);

        byte[] itemsBlob = new byte[1920];
        Span<byte> itemInfo = stackalloc byte[Item.WireByteSize];
        for (int slot = 0; slot < player.WarehouseItems.Length && slot < 120; slot++)
        {
            var item = player.WarehouseItems[slot];
            if (item.IsItem())
            {
                item.ToWireBytes(itemInfo);
                itemInfo.CopyTo(itemsBlob.AsSpan(slot * 16, 16));
            }
            else
            {
                itemsBlob.AsSpan(slot * 16, 16).Fill(0xFF);
            }
        }
        w.WriteBytes(itemsBlob, 1920);
        w.WriteUInt32(player.WarehouseMoney);
        w.WriteUInt32(player.WarehousePassword);
        w.WriteUInt32(0);
        return PacketBuilder.BuildC2Sub(0x05, 0x30, w.ToArray());
    }

    /// <summary>C1:0x70 -- tells DataServer that this index now controls this character (in memory, see
    /// DataServerProtocolHandler.OnConnectCharacterAsync, already implemented).</summary>
    public static byte[] ConnectCharacter(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x70, w.ToArray());
    }

    /// <summary>C1:0x71 -- disconnect counterpart.</summary>
    public static byte[] DisconnectCharacter(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x71, w.ToArray());
    }

    /// <summary> SDHP_CHARACTER_INFO_SAVE_SEND (GS->DS), C2:0x30 -- port of GDCharacterInfoSaveSend
    /// (DSProtocol.cpp:991-1047). Exact mirror of what
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterInfoSaveRecv.Parse expects to read (which
    /// was already implemented and connected to NpgsqlCharacterDataRepository.SaveCharacterAsync from before --
    /// what was missing was this GameServer->DataServer side, this packet was never sent). Real bug confirmed
    /// this session and root cause of "progress is not saved"/"all characters always appear at the same
    /// position X=182 Y=128": without this packet, the <c>character</c> row in the database is never updated
    /// after creation (which does record the default position of <c>DefaultClassType</c>, 182/128 for
    /// DW/DK/MG/DL -- that value matches, byte for byte, the real INSERT of MuOnline.sql, it is not hardcoded
    /// by us), so EVERY login re-reads that same untouched row forever, no matter how far the character walked
    /// in earlier sessions. </summary>
    public static byte[] CharacterInfoSaveSend(World.PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16((ushort)p.Index);
        w.WriteFixedString(p.Account, 11);
        w.WriteFixedString(p.Name, 11);
        w.WriteUInt16(p.Level);
        w.WriteByte((byte)((p.Class * 16) + p.ChangeUp)); // DBClass, same pattern as CharacterInfoSend
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(p.Money);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteBytes(p.Inventory, 1728); // ya sincronizado por SetItem, ver doc-comment de PlayerObject.Inventory
        w.WriteBytes(p.Skill, 180);
        w.WriteByte(p.Map);
        w.WriteByte(p.X); // LIVE position (updated by OnMoveAsync) -- not the login one
        w.WriteByte(p.Y);
        w.WriteByte(p.Dir);
        w.WriteUInt32(p.PKCount);
        w.WriteUInt32(p.PKLevel);
        w.WriteUInt32(p.PKTime);
        w.WriteBytes(p.Quest, 50);
        w.WriteUInt32(p.ChatLimitTime);
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteBytes(p.Effect, 208);
        w.WriteUInt16(p.BCCount);
        w.WriteUInt16(p.CCCount);
        w.WriteUInt16(p.DSCount);
        return PacketBuilder.BuildC2(0x30, w.ToArray());
    }

    /// <summary>SDHP_CHARACTER_LIST_SEND (GS->DS), C1:01: index+account -- asks DataServer for the account's
    /// character list (exact mirror of what
    /// MuServer.DataServer\Protocol\DataServerPackets.cs::CharacterListRecv.Parse expects to read). Port of
    /// GDCharacterListSend (DSProtocol.cpp).</summary>
    public static byte[] CharacterListRequest(ushort index, string account)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        return PacketBuilder.BuildC1(0x01, w.ToArray());
    }

    /// <summary>SDHP_CHARACTER_CREATE_SEND (GS->DS), C1:02 -- exact mirror of what
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterCreateRecv.Parse expects to read. Port of
    /// GDCharacterCreateSend (DSProtocol.cpp). Unlike the original, the prior class/CARD_CODE validation of
    /// CGCharacterCreateRecv is NOT replicated here (that logic depends on an MG/DL/SU/RF unlock system by
    /// account level that is not ported) -- it is forwarded directly and DataServer is left as the authority:
    /// its CreateCharacterAsync already rejects any class with no row in default_class_type (today only
    /// DW/DK/FE/MG/SUM, see db/postgres/003_default_class_seed.sql) with result=2, so the visible effect is the
    /// same (a non-enabled class cannot be created) without duplicating the validation logic.</summary>
    public static byte[] CharacterCreateRequest(ushort index, string account, string name, byte characterClass)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(characterClass);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }
}

/// <summary>SDHP_CHARACTER_CREATE_RECV (DS->GS), C1:02 -- exact mirror of
/// MuServer.DataServer/Protocol/DataServerPackets.cs::DataServerPacketBuilder.CharacterCreateSend. Port of
/// DGCharacterCreateRecv (DSProtocol.cpp) -- only the parsing part; the conversion of Class to the format the
/// client expects lives in ClientProtocolHandler (the same place as the rest of this phase's
/// DataServer-to-client conversions).</summary>
public sealed record CharacterCreateResultFromDataServer(ushort Index, string Account, string Name, byte Result, byte Slot, byte Class, byte[] Equipment, ushort Level)
{
    public static CharacterCreateResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3); // C1 header: type,size,head = 3 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var slot = r.ReadByte();
        var cls = r.ReadByte();
        var equipment = r.ReadBytes(24);
        var level = r.ReadUInt16();
        return new CharacterCreateResultFromDataServer(index, account, name, result, slot, cls, equipment, level);
    }
}

/// <summary>A character inside SDHP_CHARACTER_LIST_RECV (DS->GS) -- the Inventory still travels in the compact
/// 60-byte format (5 bytes/equipment slot, see Item.FromCompactPreviewBytes);
/// OnCharacterListFromDataServerAsync is what converts it to CharSet[13] for the client.</summary>
public sealed record CharacterListEntryFromDataServer(byte Slot, string Name, ushort Level, byte Class, byte CtlCode, byte[] CompactInventory);

/// <summary>SDHP_CHARACTER_LIST_RECV (DS->GS), C2:01 -- exact mirror of
/// MuServer.DataServer\Protocol\DataServerPackets.cs::DataServerPacketBuilder.CharacterListSend. Port of
/// DGCharacterListRecv (DSProtocol.cpp), only the parsing part -- the conversion to CharSet lives in
/// ClientProtocolHandler (the same place that already ports CharacterMakePreviewCharSet).</summary>
public sealed record CharacterListFromDataServer(
    ushort Index, string Account, byte MoveCnt, byte ExtClass, IReadOnlyList<CharacterListEntryFromDataServer> Entries)
{
    public static CharacterListFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C2 header: type,size(2),head = 4 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var moveCnt = r.ReadByte();
        var extClass = r.ReadByte();
        var count = r.ReadByte();

        var entries = new List<CharacterListEntryFromDataServer>(count);

        for (int n = 0; n < count; n++)
        {
            var slot = r.ReadByte();
            var name = r.ReadFixedString(11);
            var level = r.ReadUInt16();
            var cls = r.ReadByte();
            var ctlCode = r.ReadByte();
            var compact = r.ReadBytes(60);
            entries.Add(new CharacterListEntryFromDataServer(slot, name, level, cls, ctlCode, compact));
        }

        return new CharacterListFromDataServer(index, account, moveCnt, extClass, entries);
    }
}

/// <summary>SDHP_CHARACTER_INFO_RECV (DS->GS), C2:04 -- espejo exacto de
/// MuServer.DataServer/Protocol/DataServerPackets.cs::DataServerPacketBuilder.CharacterInfoSend.</summary>
public sealed record CharacterInfoFromDataServer(
    ushort Index, string Account, string Name, byte Result, byte Class, ushort Level, uint LevelUpPoint,
    uint Experience, uint Money, uint Strength, uint Dexterity, uint Vitality, uint Energy, uint Leadership,
    uint Life, uint MaxLife, uint Mana, uint MaxMana, uint BP, uint MaxBP, byte[] Inventory, byte[] Skill,
    byte Map, byte X, byte Y, byte Dir, uint PkCount, uint PkLevel, uint PkTime, byte CtlCode, byte[] Quest,
    uint ChatLimitTime, ushort FruitAddPoint, ushort FruitSubPoint, byte[] Effect, uint Reset, uint MasterReset,
    uint IsNewChar, ushort Married, string MarryName, ushort BcCount, ushort CcCount, ushort DsCount)
{
    public static CharacterInfoFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C2 header: type,size(2),head = 4 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var cls = r.ReadByte();
        var level = r.ReadUInt16();
        var levelUpPoint = r.ReadUInt32();
        var experience = r.ReadUInt32();
        var money = r.ReadUInt32();
        var strength = r.ReadUInt32();
        var dexterity = r.ReadUInt32();
        var vitality = r.ReadUInt32();
        var energy = r.ReadUInt32();
        var leadership = r.ReadUInt32();
        var life = r.ReadUInt32();
        var maxLife = r.ReadUInt32();
        var mana = r.ReadUInt32();
        var maxMana = r.ReadUInt32();
        var bp = r.ReadUInt32();
        var maxBp = r.ReadUInt32();
        var inventory = r.ReadBytes(1728);
        var skill = r.ReadBytes(180);
        var map = r.ReadByte();
        var x = r.ReadByte();
        var y = r.ReadByte();
        var dir = r.ReadByte();
        var pkCount = r.ReadUInt32();
        var pkLevel = r.ReadUInt32();
        var pkTime = r.ReadUInt32();
        var ctlCode = r.ReadByte();
        var quest = r.ReadBytes(50);
        var chatLimitTime = r.ReadUInt32();
        var fruitAddPoint = r.ReadUInt16();
        var fruitSubPoint = r.ReadUInt16();
        var effect = r.ReadBytes(208);
        var reset = r.ReadUInt32();
        var masterReset = r.ReadUInt32();
        var isNewChar = r.ReadUInt32();
        var married = r.ReadUInt16();
        var marryName = r.ReadFixedString(11);
        var bcCount = r.ReadUInt16();
        var ccCount = r.ReadUInt16();
        var dsCount = r.ReadUInt16();

        return new CharacterInfoFromDataServer(
            index, account, name, result, cls, level, levelUpPoint, experience, money, strength, dexterity,
            vitality, energy, leadership, life, maxLife, mana, maxMana, bp, maxBp, inventory, skill, map, x, y,
            dir, pkCount, pkLevel, pkTime, ctlCode, quest, chatLimitTime, fruitAddPoint, fruitSubPoint, effect,
            reset, masterReset, isNewChar, married, marryName, bcCount, ccCount, dsCount);
    }
}

// ---------------------------------------------------------------- GameServer <-> Client

public sealed record CharacterInfoRecv(string Name)
{
    // PMSG_CHARACTER_INFO_RECV (Protocol.h:252-256): PSBMSG_HEAD(4) + char name[10] -- WATCH OUT, 10 bytes
    // here, not 11 as in most of the protocol's other name fields.
    public static CharacterInfoRecv Parse(byte[] p) => new(PacketBuilder.ReadFixedString(p.AsSpan(4, 10)));
}

public sealed record MoveRecv(byte X, byte Y, byte[] Path)
{
    // PMSG_MOVE_RECV declares a fixed path[8] in the C++ struct, but the real client does NOT always send all 8
    // bytes -- header.size reflects the real size sent (which depends on how many steps the movement has: 1
    // step does not need the 8 path bytes, path[0] is enough). In C++ reading past header.size is "safe"
    // because the struct is mapped over a larger buffer that exists in memory anyway (with garbage, but it does
    // not crash); here the array already comes with the exact size the framer set, so asking for a fixed 8
    // bytes when the packet is shorter throws ArgumentOutOfRangeException -- confirmed with the real client
    // (WorldTestClient never exposed it because it always builds a full 8-byte path). What is missing is filled
    // with 0, exactly the same practical effect that "reading uninitialised garbage" had in the original (those
    // bytes are not used if pathCount does not reach them, see OnMoveAsync).
    public static MoveRecv Parse(byte[] p)
    {
        var path = new byte[8];
        int available = Math.Clamp(p.Length - 5, 0, 8);

        if (available > 0)
        {
            p.AsSpan(5, available).CopyTo(path);
        }

        return new MoveRecv(p[3], p[4], path);
    }
}

public static class WorldPacketBuilder
{
    /// <summary>Real constants of <c>GameServerInfo - Common.dat</c> (see <see
    /// cref="MuServer.GameServer.Config.ServerInfoConfig"/>) that <see cref="NextExperience"/> needs -- set
    /// once in Program.cs at start-up. If it is never set (e.g. some old test that does not go through
    /// Program.cs), an instance with the factory defaults is used, so the port keeps working as before this
    /// file was loaded.</summary>
    public static MuServer.GameServer.Config.ServerInfoConfig ServerInfo { get; set; } = new();

    /// <summary>PMSG_CHARACTER_INFO_SEND, C3:F3:03 -- built as a "logical" C1Sub (the real C3 type is set by
    /// ClientSession.SendEncryptedAsync on encrypting). GAMESERVER_EXTRA==1 in this build, so the 13 extra
    /// "View*" DWORDs go in.</summary>
    public static byte[] CharacterInfoSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte(p.X);
        w.WriteByte(p.Y);
        w.WriteByte(p.Map);
        w.WriteByte(p.Dir);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(NextExperience(p.Level));
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Strength, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Dexterity, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Vitality, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Energy, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        // PMSG_CHARACTER_INFO_SEND has no #pragma pack(1) in the original (Protocol.h:589-632), so MSVC inserts
        // 2 padding bytes here to align the DWORD Money to 4 bytes (offset 38 after MaxBP is not a multiple of
        // 4). Without this padding, Money and EVERYTHING that follows (PKLevel, CtlCode, FruitAddPoint... and
        // the 13 "View*" DWORDs at the end) reaches the real client shifted 2 bytes -- confirmed as the cause
        // of the stats with "infinite numbers" reported when playing with the real client (WorldTestClient
        // never detected it because it does not validate this packet's exact bytes, only that it arrives).
        w.WriteUInt16(0); // alignment padding (does not exist in the struct, it is compiler padding)
        w.WriteUInt32(p.Money);
        w.WriteByte(p.PKLevel);
        w.WriteByte(p.CtlCode);
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0); // MaxFruitAddPoint -- balance config, not ported yet (Phase 3+)
        w.WriteUInt16((ushort)Math.Min(p.Leadership, ushort.MaxValue));
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0); // MaxFruitSubPoint (idem)
        // GAMESERVER_EXTRA==1 -- the offset here already falls on a multiple of 4 (52), no more padding is needed.
        w.WriteUInt32(p.Reset);
        w.WriteUInt32(0); // ViewPoint (puntos de reset acumulados para gastar -- Fase 3+)
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0x03, w.ToArray());
    }

    /// <summary>PMSG_NEW_CHARACTER_CALC_SEND (C1:F3:E1) -- Updates all the extended visual stats on the client (Attack Speed, Damage, Defense, etc.).</summary>
    public static byte[] NewCharacterCalcSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(0); // ViewAddStrength
        w.WriteUInt32(0); // ViewAddDexterity
        w.WriteUInt32(0); // ViewAddVitality
        w.WriteUInt32(0); // ViewAddEnergy
        w.WriteUInt32(0); // ViewAddLeadership
        w.WriteUInt32((uint)Math.Max(0, p.PhysiDamageMin));
        w.WriteUInt32((uint)Math.Max(0, p.PhysiDamageMax));
        w.WriteUInt32((uint)Math.Max(0, p.MagicDamageMin));
        w.WriteUInt32((uint)Math.Max(0, p.MagicDamageMax));
        w.WriteUInt32(0); // ViewCurseDamageMin
        w.WriteUInt32(0); // ViewCurseDamageMax
        w.WriteUInt32(100); // ViewMulPhysiDamage
        w.WriteUInt32(100); // ViewDivPhysiDamage
        w.WriteUInt32(100); // ViewMulMagicDamage
        w.WriteUInt32(100); // ViewDivMagicDamage
        w.WriteUInt32(100); // ViewMulCurseDamage
        w.WriteUInt32(100); // ViewDivCurseDamage
        w.WriteUInt32(0); // ViewMagicDamageRate
        w.WriteUInt32(0); // ViewCurseDamageRate
        w.WriteUInt32((uint)Math.Max(0, p.PhysiSpeed));
        w.WriteUInt32((uint)Math.Max(0, p.MagicSpeed));
        w.WriteUInt32((uint)Math.Max(0, p.AttackSuccessRate));
        w.WriteUInt32((uint)Math.Max(0, p.AttackSuccessRate)); // PvP
        w.WriteUInt32((uint)Math.Max(0, p.Defense));
        w.WriteUInt32((uint)Math.Max(0, p.DefenseSuccessRate));
        w.WriteUInt32((uint)Math.Max(0, p.DefenseSuccessRate)); // PvP
        w.WriteUInt32(0); // ViewDamageMultiplier
        w.WriteUInt32(0); // ViewRFDamageMultiplierA
        w.WriteUInt32(0); // ViewRFDamageMultiplierB
        w.WriteUInt32(0); // ViewRFDamageMultiplierC
        w.WriteUInt32(0); // ViewDarkSpiritAttackDamageMin
        w.WriteUInt32(0); // ViewDarkSpiritAttackDamageMax
        w.WriteUInt32(0); // ViewDarkSpiritAttackSpeed
        w.WriteUInt32(0); // ViewDarkSpiritAttackSuccessRate
        return PacketBuilder.BuildC1Sub(0xF3, 0xE1, w.ToArray());
    }

    /// <summary>Exact port of <c>gObjSetExperienceTable</c> (User.cpp:276-297): <c>gLevelExperience[n] =
    /// (n+9)*n*n*ExperienceMultiplierConstA</c> for <c>n&lt;=255</c>, plus an extra term for levels above 255
    /// using <see cref="Config.ServerInfoConfig.ExperienceMultiplierConstB"/> (<c>over=n-255</c>, incrementing
    /// every level: <c>+= (over+9)*over*over*ConstB</c>). Before porting <c>GameServerInfo - Common.dat</c>
    /// this was a <c>level²*1000</c> placeholder.</summary>
    internal static uint NextExperience(int level)
    {
        long constA = ServerInfo.ExperienceMultiplierConstA;
        long constB = ServerInfo.ExperienceMultiplierConstB;
        long n = Math.Max(level, 0);

        long baseN = Math.Min(n, 255);
        long exp = (baseN + 9) * baseN * baseN * constA;

        if (n > 255)
        {
            long over = n - 255;
            exp += (over + 9) * over * over * constB;
        }

        return (uint)Math.Clamp(exp, 0, uint.MaxValue);
    }

    /// <summary>PMSG_CHARACTER_REGEN_SEND, C3:F3:04 (Protocol.h:634-651) -- sent to the player when they die
    /// and respawn. It orders the main.exe client to lift the character off the ground, relocate the camera and
    /// the model to (X, Y, Map, Dir) and reset the life/mana/experience/money stats.</summary>
    public static byte[] CharacterRegenSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte(p.X);
        w.WriteByte(p.Y);
        w.WriteByte(p.Map);
        w.WriteByte(p.Dir);
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16(0); // 2 bytes of MSVC alignment padding before DWORD Experience
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(p.Money);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.BP);
        return PacketBuilder.BuildC1Sub(0xF3, 0x04, w.ToArray());
    }

    /// <summary>PMSG_NEW_CHARACTER_INFO_SEND, C1:F3:E0 -- derived stats for the player's own UI (not viewport).
    /// Sent once on entering, with the same base values (without item/skill bonuses, which are added in later
    /// phases).</summary>
    public static byte[] NewCharacterInfoSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16(p.Level);
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(NextExperience(p.Level));
        w.WriteUInt16((ushort)Math.Min(p.Strength, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Dexterity, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Vitality, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Energy, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Leadership, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0);
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0);
        // Same as in CharacterInfoSend: PMSG_NEW_CHARACTER_INFO_SEND (Protocol.h:744-780) has no pack(1) either
        // -- here the DWORD ViewReset needs 2 padding bytes to land 4-aligned (offset 42 after MaxFruitSubPoint
        // is not a multiple of 4).
        w.WriteUInt16(0); // alignment padding
        w.WriteUInt32(p.Reset);
        w.WriteUInt32(0);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0xE0, w.ToArray());
    }

    /// <summary>PMSG_MOVE_SEND, C1:D7.</summary>
    public static byte[] MoveSend(int index, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0xD7, w.ToArray());
    }

    /// <summary>PMSG_POSITION_SEND, C1:D0 -- used to force/correct the client's position (Move rejected by
    /// collision or out of range). FIXED: the real struct (Protocol.h:339-345) is <c>header + index[2] + x +
    /// y</c> -- this port sent ONLY x,y (2 payload bytes) without the leading <c>index[2]</c> (4 real payload
    /// bytes). The client, which reads this packet at a fixed offset expecting 4 payload bytes, ended up
    /// interpreting the 2 bytes we did send (our x,y) as if they were <c>index[0..1]</c>, and read garbage
    /// (bytes of the next packet in the same block, or nothing) as the real x,y -- the correction position
    /// reached it corrupted. This is sent EXACTLY when a movement is rejected (collision/out of range, see
    /// ClientProtocolHandler.OnMoveAsync), which is precisely the typical moment of "walking to get closer to
    /// attack" -- it matches the report of "attacking sends me back to my spawn spot" with the real
    /// client.</summary>
    public static byte[] PositionSend(int index, byte x, byte y)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        return PacketBuilder.BuildC1(0xD0, w.ToArray());
    }

    /// <summary>PMSG_TELEPORT_SEND, C3:1C -- EXACT port of CMove::GCTeleportSend (Move.cpp:279-292) and its
    /// real struct (Move.h:36-44 of the correct source tree, "Emulator 0.99 (2.1.7)/GameServer" -- see the
    /// doc-comment of <see cref="World.Item"/> for the explanation of why this repo has two C++ trees and which
    /// one is the real one). REVERTED: an earlier pass, investigating against the wrong tree, had changed
    /// <c>gate</c> from BYTE to WORD -- the real one is BYTE (clamped to 0/1 in GCTeleportSend: <c>pMsg.gate =
    /// ((gate&gt;0)?1:gate);</c>). Used by gObjMoveGate to tell the client that it changed map/position outside
    /// a normal movement (entering/leaving Devil Square, Phase 6). It is sent block-encrypted (see
    /// ClientSession.SendEncryptedAsync) -- here it is built as a "logical" C1 packet without sub-code, like
    /// the rest of this port's C3s (the real type 0xC3 is set by SendEncryptedAsync).</summary>
    public static byte[] TeleportSend(byte gate, byte map, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(gate);
        w.WriteByte(map);
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0x1C, w.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_SEND (players appearing), C2:12. One or more PMSG_VIEWPORT_PLAYER concatenated
    /// after the header. REVERTED: an earlier porting pass investigated this packet against the WRONG source
    /// tree (see the doc-comment of <see cref="World.Item"/>) and "fixed" the struct to a much later season's
    /// version (CharSet[18] + invented attribute/MuunItem/level/MaxHP/CurHP, count moved to the end) that does
    /// not exist in this build. The real struct (Viewport.h:45-54 of the correct tree, also confirmed against
    /// the real builder <c>CViewport::GCViewportPlayerSend</c>, Viewport.cpp:601-676) is:
    /// <c>index[2]+x+y+CharSet[13]+count(WORD, effect list)+name[10]+tx+ty+DirAndPkLevel</c> -- the
    /// <c>count</c> field goes BEFORE <c>name</c>, not at the end, and is the count of a list of visual
    /// effects/buffs that are appended after the fixed struct (not ported, count=0 always). The real ViewState
    /// takes the 4 low bits of CharSet[0] (mask <c>0x0F</c>/<c>&amp;15</c>, Viewport.cpp:657-658: <c>CharSet[0]
    /// &amp;= 0xF0; CharSet[0] |= ViewState &amp; 15;</c>) -- REVERTED from a 3-bit mask (0xF8) that an earlier
    /// pass had introduced "fixing" something that was already right.</summary>
    public static byte[] ViewportPlayerAppear(IReadOnlyList<PlayerObject> players)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)players.Count);

        foreach (var target in players)
        {
            byte indexHi = (byte)((target.Index >> 8) & 0xFF);
            // "Just appeared" (spawn flag) -- in this phase we do not distinguish a real appearance from a
            // reappearance by range, so it is always left at 0 (not essential for the client to render it
            // correctly).
            body.WriteByte(indexHi);
            body.WriteByte((byte)(target.Index & 0xFF));
            body.WriteByte(target.X);
            body.WriteByte(target.Y);

            // ViewState (hidden GM/other visibility states, lpObj->ViewState) is not tracked in this port yet
            // -- it is always sent as 0 (normal visible), like a player with no special state in the original.
            target.CharSet[0] &= 0xF0;
            body.Write(target.CharSet, 0, 13); // CharSet[13] real (ver PlayerObject.CharSet)

            // PMSG_VIEWPORT_PLAYER has no #pragma pack(1) (Viewport.h:45-56), so MSVC aligns the WORD count to
            // an even offset: after CharSet[13] we are at offset 17, odd, and the compiler inserts 1 padding
            // byte. The original sends the WHOLE struct (memcpy(&send[size],&info,sizeof(info)),
            // Viewport.cpp:672, with InfoSize initialised to sizeof(info)), so that byte travels on the wire.
            // Without it, count/name/tx/ty/ DirAndPkLevel reach the real client shifted 1 byte.
            body.WriteByte(0); // alignment padding (offset 17)

            body.WriteByte(0); // count (WORD, lista de efectos) low -- sin efectos visuales portados
            body.WriteByte(0); // high

            body.Write(PacketBuilder.FixedString(target.Name, 10));

            body.WriteByte(target.TX);
            body.WriteByte(target.TY);
            body.WriteByte((byte)((target.Dir * 16) | (target.PKLevel & 0x0F)));

            // Tail padding: the struct aligns to 2 (because of the WORD count) and its last field ends at
            // offset 32, so sizeof() = 34, not 33. Without this byte, each following player in the viewport
            // starts 2 bytes earlier than where the client expects it -- a cumulative error that misaligns the
            // whole rest of the list.
            body.WriteByte(0);
        }

        return BuildC2NoSub(0x12, body.ToArray());
    }

    /// <summary>Port of the packing pattern "SET_NUMBERHB(SET_NUMBERHW(x))" used by this build for BYTE
    /// MaxHP[4]/CurHP[4] (Viewport.cpp:1698-1706) -- it is NOT standard big-endian: the resulting byte order is
    /// [bits24-31, bits8-15, bits16-23, bits0-7] (most significant byte, then the low byte of the low half,
    /// then the low byte of the high half, then the least significant byte). The same pattern is used in
    /// several other MU versions for HP/Money -- confirmed by reading the macro applied byte by byte, not an
    /// assumption.</summary>
    private static void WritePackedUInt32Swapped(Stream s, uint v)
    {
        ushort hw = (ushort)((v >> 16) & 0xFFFF);
        ushort lw = (ushort)(v & 0xFFFF);
        s.WriteByte((byte)(hw >> 8));
        s.WriteByte((byte)(lw >> 8));
        s.WriteByte((byte)(hw & 0xFF));
        s.WriteByte((byte)(lw & 0xFF));
    }

    /// <summary>PMSG_VIEWPORT_SEND carries no sub-code (it is PWMSG_HEAD, not PSWMSG_HEAD) -- it is built by
    /// hand instead of using BuildC2Sub (which adds one extra sub-code byte).</summary>
    private static byte[] BuildC2NoSub(byte head, byte[] payload)
    {
        var buff = new byte[4 + payload.Length];
        int size = buff.Length;
        buff[0] = 0xC2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        payload.CopyTo(buff, 4);
        return buff;
    }

    /// <summary>PMSG_VIEWPORT_DESTROY_SEND (desaparecer jugadores O monstruos -- mismo paquete para
    /// ambos, ver Viewport.cpp:496-546), C1:14.</summary>
    public static byte[] ViewportDestroy(IReadOnlyList<int> indexes)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)indexes.Count);

        foreach (var idx in indexes)
        {
            body.WriteByte((byte)((idx >> 8) & 0xFF));
            body.WriteByte((byte)(idx & 0xFF));
        }

        return PacketBuilder.BuildC1(0x14, body.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_SEND monster variant (GCViewportMonsterSend/GCViewportSimpleMonsterSend,
    /// Viewport.cpp:690-773,1263-1321), C2:13. <paramref name="justSpawned"/> turns on the "just appeared" bit
    /// (spawn animation) -- it is used when reviving a monster after its respawn, not when a player simply
    /// enters the view range of one that was already standing there.</summary>
    public static byte[] ViewportMonsterAppear(IReadOnlyList<Monster> monsters, bool justSpawned = false)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)monsters.Count);

        foreach (var m in monsters)
        {
            byte indexHi = (byte)((m.Index >> 8) & 0xFF);

            if (justSpawned)
            {
                indexHi |= 0x80;
            }

            body.WriteByte(indexHi);
            body.WriteByte((byte)(m.Index & 0xFF));
            body.WriteByte((byte)((m.MonsterClass >> 8) & 0xFF));
            body.WriteByte((byte)(m.MonsterClass & 0xFF));
            body.WriteByte(0); // count (WORD, effect list) low -- no active effects yet
            body.WriteByte(0); // high
            body.WriteByte(m.X);
            body.WriteByte(m.Y);
            body.WriteByte(m.TX);
            body.WriteByte(m.TY);
            body.WriteByte((byte)(m.Dir * 16)); // PKLevel no aplica a monstruos, nibble bajo en 0
            // Tail padding: the struct aligns to 2 (because of the WORD count) and its last field ends at
            // offset 10, so sizeof(PMSG_VIEWPORT_MONSTER) = 12 on the wire (MSVC pack(8)), not 11.
            body.WriteByte(0);
        }

        return BuildC2NoSub(0x13, body.ToArray());
    }

    /// <summary>PMSG_MOVE_SEND, C1:D7 -- notifica el movimiento de un objeto (monstruo/jugador) en el mapa.</summary>
    public static byte[] ViewportMonsterMove(int monsterIndex, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((monsterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(monsterIndex & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte((byte)(dir << 4));
        return PacketBuilder.BuildC1(0xD7, w.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_ITEM inside the PMSG_VIEWPORT_SEND batch (GCViewportItemSend,
    /// Viewport.cpp:~905-909), C2:20. Bit 0x80 of the high byte of the index = "just appeared" (<see
    /// cref="GroundItem.JustDropped"/>, animates the fall/appearance on the client). Money (<see
    /// cref="GroundItem.MoneyAmount"/> not null) uses a different ItemInfo packing from a normal item --
    /// byte-for-byte port verified against Viewport.cpp: byte0=index&0xFF,
    /// byte1=SET_NUMBERLB(SET_NUMBERHW(money))=bits16-23, byte2=SET_NUMBERHB(SET_NUMBERLW(money))= bits8-15,
    /// byte3=(index&256)>>1, byte4=SET_NUMBERLB(SET_NUMBERLW(money))=bits0-7. Bits 24-31 of the amount are
    /// NEVER sent for a money item on the ground in this build -- it is a real limitation of the original C++,
    /// replicated as is (not "fixed").</summary>
    public static byte[] ViewportItemAppear(IReadOnlyList<GroundItem> items)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)items.Count);

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        foreach (var g in items)
        {
            byte indexHi = (byte)((g.Index >> 8) & 0xFF);

            if (g.JustDropped)
            {
                indexHi |= 0x80;
            }

            body.WriteByte(indexHi);
            body.WriteByte((byte)(g.Index & 0xFF));
            body.WriteByte(g.X);
            body.WriteByte(g.Y);

            if (g.MoneyAmount is uint money)
            {
                info.Clear();
                info[0] = (byte)(g.Item.Index & 0xFF);
                info[1] = (byte)((money >> 16) & 0xFF); // SET_NUMBERLB(SET_NUMBERHW(money))
                info[2] = (byte)((money >> 8) & 0xFF); // SET_NUMBERHB(SET_NUMBERLW(money))
                info[3] = (byte)((g.Item.Index & 256) >> 1);
                info[4] = (byte)(money & 0xFF); // SET_NUMBERLB(SET_NUMBERLW(money))
            }
            else
            {
                g.Item.ToWireBytes(info);
            }

            body.Write(info);
        }

        return BuildC2NoSub(0x20, body.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_DESTROY_ITEM_SEND (GCViewportDestroyItemSend, Viewport.cpp:579-630), C2:21 -- a
    /// DIFFERENT packet from the one used for players/monsters (C1:14, <see cref="ViewportDestroy"/>); ground
    /// items have their own head. 2 bytes per item (only the index, no flag bit).</summary>
    public static byte[] ViewportItemDestroy(IReadOnlyList<int> indexes)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)indexes.Count);

        foreach (var idx in indexes)
        {
            body.WriteByte((byte)((idx >> 8) & 0xFF));
            body.WriteByte((byte)(idx & 0xFF));
        }

        return BuildC2NoSub(0x21, body.ToArray());
    }
}

// ---------------------------------------------------------------- Fase 4: combate (primera pasada)

/// <summary>PMSG_ATTACK_RECV (Attack.h:13-19), C1:D9 -- pedido de ataque cuerpo a cuerpo del
/// cliente (sin skill, ver CAttack::CGAttackRecv).</summary>
public sealed record AttackRecv(int TargetIndex, byte Action, byte Dir)
{
    public static AttackRecv Parse(byte[] p)
    {
        int index = (p[3] << 8) | p[4];
        return new AttackRecv(index, p[5], p[6]);
    }
}

/// <summary>PMSG_ACTION_RECV (Protocol.h:155-161), C1:18 -- pose/emote/sit (dir+action) plus an optional
/// "target" index that the original does not even validate (CGActionRecv, Protocol.cpp:611- 666, forwards it as
/// is when building PMSG_ACTION_SEND).</summary>
public sealed record ActionRecv(byte Dir, byte Action, int TargetIndex)
{
    /// <summary>Production regression (same pattern as MoveRecv's truncated path): the real client sometimes
    /// sends this packet WITHOUT the final "index[2]" field (which the original does not even use/validate, see
    /// the doc-comment above) -- the C++ struct declares a "ceiling" size of 7 bytes, but what is actually sent
    /// can be shorter. It is read defensively byte by byte instead of assuming the full length, like
    /// MoveRecv.Parse.</summary>
    public static ActionRecv Parse(byte[] p)
    {
        byte dir = p.Length > 3 ? p[3] : (byte)0;
        byte action = p.Length > 4 ? p[4] : (byte)0;
        int index = p.Length > 6 ? ((p[5] << 8) | p[6]) : 0xFFFF;
        return new ActionRecv(dir, action, index);
    }
}

/// <summary>PMSG_LEVEL_UP_POINT_RECV (Protocol.h:258-262), C1:F3:06 -- pedir agregar 1 punto de
/// level-up a un stat. type: 0=Strength,1=Dexterity,2=Vitality,3=Energy,4=Leadership.</summary>
public sealed record LevelUpPointRecv(byte Type)
{
    public static LevelUpPointRecv Parse(byte[] p) => new(p[4]);
}

public static class CombatPacketBuilder
{
    /// <summary>PMSG_ACTION_SEND (Protocol.h:355-362), C1:18 -- attack animation, sent to everyone who sees the
    /// attacker (here: only to the attacker itself, see the note in ClientProtocolHandler.OnAttackAsync about
    /// this first pass's simplified broadcast).</summary>
    public static byte[] ActionSend(int attackerIndex, byte dir, byte action, int targetIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((attackerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(attackerIndex & 0xFF));
        w.WriteByte(dir);
        w.WriteByte(action);
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x18, w.ToArray());
    }

    /// <summary>PMSG_DAMAGE_SEND (Protocol.h:327-337), C1:D9 -- damage number shown over the target. <paramref
    /// name="targetCurrentLife"/>/<paramref name="damage"/> feed the GAMESERVER_EXTRA fields
    /// (ViewCurHP/ViewDamageHP), present in this build.</summary>
    public static byte[] DamageSend(int targetIndex, int damage, byte type, bool missFlag, float targetCurrentLife)
    {
        var w = new PacketWriter();
        int clampedDamage = Math.Min(damage, 65000);
        byte hi = (byte)((targetIndex >> 8) & 0xFF);

        if (missFlag)
        {
            hi |= 0x80;
        }

        w.WriteByte(hi);
        w.WriteByte((byte)(targetIndex & 0xFF));
        w.WriteByte((byte)((clampedDamage >> 8) & 0xFF));
        w.WriteByte((byte)(clampedDamage & 0xFF));
        w.WriteByte(type);
        // FIXED (bug introduced in an earlier pass of this same session): the comment said "4-byte header ->
        // offset 9 -> 3 padding bytes", but PMSG_DAMAGE_SEND uses PBMSG_HEAD (Protocol.h:22-41: type+size+head,
        // BYTE all 3, with no fields forcing alignment > 1), which is 3 bytes, NOT 4 (the 4-byte one is
        // PSBMSG_HEAD, with an extra subh, used by the C1:F3:xx packets like CharacterInfoSend/LevelUpSend --
        // PMSG_DAMAGE_SEND is C1:D9 directly, without sub-code). The real offset after index[2]+damage[2]+type
        // = 3+2+2+1 = 8, which is ALREADY a multiple of 4 -- ZERO padding bytes are needed here. The 3 bytes
        // this port added in excess shifted ViewCurHP/ViewDamageHP by 3 bytes, and since the total packet also
        // came out 3 bytes longer than the real client expects, EVERYTHING the client read from there on (the
        // displayed damage number, and potentially the framing of subsequent packets in the same encrypted
        // block) came out as garbage -- it matches exactly the "illogical damage numbers like 9998989898"
        // reported when playing with the real client. PMSG_MANA_SEND (see ManaPacketBuilder) had the same error
        // for the same reason, also fixed.
        w.WriteUInt32((uint)targetCurrentLife);
        w.WriteUInt32((uint)damage);
        return PacketBuilder.BuildC1(0xD9, w.ToArray());
    }

    /// <summary>PMSG_USER_DIE_SEND (Protocol.h:347-353), C1:17 -- muerte (de jugador O monstruo,
    /// mismo paquete, ver GCUserDieSend).</summary>
    public static byte[] UserDieSend(int deadIndex, byte skill, int killerIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((deadIndex >> 8) & 0xFF));
        w.WriteByte((byte)(deadIndex & 0xFF));
        w.WriteByte(skill);
        w.WriteByte((byte)((killerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(killerIndex & 0xFF));
        return PacketBuilder.BuildC1(0x17, w.ToArray());
    }

    /// <summary>PMSG_SKILL_ATTACK_SEND (SkillManager.h:144-150), C3:19 -- ataque con magia/skill
    /// de monstruos a distancia (Lich, Gorgon, etc.) o jugadores.</summary>
    public static byte[] SkillAttackSend(int attackerIndex, byte skill, int targetIndex, bool hit)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((attackerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(attackerIndex & 0xFF));

        byte targetHi = (byte)((targetIndex >> 8) & 0xFF);
        if (hit)
        {
            targetHi |= 0x80;
        }

        w.WriteByte(targetHi);
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x19, w.ToArray());
    }

    /// <summary>PMSG_REWARD_EXPERIENCE_SEND (Protocol.h:449-460), C1:9C -- popup of experience gained, unicast
    /// to the player who took it (see GCMonsterDieSend). FIXED: the real struct (PBMSG_HEAD=3 bytes + index[2]
    /// + WORD experience[2] (4 bytes, NOT 2 -- it is an array of 2 WORDs) + damage[2]) was missing the
    /// alignment padding before the 3 "View*" DWORDs of GAMESERVER_EXTRA: offset after those fields = 3+2+4+2 =
    /// 11, not a multiple of 4 -- 1 padding byte is needed to reach 12. Without it,
    /// ViewDamageHP/ViewExperience/ ViewNextExperience (and the experience popup the client builds from those
    /// values) arrived shifted 1 byte -- it explains the reported "they give no experience" (the client
    /// probably discards or ignores the popup if the DWORDs do not match what is expected).</summary>
    public static byte[] MonsterDieSend(int monsterIndex, uint experience, int damage, uint currentExperience, uint nextExperience)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((monsterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(monsterIndex & 0xFF));
        w.WriteUInt16((ushort)((experience >> 16) & 0xFFFF)); // SET_NUMBERHW
        w.WriteUInt16((ushort)(experience & 0xFFFF));          // SET_NUMBERLW
        int clampedDamage = Math.Min(damage, 65000);
        w.WriteByte((byte)((clampedDamage >> 8) & 0xFF));
        w.WriteByte((byte)(clampedDamage & 0xFF));
        w.WriteByte(0); // alignment padding (offset 11 -> 12)
        w.WriteUInt32((uint)damage);
        w.WriteUInt32(currentExperience);
        w.WriteUInt32(nextExperience);
        return PacketBuilder.BuildC1(0x9C, w.ToArray());
    }

    /// <summary>PMSG_LEVEL_UP_SEND (Protocol.h:653-673), C1:F3:05.</summary>
    public static byte[] LevelUpSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16(p.Level);
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0); // MaxFruitAddPoint -- balance de fruits no portado
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0); // MaxFruitSubPoint
        // Same alignment problem as CharacterInfoSend/NewCharacterInfoSend (PMSG_LEVEL_UP_SEND,
        // Protocol.h:653-673, without pack(1)): 9 WORDs = 18 bytes after the 4-byte header, offset 22 is not a
        // multiple of 4 -- 2 padding bytes before the first "View*" DWORD.
        w.WriteUInt16(0); // alignment padding
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(WorldPacketBuilder.NextExperience(p.Level));
        return PacketBuilder.BuildC1Sub(0xF3, 0x05, w.ToArray());
    }

    /// <summary>PMSG_LEVEL_UP_POINT_SEND (Protocol.h:675-692), C1:F3:06 -- reply to LevelUpPointRecv. <paramref
    /// name="ok"/>=false sends result=0 (failure, without the rest of the fields populated -- like the
    /// original, which only fills result/MaxLifeAndMana/MaxBP/View* inside the success if). Success result =
    /// 16+type (16=Str,17=Dex,18=Vit,19=Ene, 20=Leadership, see CGLevelUpPointRecv).</summary>
    public static byte[] LevelUpPointSend(PlayerObject p, byte type, bool ok)
    {
        var w = new PacketWriter();
        w.WriteByte(ok ? (byte)(16 + type) : (byte)0);
        // result(1) after the header(4) -> offset 5, not a multiple of 2 -- 1 padding byte before the first
        // WORD (MaxLifeAndMana).
        w.WriteByte(0); // alignment padding
        uint maxLifeAndMana = type switch
        {
            2 => p.MaxLife, // Vitality sube MaxLife
            3 => p.MaxMana, // Energy sube MaxMana
            _ => 0,
        };
        w.WriteUInt16((ushort)Math.Min(maxLifeAndMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        // MaxLifeAndMana(2)+MaxBP(2) = 4 bytes after offset 6 -> offset 10, not a multiple of 4 -- 2 padding
        // bytes before the first "View*" DWORD.
        w.WriteUInt16(0); // alignment padding
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0x06, w.ToArray());
    }
}

// ---------------------------------------------------------------- Phase 3: items and inventory Port of
// ItemManager.h/.cpp -- only the subset of "move/equip inside one's own inventory" packets
// (Trade/Warehouse/Shop/PersonalShop and pick up/drop on the ground are left for a later pass of Phase 3,
// documented in the README).

/// <summary>PMSG_ITEM_MOVE_RECV, C1:24 (ItemManager.h:63-71) -- header(3) + SourceFlag(1) + SourceSlot(1) +
/// ItemInfo[5] (offsets 5-9, client echo, IGNORED -- the server is the authority on which item is really in
/// SourceSlot, ItemManager.cpp:2853-3025) + TargetFlag(1, offset 10) + TargetSlot(1, offset 11). Full packet of
/// 12 bytes. SourceFlag/TargetFlag: 0=Inventory (the only container supported for now).</summary>
public sealed record ItemMoveRecv(byte SourceFlag, byte SourceSlot, byte TargetFlag, byte TargetSlot)
{
    public static ItemMoveRecv Parse(byte[] p) => new(p[3], p[4], p[10], p[11]);
}

// ---------------------------------------------------------------- Phase 3 (second pass): pick up/drop on the
// ground Port of CGItemGetRecv/CGItemDropRecv (ItemManager.cpp:3289-3718) -- items dropped on the floor by a
// monster's death or by the player themselves (PMSG_ITEM_DROP_RECV), and picked back up (PMSG_ITEM_GET_RECV).
// See World/GroundItem.cs for the data model and the rest of the scope/simplifications doc-comment (no
// ItemBag/events/Muun/quest-items, no hop to DataServer to assign Serial -- it is generated locally).

/// <summary>PMSG_ITEM_GET_RECV, C1:22 -- <paramref name="GroundIndex"/> is the slot within the 300-item array
/// of the player's MAP (not a global index), just like <see cref="GroundItem.Index"/>.</summary>
public sealed record ItemGetRecv(int GroundIndex)
{
    public static ItemGetRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_ITEM_DROP_RECV, C1:23.</summary>
public sealed record ItemDropRecv(byte X, byte Y, byte Slot)
{
    public static ItemDropRecv Parse(byte[] p) => new(p[3], p[4], p[5]);
}

public static class ItemPacketBuilder
{
    /// <summary>PMSG_ITEM_LIST_SEND, C4:F3:10 (2-byte size, block-encrypted -- see
    /// ClientSession.SendEncryptedC4Async). Input built as a "logical" C2Sub packet with a 2-byte header; the
    /// real C4 type is set by SendEncryptedC4Async. count+repeated{slot,ItemInfo[5]} -- 6 bytes per occupied
    /// slot (ItemManager.h:187-198, PMSG_ITEM_LIST).</summary>
    public static byte[] ItemListSend(PlayerObject p)
    {
        using var body = new MemoryStream();
        var countPos = body.Position;
        body.WriteByte(0); // corrected further below
        int count = 0;

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            var item = p.Items[slot];

            if (!item.IsItem())
            {
                continue;
            }

            item.ToWireBytes(info);
            body.WriteByte((byte)slot);
            body.Write(info);
            count++;
        }

        var bytes = body.ToArray();
        bytes[countPos] = (byte)count;

        return PacketBuilder.BuildC2Sub(0xF3, 0x10, bytes);
    }

    /// <summary>PMSG_ITEM_MOVE_SEND, C3:24 (ItemManager.h:121-127) -- FIXED: <paramref name="result"/> is NOT a
    /// generic boolean -- exact port of <c>MoveItemToInventoryFromInventory</c> (ItemManager.cpp:1920-1978),
    /// which returns <c>TargetFlag</c> (0 for Inventory, the only container supported here) on success and
    /// <c>0xFF</c> on any failure (see the doc-comment of ClientProtocolHandler.OnItemMoveAsync for the full
    /// detail, including the "swap" bug this fixes). slot = real destination where the item ended up, +
    /// ItemInfo[5]. It is sent block-encrypted via SendEncryptedAsync (hence the C3), built here as a "logical"
    /// C1 packet without sub-code (head=0x24 directly, like MoveSend/0xD7).</summary>
    public static byte[] ItemMoveSend(byte result, byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x24, w.ToArray());
    }

    /// <summary>PMSG_ITEM_CHANGE_SEND, C1:25 (ItemManager.h:129-134) -- notifies others/the client itself that
    /// the item in a slot changed (used here after a successful move/equip). Byte1 of ItemInfo is overwritten
    /// with slot*16 | ((level-1)/2)&0xF, exact port of ItemManager.cpp:3445-3462. Header + index[2] +
    /// ItemInfo[5], WITHOUT the final "attribute" byte -- that field does not exist in this build (it was from
    /// a GAMESERVER_UPDATE branch of a later season that does not apply here).</summary>
    public static byte[] ItemChangeSend(int index, byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));

        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        info[1] = (byte)((slot * 16) | (((item.Level - 1) / 2) & 0x0F));

        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x25, w.ToArray());
    }

    /// <summary>PMSG_ITEM_EQUIPMENT_SEND, C1:F3:13 -- updated CharSet[13] of the player themselves (see
    /// PlayerObject.CharSet) (sent to the client itself after equipping/unequipping; the rest of the players
    /// see the change via a new ViewportPlayerAppear, see ClientProtocolHandler.OnItemMoveAsync).</summary>
    public static byte[] ItemEquipmentSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((p.Index >> 8) & 0xFF));
        w.WriteByte((byte)(p.Index & 0xFF));
        w.WriteBytes(p.CharSet, 13);
        return PacketBuilder.BuildC1Sub(0xF3, 0x13, w.ToArray());
    }

    /// <summary>PMSG_ITEM_REPAIR_SEND, C1:34 (ItemManager.h:175-180) -- puerto de CGItemRepairRecv.</summary>
    public static byte[] ItemRepairSend(uint money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD (3 bytes) + DWORD money aligned to 4 -> MSVC inserts 1 padding byte at offset 3 (sizeof =
        // 8, not 7). The original sends sizeof(pMsg), so without this the client reads money from offset 4 and
        // receives a value shifted by one byte.
        w.WriteByte(0); // alignment padding (offset 3 -> 4)
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x34, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DUR_SEND, C1:2A (ItemManager.h:182-187) -- puerto de GCItemDurSend.</summary>
    public static byte[] ItemDurSend(byte slot, byte dur, byte flag)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        w.WriteByte(dur);
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x2A, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DELETE_SEND, C1:28 (ItemManager.h:3464-3475) -- notifies the deletion of a consumed or used item.</summary>
    public static byte[] ItemDeleteSend(byte slot, byte flag = 1)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x28, w.ToArray());
    }

    /// <summary>PMSG_ITEM_MODIFY_SEND, C1:F3:14 (ItemManager.h:3564-3582) -- notifies that an item in a slot was modified (e.g. with a jewel).</summary>
    public static byte[] ItemModifySend(byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1Sub(0xF3, 0x14, w.ToArray());
    }

    /// <summary>PMSG_ITEM_GET_SEND reused as "money changed", C3:22 (ItemManager.h:105-112, result=0xFE is the
    /// special marker used by the money branch of CGItemGetRecv instead of a real item,
    /// ItemManager.cpp:2605-2669) -- the 4 money bytes are packed BIG-ENDIAN inside ItemInfo[0..3]
    /// (SET_NUMBERHB/LB of each 16-bit half), not little-endian like the rest of the protocol -- confirmed
    /// against the original's SET_NUMBER* macros, it is not a port error. ItemInfo[4] stays unused (padding 0).
    /// It is sent block-encrypted (see ClientSession.SendEncryptedAsync). REVERTED: an earlier note in this
    /// port claimed that <c>GAMESERVER_EXTRA</c> is "never defined as 1" in this tree -- FALSE, `stdafx.h:9-10`
    /// of the correct source tree ("Emulator 0.99 (2.1.7)/GameServer" -- see the doc-comment of <see
    /// cref="World.Item"/>) has <c>#ifndef GAMESERVER_EXTRA #define GAMESERVER_EXTRA 1 #endif</c>
    /// unconditionally, so it IS active, and the real struct (<c>ItemManager.h:105-112</c>) DOES have the final
    /// DWORD <c>ViewIndex</c>. The money branch of <c>CGItemGetRecv</c> does not set it explicitly (it stays at
    /// whatever the stack has, replicated here as 0). FIXED: the alignment padding before the final DWORD was
    /// missing -- PMSG_ITEM_GET_SEND uses PBMSG_HEAD (3 bytes, it is C3:22 directly without sub-code, see the
    /// doc-comment of CombatPacketBuilder.DamageSend for the full explanation of PBMSG_HEAD vs PSBMSG_HEAD).
    /// Offset after result(1)+ItemInfo[5] = 3+1+5 = 9, not a multiple of 4 -- 3 padding bytes are needed to
    /// reach 12 before ViewIndex.</summary>
    public static byte[] MoneySend(uint money)
    {
        var w = new PacketWriter();
        w.WriteByte(0xFE);

        Span<byte> info = stackalloc byte[Item.WireByteSize];
        info.Clear();
        info[0] = (byte)((money >> 24) & 0xFF);
        info[1] = (byte)((money >> 16) & 0xFF);
        info[2] = (byte)((money >> 8) & 0xFF);
        info[3] = (byte)(money & 0xFF);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        w.WriteBytes(new byte[3], 3); // alignment padding (offset 9 -> 12)
        w.WriteUInt32(0); // ViewIndex (GAMESERVER_EXTRA==1) -- rama de dinero no lo setea en el original

        return PacketBuilder.BuildC1(0x22, w.ToArray());
    }

    /// <summary>PMSG_ITEM_GET_SEND, C3:22 (ItemManager.h:105-112) -- same structure as <see cref="MoneySend"/>
    /// (same head, the original literally reuses the struct), but for the "I picked up a real item from the
    /// ground" case (CGItemGetRecv, ItemManager.cpp:3289-3528). <paramref name="result"/>: 0xFF=failed (denied
    /// by any of the validations -- out of range, blocked, no space), 0-234ish=inventory slot where it ended up
    /// (success). The original's 0xFD case ("it stacked with an existing item") is NOT ported -- this port has
    /// no item stacking logic (arrows/potions do not accumulate in a slot), so that result code never comes out
    /// of here. <paramref name="groundIndex"/> IS sent as the final DWORD <c>ViewIndex</c> (<c>pMsg.ViewIndex =
    /// index;</c> in the original, GAMESERVER_EXTRA==1 -- see the doc-comment of <see cref="MoneySend"/> for
    /// the correction of why this field is active).</summary>
    public static byte[] ItemGetSend(byte result, Item? item, int groundIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(result);

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        if (result != 0xFF && item != null)
        {
            item.ToWireBytes(info);
        }
        else
        {
            info.Clear();
        }

        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        w.WriteBytes(new byte[3], 3); // alignment padding (offset 9 -> 12, see the doc-comment of MoneySend)
        w.WriteUInt32((uint)groundIndex); // ViewIndex (GAMESERVER_EXTRA==1)
        return PacketBuilder.BuildC1(0x22, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DROP_SEND, C1:23 -- result: 0=failed (any of the CGItemDropRecv validations,
    /// ItemManager.cpp:3530-3718; this port only implements the generic subset documented in GroundItem.cs --
    /// without lucky/periodic/set/harmony/high-level excellent nor the special items with their own effect),
    /// 1=success.</summary>
    public static byte[] ItemDropSend(byte result, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte(slot);
        return PacketBuilder.BuildC1(0x23, w.ToArray());
    }
}

public sealed record ItemRepairRecv(byte Slot, byte Type)
{
    public static ItemRepairRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0);
}

public sealed record ItemUseRecv(byte SourceSlot, byte TargetSlot, byte Type)
{
    public static ItemUseRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0,
        p.Length > 5 ? p[5] : (byte)0);
}

public static class TradePacketBuilder
{
    /// <summary>PMSG_TRADE_REQUEST_SEND, C3:36 -- sends a Trade request to the target.</summary>
    public static byte[] TradeRequestSend(string name)
    {
        var w = new PacketWriter();
        Span<byte> nameBytes = stackalloc byte[10];
        nameBytes.Clear();
        Encoding.ASCII.GetBytes(name, nameBytes);
        w.WriteBytes(nameBytes.ToArray(), 10);
        return PacketBuilder.BuildC1(0x36, w.ToArray());
    }

    /// <summary>PMSG_TRADE_RESPONSE_SEND, C1:37 -- responde resultado de solicitud Trade.</summary>
    public static byte[] TradeResponseSend(byte response, string name, ushort level = 0, uint guildNumber = 0)
    {
        var w = new PacketWriter();
        w.WriteByte(response);
        Span<byte> nameBytes = stackalloc byte[10];
        nameBytes.Clear();
        Encoding.ASCII.GetBytes(name, nameBytes);
        w.WriteBytes(nameBytes.ToArray(), 10);
        w.WriteUInt16(level);
        w.WriteUInt32(guildNumber);
        return PacketBuilder.BuildC1(0x37, w.ToArray());
    }

    /// <summary>PMSG_TRADE_ITEM_DEL_SEND, C1:38 -- notifies that an item was removed from the Trade.</summary>
    public static byte[] TradeItemDelSend(byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        return PacketBuilder.BuildC1(0x38, w.ToArray());
    }

    /// <summary>PMSG_TRADE_ITEM_ADD_SEND, C1:39 -- notifies that an item was added to the Trade.</summary>
    public static byte[] TradeItemAddSend(byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x39, w.ToArray());
    }

    /// <summary>PMSG_TRADE_MONEY_SEND, C1:3B -- notifica cambio de Zen en Trade.</summary>
    public static byte[] TradeMoneySend(uint money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD (3 bytes) + DWORD money alineado a 4 -> 1 byte de relleno en el offset 3
        // (sizeof = 8, no 7). Verificado contra CTrade::GCTradeMoneySend (Trade.cpp:550-559), que
        // hace header.set(0x3B,sizeof(pMsg)) y manda esos 8 bytes.
        w.WriteByte(0); // alignment padding (offset 3 -> 4)
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x3B, w.ToArray());
    }

    /// <summary>PMSG_TRADE_OK_BUTTON_SEND, C1:3C -- notifies the OK button state (0=normal, 1=OK, 2=Yellow/Uncheck).</summary>
    public static byte[] TradeOkButtonSend(byte flag)
    {
        var w = new PacketWriter();
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x3C, w.ToArray());
    }

    /// <summary>PMSG_TRADE_RESULT_SEND, C1:3D -- notifica resultado final de Trade (0=cancel, 1=exito, 2=inv lleno, 5=zen max).</summary>
    public static byte[] TradeResultSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x3D, w.ToArray());
    }
}

public sealed record TradeRequestRecv(int TargetIndex)
{
    public static TradeRequestRecv Parse(byte[] p)
    {
        int index = p.Length > 4 ? ((p[3] << 8) | p[4]) : 0;
        return new TradeRequestRecv(index);
    }
}

public sealed record TradeResponseRecv(byte Response)
{
    public static TradeResponseRecv Parse(byte[] p)
    {
        byte response = p.Length > 3 ? p[3] : (byte)0;
        return new TradeResponseRecv(response);
    }
}

public sealed record TradeMoneyRecv(uint Money)
{
    public static TradeMoneyRecv Parse(byte[] p)
    {
        uint money = p.Length > 6 ? ((uint)p[3] << 24) | ((uint)p[4] << 16) | ((uint)p[5] << 8) | p[6] : 0;
        return new TradeMoneyRecv(money);
    }
}

public sealed record TradeOkRecv(byte Flag)
{
    public static TradeOkRecv Parse(byte[] p)
    {
        byte flag = p.Length > 3 ? p[3] : (byte)0;
        return new TradeOkRecv(flag);
    }
}

public static class WarehousePacketBuilder
{
    /// <summary>PMSG_WAREHOUSE_STATE_SEND, C1:83 -- notifies the warehouse state (0=unlocked, 1=locked/password, 10=wrong pw, 12=correct pw).</summary>
    public static byte[] WarehouseStateSend(byte state)
    {
        var w = new PacketWriter();
        w.WriteByte(state);
        return PacketBuilder.BuildC1(0x83, w.ToArray());
    }

    /// <summary>PMSG_WAREHOUSE_MONEY_SEND, C1:81 -- notifies the inventory and warehouse money after a deposit/withdrawal.</summary>
    public static byte[] WarehouseMoneySend(byte result, uint inventoryMoney, uint warehouseMoney)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteUInt32(warehouseMoney);
        w.WriteUInt32(inventoryMoney);
        return PacketBuilder.BuildC1(0x81, w.ToArray());
    }

    /// <summary>PMSG_SHOP_ITEM_LIST_SEND reused for Warehouse, C2:31 -- list of items in the warehouse (Warehouse.cpp:250-291).</summary>
    public static byte[] WarehouseListSend(PlayerObject player)
    {
        var w = new PacketWriter();
        w.WriteByte(0);
        int count = 0;
        for (int slot = 0; slot < player.WarehouseItems.Length; slot++)
        {
            if (player.WarehouseItems[slot].IsItem()) count++;
        }
        w.WriteByte((byte)count);

        Span<byte> itemInfo = stackalloc byte[Item.WireByteSize];
        for (int slot = 0; slot < player.WarehouseItems.Length; slot++)
        {
            var item = player.WarehouseItems[slot];
            if (!item.IsItem()) continue;
            w.WriteByte((byte)slot);
            item.ToWireBytes(itemInfo);
            w.WriteBytes(itemInfo.ToArray(), Item.WireByteSize);
        }

        return PacketBuilder.BuildC2(0x31, w.ToArray());
    }
}

public sealed record WarehouseMoneyRecv(byte Type, uint Money)
{
    public static WarehouseMoneyRecv Parse(byte[] p)
    {
        byte type = p.Length > 3 ? p[3] : (byte)0;
        uint money = p.Length > 7 ? ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7] : 0;
        return new WarehouseMoneyRecv(type, money);
    }
}

public sealed record WarehousePasswordRecv(byte Type, ushort Password, string PersonalCode)
{
    public static WarehousePasswordRecv Parse(byte[] p)
    {
        byte type = p.Length > 3 ? p[3] : (byte)0;
        ushort pw = p.Length > 5 ? (ushort)((p[4] << 8) | p[5]) : (ushort)0;
        string code = p.Length >= 16 ? Encoding.ASCII.GetString(p, 6, 10).TrimEnd('\0') : string.Empty;
        return new WarehousePasswordRecv(type, pw, code);
    }
}

public sealed record TeleportRecv(byte Gate, byte X, byte Y, byte Dir)
{
    public static TeleportRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0,
        p.Length > 5 ? p[5] : (byte)0,
        p.Length > 6 ? p[6] : (byte)0);
}

public sealed record TeleportMoveRecv(int MoveIndex)
{
    public static TeleportMoveRecv Parse(byte[] p)
    {
        int number = p.Length >= 10 ? ((p[8] << 8) | p[9]) : (p.Length >= 5 ? p[4] : 0);
        return new TeleportMoveRecv(number);
    }
}

// ---------------------------------------------------------------- Phase 5: chat and whisper (first pass) Port
// of CGChatRecv/CGChatWhisperRecv (Protocol.cpp) + GDGlobalWhisperRecv/DGGlobalWhisperRecv/
// DGGlobalWhisperEchoRecv (DataServer/DSProtocol.cpp, already implemented on the DataServer side). This pass
// covers: public chat (without the ~/@/@@/@>/$ sigils -- party/guild/gens are left for when those phases exist,
// documented in the README) and whisper (local + cross-GameServer via DataServer, a mechanism DataServer
// already had complete from before this phase).

/// <summary>PMSG_CHAT_RECV/SEND, C1:00 -- mismo layout en ambas direcciones (Protocol.h). El cliente
/// manda su propio nombre (se verifica contra el real, anti-spoof) + el mensaje.</summary>
public sealed record ChatRecv(string Name, string Message)
{
    public static ChatRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3); // C1 header: type,size,head = 3 bytes
        var name = r.ReadFixedString(10);
        var message = r.ReadFixedString(Math.Max(p.Length - 13, 0));
        return new ChatRecv(name, message);
    }
}

/// <summary>PMSG_CHAT_WHISPER_RECV/SEND, C1:02 -- RECV: name=nombre del destinatario. SEND:
/// name=nombre de quien susurra (origen).</summary>
public sealed record ChatWhisperRecv(string TargetName, string Message)
{
    public static ChatWhisperRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var target = r.ReadFixedString(10);
        var message = r.ReadFixedString(Math.Max(p.Length - 13, 0));
        return new ChatWhisperRecv(target, message);
    }
}

public static class ChatPacketBuilder
{
    /// <summary>C1:00 -- public chat, sent as is to the speaker itself (echo) and to everyone who has them in
    /// their VisibleTo (the same viewport mechanism movement already uses, see MsgSendV2 in the Phase 5
    /// research brief).</summary>
    public static byte[] ChatSend(string name, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x00, w.ToArray());
    }

    /// <summary>C1:02 -- delivery of a whisper to the recipient (local or via DataServer).</summary>
    public static byte[] ChatWhisperSend(string sourceName, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(sourceName, 10);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }

    /// <summary>C1:0D -- NoticeSend (notificaciones/mensajes del servidor en pantalla o chat).</summary>
    public static byte[] NoticeSend(string notice, byte type = 0)
    {
        byte[] textBytes = System.Text.Encoding.Latin1.GetBytes(notice);
        var w = new PacketWriter();
        w.WriteByte(type);   // 0=Noticia dorada en pantalla, 1=Mensaje azul en chatbox
        w.WriteByte(0);      // count
        w.WriteByte(0);      // opacity
        w.WriteUInt16(0);    // delay
        w.WriteUInt32(0);    // color
        w.WriteByte(0);      // speed
        w.WriteBytes(textBytes, textBytes.Length);
        w.WriteByte(0);      // null-terminator (avoids the stray ý character on the client)
        return PacketBuilder.BuildC1(0x0D, w.ToArray());
    }
}

public static class ChaosBoxPacketBuilder
{
    /// <summary>PMSG_SHOP_ITEM_LIST_SEND (C2:31), type=3 -- Opens the Chaos Box window on the client and syncs its items.</summary>
    public static byte[] ChaosBoxItemListSend(IReadOnlyList<Item> items)
    {
        using var body = new MemoryStream();
        byte itemCount = 0;
        Span<byte> itemWire = stackalloc byte[5];

        for (int slot = 0; slot < items.Count; slot++)
        {
            var item = items[slot];
            if (item.IsItem())
            {
                body.WriteByte((byte)slot);
                item.ToWireBytes(itemWire);
                body.Write(itemWire);
                itemCount++;
            }
        }

        var header = new PacketWriter();
        header.WriteByte(3); // Type 3 = Chaos Box
        header.WriteByte(itemCount);
        header.WriteBytes(body.ToArray(), (int)body.Length);

        return PacketBuilder.BuildC2(0x31, header.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_SEND (C1:86) -- Notifica el resultado de la mezcla de Chaos Box.</summary>
    public static byte[] ChaosMixSend(byte result, Item? createdItem)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        if (createdItem != null && createdItem.IsItem())
        {
            Span<byte> itemWire = stackalloc byte[5];
            createdItem.ToWireBytes(itemWire);
            w.WriteBytes(itemWire.ToArray(), 5);
            w.WriteByte(0xFF);
            w.WriteByte(0xFF);
        }
        else
        {
            for (int i = 0; i < 7; i++) w.WriteByte(0xFF);
        }
        return PacketBuilder.BuildC1(0x86, w.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_RATE_SEND (C1:88) -- Sends the success % and Zen cost to the Chaos Box UI.</summary>
    public static byte[] ChaosMixRateSend(int rate, int money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD is 3 bytes and the int rate needs 4-byte alignment, so MSVC inserts 1 padding byte at
        // offset 3 (sizeof = 12, not 11). The original sends the whole sizeof(pMsg) (ChaosBox.cpp:1024), so
        // without this byte rate and money reach the client shifted.
        w.WriteByte(0); // alignment padding (offset 3 -> 4)
        w.WriteUInt32((uint)rate);
        w.WriteUInt32((uint)money);
        return PacketBuilder.BuildC1(0x88, w.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_CLOSE (C1:87) -- Notifica el cierre de la ventana de Chaos Box.</summary>
    public static byte[] ChaosMixCloseSend()
    {
        return PacketBuilder.BuildC1(0x87, Array.Empty<byte>());
    }
}

public static class BloodCastlePacketBuilder
{
    /// <summary>PMSG_BLOOD_CASTLE_ENTER_SEND (C1:9A) -- Respuesta a solicitud de entrada a Blood Castle.</summary>
    public static byte[] BloodCastleEnterSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x9A, w.ToArray());
    }

    /// <summary>PMSG_BLOOD_CASTLE_STATE_SEND (C1:9B) -- Estado y reloj de Blood Castle.</summary>
    public static byte[] BloodCastleStateSend(byte state, ushort time, ushort maxMonster, ushort curMonster, ushort eventItemOwner, byte eventItemLevel)
    {
        var w = new PacketWriter();
        w.WriteByte(state);
        w.WriteUInt16(time);
        w.WriteUInt16(maxMonster);
        w.WriteUInt16(curMonster);
        w.WriteUInt16(eventItemOwner);
        w.WriteByte(eventItemLevel);
        // Tail padding: the struct aligns to 2 (because of the WORDs) and its last field ends at offset 12, so
        // sizeof = 14, not 13. The fields fall in the right place without this, but the declared size does not
        // match the original's and a client that validates sizeof rejects it.
        w.WriteByte(0);
        return PacketBuilder.BuildC1(0x9B, w.ToArray());
    }

    /// <summary>PMSG_BLOOD_CASTLE_SCORE_SEND (C1:93) -- Final score and reward delivery.</summary>
    public static byte[] BloodCastleScoreSend(byte type, byte flag, string name, uint score, uint exp, uint zen)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(flag);
        w.WriteFixedString(name, 10);
        w.WriteUInt16(0);
        w.WriteUInt32(score);
        w.WriteUInt32(exp);
        w.WriteUInt32(zen);
        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }
}

// ---------------------------------------------------------------- GameServer <-> DataServer (whisper cruzado)

public static class SocialDataServerPacketBuilder
{
    /// <summary>SDHP_GLOBAL_WHISPER_SEND (GS->DS), C1:72 -- exact mirror of what
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::GlobalWhisperRecv.Parse expects to read. Port of
    /// GDGlobalWhisperSend (Protocol.cpp), used when gObjFind does not find the recipient on THIS
    /// GameServer.</summary>
    public static byte[] GlobalWhisperRequest(ushort index, string account, string name, string targetName, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x72, w.ToArray());
    }
}

/// <summary>SDHP_GLOBAL_WHISPER_SEND (DS->GS back to the sender), C1:72 -- exact mirror of
/// DataServerPacketBuilder.GlobalWhisperSend. Port of DGGlobalWhisperRecv (Protocol.cpp): result=0 ->
/// "recipient not found on any GameServer" (GCServerMsgSend); result=1 -> it was already delivered via
/// DGGlobalWhisperEchoRecv on the recipient's GameServer.</summary>
public sealed record GlobalWhisperResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName, string Message)
{
    public static GlobalWhisperResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        var message = r.ReadFixedString(60);
        return new GlobalWhisperResultFromDataServer(index, account, name, result, targetName, message);
    }
}

/// <summary>SDHP_GLOBAL_WHISPER_ECHO_SEND (DS->recipient's GS), C1:73 -- exact mirror of
/// DataServerPacketBuilder.GlobalWhisperEchoSend. Port of DGGlobalWhisperEchoRecv (Protocol.cpp): it arrives at
/// the GameServer where the recipient is really connected, with their own Index/Account/Name (not the sender's)
/// + the name of whoever whispers + the message.</summary>
public sealed record GlobalWhisperEchoFromDataServer(ushort Index, string Account, string Name, string SourceName, string Message)
{
    public static GlobalWhisperEchoFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var sourceName = r.ReadFixedString(11);
        var message = r.ReadFixedString(60);
        return new GlobalWhisperEchoFromDataServer(index, account, name, sourceName, message);
    }
}

// ---------------------------------------------------------------- Phase 5: friends (first pass) Simplified
// port of GameServer/Friend.cpp (thin adapter towards DataServer) + Friend.h (client-side opcodes/structs).
// Friend mail (T_FriendMail) is not ported -- see the comment of db/postgres/004_friends.sql. The
// DataServer<->GameServer protocol (head 0xB0) uses a sub-code scheme of its own, simpler than the original's
// 1-to-1 PSBMSG_HEAD -- see the comment in MuServer.DataServer/Protocol/DataServerPackets.cs.

/// <summary>PMSG_FRIEND_LIST_RECV, C1:C0 -- no body. Not documented explicitly as a separate opcode in Friend.h
/// (the research only confirms the _SEND), but some trigger is needed for the client to ask for the list -- it
/// is assumed to share the head with the _SEND (the same pattern already used in this port for CHARACTER_LIST
/// and PARTY_LIST, both of which share a single head in both directions).</summary>
public sealed record FriendListClientRecv;

/// <summary>PMSG_FRIEND_REQUEST_RECV, C1:C1 -- Name[10] = who is to be added.</summary>
public sealed record FriendRequestClientRecv(string TargetName)
{
    public static FriendRequestClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new FriendRequestClientRecv(r.ReadFixedString(10));
    }
}

/// <summary>PMSG_FRIEND_RESULT_RECV, C1:C2 -- result=accept/reject, Name[10]=whoever sent the original
/// request.</summary>
public sealed record FriendResultClientRecv(byte Result, string RequesterName)
{
    public static FriendResultClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var result = r.ReadByte();
        var name = r.ReadFixedString(10);
        return new FriendResultClientRecv(result, name);
    }
}

/// <summary>PMSG_FRIEND_DELETE_RECV, C1:C3 -- Name[10] = who is to be deleted.</summary>
public sealed record FriendDeleteClientRecv(string TargetName)
{
    public static FriendDeleteClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new FriendDeleteClientRecv(r.ReadFixedString(10));
    }
}

public static class FriendPacketBuilder
{
    /// <summary>PMSG_FRIEND_LIST_SEND, C1:C0 -- MailCount/MailTotal fijos en 0 (mailbox no portado).</summary>
    public static byte[] FriendListSend(IReadOnlyList<(string Name, byte Server)> friends)
    {
        var w = new PacketWriter();
        w.WriteByte(0); // MailCount -- correo entre amigos no portado
        w.WriteByte(0); // MailTotal
        w.WriteByte((byte)Math.Min(friends.Count, 255));

        foreach (var (name, server) in friends)
        {
            w.WriteFixedString(name, 10);
            w.WriteByte(server);
        }

        return PacketBuilder.BuildC1(0xC0, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_REQUEST_SEND, C1:C1 -- push to the RECIPIENT telling them that someone wants to add
    /// them.</summary>
    public static byte[] FriendRequestSend(byte result, string name, byte server)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        w.WriteByte(server);
        return PacketBuilder.BuildC1(0xC1, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_RESULT_SEND, C1:C2 -- confirmation that they are now friends (sent to both sides:
    /// to whoever accepted, with the name of whoever asked for the friendship, and to whoever asked, with the
    /// name of whoever accepted it).</summary>
    public static byte[] FriendResultSend(string name)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        return PacketBuilder.BuildC1(0xC2, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_DELETE_SEND, C1:C3.</summary>
    public static byte[] FriendDeleteSend(byte result, string name)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        return PacketBuilder.BuildC1(0xC3, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_STATE_SEND, C1:C4 -- push no solicitado de cambio de estado online/offline.</summary>
    public static byte[] FriendStateSend(string name, byte server)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        w.WriteByte(server);
        return PacketBuilder.BuildC1(0xC4, w.ToArray());
    }
}

// ---------------------------------------------------------------- GameServer <-> DataServer (amigos, head 0xB0)

public static class FriendDataServerPacketBuilder
{
    public static byte[] FriendListRequest(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x00, w.ToArray());
    }

    public static byte[] FriendRequestRequest(ushort index, string account, string name, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x01, w.ToArray());
    }

    public static byte[] FriendResultRequest(ushort index, string account, string name, byte result, string requesterName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(requesterName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x03, w.ToArray());
    }

    public static byte[] FriendDeleteRequest(ushort index, string account, string name, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x05, w.ToArray());
    }
}

public sealed record FriendListEntryFromDataServer(string Name, byte Server);

public sealed record FriendListFromDataServer(ushort Index, string Account, string Name, IReadOnlyList<FriendListEntryFromDataServer> Friends)
{
    public static FriendListFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var count = r.ReadByte();
        var friends = new List<FriendListEntryFromDataServer>(count);

        for (int n = 0; n < count; n++)
        {
            var friendName = r.ReadFixedString(11);
            var server = r.ReadByte();
            friends.Add(new FriendListEntryFromDataServer(friendName, server));
        }

        return new FriendListFromDataServer(index, account, name, friends);
    }
}

public sealed record FriendRequestResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName)
{
    public static FriendRequestResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        return new FriendRequestResultFromDataServer(index, account, name, result, targetName);
    }
}

public sealed record FriendRequestIncomingFromDataServer(ushort TargetIndex, string TargetAccount, string TargetName, string RequesterName, byte RequesterServer)
{
    public static FriendRequestIncomingFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var requesterName = r.ReadFixedString(11);
        var requesterServer = r.ReadByte();
        return new FriendRequestIncomingFromDataServer(index, account, name, requesterName, requesterServer);
    }
}

public sealed record FriendResultAckFromDataServer(ushort Index, string Account, string Name, byte Result, string RequesterName)
{
    public static FriendResultAckFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var requesterName = r.ReadFixedString(11);
        return new FriendResultAckFromDataServer(index, account, name, result, requesterName);
    }
}

public sealed record FriendResultDeliverFromDataServer(ushort RequesterIndex, string RequesterAccount, string RequesterName, byte Result, string AccepterName)
{
    public static FriendResultDeliverFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var accepterName = r.ReadFixedString(11);
        return new FriendResultDeliverFromDataServer(index, account, name, result, accepterName);
    }
}

public sealed record FriendDeleteResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName)
{
    public static FriendDeleteResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        return new FriendDeleteResultFromDataServer(index, account, name, result, targetName);
    }
}

public sealed record FriendStateFromDataServer(ushort OwnerIndex, string OwnerAccount, string OwnerName, string FriendName, byte Server)
{
    public static FriendStateFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var friendName = r.ReadFixedString(11);
        var server = r.ReadByte();
        return new FriendStateFromDataServer(index, account, name, friendName, server);
    }
}

// ---------------------------------------------------------------- Phase 5: party (first pass) Port of
// Party.h/Party.cpp -- invite/accept/leave/kick/list/periodic life + party experience sharing (see
// ClientProtocolHandler.GrantPartyExperienceAsync). PartyMatching (party search board) is left out of this pass
// -- see the "safe to defer" note in the Phase 5 research brief (it is not a dependency of the basic party, it
// is a separate browsing UI).

/// <summary>PMSG_PARTY_REQUEST_RECV, C1:40 -- index = target of the invitation.</summary>
public sealed record PartyRequestRecv(int TargetIndex)
{
    public static PartyRequestRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_PARTY_REQUEST_RESULT_RECV, C1:41 -- result=accept/reject, index = who invited (so that the
/// server validates that the reply corresponds to a really pending invitation).</summary>
public sealed record PartyRequestResultRecv(byte Result, int InviterIndex)
{
    public static PartyRequestResultRecv Parse(byte[] p) => new(p[3], (p[4] << 8) | p[5]);
}

/// <summary>PMSG_PARTY_DEL_MEMBER_RECV, C1:43 -- number = own slot (leave) or another member's (kick, only if
/// the sender is the leader).</summary>
public sealed record PartyDelMemberRecv(byte Number)
{
    public static PartyDelMemberRecv Parse(byte[] p) => new(p[3]);
}

/// <summary>A row of PMSG_PARTY_LIST (24 bytes on the wire: 22 of fields + 2 of alignment padding that MSVC
/// inserts before <c>CurLife</c>) -- EXACT port of <c>Party.h</c> of the correct source tree ("Emulator 0.99
/// (2.1.7)/GameServer" -- see the doc-comment of <see cref="World.Item"/> for the explanation of why this repo
/// has two C++ trees and which one is the real one):
/// <c>name[10],number,map,x,y,CurLife(DWORD),MaxLife(DWORD)</c>. REVERTED: there is no ServerCode nor mana in
/// this struct -- they were fabricated fields from an earlier porting pass that investigated against the wrong
/// tree (a later season). Also confirmed against the real builder <c>CParty::GCPartyListSend</c>
/// (Party.cpp:594-650), which only fills these 7 fields.</summary>
public sealed record PartyListEntry(string Name, byte Number, byte Map, byte X, byte Y, uint CurLife, uint MaxLife);

public static class PartyPacketBuilder
{
    /// <summary>PMSG_PARTY_REQUEST_SEND, C1:40 -- tells the invitee who is inviting them.</summary>
    public static byte[] PartyRequestSend(int inviterIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((inviterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(inviterIndex & 0xFF));
        return PacketBuilder.BuildC1(0x40, w.ToArray());
    }

    /// <summary>PMSG_PARTY_RESULT_SEND, C1:41 -- result to whoever invited (0=failed/rejected, 2=party
    /// full/could not be created, 4=the target was already in a party).</summary>
    public static byte[] PartyResultSend(byte result)
    {
        return PacketBuilder.BuildC1(0x41, new[] { result });
    }

    /// <summary>PMSG_PARTY_LIST_SEND, C1:42 -- result=0 (no party) or 1 (with party) + list of members. Sent to
    /// ALL members every time the party composition changes (port of GCPartyListSend), and also to whoever asks
    /// for it on demand.</summary>
    public static byte[] PartyListSend(byte result, IReadOnlyList<PartyListEntry> members)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)members.Count);

        foreach (var m in members)
        {
            w.WriteFixedString(m.Name, 10);
            w.WriteByte(m.Number);
            w.WriteByte(m.Map);
            w.WriteByte(m.X);
            w.WriteByte(m.Y);

            // Party.h does not use #pragma pack(1): after name[10]+number+map+x+y we are at offset 14 and the
            // DWORD CurLife needs 4-byte alignment, so MSVC inserts 2 padding bytes (sizeof(PMSG_PARTY_LIST) =
            // 24, not 22). The original copies the whole struct per member
            // (memcpy(&send[size],&info,sizeof(info)), Party.cpp:535), so they go on the wire.
            w.WriteUInt16(0); // alignment padding (offset 14 -> 16)

            w.WriteUInt32(m.CurLife);
            w.WriteUInt32(m.MaxLife);
        }

        return PacketBuilder.BuildC1(0x42, w.ToArray());
    }

    /// <summary>PMSG_PARTY_DEL_MEMBER_SEND, C1:43 -- no body, tells the REMOVED client to clear its party
    /// interface (port of GCPartyDelMemberSend).</summary>
    public static byte[] PartyDelMemberSend()
    {
        return PacketBuilder.BuildC1(0x43, Array.Empty<byte>());
    }

    /// <summary>PMSG_PARTY_LIFE_SEND, C1:44 -- EXACT port of <c>CParty::GCPartyLifeSend</c> (Party.cpp:666-700
    /// of the correct source tree). REVERTED: an earlier pass had fabricated a 13-byte/member format
    /// (life%+mana%+name) investigating against the wrong tree. The real one is 1 BYTE per member: high nibble
    /// = slot (<c>number*16</c>), low nibble = life in "tenths" (<c>Life/((MaxLife+AddLife)/10)</c>, 0-9) -- no
    /// mana, no name (the client already has the name list from PMSG_PARTY_LIST and only needs to refresh the
    /// life bar per slot).</summary>
    public static byte[] PartyLifeSend(IReadOnlyList<(byte Number, uint Life, uint MaxLife)> members)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)members.Count);

        foreach (var (number, life, maxLife) in members)
        {
            uint tenth = maxLife == 0 ? 0 : Math.Max(1u, maxLife / 10);
            byte lifeBucket = (byte)Math.Min(15u, life / tenth);
            w.WriteByte((byte)(((number * 16) & 0xF0) | (lifeBucket & 0x0F)));
        }

        return PacketBuilder.BuildC1(0x44, w.ToArray());
    }
}

// ---------------------------------------------------------------- Phase 6: Devil Square (first pass) Port of
// DevilSquare.h/.cpp + the "Devil Square" portion of Protocol.h/.cpp (CGDevilSquareEnterRecv,
// CGEventRemainTimeRecv) + DSProtocol.h (GDRankingDevilSquareSaveSend, head 0x3F -- the DataServer side was
// already complete from before this phase, see DataServerProtocolHandler.OnRankingScoreSaveAsync). The state
// engine and all the entry/score/reward logic live in World/DevilSquareManager.cs; here only the packet
// layouts, like the rest of this file.

/// <summary>PMSG_DEVIL_SQUARE_ENTER_RECV, C1:90 -- level = bracket pedido (0-based), slot = slot de
/// INVENTARIO COMPLETO (incluye equipo, el servidor resta INVENTORY_WEAR_SIZE=12 antes de usarlo).</summary>
public sealed record DevilSquareEnterRecv(byte Level, byte Slot)
{
    public static DevilSquareEnterRecv Parse(byte[] p) => new(p[3], p[4]);
}

/// <summary>PMSG_EVENT_REMAIN_TIME_RECV, C1:91 -- EventType=1 is Devil Square (2/3/4 are Blood/Chaos/ Illusion
/// Temple, not ported). The server ignores ItemLevel and recomputes the player's own bracket (see
/// DevilSquareManager.HandleRemainTimeQueryAsync) -- it is parsed anyway so as not to break framing, even
/// though it is unused.</summary>
public sealed record EventRemainTimeRecv(byte EventType, byte ItemLevel)
{
    public static EventRemainTimeRecv Parse(byte[] p) => new(p[3], p[4]);
}

/// <summary>A row of PMSG_DEVIL_SQUARE_SCORE (24 bytes on the wire: name[10]+2 alignment
/// padding+score(4)+rewardExp(4)+ rewardMoney(4)) -- see DevilSquareManager.BuildScoreEntry.</summary>
public sealed record DevilSquareScoreEntry(string Name, uint Score, uint RewardExperience, uint RewardMoney);

public static class DevilSquarePacketBuilder
{
    /// <summary>PMSG_DEVIL_SQUARE_ENTER_SEND, C1:90 -- result: 0=ok, 1=invalid level/slot/item, 2=window
    /// closed, 3=character level too high for this bracket, 4=too low, 5=full.</summary>
    public static byte[] EnterSend(byte result) => PacketBuilder.BuildC1(0x90, new[] { result });

    /// <summary>PMSG_EVENT_REMAIN_TIME_SEND, C1:91 -- RemainTimeH = minutes left (until it opens, if still
    /// closed) OR EnteredUser = current participants (if already open) -- mutually exclusive, like the original
    /// (see this phase's research). RemainTimeL is always 0 in the Devil Square branch (the original does not
    /// write it there either).</summary>
    public static byte[] RemainTimeSend(byte eventType, byte remainTimeH, byte enteredUser)
    {
        var w = new PacketWriter();
        w.WriteByte(eventType);
        w.WriteByte(remainTimeH);
        w.WriteByte(enteredUser);
        w.WriteByte(0);
        return PacketBuilder.BuildC1(0x91, w.ToArray());
    }

    /// <summary>PMSG_TIME_COUNT_SEND, C1:92 -- 30-second klaxon, without text (the message catalogue is not
    /// ported, see the technical debt documented in DevilSquareManager). type: 0=EMPTY phase (broadcast to the
    /// WHOLE server), 1=STAND phase, 2=START phase (broadcast only to participants).</summary>
    public static byte[] TimeCountSend(byte type) => PacketBuilder.BuildC1(0x92, new[] { type });

    /// <summary>PMSG_DEVIL_SQUARE_SCORE_SEND, C1:93 -- rank = final position of the receiver (1-based),
    /// followed by up to MAX_DS_RANK(10) entries where #0 is ALWAYS the receiver themselves (repeated further
    /// down at their real position if they make the top 9 -- a quirk documented in the research, see
    /// DevilSquareManager.SendScoreListAsync).</summary>
    public static byte[] ScoreSend(byte rank, IReadOnlyList<DevilSquareScoreEntry> entries)
    {
        var w = new PacketWriter();
        w.WriteByte(rank);
        w.WriteByte((byte)entries.Count);

        foreach (var e in entries)
        {
            w.WriteFixedString(e.Name, 10);
            // DevilSquare.h does not use #pragma pack(1): after name[10] we are at offset 10 and the DWORD
            // score aligns to 4, so MSVC inserts 2 padding bytes (sizeof = 24, not 22). The original copies the
            // whole struct per entry (memcpy(&send[size],&info,sizeof(info)), DevilSquare.cpp:1307-1308), so
            // they go on the wire.
            w.WriteUInt16(0); // alignment padding (offset 10 -> 12)
            w.WriteUInt32(e.Score);
            w.WriteUInt32(e.RewardExperience);
            w.WriteUInt32(e.RewardMoney);
        }

        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }
}

/// <summary>GameServer -> DataServer, header shared by 0x3D/0x3E/0x3F/0x40 (Blood/Chaos/Devil/ Illusion Temple
/// -- see DSProtocol.h) -- exact mirror of what
/// MuServer.DataServer/Protocol/DataServerPackets.cs::RankingScoreSaveRecv.Parse expects to read (already
/// implemented on the DataServer side for the 4 heads, see DataServerProtocolHandler:62-65). Fire-and- forget,
/// no reply.</summary>
public static class EventDataServerPacketBuilder
{
    public const byte HeadDevilSquare = 0x3F;

    public static byte[] RankingScoreSave(byte head, ushort index, string account, string name, uint score)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteUInt32(score);
        return PacketBuilder.BuildC1(head, w.ToArray());
    }
}

// ---------------------------------------------------------------- "Skills" phase: magic and mana

/// <summary>PMSG_SKILL_ATTACK_RECV (SkillManager.h:102-108), C3:19 -- request to cast a single-target attack
/// skill (CSkillManager::CGSkillAttackRecv). Like the rest of this port's C3s: GameClientFramer already
/// synthesises it into a logical C1 packet before it gets here (the real type 0xC3/block encryption is resolved
/// in the framing layer), so it is parsed with the same 3-byte header layout as a normal C1.</summary>
public sealed record SkillAttackRecv(byte Skill, int TargetIndex, byte Dis)
{
    public static SkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        int index = p.Length > 5 ? ((p[4] << 8) | p[5]) : (p.Length > 4 ? p[4] : 0);
        byte dis = p.Length > 6 ? p[6] : (byte)0;
        return new SkillAttackRecv(skill, index, dis);
    }
}

public sealed record DurationSkillAttackRecv(byte Skill, byte X, byte Y, byte Dir, byte Dis, byte Angle, int TargetIndex, byte MagicKey)
{
    public static DurationSkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        byte x = p.Length > 4 ? p[4] : (byte)0;
        byte y = p.Length > 5 ? p[5] : (byte)0;
        byte dir = p.Length > 6 ? p[6] : (byte)0;
        byte dis = p.Length > 7 ? p[7] : (byte)0;
        byte angle = p.Length > 8 ? p[8] : (byte)0;
        int targetIndex = p.Length > 10 ? ((p[9] << 8) | p[10]) : 0xFFFF;
        byte magicKey = p.Length > 11 ? p[11] : (byte)0;
        return new DurationSkillAttackRecv(skill, x, y, dir, dis, angle, targetIndex, magicKey);
    }
}

public sealed record MultiSkillAttackRecv(byte Skill, byte X, byte Y, byte Serial, byte Count, IReadOnlyList<int> Targets)
{
    public static MultiSkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        byte x = p.Length > 4 ? p[4] : (byte)0;
        byte y = p.Length > 5 ? p[5] : (byte)0;
        byte serial = p.Length > 6 ? p[6] : (byte)0;
        byte count = p.Length > 7 ? p[7] : (byte)0;
        count = Math.Min(count, (byte)10);

        var targets = new List<int>();
        int offset = 8;
        for (int i = 0; i < count && offset + 2 <= p.Length; i++)
        {
            int index = (p[offset] << 8) | p[offset + 1];
            targets.Add(index);
            offset += 3;
        }

        return new MultiSkillAttackRecv(skill, x, y, serial, count, targets);
    }
}

public static class SkillPacketBuilder
{
    /// <summary>PMSG_SKILL_ATTACK_SEND (SkillManager.h:144-150), C3:19 -- port of
    /// CSkillManager::GCSkillAttackSend (SkillManager.cpp:2665-2685): it is sent both to the caster itself
    /// (unicast) and, by viewport, to those watching -- see ClientProtocolHandler.OnSkillAttackAsync for the
    /// fan-out. Block-encrypted (ClientSession.SendEncryptedAsync), like
    /// WorldPacketBuilder.TeleportSend.</summary>
    public static byte[] SkillAttackSend(byte skill, int casterIndex, int targetIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((casterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(casterIndex & 0xFF));
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x19, w.ToArray());
    }

    /// <summary>PMSG_DURATION_SKILL_ATTACK_SEND (C3:1E) -- Emits the spell animation and cast to the map's observers.</summary>
    public static byte[] DurationSkillAttackSend(int casterIndex, byte skill, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((casterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(casterIndex & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0x1E, w.ToArray());
    }

    /// <summary>PMSG_SKILL_LIST_SEND (SkillManager.h:177-189), C1:F3:11 -- puerto de
    /// CSkillManager::GCSkillListSend (SkillManager.cpp:2789-2827): manda la lista completa de skills
    /// activos del personaje (aprendidos + default de clase + skills de armas equipadas).</summary>
    public static byte[] SkillListSend(IReadOnlyList<(byte slot, ushort skillIndex, byte level)> skills)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)skills.Count);

        foreach (var (slot, skillIndex, level) in skills)
        {
            w.WriteByte(slot);
            byte b1 = (byte)(skillIndex & 0xFF);
            byte b2 = (byte)(((skillIndex / 255) & 7) | (level << 3));
            w.WriteByte(b1);
            w.WriteByte(b2);
        }

        return PacketBuilder.BuildC1Sub(0xF3, 0x11, w.ToArray());
    }

    /// <summary>PMSG_SKILL_LIST_SEND (C1:F3:11) count=0xFE -- Adds a skill to the client's bar in real time.</summary>
    public static byte[] SkillAddSend(byte slot, ushort skillIndex, byte level = 0)
    {
        var w = new PacketWriter();
        w.WriteByte(0xFE);
        w.WriteByte(slot);
        byte b1 = (byte)(skillIndex & 0xFF);
        byte b2 = (byte)(((skillIndex / 255) & 7) | (level << 3));
        w.WriteByte(b1);
        w.WriteByte(b2);
        return PacketBuilder.BuildC1Sub(0xF3, 0x11, w.ToArray());
    }

    /// <summary>PMSG_POSITION_SEND (Protocol.h:593), C1:D0 -- puerto de CGPositionRecv.</summary>
    public static byte[] PositionSend(int index, byte x, byte y)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        return PacketBuilder.BuildC1(0xD0, w.ToArray());
    }
}

public sealed record PositionRecv(byte X, byte Y)
{
    public static PositionRecv Parse(byte[] p)
    {
        byte x = p.Length > 3 ? p[3] : (byte)0;
        byte y = p.Length > 4 ? p[4] : (byte)0;
        return new PositionRecv(x, y);
    }
}

public static class LifePacketBuilder
{
    /// <summary>PMSG_LIFE_SEND (Protocol.h:364-373), C1:26 -- port of GCLifeSend (Protocol.cpp:1527): type=0xFE
    /// (MaxLife), type=0xFF (Current Life). In C++, ViewHP (DWORD) is aligned to 4 bytes, landing at offset 8
    /// (1 padding byte after flag). Without that padding byte, the client reads ViewHP shifted 8 bits producing
    /// negative garbage numbers.</summary>
    public static byte[] LifeSend(byte type, int life)
    {
        var w = new PacketWriter();
        int clampedLife = Math.Clamp(life, 0, 65535);
        w.WriteByte(type);
        w.WriteByte((byte)((clampedLife >> 8) & 0xFF));
        w.WriteByte((byte)(clampedLife & 0xFF));
        w.WriteByte(0); // flag (offset 6)
        w.WriteByte(0); // PADDING (offset 7) para alinear ViewHP a offset 8
        w.WriteUInt32((uint)clampedLife); // ViewHP (offset 8)
        return PacketBuilder.BuildC1(0x26, w.ToArray());
    }
}

public static class ManaPacketBuilder
{
    /// <summary>PMSG_MANA_SEND (Protocol.h:375-386), C1:27 -- port of GCManaSend (Protocol.cpp:1547): mana/bp
    /// are packed as big-endian WORDs (SET_NUMBERHB/LB, like the rest of this protocol's hand-built 2-byte
    /// fields, NOT as a normal little-endian WriteUInt16), clamped to 0-65535 like the original's
    /// GET_MAX_WORD_VALUE. GAMESERVER_EXTRA==1 in this build adds ViewMP/ViewBP (DWORD) at the end. FIXED (bug
    /// introduced in an earlier pass of this same session): PMSG_MANA_SEND uses PBMSG_HEAD (3 bytes:
    /// type+size+head, see the doc-comment of CombatPacketBuilder.DamageSend for the full explanation), NOT
    /// PSBMSG_HEAD (4 bytes) -- it is C1:27 directly, without sub-code. Real offset after type+mana[2]+bp[2] =
    /// 3+1+2+2 = 8, which is ALREADY a multiple of 4 -- ZERO padding bytes are needed. The 3 bytes that were
    /// added in excess shifted ViewMP/ViewBP and lengthened the packet, producing the same garbage mana/BP
    /// numbers that <see cref="CombatPacketBuilder.DamageSend"/> produced with life/damage.</summary>
    public static byte[] ManaSend(byte type, int mana, int bp)
    {
        var w = new PacketWriter();
        int clampedMana = Math.Clamp(mana, 0, 65535);
        int clampedBp = Math.Clamp(bp, 0, 65535);
        w.WriteByte(type);
        w.WriteByte((byte)((clampedMana >> 8) & 0xFF));
        w.WriteByte((byte)(clampedMana & 0xFF));
        w.WriteByte((byte)((clampedBp >> 8) & 0xFF));
        w.WriteByte((byte)(clampedBp & 0xFF));
        w.WriteUInt32((uint)clampedMana); // ViewMP
        w.WriteUInt32((uint)clampedBp); // ViewBP
        return PacketBuilder.BuildC1(0x27, w.ToArray());
    }
}

// ---------------------------------------------------------------- Quest info / pet item info (minimal replies
// so that the real client does not keep retrying -- the quest/pet systems themselves are not ported yet, see
// README).

/// <summary>PMSG_PET_ITEM_INFO_RECV (Protocol.h:178-184), C1:A9 -- query of the level/experience of a pet (Dark
/// Horse/Dark Reaven) stored in an inventory slot. type: 0/1 (which of the two pets), flag: 0=Inventory (the
/// only container supported in this pass, like the rest of the inventory -- see ItemPacketBuilder), slot: index
/// within that container.</summary>
public sealed record PetItemInfoRecv(byte Type, byte Flag, byte Slot)
{
    public static PetItemInfoRecv Parse(byte[] p) => new(p[3], p[4], p[5]);
}

public static class QuestPacketBuilder
{
    /// <summary>PMSG_QUEST_INFO_SEND (Quest.h:39-44), C1:A0 -- reply to CGQuestInfoRecv and to
    /// GCQuestStateSend/NpcTalk (Quest.cpp:397-417,419-432: GCQuestStateSend always sends this packet first).
    /// <paramref name="totalCount"/> is the real <c>m_QuestInfo.size()</c> (total rows loaded from Quest.txt,
    /// NOT "1 quest available" -- that hardcoding from an earlier pass stayed fixed at 1 no matter how many
    /// quests there really were).</summary>
    public static byte[] QuestInfoSend(byte[] questBlob, int totalCount = 1)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)Math.Clamp(totalCount, 0, 255));
        w.WriteBytes(questBlob, 50);
        return PacketBuilder.BuildC1(0xA0, w.ToArray());
    }

    /// <summary>PMSG_QUEST_STATE_SEND (Quest.h:46-51), C1:A1 -- respuesta a CGQuestStateRecv.</summary>
    public static byte[] QuestStateSend(byte questIndex, byte questState)
    {
        var w = new PacketWriter();
        w.WriteByte(questIndex);
        w.WriteByte(questState);
        return PacketBuilder.BuildC1(0xA1, w.ToArray());
    }

    /// <summary>PMSG_QUEST_RESULT_SEND (Quest.h:53-59), C1:A2 -- reply to Sebina's dialog.</summary>
    public static byte[] QuestResultSend(byte questIndex, byte questResult, byte questState)
    {
        var w = new PacketWriter();
        w.WriteByte(questIndex);
        w.WriteByte(questResult);
        w.WriteByte(questState);
        return PacketBuilder.BuildC1(0xA2, w.ToArray());
    }

    /// <summary>PMSG_QUEST_REWARD_SEND (Quest.h:61-70), C1:A3 -- port of CQuest::GCQuestRewardSend
    /// (Quest.cpp:449-471), called from CQuestReward::InsertQuestReward for each reward applied
    /// (POINT/CHANGE1/HERO/COMBO). <c>index</c> is the player's own index (not the quest's); <paramref
    /// name="rewardType"/>/<paramref name="amount"/> are <c>lpInfo.Index</c> (identifies WHICH reward, see <see
    /// cref="Config.QuestRewardType"/>) and the second parameter of each real call (Quantity for POINT/COMBO,
    /// the class just built for CHANGE1, the computed point for HERO) respectively -- the original reuses the
    /// same packet for the 4 types with different semantics in <c>QuestAmount</c> depending on the type,
    /// replicated as is here.</summary>
    public static byte[] QuestRewardSend(int playerIndex, byte rewardType, byte amount, uint viewPoint)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((playerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(playerIndex & 0xFF));
        w.WriteByte(rewardType);
        w.WriteByte(amount);
        // Tras header(3)+index[2]+QuestReward+QuestAmount vamos en el offset 7, y el DWORD ViewPoint
        // se alinea a 4 -> 1 byte de relleno (sizeof = 12, no 11).
        w.WriteByte(0); // alignment padding (offset 7 -> 8)
        w.WriteUInt32(viewPoint); // GAMESERVER_EXTRA==1 (siempre en este build, ver stdafx.h)
        return PacketBuilder.BuildC1(0xA3, w.ToArray());
    }

    /// <summary>PMSG_PET_ITEM_INFO_SEND (Protocol.h:462-470), C1:A9 -- echo of a pet's level/experience. This
    /// port does not track PetItemLevel/PetItemExp yet (no real pet item spawns with the current balance), so
    /// it always answers level=0/experience=0 -- enough for the real client not to keep waiting for a reply
    /// that never arrives. Note: the original GCPetItemInfoSend itself receives a "durability" parameter that
    /// it NEVER uses (it is not in the packet's struct, Protocol.cpp:1779-1796) -- it is omitted here for the
    /// same reason, it is not a simplification of this port.</summary>
    public static byte[] PetItemInfoSend(byte type, byte flag, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(flag);
        w.WriteByte(slot);
        w.WriteByte(0); // level
        // Tras header(3)+type+flag+slot+level vamos en el offset 7, y el UINT experience se alinea a
        // 4 -> 1 byte de relleno (sizeof = 12, no 11).
        w.WriteByte(0); // alignment padding (offset 7 -> 8)
        w.WriteUInt32(0); // experience
        return PacketBuilder.BuildC1(0xA9, w.ToArray());
    }
}

// ---------------------------------------------------------------- NPCs and shops (first pass) Port of
// NpcTalk.h/.cpp (only CGNpcTalkRecv/CGNpcTalkCloseRecv, without quests nor the special NPC classes --
// Trainer/AngelKing/Charon/Warehouse/GuildMaster, see README) + Shop.h/.cpp/ ShopManager.h/.cpp (buy/sell,
// without Trade/Warehouse/PersonalShop yet).

/// <summary>PMSG_NPC_TALK_RECV (NpcTalk.h:14-19), C1:30 -- index is the <em>gObj[]</em> slot of the NPC (the
/// same index space MonsterRegistry/PlayerRegistry use in this port, see the comment of Monster.ShopNumber),
/// not a "shop number".</summary>
public sealed record NpcTalkRecv(int NpcIndex)
{
    public static NpcTalkRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_ITEM_BUY_RECV (ItemManager.h:81-85), C1:32 -- slot dentro de la grilla de 8x15 de
/// la tienda actualmente abierta (PlayerObject.TargetShopNumber).</summary>
public sealed record ItemBuyRecv(byte Slot)
{
    public static ItemBuyRecv Parse(byte[] p) => new(p[3]);
}

/// <summary>PMSG_ITEM_SELL_RECV (ItemManager.h:87-91), C1:33 -- slot del INVENTARIO del jugador (no
/// de la tienda).</summary>
public sealed record ItemSellRecv(byte Slot)
{
    public static ItemSellRecv Parse(byte[] p) => new(p[3]);
}

public static class ShopPacketBuilder
{
    private static byte[] BuildC2NoSub(byte head, byte[] payload)
    {
        var buff = new byte[4 + payload.Length];
        int size = buff.Length;
        buff[0] = 0xC2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        payload.CopyTo(buff, 4);
        return buff;
    }

    /// <summary>PMSG_NPC_TALK_SEND (NpcTalk.h:21-25), C3:30 -- confirms that the NPC can be talked to. result=0
    /// always in this pass (only the "shop" branch of CNpcTalk::NpcTalk was ported, which in the original sends
    /// result=0; the special classes -- Trainer/Charon/etc -- use other result values that this port does not
    /// generate because it does not implement them).</summary>
    public static byte[] NpcTalkSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x30, w.ToArray());
    }

    /// <summary>PMSG_SHOP_ITEM_LIST_SEND (Shop.h:19-30), C2:31 (watch out!: same head value as
    /// NpcTalkCloseRecv, but it is a DIFFERENT packet -- this one goes server->client unencrypted, C2 instead
    /// of C1, independent head namespace per direction like the rest of the protocol). type always 0 in the
    /// original (it does not distinguish shop variants). count+repeated{slot,ItemInfo[5]} -- the same
    /// 6-byte-per-entry layout as ItemListSend.</summary>
    public static byte[] ShopItemListSend(ShopInfo shop)
    {
        using var body = new MemoryStream();
        body.WriteByte(0); // type
        var countPos = body.Position;
        body.WriteByte(0); // corrected further below
        int count = 0;

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        for (int slot = 0; slot < ShopInfo.Size; slot++)
        {
            var item = shop.Slots[slot];

            if (item == null || !item.IsItem())
            {
                continue;
            }

            item.ToWireBytes(info);
            body.WriteByte((byte)slot);
            body.Write(info);
            count++;
        }

        var bytes = body.ToArray();
        bytes[countPos] = (byte)count;

        return BuildC2NoSub(0x31, bytes);
    }

    /// <summary>PMSG_ITEM_BUY_SEND (ItemManager.h:151-157), C1:32 -- result=0xFF is the failure marker (like
    /// the original's CGItemBuyRecv: all the failed validations send this same packet with result=0xFF instead
    /// of closing the connection or not answering). Success result = the inventory slot where the item ended
    /// up.</summary>
    public static byte[] ItemBuySend(byte result, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x32, w.ToArray());
    }

    /// <summary>PMSG_ITEM_SELL_SEND (ItemManager.h:159-163), C1:33 -- result=0 failure/1 success (like the rest
    /// of this protocol's booleans), money=TOTAL money after the sale (not the delta).</summary>
    public static byte[] ItemSellSend(byte result, uint money)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x33, w.ToArray());
    }
}

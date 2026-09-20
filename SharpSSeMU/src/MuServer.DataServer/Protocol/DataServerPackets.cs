using MuServer.Shared.Protocol;

namespace MuServer.DataServer.Protocol;

// All the original structs use PBMSG_HEAD (C1, 1-byte size) except where C2 (word size) is indicated: 0x09
// (Pet), 0x01 (CharacterList), 0x04 (CharacterInfo), 0x30/0x31 (big saves), 0x34 (Pet save). Offsets replicated
// 1:1 from DataServerProtocol.h (structs without padding, 1 byte).

public sealed record ServerInfoRecv(byte Type, ushort ServerPort, string ServerName, ushort ServerCode, uint FreeSize)
{
    public static ServerInfoRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var type = r.ReadByte();
        var port = r.ReadUInt16();
        var name = r.ReadFixedString(50);
        var code = r.ReadUInt16();
        var free = r.ReadUInt32();
        return new ServerInfoRecv(type, port, name, code, free);
    }
}

public sealed record CharacterListRecv(ushort Index, string Account)
{
    public static CharacterListRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new CharacterListRecv(r.ReadUInt16(), r.ReadFixedString(11));
    }
}

public sealed record CharacterCreateRecv(ushort Index, string Account, string Name, byte Class)
{
    public static CharacterCreateRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var cls = r.ReadByte();
        return new CharacterCreateRecv(index, account, name, cls);
    }
}

public sealed record CharacterDeleteRecv(ushort Index, string Account, string Name)
{
    public static CharacterDeleteRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        // guild(1) + GuildName[9] no se usan en este alcance (gremios diferido).
        return new CharacterDeleteRecv(index, account, name);
    }
}

public sealed record CharacterInfoRecv(ushort Index, string Account, string Name)
{
    public static CharacterInfoRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new CharacterInfoRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record CreateItemRecv(
    ushort Index, string Account, byte X, byte Y, byte Map, ushort ItemIndex, byte Level, byte Dur,
    byte Option1, byte Option2, byte Option3, byte NewOption, ushort LootIndex, byte SetOption)
{
    public static CreateItemRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var x = r.ReadByte();
        var y = r.ReadByte();
        var map = r.ReadByte();
        var itemIndex = r.ReadUInt16();
        var level = r.ReadByte();
        var dur = r.ReadByte();
        var o1 = r.ReadByte();
        var o2 = r.ReadByte();
        var o3 = r.ReadByte();
        var newOpt = r.ReadByte();
        var lootIndex = r.ReadUInt16();
        var setOpt = r.ReadByte();
        return new CreateItemRecv(index, account, x, y, map, itemIndex, level, dur, o1, o2, o3, newOpt, lootIndex, setOpt);
    }
}

public sealed record OptionDataRecv(ushort Index, string Account, string Name)
{
    public static OptionDataRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new OptionDataRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record PetItemInfoRecv(ushort Index, string Account, byte Type, IReadOnlyList<(byte Slot, uint Serial)> Items)
{
    public static PetItemInfoRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // header C2 = 4 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var type = r.ReadByte();
        var count = r.ReadByte();

        var items = new List<(byte, uint)>(count);

        for (int n = 0; n < count; n++)
        {
            var slot = r.ReadByte();
            var serial = r.ReadUInt32();
            items.Add((slot, serial));
        }

        return new PetItemInfoRecv(index, account, type, items);
    }
}

public sealed record GlobalPostRecv(ushort MapServerGroup, byte Type, string Name, string Message)
{
    public static GlobalPostRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new GlobalPostRecv(r.ReadUInt16(), r.ReadByte(), r.ReadFixedString(11), r.ReadFixedString(60));
    }
}

public sealed record GlobalNoticeRecv(ushort MapServerGroup, byte Type, byte Count, byte Opacity, ushort Delay, uint Color, byte Speed, string Message)
{
    public static GlobalNoticeRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var group = r.ReadUInt16();
        var type = r.ReadByte();
        var count = r.ReadByte();
        var opacity = r.ReadByte();
        var delay = r.ReadUInt16();
        var color = r.ReadUInt32();
        var speed = r.ReadByte();
        var message = r.ReadFixedString(128);
        return new GlobalNoticeRecv(group, type, count, opacity, delay, color, speed, message);
    }
}

public sealed record MonsterKillCountRecv(ushort Index, string Account, string Name, ushort MonsterClass)
{
    public static MonsterKillCountRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new MonsterKillCountRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11), r.ReadUInt16());
    }
}

public sealed record CharacterInfoSaveRecv(
    ushort Index, string Account, string Name, ushort Level, byte Class, uint LevelUpPoint, uint Experience,
    uint Money, uint Strength, uint Dexterity, uint Vitality, uint Energy, uint Leadership, uint Life,
    uint MaxLife, uint Mana, uint MaxMana, uint BP, uint MaxBP, byte[] Inventory, byte[] Skill, byte Map,
    byte X, byte Y, byte Dir, uint PKCount, uint PKLevel, uint PKTime, byte[] Quest, uint ChatLimitTime,
    ushort FruitAddPoint, ushort FruitSubPoint, byte[] Effect, ushort BCCount, ushort CCCount, ushort DSCount)
{
    public static CharacterInfoSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // header C2
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var level = r.ReadUInt16();
        var cls = r.ReadByte();
        var lup = r.ReadUInt32();
        var exp = r.ReadUInt32();
        var money = r.ReadUInt32();
        var str = r.ReadUInt32();
        var dex = r.ReadUInt32();
        var vit = r.ReadUInt32();
        var ene = r.ReadUInt32();
        var lead = r.ReadUInt32();
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
        var pkc = r.ReadUInt32();
        var pkl = r.ReadUInt32();
        var pkt = r.ReadUInt32();
        var quest = r.ReadBytes(50);
        var chat = r.ReadUInt32();
        var fap = r.ReadUInt16();
        var fsp = r.ReadUInt16();
        var effect = r.ReadBytes(208);
        var bc = r.ReadUInt16();
        var cc = r.ReadUInt16();
        var ds = r.ReadUInt16();

        return new CharacterInfoSaveRecv(index, account, name, level, cls, lup, exp, money, str, dex, vit, ene,
            lead, life, maxLife, mana, maxMana, bp, maxBp, inventory, skill, map, x, y, dir, pkc, pkl, pkt, quest,
            chat, fap, fsp, effect, bc, cc, ds);
    }
}

public sealed record InventoryItemSaveRecv(ushort Index, string Account, string Name, byte[] Inventory)
{
    public static InventoryItemSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // header C2
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var inventory = r.ReadBytes(1728);
        return new InventoryItemSaveRecv(index, account, name, inventory);
    }
}

public sealed record OptionDataSaveRecv(ushort Index, string Account, string Name, byte[] SkillKey, byte GameOption, byte QKey, byte WKey, byte EKey, byte ChatWindow)
{
    public static OptionDataSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3); // header C1
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var skillKey = r.ReadBytes(10);
        var gameOption = r.ReadByte();
        var qKey = r.ReadByte();
        var wKey = r.ReadByte();
        var eKey = r.ReadByte();
        var chatWindow = r.ReadByte();
        return new OptionDataSaveRecv(index, account, name, skillKey, gameOption, qKey, wKey, eKey, chatWindow);
    }
}

public sealed record PetItemInfoSaveRecv(ushort Index, string Account, IReadOnlyList<(uint Serial, byte Level, uint Experience)> Items)
{
    public static PetItemInfoSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // header C2
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var count = r.ReadByte();

        var items = new List<(uint, byte, uint)>(count);

        for (int n = 0; n < count; n++)
        {
            var serial = r.ReadUInt32();
            var level = r.ReadByte();
            var exp = r.ReadUInt32();
            items.Add((serial, level, exp));
        }

        return new PetItemInfoSaveRecv(index, account, items);
    }
}

public sealed record ResetInfoSaveRecv(ushort Index, string Account, string Name, uint Reset, uint ResetDay, uint ResetWek, uint ResetMon)
{
    public static ResetInfoSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        return new ResetInfoSaveRecv(index, account, name, r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
    }
}

public sealed record MasterResetInfoSaveRecv(ushort Index, string Account, string Name, uint Reset, uint MasterReset, uint MasterResetDay, uint MasterResetWek, uint MasterResetMon)
{
    public static MasterResetInfoSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        return new MasterResetInfoSaveRecv(index, account, name, r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
    }
}

public sealed record RankingDuelSaveRecv(ushort Index, string Account, string Name, uint WinScore, uint LoseScore)
{
    public static RankingDuelSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        return new RankingDuelSaveRecv(index, account, name, r.ReadUInt32(), r.ReadUInt32());
    }
}

/// <summary>Common shape of 0x3D/0x3E/0x3F/0x40 (Blood/Chaos/Devil/IllusionTemple) — a single DWORD score.</summary>
public sealed record RankingScoreSaveRecv(ushort Index, string Account, string Name, uint Score)
{
    public static RankingScoreSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        return new RankingScoreSaveRecv(index, account, name, r.ReadUInt32());
    }
}

public sealed record CreationCardSaveRecv(ushort Index, string Account, byte ExtClass)
{
    public static CreationCardSaveRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new CreationCardSaveRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadByte());
    }
}

public sealed record ConnectCharacterRecv(ushort Index, string Account, string Name)
{
    public static ConnectCharacterRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new ConnectCharacterRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record DisconnectCharacterRecv(ushort Index, string Account, string Name)
{
    public static DisconnectCharacterRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new DisconnectCharacterRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record GlobalWhisperRecv(ushort Index, string Account, string Name, string TargetName, string Message)
{
    public static GlobalWhisperRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var target = r.ReadFixedString(11);
        var message = r.ReadFixedString(60);
        return new GlobalWhisperRecv(index, account, name, target, message);
    }
}

/// <summary>Entrada individual de la lista de personajes (formato compacto de inventario, 60 bytes).</summary>
public sealed record CharacterListEntry(byte Slot, string Name, ushort Level, byte Class, byte CtlCode, byte[] CompactInventory);

// ---------------------------------------------------------------- Phase 5 (GameServer): friends, head 0xB0
// Simplified port of CFriend/DataServer/Friend.cpp: instead of the PSBMSG_HEAD layout identical to the original
// (sub-codes 0x00-0x08 mirroring the client's 1 to 1), a simpler scheme of its own is used here for GS->DS vs.
// DS->GS sub-codes, since this port does not need to keep binary compatibility with an external
// DataServer-to-DataServer client (only with its own GameServer, also ported in this same repo). Friend mail
// (T_FriendMail) is not ported -- see the comment in 004_friends.sql.

public sealed record FriendListRequest(ushort Index, string Account, string Name)
{
    public static FriendListRequest Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C1 header + subh: type,size,head,subh = 4 bytes
        return new FriendListRequest(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record FriendRequestRecv(ushort Index, string Account, string Name, string TargetName)
{
    public static FriendRequestRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        return new FriendRequestRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public sealed record FriendResultRecv(ushort Index, string Account, string Name, byte Result, string RequesterName)
{
    public static FriendResultRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var requesterName = r.ReadFixedString(11);
        return new FriendResultRecv(index, account, name, result, requesterName);
    }
}

public sealed record FriendDeleteRecv(ushort Index, string Account, string Name, string TargetName)
{
    public static FriendDeleteRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        return new FriendDeleteRecv(r.ReadUInt16(), r.ReadFixedString(11), r.ReadFixedString(11), r.ReadFixedString(11));
    }
}

public static class DataServerPacketBuilder
{
    public static byte[] ServerInfoSend(byte result, uint itemCount)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteUInt32(itemCount);
        return PacketBuilder.BuildC1(0x00, w.ToArray());
    }

    public static byte[] CharacterListSend(ushort index, string account, byte moveCnt, byte extClass, IReadOnlyList<CharacterListEntry> entries)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteByte(moveCnt);
        w.WriteByte(extClass);
        w.WriteByte((byte)entries.Count);

        foreach (var e in entries)
        {
            w.WriteByte(e.Slot);
            w.WriteFixedString(e.Name, 11);
            w.WriteUInt16(e.Level);
            w.WriteByte(e.Class);
            w.WriteByte(e.CtlCode);
            w.WriteBytes(e.CompactInventory, 60);
        }

        return PacketBuilder.BuildC2(0x01, w.ToArray());
    }

    public static byte[] CharacterCreateSend(ushort index, string account, string name, byte result, byte slot, byte cls, ushort level)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteByte(slot);
        w.WriteByte(cls);
        // The original fills "equipment" with 0xFF (empty slot), not with 0x00.
        var equipment = new byte[24];
        Array.Fill(equipment, (byte)0xFF);
        w.WriteBytes(equipment, 24);
        w.WriteUInt16(level);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }

    public static byte[] CharacterDeleteSend(ushort index, string account, byte result)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x03, w.ToArray());
    }

    public static byte[] CharacterInfoSend(
        ushort index, string account, string name, byte result, byte cls, ushort level, uint levelUpPoint,
        uint experience, uint money, uint strength, uint dexterity, uint vitality, uint energy, uint leadership,
        uint life, uint maxLife, uint mana, uint maxMana, uint bp, uint maxBp, byte[] inventory, byte[] skill,
        byte map, byte x, byte y, byte dir, uint pkCount, uint pkLevel, uint pkTime, byte ctlCode, byte[] quest,
        uint chatLimitTime, ushort fruitAddPoint, ushort fruitSubPoint, byte[] effect, uint reset, uint masterReset,
        uint isNewChar, ushort married, string marryName, ushort bcCount, ushort ccCount, ushort dsCount)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteByte(cls);
        w.WriteUInt16(level);
        w.WriteUInt32(levelUpPoint);
        w.WriteUInt32(experience);
        w.WriteUInt32(money);
        w.WriteUInt32(strength);
        w.WriteUInt32(dexterity);
        w.WriteUInt32(vitality);
        w.WriteUInt32(energy);
        w.WriteUInt32(leadership);
        w.WriteUInt32(life);
        w.WriteUInt32(maxLife);
        w.WriteUInt32(mana);
        w.WriteUInt32(maxMana);
        w.WriteUInt32(bp);
        w.WriteUInt32(maxBp);
        w.WriteBytes(inventory, 1728);
        w.WriteBytes(skill, 180);
        w.WriteByte(map);
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        w.WriteUInt32(pkCount);
        w.WriteUInt32(pkLevel);
        w.WriteUInt32(pkTime);
        w.WriteByte(ctlCode);
        w.WriteBytes(quest, 50);
        w.WriteUInt32(chatLimitTime);
        w.WriteUInt16(fruitAddPoint);
        w.WriteUInt16(fruitSubPoint);
        w.WriteBytes(effect, 208);
        w.WriteUInt32(reset);
        w.WriteUInt32(masterReset);
        // DSProtocol.h (GameServer) expects these 3 fields here which the original DataServerProtocol.h
        // (DataServer) does not have -- a real discrepancy between the original packet's two headers. They are
        // added with default values (marriage not implemented yet) so as not to break the binary layout the
        // GameServer expects.
        w.WriteUInt32(isNewChar);
        w.WriteUInt16(married);
        w.WriteFixedString(marryName, 11);
        w.WriteUInt16(bcCount);
        w.WriteUInt16(ccCount);
        w.WriteUInt16(dsCount);
        return PacketBuilder.BuildC2(0x04, w.ToArray());
    }

    public static byte[] CreateItemSend(
        ushort index, string account, byte x, byte y, byte map, uint serial, ushort itemIndex, byte level,
        byte dur, byte option1, byte option2, byte option3, byte newOption, ushort lootIndex, byte setOption)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(map);
        w.WriteUInt32(serial);
        w.WriteUInt16(itemIndex);
        w.WriteByte(level);
        w.WriteByte(dur);
        w.WriteByte(option1);
        w.WriteByte(option2);
        w.WriteByte(option3);
        w.WriteByte(newOption);
        w.WriteUInt16(lootIndex);
        w.WriteByte(setOption);
        return PacketBuilder.BuildC1(0x07, w.ToArray());
    }

    public static byte[] OptionDataSend(ushort index, string account, string name, byte[] skillKey, byte gameOption, byte qKey, byte wKey, byte eKey, byte chatWindow)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteBytes(skillKey, 10);
        w.WriteByte(gameOption);
        w.WriteByte(qKey);
        w.WriteByte(wKey);
        w.WriteByte(eKey);
        w.WriteByte(chatWindow);
        return PacketBuilder.BuildC1(0x08, w.ToArray());
    }

    public static byte[] PetItemInfoSend(ushort index, string account, byte type, IReadOnlyList<(byte Slot, uint Serial, byte Level, uint Experience)> items)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteByte(type);
        w.WriteByte((byte)items.Count);

        foreach (var it in items)
        {
            w.WriteByte(it.Slot);
            w.WriteUInt32(it.Serial);
            w.WriteByte(it.Level);
            w.WriteUInt32(it.Experience);
        }

        return PacketBuilder.BuildC2(0x09, w.ToArray());
    }

    public static byte[] MonsterKillCountSend(ushort index, string account, string name, ushort monsterClass, ushort count)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteUInt16(monsterClass);
        w.WriteUInt16(count);
        return PacketBuilder.BuildC1(0x50, w.ToArray());
    }

    public static byte[] GlobalPostSend(ushort mapServerGroup, byte type, string name, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(mapServerGroup);
        w.WriteByte(type);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x20, w.ToArray());
    }

    public static byte[] GlobalNoticeSend(ushort mapServerGroup, byte type, byte count, byte opacity, ushort delay, uint color, byte speed, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(mapServerGroup);
        w.WriteByte(type);
        w.WriteByte(count);
        w.WriteByte(opacity);
        w.WriteUInt16(delay);
        w.WriteUInt32(color);
        w.WriteByte(speed);
        w.WriteFixedString(message, 128);
        return PacketBuilder.BuildC1(0x21, w.ToArray());
    }

    public static byte[] GlobalWhisperSend(ushort index, string account, string name, byte result, string targetName, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(targetName, 11);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x72, w.ToArray());
    }

    public static byte[] GlobalWhisperEchoSend(ushort index, string account, string name, string sourceName, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(sourceName, 11);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x73, w.ToArray());
    }

    // ---------------------------------------------------------------- Fase 5 (GameServer): amigos, head 0xB0

    /// <summary>0xB0:00 -- reply to FriendListRequest. server=0xFF if the friend is offline (or could not be
    /// determined), or the real ServerCode if online on some GameServer.</summary>
    public static byte[] FriendListSend(ushort index, string account, string name, IReadOnlyList<(string Name, byte Server)> friends)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte((byte)Math.Min(friends.Count, 255));

        foreach (var (friendName, server) in friends)
        {
            w.WriteFixedString(friendName, 11);
            w.WriteByte(server);
        }

        return PacketBuilder.BuildC1Sub(0xB0, 0x00, w.ToArray());
    }

    /// <summary>0xB0:01 -- ack to whoever SENT the friend request (0=could not, e.g. non-existent target or
    /// already friends; 1=request stored/delivered).</summary>
    public static byte[] FriendRequestResultSend(ushort index, string account, string name, byte result, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x01, w.ToArray());
    }

    /// <summary>0xB0:02 -- push to the RECIPIENT's GameServer: someone sent them a friend request (only
    /// delivered if they are online at THAT moment -- same documented limitation as whisper: there is no
    /// notification queue for when the recipient connects later).</summary>
    public static byte[] FriendRequestIncomingSend(ushort targetIndex, string targetAccount, string targetName, string requesterName, byte requesterServer)
    {
        var w = new PacketWriter();
        w.WriteUInt16(targetIndex);
        w.WriteFixedString(targetAccount, 11);
        w.WriteFixedString(targetName, 11);
        w.WriteFixedString(requesterName, 11);
        w.WriteByte(requesterServer);
        return PacketBuilder.BuildC1Sub(0xB0, 0x02, w.ToArray());
    }

    /// <summary>0xB0:03 -- ack to whoever ACCEPTED/REJECTED, confirming it was processed.</summary>
    public static byte[] FriendResultAckSend(ushort index, string account, string name, byte result, string requesterName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(requesterName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x03, w.ToArray());
    }

    /// <summary>0xB0:04 -- push to the GameServer of whoever sent the ORIGINAL request, telling them that it
    /// was accepted/rejected. Only delivered if they are still online.</summary>
    public static byte[] FriendResultDeliverSend(ushort requesterIndex, string requesterAccount, string requesterName, byte result, string accepterName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(requesterIndex);
        w.WriteFixedString(requesterAccount, 11);
        w.WriteFixedString(requesterName, 11);
        w.WriteByte(result);
        w.WriteFixedString(accepterName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x04, w.ToArray());
    }

    /// <summary>0xB0:05 -- ack de borrado de amistad.</summary>
    public static byte[] FriendDeleteResultSend(ushort index, string account, string name, byte result, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x05, w.ToArray());
    }

    /// <summary>0xB0:06 -- port of CFriend::DGFriendStateSend: push to the GameServer of <paramref
    /// name="ownerIndex"/> telling them that <paramref name="friendName"/> changed state (server=0xFF offline,
    /// or the real ServerCode if it connected).</summary>
    public static byte[] FriendStateSend(ushort ownerIndex, string ownerAccount, string ownerName, string friendName, byte server)
    {
        var w = new PacketWriter();
        w.WriteUInt16(ownerIndex);
        w.WriteFixedString(ownerAccount, 11);
        w.WriteFixedString(ownerName, 11);
        w.WriteFixedString(friendName, 11);
        w.WriteByte(server);
        return PacketBuilder.BuildC1Sub(0xB0, 0x06, w.ToArray());
    }
}

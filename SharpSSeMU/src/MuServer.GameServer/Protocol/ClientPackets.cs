using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary>Port of Protocol.h — only the login/disconnect packets needed for Phase 1.</summary>
public sealed record ConnectAccountRecv(string Account, string Password, uint TickCount, byte[] ClientVersion, byte[] ClientSerial)
{
    /// <summary>Logical layout already synthesised by GameClientFramer: C1,size,head,subh,account[10],password[10],TickCount(4),ClientVersion[5],ClientSerial[16].</summary>
    public static ConnectAccountRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // tras header(2)+head(1)+subh(1)
        var accountRaw = r.ReadBytes(10);
        var passwordRaw = r.ReadBytes(10);
        var tick = r.ReadUInt32();
        var version = r.ReadBytes(5);
        var serial = r.ReadBytes(16);

        var accountBuf = new byte[10];
        var passwordBuf = new byte[10];
        ClientArgumentCipher.Decrypt(accountBuf, accountRaw);
        ClientArgumentCipher.Decrypt(passwordBuf, passwordRaw);

        return new ConnectAccountRecv(
            PacketBuilder.ReadFixedString(accountBuf),
            PacketBuilder.ReadFixedString(passwordBuf),
            tick, version, serial);
    }
}

public sealed record CloseClientRecv(byte Type)
{
    public static CloseClientRecv Parse(byte[] p) => new(p[4]);
}

/// <summary>PMSG_CHARACTER_CREATE_RECV, C1:F3:01 -- name[10]+Class(1). Port of the character creation screen
/// (entirely missing until now: the real client sends it as soon as the user confirms name/class on the
/// selection screen when the account has 0 characters, and without a reply the client waits forever on that
/// screen).</summary>
public sealed record CharacterCreateRecv(string Name, byte Class)
{
    public static CharacterCreateRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C1Sub header: type,size,head,subh = 4 bytes
        var name = r.ReadFixedString(10);
        var cls = r.ReadByte();
        return new CharacterCreateRecv(name, cls);
    }
}

/// <summary>Puerto de PacketArgumentDecrypt (Util.cpp): XOR de 3 bytes aplicado a account/password dentro del login.</summary>
public static class ClientArgumentCipher
{
    private static readonly byte[] XorTable = { 0xFC, 0xCF, 0xAB };

    public static void Decrypt(byte[] outBuff, byte[] inBuff)
    {
        for (int n = 0; n < outBuff.Length && n < inBuff.Length; n++)
        {
            outBuff[n] = (byte)(inBuff[n] ^ XorTable[n % 3]);
        }
    }
}

public static class ClientPacketBuilder
{
    /// <summary>C1:F1:00 — first packet the server sends when accepting the connection.</summary>
    public static byte[] ConnectClientSend(byte result, ushort index, byte[] serverVersion, ushort serverCode)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteBytes(serverVersion, 5);
        w.WriteUInt16(serverCode); // GAMESERVER_EXTRA==1
        return PacketBuilder.BuildC1Sub(0xF1, 0x00, w.ToArray());
    }

    /// <summary>C1:F1:01 — login result (1=ok, 0=invalid account/password, 2=already connected, 3=blocked, 6=invalid version/serial, etc).</summary>
    public static byte[] ConnectAccountSend(byte result)
    {
        return PacketBuilder.BuildC1Sub(0xF1, 0x01, new[] { result });
    }

    /// <summary>C1:F1:02</summary>
    public static byte[] CloseClientSend(byte result)
    {
        return PacketBuilder.BuildC1Sub(0xF1, 0x02, new[] { result });
    }

    /// <summary>PMSG_CHARACTER_LIST_SEND, C1:F3:00 -- the account's character list for the selection screen
    /// (port of DGCharacterListRecv, DSProtocol.cpp:181-365). ClassCode (enables creating MG/DL depending on
    /// the maximum level reached by some character of the account) stays fixed at 2 (no extra class unlocked)
    /// until the m_MGCreateLevel/m_DLCreateLevel balance tables are ported -- it blocks nothing, it just does
    /// not offer creating MG/DL yet (same as a new account in the original). FIXED (reverting an earlier "fix"
    /// based on the WRONG source tree -- see the doc-comment of <see cref="World.Item"/> for the full
    /// explanation of why this repo has two C++ trees and which one is the real one for this project). The real
    /// struct, `Protocol.h` of the correct tree (`Emulator 0.99 (2.1.7)/GameServer`), is:
    /// <c>PMSG_CHARACTER_LIST_SEND { header; BYTE ClassCode; BYTE MoveCnt; BYTE count; }</c> -- WITHOUT
    /// ExtWarehouse (that byte is from a later season with extended warehouse, it does not exist here) -- and
    /// each entry <c>PMSG_CHARACTER_LIST { BYTE slot; char Name[10]; WORD Level; BYTE CtlCode; BYTE
    /// CharSet[13]; }</c> -- the real CharSet is 13 bytes (not 18) and there is NO GuildStatus (no guild
    /// ported, and the original does not send that byte in this build either). The alignment padding byte
    /// between Name[10] and the WORD Level IS real (the struct has no #pragma pack(1) in this region of the
    /// header, so the compiler aligns the WORD to 2 bytes) -- that was the only thing actually needed for the
    /// original bug ("only the first character was visible": with 2+ characters, the lack of this single
    /// padding byte misaligned the following entries).</summary>
    public static byte[] CharacterListSend(byte moveCnt, IReadOnlyList<CharacterListItem> characters)
    {
        var w = new PacketWriter();
        w.WriteByte(2); // ClassCode
        w.WriteByte(moveCnt);
        w.WriteByte((byte)characters.Count);

        foreach (var c in characters)
        {
            // PMSG_CHARACTER_LIST (Protocol.h) does NOT have #pragma pack(1) -- the compiler aligns the WORD
            // Level to 2 bytes, inserting 1 padding byte after slot(1)+Name[10] (offset 11, odd) before Level.
            // Same struct alignment bug as CharacterInfoSend/ DamageSend/LevelUpSend (see the comments there).
            w.WriteByte(c.Slot);
            w.WriteFixedString(c.Name, 10);
            w.WriteByte(0); // alignment padding (does not exist in the struct, it is compiler padding)
            w.WriteUInt16(c.Level);
            w.WriteByte(c.CtlCode);
            w.WriteBytes(c.CharSet, 13);
        }

        return PacketBuilder.BuildC1Sub(0xF3, 0x00, w.ToArray());
    }

    /// <summary>PMSG_CHARACTER_CREATE_SEND, C1:F3:01 -- result(1)+name[10]+slot(1)+level(2)+
    /// Class(1)+equipment[24]+padding(1) (40 body bytes; sizeof = 44 with the 4-byte header, exact port of
    /// Protocol.h). result: 0=failed (invalid/forbidden name/class not available), 1=ok, 2=no space (account
    /// full).</summary>
    public static byte[] CharacterCreateSend(byte result, string name, byte slot, ushort level, byte cls, byte[] equipment)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        w.WriteByte(slot);
        w.WriteUInt16(level);
        w.WriteByte(cls);
        w.WriteBytes(equipment, 24);
        // Tail padding: the struct aligns to 2 (because of the WORD level) and its last field ends at offset
        // 42, so sizeof = 44, not 43. The fields fall in the right place without this, but the declared size
        // does not match the original's.
        w.WriteByte(0);
        return PacketBuilder.BuildC1Sub(0xF3, 0x01, w.ToArray());
    }
}

/// <summary>A row of PMSG_CHARACTER_LIST_SEND already in client format (CharSet already converted from
/// DataServer's compact format -- see PlayerObject.BuildCharSet).</summary>
public sealed record CharacterListItem(byte Slot, string Name, ushort Level, byte CtlCode, byte[] CharSet);

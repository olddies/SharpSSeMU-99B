using MuServer.Shared.Protocol;

namespace MuServer.JoinServer.Protocol;

// All the packets of this protocol use PBMSG_HEAD (C1, 1-byte size, 1-byte head — without subh), unlike
// ConnectServer which uses "Sub"/"Word" variants. Fixed offsets, replicating 1:1 the SDHP_* structs of
// JoinServerProtocol.h (natural 1-byte packing, no padding).

public sealed record ServerInfoRecv(byte Type, ushort ServerPort, string ServerName, ushort ServerCode)
{
    public const int Size = 3 + 1 + 2 + 50 + 2; // 58

    public static ServerInfoRecv Parse(byte[] p) => new(
        Type: p[3],
        ServerPort: PacketBuilder.ReadUInt16LE(p.AsSpan(4, 2)),
        ServerName: PacketBuilder.ReadFixedString(p.AsSpan(6, 50)),
        ServerCode: PacketBuilder.ReadUInt16LE(p.AsSpan(56, 2)));
}

public sealed record ConnectAccountRecv(ushort Index, string Account, string Password, string IpAddress)
{
    public const int Size = 3 + 2 + 11 + 11 + 16; // 43

    public static ConnectAccountRecv Parse(byte[] p) => new(
        Index: PacketBuilder.ReadUInt16LE(p.AsSpan(3, 2)),
        Account: PacketBuilder.ReadFixedString(p.AsSpan(5, 11)),
        Password: PacketBuilder.ReadFixedString(p.AsSpan(16, 11)),
        IpAddress: PacketBuilder.ReadFixedString(p.AsSpan(27, 16)));
}

public sealed record DisconnectAccountRecv(ushort Index, string Account, string IpAddress)
{
    public const int Size = 3 + 2 + 11 + 16; // 32

    public static DisconnectAccountRecv Parse(byte[] p) => new(
        Index: PacketBuilder.ReadUInt16LE(p.AsSpan(3, 2)),
        Account: PacketBuilder.ReadFixedString(p.AsSpan(5, 11)),
        IpAddress: PacketBuilder.ReadFixedString(p.AsSpan(16, 16)));
}

public sealed record AccountLevelRecv(ushort Index, string Account)
{
    public const int Size = 3 + 2 + 11; // 16

    public static AccountLevelRecv Parse(byte[] p) => new(
        Index: PacketBuilder.ReadUInt16LE(p.AsSpan(3, 2)),
        Account: PacketBuilder.ReadFixedString(p.AsSpan(5, 11)));
}

public sealed record AccountLevelSaveRecv(ushort Index, string Account, ushort AccountLevel, uint AccountExpireTime)
{
    public const int Size = 3 + 2 + 11 + 2 + 4; // 22

    public static AccountLevelSaveRecv Parse(byte[] p) => new(
        Index: PacketBuilder.ReadUInt16LE(p.AsSpan(3, 2)),
        Account: PacketBuilder.ReadFixedString(p.AsSpan(5, 11)),
        AccountLevel: PacketBuilder.ReadUInt16LE(p.AsSpan(16, 2)),
        AccountExpireTime: PacketBuilder.ReadUInt32LE(p.AsSpan(18, 4)));
}

public sealed record ServerUserInfoRecv(ushort CurUserCount, ushort MaxUserCount)
{
    public const int Size = 3 + 2 + 2; // 7

    public static ServerUserInfoRecv Parse(byte[] p) => new(
        CurUserCount: PacketBuilder.ReadUInt16LE(p.AsSpan(3, 2)),
        MaxUserCount: PacketBuilder.ReadUInt16LE(p.AsSpan(5, 2)));
}

public sealed record ExternalDisconnectAccountRecv(string Account)
{
    public const int Size = 3 + 11; // 14

    public static ExternalDisconnectAccountRecv Parse(byte[] p) => new(
        Account: PacketBuilder.ReadFixedString(p.AsSpan(3, 11)));
}

/// <summary>Builders de los paquetes JoinServer -> GameServer.</summary>
public static class JoinServerPacketBuilder
{
    public static byte[] ConnectAccountSend(ushort index, string account, string personalCode, byte result, byte blockCode, ushort accountLevel, string accountExpireDate)
    {
        using var ms = new MemoryStream();
        ms.Write(PacketBuilder.WriteUInt16LE(index));
        ms.Write(PacketBuilder.FixedString(account, 11));
        ms.Write(PacketBuilder.FixedString(personalCode, 14));
        ms.WriteByte(result);
        ms.WriteByte(blockCode);
        ms.Write(PacketBuilder.WriteUInt16LE(accountLevel));
        ms.Write(PacketBuilder.FixedString(accountExpireDate, 20));
        return PacketBuilder.BuildC1(0x01, ms.ToArray());
    }

    public static byte[] DisconnectAccountSend(ushort index, string account, byte result)
    {
        using var ms = new MemoryStream();
        ms.Write(PacketBuilder.WriteUInt16LE(index));
        ms.Write(PacketBuilder.FixedString(account, 11));
        ms.WriteByte(result);
        return PacketBuilder.BuildC1(0x02, ms.ToArray());
    }

    public static byte[] AccountLevelSend(ushort index, string account, ushort accountLevel, string accountExpireDate)
    {
        using var ms = new MemoryStream();
        ms.Write(PacketBuilder.WriteUInt16LE(index));
        ms.Write(PacketBuilder.FixedString(account, 11));
        ms.Write(PacketBuilder.WriteUInt16LE(accountLevel));
        ms.Write(PacketBuilder.FixedString(accountExpireDate, 20));
        return PacketBuilder.BuildC1(0x05, ms.ToArray());
    }

    public static byte[] AccountAlreadyConnectedSend(ushort index, string account)
    {
        using var ms = new MemoryStream();
        ms.Write(PacketBuilder.WriteUInt16LE(index));
        ms.Write(PacketBuilder.FixedString(account, 11));
        return PacketBuilder.BuildC1(0x30, ms.ToArray());
    }
}

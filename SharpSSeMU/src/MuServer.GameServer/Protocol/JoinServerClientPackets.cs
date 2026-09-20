using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

// Puerto de JSProtocol.h — lado GameServer (cliente de JoinServer). Espejo exacto de
// MuServer.JoinServer/Protocol/JoinServerPackets.cs, que ya implementa el lado servidor.

public sealed record JoinAccountResultRecv(ushort Index, string Account, string PersonalCode, byte Result, byte BlockCode, ushort AccountLevel, string AccountExpireDate)
{
    public static JoinAccountResultRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var personalCode = r.ReadFixedString(14);
        var result = r.ReadByte();
        var blockCode = r.ReadByte();
        var accountLevel = r.ReadUInt16();
        var expireDate = r.ReadFixedString(20);
        return new JoinAccountResultRecv(index, account, personalCode, result, blockCode, accountLevel, expireDate);
    }
}

public sealed record JoinAccountAlreadyConnectedRecv(ushort Index, string Account)
{
    public static JoinAccountAlreadyConnectedRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new JoinAccountAlreadyConnectedRecv(r.ReadUInt16(), r.ReadFixedString(11));
    }
}

/// <summary>SDHP_DISCONNECT_ACCOUNT_RECV, lado GameServer -- la respuesta de JoinServer a
/// DisconnectAccountSend (JSProtocol.h:21-27; espejo de JoinServerPacketBuilder.DisconnectAccountSend
/// en el lado JoinServer). result: 1=éxito (cuenta liberada en JoinServer/DB), 0=no encontrada/no
/// coincidía el índice o código de servidor.</summary>
public sealed record DisconnectAccountAckRecv(ushort Index, string Account, byte Result)
{
    public static DisconnectAccountAckRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var result = r.ReadByte();
        return new DisconnectAccountAckRecv(index, account, result);
    }
}

public static class JoinServerClientPacketBuilder
{
    public static byte[] ServerInfoSend(byte type, ushort serverPort, string serverName, ushort serverCode)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteUInt16(serverPort);
        w.WriteFixedString(serverName, 50);
        w.WriteUInt16(serverCode);
        return PacketBuilder.BuildC1(0x00, w.ToArray());
    }

    public static byte[] ConnectAccountSend(ushort index, string account, string password, string ipAddress)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(password, 11);
        w.WriteFixedString(ipAddress, 16);
        return PacketBuilder.BuildC1(0x01, w.ToArray());
    }

    public static byte[] DisconnectAccountSend(ushort index, string account, string ipAddress)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(ipAddress, 16);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }

    public static byte[] ServerUserInfoSend(ushort curUsers, ushort maxUsers)
    {
        var w = new PacketWriter();
        w.WriteUInt16(curUsers);
        w.WriteUInt16(maxUsers);
        return PacketBuilder.BuildC1(0x20, w.ToArray());
    }
}

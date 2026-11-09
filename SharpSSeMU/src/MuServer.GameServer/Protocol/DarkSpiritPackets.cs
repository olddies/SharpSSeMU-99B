using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary>The Dark Raven's packets (DarkSpirit.h, Protocol.h). The client orders it with C1:A7 and the server tells the
/// master which mode it is in (C1:A7) and everybody around that it attacks (C1:A8).</summary>
public static class DarkSpiritPacketBuilder
{
    /// <summary>PMSG_DARK_SPIRIT_MODE_SEND, C1:A7: the mode the raven is in and, for the attack-target mode, who it was sent
    /// to (0xFFFF for none). Only the master gets it.</summary>
    public static byte[] ModeSend(byte mode, int target)
    {
        var w = new PacketWriter();
        w.WriteByte(0); // type
        w.WriteByte(mode);
        w.WriteByte((byte)((target >> 8) & 0xFF));
        w.WriteByte((byte)(target & 0xFF));
        return PacketBuilder.BuildC1(0xA7, w.ToArray());
    }

    /// <summary>PMSG_DARK_SPIRIT_ATTACK_SEND, C1:A8: the raven of <paramref name="master"/> attacks <paramref name="target"/>
    /// (<paramref name="action"/> 1 for a blow that spreads to those around). The master and whoever sees them get it.</summary>
    public static byte[] AttackSend(int master, int target, byte action)
    {
        var w = new PacketWriter();
        w.WriteByte(0); // type
        w.WriteByte(action);
        w.WriteByte((byte)((master >> 8) & 0xFF));
        w.WriteByte((byte)(master & 0xFF));
        w.WriteByte((byte)((target >> 8) & 0xFF));
        w.WriteByte((byte)(target & 0xFF));
        return PacketBuilder.BuildC1(0xA8, w.ToArray());
    }
}

/// <summary>PMSG_PET_ITEM_COMMAND_RECV, C1:A7: the order the player gives the raven.</summary>
public sealed record PetCommandRecv(byte Type, byte Command, int Index)
{
    public static PetCommandRecv Parse(byte[] p) =>
        new(p.Length > 3 ? p[3] : (byte)0, p.Length > 4 ? p[4] : (byte)0, p.Length > 6 ? (p[5] << 8) | p[6] : 0xFFFF);
}

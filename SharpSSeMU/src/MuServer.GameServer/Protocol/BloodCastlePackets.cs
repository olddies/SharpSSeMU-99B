using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary> The Blood Castle packets (BloodCastle.h): the answer to an entry request, the event state shown in the
/// castle's window, the final score, plus the two generic ones the event relies on -- the map attribute change that
/// opens and closes its zones (GCMapAttrSend) and the server command that makes the client show a stock message
/// (GCServerCommandSend). </summary>
public static class BloodCastlePacketBuilder
{
    /// <summary>PMSG_BLOOD_CASTLE_ENTER_SEND, C1:9A. 0 = in, 1 = wrong ticket or level, 2 = closed, 3 = level too high,
    /// 4 = level too low, 5 = full, 7 = PK.</summary>
    public static byte[] EnterSend(byte result) => PacketBuilder.BuildC1(0x9A, new[] { result });

    /// <summary>PMSG_BLOOD_CASTLE_STATE_SEND, C1:9B: <paramref name="state"/> 0 started, 1 fighting (monsters of the
    /// stage left), 2 closed, 3 bridge open, 4 gate/statue stage. The original struct is not packed, so it ends
    /// with a pad byte that the length includes.</summary>
    public static byte[] StateSend(byte state, ushort time, ushort maxMonster, ushort curMonster, ushort eventItemOwner, byte eventItemLevel)
    {
        var w = new PacketWriter();
        w.WriteByte(state);
        w.WriteUInt16(time);
        w.WriteUInt16(maxMonster);
        w.WriteUInt16(curMonster);
        w.WriteUInt16(eventItemOwner);
        w.WriteByte(eventItemLevel);
        w.WriteByte(0); // padding of the C++ struct
        return PacketBuilder.BuildC1(0x9B, w.ToArray());
    }

    /// <summary>PMSG_BLOOD_CASTLE_SCORE_SEND, C1:93 (packed): the result window. <paramref name="type"/> 1 for the
    /// winners, 0 for the others.</summary>
    public static byte[] ScoreSend(byte type, string name, uint score, uint rewardExperience, uint rewardMoney)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(0xFF);
        w.WriteFixedString(name, 10);
        w.WriteUInt16(0);
        w.WriteUInt32(score);
        w.WriteUInt32(rewardExperience);
        w.WriteUInt32(rewardMoney);
        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }

    /// <summary>PMSG_MAP_ATTR_SEND, C1:46: sets (<paramref name="flag"/> 0) or clears (1) the attribute bits
    /// <paramref name="attr"/> in each rectangle of the client's copy of the map.</summary>
    public static byte[] MapAttrSend(byte type, byte attr, byte flag, IReadOnlyList<(byte X, byte Y, byte TX, byte TY)> zones)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(attr);
        w.WriteByte(flag);
        w.WriteByte((byte)zones.Count);

        foreach (var (x, y, tx, ty) in zones)
        {
            w.WriteByte(x);
            w.WriteByte(y);
            w.WriteByte(tx);
            w.WriteByte(ty);
        }

        return PacketBuilder.BuildC1(0x46, w.ToArray());
    }

    /// <summary>PMSG_SERVER_COMMAND_SEND, C1:F3:40: makes the client show a stock message or effect.</summary>
    public static byte[] ServerCommandSend(byte type, byte x, byte y) =>
        PacketBuilder.BuildC1Sub(0xF3, 0x40, new[] { type, x, y });
}

using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary> The Chaos Castle packets (ChaosCastle.h). The event state shown in the client's window (C1:9B), the
/// time-count klaxon (C1:92) and the map attribute change (C1:46) are the same packets Blood Castle uses
/// (<see cref="BloodCastlePacketBuilder"/>, <see cref="DevilSquarePacketBuilder"/>); what is its own is the answer to
/// an entry request (C1:AF:01), the request to set the position after being blown away (C1:AF:02) and the score
/// window (C1:93, a different body than Blood Castle's). </summary>
public static class ChaosCastlePacketBuilder
{
    /// <summary>PMSG_CHAOS_CASTLE_ENTER_SEND, C1:AF:01. 0 = in, 1 = wrong ticket, party or state, 2 = closed, 5 = full,
    /// 7 = not enough zen, 8 = PK.</summary>
    public static byte[] EnterSend(byte result) => PacketBuilder.BuildC1Sub(0xAF, 0x01, new[] { result });

    /// <summary>PMSG_CHAOS_CASTLE_SCORE_SEND, C1:93 (packed): the result window. <paramref name="type"/> 1 for the
    /// winner, 0 for the others. The flag byte is 0xFE here, 0xFF in Blood Castle's.</summary>
    public static byte[] ScoreSend(byte type, string name, uint monsterKillCount, uint rewardExperience, uint userKillCount)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(0xFE);
        w.WriteFixedString(name, 10);
        w.WriteUInt16(0);
        w.WriteUInt32(monsterKillCount);
        w.WriteUInt32(rewardExperience);
        w.WriteUInt32(userKillCount);
        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }
}

/// <summary>PMSG_CHAOS_CASTLE_ENTER_RECV, C1:AF:01: the castle the client asked for (the server decides it by the
/// player's level anyway) and the inventory slot of the Armor of Guardsman.</summary>
public sealed record ChaosCastleEnterRecv(byte Level, byte Slot)
{
    public static ChaosCastleEnterRecv Parse(byte[] p) =>
        new(p.Length > 4 ? p[4] : (byte)0, p.Length > 5 ? p[5] : (byte)0);
}

/// <summary>PMSG_CHAOS_CASTLE_POSITION_RECV, C1:AF:02: where the client says the player landed after being blown away.</summary>
public sealed record ChaosCastlePositionRecv(byte X, byte Y)
{
    public static ChaosCastlePositionRecv Parse(byte[] p) =>
        new(p.Length > 4 ? p[4] : (byte)0, p.Length > 5 ? p[5] : (byte)0);
}

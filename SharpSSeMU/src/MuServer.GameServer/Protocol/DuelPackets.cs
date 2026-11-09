using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary> The duel packets (Duel.h). The client asks (C1:AA) a player to duel, answers a request (C1:AC) with yes or
/// no and ends a duel (C1:AB); the server tells the target (C1:AC), answers the asker and announces the start (C1:AA),
/// the end (C1:AB) and the score (C1:AD). </summary>
public static class DuelPacketBuilder
{
    /// <summary>PMSG_DUEL_START_SEND, C1:AA: <paramref name="result"/> 1 the duel starts with <paramref name="other"/>, 0 it did
    /// not happen (refused, expired, impossible).</summary>
    public static byte[] StartSend(byte result, int other, string otherName) =>
        PacketBuilder.BuildC1(0xAA, Body(result, other, otherName));

    /// <summary>PMSG_DUEL_OK_SEND, C1:AC: <paramref name="asker"/> wants to duel the player this goes to.</summary>
    public static byte[] OkSend(int asker, string askerName) =>
        PacketBuilder.BuildC1(0xAC, IndexAndName(asker, askerName));

    /// <summary>PMSG_DUEL_END_SEND, C1:AB: the duel is over. The original names the receiver itself in it.</summary>
    public static byte[] EndSend(int player, string name) =>
        PacketBuilder.BuildC1(0xAB, IndexAndName(player, name));

    /// <summary>PMSG_DUEL_SCORE_SEND, C1:AD: the two duelists and their scores.</summary>
    public static byte[] ScoreSend(int first, int second, byte firstScore, byte secondScore)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((first >> 8) & 0xFF));
        w.WriteByte((byte)(first & 0xFF));
        w.WriteByte((byte)((second >> 8) & 0xFF));
        w.WriteByte((byte)(second & 0xFF));
        w.WriteByte(firstScore);
        w.WriteByte(secondScore);
        return PacketBuilder.BuildC1(0xAD, w.ToArray());
    }

    private static byte[] IndexAndName(int index, string name)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteFixedString(name, 10);
        return w.ToArray();
    }

    private static byte[] Body(byte result, int index, string name)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteBytes(IndexAndName(index, name), 12);
        return w.ToArray();
    }
}

/// <summary>PMSG_DUEL_START_RECV, C1:AA: who the player wants to duel.</summary>
public sealed record DuelStartRecv(int Index, string Name)
{
    public static DuelStartRecv Parse(byte[] p) =>
        new(p.Length > 4 ? (p[3] << 8) | p[4] : 0, ReadName(p, 5));

    internal static string ReadName(byte[] p, int offset)
    {
        if (p.Length <= offset)
        {
            return string.Empty;
        }

        int length = Math.Min(10, p.Length - offset);
        return System.Text.Encoding.ASCII.GetString(p, offset, length).TrimEnd('\0');
    }
}

/// <summary>PMSG_DUEL_OK_RECV, C1:AC: the answer (1 yes, 0 no) to the request of the player <see cref="Index"/>.</summary>
public sealed record DuelOkRecv(byte Flag, int Index, string Name)
{
    public static DuelOkRecv Parse(byte[] p) =>
        new(p.Length > 3 ? p[3] : (byte)0, p.Length > 5 ? (p[4] << 8) | p[5] : 0, DuelStartRecv.ReadName(p, 6));
}

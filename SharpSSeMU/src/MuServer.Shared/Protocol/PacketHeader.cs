namespace MuServer.Shared.Protocol;

/// <summary> Packet header format of the MU Online protocol (identical to the one used by PBMSG_HEAD /
/// PWMSG_HEAD / PSBMSG_HEAD / PSWMSG_HEAD in the original C++ ConnectServer/GameServer). C1 + size(1 byte)
/// + head(1 byte)                => unencrypted "byte size" C2 + size(2 bytes, big-endian) + head(1 byte)
/// => unencrypted "word size" C3 + size(1 byte)              + head(1 byte)                => encrypted "byte
/// size" (XOR/not used by ConnectServer) C4 + size(2 bytes, big-endian) + head(1 byte)                =>
/// encrypted "word size" The "Sub" variants add an extra sub-code byte right after the head (used for example
/// in 0xF4:0x02, 0xF4:0x03, 0xF3:0xEA). </summary>
public static class PacketType
{
    public const byte C1 = 0xC1;
    public const byte C2 = 0xC2;
    public const byte C3 = 0xC3;
    public const byte C4 = 0xC4;
}

public static class PacketBuilder
{
    /// <summary>C1 head [payload] — header with a 1-byte size.</summary>
    public static byte[] BuildC1(byte head, ReadOnlySpan<byte> payload)
    {
        var buff = new byte[3 + payload.Length];
        buff[0] = PacketType.C1;
        buff[1] = (byte)buff.Length;
        buff[2] = head;
        payload.CopyTo(buff.AsSpan(3));
        return buff;
    }

    /// <summary>C1 head subh [payload] — variant with sub-code.</summary>
    public static byte[] BuildC1Sub(byte head, byte subh, ReadOnlySpan<byte> payload)
    {
        var buff = new byte[4 + payload.Length];
        buff[0] = PacketType.C1;
        buff[1] = (byte)buff.Length;
        buff[2] = head;
        buff[3] = subh;
        payload.CopyTo(buff.AsSpan(4));
        return buff;
    }

    /// <summary>C2 head [payload] — header with a 2-byte size (big-endian, high byte first).</summary>
    public static byte[] BuildC2(byte head, ReadOnlySpan<byte> payload)
    {
        var buff = new byte[4 + payload.Length];
        int size = buff.Length;
        buff[0] = PacketType.C2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        payload.CopyTo(buff.AsSpan(4));
        return buff;
    }

    /// <summary>C2 head subh [payload] — variant with sub-code and 2-byte size.</summary>
    public static byte[] BuildC2Sub(byte head, byte subh, ReadOnlySpan<byte> payload)
    {
        var buff = new byte[5 + payload.Length];
        int size = buff.Length;
        buff[0] = PacketType.C2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        buff[4] = subh;
        payload.CopyTo(buff.AsSpan(5));
        return buff;
    }

    /// <summary> Packs a fixed-size string (zero-padded), like the fixed char[] used in the C++ structs (e.g.
    /// ServerAddress[16], ServerName[32]). </summary>
    public static byte[] FixedString(string? value, int length)
    {
        var buff = new byte[length];
        if (!string.IsNullOrEmpty(value))
        {
            var bytes = System.Text.Encoding.Latin1.GetBytes(value);
            var copyLen = Math.Min(bytes.Length, length);
            Array.Copy(bytes, buff, copyLen);
        }
        return buff;
    }

    /// <summary> Reads a fixed C-style char[] (terminated at the first 0x00 byte, or the whole range if there
    /// is no terminator) and decodes it as Latin1/ANSI — just as the client/GameServer receive the account[11],
    /// password[11], ServerName[50], etc. fields. </summary>
    public static string ReadFixedString(ReadOnlySpan<byte> source)
    {
        var nullIndex = source.IndexOf((byte)0);
        var slice = nullIndex >= 0 ? source[..nullIndex] : source;
        return System.Text.Encoding.Latin1.GetString(slice);
    }

    public static ushort ReadUInt16LE(ReadOnlySpan<byte> source) => (ushort)(source[0] | (source[1] << 8));

    public static uint ReadUInt32LE(ReadOnlySpan<byte> source) =>
        (uint)(source[0] | (source[1] << 8) | (source[2] << 16) | (source[3] << 24));

    public static byte[] WriteUInt16LE(ushort value) => new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };

    public static byte[] WriteUInt32LE(uint value) => new[]
    {
        (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF), (byte)((value >> 24) & 0xFF),
    };
}

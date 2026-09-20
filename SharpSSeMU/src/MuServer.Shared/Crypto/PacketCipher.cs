using MuServer.Shared.Localization;
using System.Buffers.Binary;

namespace MuServer.Shared.Crypto;

/// <summary> Port of CPacketManager (PacketManager.cpp) of the original GameServer: the block cipher used ONLY
/// for the packets the original marks with a C3/C4 header (e.g. the C3:F1:01 login). It converts a "logical"
/// packet of 8 bytes per block into an encrypted 11-byte block (and vice versa), using 4-element
/// Modulus/Key/Xor tables loaded from binary files (Hack/Enc2.dat to encrypt, Hack/Dec1.dat to decrypt) — the
/// same files the original package ships and that the client also uses, so they have to be read as they are,
/// they cannot be invented. </summary>
public sealed class PacketCipher
{
    private readonly struct KeyTable
    {
        public readonly uint[] Modulus;
        public readonly uint[] Key;
        public readonly uint[] Xor;

        public KeyTable(uint[] modulus, uint[] key, uint[] xor)
        {
            Modulus = modulus;
            Key = key;
            Xor = xor;
        }
    }

    private static readonly uint[] SaveLoadXor = { 0x3F08A79B, 0xE25CC287, 0x93D27AB9, 0x20DEA7BF };

    private static readonly byte[] XorFilter =
    {
        0xE7, 0x6D, 0x3A, 0x89, 0xBC, 0xB2, 0x9F, 0x73, 0x23, 0xA8, 0xFE, 0xB6, 0x49, 0x5D, 0x39, 0x5D,
        0x8A, 0xCB, 0x63, 0x8D, 0xEA, 0x7D, 0x2B, 0x5F, 0xC3, 0xB1, 0xE9, 0x83, 0x29, 0x51, 0xE8, 0x56,
    };

    private KeyTable _encryption;
    private KeyTable _decryption;

    public static PacketCipher LoadFromFiles(string encryptionKeyPath, string decryptionKeyPath)
    {
        var cipher = new PacketCipher();
        cipher._encryption = LoadKey(encryptionKeyPath);
        cipher._decryption = LoadKey(decryptionKeyPath);
        return cipher;
    }

    private static KeyTable LoadKey(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        ushort header = br.ReadUInt16();
        uint size = br.ReadUInt32();

        if (header != 4370 || size != (6 + 48))
        {
            throw new InvalidDataException(Loc.F("Invalid key file: {0}", path));
        }

        var modulus = new uint[4];
        var key = new uint[4];
        var xor = new uint[4];

        for (int n = 0; n < 4; n++) modulus[n] = br.ReadUInt32() ^ SaveLoadXor[n];
        for (int n = 0; n < 4; n++) key[n] = br.ReadUInt32() ^ SaveLoadXor[n];
        for (int n = 0; n < 4; n++) xor[n] = br.ReadUInt32() ^ SaveLoadXor[n];

        return new KeyTable(modulus, key, xor);
    }

    /// <summary>Encrypts "source" (arbitrary size) in blocks of 8 -> 11 bytes. Returns the full ciphertext.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> source)
    {
        int blockCount = (source.Length + 7) / 8;
        var target = new byte[blockCount * 11];
        Span<byte> block = stackalloc byte[8];

        for (int i = 0; i < blockCount; i++)
        {
            int offset = i * 8;
            int remaining = source.Length - offset;
            int chunkSize = remaining >= 8 ? 8 : remaining;

            block.Clear();
            source.Slice(offset, chunkSize).CopyTo(block);

            EncryptBlock(target.AsSpan(i * 11, 11), block, chunkSize);
        }

        return target;
    }

    /// <summary> Decrypts "source" (a multiple of 11 bytes) returning the reconstructed plaintext, or null if
    /// any block fails the checksum (equivalent to the original's -1, which disconnects the client). </summary>
    public byte[]? Decrypt(ReadOnlySpan<byte> source)
    {
        if (source.Length % 11 != 0)
        {
            return null;
        }

        int blockCount = source.Length / 11;
        var plain = new byte[blockCount * 8];
        int totalSize = 0;

        for (int i = 0; i < blockCount; i++)
        {
            Span<byte> block = plain.AsSpan(i * 8, 8);
            int blockSize = DecryptBlock(block, source.Slice(i * 11, 11));

            if (blockSize < 0)
            {
                return null;
            }

            totalSize += blockSize;
        }

        return plain.AsSpan(0, totalSize).ToArray();
    }

    private void EncryptBlock(Span<byte> target, ReadOnlySpan<byte> source8, int size)
    {
        target.Clear();

        var encBuffer = new uint[4];
        uint encValue = 0;

        for (int n = 0; n < 4; n++)
        {
            ushort word = BinaryPrimitives.ReadUInt16LittleEndian(source8.Slice(n * 2, 2));
            encBuffer[n] = unchecked((uint)(((_encryption.Xor[n] ^ word) ^ encValue) * _encryption.Key[n]) % _encryption.Modulus[n]);
            encValue = (ushort)encBuffer[n];
        }

        for (int n = 0; n < 3; n++)
        {
            encBuffer[n] = (encBuffer[n] ^ _encryption.Xor[n]) ^ (ushort)encBuffer[n + 1];
        }

        int bitPos = 0;

        for (int n = 0; n < 4; n++)
        {
            var bytes4 = BitConverter.GetBytes(encBuffer[n]);
            bitPos = AddBits(target, bitPos, bytes4, 0, 16);
            bitPos = AddBits(target, bitPos, bytes4, 22, 2);
        }

        byte checkSum = 0xF8;

        for (int n = 0; n < 8; n++)
        {
            checkSum ^= source8[n];
        }

        var encValueBytes = new byte[4];
        encValueBytes[0] = (byte)((checkSum ^ (byte)size) ^ 0x3D);
        encValueBytes[1] = checkSum;

        AddBits(target, bitPos, encValueBytes, 0, 16);
    }

    private int DecryptBlock(Span<byte> target, ReadOnlySpan<byte> source11)
    {
        target.Clear();

        var decBuffer = new uint[4];
        int bitPos = 0;

        for (int n = 0; n < 4; n++)
        {
            // Same as the original: two AddBits over the SAME 4-byte buffer (OR-accumulated) -- first the 16
            // bits at position 0, then 2 more bits at bit position 22.
            var buf4 = new byte[4];
            AddBits(buf4, 0, source11, bitPos, 16);
            bitPos += 16;
            AddBits(buf4, 22, source11, bitPos, 2);
            bitPos += 2;
            decBuffer[n] = BitConverter.ToUInt32(buf4);
        }

        for (int n = 2; n >= 0; n--)
        {
            decBuffer[n] = (decBuffer[n] ^ _decryption.Xor[n]) ^ (ushort)decBuffer[n + 1];
        }

        uint value = 0;

        for (int n = 0; n < 4; n++)
        {
            uint decoded = (uint)(((_decryption.Key[n] * decBuffer[n]) % _decryption.Modulus[n]) ^ _decryption.Xor[n]) ^ value;
            BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(n * 2, 2), (ushort)decoded);
            value = (ushort)decBuffer[n];
        }

        var checkBytes = new byte[4];
        AddBits(checkBytes, 0, source11, bitPos, 16);

        byte recoveredSize = (byte)((checkBytes[0] ^ checkBytes[1]) ^ 0x3D);
        byte recoveredChecksum = checkBytes[1];

        byte checkSum = 0xF8;

        for (int n = 0; n < 8; n++)
        {
            checkSum ^= target[n];
        }

        if (checkSum != recoveredChecksum)
        {
            return -1;
        }

        return recoveredSize;
    }

    // ---------------------------------------------------------------- bit packing (puerto de AddBits/Shift/GetByteOfBit)

    private static int AddBits(Span<byte> target, int targetBitPos, ReadOnlySpan<byte> source, int sourceBitPos, int size)
    {
        int sourceBitEnd = sourceBitPos + size;
        int tempSize1 = ByteOfBit(sourceBitEnd - 1) + (1 - ByteOfBit(sourceBitPos));

        Span<byte> temp = stackalloc byte[tempSize1 + 1];
        temp.Clear();

        source.Slice(ByteOfBit(sourceBitPos), tempSize1).CopyTo(temp);

        if ((sourceBitEnd % 8) != 0)
        {
            temp[tempSize1 - 1] &= (byte)(0xFF << (8 - (sourceBitEnd % 8)));
        }

        int shiftLeft = sourceBitPos % 8;
        int shiftRight = targetBitPos % 8;

        Shift(temp[..tempSize1], -shiftLeft);
        Shift(temp[..(tempSize1 + 1)], shiftRight);

        int tempSize2 = (shiftRight <= shiftLeft ? 0 : 1) + tempSize1;

        var targetSlice = target[ByteOfBit(targetBitPos)..];

        for (int n = 0; n < tempSize2; n++)
        {
            targetSlice[n] |= temp[n];
        }

        return targetBitPos + size;
    }

    private static int ByteOfBit(int value) => value >> 3;

    private static void Shift(Span<byte> buff, int shiftSize)
    {
        int size = buff.Length;

        if (shiftSize == 0)
        {
            return;
        }

        if (shiftSize > 0)
        {
            if (size - 1 > 0)
            {
                for (int n = size - 1; n > 0; n--)
                {
                    buff[n] = (byte)((buff[n - 1] << (8 - shiftSize)) | (buff[n] >> shiftSize));
                }
            }

            buff[0] = (byte)(buff[0] >> shiftSize);
        }
        else
        {
            int s = -shiftSize;

            if (size - 1 > 0)
            {
                for (int n = 0; n < size - 1; n++)
                {
                    buff[n] = (byte)((buff[n + 1] >> (8 - s)) | (buff[n] << s));
                }
            }

            buff[size - 1] = (byte)(buff[size - 1] << s);
        }
    }

    // ---------------------------------------------------------------- XorData (port of
    // CPacketManager::XorData) Applied ONLY on receive (both to plain C1/C2 packets and to the already decoded
    // result of a C3/C4 block), never on send — that is how the original GameServer behaves (see DataRecv/
    // DataSend in SocketManager.cpp: XorData lives inside ExtractPacket, called only from the receive path).

    public static void DeobfuscateInPlace(Span<byte> buff, int size, int headerLength)
    {
        int start = size - 1;
        int end = headerLength;

        if (start < end)
        {
            return;
        }

        for (int n = start; n > end; n--)
        {
            buff[n] ^= (byte)(buff[n - 1] ^ XorFilter[n % 32]);
        }
    }

    /// <summary> "Encode" counterpart of <see cref="DeobfuscateInPlace"/> — it does not exist in the original
    /// GameServer (XorData is receive-only there), but it DOES have to exist in the closed real client so that
    /// the server's de-obfuscation reconstructs the original packet. It is obtained by solving the XorData
    /// recurrence: if decoded[n] = wire[n]^wire[n-1]^filter[n%32] (walking n from highest to lowest, using
    /// wire[n-1] NOT yet touched), then wire[n] = plain[n]^wire[n-1]^filter[n%32] walking n from LOWEST to
    /// highest. Used only by the test harness (TestClient) to build authentic C3 packets, since the project
    /// does not include the real client (closed binary). </summary>
    public static void ObfuscateInPlace(Span<byte> buff, int size, int headerLength)
    {
        int start = size - 1;
        int end = headerLength;

        if (start < end)
        {
            return;
        }

        for (int n = end + 1; n <= start; n++)
        {
            buff[n] = (byte)(buff[n] ^ buff[n - 1] ^ XorFilter[n % 32]);
        }
    }
}

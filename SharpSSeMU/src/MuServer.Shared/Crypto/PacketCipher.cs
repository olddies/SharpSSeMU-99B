using MuServer.Shared.Localization;
using System.Buffers.Binary;

namespace MuServer.Shared.Crypto;

/// <summary>
/// Puerto de CPacketManager (PacketManager.cpp) del GameServer original: el cifrado por bloques
/// usado SOLO para los paquetes que el original marca con cabecera C3/C4 (ej. el login C3:F1:01).
/// Convierte un paquete "lógico" de 8 bytes por bloque en un bloque cifrado de 11 bytes (y viceversa),
/// usando tablas Modulus/Key/Xor de 4 elementos cargadas desde archivos binarios (Hack/Enc2.dat para
/// cifrar, Hack/Dec1.dat para descifrar) — los mismos archivos que trae el paquete original y que
/// también usa el cliente, así que hay que leerlos tal cual, no se pueden inventar.
/// </summary>
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

    /// <summary>Cifra "source" (tamaño arbitrario) en bloques de 8 -> 11 bytes. Devuelve el ciphertext completo.</summary>
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

    /// <summary>
    /// Descifra "source" (múltiplo de 11 bytes) devolviendo el plaintext reconstruido, o null si
    /// algún bloque falla el checksum (equivalente al -1 del original, que desconecta al cliente).
    /// </summary>
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
            // Igual que el original: dos AddBits sobre el MISMO buffer de 4 bytes (OR-acumulado) --
            // primero los 16 bits en la posición 0, después 2 bits más en la posición de bit 22.
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

    // ---------------------------------------------------------------- XorData (puerto de CPacketManager::XorData)
    // Se aplica SOLO en recepción (tanto a paquetes C1/C2 planos como al resultado ya decodificado
    // de un bloque C3/C4), nunca al enviar — así se comporta el GameServer original (ver DataRecv/
    // DataSend en SocketManager.cpp: XorData vive dentro de ExtractPacket, llamado únicamente desde
    // el camino de recepción).

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

    /// <summary>
    /// Contraparte "encode" de <see cref="DeobfuscateInPlace"/> — no existe en el GameServer original
    /// (XorData es solo de recepción ahí), pero SÍ tiene que existir en el cliente real cerrado para que
    /// la des-ofuscación del servidor reconstruya el paquete original. Se obtiene despejando la
    /// recurrencia de XorData: si decoded[n] = wire[n]^wire[n-1]^filter[n%32] (recorriendo n de mayor a
    /// menor, usando el wire[n-1] SIN tocar todavía), entonces wire[n] = plain[n]^wire[n-1]^filter[n%32]
    /// recorriendo n de MENOR a mayor. Usado únicamente por el arnés de pruebas (TestClient) para
    /// construir paquetes C3 auténticos, ya que el proyecto no incluye el cliente real (binario cerrado).
    /// </summary>
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

namespace MuServer.Shared.Crypto;

/// <summary> Port of HackCheck.cpp (EncryptData/DecryptData) of the original GameServer: stream cipher applied
/// to EVERYTHING entering/leaving the real client's socket (not to ConnectServer/JoinServer/ DataServer, which
/// do not use it). It is an XOR + subtraction with two 1-byte keys derived from the "ServerSerial" configured
/// in ServerInfo. </summary>
public sealed class GameStreamCipher
{
    private readonly byte _key1;
    private readonly byte _key2;
    private readonly byte _mhpKey1;
    private readonly byte _mhpKey2;

    public GameStreamCipher(byte key1, byte key2, byte mhpKey1 = 0, byte mhpKey2 = 0)
    {
        _key1 = key1;
        _key2 = key2;
        _mhpKey1 = mhpKey1;
        _mhpKey2 = mhpKey2;
    }

    /// <summary> Derives the EncDecKey1/2 keys like InitHackCheck(): from a fixed "CustomerName" ("SSE",
    /// hardcoded in the original) XORed/subtracted with the .ini's ServerSerial. </summary>
    public static GameStreamCipher FromServerSerial(byte[] serverSerial, byte mhpKey1 = 0, byte mhpKey2 = 0)
    {
        const string customerName = "SSE"; // 32 bytes reservados en el original, solo 3 usados + ceros
        var nameBytes = new byte[32];
        System.Text.Encoding.ASCII.GetBytes(customerName).CopyTo(nameBytes, 0);

        ushort encDecKey = 0;

        for (int n = 0; n < nameBytes.Length; n++)
        {
            byte serial = serverSerial.Length > 0 ? serverSerial[n % serverSerial.Length] : (byte)0;
            encDecKey += (byte)(nameBytes[n] ^ serial);
            encDecKey ^= (byte)(nameBytes[n] - serial);
        }

        byte key1 = (byte)(0xBB + (byte)(encDecKey & 0xFF));
        byte key2 = (byte)(0xCC + (byte)((encDecKey >> 8) & 0xFF));

        byte effMhp1 = mhpKey1;
        byte effMhp2 = mhpKey2;

        if (mhpKey1 != 0 || mhpKey2 != 0)
        {
            ushort mhpEncDecKey = 0;

            foreach (var b in nameBytes)
            {
                mhpEncDecKey += b;
            }

            effMhp1 = (byte)(mhpKey1 + (byte)(mhpEncDecKey & 0xFF));
            effMhp2 = (byte)(mhpKey2 + (byte)((mhpEncDecKey >> 8) & 0xFF));
        }

        return new GameStreamCipher(key1, key2, effMhp1, effMhp2);
    }

    public void Encrypt(Span<byte> data)
    {
        for (int n = 0; n < data.Length; n++)
        {
            data[n] = (byte)((data[n] + (byte)(_key2 * _key1)) ^ _key1);
        }

        if (_mhpKey1 != 0 || _mhpKey2 != 0)
        {
            MhpEncrypt(data);
        }
    }

    public void Decrypt(Span<byte> data)
    {
        if (_mhpKey1 != 0 || _mhpKey2 != 0)
        {
            MhpDecrypt(data);
        }

        for (int n = 0; n < data.Length; n++)
        {
            data[n] = (byte)((data[n] ^ _key1) - (byte)(_key2 * _key1));
        }
    }

    private void MhpEncrypt(Span<byte> data)
    {
        for (int n = 0; n < data.Length; n++)
        {
            data[n] = (byte)((data[n] + (byte)(_mhpKey2 * _mhpKey1)) ^ _mhpKey1);
        }
    }

    private void MhpDecrypt(Span<byte> data)
    {
        for (int n = 0; n < data.Length; n++)
        {
            data[n] = (byte)((data[n] ^ _mhpKey1) - (byte)(_mhpKey2 * _mhpKey1));
        }
    }
}

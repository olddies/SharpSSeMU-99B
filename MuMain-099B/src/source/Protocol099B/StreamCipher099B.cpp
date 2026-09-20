#include "StreamCipher099B.h"

namespace Mu099B
{

namespace
{

/// The original reserves 32 bytes for the client name and only uses the first three; the rest stays at zero and
/// takes part in the derivation all the same.
constexpr size_t CustomerNameSize = 32;
constexpr char CustomerName[] = "SSE";

}  // namespace

StreamCipher::StreamCipher(uint8_t key1, uint8_t key2, uint8_t mhpKey1, uint8_t mhpKey2)
    : _key1(key1), _key2(key2), _mhpKey1(mhpKey1), _mhpKey2(mhpKey2)
{
}

StreamCipher StreamCipher::FromServerSerial(const uint8_t* serverSerial, size_t serialLength,
                                            uint8_t mhpKey1, uint8_t mhpKey2)
{
    uint8_t name[CustomerNameSize] = {};
    for (size_t i = 0; i < sizeof(CustomerName) - 1 && i < CustomerNameSize; ++i)
    {
        name[i] = static_cast<uint8_t>(CustomerName[i]);
    }

    // The serial field is 17 bytes padded with zeros, and the derivation cycles over all 17 -- not over the
    // characters that were written in the .ini. Materialising it here is what keeps the text length from
    // leaking into the key.
    uint8_t serialField[ServerSerialFieldSize] = {};
    for (size_t i = 0; i < serialLength && i < ServerSerialFieldSize; ++i)
    {
        serialField[i] = serverSerial[i];
    }

    uint16_t encDecKey = 0;
    for (size_t n = 0; n < CustomerNameSize; ++n)
    {
        const uint8_t serial = serialField[n % ServerSerialFieldSize];
        encDecKey = static_cast<uint16_t>(encDecKey + static_cast<uint8_t>(name[n] ^ serial));
        encDecKey = static_cast<uint16_t>(encDecKey ^ static_cast<uint8_t>(name[n] - serial));
    }

    const auto key1 = static_cast<uint8_t>(0xBB + static_cast<uint8_t>(encDecKey & 0xFF));
    const auto key2 = static_cast<uint8_t>(0xCC + static_cast<uint8_t>((encDecKey >> 8) & 0xFF));

    uint8_t effectiveMhp1 = mhpKey1;
    uint8_t effectiveMhp2 = mhpKey2;

    if (mhpKey1 != 0 || mhpKey2 != 0)
    {
        uint16_t mhpEncDecKey = 0;
        for (size_t n = 0; n < CustomerNameSize; ++n)
        {
            mhpEncDecKey = static_cast<uint16_t>(mhpEncDecKey + name[n]);
        }

        effectiveMhp1 = static_cast<uint8_t>(mhpKey1 + static_cast<uint8_t>(mhpEncDecKey & 0xFF));
        effectiveMhp2 = static_cast<uint8_t>(mhpKey2 + static_cast<uint8_t>((mhpEncDecKey >> 8) & 0xFF));
    }

    return StreamCipher(key1, key2, effectiveMhp1, effectiveMhp2);
}

void StreamCipher::Encrypt(uint8_t* data, size_t length) const
{
    const auto product = static_cast<uint8_t>(_key2 * _key1);
    for (size_t n = 0; n < length; ++n)
    {
        data[n] = static_cast<uint8_t>(static_cast<uint8_t>(data[n] + product) ^ _key1);
    }

    if (_mhpKey1 != 0 || _mhpKey2 != 0)
    {
        MhpEncrypt(data, length);
    }
}

void StreamCipher::Decrypt(uint8_t* data, size_t length) const
{
    if (_mhpKey1 != 0 || _mhpKey2 != 0)
    {
        MhpDecrypt(data, length);
    }

    const auto product = static_cast<uint8_t>(_key2 * _key1);
    for (size_t n = 0; n < length; ++n)
    {
        data[n] = static_cast<uint8_t>(static_cast<uint8_t>(data[n] ^ _key1) - product);
    }
}

void StreamCipher::MhpEncrypt(uint8_t* data, size_t length) const
{
    const auto product = static_cast<uint8_t>(_mhpKey2 * _mhpKey1);
    for (size_t n = 0; n < length; ++n)
    {
        data[n] = static_cast<uint8_t>(static_cast<uint8_t>(data[n] + product) ^ _mhpKey1);
    }
}

void StreamCipher::MhpDecrypt(uint8_t* data, size_t length) const
{
    const auto product = static_cast<uint8_t>(_mhpKey2 * _mhpKey1);
    for (size_t n = 0; n < length; ++n)
    {
        data[n] = static_cast<uint8_t>(static_cast<uint8_t>(data[n] ^ _mhpKey1) - product);
    }
}

}  // namespace Mu099B

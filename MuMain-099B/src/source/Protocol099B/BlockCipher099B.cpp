#include "BlockCipher099B.h"

#include <algorithm>
#include <cstring>
#include <fstream>

namespace Mu099B
{

namespace
{

/// The tables are stored XOR-ed with these four constants.
constexpr uint32_t SaveLoadXor[4] = {0x3F08A79Bu, 0xE25CC287u, 0x93D27AB9u, 0x20DEA7BFu};

/// Header of the key files: fixed mark and total size.
constexpr uint16_t KeyFileMarker = 4370;
constexpr uint32_t KeyFileSize = 6 + 48;

constexpr uint8_t XorFilter[32] = {
    0xE7, 0x6D, 0x3A, 0x89, 0xBC, 0xB2, 0x9F, 0x73, 0x23, 0xA8, 0xFE, 0xB6, 0x49, 0x5D, 0x39, 0x5D,
    0x8A, 0xCB, 0x63, 0x8D, 0xEA, 0x7D, 0x2B, 0x5F, 0xC3, 0xB1, 0xE9, 0x83, 0x29, 0x51, 0xE8, 0x56,
};

int ByteOfBit(int value)
{
    return value >> 3;
}

/// Bit shift over a whole buffer. Positive shifts right, negative left (same criterion as the original's
/// Shift).
void Shift(uint8_t* buff, size_t size, int shiftSize)
{
    if (shiftSize == 0 || size == 0)
    {
        return;
    }

    if (shiftSize > 0)
    {
        for (size_t n = size - 1; n > 0; --n)
        {
            buff[n] = static_cast<uint8_t>((buff[n - 1] << (8 - shiftSize)) | (buff[n] >> shiftSize));
        }
        buff[0] = static_cast<uint8_t>(buff[0] >> shiftSize);
    }
    else
    {
        const int s = -shiftSize;
        for (size_t n = 0; n + 1 < size; ++n)
        {
            buff[n] = static_cast<uint8_t>((buff[n + 1] >> (8 - s)) | (buff[n] << s));
        }
        buff[size - 1] = static_cast<uint8_t>(buff[size - 1] << s);
    }
}

/// Copies `size` bits of `source` (from `sourceBitPos`) into `target` starting at `targetBitPos`, OR-ing over
/// whatever is already there. Returns the new bit position in the destination.
int AddBits(uint8_t* target, int targetBitPos, const uint8_t* source, int sourceBitPos, int size)
{
    const int sourceBitEnd = sourceBitPos + size;
    const int tempSize1 = ByteOfBit(sourceBitEnd - 1) + (1 - ByteOfBit(sourceBitPos));

    uint8_t temp[8] = {};
    std::memcpy(temp, source + ByteOfBit(sourceBitPos), static_cast<size_t>(tempSize1));

    if ((sourceBitEnd % 8) != 0)
    {
        temp[tempSize1 - 1] &= static_cast<uint8_t>(0xFF << (8 - (sourceBitEnd % 8)));
    }

    const int shiftLeft = sourceBitPos % 8;
    const int shiftRight = targetBitPos % 8;

    Shift(temp, static_cast<size_t>(tempSize1), -shiftLeft);
    Shift(temp, static_cast<size_t>(tempSize1) + 1, shiftRight);

    const int tempSize2 = (shiftRight <= shiftLeft ? 0 : 1) + tempSize1;
    uint8_t* targetSlice = target + ByteOfBit(targetBitPos);

    for (int n = 0; n < tempSize2; ++n)
    {
        targetSlice[n] |= temp[n];
    }

    return targetBitPos + size;
}

uint16_t ReadUInt16LE(const uint8_t* p)
{
    return static_cast<uint16_t>(p[0] | (p[1] << 8));
}

void WriteUInt16LE(uint8_t* p, uint16_t value)
{
    p[0] = static_cast<uint8_t>(value & 0xFF);
    p[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
}

void WriteUInt32LE(uint8_t* p, uint32_t value)
{
    p[0] = static_cast<uint8_t>(value & 0xFF);
    p[1] = static_cast<uint8_t>((value >> 8) & 0xFF);
    p[2] = static_cast<uint8_t>((value >> 16) & 0xFF);
    p[3] = static_cast<uint8_t>((value >> 24) & 0xFF);
}

uint32_t ReadUInt32LE(const uint8_t* p)
{
    return static_cast<uint32_t>(p[0]) | (static_cast<uint32_t>(p[1]) << 8) |
           (static_cast<uint32_t>(p[2]) << 16) | (static_cast<uint32_t>(p[3]) << 24);
}

}  // namespace

BlockCipher::BlockCipher(const KeyTable& encryption, const KeyTable& decryption)
    : _encryption(encryption), _decryption(decryption)
{
}

std::optional<BlockCipher::KeyTable> BlockCipher::LoadKey(const std::string& path)
{
    std::ifstream file(path, std::ios::binary);
    if (!file)
    {
        return std::nullopt;
    }

    uint8_t raw[6 + 48] = {};
    file.read(reinterpret_cast<char*>(raw), sizeof(raw));
    if (file.gcount() != static_cast<std::streamsize>(sizeof(raw)))
    {
        return std::nullopt;
    }

    const uint16_t marker = ReadUInt16LE(raw);
    const uint32_t size = ReadUInt32LE(raw + 2);
    if (marker != KeyFileMarker || size != KeyFileSize)
    {
        return std::nullopt;
    }

    KeyTable table{};
    for (int n = 0; n < 4; ++n)
    {
        table.Modulus[n] = ReadUInt32LE(raw + 6 + n * 4) ^ SaveLoadXor[n];
        table.Key[n] = ReadUInt32LE(raw + 6 + 16 + n * 4) ^ SaveLoadXor[n];
        table.Xor[n] = ReadUInt32LE(raw + 6 + 32 + n * 4) ^ SaveLoadXor[n];
    }
    return table;
}

std::optional<BlockCipher> BlockCipher::LoadFromFiles(const std::string& encryptionKeyPath,
                                                      const std::string& decryptionKeyPath)
{
    const auto encryption = LoadKey(encryptionKeyPath);
    const auto decryption = LoadKey(decryptionKeyPath);
    if (!encryption || !decryption)
    {
        return std::nullopt;
    }
    return BlockCipher(*encryption, *decryption);
}

std::vector<uint8_t> BlockCipher::Encrypt(const uint8_t* source, size_t length) const
{
    const size_t blockCount = (length + PlainBlockSize - 1) / PlainBlockSize;
    std::vector<uint8_t> target(blockCount * CipherBlockSize, 0);

    for (size_t i = 0; i < blockCount; ++i)
    {
        const size_t offset = i * PlainBlockSize;
        const size_t chunkSize = std::min(length - offset, PlainBlockSize);

        uint8_t block[PlainBlockSize] = {};
        std::memcpy(block, source + offset, chunkSize);

        EncryptBlock(target.data() + i * CipherBlockSize, block, chunkSize);
    }

    return target;
}

std::optional<std::vector<uint8_t>> BlockCipher::Decrypt(const uint8_t* source, size_t length) const
{
    if (length % CipherBlockSize != 0)
    {
        return std::nullopt;
    }

    const size_t blockCount = length / CipherBlockSize;
    std::vector<uint8_t> plain(blockCount * PlainBlockSize, 0);
    size_t totalSize = 0;

    for (size_t i = 0; i < blockCount; ++i)
    {
        const int blockSize = DecryptBlock(plain.data() + i * PlainBlockSize,
                                           source + i * CipherBlockSize);
        if (blockSize < 0)
        {
            return std::nullopt;
        }
        totalSize += static_cast<size_t>(blockSize);
    }

    plain.resize(totalSize);
    return plain;
}

void BlockCipher::EncryptBlock(uint8_t* target, const uint8_t* source8, size_t size) const
{
    std::memset(target, 0, CipherBlockSize);

    uint32_t encBuffer[4] = {};
    uint32_t encValue = 0;

    for (int n = 0; n < 4; ++n)
    {
        const uint16_t word = ReadUInt16LE(source8 + n * 2);
        encBuffer[n] = ((((_encryption.Xor[n] ^ word) ^ encValue) * _encryption.Key[n]) %
                        _encryption.Modulus[n]);
        encValue = static_cast<uint16_t>(encBuffer[n]);
    }

    for (int n = 0; n < 3; ++n)
    {
        encBuffer[n] = (encBuffer[n] ^ _encryption.Xor[n]) ^ static_cast<uint16_t>(encBuffer[n + 1]);
    }

    int bitPos = 0;
    for (int n = 0; n < 4; ++n)
    {
        uint8_t bytes4[4];
        WriteUInt32LE(bytes4, encBuffer[n]);
        bitPos = AddBits(target, bitPos, bytes4, 0, 16);
        bitPos = AddBits(target, bitPos, bytes4, 22, 2);
    }

    uint8_t checkSum = 0xF8;
    for (int n = 0; n < 8; ++n)
    {
        checkSum ^= source8[n];
    }

    uint8_t encValueBytes[4] = {};
    encValueBytes[0] = static_cast<uint8_t>((checkSum ^ static_cast<uint8_t>(size)) ^ 0x3D);
    encValueBytes[1] = checkSum;

    AddBits(target, bitPos, encValueBytes, 0, 16);
}

int BlockCipher::DecryptBlock(uint8_t* target, const uint8_t* source11) const
{
    std::memset(target, 0, PlainBlockSize);

    uint32_t decBuffer[4] = {};
    int bitPos = 0;

    for (int n = 0; n < 4; ++n)
    {
        // Same as the original: two AddBits over the SAME 4-byte buffer (accumulated OR) -- first 16 bits at
        // position 0, then 2 more bits at bit position 22.
        uint8_t buf4[4] = {};
        AddBits(buf4, 0, source11, bitPos, 16);
        bitPos += 16;
        AddBits(buf4, 22, source11, bitPos, 2);
        bitPos += 2;
        decBuffer[n] = ReadUInt32LE(buf4);
    }

    for (int n = 2; n >= 0; --n)
    {
        decBuffer[n] = (decBuffer[n] ^ _decryption.Xor[n]) ^ static_cast<uint16_t>(decBuffer[n + 1]);
    }

    uint32_t value = 0;
    for (int n = 0; n < 4; ++n)
    {
        const uint32_t decoded =
            (((_decryption.Key[n] * decBuffer[n]) % _decryption.Modulus[n]) ^ _decryption.Xor[n]) ^ value;
        WriteUInt16LE(target + n * 2, static_cast<uint16_t>(decoded));
        value = static_cast<uint16_t>(decBuffer[n]);
    }

    uint8_t checkBytes[4] = {};
    AddBits(checkBytes, 0, source11, bitPos, 16);

    const auto recoveredSize = static_cast<uint8_t>((checkBytes[0] ^ checkBytes[1]) ^ 0x3D);
    const uint8_t recoveredChecksum = checkBytes[1];

    uint8_t checkSum = 0xF8;
    for (int n = 0; n < 8; ++n)
    {
        checkSum ^= target[n];
    }

    if (checkSum != recoveredChecksum)
    {
        return -1;
    }

    return recoveredSize;
}

void DeobfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength)
{
    if (size == 0 || size - 1 < headerLength)
    {
        return;
    }

    for (size_t n = size - 1; n > headerLength; --n)
    {
        buffer[n] ^= static_cast<uint8_t>(buffer[n - 1] ^ XorFilter[n % 32]);
    }
}

void ObfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength)
{
    if (size == 0 || size - 1 < headerLength)
    {
        return;
    }

    for (size_t n = headerLength + 1; n <= size - 1; ++n)
    {
        buffer[n] = static_cast<uint8_t>(buffer[n] ^ buffer[n - 1] ^ XorFilter[n % 32]);
    }
}

}  // namespace Mu099B

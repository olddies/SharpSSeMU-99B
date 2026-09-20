// Block cipher (SimpleModulus) and XorData obfuscation of the 0.99B protocol. Port of the emulator's
// CPacketManager (PacketManager.cpp). It is applied only to packets marked C3/C4; each logical 8-byte block
// becomes 11 wire bytes and vice versa, with 4-element Modulus/Key/Xor tables loaded from binary files. On the
// client side the tables come from Data/Enc1.dat (encrypt) and Data/Dec2.dat (decrypt) -- the same files the
// client already ships. They are read as they are: they cannot be invented or derived. Verified note: these
// tables match, byte for byte, OpenMU's default SimpleModulus keys. The incompatibility with the C# library is
// not here, but in XorData (different table and chaining in the opposite direction) and in the stream cipher,
// which does not exist in OpenMU.

#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace Mu099B
{

/// Logical block and wire block of the block cipher.
inline constexpr size_t PlainBlockSize = 8;
inline constexpr size_t CipherBlockSize = 11;

class BlockCipher
{
public:
    struct KeyTable
    {
        uint32_t Modulus[4];
        uint32_t Key[4];
        uint32_t Xor[4];
    };

    BlockCipher(const KeyTable& encryption, const KeyTable& decryption);

    /// Loads the two tables from their files. Returns `nullopt` if either does not exist or lacks the expected
    /// header, instead of carrying on with garbage.
    static std::optional<BlockCipher> LoadFromFiles(const std::string& encryptionKeyPath,
                                                    const std::string& decryptionKeyPath);

    /// Reads a single table (useful for tests and diagnostics).
    static std::optional<KeyTable> LoadKey(const std::string& path);

    /// Cifra un largo arbitrario en bloques de 8 -> 11 bytes.
    std::vector<uint8_t> Encrypt(const uint8_t* source, size_t length) const;

    /// Decrypts a multiple of 11 bytes. Returns `nullopt` if any block fails the checksum, which in the
    /// original is what disconnects the client.
    std::optional<std::vector<uint8_t>> Decrypt(const uint8_t* source, size_t length) const;

private:
    void EncryptBlock(uint8_t* target, const uint8_t* source8, size_t size) const;
    int DecryptBlock(uint8_t* target, const uint8_t* source11) const;

    KeyTable _encryption;
    KeyTable _decryption;
};

/// XorData: chained XOR de-obfuscation over the bytes following the header. In the emulator it lives inside
/// ExtractPacket, so it is applied ONLY on receive -- both to plain C1/C2 and to the already decrypted result
/// of a C3/C4 block.
void DeobfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength);

/// Send-side counterpart. It does not exist on the server (there XorData is receive-only), but the client has
/// to apply it so that the server's de-obfuscation reconstructs the original packet.
void ObfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength);

}  // namespace Mu099B

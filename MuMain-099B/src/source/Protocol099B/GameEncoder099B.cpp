#include "GameEncoder099B.h"

#include <cstring>

namespace Mu099B
{

GameEncoder::GameEncoder(const StreamCipher& streamCipher, const BlockCipher& blockCipher)
    : _streamCipher(streamCipher), _blockCipher(blockCipher)
{
}

std::vector<uint8_t> GameEncoder::Encode(const uint8_t* logicalPacket, size_t length)
{
    if (length < 3)
    {
        return {};
    }

    const uint8_t type = logicalPacket[0];
    const bool encrypted = (type == 0xC3 || type == 0xC4);
    const size_t headerLen = (type == 0xC1 || type == 0xC3) ? 2 : 3;

    std::vector<uint8_t> logical(logicalPacket, logicalPacket + length);

    // The XorData chaining starts by taking the byte BEFORE the first one it obfuscates, which is precisely the
    // size byte. The server, when rebuilding the logical packet, recomputes it from what it decrypted; if the
    // one we send does not match, every byte of the body is de-obfuscated differently and the packet arrives
    // corrupt with nothing to warn about it. It is normalised here instead of trusting that the caller left it
    // right.
    if (headerLen == 2)
    {
        logical[1] = static_cast<uint8_t>(length);
    }
    else
    {
        logical[1] = static_cast<uint8_t>((length >> 8) & 0xFF);
        logical[2] = static_cast<uint8_t>(length & 0xFF);
    }

    // XorData over the body: it is what the server undoes on receive.
    ObfuscateInPlace(logical.data(), logical.size(), headerLen);

    std::vector<uint8_t> wire;

    if (!encrypted)
    {
        wire = std::move(logical);
    }
    else
    {
        // The serial number takes the place of the size byte: the original swaps that byte before encrypting
        // and restores it afterwards. Here a new buffer is built, so there is nothing to restore.
        std::vector<uint8_t> plain;
        plain.reserve(length - 1);
        plain.push_back(_sendSerial++);
        plain.insert(plain.end(), logical.begin() + headerLen, logical.end());

        const auto cipherText = _blockCipher.Encrypt(plain.data(), plain.size());

        wire.reserve(headerLen + cipherText.size());
        wire.push_back(type);
        if (headerLen == 2)
        {
            wire.push_back(static_cast<uint8_t>(headerLen + cipherText.size()));
        }
        else
        {
            const auto total = static_cast<uint16_t>(headerLen + cipherText.size());
            wire.push_back(static_cast<uint8_t>((total >> 8) & 0xFF));
            wire.push_back(static_cast<uint8_t>(total & 0xFF));
        }
        wire.insert(wire.end(), cipherText.begin(), cipherText.end());
    }

    _streamCipher.Encrypt(wire.data(), wire.size());
    return wire;
}

}  // namespace Mu099B

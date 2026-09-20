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

    // El encadenado de XorData arranca tomando el byte ANTERIOR al primero que
    // ofusca, que es justamente el de tamaño. El servidor, al reconstruir el
    // paquete lógico, lo recalcula a partir de lo que descifró; si el que
    // mandamos no coincide, cada byte del cuerpo se des-ofusca distinto y el
    // paquete llega corrupto sin que nada avise. Se normaliza acá en vez de
    // confiar en que quien llama lo dejó bien.
    if (headerLen == 2)
    {
        logical[1] = static_cast<uint8_t>(length);
    }
    else
    {
        logical[1] = static_cast<uint8_t>((length >> 8) & 0xFF);
        logical[2] = static_cast<uint8_t>(length & 0xFF);
    }

    // XorData sobre el cuerpo: es lo que el servidor deshace al recibir.
    ObfuscateInPlace(logical.data(), logical.size(), headerLen);

    std::vector<uint8_t> wire;

    if (!encrypted)
    {
        wire = std::move(logical);
    }
    else
    {
        // El número de serie ocupa el lugar del byte de tamaño: el original
        // intercambia ese byte antes de cifrar y lo restaura después. Acá se
        // arma un buffer nuevo, así que no hay nada que restaurar.
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

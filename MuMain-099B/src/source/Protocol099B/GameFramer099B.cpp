#include "GameFramer099B.h"

#include <cstring>

namespace Mu099B
{

namespace
{

/// Minimum needed to read type + size + head of a C1/C3 packet.
constexpr size_t MinPacketSize = 3;

std::string ToHex(uint8_t value)
{
    constexpr char digits[] = "0123456789ABCDEF";
    return std::string("0x") + digits[(value >> 4) & 0xF] + digits[value & 0xF];
}

}  // namespace

GameFramer::GameFramer(const StreamCipher& streamCipher, const BlockCipher& blockCipher,
                       size_t maxPacketSize)
    : _streamCipher(streamCipher), _blockCipher(blockCipher), _buffer(maxPacketSize, 0)
{
}

bool GameFramer::Fail(std::string reason)
{
    _lastError = std::move(reason);
    return false;
}

bool GameFramer::Feed(const uint8_t* data, size_t length, std::vector<DecodedPacket>& packets)
{
    if (_size + length > _buffer.size())
    {
        return Fail("desbordamiento del buffer: llegaron más datos de los permitidos "
                    "sin completar un paquete válido");
    }

    // Decrypt ONLY what just arrived: the stream cipher has no state, so what was already accumulated was
    // decrypted at the time and passing it through again would break it.
    std::memcpy(_buffer.data() + _size, data, length);
    _streamCipher.Decrypt(_buffer.data() + _size, length);
    _size += length;

    size_t count = 0;

    while (_size - count >= MinPacketSize)
    {
        const uint8_t type = _buffer[count];
        size_t size = 0;
        size_t headerLen = 0;

        if (type == 0xC1 || type == 0xC3)
        {
            size = _buffer[count + 1];
            headerLen = 2;
        }
        else if (type == 0xC2 || type == 0xC4)
        {
            if (_size - count < 4)
            {
                break;
            }
            size = static_cast<size_t>((_buffer[count + 1] << 8) | _buffer[count + 2]);
            headerLen = 3;
        }
        else
        {
            return Fail("cabecera de protocolo inválida: " + ToHex(type));
        }

        if (size < MinPacketSize || size > _buffer.size())
        {
            return Fail("tamaño de paquete inválido: " + std::to_string(size));
        }

        if (count + size > _size)
        {
            break;  // incomplete packet: wait for more data
        }

        if (type == 0xC1 || type == 0xC2)
        {
            DecodedPacket packet;
            packet.Data.assign(_buffer.begin() + count, _buffer.begin() + count + size);
            packets.push_back(std::move(packet));
        }
        else
        {
            // The block cipher starts right after type+size, with no extra bytes in between. The first
            // decrypted byte is the serial number, not the real head.
            const auto decrypted =
                _blockCipher.Decrypt(_buffer.data() + count + headerLen, size - headerLen);

            if (!decrypted || decrypted->empty())
            {
                return Fail("checksum de bloque cifrado inválido (paquete corrupto)");
            }

            const size_t payloadLen = decrypted->size() - 1;
            const size_t logicalSize = headerLen + payloadLen;

            DecodedPacket packet;
            packet.Serial = (*decrypted)[0];
            packet.WasEncrypted = true;
            packet.Data.resize(logicalSize);
            packet.Data[0] = static_cast<uint8_t>(type == 0xC3 ? 0xC1 : 0xC2);

            if (headerLen == 2)
            {
                packet.Data[1] = static_cast<uint8_t>(logicalSize);
            }
            else
            {
                packet.Data[1] = static_cast<uint8_t>((logicalSize >> 8) & 0xFF);
                packet.Data[2] = static_cast<uint8_t>(logicalSize & 0xFF);
            }

            std::memcpy(packet.Data.data() + headerLen, decrypted->data() + 1, payloadLen);
            packets.push_back(std::move(packet));
        }

        count += size;
    }

    const size_t remaining = _size - count;
    if (remaining > 0 && count > 0)
    {
        std::memmove(_buffer.data(), _buffer.data() + count, remaining);
    }
    _size = remaining;

    return true;
}

}  // namespace Mu099B

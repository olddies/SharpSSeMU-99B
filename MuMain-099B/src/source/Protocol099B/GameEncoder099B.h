// Outgoing encoder of the 0.99B GameServer socket. Counterpart of GameFramer099B: takes an already built
// logical packet (type, size, head and body) and returns the exact bytes that go to the socket. The order is
// the inverse of receive, and the obfuscation is asymmetric: 1. XorData over the body. The server undoes it on
// receive (ExtractPacket), so here it DOES have to be applied -- the opposite of the server -> client
// direction, where nobody obfuscates. 2. For C3/C4, block cipher of [serial][body], with the serial number
// taking the place of the logical packet's size byte. 3. Stream cipher over the whole packet, header included.
// The serial number is kept by the encoder because the original increments it per connection on every encrypted
// send.

#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

#include "BlockCipher099B.h"
#include "StreamCipher099B.h"

namespace Mu099B
{

class GameEncoder
{
public:
    GameEncoder(const StreamCipher& streamCipher, const BlockCipher& blockCipher);

    /// Encodes a logical packet. If its type byte is 0xC3 or 0xC4 it is block-encrypted; with 0xC1 or 0xC2 it
    /// goes in the clear. In both cases the body is obfuscated and the stream is encrypted.
    std::vector<uint8_t> Encode(const uint8_t* logicalPacket, size_t length);

    /// Serial that will be used on the next encrypted send (for diagnostics).
    uint8_t NextSerial() const { return _sendSerial; }

private:
    StreamCipher _streamCipher;
    BlockCipher _blockCipher;
    uint8_t _sendSerial = 0;
};

}  // namespace Mu099B

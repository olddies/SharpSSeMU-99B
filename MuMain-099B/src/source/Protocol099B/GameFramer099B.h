// Packet splitter of the 0.99B GameServer socket. Port of CSocketManager::OnRecv + DataRecv +
// CPacketManager::ExtractPacket. Order matters and is the reason this framing cannot be delegated to the C#
// library: 1. The bytes just read from the socket are decrypted in place with the stream cipher -- which also
// covers the type and size bytes, so without this step there is no way to know where a packet starts and ends.
// 2. Packets are separated by header: C1/C3 carry the size in one byte, C2/C4 in two (big-endian). 3. C3/C4 are
// block-decrypted (11 -> 8) and rebuilt as a logical C1/C2 packet. The first decrypted byte is a serial number,
// not the real head. The result is a logical packet ready for the client's dispatcher. WATCH OUT FOR XorData:
// it is asymmetric and is not applied here. In the emulator XorData lives inside ExtractPacket, that is, only
// on the RECEIVE path, and sending (DataSend) never applies it. Therefore: client -> server: the client
// obfuscates, the server de-obfuscates; server -> client: nobody applies it. This framer is the CLIENT's, so it
// receives unobfuscated. The outgoing obfuscation lives on the send path. Porting the server's framer as is --
// which does de-obfuscate -- leaves each packet's body scrambled.

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "BlockCipher099B.h"
#include "StreamCipher099B.h"

namespace Mu099B
{

/// Un paquete ya listo para despachar.
struct DecodedPacket
{
    std::vector<uint8_t> Data;
    /// Serial number that came in the encrypted block, or -1 if the packet arrived in the clear.
    int Serial = -1;
    bool WasEncrypted = false;
};

class GameFramer
{
public:
    GameFramer(const StreamCipher& streamCipher, const BlockCipher& blockCipher,
               size_t maxPacketSize = 8192);

    /// Feeds raw socket bytes and extracts whatever complete packets there are. Returns false on a protocol
    /// violation (unknown header, impossible size, invalid checksum); in that case the stream is out of sync
    /// and the connection has to be cut, just as the server does.
    bool Feed(const uint8_t* data, size_t length, std::vector<DecodedPacket>& packets);

    /// Description of the last failure, for the log.
    const std::string& LastError() const { return _lastError; }

    /// Bytes of an incomplete packet left waiting for more data.
    size_t Pending() const { return _size; }

private:
    bool Fail(std::string reason);

    StreamCipher _streamCipher;
    BlockCipher _blockCipher;
    std::vector<uint8_t> _buffer;
    size_t _size = 0;
    std::string _lastError;
};

}  // namespace Mu099B

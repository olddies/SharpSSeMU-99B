// Native GameServer socket for the 0.99B protocol. Replaces the C# library's connection for the game socket.
// This is not a style preference: the 0.99B stream cipher also covers the type and size bytes, so no framer
// working on the encrypted stream can split packets. Framing has to happen after decrypting, and that forces
// handling the socket here. Usage model: non-blocking socket, polled once per frame with Poll(). No threads or
// callbacks -- the client already drains its packet queue in the main loop, so adding a thread would only add
// synchronisation for no gain.

#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "GameEncoder099B.h"
#include "GameFramer099B.h"

namespace Mu099B
{

class GameSocket
{
public:
    GameSocket();
    ~GameSocket();

    GameSocket(const GameSocket&) = delete;
    GameSocket& operator=(const GameSocket&) = delete;

    /// Connects, loading the keys from disk. `serverSerial` is the one in the server's .ini;
    /// `encryptionKeyPath` and `decryptionKeyPath` are the client's Enc1.dat and Dec2.dat. Returns false if it
    /// could not connect or if any key is missing -- it never carries on with half a set of keys.
    bool Connect(const std::string& host, uint16_t port, const std::string& serverSerial,
                 const std::string& encryptionKeyPath, const std::string& decryptionKeyPath);

    /// Connects with the cipher layers already built. It is the path the local loop test uses and serves to
    /// reuse already loaded keys.
    bool Connect(const std::string& host, uint16_t port, const StreamCipher& streamCipher,
                 const BlockCipher& blockCipher);

    /// Encodes and sends a logical packet. Returns false if the connection was cut or the packet could not be
    /// written whole.
    bool Send(const uint8_t* logicalPacket, size_t length);

    /// Reads whatever has arrived and appends the complete packets to `packets`. Returns false if the
    /// connection closed or the stream got out of sync; in that case it has to be closed, just as the server
    /// does.
    bool Poll(std::vector<DecodedPacket>& packets);

    void Close();
    bool IsConnected() const;

    /// Reason for the last failure, for the log.
    const std::string& LastError() const { return _lastError; }

private:
    bool Fail(std::string reason);

    struct Impl;
    std::unique_ptr<Impl> _impl;
    std::string _lastError;
};

}  // namespace Mu099B

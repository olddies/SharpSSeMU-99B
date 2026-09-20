// Stream cipher of the 0.99B GameServer socket. Port of the emulator's HackCheck.cpp (InitHackCheck /
// EncryptData / DecryptData). It is applied to EVERYTHING entering and leaving the GameServer socket,
// **including the type and size bytes of each packet**, so it runs before any attempt to split packets. Neither
// the ConnectServer nor the JoinServer nor the DataServer use it. It is the reason the C# library's framer is
// no use for this socket: it would see the headers encrypted and could not segment. That is why this
// connection's framing has to be native. Stateless: each byte is transformed independently, so it does not
// matter in how many pieces the stream arrives.

#pragma once

#include <cstddef>
#include <cstdint>

namespace Mu099B
{

/// The ServerSerial is a 17-byte field: the 16 configured characters plus the terminator. The server pads to
/// that length before deriving the key, so the null byte is key material like any other. Deriving over the 16
/// characters gives a different key1 and the whole stream comes out wrong -- it happened, and no offline test
/// caught it because client and server have to match the *original*, not each other.
constexpr size_t ServerSerialFieldSize = 17;

class StreamCipher
{
public:
    /// Already derived keys. `mhpKey1`/`mhpKey2` at zero disable the second layer (the usual case: the .ini
    /// files carry ServerEncDecKey1/2 = 0).
    StreamCipher(uint8_t key1, uint8_t key2, uint8_t mhpKey1 = 0, uint8_t mhpKey2 = 0);

    /// Derives the keys like InitHackCheck(): from the fixed client name "SSE" (32 bytes, the rest zero)
    /// combined with the .ini's ServerSerial. `serialLength` is how many characters the configured serial has,
    /// not the field length: the function copies it into a buffer of ServerSerialFieldSize bytes and pads with
    /// zeros, just like the server. Passing more characters than the field discards them.
    static StreamCipher FromServerSerial(const uint8_t* serverSerial, size_t serialLength,
                                         uint8_t mhpKey1 = 0, uint8_t mhpKey2 = 0);

    void Encrypt(uint8_t* data, size_t length) const;
    void Decrypt(uint8_t* data, size_t length) const;

    uint8_t Key1() const { return _key1; }
    uint8_t Key2() const { return _key2; }

private:
    void MhpEncrypt(uint8_t* data, size_t length) const;
    void MhpDecrypt(uint8_t* data, size_t length) const;

    uint8_t _key1;
    uint8_t _key2;
    uint8_t _mhpKey1;
    uint8_t _mhpKey2;
};

}  // namespace Mu099B

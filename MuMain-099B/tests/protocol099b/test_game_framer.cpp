// Packet splitter of the GameServer socket: here the three layers (stream cipher, block cipher and XorData)
// come together in the right order. The cases build the stream as the server would emit it and check that the
// framer recovers the exact logical packet. The point that matters most is that the stream can be split
// anywhere: the socket delivers whatever it wants, not whole packets.

#include <doctest.h>

#include <vector>

#include "Protocol099B/BlockCipher099B.h"
#include "Protocol099B/GameFramer099B.h"
#include "Protocol099B/StreamCipher099B.h"

namespace
{

// Data/Enc1.dat y Data/Dec2.dat (ver test_block_cipher.cpp).
constexpr Mu099B::BlockCipher::KeyTable Enc1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00005BC1u, 0x00002E87u, 0x00004D68u, 0x0000354Fu},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
};
// Data/Hack/Enc2.dat -- the SERVER encrypts with this.
constexpr Mu099B::BlockCipher::KeyTable Enc2 = {
    {0x00011E6Eu, 0x0001ADA5u, 0x0001821Bu, 0x00029C32u},
    {0x00003371u, 0x00004A5Cu, 0x00008A9Au, 0x00007393u},
    {0x0000F234u, 0x0000FB99u, 0x00008A2Eu, 0x0000FC57u},
};
constexpr Mu099B::BlockCipher::KeyTable Dec2 = {
    {0x00011E6Eu, 0x0001ADA5u, 0x0001821Bu, 0x00029C32u},
    {0x00004673u, 0x00007684u, 0x0000607Du, 0x00002B85u},
    {0x0000F234u, 0x0000FB99u, 0x00008A2Eu, 0x0000FC57u},
};

const std::string kSerial = "SharpSSeMU99B-v1";

Mu099B::StreamCipher MakeStream()
{
    return Mu099B::StreamCipher::FromServerSerial(
        reinterpret_cast<const uint8_t*>(kSerial.data()), kSerial.size());
}

Mu099B::GameFramer MakeFramer()
{
    return Mu099B::GameFramer(MakeStream(), Mu099B::BlockCipher(Enc1, Dec2));
}

/// Builds a plain C1 as it leaves the server: WITHOUT XorData (sending never applies it) and passed through the
/// stream cipher.
std::vector<uint8_t> MakePlainPacket(uint8_t head, const std::vector<uint8_t>& body)
{
    std::vector<uint8_t> packet;
    packet.push_back(0xC1);
    packet.push_back(static_cast<uint8_t>(3 + body.size()));
    packet.push_back(head);
    packet.insert(packet.end(), body.begin(), body.end());

    MakeStream().Encrypt(packet.data(), packet.size());
    return packet;
}

}  // namespace

TEST_CASE("Un paquete plano se recupera entero")
{
    auto framer = MakeFramer();
    const std::vector<uint8_t> body = {0x11, 0x22, 0x33, 0x44};
    const auto wire = MakePlainPacket(0xF3, body);

    std::vector<Mu099B::DecodedPacket> packets;
    REQUIRE(framer.Feed(wire.data(), wire.size(), packets));
    REQUIRE(packets.size() == 1);

    const auto& p = packets[0];
    CHECK_FALSE(p.WasEncrypted);
    CHECK(p.Serial == -1);
    REQUIRE(p.Data.size() == 3 + body.size());
    CHECK(p.Data[0] == 0xC1);
    CHECK(p.Data[2] == 0xF3);
    CHECK(std::vector<uint8_t>(p.Data.begin() + 3, p.Data.end()) == body);
}

TEST_CASE("El flujo se puede partir en cualquier lado")
{
    // The socket delivers whatever it wants: half a header, three packets together, a loose byte. The framer
    // has to withstand all of that.
    auto framer = MakeFramer();
    const auto wire = MakePlainPacket(0xF3, {0xAA, 0xBB, 0xCC, 0xDD, 0xEE});

    std::vector<Mu099B::DecodedPacket> packets;
    for (size_t i = 0; i < wire.size(); ++i)
    {
        REQUIRE(framer.Feed(wire.data() + i, 1, packets));
    }

    REQUIRE(packets.size() == 1);
    CHECK(packets[0].Data[2] == 0xF3);
    CHECK(framer.Pending() == 0);
}

TEST_CASE("Varios paquetes en una sola entrega salen todos")
{
    auto framer = MakeFramer();
    std::vector<uint8_t> wire;
    for (uint8_t head : {0xF1, 0xF3, 0x0E})
    {
        const auto one = MakePlainPacket(head, {head, 0x01});
        wire.insert(wire.end(), one.begin(), one.end());
    }

    std::vector<Mu099B::DecodedPacket> packets;
    REQUIRE(framer.Feed(wire.data(), wire.size(), packets));

    REQUIRE(packets.size() == 3);
    CHECK(packets[0].Data[2] == 0xF1);
    CHECK(packets[1].Data[2] == 0xF3);
    CHECK(packets[2].Data[2] == 0x0E);
}

TEST_CASE("Un paquete a medias queda esperando, sin producir nada")
{
    auto framer = MakeFramer();
    const auto wire = MakePlainPacket(0xF3, {0x01, 0x02, 0x03, 0x04, 0x05, 0x06});

    std::vector<Mu099B::DecodedPacket> packets;
    REQUIRE(framer.Feed(wire.data(), wire.size() - 2, packets));
    CHECK(packets.empty());
    CHECK(framer.Pending() == wire.size() - 2);

    REQUIRE(framer.Feed(wire.data() + wire.size() - 2, 2, packets));
    CHECK(packets.size() == 1);
    CHECK(framer.Pending() == 0);
}

TEST_CASE("Una cabecera desconocida corta la conexión en vez de resincronizar sola")
{
    // If the type is not C1/C2/C3/C4 the stream is already out of sync: carrying on guessing only delivers
    // garbage as if it were protocol.
    auto framer = MakeFramer();
    std::vector<uint8_t> garbage = {0x11, 0x22, 0x33, 0x44};
    MakeStream().Encrypt(garbage.data(), garbage.size());

    std::vector<Mu099B::DecodedPacket> packets;
    CHECK_FALSE(framer.Feed(garbage.data(), garbage.size(), packets));
    CHECK_FALSE(framer.LastError().empty());
}

TEST_CASE("Un paquete cifrado por bloques se reconstruye como paquete lógico")
{
    // Full path of a C3 as the server emits it: it is encrypted with Enc2.dat (server role) and the client
    // decrypts it with Dec2.dat. The first byte of the block is the serial number, not the head.
    constexpr uint8_t serial = 0x2A;
    const std::vector<uint8_t> logicalBody = {0xF1, 0x00, 0x01, 0x02, 0x03};

    // El servidor no ofusca al enviar: el bloque lleva [serial][cuerpo] tal cual.
    std::vector<uint8_t> toEncrypt;
    toEncrypt.push_back(serial);
    toEncrypt.insert(toEncrypt.end(), logicalBody.begin(), logicalBody.end());

    const Mu099B::BlockCipher serverCipher(Enc2, Dec2);
    const auto encrypted = serverCipher.Encrypt(toEncrypt.data(), toEncrypt.size());

    std::vector<uint8_t> wire;
    wire.push_back(0xC3);
    wire.push_back(static_cast<uint8_t>(2 + encrypted.size()));
    wire.insert(wire.end(), encrypted.begin(), encrypted.end());
    MakeStream().Encrypt(wire.data(), wire.size());

    auto framer = MakeFramer();
    std::vector<Mu099B::DecodedPacket> packets;
    REQUIRE(framer.Feed(wire.data(), wire.size(), packets));
    REQUIRE(packets.size() == 1);

    const auto& p = packets[0];
    CHECK(p.WasEncrypted);
    CHECK(p.Serial == serial);
    // It is rebuilt as a logical C1, with the original body already de-obfuscated.
    CHECK(p.Data[0] == 0xC1);
    REQUIRE(p.Data.size() >= 2 + logicalBody.size());
    CHECK(std::vector<uint8_t>(p.Data.begin() + 2, p.Data.begin() + 2 + logicalBody.size()) ==
          logicalBody);
}

TEST_CASE("Un bloque cifrado con la tabla equivocada se rechaza")
{
    // The checksum is the only thing that tells a legitimate packet from noise; with tables that do not match
    // it has to fail, not deliver garbage.
    const std::vector<uint8_t> payload = {0x2A, 0xF1, 0x00, 0x01};
    const Mu099B::BlockCipher wrongCipher(Enc1, Dec2);  // Enc1 es del cliente, no del servidor
    const auto encrypted = wrongCipher.Encrypt(payload.data(), payload.size());

    std::vector<uint8_t> wire;
    wire.push_back(0xC3);
    wire.push_back(static_cast<uint8_t>(2 + encrypted.size()));
    wire.insert(wire.end(), encrypted.begin(), encrypted.end());
    MakeStream().Encrypt(wire.data(), wire.size());

    auto framer = MakeFramer();
    std::vector<Mu099B::DecodedPacket> packets;
    CHECK_FALSE(framer.Feed(wire.data(), wire.size(), packets));
    CHECK_FALSE(framer.LastError().empty());
}

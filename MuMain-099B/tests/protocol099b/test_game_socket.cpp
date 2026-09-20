// GameServer socket against a fake server in the same process. The test starts a loopback listener and speaks
// the real protocol from the other side: it encrypts with the server's tables, does not obfuscate on send, and
// de-obfuscates what it receives. It is the only test that exercises the full order of the layers together with
// socket handling -- which the tests of the individual pieces cannot cover.

#include <doctest.h>

#include <chrono>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
using TestSocket = SOCKET;
#define TEST_INVALID_SOCKET INVALID_SOCKET
#define TestCloseSocket closesocket
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>
using TestSocket = int;
#define TEST_INVALID_SOCKET (-1)
#define TestCloseSocket close
#endif

#include "Protocol099B/BlockCipher099B.h"
#include "Protocol099B/GameSocket099B.h"
#include "Protocol099B/StreamCipher099B.h"

namespace
{

constexpr Mu099B::BlockCipher::KeyTable Enc1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00005BC1u, 0x00002E87u, 0x00004D68u, 0x0000354Fu},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
};
constexpr Mu099B::BlockCipher::KeyTable Dec1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00007B38u, 0x000007FFu, 0x0000DEB3u, 0x000027C7u},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
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

/// Listener de loopback en un puerto que elige el sistema.
class FakeServer
{
public:
    FakeServer()
    {
#ifdef _WIN32
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
#endif
        _listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        REQUIRE(_listener != TEST_INVALID_SOCKET);

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        address.sin_port = 0;  // que elija el sistema

        REQUIRE(bind(_listener, reinterpret_cast<sockaddr*>(&address), sizeof(address)) == 0);
        REQUIRE(listen(_listener, 1) == 0);

        sockaddr_in bound{};
        socklen_t boundLength = sizeof(bound);
        REQUIRE(getsockname(_listener, reinterpret_cast<sockaddr*>(&bound), &boundLength) == 0);
        _port = ntohs(bound.sin_port);
    }

    ~FakeServer()
    {
        if (_client != TEST_INVALID_SOCKET)
        {
            TestCloseSocket(_client);
        }
        if (_listener != TEST_INVALID_SOCKET)
        {
            TestCloseSocket(_listener);
        }
    }

    uint16_t Port() const { return _port; }

    void Accept() { _client = accept(_listener, nullptr, nullptr); }
    bool Accepted() const { return _client != TEST_INVALID_SOCKET; }

    /// Sends a plain C1 as the server would: without XorData, stream cipher only.
    void SendPlain(uint8_t head, const std::vector<uint8_t>& body)
    {
        std::vector<uint8_t> packet;
        packet.push_back(0xC1);
        packet.push_back(static_cast<uint8_t>(3 + body.size()));
        packet.push_back(head);
        packet.insert(packet.end(), body.begin(), body.end());

        MakeStream().Encrypt(packet.data(), packet.size());
        Write(packet);
    }

    /// Reads what arrived from the client and decodes it with the server's rules.
    std::vector<uint8_t> ReadOnePacket()
    {
        std::vector<uint8_t> raw(512);
        const int received =
            recv(_client, reinterpret_cast<char*>(raw.data()), static_cast<int>(raw.size()), 0);
        REQUIRE(received > 0);
        raw.resize(static_cast<size_t>(received));

        MakeStream().Decrypt(raw.data(), raw.size());

        const uint8_t type = raw[0];
        const size_t headerLen = (type == 0xC1 || type == 0xC3) ? 2 : 3;

        if (type == 0xC1 || type == 0xC2)
        {
            Mu099B::DeobfuscateInPlace(raw.data(), raw.size(), headerLen);
            return raw;
        }

        const Mu099B::BlockCipher serverCipher(Enc1, Dec1);
        const auto plain = serverCipher.Decrypt(raw.data() + headerLen, raw.size() - headerLen);
        REQUIRE(plain.has_value());

        const size_t payloadLen = plain->size() - 1;
        const size_t logicalSize = headerLen + payloadLen;
        std::vector<uint8_t> logical(logicalSize);
        logical[0] = 0xC1;
        logical[1] = static_cast<uint8_t>(logicalSize);
        std::memcpy(logical.data() + headerLen, plain->data() + 1, payloadLen);
        Mu099B::DeobfuscateInPlace(logical.data(), logicalSize, headerLen);
        return logical;
    }

private:
    void Write(const std::vector<uint8_t>& bytes)
    {
        size_t sent = 0;
        while (sent < bytes.size())
        {
            const int written = send(_client, reinterpret_cast<const char*>(bytes.data() + sent),
                                     static_cast<int>(bytes.size() - sent), 0);
            REQUIRE(written > 0);
            sent += static_cast<size_t>(written);
        }
    }

    TestSocket _listener = TEST_INVALID_SOCKET;
    TestSocket _client = TEST_INVALID_SOCKET;
    uint16_t _port = 0;
};

/// Polls the socket until something arrives or patience runs out. The socket is non-blocking on purpose, so
/// Poll may return with nothing.
bool PollUntil(Mu099B::GameSocket& socket, std::vector<Mu099B::DecodedPacket>& packets,
               size_t expected)
{
    for (int attempt = 0; attempt < 200 && packets.size() < expected; ++attempt)
    {
        if (!socket.Poll(packets))
        {
            return false;
        }
        if (packets.size() < expected)
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(5));
        }
    }
    return packets.size() >= expected;
}

}  // namespace

TEST_CASE("El cliente conecta y recibe un paquete del servidor")
{
    FakeServer server;
    Mu099B::GameSocket client;

    std::thread accepter([&] { server.Accept(); });
    const bool connected =
        client.Connect("127.0.0.1", server.Port(), MakeStream(), Mu099B::BlockCipher(Enc1, Dec2));
    accepter.join();

    REQUIRE(connected);
    REQUIRE(server.Accepted());

    const std::vector<uint8_t> body = {0x11, 0x22, 0x33};
    server.SendPlain(0xF3, body);

    std::vector<Mu099B::DecodedPacket> packets;
    REQUIRE(PollUntil(client, packets, 1));
    REQUIRE(packets.size() == 1);
    CHECK(packets[0].Data[2] == 0xF3);
    CHECK(std::vector<uint8_t>(packets[0].Data.begin() + 3, packets[0].Data.end()) == body);
}

TEST_CASE("Lo que manda el cliente le llega bien al servidor")
{
    FakeServer server;
    Mu099B::GameSocket client;

    std::thread accepter([&] { server.Accept(); });
    REQUIRE(client.Connect("127.0.0.1", server.Port(), MakeStream(),
                           Mu099B::BlockCipher(Enc1, Dec2)));
    accepter.join();

    const std::vector<uint8_t> logical = {0xC1, 0x07, 0xF3, 0x01, 0xAA, 0xBB, 0xCC};
    REQUIRE(client.Send(logical.data(), logical.size()));

    CHECK(server.ReadOnePacket() == logical);
}

TEST_CASE("Un paquete cifrado del cliente le llega bien al servidor")
{
    FakeServer server;
    Mu099B::GameSocket client;

    std::thread accepter([&] { server.Accept(); });
    REQUIRE(client.Connect("127.0.0.1", server.Port(), MakeStream(),
                           Mu099B::BlockCipher(Enc1, Dec2)));
    accepter.join();

    // Login C3:F1:01, el primer paquete cifrado real del protocolo.
    const std::vector<uint8_t> logical = {0xC3, 0x08, 0xF1, 0x01, 0x61, 0x62, 0x63, 0x00};
    REQUIRE(client.Send(logical.data(), logical.size()));

    const auto received = server.ReadOnePacket();
    REQUIRE(received.size() == logical.size());
    CHECK(received[0] == 0xC1);  // the server rebuilds it as a logical C1
    CHECK(std::vector<uint8_t>(received.begin() + 2, received.end()) ==
          std::vector<uint8_t>(logical.begin() + 2, logical.end()));
}

TEST_CASE("Conectar a un puerto cerrado falla con un motivo, no cuelga")
{
    Mu099B::GameSocket client;
    // Port 1 on loopback: nobody is listening there.
    CHECK_FALSE(client.Connect("127.0.0.1", 1, MakeStream(), Mu099B::BlockCipher(Enc1, Dec2)));
    CHECK_FALSE(client.LastError().empty());
    CHECK_FALSE(client.IsConnected());
}

TEST_CASE("Mandar sin conexión falla en vez de escribir a la nada")
{
    Mu099B::GameSocket client;
    const std::vector<uint8_t> logical = {0xC1, 0x04, 0xF3, 0x00};
    CHECK_FALSE(client.Send(logical.data(), logical.size()));
    CHECK_FALSE(client.LastError().empty());
}

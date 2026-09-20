// Verifies byte by byte the packets the client sends to the ConnectServer in the 0.99B dialect, and the layout
// of those it receives. The expected values are not invented: they are what SharpSSeMU's real ConnectServer
// accepts, confirmed by running the full stack (SharpSSeMU/tests/full_chain_e2e_test.py, which negotiates the
// server list and IP:port resolution against the four servers actually running).

#include <doctest.h>

#include <cstring>

#include "Protocol099B/Wire099B.h"

namespace
{

const Mu099B::BYTE* Bytes(const void* packet)
{
    return reinterpret_cast<const Mu099B::BYTE*>(packet);
}

}  // namespace

TEST_CASE("La petición de lista de servidores es C1:F4:02 de 4 bytes")
{
    const auto packet = Mu099B::BuildServerListRequest();
    const Mu099B::BYTE* raw = Bytes(&packet);

    REQUIRE(sizeof(packet) == 4);
    CHECK(raw[0] == 0xC1);  // plain, 1-byte size
    CHECK(raw[1] == 0x04);  // the size includes the whole header
    CHECK(raw[2] == 0xF4);
    CHECK(raw[3] == 0x02);
}

TEST_CASE("La petición de datos de servidor es C1:F4:03 con el código en 1 byte")
{
    // The ServerCode travels as a BYTE in this build, not a WORD: the server's struct (PMSG_SERVER_INFO_RECV)
    // declares `BYTE ServerCode` and the whole packet is 5 bytes.
    const auto packet = Mu099B::BuildServerInfoRequest(7);
    const Mu099B::BYTE* raw = Bytes(&packet);

    REQUIRE(sizeof(packet) == 5);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 0x05);
    CHECK(raw[2] == 0xF4);
    CHECK(raw[3] == 0x03);
    CHECK(raw[4] == 7);
}

TEST_CASE("Los encabezados de 2 bytes llevan el tamaño en big-endian")
{
    // SET_NUMBERHB/SET_NUMBERLB of the original: high byte first. Writing it the other way round gives packets
    // the server discards for impossible size.
    Mu099B::PWMSG_HEAD header{};
    Mu099B::SetHeader(header, 0x12, 0x0102);

    CHECK(header.type == 0xC2);
    CHECK(header.size[0] == 0x01);
    CHECK(header.size[1] == 0x02);
    CHECK(header.head == 0x12);
}

TEST_CASE("La variante cifrada solo cambia el byte de tipo")
{
    Mu099B::PBMSG_HEAD plain{};
    Mu099B::SetHeader(plain, 0x1C, 8, false);
    CHECK(plain.type == 0xC1);

    Mu099B::PBMSG_HEAD encrypted{};
    Mu099B::SetHeader(encrypted, 0x1C, 8, true);
    CHECK(encrypted.type == 0xC3);
    CHECK(encrypted.size == plain.size);
    CHECK(encrypted.head == plain.head);
}

TEST_CASE("La lista de servidores que llega tiene contador de 1 byte y filas de 4")
{
    // The previous dialect carried the counter in 2 bytes and the rows started at offset 7. In 0.99B the
    // counter takes 1 byte (offset 5) and the rows start at 6: reading it with the old layout shifts everything
    // by one byte.
    CHECK(sizeof(Mu099B::PMSG_SERVER_LIST_SEND) == 6);
    CHECK(offsetof(Mu099B::PMSG_SERVER_LIST_SEND, count) == 5);

    CHECK(sizeof(Mu099B::PMSG_SERVER_LIST) == 4);
    CHECK(offsetof(Mu099B::PMSG_SERVER_LIST, ServerCode) == 0);
    CHECK(offsetof(Mu099B::PMSG_SERVER_LIST, UserTotal) == 2);
    CHECK(offsetof(Mu099B::PMSG_SERVER_LIST, type) == 3);
}

TEST_CASE("La respuesta con IP y puerto pone el puerto en el offset 20")
{
    // PMSG_SERVER_INFO_SEND no usa #pragma pack(1): tras el encabezado de 4 y
    // char ServerAddress[16] queda en el offset 20, ya alineado para el WORD.
    CHECK(sizeof(Mu099B::PMSG_SERVER_INFO_SEND) == 22);
    CHECK(offsetof(Mu099B::PMSG_SERVER_INFO_SEND, ServerAddress) == 4);
    CHECK(offsetof(Mu099B::PMSG_SERVER_INFO_SEND, ServerPort) == 20);
}

TEST_CASE("Se puede leer una lista de servidores armada como la manda el servidor")
{
    // One server, code 0, 25% load: exactly the shape SharpSSeMU returns when full_chain asks for the list.
    Mu099B::BYTE wire[6 + 4] = {};
    auto* envelope = reinterpret_cast<Mu099B::PMSG_SERVER_LIST_SEND*>(wire);
    Mu099B::SetHeader(envelope->header, 0xF4, 0x02, static_cast<Mu099B::WORD>(sizeof(wire)));
    envelope->count = 1;

    auto* row = reinterpret_cast<Mu099B::PMSG_SERVER_LIST*>(wire + sizeof(*envelope));
    row->ServerCode = 0;
    row->UserTotal = 25;
    row->type = 1;

    CHECK(wire[0] == 0xC2);
    CHECK(wire[1] == 0x00);
    CHECK(wire[2] == 0x0A);  // 10 bytes en total
    CHECK(wire[3] == 0xF4);
    CHECK(wire[4] == 0x02);
    CHECK(wire[5] == 1);     // contador, un solo byte

    const auto* readBack =
        reinterpret_cast<const Mu099B::PMSG_SERVER_LIST*>(wire + sizeof(*envelope));
    CHECK(readBack->ServerCode == 0);
    CHECK(readBack->UserTotal == 25);
}

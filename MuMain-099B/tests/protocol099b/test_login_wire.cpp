// Paquetes de login y de gestión de personajes.
//
// El caso central va hasta el final: se arma el login, se codifica con las tres
// capas de salida y se decodifica con las reglas del servidor, incluido el XOR
// de argumentos. Es lo más cerca que se puede estar de un login real sin
// levantar el servidor.

#include <doctest.h>

#include <cstring>
#include <string>
#include <vector>

#include "Protocol099B/BlockCipher099B.h"
#include "Protocol099B/GameEncoder099B.h"
#include "Protocol099B/StreamCipher099B.h"
#include "Protocol099B/Wire099B.h"

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

const std::string kSerial = "SharpSSeMU99B-v1";

// Lo que espera el servidor de pruebas: ServerVersion "10200" y el mismo serial.
constexpr Mu099B::BYTE kVersion[5] = {'1', '0', '2', '0', '0'};

Mu099B::StreamCipher MakeStream()
{
    return Mu099B::StreamCipher::FromServerSerial(
        reinterpret_cast<const uint8_t*>(kSerial.data()), kSerial.size());
}

/// Decodifica como el emulador al recibir del cliente.
std::vector<uint8_t> ServerDecode(std::vector<uint8_t> wire)
{
    MakeStream().Decrypt(wire.data(), wire.size());

    const uint8_t type = wire[0];
    const size_t headerLen = (type == 0xC1 || type == 0xC3) ? 2 : 3;

    if (type == 0xC1 || type == 0xC2)
    {
        Mu099B::DeobfuscateInPlace(wire.data(), wire.size(), headerLen);
        return wire;
    }

    const Mu099B::BlockCipher serverCipher(Enc1, Dec1);
    const auto plain = serverCipher.Decrypt(wire.data() + headerLen, wire.size() - headerLen);
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

std::string ReadFixed(const uint8_t* field, size_t length)
{
    std::string text(reinterpret_cast<const char*>(field), length);
    const auto end = text.find('\0');
    return end == std::string::npos ? text : text.substr(0, end);
}

}  // namespace

TEST_CASE("El login viaja cifrado y con cuenta y contraseña protegidas")
{
    Mu099B::BYTE clientSerial[16];
    std::memcpy(clientSerial, kSerial.data(), sizeof(clientSerial));

    const auto packet = Mu099B::BuildLoginRequest("test", "test", 0x12345678, kVersion, clientSerial);

    REQUIRE(sizeof(packet) == 49);
    CHECK(packet.header.type == 0xC3);  // el login va cifrado por bloques
    CHECK(packet.header.head == 0xF1);
    CHECK(packet.header.subh == 0x01);
    CHECK(packet.header.size == 49);

    // La cuenta NO puede viajar en claro.
    CHECK(std::memcmp(packet.account, "test", 4) != 0);

    // Y con el mismo XOR se recupera: es involutivo.
    Mu099B::BYTE account[10];
    std::memcpy(account, packet.account, sizeof(account));
    Mu099B::ApplyArgumentCipher(account, sizeof(account));
    CHECK(ReadFixed(account, sizeof(account)) == "test");
}

TEST_CASE("El login llega al servidor con las credenciales correctas")
{
    Mu099B::BYTE clientSerial[16];
    std::memcpy(clientSerial, kSerial.data(), sizeof(clientSerial));

    const auto packet =
        Mu099B::BuildLoginRequest("admin", "secreto123", 0xAABBCCDD, kVersion, clientSerial);

    Mu099B::GameEncoder encoder(MakeStream(), Mu099B::BlockCipher(Enc1, Dec1));
    const auto wire = encoder.Encode(reinterpret_cast<const uint8_t*>(&packet), sizeof(packet));
    const auto decoded = ServerDecode(wire);

    REQUIRE(decoded.size() == sizeof(packet));
    CHECK(decoded[2] == 0xF1);
    CHECK(decoded[3] == 0x01);

    // El servidor deshace el XOR de argumentos sobre los campos en su offset.
    Mu099B::BYTE account[10];
    Mu099B::BYTE password[10];
    std::memcpy(account, decoded.data() + 4, sizeof(account));
    std::memcpy(password, decoded.data() + 14, sizeof(password));
    Mu099B::ApplyArgumentCipher(account, sizeof(account));
    Mu099B::ApplyArgumentCipher(password, sizeof(password));

    CHECK(ReadFixed(account, sizeof(account)) == "admin");
    // "secreto123" tiene 10 caracteres: entra justo, sin terminador.
    CHECK(ReadFixed(password, sizeof(password)) == "secreto123");

    // Versión y serial tienen que llegar intactos o el servidor rebota el login.
    CHECK(std::memcmp(decoded.data() + 28, kVersion, sizeof(kVersion)) == 0);
    CHECK(std::memcmp(decoded.data() + 33, clientSerial, sizeof(clientSerial)) == 0);
}

TEST_CASE("Un nombre más largo que el campo se recorta sin desbordar")
{
    const auto packet = Mu099B::BuildCharacterCreateRequest("NombreDemasiadoLargo", 0);

    REQUIRE(sizeof(packet) == 15);
    CHECK(std::memcmp(packet.name, "NombreDema", 10) == 0);
    CHECK(packet.Class == 0);
}

TEST_CASE("El pedido de lista de personajes no lleva cuerpo")
{
    const auto header = Mu099B::BuildCharacterListRequest();
    const auto* raw = reinterpret_cast<const uint8_t*>(&header);

    REQUIRE(sizeof(header) == 4);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 0x04);
    CHECK(raw[2] == 0xF3);
    CHECK(raw[3] == 0x00);
}

TEST_CASE("Seleccionar personaje manda sólo el nombre")
{
    const auto packet = Mu099B::BuildCharacterSelectRequest("Hero1");

    REQUIRE(sizeof(packet) == 14);
    CHECK(packet.header.head == 0xF3);
    CHECK(packet.header.subh == 0x03);
    CHECK(ReadFixed(reinterpret_cast<const Mu099B::BYTE*>(packet.name), sizeof(packet.name)) ==
          "Hero1");
    // Lo que sobra del campo queda en cero, no con basura de la pila.
    for (size_t i = 5; i < sizeof(packet.name); ++i)
    {
        CHECK(packet.name[i] == '\0');
    }
}

TEST_CASE("Borrar personaje lleva nombre y código personal")
{
    const auto packet = Mu099B::BuildCharacterDeleteRequest("Hero1", "1234567");

    REQUIRE(sizeof(packet) == 24);
    CHECK(packet.header.subh == 0x02);
    CHECK(ReadFixed(reinterpret_cast<const Mu099B::BYTE*>(packet.name), sizeof(packet.name)) ==
          "Hero1");
    CHECK(ReadFixed(reinterpret_cast<const Mu099B::BYTE*>(packet.PersonalCode),
                    sizeof(packet.PersonalCode)) == "1234567");
}

TEST_CASE("La lista de personajes que llega tiene renglones de 28 bytes")
{
    // El renglón NO usa #pragma pack(1): tras slot + Name[10] queda un byte de
    // relleno antes del WORD Level. Sin él, del segundo personaje en adelante
    // todo se lee corrido -- el bug de "solo veo el primero que creé".
    CHECK(sizeof(Mu099B::PMSG_CHARACTER_LIST_SEND) == 7);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST_SEND, count) == 6);

    CHECK(sizeof(Mu099B::PMSG_CHARACTER_LIST) == 28);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST, slot) == 0);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST, Name) == 1);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST, Level) == 12);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST, CtlCode) == 14);
    CHECK(offsetof(Mu099B::PMSG_CHARACTER_LIST, CharSet) == 15);
}

// Codificador de salida del socket de GameServer.
//
// La prueba de fondo es la ida y vuelta contra el SERVIDOR: se codifica un
// paquete como lo haría el cliente y se decodifica con las mismas reglas que
// aplica el emulador al recibir (descifrar flujo, separar, descifrar bloques,
// des-ofuscar XorData). Si alguna capa quedara en el orden equivocado o con la
// ofuscación en el sentido contrario, el cuerpo no se recuperaría.

#include <doctest.h>

#include <cstring>
#include <vector>

#include "Protocol099B/BlockCipher099B.h"
#include "Protocol099B/GameEncoder099B.h"
#include "Protocol099B/StreamCipher099B.h"

namespace
{

// Data/Enc1.dat -- con esto cifra el cliente; el servidor descifra con Dec1.dat.
constexpr Mu099B::BlockCipher::KeyTable Enc1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00005BC1u, 0x00002E87u, 0x00004D68u, 0x0000354Fu},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
};
// Data/Dec1.dat -- con esto descifra el SERVIDOR lo que manda el cliente.
constexpr Mu099B::BlockCipher::KeyTable Dec1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00007B38u, 0x000007FFu, 0x0000DEB3u, 0x000027C7u},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
};

const std::string kSerial = "PoweredSetecSoft";

Mu099B::StreamCipher MakeStream()
{
    return Mu099B::StreamCipher::FromServerSerial(
        reinterpret_cast<const uint8_t*>(kSerial.data()), kSerial.size());
}

/// Decodifica como lo hace el emulador al recibir del cliente.
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
    logical[0] = static_cast<uint8_t>(type == 0xC3 ? 0xC1 : 0xC2);
    if (headerLen == 2)
    {
        logical[1] = static_cast<uint8_t>(logicalSize);
    }
    else
    {
        logical[1] = static_cast<uint8_t>((logicalSize >> 8) & 0xFF);
        logical[2] = static_cast<uint8_t>(logicalSize & 0xFF);
    }
    std::memcpy(logical.data() + headerLen, plain->data() + 1, payloadLen);

    Mu099B::DeobfuscateInPlace(logical.data(), logicalSize, headerLen);
    return logical;
}

Mu099B::GameEncoder MakeEncoder()
{
    return Mu099B::GameEncoder(MakeStream(), Mu099B::BlockCipher(Enc1, Dec1));
}

}  // namespace

TEST_CASE("Un C1 plano llega al servidor tal como se armó")
{
    auto encoder = MakeEncoder();
    const std::vector<uint8_t> logical = {0xC1, 0x08, 0xF3, 0x11, 0x22, 0x33, 0x44, 0x55};

    const auto wire = encoder.Encode(logical.data(), logical.size());
    CHECK(wire != logical);  // que efectivamente cifre

    CHECK(ServerDecode(wire) == logical);
}

TEST_CASE("Un C3 cifrado llega al servidor tal como se armó")
{
    auto encoder = MakeEncoder();
    // Login: C3:F1:01 con cuerpo de relleno.
    std::vector<uint8_t> logical = {0xC3, 0x0C, 0xF1, 0x01, 0x61, 0x62,
                                    0x63, 0x00, 0x70, 0x77, 0x64, 0x00};

    const auto wire = encoder.Encode(logical.data(), logical.size());
    const auto decoded = ServerDecode(wire);

    // El servidor lo reconstruye como C1 lógico; el resto del paquete es igual.
    REQUIRE(decoded.size() == logical.size());
    CHECK(decoded[0] == 0xC1);
    CHECK(decoded[1] == logical[1]);
    CHECK(std::vector<uint8_t>(decoded.begin() + 2, decoded.end()) ==
          std::vector<uint8_t>(logical.begin() + 2, logical.end()));
}

TEST_CASE("El número de serie avanza en cada envío cifrado")
{
    // El original lo incrementa por conexión: dos envíos idénticos tienen que
    // salir distintos al wire.
    auto encoder = MakeEncoder();
    const std::vector<uint8_t> logical = {0xC3, 0x06, 0xF1, 0x01, 0x00, 0x00};

    CHECK(encoder.NextSerial() == 0);
    const auto first = encoder.Encode(logical.data(), logical.size());
    CHECK(encoder.NextSerial() == 1);
    const auto second = encoder.Encode(logical.data(), logical.size());

    CHECK(first != second);
    // Aun así los dos se decodifican al mismo paquete lógico.
    CHECK(ServerDecode(first) == ServerDecode(second));
}

TEST_CASE("Un tamaño mal puesto por quien llama se corrige antes de ofuscar")
{
    // XorData encadena desde el byte de tamaño, y el servidor lo recalcula al
    // reconstruir: si se mandara el valor equivocado, el cuerpo llegaría
    // revuelto sin ningún error visible.
    auto encoder = MakeEncoder();
    std::vector<uint8_t> wrongSize = {0xC1, 0x00, 0xF3, 0xAA, 0xBB, 0xCC};

    const auto wire = encoder.Encode(wrongSize.data(), wrongSize.size());
    const auto decoded = ServerDecode(wire);

    CHECK(decoded[1] == 0x06);  // normalizado al largo real
    CHECK(decoded[2] == 0xF3);
    CHECK(decoded[3] == 0xAA);
    CHECK(decoded[4] == 0xBB);
    CHECK(decoded[5] == 0xCC);
}

TEST_CASE("Un paquete más corto que un encabezado no produce nada")
{
    auto encoder = MakeEncoder();
    const std::vector<uint8_t> tooShort = {0xC1, 0x02};
    CHECK(encoder.Encode(tooShort.data(), tooShort.size()).empty());
}

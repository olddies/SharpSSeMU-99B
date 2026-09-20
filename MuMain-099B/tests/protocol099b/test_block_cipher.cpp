// Cifrado por bloques (SimpleModulus) y ofuscación XorData del protocolo 0.99B.
//
// Los vectores no son auto-generados por este código: salen de la
// implementación en C# del servidor (SharpSSeMU PacketCipher), que es un puerto
// verificado del C++ original. Si la transcripción a C++ se desviara aunque sea
// en un bit, estos bytes no darían.
//
// Las tablas van embebidas en vez de leerse de Enc1.dat/Dec2.dat para que el
// test no dependa de rutas fuera del repo; que esos valores sean los correctos
// se comprueba aparte, al cargar los archivos reales en el cliente.

#include <doctest.h>

#include <vector>

#include "Protocol099B/BlockCipher099B.h"

namespace
{

// Data/Enc1.dat -- con esto CIFRA el cliente.
constexpr Mu099B::BlockCipher::KeyTable Enc1 = {
    {0x0001F44Fu, 0x00028386u, 0x0001125Bu, 0x0001A192u},
    {0x00005BC1u, 0x00002E87u, 0x00004D68u, 0x0000354Fu},
    {0x0000BD1Du, 0x0000B455u, 0x00003B43u, 0x00009239u},
};

// Data/Dec2.dat -- con esto DESCIFRA el cliente lo que manda el servidor.
constexpr Mu099B::BlockCipher::KeyTable Dec2 = {
    {0x00011E6Eu, 0x0001ADA5u, 0x0001821Bu, 0x00029C32u},
    {0x00004673u, 0x00007684u, 0x0000607Du, 0x00002B85u},
    {0x0000F234u, 0x0000FB99u, 0x00008A2Eu, 0x0000FC57u},
};

Mu099B::BlockCipher ClientCipher()
{
    return Mu099B::BlockCipher(Enc1, Dec2);
}

}  // namespace

TEST_CASE("Cifrar produce exactamente los bytes que produce el servidor")
{
    // Vector generado con SharpSSeMU PacketCipher cargando Enc1.dat.
    const std::vector<uint8_t> plain = {0xF1, 0x01, 0x11, 0x22, 0x33,
                                        0x44, 0x55, 0x66, 0x77, 0x88, 0x99};
    const std::vector<uint8_t> expected = {
        0x2B, 0x30, 0x65, 0x60, 0x45, 0x34, 0x03, 0x9E, 0x78, 0x4A, 0x7F,
        0xD7, 0xBB, 0x6A, 0x09, 0x01, 0x24, 0xA0, 0x8E, 0x58, 0xA0, 0x9E,
    };

    const auto actual = ClientCipher().Encrypt(plain.data(), plain.size());

    // 11 bytes lógicos ocupan dos bloques de 8 -> 22 bytes de wire.
    REQUIRE(actual.size() == 22);
    CHECK(actual == expected);
}

TEST_CASE("Descifrar lo que cifró el servidor devuelve el original")
{
    // Vector generado con SharpSSeMU PacketCipher cargando Enc2.dat (rol
    // servidor); el cliente lo descifra con Dec2.dat.
    const std::vector<uint8_t> wire = {0xDF, 0x5D, 0x36, 0x4D, 0xCD, 0x38,
                                       0x31, 0x87, 0x50, 0x31, 0x09};
    const std::vector<uint8_t> expected = {0xF1, 0x00, 0x01, 0x02, 0x03};

    const auto decrypted = ClientCipher().Decrypt(wire.data(), wire.size());

    REQUIRE(decrypted.has_value());
    CHECK(*decrypted == expected);
}

TEST_CASE("Un bloque corrupto se rechaza en vez de devolver basura")
{
    // El checksum es lo único que separa "paquete válido" de "ruido"; en el
    // original un bloque que no valida desconecta al cliente.
    std::vector<uint8_t> wire = {0xDF, 0x5D, 0x36, 0x4D, 0xCD, 0x38,
                                 0x31, 0x87, 0x50, 0x31, 0x09};
    wire[3] ^= 0xFF;

    CHECK_FALSE(ClientCipher().Decrypt(wire.data(), wire.size()).has_value());
}

TEST_CASE("Un largo que no es múltiplo del bloque de wire se rechaza")
{
    const std::vector<uint8_t> wire(10, 0);
    CHECK_FALSE(ClientCipher().Decrypt(wire.data(), wire.size()).has_value());
}

TEST_CASE("XorData es reversible y no toca el encabezado")
{
    // El encadenado va en sentidos opuestos al ofuscar y al des-ofuscar; si se
    // hiciera en el mismo sentido, la ida y vuelta no cerraría.
    constexpr size_t headerLength = 3;
    std::vector<uint8_t> data = {0xC1, 0x0C, 0xF3, 0x10, 0x20, 0x30,
                                 0x40, 0x50, 0x60, 0x70, 0x80, 0x90};
    const std::vector<uint8_t> original = data;

    Mu099B::ObfuscateInPlace(data.data(), data.size(), headerLength);
    CHECK(data != original);
    // El encabezado queda intacto: el framer lo necesita legible.
    CHECK(data[0] == 0xC1);
    CHECK(data[1] == 0x0C);
    CHECK(data[2] == 0xF3);

    Mu099B::DeobfuscateInPlace(data.data(), data.size(), headerLength);
    CHECK(data == original);
}

TEST_CASE("XorData no hace nada si no hay cuerpo detrás del encabezado")
{
    std::vector<uint8_t> data = {0xC1, 0x03, 0xF3};
    const std::vector<uint8_t> original = data;

    Mu099B::ObfuscateInPlace(data.data(), data.size(), 3);
    CHECK(data == original);

    Mu099B::DeobfuscateInPlace(data.data(), data.size(), 3);
    CHECK(data == original);
}

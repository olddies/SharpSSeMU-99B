// Cifrado de flujo del socket de GameServer (HackCheck EncryptData/DecryptData).
//
// Las claves esperadas se calcularon con una implementación independiente del
// mismo algoritmo, no copiando este código: si la transcripción a C++ se
// hubiera desviado, estos valores no darían.

#include <doctest.h>

#include <cstring>
#include <string>
#include <vector>

#include "Protocol099B/StreamCipher099B.h"

namespace
{

Mu099B::StreamCipher FromSerial(const std::string& serial)
{
    return Mu099B::StreamCipher::FromServerSerial(
        reinterpret_cast<const uint8_t*>(serial.data()), serial.size());
}

}  // namespace

TEST_CASE("Las claves salen del ServerSerial igual que en InitHackCheck")
{
    // 'PoweredSetecSoft' es el serial que usan los tests end-to-end del servidor.
    // Estos dos valores no salen de leer el código: salen de descifrar el saludo
    // que manda un GameServer real, que es la única fuente que no puede estar de
    // acuerdo con un error nuestro.
    const auto cipher = FromSerial("PoweredSetecSoft");
    CHECK(cipher.Key1() == 0x3B);
    CHECK(cipher.Key2() == 0xDA);

    // Un serial vacío deja las constantes desnudas del original (0xBB / 0xCC).
    const auto empty = FromSerial("");
    CHECK(empty.Key1() == 0xBB);
    CHECK(empty.Key2() == 0xCC);

    const auto single = FromSerial("A");
    CHECK(single.Key1() == 0xB9);
    CHECK(single.Key2() == 0xCC);
}

TEST_CASE("El serial se rellena hasta los 17 bytes del campo")
{
    // La derivación cicla sobre el campo completo, no sobre lo que se escribió.
    // Escribir el relleno a mano tiene que dar exactamente lo mismo que dejar
    // que lo complete la función: si no, el largo del texto se cuela en la clave
    // y el flujo entero sale corrido.
    std::string padded("PoweredSetecSoft");
    padded.resize(Mu099B::ServerSerialFieldSize);  // completa con nulos
    REQUIRE(padded.size() == 17);

    const auto implicit = FromSerial("PoweredSetecSoft");
    const auto explicitly = FromSerial(padded);

    CHECK(implicit.Key1() == explicitly.Key1());
    CHECK(implicit.Key2() == explicitly.Key2());
}

TEST_CASE("Cifrar y descifrar devuelve el original")
{
    const auto cipher = FromSerial("PoweredSetecSoft");

    std::vector<uint8_t> data;
    for (int i = 0; i < 256; ++i)
    {
        data.push_back(static_cast<uint8_t>(i));
    }
    const std::vector<uint8_t> original = data;

    cipher.Encrypt(data.data(), data.size());
    CHECK(data != original);  // que efectivamente haga algo

    cipher.Decrypt(data.data(), data.size());
    CHECK(data == original);
}

TEST_CASE("El cifrado es sin estado: no importa cómo se parta el flujo")
{
    // El framer alimenta lo que llegue del socket, que puede cortarse en
    // cualquier lado. Si el cifrado tuviera estado, descifrar por pedazos daría
    // distinto que descifrar todo junto.
    const auto cipher = FromSerial("PoweredSetecSoft");

    std::vector<uint8_t> whole(64);
    for (size_t i = 0; i < whole.size(); ++i)
    {
        whole[i] = static_cast<uint8_t>(i * 7 + 3);
    }
    std::vector<uint8_t> chunked = whole;

    cipher.Encrypt(whole.data(), whole.size());

    // En tres pedazos desparejos.
    cipher.Encrypt(chunked.data(), 5);
    cipher.Encrypt(chunked.data() + 5, 30);
    cipher.Encrypt(chunked.data() + 35, chunked.size() - 35);

    CHECK(whole == chunked);
}

TEST_CASE("Un serial distinto produce un flujo distinto")
{
    const auto a = FromSerial("PoweredSetecSoft");
    const auto b = FromSerial("OtroSerialDistinto");
    REQUIRE(a.Key1() != b.Key1());

    std::vector<uint8_t> withA(16, 0x41);
    std::vector<uint8_t> withB(16, 0x41);
    a.Encrypt(withA.data(), withA.size());
    b.Encrypt(withB.data(), withB.size());

    CHECK(withA != withB);
}

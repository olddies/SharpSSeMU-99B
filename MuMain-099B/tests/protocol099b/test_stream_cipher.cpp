// Stream cipher of the GameServer socket (HackCheck EncryptData/DecryptData). The expected keys were computed
// with an independent implementation of the same algorithm, not by copying this code: if the transcription to
// C++ had deviated, these values would not match.

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
    // 'SharpSSeMU99B-v1' is the serial used by the server's end-to-end tests. These two values do not come from
    // reading the code: they come from decrypting the greeting sent by a real GameServer, which is the only
    // source that cannot agree with a mistake of ours.
    const auto cipher = FromSerial("SharpSSeMU99B-v1");
    CHECK(cipher.Key1() == 0xE1);
    CHECK(cipher.Key2() == 0xD5);

    // An empty serial leaves the original's bare constants (0xBB / 0xCC).
    const auto empty = FromSerial("");
    CHECK(empty.Key1() == 0xBB);
    CHECK(empty.Key2() == 0xCC);

    const auto single = FromSerial("A");
    CHECK(single.Key1() == 0xB9);
    CHECK(single.Key2() == 0xCC);
}

TEST_CASE("El serial se rellena hasta los 17 bytes del campo")
{
    // The derivation cycles over the whole field, not over what was written. Writing the padding by hand has to
    // give exactly the same as letting the function complete it: otherwise the text length leaks into the key
    // and the whole stream comes out shifted.
    std::string padded("SharpSSeMU99B-v1");
    padded.resize(Mu099B::ServerSerialFieldSize);  // completa con nulos
    REQUIRE(padded.size() == 17);

    const auto implicit = FromSerial("SharpSSeMU99B-v1");
    const auto explicitly = FromSerial(padded);

    CHECK(implicit.Key1() == explicitly.Key1());
    CHECK(implicit.Key2() == explicitly.Key2());
}

TEST_CASE("Cifrar y descifrar devuelve el original")
{
    const auto cipher = FromSerial("SharpSSeMU99B-v1");

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
    // The framer feeds whatever comes from the socket, which can be cut anywhere. If the cipher had state,
    // decrypting in pieces would give a different result than decrypting everything together.
    const auto cipher = FromSerial("SharpSSeMU99B-v1");

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
    const auto a = FromSerial("SharpSSeMU99B-v1");
    const auto b = FromSerial("OtroSerialDistinto");
    REQUIRE(a.Key1() != b.Key1());

    std::vector<uint8_t> withA(16, 0x41);
    std::vector<uint8_t> withB(16, 0x41);
    a.Encrypt(withA.data(), withA.size());
    b.Encrypt(withB.data(), withB.size());

    CHECK(withA != withB);
}

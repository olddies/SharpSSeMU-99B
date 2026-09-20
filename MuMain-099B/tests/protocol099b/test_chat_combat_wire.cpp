// Chat, whisper, attack and pose. They are small packets, but they share two traps that already cost dearly
// elsewhere in the protocol: object indices are big-endian, and text fields are fixed-length with zero padding
// -- what is left over cannot be left with stack garbage.

#include <doctest.h>

#include <cstring>
#include <string>

#include "Protocol099B/Wire099B.h"

namespace
{

std::string ReadFixed(const char* field, size_t length)
{
    std::string text(field, length);
    const auto end = text.find('\0');
    return end == std::string::npos ? text : text.substr(0, end);
}

}  // namespace

TEST_CASE("El chat público lleva nombre y mensaje de largo fijo")
{
    const auto packet = Mu099B::BuildChatRequest("Hero1", "hola mundo");
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 73);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 73);
    CHECK(raw[2] == 0x00);

    CHECK(ReadFixed(packet.name, sizeof(packet.name)) == "Hero1");
    CHECK(ReadFixed(packet.message, sizeof(packet.message)) == "hola mundo");
}

TEST_CASE("El resto de cada campo de texto queda en cero")
{
    // If garbage were left, the server would take it as part of the text: the fields are fixed-length, not
    // terminated strings.
    const auto packet = Mu099B::BuildChatRequest("Ab", "xy");

    for (size_t i = 2; i < sizeof(packet.name); ++i)
    {
        CHECK(packet.name[i] == '\0');
    }
    for (size_t i = 2; i < sizeof(packet.message); ++i)
    {
        CHECK(packet.message[i] == '\0');
    }
}

TEST_CASE("Un mensaje más largo que el campo se recorta sin desbordar")
{
    const std::string tooLong(200, 'x');
    const auto packet = Mu099B::BuildChatRequest("Hero1", tooLong.c_str());

    // The whole field is filled, with no terminator and without overrunning.
    CHECK(std::memcmp(packet.message, tooLong.data(), sizeof(packet.message)) == 0);
}

TEST_CASE("El susurro usa el head 0x02 y el nombre es el destinatario")
{
    const auto packet = Mu099B::BuildWhisperRequest("Hero2", "secreto");
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 73);
    CHECK(raw[2] == 0x02);
    CHECK(ReadFixed(packet.name, sizeof(packet.name)) == "Hero2");
    CHECK(ReadFixed(packet.message, sizeof(packet.message)) == "secreto");
}

TEST_CASE("El ataque manda el índice del objetivo en big-endian")
{
    // Writing it the other way round would point to a completely different object.
    const auto packet = Mu099B::BuildAttackRequest(0x1234, 0x78, 3);
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 7);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 7);
    CHECK(raw[2] == 0xD9);
    CHECK(raw[3] == 0x12);  // byte alto primero
    CHECK(raw[4] == 0x34);
    CHECK(raw[5] == 0x78);
    CHECK(raw[6] == 3);
}

TEST_CASE("La pose pone dirección y acción antes del índice")
{
    // The field order is NOT the same as in the attack: here direction and action come first, and the index
    // last.
    const auto packet = Mu099B::BuildActionRequest(5, 0x81, 0xABCD);
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 7);
    CHECK(raw[2] == 0x18);
    CHECK(raw[3] == 5);     // dir
    CHECK(raw[4] == 0x81);  // action
    CHECK(raw[5] == 0xAB);
    CHECK(raw[6] == 0xCD);
}

TEST_CASE("El daño que llega trae la vida en 32 bits")
{
    // The two `damage` bytes cap at 65535; ViewCurHP and ViewDamageHP are the real values (GAMESERVER_EXTRA).
    // The padding matters: without it, the two DWORDs are read shifted.
    CHECK(sizeof(Mu099B::PMSG_DAMAGE_SEND) == 16);
    CHECK(offsetof(Mu099B::PMSG_DAMAGE_SEND, damage) == 5);
    CHECK(offsetof(Mu099B::PMSG_DAMAGE_SEND, type) == 7);
    CHECK(offsetof(Mu099B::PMSG_DAMAGE_SEND, ViewCurHP) == 8);
    CHECK(offsetof(Mu099B::PMSG_DAMAGE_SEND, ViewDamageHP) == 12);
}

TEST_CASE("La pose que difunde el servidor lleva también el objetivo")
{
    CHECK(sizeof(Mu099B::PMSG_ACTION_SEND) == 9);
    CHECK(offsetof(Mu099B::PMSG_ACTION_SEND, index) == 3);
    CHECK(offsetof(Mu099B::PMSG_ACTION_SEND, dir) == 5);
    CHECK(offsetof(Mu099B::PMSG_ACTION_SEND, action) == 6);
    CHECK(offsetof(Mu099B::PMSG_ACTION_SEND, target) == 7);
}

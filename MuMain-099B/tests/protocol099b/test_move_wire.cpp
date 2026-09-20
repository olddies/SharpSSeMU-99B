// Movement and position. The case that matters is the variable path length: the struct declares path[8] but the
// client only sends the bytes it uses. A server that assumes all 8 reads too much -- exactly what happened to
// SharpSSeMU with real packets. Here it is checked against a decoder that replicates the emulator's.

#include <doctest.h>

#include <cstring>
#include <vector>

#include "Protocol099B/Wire099B.h"

namespace
{

/// Decodes the path like CGMoveRecv: step n (from 1) comes from byte (n+1)/2, high nibble if n is odd and low
/// if even.
std::vector<uint8_t> ServerDecodePath(const uint8_t* packet)
{
    const uint8_t* path = packet + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    const int stepCount = path[0] & 0x0F;

    std::vector<uint8_t> steps;
    for (int n = 1; n <= stepCount; ++n)
    {
        const uint8_t byte = path[(n + 1) / 2];
        steps.push_back((n % 2) == 1 ? static_cast<uint8_t>(byte >> 4)
                                     : static_cast<uint8_t>(byte & 0x0F));
    }
    return steps;
}

}  // namespace

TEST_CASE("Un movimiento sin pasos ocupa lo mínimo")
{
    // Only the destination cell: header + x + y + one path byte.
    const auto request = Mu099B::BuildMoveRequest(120, 130, 3, nullptr, 0);

    CHECK(request.Length == 6);
    CHECK(request.Data[0] == 0xC1);
    CHECK(request.Data[1] == 6);
    CHECK(request.Data[2] == 0xD7);
    CHECK(request.Data[3] == 120);
    CHECK(request.Data[4] == 130);
    CHECK(request.Data[5] == 0x30);  // direction 3 above, zero steps below
}

TEST_CASE("El largo crece de a un byte cada dos pasos")
{
    const uint8_t steps[8] = {1, 2, 3, 4, 5, 6, 7, 0};

    struct Expected
    {
        size_t StepCount;
        uint8_t Length;
    };

    for (const auto& e : {Expected{1, 7}, Expected{2, 7}, Expected{3, 8},
                          Expected{4, 8}, Expected{5, 9}, Expected{8, 10}})
    {
        CAPTURE(e.StepCount);
        const auto request = Mu099B::BuildMoveRequest(10, 20, 0, steps, e.StepCount);
        CHECK(request.Length == e.Length);
        // The declared size always matches what is sent.
        CHECK(request.Data[1] == e.Length);
    }
}

TEST_CASE("El servidor recupera exactamente los pasos que se mandaron")
{
    const std::vector<uint8_t> steps = {1, 7, 3, 0, 5};
    const auto request = Mu099B::BuildMoveRequest(200, 150, 2, steps.data(), steps.size());

    CHECK(ServerDecodePath(request.Data) == steps);

    // And the initial direction goes in the high nibble of the first path byte.
    const uint8_t* path = request.Data + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    CHECK((path[0] >> 4) == 2);
    CHECK((path[0] & 0x0F) == steps.size());
}

TEST_CASE("Los pasos impares y pares caen en el nibble correcto")
{
    // With two different steps in the same byte it shows if they were reversed.
    const uint8_t steps[2] = {0x0A & 0x0F, 0x05};
    const auto request = Mu099B::BuildMoveRequest(0, 0, 0, steps, 2);

    const uint8_t* path = request.Data + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    CHECK((path[1] >> 4) == steps[0]);    // paso 1: nibble alto
    CHECK((path[1] & 0x0F) == steps[1]);  // paso 2: nibble bajo
}

TEST_CASE("Más pasos de los que entran se recortan en vez de desbordar")
{
    // The counter lives in a nibble, so there is no way to express more than 15.
    std::vector<uint8_t> tooMany(40, 1);
    const auto request = Mu099B::BuildMoveRequest(1, 2, 0, tooMany.data(), tooMany.size());

    CHECK(request.Length <= sizeof(Mu099B::PMSG_MOVE_RECV));
    const uint8_t* path = request.Data + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    CHECK((path[0] & 0x0F) == Mu099B::MaxMoveSteps);
}

TEST_CASE("La corrección de posición son cinco bytes")
{
    const auto packet = Mu099B::BuildPositionRequest(77, 88);
    const auto* raw = reinterpret_cast<const uint8_t*>(&packet);

    REQUIRE(sizeof(packet) == 5);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 5);
    CHECK(raw[2] == 0xD0);
    CHECK(raw[3] == 77);
    CHECK(raw[4] == 88);
}

TEST_CASE("El viewport de jugadores tiene un paso fijo de 34 bytes")
{
    // `count` does NOT count bytes following the entry: GenerateEffectList returns a mask of active effects and
    // appends nothing. The stride is fixed.
    CHECK(sizeof(Mu099B::PMSG_VIEWPORT_SEND) == 5);
    CHECK(sizeof(Mu099B::PMSG_VIEWPORT_PLAYER) == 34);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_PLAYER, CharSet) == 4);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_PLAYER, count) == 18);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_PLAYER, name) == 20);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_PLAYER, DirAndPkLevel) == 32);
}

TEST_CASE("El viewport de monstruos tiene un paso fijo de 12 bytes")
{
    CHECK(sizeof(Mu099B::PMSG_VIEWPORT_MONSTER) == 12);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_MONSTER, type) == 2);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_MONSTER, x) == 6);
    CHECK(offsetof(Mu099B::PMSG_VIEWPORT_MONSTER, DirAndPkLevel) == 10);
}

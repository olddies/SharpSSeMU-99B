// Movimiento y posición.
//
// El caso que importa es el largo variable del camino: el struct declara
// path[8] pero el cliente sólo manda los bytes que usa. Un servidor que asuma
// los 8 lee de más -- exactamente lo que le pasó a SharpSSeMU con paquetes
// reales. Acá se comprueba contra un decodificador que replica el del emulador.

#include <doctest.h>

#include <cstring>
#include <vector>

#include "Protocol099B/Wire099B.h"

namespace
{

/// Decodifica el camino como CGMoveRecv: el paso n (desde 1) sale del byte
/// (n+1)/2, nibble alto si n es impar y bajo si es par.
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
    // Sólo la casilla destino: encabezado + x + y + un byte de camino.
    const auto request = Mu099B::BuildMoveRequest(120, 130, 3, nullptr, 0);

    CHECK(request.Length == 6);
    CHECK(request.Data[0] == 0xC1);
    CHECK(request.Data[1] == 6);
    CHECK(request.Data[2] == 0xD7);
    CHECK(request.Data[3] == 120);
    CHECK(request.Data[4] == 130);
    CHECK(request.Data[5] == 0x30);  // dirección 3 arriba, cero pasos abajo
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
        // El tamaño declarado siempre coincide con lo que se manda.
        CHECK(request.Data[1] == e.Length);
    }
}

TEST_CASE("El servidor recupera exactamente los pasos que se mandaron")
{
    const std::vector<uint8_t> steps = {1, 7, 3, 0, 5};
    const auto request = Mu099B::BuildMoveRequest(200, 150, 2, steps.data(), steps.size());

    CHECK(ServerDecodePath(request.Data) == steps);

    // Y la dirección inicial va en el nibble alto del primer byte de camino.
    const uint8_t* path = request.Data + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    CHECK((path[0] >> 4) == 2);
    CHECK((path[0] & 0x0F) == steps.size());
}

TEST_CASE("Los pasos impares y pares caen en el nibble correcto")
{
    // Con dos pasos distintos en el mismo byte se nota si se invirtieran.
    const uint8_t steps[2] = {0x0A & 0x0F, 0x05};
    const auto request = Mu099B::BuildMoveRequest(0, 0, 0, steps, 2);

    const uint8_t* path = request.Data + offsetof(Mu099B::PMSG_MOVE_RECV, path);
    CHECK((path[1] >> 4) == steps[0]);    // paso 1: nibble alto
    CHECK((path[1] & 0x0F) == steps[1]);  // paso 2: nibble bajo
}

TEST_CASE("Más pasos de los que entran se recortan en vez de desbordar")
{
    // El contador vive en un nibble, así que no hay forma de expresar más de 15.
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
    // `count` NO cuenta bytes que sigan a la entrada: GenerateEffectList
    // devuelve una máscara de efectos activos y no apenda nada. El paso es fijo.
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

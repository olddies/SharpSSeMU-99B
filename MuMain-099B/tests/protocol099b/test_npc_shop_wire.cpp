// Pedidos de NPC, tienda, puertas y señal de vida.
//
// Todos son paquetes chicos, y ahí está el riesgo: un opcode equivocado o un
// campo corrido no rompe nada visible, sólo hace que el servidor conteste otra
// cosa o nada. Los valores esperados salen del despacho de SharpSSeMU y de los
// static_assert del generador, no de leer los constructores.

#include <doctest.h>

#include <cstdint>

#include "Protocol099B/Wire099B.h"

namespace
{

const Mu099B::BYTE* Raw(const void* packet)
{
    return reinterpret_cast<const Mu099B::BYTE*>(packet);
}

}  // namespace

TEST_CASE("Hablarle a un NPC lleva su indice de objeto en big-endian")
{
    const auto packet = Mu099B::BuildNpcTalkRequest(0x1234);
    const auto* raw = Raw(&packet);

    REQUIRE(sizeof(packet) == 5);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 5);
    CHECK(raw[2] == 0x30);
    CHECK(raw[3] == 0x12);  // los indices de objeto van big-endian
    CHECK(raw[4] == 0x34);
}

TEST_CASE("Cerrar la ventana del NPC es solo la cabecera")
{
    const auto packet = Mu099B::BuildNpcCloseRequest();
    const auto* raw = Raw(&packet);

    // Tres bytes: sin cuerpo. Si el tamaño declarado no coincide con lo que se
    // manda, el servidor pierde la sincronización del flujo entero.
    REQUIRE(sizeof(packet) == 3);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 3);
    CHECK(raw[2] == 0x31);
}

TEST_CASE("Comprar y vender se distinguen solo por el opcode")
{
    // Los dos llevan un slot y nada más, así que confundirlos vende lo que uno
    // queria comprar. Por eso el test los mira juntos.
    const auto buy = Mu099B::BuildItemBuyRequest(7);
    const auto sell = Mu099B::BuildItemSellRequest(7);

    REQUIRE(sizeof(buy) == 4);
    REQUIRE(sizeof(sell) == 4);

    CHECK(Raw(&buy)[2] == 0x32);
    CHECK(Raw(&sell)[2] == 0x33);

    CHECK(buy.slot == 7);
    CHECK(sell.slot == 7);
}

TEST_CASE("Reparar lleva slot y tipo")
{
    const auto packet = Mu099B::BuildItemRepairRequest(3, 1);

    REQUIRE(sizeof(packet) == 5);
    CHECK(Raw(&packet)[2] == 0x34);
    CHECK(packet.slot == 3);
    CHECK(packet.type == 1);
}

TEST_CASE("Cruzar una puerta manda tambien donde esta uno parado")
{
    // El servidor usa x e y para comprobar que el personaje de verdad llegó
    // hasta la puerta: mandar la posición de destino en vez de la actual hace
    // que rechace el cruce.
    const auto packet = Mu099B::BuildTeleportRequest(17, 125, 130);
    const auto* raw = Raw(&packet);

    REQUIRE(sizeof(packet) == 6);
    CHECK(raw[2] == 0x1C);
    CHECK(packet.gate == 17);
    CHECK(packet.x == 125);
    CHECK(packet.y == 130);
}

TEST_CASE("La senal de vida lleva el reloj y las dos velocidades")
{
    const auto packet = Mu099B::BuildLiveClientRequest(0x11223344, 0x0102, 0x0304);
    const auto* raw = Raw(&packet);

    REQUIRE(sizeof(packet) == 12);
    CHECK(raw[2] == 0x0E);

    // Estos tres sí son campos nativos del struct, no bytes armados a mano, así
    // que viajan en little-endian como los escribe el compilador.
    CHECK(packet.TickCount == 0x11223344u);
    CHECK(packet.PhysiSpeed == 0x0102);
    CHECK(packet.MagicSpeed == 0x0304);

    // El relleno entre la cabecera de 3 bytes y el DWORD alineado a 4 viaja por
    // la red: el servidor lee sizeof(), no la suma de los campos.
    CHECK(offsetof(Mu099B::PMSG_LIVE_CLIENT_RECV, TickCount) == 4);
}

TEST_CASE("Todos declaran el tamano que realmente ocupan")
{
    // El framer del servidor corta por el byte de tamaño: si miente, lo que
    // sigue en el flujo se lee como si empezara en otro lado.
    const auto talk = Mu099B::BuildNpcTalkRequest(1);
    const auto buy = Mu099B::BuildItemBuyRequest(0);
    const auto sell = Mu099B::BuildItemSellRequest(0);
    const auto repair = Mu099B::BuildItemRepairRequest(0, 0);
    const auto gate = Mu099B::BuildTeleportRequest(0, 0, 0);
    const auto live = Mu099B::BuildLiveClientRequest(0, 0, 0);

    CHECK(Raw(&talk)[1] == sizeof(talk));
    CHECK(Raw(&buy)[1] == sizeof(buy));
    CHECK(Raw(&sell)[1] == sizeof(sell));
    CHECK(Raw(&repair)[1] == sizeof(repair));
    CHECK(Raw(&gate)[1] == sizeof(gate));
    CHECK(Raw(&live)[1] == sizeof(live));
}

TEST_CASE("El dinero del trade y del baul viaja en big-endian")
{
    // Los dos campos están declarados como entero de cuatro bytes pero se
    // llenan a mano, byte por byte, empezando por el más significativo.
    // Asignarlos como enteros nativos los invierte y el servidor lee otra cifra
    // -- una que además es enorme, no cero, así que el error no se nota hasta
    // que alguien pierde dinero.
    const auto trade = Mu099B::BuildTradeMoneyRequest(0x01020304);

    REQUIRE(Mu099B::TradeMoneyRequest::Length == 7);
    CHECK(trade.Data[0] == 0xC1);
    CHECK(trade.Data[1] == 7);
    CHECK(trade.Data[2] == 0x3A);  // PMSG_TRADE_MONEY_RECV::kHead -- ver docs/protocolo-099b.md
    CHECK(trade.Data[3] == 0x01);
    CHECK(trade.Data[4] == 0x02);
    CHECK(trade.Data[5] == 0x03);
    CHECK(trade.Data[6] == 0x04);

    const auto vault = Mu099B::BuildWarehouseMoneyRequest(0, 0x01020304);
    const auto* raw = Raw(&vault);

    REQUIRE(sizeof(vault) == 8);
    CHECK(raw[2] == 0x81);
    CHECK(raw[3] == 0);     // tipo
    CHECK(raw[4] == 0x01);  // el monto arranca en el byte 4, ya alineado
    CHECK(raw[5] == 0x02);
    CHECK(raw[6] == 0x03);
    CHECK(raw[7] == 0x04);
}

TEST_CASE("Los paquetes sin cuerpo declaran tres bytes")
{
    // Cancelar el trade y cerrar el baúl no existen como struct en el emulador;
    // el layout sale del parser del servidor. Si el tamaño no coincide con lo
    // que se manda, el flujo entero se desincroniza.
    const auto cancel = Mu099B::BuildTradeCancelRequest();
    const auto close = Mu099B::BuildWarehouseCloseRequest();

    CHECK(Raw(&cancel)[1] == 3);
    CHECK(Raw(&cancel)[2] == 0x3D);
    CHECK(Raw(&close)[1] == 3);
    CHECK(Raw(&close)[2] == 0x82);
}

TEST_CASE("La respuesta al trade manda el struct completo aunque use un byte")
{
    // El servidor sólo lee el primer byte, pero el original manda sizeof()
    // entero. Recortarlo cambiaría el tamaño declarado y el framer del servidor
    // cortaría en el lugar equivocado.
    const auto accept = Mu099B::BuildTradeResponse(true);
    const auto reject = Mu099B::BuildTradeResponse(false);

    REQUIRE(sizeof(accept) == 20);
    CHECK(Raw(&accept)[1] == 20);
    CHECK(Raw(&accept)[2] == 0x37);
    CHECK(accept.response == 1);
    CHECK(reject.response == 0);
}

TEST_CASE("El skill de area es de largo variable y lleva los objetivos detras")
{
    const Mu099B::WORD targets[] = {0x0102, 0x0304, 0x0506};
    const auto packet = Mu099B::BuildMultiSkillRequest(0x2C, 100, 120, 7, targets, 3);

    // Ocho de cabecera más dos por objetivo. El tamaño declarado tiene que ser
    // ese, no el del buffer: el framer del servidor corta por ahí.
    REQUIRE(packet.Length == 8 + 3 * 2);
    CHECK(packet.Data[0] == 0xC1);
    CHECK(packet.Data[1] == packet.Length);
    CHECK(packet.Data[2] == 0x1D);
    CHECK(packet.Data[3] == 0x2C);
    CHECK(packet.Data[4] == 100);
    CHECK(packet.Data[5] == 120);
    CHECK(packet.Data[6] == 7);
    CHECK(packet.Data[7] == 3);

    // Los indices, big-endian como todos los de este protocolo.
    CHECK(packet.Data[8] == 0x01);
    CHECK(packet.Data[9] == 0x02);
    CHECK(packet.Data[10] == 0x03);
    CHECK(packet.Data[11] == 0x04);
    CHECK(packet.Data[12] == 0x05);
    CHECK(packet.Data[13] == 0x06);
}

TEST_CASE("Mas objetivos de los que entran se recortan, no desbordan")
{
    // Escribir fuera del buffer por confiar en un conteo que viene de la logica
    // de juego seria un desborde de pila; el servidor tampoco procesa tantos.
    Mu099B::WORD muchos[Mu099B::MultiSkillRequest::MaxTargets + 5] = {};
    const auto packet = Mu099B::BuildMultiSkillRequest(
        1, 0, 0, 0, muchos, Mu099B::MultiSkillRequest::MaxTargets + 5);

    CHECK(packet.Length == 8 + Mu099B::MultiSkillRequest::MaxTargets * 2);
    CHECK(packet.Data[7] == Mu099B::MultiSkillRequest::MaxTargets);
    CHECK(packet.Length <= sizeof(packet.Data));
}

TEST_CASE("Los pedidos de amistad se distinguen por opcode y por tamano")
{
    const auto add = Mu099B::BuildFriendAddRequest("Hero1");
    const auto result = Mu099B::BuildFriendResultRequest(true, "Hero1");
    const auto del = Mu099B::BuildFriendDeleteRequest("Hero1");
    const auto list = Mu099B::BuildFriendListRequest();

    // Agregar y borrar llevan solo el nombre; aceptar lleva ademas el resultado
    // adelante, asi que el nombre arranca un byte despues.
    REQUIRE(sizeof(add) == 13);
    REQUIRE(sizeof(result) == 14);
    REQUIRE(sizeof(del) == 13);

    CHECK(Raw(&add)[2] == 0xC1);
    CHECK(Raw(&result)[2] == 0xC2);
    CHECK(Raw(&del)[2] == 0xC3);
    CHECK(Raw(&list)[2] == 0xC0);

    CHECK(offsetof(Mu099B::PMSG_FRIEND_REQUEST_RECV, Name) == 3);
    CHECK(offsetof(Mu099B::PMSG_FRIEND_RESULT_RECV, Name) == 4);
    CHECK(result.result == 1);
}

TEST_CASE("Crear un gremio lleva ocho caracteres de nombre y el emblema entero")
{
    Mu099B::BYTE mark[32];
    for (size_t i = 0; i < sizeof(mark); ++i)
    {
        mark[i] = static_cast<Mu099B::BYTE>(i);
    }

    const auto packet = Mu099B::BuildGuildCreateRequest("MiGremio", mark);

    // El nombre de gremio son ocho, no diez como los de personaje: usar el
    // largo equivocado corre el emblema entero.
    REQUIRE(sizeof(packet) == 43);
    CHECK(Raw(&packet)[2] == 0x55);
    CHECK(offsetof(Mu099B::PMSG_GUILD_CREATE_RECV, GuildName) == 3);
    CHECK(offsetof(Mu099B::PMSG_GUILD_CREATE_RECV, Mark) == 11);

    for (size_t i = 0; i < sizeof(mark); ++i)
    {
        CHECK(packet.Mark[i] == mark[i]);
    }
}

TEST_CASE("Repartir un punto identifica la estadistica por numero")
{
    // El orden del servidor es fuerza, agilidad, vitalidad, energia, liderazgo.
    // Coincide con el enum del cliente, y por eso el cast directo alcanza -- si
    // alguno de los dos cambiara, este test lo delata.
    const auto packet = Mu099B::BuildLevelUpPointRequest(2);  // vitalidad

    REQUIRE(sizeof(packet) == 5);
    CHECK(Raw(&packet)[2] == 0xF3);
    CHECK(Raw(&packet)[3] == 0x06);
    CHECK(packet.type == 2);
}

TEST_CASE("Las peticiones sin cuerpo declaran solo su cabecera")
{
    const auto party = Mu099B::BuildPartyListRequest();
    const auto guild = Mu099B::BuildGuildListRequest();
    const auto viewport = Mu099B::BuildViewportEnableRequest();

    CHECK(Raw(&party)[1] == 3);
    CHECK(Raw(&party)[2] == 0x42);
    CHECK(Raw(&guild)[1] == 3);
    CHECK(Raw(&guild)[2] == 0x52);

    // Este lleva sub-código, así que su cabecera es de cuatro y no de tres.
    CHECK(Raw(&viewport)[1] == 4);
    CHECK(Raw(&viewport)[2] == 0xF3);
    CHECK(Raw(&viewport)[3] == 0x12);
}

TEST_CASE("Los pedidos de evento y de mezcla llevan sus dos campos")
{
    const auto devil = Mu099B::BuildDevilSquareEnterRequest(3, 20);
    const auto blood = Mu099B::BuildBloodCastleEnterRequest(2, 21);
    const auto mix = Mu099B::BuildChaosMixRequest(1, 0);
    const auto rate = Mu099B::BuildChaosMixRateRequest(1);
    const auto quest = Mu099B::BuildQuestStateRequest(5, 1);

    CHECK(Raw(&devil)[2] == 0x90);
    CHECK(devil.level == 3);
    CHECK(devil.slot == 20);

    CHECK(Raw(&blood)[2] == 0x9A);
    CHECK(blood.level == 2);
    CHECK(blood.slot == 21);

    CHECK(Raw(&mix)[2] == 0x86);
    CHECK(mix.type == 1);

    // Este declara un entero de cuatro bytes, así que la cabecera de tres deja
    // un byte de relleno antes: el campo arranca en el 4, no en el 3.
    REQUIRE(sizeof(rate) == 8);
    CHECK(Raw(&rate)[2] == 0x88);
    CHECK(offsetof(Mu099B::PMSG_CHAOS_MIX_RATE_RECV, type) == 4);

    CHECK(Raw(&quest)[2] == 0xA2);
    CHECK(quest.QuestIndex == 5);
    CHECK(quest.QuestState == 1);
}

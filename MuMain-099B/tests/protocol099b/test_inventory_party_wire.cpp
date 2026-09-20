// Inventory, skills and party. What repeats most here is the big-endian object index and the 5 ItemInfo bytes,
// which this build uses everywhere (the previous dialect uses 12). The tests pin those two contracts.

#include <doctest.h>

#include <cstring>

#include "Protocol099B/Wire099B.h"

TEST_CASE("Mover un item lleva contenedor y slot de los dos lados")
{
    // The 5 ItemInfo bytes are returned as they are: the server uses them to confirm that the item the client
    // believes is being moved is the one moved.
    const Mu099B::BYTE itemInfo[Mu099B::ItemInfoSize] = {0x00, 0x08, 0x3C, 0x00, 0x00};

    const auto packet = Mu099B::BuildItemMoveRequest(
        Mu099B::ItemContainer::Inventory, 12, itemInfo, Mu099B::ItemContainer::Inventory, 0);
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 12);
    CHECK(raw[0] == 0xC1);
    CHECK(raw[1] == 12);
    CHECK(raw[2] == 0x24);
    CHECK(raw[3] == 0);   // contenedor origen: inventario
    CHECK(raw[4] == 12);  // slot origen
    CHECK(std::memcmp(raw + 5, itemInfo, sizeof(itemInfo)) == 0);
    CHECK(raw[10] == 0);  // contenedor destino
    CHECK(raw[11] == 0);  // slot destino
}

TEST_CASE("Los contenedores tienen los valores que espera el servidor")
{
    // A wrong value here sends the item to another container: from the inventory to the warehouse, for example.
    CHECK(static_cast<Mu099B::BYTE>(Mu099B::ItemContainer::Inventory) == 0);
    CHECK(static_cast<Mu099B::BYTE>(Mu099B::ItemContainer::Trade) == 1);
    CHECK(static_cast<Mu099B::BYTE>(Mu099B::ItemContainer::Warehouse) == 2);
    CHECK(static_cast<Mu099B::BYTE>(Mu099B::ItemContainer::ChaosBox) == 3);
}

TEST_CASE("Mover sin ItemInfo deja los cinco bytes en cero, no con basura")
{
    const auto packet = Mu099B::BuildItemMoveRequest(
        Mu099B::ItemContainer::Inventory, 5, nullptr, Mu099B::ItemContainer::Warehouse, 3);

    for (size_t i = 0; i < sizeof(packet.ItemInfo); ++i)
    {
        CHECK(packet.ItemInfo[i] == 0);
    }
    CHECK(packet.TargetFlag == 2);
}

TEST_CASE("Usar, tirar y levantar items")
{
    const auto use = Mu099B::BuildItemUseRequest(20, 0, 1);
    REQUIRE(sizeof(use) == 6);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&use)[2] == 0x26);
    CHECK(use.SourceSlot == 20);
    CHECK(use.TargetSlot == 0);
    CHECK(use.type == 1);

    const auto drop = Mu099B::BuildItemDropRequest(120, 130, 15);
    REQUIRE(sizeof(drop) == 6);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&drop)[2] == 0x23);
    CHECK(drop.x == 120);
    CHECK(drop.y == 130);
    CHECK(drop.slot == 15);

    const auto get = Mu099B::BuildItemGetRequest(0x0102);
    REQUIRE(sizeof(get) == 5);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&get)[2] == 0x22);
    CHECK(get.index[0] == 0x01);  // big-endian
    CHECK(get.index[1] == 0x02);
}

TEST_CASE("El skill lleva número, objetivo y distancia")
{
    const auto packet = Mu099B::BuildSkillAttackRequest(0x2C, 0x1234, 3);
    const auto* raw = reinterpret_cast<const Mu099B::BYTE*>(&packet);

    REQUIRE(sizeof(packet) == 7);
    CHECK(raw[2] == 0x19);
    CHECK(raw[3] == 0x2C);  // the skill goes BEFORE the index
    CHECK(raw[4] == 0x12);
    CHECK(raw[5] == 0x34);
    CHECK(raw[6] == 3);
}

TEST_CASE("La invitación de party y su respuesta")
{
    const auto invite = Mu099B::BuildPartyRequest(0x00AB);
    REQUIRE(sizeof(invite) == 5);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&invite)[2] == 0x40);
    CHECK(invite.index[0] == 0x00);
    CHECK(invite.index[1] == 0xAB);

    const auto accept = Mu099B::BuildPartyRequestResult(true, 0x00AB);
    REQUIRE(sizeof(accept) == 6);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&accept)[2] == 0x41);
    CHECK(accept.result == 1);
    CHECK(accept.index[1] == 0xAB);

    const auto reject = Mu099B::BuildPartyRequestResult(false, 0x00AB);
    CHECK(reject.result == 0);
}

TEST_CASE("Echar del grupo va por número de miembro, no por índice de objeto")
{
    const auto packet = Mu099B::BuildPartyDeleteMember(2);
    REQUIRE(sizeof(packet) == 4);
    CHECK(reinterpret_cast<const Mu099B::BYTE*>(&packet)[2] == 0x43);
    CHECK(packet.number == 2);
}

TEST_CASE("Los renglones de la lista de party miden 24 bytes")
{
    // With the padding before the DWORD CurLife. Reading it as 22 shifts the life bars and misaligns from the
    // second member on -- the same bug that had to be fixed on the server side.
    CHECK(sizeof(Mu099B::PMSG_PARTY_LIST) == 24);
    CHECK(offsetof(Mu099B::PMSG_PARTY_LIST, name) == 0);
    CHECK(offsetof(Mu099B::PMSG_PARTY_LIST, number) == 10);
    CHECK(offsetof(Mu099B::PMSG_PARTY_LIST, CurLife) == 16);
    CHECK(offsetof(Mu099B::PMSG_PARTY_LIST, MaxLife) == 20);
}

TEST_CASE("El equipo que difunde el servidor trae el CharSet completo")
{
    CHECK(sizeof(Mu099B::PMSG_ITEM_EQUIPMENT_SEND) == 19);
    CHECK(offsetof(Mu099B::PMSG_ITEM_EQUIPMENT_SEND, index) == 4);
    CHECK(offsetof(Mu099B::PMSG_ITEM_EQUIPMENT_SEND, CharSet) == 6);
}

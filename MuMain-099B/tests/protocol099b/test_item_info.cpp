// The five item bytes of 0.99B and the index conversion. The expected values come from the emulator's
// ItemByteConvert, not from reading my own decoder: a test comparing the implementation against itself passes
// even when wrong, which is exactly what let the 17-byte ServerSerial error through.

#include <doctest.h>

#include <array>
#include <cstdint>

#include "Protocol099B/ItemInfo099B.h"

namespace
{

/// Reimplementation of CItemManager::ItemByteConvert as the server does it, to compare against it instead of
/// against ourselves.
std::array<uint8_t, Mu099B::ItemInfoSize> ServerEncode(int index, int level, int durability,
                                                       int luck, int skill, int option3,
                                                       int newOption, int setOption)
{
    std::array<uint8_t, Mu099B::ItemInfoSize> bytes{};

    bytes[0] = static_cast<uint8_t>(index & 0xFF);

    bytes[1] = 0;
    bytes[1] |= static_cast<uint8_t>(level * 8);
    bytes[1] |= static_cast<uint8_t>(luck * 128);
    bytes[1] |= static_cast<uint8_t>(skill * 4);
    bytes[1] |= static_cast<uint8_t>(option3 & 3);

    bytes[2] = static_cast<uint8_t>(durability);

    bytes[3] = 0;
    bytes[3] |= static_cast<uint8_t>((index & 256) >> 1);
    bytes[3] |= static_cast<uint8_t>(option3 > 3 ? 64 : 0);
    bytes[3] |= static_cast<uint8_t>(newOption);

    bytes[4] = static_cast<uint8_t>(setOption);

    return bytes;
}

}  // namespace

TEST_CASE("Los cinco bytes se leen igual que los arma el servidor")
{
    // Una espada +13 con suerte, habilidad, opcion +16 y durabilidad 60.
    const auto bytes = ServerEncode(/*index*/ 3, /*level*/ 13, /*durability*/ 60,
                                    /*luck*/ 1, /*skill*/ 1, /*option3*/ 5,
                                    /*newOption*/ 2, /*setOption*/ 7);

    const auto item = Mu099B::DecodeItemInfo(bytes.data());

    CHECK(item.Present);
    CHECK(item.Index == 3);
    CHECK(item.Group == 0);   // espadas
    CHECK(item.Number == 3);
    CHECK(item.Level == 13);
    CHECK(item.Durability == 60);
    CHECK(item.Luck);
    CHECK(item.Skill);
    CHECK(item.OptionLevel == 5);
    CHECK(item.ExcellentFlags == 2);
    CHECK(item.SetOption == 7);
}

TEST_CASE("El noveno bit del indice viaja en el byte 3")
{
    // With sections of 32 and sixteen sections the index reaches 511, which does not fit in byte 0. Losing that
    // bit turns any item in the second half of the table into another one 256 places earlier.
    for (const uint16_t index : {static_cast<uint16_t>(255), static_cast<uint16_t>(256),
                                 static_cast<uint16_t>(300), static_cast<uint16_t>(511)})
    {
        const auto bytes = ServerEncode(index, 0, 100, 0, 0, 0, 0, 0);
        const auto item = Mu099B::DecodeItemInfo(bytes.data());

        CHECK(item.Present);
        CHECK(item.Index == index);
    }

    // And that the bit is where the server puts it, not elsewhere in the byte.
    const auto high = ServerEncode(256, 0, 0, 0, 0, 0, 0, 0);
    CHECK(high[3] == 0x80);
}

TEST_CASE("El tercer bit del nivel de opcion pesa cuatro, no uno")
{
    // The two low bits go in byte 1 and the third in byte 3. Adding it as if it were the lowest-weight bit
    // gives the wrong option.
    for (int optionLevel = 0; optionLevel < 8; ++optionLevel)
    {
        const auto bytes = ServerEncode(0, 0, 1, 0, 0, optionLevel, 0, 0);
        CHECK(Mu099B::DecodeItemInfo(bytes.data()).OptionLevel == optionLevel);
    }
}

TEST_CASE("Un slot vacio son cinco ceros, y el indice 0 es un item de verdad")
{
    const std::array<uint8_t, Mu099B::ItemInfoSize> empty{};
    CHECK_FALSE(Mu099B::DecodeItemInfo(empty.data()).Present);

    // The first sword has index 0. If "no item" were decided by looking only at the index, that sword would
    // vanish from the inventory.
    const auto sword = ServerEncode(0, 0, /*durability*/ 20, 0, 0, 0, 0, 0);
    const auto item = Mu099B::DecodeItemInfo(sword.data());
    CHECK(item.Present);
    CHECK(item.Index == 0);
    CHECK(item.Durability == 20);
}

TEST_CASE("Codificar y decodificar devuelve el original")
{
    Mu099B::ItemInfo item;
    item.Present = true;
    item.Index = 457;
    item.Level = 9;
    item.Durability = 44;
    item.Luck = true;
    item.Skill = false;
    item.OptionLevel = 6;
    item.ExcellentFlags = 33;
    item.SetOption = 12;

    uint8_t bytes[Mu099B::ItemInfoSize];
    Mu099B::EncodeItemInfo(item, bytes);

    // And that they are the same bytes the server would build.
    const auto expected = ServerEncode(457, 9, 44, 1, 0, 6, 33, 12);
    for (size_t i = 0; i < Mu099B::ItemInfoSize; ++i)
    {
        CHECK(bytes[i] == expected[i]);
    }

    const auto roundTrip = Mu099B::DecodeItemInfo(bytes);
    CHECK(roundTrip.Index == item.Index);
    CHECK(roundTrip.Level == item.Level);
    CHECK(roundTrip.Durability == item.Durability);
    CHECK(roundTrip.Luck == item.Luck);
    CHECK(roundTrip.Skill == item.Skill);
    CHECK(roundTrip.OptionLevel == item.OptionLevel);
    CHECK(roundTrip.ExcellentFlags == item.ExcellentFlags);
    CHECK(roundTrip.SetOption == item.SetOption);
}

TEST_CASE("Los dos indices se diferencian solo en el paso")
{
    // Same group and same number in both numberings: 0.99B advances by 32 per section and the client by 512 per
    // group.
    CHECK(Mu099B::ToClientItemIndex(0) == 0);        // espada 0
    CHECK(Mu099B::ToClientItemIndex(31) == 31);      // espada 31
    CHECK(Mu099B::ToClientItemIndex(32) == 512);     // hacha 0
    CHECK(Mu099B::ToClientItemIndex(33) == 513);     // hacha 1
    CHECK(Mu099B::ToClientItemIndex(224) == 3584);   // helm 0 (section 7)
    CHECK(Mu099B::ToClientItemIndex(511) == 7711);   // misc 31 (section 15)

    for (uint16_t wire = 0; wire < 512; ++wire)
    {
        uint16_t back = 0;
        REQUIRE(Mu099B::ToWireItemIndex(Mu099B::ToClientItemIndex(wire), back));
        CHECK(back == wire);
    }
}

TEST_CASE("Un item que 0.99B no tiene se rechaza en vez de recortarse")
{
    // Client groups reach 512 items; 0.99B's sections, 32. A number of 40 does not exist here, and sending it
    // trimmed would make the server understand another object -- exactly the kind of error that gives no
    // symptom until someone loses an item.
    uint16_t wire = 0xFFFF;
    CHECK_FALSE(Mu099B::ToWireItemIndex(40, wire));
    CHECK_FALSE(Mu099B::ToWireItemIndex(512 + 100, wire));

    // The exact limit: 31 fits, 32 does not.
    CHECK(Mu099B::ToWireItemIndex(31, wire));
    CHECK(wire == 31);
    CHECK_FALSE(Mu099B::ToWireItemIndex(32, wire));
}

TEST_CASE("El bloque para el cliente se lee igual que lo lee el cliente")
{
    // Reimplementation of ParseItemData (NewUIItemMng.cpp), which is what will consume these bytes. Comparing
    // against the real reader is the point: a block that we write and we read proves nothing.
    struct Parsed
    {
        int Group = 0, Number = 0, Level = 0, Durability = 0;
        bool Luck = false, Skill = false;
        int OptionLevel = 0, OptionType = 0, ExcellentFlags = 0, AncientDiscriminator = 0;
    };

    auto clientParse = [](const uint8_t* d) {
        Parsed p;
        p.Group = (d[0] >> 4) & 0xF;
        p.Number = ((d[0] & 0xF) << 8) + d[1];
        p.Level = d[2];
        p.Durability = d[3];
        const uint8_t flags = d[4];
        p.Luck = (flags & 0x02) != 0;
        p.Skill = (flags & 0x04) != 0;

        int offset = 0;
        if (flags & 0x01)
        {
            p.OptionLevel = d[5] & 0xF;
            p.OptionType = (d[5] >> 4) & 0xF;
            ++offset;
        }
        if (flags & 0x08)
        {
            p.ExcellentFlags = d[5 + offset];
            ++offset;
        }
        if (flags & 0x10)
        {
            p.AncientDiscriminator = d[5 + offset] & 0xF;
            ++offset;
        }
        return p;
    };

    Mu099B::ItemInfo item;
    item.Present = true;
    item.Index = 300;  // section 9 (pants), sub 12
    item.Group = 9;
    item.Number = 12;
    item.Level = 11;
    item.Durability = 88;
    item.Luck = true;
    item.Skill = true;
    item.OptionLevel = 3;
    item.ExcellentFlags = 0x21;
    item.SetOption = 5;

    uint8_t block[Mu099B::ClientItemBlockSize];
    const size_t written = Mu099B::WriteClientItemBlock(item, block);

    // Five fixed plus option, excellent and set.
    CHECK(written == 8);

    const auto parsed = clientParse(block);
    CHECK(parsed.Group == 9);
    CHECK(parsed.Number == 12);
    CHECK(parsed.Level == 11);
    CHECK(parsed.Durability == 88);
    CHECK(parsed.Luck);
    CHECK(parsed.Skill);
    CHECK(parsed.OptionLevel == 3);
    CHECK(parsed.ExcellentFlags == 0x21);
    CHECK(parsed.AncientDiscriminator == 5);
}

TEST_CASE("Un item sin opciones ocupa solo los cinco bytes fijos")
{
    // The flags rule: announcing a field that was not written would shift everything that follows, and the
    // client's inventory reads the items one after another.
    Mu099B::ItemInfo item;
    item.Present = true;
    item.Index = 0;
    item.Durability = 20;

    uint8_t block[Mu099B::ClientItemBlockSize];
    CHECK(Mu099B::WriteClientItemBlock(item, block) == 5);
    CHECK(block[4] == 0);  // sin banderas

    item.Luck = true;
    CHECK(Mu099B::WriteClientItemBlock(item, block) == 5);  // la suerte no agrega bytes
    CHECK(block[4] == 0x02);

    item.OptionLevel = 1;
    CHECK(Mu099B::WriteClientItemBlock(item, block) == 6);
    CHECK((block[4] & 0x01) != 0);
}

TEST_CASE("Un slot vacio no escribe bloque")
{
    const Mu099B::ItemInfo empty;
    uint8_t block[Mu099B::ClientItemBlockSize];
    CHECK(Mu099B::WriteClientItemBlock(empty, block) == 0);
}

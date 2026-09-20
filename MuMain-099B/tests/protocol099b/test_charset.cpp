// Decoding of the appearance CharSet[13]. The reference is the server's composer: here
// CharacterMakePreviewCharSet is reimplemented exactly as the emulator does it, a CharSet is built and it is
// checked that the decoder recovers exactly what was put in. One shifted bit in this format makes all
// characters look the same, which is the symptom that already cost dearly on the server side.

#include <doctest.h>

#include <array>
#include <cstdint>
#include <cstring>
#include <vector>

#include "Protocol099B/CharSet099B.h"

namespace
{

/// A piece of equipment as the server has it before packing it.
struct ServerItem
{
    int Index = -1;  // < full index (section*32 + sub); -1 = empty slot
    int Level = 1;
    bool Excellent = false;
    bool SetItem = false;
};

constexpr int SlotWeapon1 = 0;
constexpr int SlotWeapon2 = 1;
constexpr int SlotHelm = 2;
constexpr int SlotArmor = 3;
constexpr int SlotPants = 4;
constexpr int SlotGloves = 5;
constexpr int SlotBoots = 6;
constexpr int SlotWing = 7;
constexpr int SlotHelper = 8;

/// Puerto del compositor del servidor (ObjectManager.cpp:1139-1269), usado como
/// referencia independiente del decodificador.
std::array<uint8_t, 13> BuildCharSet(uint8_t characterClass, uint8_t changeUp,
                                     const std::array<ServerItem, 9>& wear)
{
    std::array<uint8_t, 13> charSet{};

    auto b0 = static_cast<uint8_t>(changeUp * 16);
    b0 -= static_cast<uint8_t>(b0 / 32);
    b0 += static_cast<uint8_t>(characterClass * 32);
    charSet[0] = b0;

    int temp[9];
    for (int n = 0; n < 9; ++n)
    {
        const bool present = wear[n].Index >= 0;
        if (n == SlotWeapon1 || n == SlotWeapon2)
        {
            temp[n] = present ? wear[n].Index : Mu099B::NoWeapon;
        }
        else
        {
            temp[n] = present ? (wear[n].Index % Mu099B::ItemsPerGroup) : Mu099B::NoItem;
        }
    }

    charSet[1] = static_cast<uint8_t>(temp[SlotWeapon1] % 256);
    charSet[2] = static_cast<uint8_t>(temp[SlotWeapon2] % 256);

    charSet[3] |= static_cast<uint8_t>((temp[SlotHelm] & 0x0F) << 4);
    charSet[9] |= static_cast<uint8_t>((temp[SlotHelm] & 0x10) << 3);
    charSet[3] |= static_cast<uint8_t>(temp[SlotArmor] & 0x0F);
    charSet[9] |= static_cast<uint8_t>((temp[SlotArmor] & 0x10) << 2);
    charSet[4] |= static_cast<uint8_t>((temp[SlotPants] & 0x0F) << 4);
    charSet[9] |= static_cast<uint8_t>((temp[SlotPants] & 0x10) << 1);
    charSet[4] |= static_cast<uint8_t>(temp[SlotGloves] & 0x0F);
    charSet[9] |= static_cast<uint8_t>(temp[SlotGloves] & 0x10);
    charSet[5] |= static_cast<uint8_t>((temp[SlotBoots] & 0x0F) << 4);
    charSet[9] |= static_cast<uint8_t>((temp[SlotBoots] & 0x10) >> 1);

    int level = 0;
    constexpr int table[7] = {1, 0, 6, 5, 4, 3, 2};
    for (int n = 0; n < 7; ++n)
    {
        if (temp[n] == Mu099B::NoItem || temp[n] == Mu099B::NoWeapon)
        {
            continue;
        }
        level |= (((wear[n].Level - 1) / 2) & 7) << (n * 3);
        charSet[10] |= static_cast<uint8_t>((wear[n].Excellent ? 2 : 0) << table[n]);
        charSet[11] |= static_cast<uint8_t>((wear[n].SetItem ? 2 : 0) << table[n]);
    }

    charSet[6] = static_cast<uint8_t>(level >> 16);
    charSet[7] = static_cast<uint8_t>(level >> 8);
    charSet[8] = static_cast<uint8_t>(level);

    const int wing = temp[SlotWing];
    if (wing >= 0 && wing <= 2)
    {
        charSet[5] |= static_cast<uint8_t>(wing << 2);
    }
    else if (wing >= 3 && wing <= 6)
    {
        charSet[5] |= 12;
        charSet[9] |= static_cast<uint8_t>(wing - 2);
    }
    else if (wing == 30)
    {
        charSet[5] |= 12;
        charSet[9] |= 5;
    }

    const int helper = temp[SlotHelper];
    if (helper == Mu099B::NoItem)
    {
        charSet[5] |= 3;
    }
    else if (helper >= 0 && helper <= 2)
    {
        charSet[5] |= static_cast<uint8_t>(helper);
    }
    else if (helper == 3)
    {
        charSet[5] |= 3;
        charSet[10] |= 1;
    }
    else if (helper == 4)
    {
        charSet[5] |= 3;
        charSet[12] |= 1;
    }

    return charSet;
}

std::array<ServerItem, 9> EmptyWear()
{
    return {};
}

}  // namespace

TEST_CASE("La clase y el change-up salen del primer byte")
{
    for (uint8_t characterClass = 0; characterClass < 5; ++characterClass)
    {
        for (uint8_t changeUp = 0; changeUp < 2; ++changeUp)
        {
            const auto charSet = BuildCharSet(characterClass, changeUp, EmptyWear());
            const auto appearance = Mu099B::DecodeCharSet(charSet.data());

            CHECK(appearance.CharacterClass == characterClass);
            CHECK(appearance.ChangeUp == changeUp);
        }
    }
}

TEST_CASE("Los cuatro bits bajos del primer byte no ensucian la clase")
{
    // The ViewState goes there, which the server writes separately when building the viewport.
    auto charSet = BuildCharSet(3, 0, EmptyWear());
    charSet[0] = static_cast<uint8_t>((charSet[0] & 0xF0) | 0x0F);

    CHECK(Mu099B::DecodeCharSet(charSet.data()).CharacterClass == 3);
}

TEST_CASE("Un personaje sin equipo no reporta ninguna pieza")
{
    const auto charSet = BuildCharSet(0, 0, EmptyWear());
    const auto appearance = Mu099B::DecodeCharSet(charSet.data());

    CHECK_FALSE(appearance.Weapon[0].Present);
    CHECK_FALSE(appearance.Weapon[1].Present);
    for (const auto& part : appearance.BodyPart)
    {
        CHECK_FALSE(part.Present);
    }
    CHECK_FALSE(appearance.Wing.Present);
}

TEST_CASE("Las armas llevan el índice completo, de donde salen grupo y número")
{
    auto wear = EmptyWear();
    wear[SlotWeapon1] = {/*Index=*/1 * 32 + 5, /*Level=*/7};   // section 1 (axes), sub 5
    wear[SlotWeapon2] = {/*Index=*/6 * 32 + 3, /*Level=*/1};   // section 6 (shields), sub 3

    const auto charSet = BuildCharSet(1, 0, wear);
    const auto appearance = Mu099B::DecodeCharSet(charSet.data());

    REQUIRE(appearance.Weapon[0].Present);
    CHECK(appearance.Weapon[0].Group == 1);
    CHECK(appearance.Weapon[0].Number == 5);
    CHECK(appearance.Weapon[0].GlowLevel == 3);  // ((7-1)/2)&7

    REQUIRE(appearance.Weapon[1].Present);
    CHECK(appearance.Weapon[1].Group == 6);
    CHECK(appearance.Weapon[1].Number == 3);
    CHECK(appearance.Weapon[1].GlowLevel == 0);
}

TEST_CASE("Cada pieza de armadura recupera su grupo implícito y su sub-índice")
{
    // Sub-indices with the 0x10 bit set are used on purpose: that bit travels loose in charSet[9], with a
    // different shift per slot.
    auto wear = EmptyWear();
    wear[SlotHelm] = {7 * 32 + 20, 1};
    wear[SlotArmor] = {8 * 32 + 17, 3};
    wear[SlotPants] = {9 * 32 + 2, 5};
    wear[SlotGloves] = {10 * 32 + 30, 1};
    wear[SlotBoots] = {11 * 32 + 16, 9};

    const auto appearance = Mu099B::DecodeCharSet(BuildCharSet(2, 0, wear).data());

    const uint8_t expectedGroups[5] = {7, 8, 9, 10, 11};
    const uint16_t expectedNumbers[5] = {20, 17, 2, 30, 16};
    const uint8_t expectedGlow[5] = {0, 1, 2, 0, 4};

    for (int i = 0; i < 5; ++i)
    {
        CAPTURE(i);
        REQUIRE(appearance.BodyPart[i].Present);
        CHECK(appearance.BodyPart[i].Group == expectedGroups[i]);
        CHECK(appearance.BodyPart[i].Number == expectedNumbers[i]);
        CHECK(appearance.BodyPart[i].GlowLevel == expectedGlow[i]);
    }
}

TEST_CASE("Los bits de excelente y conjunto van por slot, en un orden propio")
{
    // The server does not store them in slot order but with the table {1,0,6,5,4,3,2}: if the decoder used the
    // natural order, it would mark them on the wrong piece.
    auto wear = EmptyWear();
    wear[SlotWeapon1] = {0 * 32 + 1, 1, /*Excellent=*/true, /*SetItem=*/false};
    wear[SlotBoots] = {11 * 32 + 4, 1, /*Excellent=*/false, /*SetItem=*/true};

    const auto appearance = Mu099B::DecodeCharSet(BuildCharSet(0, 0, wear).data());

    CHECK(appearance.Weapon[0].Excellent);
    CHECK_FALSE(appearance.Weapon[0].SetItem);

    CHECK(appearance.BodyPart[4].SetItem);  // botas
    CHECK_FALSE(appearance.BodyPart[4].Excellent);
}

TEST_CASE("Las alas recuperan su número, incluidas las variantes altas")
{
    struct Case
    {
        int Wing;
        bool Expected;
    };

    for (const auto& testCase : {Case{0, false},  // 0 is "no wings" in the packing
                                 Case{1, true}, Case{2, true}, Case{3, true},
                                 Case{6, true}, Case{30, true}})
    {
        CAPTURE(testCase.Wing);
        auto wear = EmptyWear();
        wear[SlotWing] = {12 * 32 + testCase.Wing, 1};

        const auto appearance = Mu099B::DecodeCharSet(BuildCharSet(0, 0, wear).data());
        CHECK(appearance.Wing.Present == testCase.Expected);
        if (testCase.Expected)
        {
            CHECK(appearance.Wing.Group == static_cast<uint8_t>(Mu099B::ItemGroup::Wing));
            CHECK(appearance.Wing.Number == testCase.Wing);
        }
    }
}

TEST_CASE("La mascota recupera sus cinco variantes")
{
    for (int helper : {0, 1, 2, 3, 4})
    {
        CAPTURE(helper);
        auto wear = EmptyWear();
        wear[SlotHelper] = {13 * 32 + helper, 1};

        const auto appearance = Mu099B::DecodeCharSet(BuildCharSet(0, 0, wear).data());
        REQUIRE(appearance.Helper.Present);
        CHECK(appearance.Helper.Group == static_cast<uint8_t>(Mu099B::ItemGroup::Helper));
        CHECK(appearance.Helper.Number == helper);
    }
}

TEST_CASE("El bloque extendido queda en el formato que espera el cliente")
{
    auto wear = EmptyWear();
    wear[SlotWeapon1] = {1 * 32 + 5, 7, /*Excellent=*/true};
    wear[SlotArmor] = {8 * 32 + 17, 3};

    const auto appearance = Mu099B::DecodeCharSet(BuildCharSet(1, 0, wear).data());

    uint8_t equipment[Mu099B::ExtendedEquipmentSize];
    Mu099B::WriteExtendedEquipment(appearance, equipment);

    // Weapon 1 at offset 0: group in the high nibble, number in the next byte, glow in the high nibble of the
    // third and excellent in bit 3.
    CHECK(((equipment[0] & 0xF0) >> 4) == 1);
    CHECK(equipment[1] == 5);
    CHECK(((equipment[2] & 0xF0) >> 4) == 3);
    CHECK((equipment[2] & 0x08) != 0);

    // Empty weapon 2: the client detects it by the two 0xFF.
    CHECK(equipment[3] == 0xFF);
    CHECK(equipment[4] == 0xFF);

    // Armor: it is the second piece, at offset 6 + 3.
    CHECK(((equipment[9] & 0xF0) >> 4) == 8);
    CHECK(equipment[10] == 17);
}

TEST_CASE("La clase de la base de datos usa otro empaquetado que la del CharSet")
{
    // These five values are the server's default_class_type rows. Character creation does INSERT ... SELECT ...
    // WHERE class = @class, so a byte not in this list inserts nothing and the server answers "account full" --
    // a message that gives no hint of the cause. It happened with the Season 6 packing (class << 2), which
    // gives 0, 4, 8, 12, 16.
    CHECK(Mu099B::MakeDatabaseClassByte(0) == 0);   // Dark Wizard
    CHECK(Mu099B::MakeDatabaseClassByte(1) == 16);  // Dark Knight
    CHECK(Mu099B::MakeDatabaseClassByte(2) == 32);  // Fairy Elf
    CHECK(Mu099B::MakeDatabaseClassByte(3) == 48);  // Magic Gladiator
    CHECK(Mu099B::MakeDatabaseClassByte(4) == 64);  // Dark Lord

    for (uint8_t base = 0; base < 5; ++base)
    {
        const auto decoded = Mu099B::DecodeDatabaseClassByte(Mu099B::MakeDatabaseClassByte(base));
        CHECK(decoded.CharacterClass == base);
        CHECK(decoded.ChangeUp == 0);
    }

    // The evolution travels in the low nibble.
    const auto evolved = Mu099B::DecodeDatabaseClassByte(0x11);
    CHECK(evolved.CharacterClass == 1);  // Dark Knight
    CHECK(evolved.ChangeUp == 1);        // Blade Knight
}

TEST_CASE("Las dos codificaciones de clase no son intercambiables")
{
    // The CharSet shifts the class one more bit because the low bits carry the ViewState. Mixing them up is
    // silent: both decoders return a valid number, just the wrong one.
    for (uint8_t base = 1; base < 5; ++base)
    {
        const uint8_t database = Mu099B::MakeDatabaseClassByte(base);
        CHECK(Mu099B::DecodeClassByte(database).CharacterClass != base);
    }

    // The only one that matches is class 0, which is why creating a Dark Wizard worked by chance while the rest
    // failed.
    CHECK(Mu099B::DecodeClassByte(Mu099B::MakeDatabaseClassByte(0)).CharacterClass == 0);
}

TEST_CASE("El par de creacion usa una codificacion en cada sentido")
{
    // The request sends the class with the database packing, but the server converts it to the CharSet one
    // before answering. This is the same computation DGCharacterCreateRecv does in the emulator.
    auto serverTransform = [](uint8_t databaseClass) {
        int transformed = (databaseClass % 16) * 16;
        transformed -= transformed / 32;
        transformed += (databaseClass / 16) * 32;
        return static_cast<uint8_t>(transformed & 0xFF);
    };

    for (uint8_t base = 0; base < 5; ++base)
    {
        const uint8_t sent = Mu099B::MakeDatabaseClassByte(base);
        const uint8_t echoed = serverTransform(sent);

        // What comes back has to be read with the CharSet decoder. Reading it with the database one gives
        // another class without failing anywhere: a Dark Knight comes back as a Fairy Elf.
        CHECK(Mu099B::DecodeClassByte(echoed).CharacterClass == base);
    }

    // The concrete case that gave it away: 16 (Dark Knight) is sent and 32 comes back.
    CHECK(serverTransform(16) == 32);
    CHECK(Mu099B::DecodeClassByte(32).CharacterClass == 1);
    CHECK(Mu099B::DecodeDatabaseClassByte(32).CharacterClass == 2);  // la lectura equivocada
}

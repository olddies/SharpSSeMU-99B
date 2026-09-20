#include "CharSet099B.h"

#include <cstring>

namespace Mu099B
{

namespace
{

/// The five armor slots, in the order the client expects them, with the group each one implies.
constexpr uint8_t BodyPartGroups[5] = {
    static_cast<uint8_t>(ItemGroup::Helm),   static_cast<uint8_t>(ItemGroup::Armor),
    static_cast<uint8_t>(ItemGroup::Pants),  static_cast<uint8_t>(ItemGroup::Gloves),
    static_cast<uint8_t>(ItemGroup::Boots),
};

/// Position of the high bit (0x10) of each armor sub-index inside charSet[9], in the same order as
/// BodyPartGroups. The server scatters them with different shifts per slot, so they are listed one by one
/// instead of trying a formula.
struct HighBitPlacement
{
    int Shift;     // < shift applied over bit 0x10 when composing
    bool ShiftLeft;// < true = shifted left, false = shifted right
};

constexpr HighBitPlacement BodyPartHighBits[5] = {
    {3, true},   // casco:    (sub & 0x10) << 3
    {2, true},   // armadura: (sub & 0x10) << 2
    {1, true},   // pants: (sub & 0x10) << 1
    {0, true},   // guantes:  (sub & 0x10)
    {1, false},  // botas:    (sub & 0x10) >> 1
};

/// Recovers the high bit of a sub-index from charSet[9].
bool HasHighBit(uint8_t charSet9, const HighBitPlacement& placement)
{
    const uint8_t mask = placement.ShiftLeft
                             ? static_cast<uint8_t>(0x10 << placement.Shift)
                             : static_cast<uint8_t>(0x10 >> placement.Shift);
    return (charSet9 & mask) != 0;
}

/// Nibble bajo o alto de un byte.
uint8_t Nibble(uint8_t value, bool high)
{
    return high ? static_cast<uint8_t>((value >> 4) & 0x0F) : static_cast<uint8_t>(value & 0x0F);
}

/// Order in which the server stores the excellent and set bits: it is not the slot order, but the table
/// {1,0,6,5,4,3,2} of ObjectManager.cpp.
constexpr int FlagBitBySlot[7] = {1, 0, 6, 5, 4, 3, 2};

bool SlotFlag(uint8_t flagByte, int slot)
{
    return (flagByte & (2 << FlagBitBySlot[slot])) != 0;
}

/// Nivel de brillo de un slot: tres bits dentro del entero de 24 que ocupan
/// charSet[6..8].
uint8_t SlotGlow(uint32_t packedLevel, int slot)
{
    return static_cast<uint8_t>((packedLevel >> (slot * 3)) & 0x07);
}

/// Escribe un slot en el formato de tres bytes del bloque extendido.
void WriteSlot3(uint8_t* target, const AppearanceSlot& slot)
{
    if (!slot.Present)
    {
        target[0] = 0xFF;
        target[1] = 0xFF;
        target[2] = 0x00;
        return;
    }

    target[0] = static_cast<uint8_t>((slot.Group << 4) | ((slot.Number >> 8) & 0x0F));
    target[1] = static_cast<uint8_t>(slot.Number & 0xFF);
    target[2] = static_cast<uint8_t>((slot.GlowLevel << 4) | (slot.Excellent ? 0x08 : 0x00));
}

/// Wings and pet take only two bytes: the client builds the number from the low nibble of the first plus the
/// whole second.
void WriteSlot2(uint8_t* target, const AppearanceSlot& slot)
{
    if (!slot.Present)
    {
        target[0] = 0xFF;
        target[1] = 0xFF;
        return;
    }

    target[0] = static_cast<uint8_t>((slot.Group << 4) | ((slot.Number >> 8) & 0x0F));
    target[1] = static_cast<uint8_t>(slot.Number & 0xFF);
}

}  // namespace

ClassByte DecodeClassByte(uint8_t value)
{
    ClassByte result;
    result.CharacterClass = static_cast<uint8_t>(value / 32);
    result.ChangeUp = static_cast<uint8_t>((value % 32) / 16);
    return result;
}

ClassByte DecodeDatabaseClassByte(uint8_t value)
{
    ClassByte result;
    result.CharacterClass = static_cast<uint8_t>(value >> 4);
    result.ChangeUp = static_cast<uint8_t>(value & 0x0F);
    return result;
}

uint8_t MakeDatabaseClassByte(uint8_t baseClass)
{
    return static_cast<uint8_t>(baseClass << 4);
}

Appearance DecodeCharSet(const uint8_t charSet[13])
{
    Appearance appearance;

    // Byte 0: class in the high bits, change-up in bit 4. The low four bits are the ViewState, which is not
    // part of the appearance.
    const auto classByte = DecodeClassByte(charSet[0]);
    appearance.CharacterClass = classByte.CharacterClass;
    appearance.ChangeUp = classByte.ChangeUp;

    const uint32_t packedLevel = (static_cast<uint32_t>(charSet[6]) << 16) |
                                 (static_cast<uint32_t>(charSet[7]) << 8) |
                                 static_cast<uint32_t>(charSet[8]);

    // Weapons: they carry the full index, so group and number are derived.
    for (int i = 0; i < 2; ++i)
    {
        const uint8_t index = charSet[1 + i];
        if (index == NoWeapon)
        {
            continue;
        }

        AppearanceSlot& slot = appearance.Weapon[i];
        slot.Present = true;
        slot.Group = static_cast<uint8_t>(index / ItemsPerGroup);
        slot.Number = static_cast<uint16_t>(index % ItemsPerGroup);
        slot.GlowLevel = SlotGlow(packedLevel, i);
        slot.Excellent = SlotFlag(charSet[10], i);
        slot.SetItem = SlotFlag(charSet[11], i);
    }

    // Armor: only the sub-index, five bits split between a nibble and a loose bit in charSet[9]. The slot
    // supplies the group.
    const uint8_t nibbleBytes[5] = {charSet[3], charSet[3], charSet[4], charSet[4], charSet[5]};
    const bool nibbleHigh[5] = {true, false, true, false, true};

    for (int i = 0; i < 5; ++i)
    {
        uint8_t sub = Nibble(nibbleBytes[i], nibbleHigh[i]);
        if (HasHighBit(charSet[9], BodyPartHighBits[i]))
        {
            sub = static_cast<uint8_t>(sub | 0x10);
        }

        if (sub == NoItem)
        {
            continue;
        }

        AppearanceSlot& slot = appearance.BodyPart[i];
        slot.Present = true;
        slot.Group = BodyPartGroups[i];
        slot.Number = sub;
        slot.GlowLevel = SlotGlow(packedLevel, i + 2);
        slot.Excellent = SlotFlag(charSet[10], i + 2);
        slot.SetItem = SlotFlag(charSet[11], i + 2);
    }

    // Wings: bits 2-3 of charSet[5] distinguish three cases, and the value 3 means "look at charSet[9]" for the
    // high variants.
    const uint8_t wingBits = static_cast<uint8_t>((charSet[5] >> 2) & 0x03);
    if (wingBits != 0)
    {
        int wingNumber = -1;
        if (wingBits < 3)
        {
            wingNumber = wingBits;
        }
        else
        {
            const uint8_t extended = static_cast<uint8_t>(charSet[9] & 0x07);
            if (extended == 5)
            {
                wingNumber = 30;
            }
            else if (extended >= 1 && extended <= 4)
            {
                wingNumber = extended + 2;
            }
        }

        if (wingNumber >= 0)
        {
            appearance.Wing.Present = true;
            appearance.Wing.Group = static_cast<uint8_t>(ItemGroup::Wing);
            appearance.Wing.Number = static_cast<uint16_t>(wingNumber);
        }
    }

    // Pet: the two low bits of charSet[5], plus the bits of charSet[10] and charSet[12] for the two variants
    // that do not fit in two bits.
    const uint8_t helperBits = static_cast<uint8_t>(charSet[5] & 0x03);
    int helperNumber = -1;

    if (helperBits != 3)
    {
        helperNumber = helperBits;
    }
    else if ((charSet[10] & 0x01) != 0)
    {
        helperNumber = 3;
    }
    else if ((charSet[12] & 0x01) != 0)
    {
        helperNumber = 4;
    }

    if (helperNumber >= 0)
    {
        appearance.Helper.Present = true;
        appearance.Helper.Group = static_cast<uint8_t>(ItemGroup::Helper);
        appearance.Helper.Number = static_cast<uint16_t>(helperNumber);
    }

    return appearance;
}

void WriteExtendedEquipment(const Appearance& appearance, uint8_t equipment[ExtendedEquipmentSize])
{
    std::memset(equipment, 0xFF, ExtendedEquipmentSize);

    int offset = 0;
    for (const auto& weapon : appearance.Weapon)
    {
        WriteSlot3(equipment + offset, weapon);
        offset += 3;
    }
    for (const auto& part : appearance.BodyPart)
    {
        WriteSlot3(equipment + offset, part);
        offset += 3;
    }

    WriteSlot2(equipment + offset, appearance.Wing);
    offset += 2;
    WriteSlot2(equipment + offset, appearance.Helper);
}

}  // namespace Mu099B

// Character appearance: CharSet[13] of the 0.99B protocol. The server sends class, equipment, glow level,
// excellent, set, wings and pet packed into thirteen bytes (CObjectManager::CharacterMakePreviewCharSet). The
// client, on the other hand, expects the 25-byte "extended" block with three bytes per slot. The translation is
// done here. The delicate point is that the CharSet stores **the sub-index** of each armor piece, not the full
// index: the group is implicit in the slot (helm = 7, armor = 8, pants = 9, gloves = 10, boots = 11). Weapons
// do carry the full index, from which group and number are derived. Mixing one up with the other is what makes
// all characters look the same.

#pragma once

#include <cstdint>

#include "Protocol099B.generated.h"

namespace Mu099B
{

/// Grupos de item del cliente. Coinciden con ITEM_GROUP_* de _define.h.
enum class ItemGroup : uint8_t
{
    Helm = 7,
    Armor = 8,
    Pants = 9,
    Gloves = 10,
    Boots = 11,
    Wing = 12,
    Helper = 13,
};

/// In 0.99B each item section has 32 entries (MAX_ITEM_TYPE), so the full index is section*32 + sub.
inline constexpr int ItemsPerGroup = 32;

/// "Empty" values inside the CharSet.
inline constexpr int NoWeapon = 0xFF;
inline constexpr int NoItem = ItemsPerGroup - 1;  // 0x1F

/// Una pieza de equipo ya interpretada.
struct AppearanceSlot
{
    bool Present = false;
    uint8_t Group = 0;
    uint16_t Number = 0;
    /// Glow level as the client expects it (0-7); it goes straight to the high nibble, without conversion: the
    /// server's packing (((Level-1)/2)&7) is already the exact inverse of LevelConvert.
    uint8_t GlowLevel = 0;
    bool Excellent = false;
    bool SetItem = false;
};

/// Apariencia completa de un personaje.
struct Appearance
{
    uint8_t CharacterClass = 0;
    uint8_t ChangeUp = 0;

    AppearanceSlot Weapon[2];
    /// Helm, armor, pants, gloves, boots -- in that order, which is the one the client's equipment block
    /// expects.
    AppearanceSlot BodyPart[5];
    AppearanceSlot Wing;
    AppearanceSlot Helper;
};

/// Largo del bloque de equipo extendido del cliente (EQUIPMENT_LENGTH_EXTENDED).
inline constexpr int ExtendedEquipmentSize = 25;

/// Class and "change up" as the server packs them into a single byte: class in the high bits, change-up in bit
/// 4. Used both in CharSet[0] and in the Class field of the character-creation answer, which carry the same
/// format (DGCharacterCreateRecv builds the byte the same way as CharacterMakePreviewCharSet). The low four
/// bits are NOT part of this: the ViewState goes there.
struct ClassByte
{
    uint8_t CharacterClass = 0;
    uint8_t ChangeUp = 0;
};

/// Unpacks the class byte **of the CharSet**, where the class takes the three high bits (base * 32) because the
/// low bits carry the ViewState.
ClassByte DecodeClassByte(uint8_t value);

/// Unpacks the class byte **of the database**, which is a different packing: the base class goes in the high
/// nibble and the evolution in the low one (0, 16, 32, 48, 64 for DW, DK, FE, MG and DL -- exactly the
/// default_class_type rows of the server). Having two encodings coexist is not an oversight of the port: the
/// CharSet needs the low bits for something else, so it shifts the class one bit further. Using the CharSet one
/// here makes the server not find the class and reject the creation with "account full", a message that is no
/// help at all in finding the cause.
ClassByte DecodeDatabaseClassByte(uint8_t value);

/// Builds the database class byte from the base class index (0-4). New characters are always born without
/// evolution.
uint8_t MakeDatabaseClassByte(uint8_t baseClass);

/// Interpreta los trece bytes del CharSet.
Appearance DecodeCharSet(const uint8_t charSet[13]);

/// Writes the appearance into the 25-byte block consumed by ReadEquipmentExtended: three bytes for each weapon
/// and each armor piece, two for the wings and two for the pet.
void WriteExtendedEquipment(const Appearance& appearance, uint8_t equipment[ExtendedEquipmentSize]);

}  // namespace Mu099B

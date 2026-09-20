#include "ItemInfo099B.h"

namespace Mu099B
{

namespace
{

// Reparto de bits del byte 1. El nivel ocupa cuatro bits en el medio, con la
// suerte arriba y la habilidad abajo; los dos bits de excelente que quedan se
// completan con un tercero que viaja en el byte 3.
constexpr uint8_t LuckMask = 0x80;
constexpr uint8_t LevelMask = 0x78;
constexpr int LevelShift = 3;
constexpr uint8_t SkillMask = 0x04;
constexpr uint8_t OptionLowMask = 0x03;

// Reparto del byte 3. El bit alto es el noveno bit del índice: con secciones de
// 32 y dieciséis secciones el índice llega a 511, que no entra en el byte 0.
constexpr uint8_t IndexHighMask = 0x80;
constexpr uint8_t OptionHighMask = 0x40;
constexpr uint8_t ExcellentMask = 0x3F;

/// El tercer bit del nivel de opción vale 4, no 1: los otros dos son los de
/// menor peso.
constexpr uint8_t OptionHighValue = 4;

// Banderas del bloque del cliente (ItemOptionFlags en _enum.h). Se replican acá
// a propósito: este módulo no depende de los headers del cliente, así que los
// tests pueden verificarlo sin arrastrar medio motor. El test comprueba que
// coincidan con el enum.
constexpr uint8_t ClientFlagOption = 0x01;
constexpr uint8_t ClientFlagLuck = 0x02;
constexpr uint8_t ClientFlagSkill = 0x04;
constexpr uint8_t ClientFlagExcellent = 0x08;
constexpr uint8_t ClientFlagAncient = 0x10;

}  // namespace

ItemInfo DecodeItemInfo(const uint8_t bytes[ItemInfoSize])
{
    ItemInfo item;

    // Cinco ceros es el "sin item" del servidor. Hay que mirarlo antes de
    // desarmar nada: el índice 0 es un item válido (la primera espada), así que
    // un índice en cero por sí solo no distingue.
    bool allZero = true;
    for (size_t i = 0; i < ItemInfoSize; ++i)
    {
        if (bytes[i] != 0)
        {
            allZero = false;
            break;
        }
    }

    if (allZero)
    {
        return item;
    }

    item.Present = true;

    item.Index = static_cast<uint16_t>(
        bytes[0] | ((bytes[3] & IndexHighMask) != 0 ? 0x100 : 0));
    item.Group = static_cast<uint8_t>(item.Index / ItemsPerGroup);
    item.Number = static_cast<uint8_t>(item.Index % ItemsPerGroup);

    item.Level = static_cast<uint8_t>((bytes[1] & LevelMask) >> LevelShift);
    item.Luck = (bytes[1] & LuckMask) != 0;
    item.Skill = (bytes[1] & SkillMask) != 0;
    item.OptionLevel = static_cast<uint8_t>(bytes[1] & OptionLowMask);
    if ((bytes[3] & OptionHighMask) != 0)
    {
        item.OptionLevel = static_cast<uint8_t>(item.OptionLevel | OptionHighValue);
    }

    item.Durability = bytes[2];
    item.ExcellentFlags = static_cast<uint8_t>(bytes[3] & ExcellentMask);
    item.SetOption = bytes[4];

    return item;
}

void EncodeItemInfo(const ItemInfo& item, uint8_t bytes[ItemInfoSize])
{
    for (size_t i = 0; i < ItemInfoSize; ++i)
    {
        bytes[i] = 0;
    }

    if (!item.Present)
    {
        return;
    }

    bytes[0] = static_cast<uint8_t>(item.Index & 0xFF);

    bytes[1] = static_cast<uint8_t>(((item.Level << LevelShift) & LevelMask) |
                                    (item.Luck ? LuckMask : 0) |
                                    (item.Skill ? SkillMask : 0) |
                                    (item.OptionLevel & OptionLowMask));

    bytes[2] = item.Durability;

    bytes[3] = static_cast<uint8_t>(((item.Index & 0x100) != 0 ? IndexHighMask : 0) |
                                    (item.OptionLevel >= OptionHighValue ? OptionHighMask : 0) |
                                    (item.ExcellentFlags & ExcellentMask));

    bytes[4] = item.SetOption;
}

size_t WriteClientItemBlock(const ItemInfo& item, uint8_t block[ClientItemBlockSize])
{
    for (size_t i = 0; i < ClientItemBlockSize; ++i)
    {
        block[i] = 0;
    }

    if (!item.Present)
    {
        return 0;
    }

    // El cliente mete el grupo en el nibble alto y le deja doce bits al número,
    // que le sobran para los treinta y dos de 0.99B.
    block[0] = static_cast<uint8_t>((item.Group << 4) | ((item.Number >> 8) & 0x0F));
    block[1] = static_cast<uint8_t>(item.Number & 0xFF);
    block[2] = item.Level;
    block[3] = item.Durability;

    uint8_t flags = 0;
    size_t written = 5;

    if (item.Luck)
    {
        flags |= ClientFlagLuck;
    }
    if (item.Skill)
    {
        flags |= ClientFlagSkill;
    }

    if (item.OptionLevel != 0)
    {
        flags |= ClientFlagOption;
        // El tipo de opción va en el nibble alto; 0.99B tiene una sola, así que
        // queda en cero.
        block[written] = static_cast<uint8_t>(item.OptionLevel & 0x0F);
        ++written;
    }

    if (item.ExcellentFlags != 0)
    {
        flags |= ClientFlagExcellent;
        block[written] = item.ExcellentFlags;
        ++written;
    }

    if (item.SetOption != 0)
    {
        // El cliente lee este byte como "antiguo": discriminante en el nibble
        // bajo y bonus en el alto. 0.99B sólo usa el bajo.
        flags |= ClientFlagAncient;
        block[written] = static_cast<uint8_t>(item.SetOption & 0x0F);
        ++written;
    }

    block[4] = flags;
    return written;
}

uint32_t ToClientItemIndex(uint16_t wireIndex)
{
    const uint32_t group = wireIndex / ItemsPerGroup;
    const uint32_t number = wireIndex % ItemsPerGroup;
    return (group * ClientItemsPerGroup) + number;
}

bool ToWireItemIndex(uint32_t clientIndex, uint16_t& wireIndex)
{
    const uint32_t group = clientIndex / ClientItemsPerGroup;
    const uint32_t number = clientIndex % ClientItemsPerGroup;

    // Un número por encima de 32 es un item que 0.99B no tiene. Recortarlo
    // silenciosamente mandaría otro objeto, así que se avisa y el llamador
    // decide -- normalmente, no mandar nada.
    if (number >= static_cast<uint32_t>(ItemsPerGroup) ||
        group >= static_cast<uint32_t>(ItemSectionCount))
    {
        return false;
    }

    wireIndex = static_cast<uint16_t>((group * ItemsPerGroup) + number);
    return true;
}

bool DecodeDroppedMoney(const uint8_t bytes[ItemInfoSize], uint32_t& amount)
{
    // El índice se arma igual que en DecodeItemInfo: byte 0 más el noveno bit
    // que viaja en el byte 3.
    const uint16_t index = static_cast<uint16_t>(
        bytes[0] | ((bytes[3] & IndexHighMask) != 0 ? 0x100 : 0));

    if (index != ZenItemIndex)
    {
        return false;
    }

    // Inverso exacto de lo que arma el servidor: byte alto, medio y bajo del
    // monto repartidos en 1, 2 y 4.
    amount = (static_cast<uint32_t>(bytes[1]) << 16) |
             (static_cast<uint32_t>(bytes[2]) << 8) |
             static_cast<uint32_t>(bytes[4]);
    return true;
}

}  // namespace Mu099B

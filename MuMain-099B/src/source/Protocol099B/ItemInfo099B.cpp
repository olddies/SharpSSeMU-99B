#include "ItemInfo099B.h"

namespace Mu099B
{

namespace
{

// Bit layout of byte 1. The level takes four bits in the middle, with luck on top and skill below; the two
// excellent bits that remain are completed with a third that travels in byte 3.
constexpr uint8_t LuckMask = 0x80;
constexpr uint8_t LevelMask = 0x78;
constexpr int LevelShift = 3;
constexpr uint8_t SkillMask = 0x04;
constexpr uint8_t OptionLowMask = 0x03;

// Layout of byte 3. The high bit is the ninth bit of the index: with sections of 32 and sixteen sections the
// index reaches 511, which does not fit in byte 0.
constexpr uint8_t IndexHighMask = 0x80;
constexpr uint8_t OptionHighMask = 0x40;
constexpr uint8_t ExcellentMask = 0x3F;

/// The third bit of the option level is worth 4, not 1: the other two are the lowest-weight ones.
constexpr uint8_t OptionHighValue = 4;

// Flags of the client's block (ItemOptionFlags in _enum.h). They are replicated here on purpose: this module
// does not depend on the client's headers, so the tests can verify it without dragging in half the engine. The
// test checks that they match the enum.
constexpr uint8_t ClientFlagOption = 0x01;
constexpr uint8_t ClientFlagLuck = 0x02;
constexpr uint8_t ClientFlagSkill = 0x04;
constexpr uint8_t ClientFlagExcellent = 0x08;
constexpr uint8_t ClientFlagAncient = 0x10;

}  // namespace

ItemInfo DecodeItemInfo(const uint8_t bytes[ItemInfoSize])
{
    ItemInfo item;

    // Five zeros is the server's "no item". It has to be checked before unpacking anything: index 0 is a valid
    // item (the first sword), so a zero index alone does not tell them apart.
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

    // The client puts the group in the high nibble and leaves twelve bits for the number, which are more than
    // enough for 0.99B's thirty-two.
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
        // The option type goes in the high nibble; 0.99B has only one, so it stays at zero.
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
        // The client reads this byte as "ancient": discriminator in the low nibble and bonus in the high one.
        // 0.99B only uses the low one.
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

    // A number above 32 is an item that 0.99B does not have. Silently clipping it would send another object, so
    // it is reported and the caller decides -- normally, to send nothing.
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
    // The index is built the same as in DecodeItemInfo: byte 0 plus the ninth bit that travels in byte 3.
    const uint16_t index = static_cast<uint16_t>(
        bytes[0] | ((bytes[3] & IndexHighMask) != 0 ? 0x100 : 0));

    if (index != ZenItemIndex)
    {
        return false;
    }

    // Exact inverse of what the server builds: high, middle and low byte of the amount spread over 1, 2 and 4.
    amount = (static_cast<uint32_t>(bytes[1]) << 16) |
             (static_cast<uint32_t>(bytes[2]) << 8) |
             static_cast<uint32_t>(bytes[4]);
    return true;
}

}  // namespace Mu099B

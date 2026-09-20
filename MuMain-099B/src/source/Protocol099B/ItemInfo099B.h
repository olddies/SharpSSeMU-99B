// The five item bytes of 0.99B (MAX_ITEM_INFO), and the translation to the index the client's models use. These
// five bytes travel in everything that touches objects: inventory, item on the ground, move, buy, sell, shop.
// The later dialect uses twelve, with sockets and Jewel of Harmony that do not exist in this build. There are
// two item numberings coexisting, and the difference is only the stride: * **0.99B**: section * 32 + sub, nine
// bits in total (0-511). This is what travels over the network. * **Client**: group * 512 + number, which is
// what the models index. The sixteen sections are the same sixteen groups and in the same order (sword, axe,
// mace, spear, bow, staff, shield, helm, armor, pants, gloves, boots, wings, helper, potion, misc), so the
// conversion is just changing the stride -- there is no equivalence table. Like the rest of the module, it
// depends neither on the transport nor on the PCH: the tests verify the bytes without linking the network.

#pragma once

#include <cstddef>
#include <cstdint>

#include "CharSet099B.h"

namespace Mu099B
{

/// Bytes an item takes on the wire in this build (MAX_ITEM_INFO).
inline constexpr size_t ItemInfoSize = 5;

/// Item sections that 0.99B has (MAX_ITEM_SECTION), which are the same sixteen client groups in the same order.
inline constexpr int ItemSectionCount = 16;

/// How many items fit in a group on the client side. 0.99B's is ItemsPerGroup (32); this is the other half of
/// the conversion.
inline constexpr int ClientItemsPerGroup = 512;

/// Un item ya desarmado de los cinco bytes.
struct ItemInfo
{
    /// Five zero bytes mean "no item": the server only sends occupied slots, so this does not show up in the
    /// inventory, but it does in packets where the field is optional.
    bool Present = false;

    /// 0.99B index (section * 32 + sub, 0-511).
    uint16_t Index = 0;

    /// The same index unpacked, which is how the client wants it.
    uint8_t Group = 0;
    uint8_t Number = 0;

    uint8_t Level = 0;       ///< 0-15, el "+n" del item.
    uint8_t Durability = 0;
    bool Luck = false;
    bool Skill = false;
    /// The "+4 / +8 / +12 / +16": two bits in byte 1 and the third in 3. The server caps it at 4
    /// (ClientProtocolHandler: "Max +16 option").
    uint8_t OptionLevel = 0;

    /// Mask of excellent options, one bit per option. The server turns on the CharSet glow with (ExcellentFlags
    /// & 0x3F) != 0.
    uint8_t ExcellentFlags = 0;

    /// Set-item / antiguo, en el nibble bajo.
    uint8_t SetOption = 0;
};

/// Desarma los cinco bytes. Puerto de CItemManager::ItemByteConvert.
ItemInfo DecodeItemInfo(const uint8_t bytes[ItemInfoSize]);

/// Builds them back. An item with Present false writes five zeros. The full round trip is needed because
/// several client requests return the item they believe they are touching: the server uses them to confirm that
/// client and server are talking about the same object.
void EncodeItemInfo(const ItemInfo& item, uint8_t bytes[ItemInfoSize]);

/// Maximum size of the block WriteClientItemBlock builds: the five fixed bytes plus option, excellent and set.
inline constexpr size_t ClientItemBlockSize = 8;

/// Translates an item into the block of bytes the client's inventory consumes (ParseItemData): group and number
/// packed into a WORD, level, durability, flags, and then the optional fields that the flags announce. It is
/// the same trick as WriteExtendedEquipment: instead of touching the client's inventory, it is given what it
/// already knows how to read. Returns how many bytes it wrote.
size_t WriteClientItemBlock(const ItemInfo& item, uint8_t block[ClientItemBlockSize]);

/// 0.99B index -> client index. The stride changes from 32 to 512.
uint32_t ToClientItemIndex(uint16_t wireIndex);

/// Client index -> 0.99B index. Returns false if the number inside the group does not fit in the 32 a section
/// has, which is what happens with items added in later seasons: they have no equivalent here and sending them
/// anyway would make the server interpret another object.
bool ToWireItemIndex(uint32_t clientIndex, uint16_t& wireIndex);

/// The pile of zen on the ground: section 14, sub 15 (GET_ITEM(14,15) of the emulator).
inline constexpr uint16_t ZenItemIndex = 14 * ItemsPerGroup + 15;

/// Unpacks a pile of zen from the ground. The server does NOT use the normal item format for money: it sends
/// the zen index and puts the amount raw in bytes 1, 2 and 4 (CViewport::GCViewportItemSend,
/// Viewport.cpp:903-911), so passing it through DecodeItemInfo returns invented level and durability instead of
/// the amount. Returns false if these five bytes are not a pile of zen, and in that case it does not touch
/// `amount`.
bool DecodeDroppedMoney(const uint8_t bytes[ItemInfoSize], uint32_t& amount);

}  // namespace Mu099B

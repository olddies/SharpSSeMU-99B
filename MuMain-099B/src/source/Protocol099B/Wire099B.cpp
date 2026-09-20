#include "Wire099B.h"

#include <cstring>

namespace Mu099B
{

namespace
{

/// In 2-byte headers the size is big-endian: high byte first (SET_NUMBERHB / SET_NUMBERLB in the original).
void WriteWordSize(BYTE* field, WORD size)
{
    field[0] = static_cast<BYTE>((size >> 8) & 0xFF);
    field[1] = static_cast<BYTE>(size & 0xFF);
}

BYTE TypeByteSize(bool encrypted)
{
    return static_cast<BYTE>(encrypted ? PacketType::EncryptedByteSize
                                       : PacketType::PlainByteSize);
}

BYTE TypeWordSize(bool encrypted)
{
    return static_cast<BYTE>(encrypted ? PacketType::EncryptedWordSize
                                       : PacketType::PlainWordSize);
}

}  // namespace

void SetHeader(PBMSG_HEAD& header, BYTE head, BYTE size, bool encrypted)
{
    header.type = TypeByteSize(encrypted);
    header.size = size;
    header.head = head;
}

void SetHeader(PSBMSG_HEAD& header, BYTE head, BYTE sub, BYTE size, bool encrypted)
{
    header.type = TypeByteSize(encrypted);
    header.size = size;
    header.head = head;
    header.subh = sub;
}

void SetHeader(PWMSG_HEAD& header, BYTE head, WORD size, bool encrypted)
{
    header.type = TypeWordSize(encrypted);
    WriteWordSize(header.size, size);
    header.head = head;
}

void SetHeader(PSWMSG_HEAD& header, BYTE head, BYTE sub, WORD size, bool encrypted)
{
    header.type = TypeWordSize(encrypted);
    WriteWordSize(header.size, size);
    header.head = head;
    header.subh = sub;
}

PMSG_SERVER_LIST_RECV BuildServerListRequest()
{
    PMSG_SERVER_LIST_RECV packet{};
    SetHeader(packet.header, PMSG_SERVER_LIST_RECV::kHead, PMSG_SERVER_LIST_RECV::kSub,
              sizeof(packet));
    return packet;
}

PMSG_SERVER_INFO_RECV BuildServerInfoRequest(BYTE serverCode)
{
    PMSG_SERVER_INFO_RECV packet{};
    SetHeader(packet.header, PMSG_SERVER_INFO_RECV::kHead, PMSG_SERVER_INFO_RECV::kSub,
              sizeof(packet));
    packet.ServerCode = serverCode;
    return packet;
}

namespace
{

/// Tabla del XOR de argumentos (PacketArgumentDecrypt, Util.cpp).
constexpr BYTE ArgumentXor[3] = {0xFC, 0xCF, 0xAB};

/// Copies a text into a fixed-length field, trimming and padding with zeros. The server reads these fields as
/// fixed-length strings, so what is left over has to be zero and not stack garbage.
void WriteFixedString(char* field, size_t fieldSize, const char* text)
{
    std::memset(field, 0, fieldSize);
    if (text == nullptr)
    {
        return;
    }
    const size_t length = std::strlen(text);
    std::memcpy(field, text, length < fieldSize ? length : fieldSize);
}

}  // namespace

void ApplyArgumentCipher(BYTE* buffer, size_t length)
{
    for (size_t n = 0; n < length; ++n)
    {
        buffer[n] ^= ArgumentXor[n % 3];
    }
}

PMSG_CONNECT_ACCOUNT_RECV BuildLoginRequest(const char* account, const char* password,
                                            DWORD tickCount, const BYTE* clientVersion,
                                            const BYTE* clientSerial)
{
    PMSG_CONNECT_ACCOUNT_RECV packet{};
    SetHeader(packet.header, PMSG_CONNECT_ACCOUNT_RECV::kHead, PMSG_CONNECT_ACCOUNT_RECV::kSub,
              sizeof(packet), /*encrypted=*/true);

    WriteFixedString(packet.account, sizeof(packet.account), account);
    WriteFixedString(packet.password, sizeof(packet.password), password);
    ApplyArgumentCipher(reinterpret_cast<BYTE*>(packet.account), sizeof(packet.account));
    ApplyArgumentCipher(reinterpret_cast<BYTE*>(packet.password), sizeof(packet.password));

    packet.TickCount = tickCount;
    std::memcpy(packet.ClientVersion, clientVersion, sizeof(packet.ClientVersion));
    std::memcpy(packet.ClientSerial, clientSerial, sizeof(packet.ClientSerial));
    return packet;
}

PSBMSG_HEAD BuildCharacterListRequest()
{
    PSBMSG_HEAD header{};
    SetHeader(header, 0xF3, 0x00, sizeof(header));
    return header;
}

PMSG_CHARACTER_CREATE_RECV BuildCharacterCreateRequest(const char* name, BYTE characterClass)
{
    PMSG_CHARACTER_CREATE_RECV packet{};
    SetHeader(packet.header, PMSG_CHARACTER_CREATE_RECV::kHead,
              PMSG_CHARACTER_CREATE_RECV::kSub, sizeof(packet));
    WriteFixedString(packet.name, sizeof(packet.name), name);
    packet.Class = characterClass;
    return packet;
}

PMSG_CHARACTER_DELETE_RECV BuildCharacterDeleteRequest(const char* name, const char* personalCode)
{
    PMSG_CHARACTER_DELETE_RECV packet{};
    SetHeader(packet.header, PMSG_CHARACTER_DELETE_RECV::kHead,
              PMSG_CHARACTER_DELETE_RECV::kSub, sizeof(packet));
    WriteFixedString(packet.name, sizeof(packet.name), name);
    WriteFixedString(packet.PersonalCode, sizeof(packet.PersonalCode), personalCode);
    return packet;
}

PMSG_CHARACTER_INFO_RECV BuildCharacterSelectRequest(const char* name)
{
    PMSG_CHARACTER_INFO_RECV packet{};
    SetHeader(packet.header, PMSG_CHARACTER_INFO_RECV::kHead, PMSG_CHARACTER_INFO_RECV::kSub,
              sizeof(packet));
    WriteFixedString(packet.name, sizeof(packet.name), name);
    return packet;
}

MoveRequest BuildMoveRequest(BYTE x, BYTE y, BYTE direction, const BYTE* steps, size_t stepCount)
{
    if (stepCount > MaxMoveSteps)
    {
        stepCount = MaxMoveSteps;
    }

    MoveRequest request{};
    std::memset(request.Data, 0, sizeof(request.Data));

    auto* packet = reinterpret_cast<PMSG_MOVE_RECV*>(request.Data);
    packet->x = x;
    packet->y = y;

    // path[0]: initial direction in the high nibble, number of steps in the low one.
    packet->path[0] = static_cast<BYTE>(((direction & 0x0F) << 4) | (stepCount & 0x0F));

    // From there on, two directions per byte. Step n (counting from 1) goes in byte (n+1)/2: high nibble if n
    // is odd, low if even.
    for (size_t n = 1; n <= stepCount; ++n)
    {
        const size_t byteIndex = (n + 1) / 2;
        const BYTE step = static_cast<BYTE>(steps[n - 1] & 0x0F);

        if ((n % 2) == 1)
        {
            packet->path[byteIndex] |= static_cast<BYTE>(step << 4);
        }
        else
        {
            packet->path[byteIndex] |= step;
        }
    }

    // Only up to the last actually used path byte is sent.
    const size_t pathBytes = 1 + ((stepCount + 1) / 2);
    const auto length = static_cast<BYTE>(offsetof(PMSG_MOVE_RECV, path) + pathBytes);
    SetHeader(packet->header, PMSG_MOVE_RECV::kHead, length);
    request.Length = length;
    return request;
}

PMSG_POSITION_RECV BuildPositionRequest(BYTE x, BYTE y)
{
    PMSG_POSITION_RECV packet{};
    SetHeader(packet.header, PMSG_POSITION_RECV::kHead, sizeof(packet));
    packet.x = x;
    packet.y = y;
    return packet;
}

PMSG_CHAT_RECV BuildChatRequest(const char* name, const char* message)
{
    PMSG_CHAT_RECV packet{};
    SetHeader(packet.header, PMSG_CHAT_RECV::kHead, sizeof(packet));
    WriteFixedString(packet.name, sizeof(packet.name), name);
    WriteFixedString(packet.message, sizeof(packet.message), message);
    return packet;
}

PMSG_CHAT_WHISPER_RECV BuildWhisperRequest(const char* targetName, const char* message)
{
    PMSG_CHAT_WHISPER_RECV packet{};
    SetHeader(packet.header, PMSG_CHAT_WHISPER_RECV::kHead, sizeof(packet));
    WriteFixedString(packet.name, sizeof(packet.name), targetName);
    WriteFixedString(packet.message, sizeof(packet.message), message);
    return packet;
}

PMSG_ATTACK_RECV BuildAttackRequest(WORD targetIndex, BYTE action, BYTE direction)
{
    PMSG_ATTACK_RECV packet{};
    SetHeader(packet.header, PMSG_ATTACK_RECV::kHead, sizeof(packet));
    // Object indices are big-endian throughout the protocol.
    packet.index[0] = static_cast<BYTE>((targetIndex >> 8) & 0xFF);
    packet.index[1] = static_cast<BYTE>(targetIndex & 0xFF);
    packet.action = action;
    packet.dir = direction;
    return packet;
}

PMSG_ACTION_RECV BuildActionRequest(BYTE direction, BYTE action, WORD targetIndex)
{
    PMSG_ACTION_RECV packet{};
    SetHeader(packet.header, PMSG_ACTION_RECV::kHead, sizeof(packet));
    packet.dir = direction;
    packet.action = action;
    packet.index[0] = static_cast<BYTE>((targetIndex >> 8) & 0xFF);
    packet.index[1] = static_cast<BYTE>(targetIndex & 0xFF);
    return packet;
}

namespace
{

/// Object indices travel big-endian throughout the protocol.
void WriteObjectIndex(BYTE* field, WORD index)
{
    field[0] = static_cast<BYTE>((index >> 8) & 0xFF);
    field[1] = static_cast<BYTE>(index & 0xFF);
}

}  // namespace

PMSG_ITEM_MOVE_RECV BuildItemMoveRequest(ItemContainer sourceContainer, BYTE sourceSlot,
                                         const BYTE* itemInfo, ItemContainer targetContainer,
                                         BYTE targetSlot)
{
    PMSG_ITEM_MOVE_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_MOVE_RECV::kHead, sizeof(packet));
    packet.SourceFlag = static_cast<BYTE>(sourceContainer);
    packet.SourceSlot = sourceSlot;
    if (itemInfo != nullptr)
    {
        std::memcpy(packet.ItemInfo, itemInfo, sizeof(packet.ItemInfo));
    }
    packet.TargetFlag = static_cast<BYTE>(targetContainer);
    packet.TargetSlot = targetSlot;
    return packet;
}

PMSG_ITEM_USE_RECV BuildItemUseRequest(BYTE sourceSlot, BYTE targetSlot, BYTE type)
{
    PMSG_ITEM_USE_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_USE_RECV::kHead, sizeof(packet));
    packet.SourceSlot = sourceSlot;
    packet.TargetSlot = targetSlot;
    packet.type = type;
    return packet;
}

PMSG_ITEM_DROP_RECV BuildItemDropRequest(BYTE x, BYTE y, BYTE slot)
{
    PMSG_ITEM_DROP_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_DROP_RECV::kHead, sizeof(packet));
    packet.x = x;
    packet.y = y;
    packet.slot = slot;
    return packet;
}

PMSG_ITEM_GET_RECV BuildItemGetRequest(WORD groundIndex)
{
    PMSG_ITEM_GET_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_GET_RECV::kHead, sizeof(packet));
    WriteObjectIndex(packet.index, groundIndex);
    return packet;
}

PMSG_SKILL_ATTACK_RECV BuildSkillAttackRequest(BYTE skill, WORD targetIndex, BYTE distance)
{
    PMSG_SKILL_ATTACK_RECV packet{};
    SetHeader(packet.header, PMSG_SKILL_ATTACK_RECV::kHead, sizeof(packet));
    packet.skill = skill;
    WriteObjectIndex(packet.index, targetIndex);
    packet.dis = distance;
    return packet;
}

PMSG_PARTY_REQUEST_RECV BuildPartyRequest(WORD targetIndex)
{
    PMSG_PARTY_REQUEST_RECV packet{};
    SetHeader(packet.header, PMSG_PARTY_REQUEST_RECV::kHead, sizeof(packet));
    WriteObjectIndex(packet.index, targetIndex);
    return packet;
}

PMSG_PARTY_REQUEST_RESULT_RECV BuildPartyRequestResult(bool accepted, WORD inviterIndex)
{
    PMSG_PARTY_REQUEST_RESULT_RECV packet{};
    SetHeader(packet.header, PMSG_PARTY_REQUEST_RESULT_RECV::kHead, sizeof(packet));
    packet.result = accepted ? 1 : 0;
    WriteObjectIndex(packet.index, inviterIndex);
    return packet;
}

PMSG_PARTY_DEL_MEMBER_RECV BuildPartyDeleteMember(BYTE memberNumber)
{
    PMSG_PARTY_DEL_MEMBER_RECV packet{};
    SetHeader(packet.header, PMSG_PARTY_DEL_MEMBER_RECV::kHead, sizeof(packet));
    packet.number = memberNumber;
    return packet;
}

void WriteItemInfo(BYTE* dst, const ItemWire& item)
{
    std::memset(dst, 0, ItemInfoSize);

    dst[0] = static_cast<BYTE>(item.Index & 0xFF);

    dst[1] = static_cast<BYTE>((item.Level * 8) | (item.Option1 * 128) | (item.Option2 * 4) |
                               (item.Option3 & 3));

    dst[2] = item.Durability;

    // Bit 8 of the index goes here, not in dst[0].
    dst[3] = static_cast<BYTE>(((item.Index & 256) >> 1) | (item.Option3 > 3 ? 64 : 0) |
                               item.NewOption);

    dst[4] = item.SetOption;
}

PMSG_NPC_TALK_RECV BuildNpcTalkRequest(WORD npcIndex)
{
    PMSG_NPC_TALK_RECV packet{};
    SetHeader(packet.header, PMSG_NPC_TALK_RECV::kHead, sizeof(packet));
    WriteObjectIndex(packet.index, npcIndex);
    return packet;
}

PBMSG_HEAD BuildNpcCloseRequest()
{
    // No body: the opcode is enough for the server. The head is written by hand because there is no struct of
    // its own for a packet that is just a header.
    constexpr BYTE CloseNpcHead = 0x31;

    PBMSG_HEAD header{};
    SetHeader(header, CloseNpcHead, sizeof(header));
    return header;
}

PMSG_ITEM_BUY_RECV BuildItemBuyRequest(BYTE slot)
{
    PMSG_ITEM_BUY_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_BUY_RECV::kHead, sizeof(packet));
    packet.slot = slot;
    return packet;
}

PMSG_ITEM_SELL_RECV BuildItemSellRequest(BYTE slot)
{
    PMSG_ITEM_SELL_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_SELL_RECV::kHead, sizeof(packet));
    packet.slot = slot;
    return packet;
}

PMSG_ITEM_REPAIR_RECV BuildItemRepairRequest(BYTE slot, BYTE type)
{
    PMSG_ITEM_REPAIR_RECV packet{};
    SetHeader(packet.header, PMSG_ITEM_REPAIR_RECV::kHead, sizeof(packet));
    packet.slot = slot;
    packet.type = type;
    return packet;
}

PMSG_TELEPORT_RECV BuildTeleportRequest(BYTE gate, BYTE x, BYTE y)
{
    PMSG_TELEPORT_RECV packet{};
    SetHeader(packet.header, PMSG_TELEPORT_RECV::kHead, sizeof(packet));
    packet.gate = gate;
    packet.x = x;
    packet.y = y;
    return packet;
}

PMSG_LIVE_CLIENT_RECV BuildLiveClientRequest(DWORD tickCount, WORD physicalSpeed,
                                             WORD magicSpeed)
{
    PMSG_LIVE_CLIENT_RECV packet{};
    SetHeader(packet.header, PMSG_LIVE_CLIENT_RECV::kHead, sizeof(packet));
    packet.TickCount = tickCount;
    packet.PhysiSpeed = physicalSpeed;
    packet.MagicSpeed = magicSpeed;
    return packet;
}

namespace
{

/// Writes a big-endian DWORD. Several fields declared as DWORD travel this way, built by hand with SET_NUMBER*:
/// assigning them as native integers reverses them and the server reads another figure.
void WriteBigEndianDword(BYTE* dst, DWORD value)
{
    dst[0] = static_cast<BYTE>((value >> 24) & 0xFF);
    dst[1] = static_cast<BYTE>((value >> 16) & 0xFF);
    dst[2] = static_cast<BYTE>((value >> 8) & 0xFF);
    dst[3] = static_cast<BYTE>(value & 0xFF);
}

}  // namespace

PMSG_TRADE_REQUEST_RECV BuildTradeRequest(WORD targetIndex)
{
    PMSG_TRADE_REQUEST_RECV packet{};
    SetHeader(packet.header, PMSG_TRADE_REQUEST_RECV::kHead, sizeof(packet));
    WriteObjectIndex(packet.index, targetIndex);
    return packet;
}

PMSG_TRADE_RESPONSE_RECV BuildTradeResponse(bool accepted)
{
    PMSG_TRADE_RESPONSE_RECV packet{};
    SetHeader(packet.header, PMSG_TRADE_RESPONSE_RECV::kHead, sizeof(packet));
    packet.response = accepted ? 1 : 0;
    return packet;
}

// The packet is built byte by byte instead of with the generated struct because the money DWORD is aligned to 4
// in memory (sizeof(PMSG_TRADE_MONEY_RECV) == 8) but travels right after the header, without that padding. The
// head does come from the generated struct: it was hand-written as 0x3B --the value of the emulator's wrong
// Trade.h comment-- and the real one is 0x3A, which is what Protocol.cpp dispatches and what protogen
// generates.
TradeMoneyRequest BuildTradeMoneyRequest(DWORD money)
{
    TradeMoneyRequest request{};
    request.Data[0] = 0xC1;
    request.Data[1] = static_cast<BYTE>(TradeMoneyRequest::Length);
    request.Data[2] = PMSG_TRADE_MONEY_RECV::kHead;
    WriteBigEndianDword(&request.Data[3], money);
    return request;
}

PMSG_TRADE_OK_BUTTON_RECV BuildTradeOkButton(bool ready)
{
    PMSG_TRADE_OK_BUTTON_RECV packet{};
    SetHeader(packet.header, PMSG_TRADE_OK_BUTTON_RECV::kHead, sizeof(packet));
    packet.flag = ready ? 1 : 0;
    return packet;
}

PBMSG_HEAD BuildTradeCancelRequest()
{
    constexpr BYTE TradeCancelHead = 0x3D;

    PBMSG_HEAD header{};
    SetHeader(header, TradeCancelHead, sizeof(header));
    return header;
}

PMSG_WAREHOUSE_MONEY_RECV BuildWarehouseMoneyRequest(BYTE type, DWORD money)
{
    PMSG_WAREHOUSE_MONEY_RECV packet{};
    SetHeader(packet.header, PMSG_WAREHOUSE_MONEY_RECV::kHead, sizeof(packet));
    packet.type = type;

    // The field is declared DWORD but filled byte by byte: the server reads it big-endian.
    WriteBigEndianDword(reinterpret_cast<BYTE*>(&packet.money), money);
    return packet;
}

PBMSG_HEAD BuildWarehouseCloseRequest()
{
    constexpr BYTE WarehouseCloseHead = 0x82;

    PBMSG_HEAD header{};
    SetHeader(header, WarehouseCloseHead, sizeof(header));
    return header;
}

PMSG_WAREHOUSE_PASSWORD_RECV BuildWarehousePasswordRequest(BYTE type, WORD password,
                                                           const char* personalCode)
{
    PMSG_WAREHOUSE_PASSWORD_RECV packet{};
    SetHeader(packet.header, PMSG_WAREHOUSE_PASSWORD_RECV::kHead, sizeof(packet));
    packet.type = type;
    packet.password = password;
    WriteFixedString(packet.PersonalCode, sizeof(packet.PersonalCode), personalCode);
    return packet;
}

MultiSkillRequest BuildMultiSkillRequest(BYTE skill, BYTE x, BYTE y, BYTE serial,
                                         const WORD* targets, size_t targetCount)
{
    constexpr BYTE MultiSkillHead = 0x1D;
    constexpr size_t HeaderSize = 8;

    if (targetCount > MultiSkillRequest::MaxTargets)
    {
        // Trimming is preferable to writing outside the buffer; the server does not process more than a handful
        // per hit either.
        targetCount = MultiSkillRequest::MaxTargets;
    }

    MultiSkillRequest request{};
    request.Length = HeaderSize + targetCount * 2;

    request.Data[0] = 0xC1;
    request.Data[1] = static_cast<BYTE>(request.Length);
    request.Data[2] = MultiSkillHead;
    request.Data[3] = skill;
    request.Data[4] = x;
    request.Data[5] = y;
    request.Data[6] = serial;
    request.Data[7] = static_cast<BYTE>(targetCount);

    for (size_t i = 0; i < targetCount; ++i)
    {
        WriteObjectIndex(&request.Data[HeaderSize + i * 2], targets[i]);
    }

    return request;
}

PMSG_DURATION_SKILL_ATTACK_RECV BuildDurationSkillRequest(BYTE skill, BYTE x, BYTE y,
                                                          BYTE direction, WORD targetIndex,
                                                          BYTE magicKey)
{
    PMSG_DURATION_SKILL_ATTACK_RECV packet{};
    SetHeader(packet.header, PMSG_DURATION_SKILL_ATTACK_RECV::kHead, sizeof(packet));
    packet.skill = skill;
    packet.x = x;
    packet.y = y;
    packet.dir = direction;
    WriteObjectIndex(packet.index, targetIndex);
    packet.MagicKey = magicKey;
    return packet;
}

PMSG_FRIEND_REQUEST_RECV BuildFriendAddRequest(const char* name)
{
    PMSG_FRIEND_REQUEST_RECV packet{};
    SetHeader(packet.header, PMSG_FRIEND_REQUEST_RECV::kHead, sizeof(packet));
    WriteFixedString(packet.Name, sizeof(packet.Name), name);
    return packet;
}

PMSG_FRIEND_RESULT_RECV BuildFriendResultRequest(bool accepted, const char* name)
{
    PMSG_FRIEND_RESULT_RECV packet{};
    SetHeader(packet.header, PMSG_FRIEND_RESULT_RECV::kHead, sizeof(packet));
    packet.result = accepted ? 1 : 0;
    WriteFixedString(packet.Name, sizeof(packet.Name), name);
    return packet;
}

PMSG_FRIEND_DELETE_RECV BuildFriendDeleteRequest(const char* name)
{
    PMSG_FRIEND_DELETE_RECV packet{};
    SetHeader(packet.header, PMSG_FRIEND_DELETE_RECV::kHead, sizeof(packet));
    WriteFixedString(packet.Name, sizeof(packet.Name), name);
    return packet;
}

PBMSG_HEAD BuildFriendListRequest()
{
    constexpr BYTE FriendListHead = 0xC0;

    PBMSG_HEAD header{};
    SetHeader(header, FriendListHead, sizeof(header));
    return header;
}

PMSG_GUILD_MASTER_OPEN_RECV BuildGuildMasterOpenRequest(BYTE result)
{
    PMSG_GUILD_MASTER_OPEN_RECV packet{};
    SetHeader(packet.header, PMSG_GUILD_MASTER_OPEN_RECV::kHead, sizeof(packet));
    packet.result = result;
    return packet;
}

PMSG_GUILD_CREATE_RECV BuildGuildCreateRequest(const char* guildName, const BYTE* mark)
{
    PMSG_GUILD_CREATE_RECV packet{};
    SetHeader(packet.header, PMSG_GUILD_CREATE_RECV::kHead, sizeof(packet));
    WriteFixedString(packet.GuildName, sizeof(packet.GuildName), guildName);

    if (mark != nullptr)
    {
        std::memcpy(packet.Mark, mark, sizeof(packet.Mark));
    }

    return packet;
}

PMSG_LEVEL_UP_POINT_RECV BuildLevelUpPointRequest(BYTE type)
{
    PMSG_LEVEL_UP_POINT_RECV packet{};
    SetHeader(packet.header, PMSG_LEVEL_UP_POINT_RECV::kHead, PMSG_LEVEL_UP_POINT_RECV::kSub,
              sizeof(packet));
    packet.type = type;
    return packet;
}

PSBMSG_HEAD BuildViewportEnableRequest()
{
    constexpr BYTE ViewportEnableHead = 0xF3;
    constexpr BYTE ViewportEnableSub = 0x12;

    PSBMSG_HEAD header{};
    SetHeader(header, ViewportEnableHead, ViewportEnableSub, sizeof(header));
    return header;
}

PBMSG_HEAD BuildPartyListRequest()
{
    constexpr BYTE PartyListHead = 0x42;

    PBMSG_HEAD header{};
    SetHeader(header, PartyListHead, sizeof(header));
    return header;
}

PBMSG_HEAD BuildGuildListRequest()
{
    constexpr BYTE GuildListHead = 0x52;

    PBMSG_HEAD header{};
    SetHeader(header, GuildListHead, sizeof(header));
    return header;
}

PMSG_CHAOS_MIX_RECV BuildChaosMixRequest(BYTE type, BYTE info)
{
    PMSG_CHAOS_MIX_RECV packet{};
    SetHeader(packet.header, PMSG_CHAOS_MIX_RECV::kHead, sizeof(packet));
    packet.type = type;
    packet.info = info;
    return packet;
}

PMSG_CHAOS_MIX_RATE_RECV BuildChaosMixRateRequest(DWORD type)
{
    PMSG_CHAOS_MIX_RATE_RECV packet{};
    SetHeader(packet.header, PMSG_CHAOS_MIX_RATE_RECV::kHead, sizeof(packet));
    packet.type = type;
    return packet;
}

PMSG_DEVIL_SQUARE_ENTER_RECV BuildDevilSquareEnterRequest(BYTE level, BYTE slot)
{
    PMSG_DEVIL_SQUARE_ENTER_RECV packet{};
    SetHeader(packet.header, PMSG_DEVIL_SQUARE_ENTER_RECV::kHead, sizeof(packet));
    packet.level = level;
    packet.slot = slot;
    return packet;
}

PMSG_EVENT_REMAIN_TIME_RECV BuildEventRemainTimeRequest(BYTE eventType, BYTE itemLevel)
{
    PMSG_EVENT_REMAIN_TIME_RECV packet{};
    SetHeader(packet.header, PMSG_EVENT_REMAIN_TIME_RECV::kHead, sizeof(packet));
    packet.EventType = eventType;
    packet.ItemLevel = itemLevel;
    return packet;
}

PMSG_BLOOD_CASTLE_ENTER_RECV BuildBloodCastleEnterRequest(BYTE level, BYTE slot)
{
    PMSG_BLOOD_CASTLE_ENTER_RECV packet{};
    SetHeader(packet.header, PMSG_BLOOD_CASTLE_ENTER_RECV::kHead, sizeof(packet));
    packet.level = level;
    packet.slot = slot;
    return packet;
}

PMSG_QUEST_STATE_RECV BuildQuestStateRequest(BYTE questIndex, BYTE questState)
{
    PMSG_QUEST_STATE_RECV packet{};
    SetHeader(packet.header, PMSG_QUEST_STATE_RECV::kHead, sizeof(packet));
    packet.QuestIndex = questIndex;
    packet.QuestState = questState;
    return packet;
}

PMSG_PET_ITEM_INFO_RECV BuildPetItemInfoRequest(BYTE type, BYTE flag, BYTE slot)
{
    PMSG_PET_ITEM_INFO_RECV packet{};
    SetHeader(packet.header, PMSG_PET_ITEM_INFO_RECV::kHead, sizeof(packet));
    packet.type = type;
    packet.flag = flag;
    packet.slot = slot;
    return packet;
}

PMSG_ITEM_REPAIR_RECV BuildSelfRepairRequest(BYTE slot, BYTE type)
{
    constexpr BYTE SelfRepairHead = 0x35;

    PMSG_ITEM_REPAIR_RECV packet{};
    SetHeader(packet.header, SelfRepairHead, sizeof(packet));
    packet.slot = slot;
    packet.type = type;
    return packet;
}

PBMSG_HEAD BuildChaosMixCloseRequest()
{
    constexpr BYTE ChaosMixCloseHead = 0x87;

    PBMSG_HEAD header{};
    SetHeader(header, ChaosMixCloseHead, sizeof(header));
    return header;
}

TeleportMoveRequest BuildTeleportMoveRequest(WORD moveIndex)
{
    constexpr BYTE TeleportMoveHead = 0x8E;

    TeleportMoveRequest request{};
    request.Data[0] = 0xC1;
    request.Data[1] = static_cast<BYTE>(TeleportMoveRequest::Length);
    request.Data[2] = TeleportMoveHead;

    // Bytes 3 to 7 stay at zero: the server does not look at them. The number goes at the end, which is where
    // it looks first.
    WriteObjectIndex(&request.Data[8], moveIndex);
    return request;
}

PBMSG_HEAD BuildQuestInfoRequest()
{
    constexpr BYTE QuestInfoHead = 0xA0;

    PBMSG_HEAD header{};
    SetHeader(header, QuestInfoHead, sizeof(header));
    return header;
}

PMSG_HARDWARE_ID_INFO_RECV BuildHardwareIdRequest(const char* hardwareId)
{
    PMSG_HARDWARE_ID_INFO_RECV packet{};
    SetHeader(packet.header, PMSG_HARDWARE_ID_INFO_RECV::kHead,
              PMSG_HARDWARE_ID_INFO_RECV::kSub, sizeof(packet));
    WriteFixedString(packet.HardwareId, sizeof(packet.HardwareId), hardwareId);
    return packet;
}

}  // namespace Mu099B

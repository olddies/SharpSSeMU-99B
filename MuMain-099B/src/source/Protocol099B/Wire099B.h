// Wire layer of the SSeMU 0.99B protocol: building headers and packets. The structs come from
// Protocol099B.generated.h, derived from the emulator sources (see tools/protogen/README.md). No layout is
// written by hand here: only the header is stamped and fields are filled. This file depends neither on the
// transport nor on the client's precompiled header, so the tests can compile it on its own and verify the exact
// bytes that go out to the socket. Sending lives in Send099B.h.

#pragma once

#include <cstddef>

#include "ItemInfo099B.h"  // ItemInfoSize y el desarmado de los cinco bytes
#include "Protocol099B.generated.h"

namespace Mu099B
{

/// Wire packet types. The low bit distinguishes "1-byte size" (C1/C3) from "2-byte size" (C2/C4); the high pair
/// is the encrypted variant.
enum class PacketType : BYTE
{
    PlainByteSize = 0xC1,
    PlainWordSize = 0xC2,
    EncryptedByteSize = 0xC3,
    EncryptedWordSize = 0xC4,
};

/// Stamps a PBMSG_HEAD (1-byte size, no sub-code).
void SetHeader(PBMSG_HEAD& header, BYTE head, BYTE size, bool encrypted = false);

/// Stamps a PSBMSG_HEAD (1-byte size, with sub-code).
void SetHeader(PSBMSG_HEAD& header, BYTE head, BYTE sub, BYTE size, bool encrypted = false);

/// Stamps a PWMSG_HEAD (2-byte size, no sub-code).
void SetHeader(PWMSG_HEAD& header, BYTE head, WORD size, bool encrypted = false);

/// Stamps a PSWMSG_HEAD (2-byte size, with sub-code).
void SetHeader(PSWMSG_HEAD& header, BYTE head, BYTE sub, WORD size, bool encrypted = false);

// -- ConnectServer ---------------------------------------------------------

/// C1:F4:02 -- pide la lista de servidores.
PMSG_SERVER_LIST_RECV BuildServerListRequest();

/// C1:F4:03 -- asks for the IP and port of a ServerCode. Note: in this build the server code travels as a BYTE,
/// not a WORD.
PMSG_SERVER_INFO_RECV BuildServerInfoRequest(BYTE serverCode);

// -- Login y personajes ----------------------------------------------------

/// 3-byte XOR that protects account and password inside the login (port of PacketArgumentDecrypt, Util.cpp). It
/// is an involution: the same function encrypts and decrypts.
void ApplyArgumentCipher(BYTE* buffer, size_t length);

/// C3:F1:01 -- login. It goes block-encrypted, hence the 0xC3. `clientVersion` is 5 bytes and `clientSerial`
/// 16; both have to match the server's .ini exactly or it bounces with "wrong version". Account and password
/// are trimmed to 10 bytes and passed through the argument XOR.
PMSG_CONNECT_ACCOUNT_RECV BuildLoginRequest(const char* account, const char* password,
                                            DWORD tickCount, const BYTE* clientVersion,
                                            const BYTE* clientSerial);

/// C1:F3:00 -- pide la lista de personajes. No lleva cuerpo.
PSBMSG_HEAD BuildCharacterListRequest();

/// C1:F3:01 -- crea un personaje.
PMSG_CHARACTER_CREATE_RECV BuildCharacterCreateRequest(const char* name, BYTE characterClass);

/// C1:F3:02 -- deletes a character. `personalCode` is the account's security code (10 bytes).
PMSG_CHARACTER_DELETE_RECV BuildCharacterDeleteRequest(const char* name, const char* personalCode);

/// C1:F3:03 -- enters the world with the character `name`.
PMSG_CHARACTER_INFO_RECV BuildCharacterSelectRequest(const char* name);

// -- World: movement and position -------------------------------------------

/// Maximum number of steps that fit in a move request. The counter is a nibble, so in theory it could reach 15
/// -- but path[] is 8 bytes and the first one is taken by direction + counter, leaving 7 for the directions at
/// two per byte: 14. Asking for 15 would overrun the struct.
inline constexpr size_t MaxMoveSteps = 14;

/// An already serialised move request. The length is VARIABLE: the struct declares path[8] but the client only
/// sends the bytes it needs according to the step count. Always sending all 8 is not incorrect on the wire, but
/// reading too much does break anyone assuming a fixed length -- in fact it is the bug that took the server
/// down with real packets.
struct MoveRequest
{
    BYTE Data[sizeof(PMSG_MOVE_RECV)];
    BYTE Length;
};

/// C1:D7 -- asks to move to (x, y) following `steps` directions (0-7 each). `direction` is where the character
/// faces when starting.
MoveRequest BuildMoveRequest(BYTE x, BYTE y, BYTE direction, const BYTE* steps, size_t stepCount);

/// C1:D0 -- corrects the character's position (without walking).
PMSG_POSITION_RECV BuildPositionRequest(BYTE x, BYTE y);

// -- Chat y combate --------------------------------------------------------

/// C1:00 -- public chat. The client sends the name anyway and the server replaces it with the real one, so it
/// is no use for impersonating someone else.
PMSG_CHAT_RECV BuildChatRequest(const char* name, const char* message);

/// C1:02 -- whisper. Here `name` is the RECIPIENT (on receive, in contrast, it is who sent it).
PMSG_CHAT_WHISPER_RECV BuildWhisperRequest(const char* targetName, const char* message);

/// C1:D9 -- ataque cuerpo a cuerpo sobre `targetIndex`.
PMSG_ATTACK_RECV BuildAttackRequest(WORD targetIndex, BYTE action, BYTE direction);

/// C1:18 -- pose or animation. `targetIndex` is who is being looked at, or 0xFFFF if nobody.
PMSG_ACTION_RECV BuildActionRequest(BYTE direction, BYTE action, WORD targetIndex);

// -- Inventario ------------------------------------------------------------

/// Contenedores que puede referir un movimiento de item.
enum class ItemContainer : BYTE
{
    Inventory = 0,
    Trade = 1,
    Warehouse = 2,
    ChaosBox = 3,
};

/// C1:24 -- moves an item between containers or inside the inventory. The `itemInfo` field is the item's 5
/// bytes as the server sent them; they are returned as they are because the server uses them to validate that
/// what is thought to be moved is what is moved.
PMSG_ITEM_MOVE_RECV BuildItemMoveRequest(ItemContainer sourceContainer, BYTE sourceSlot,
                                         const BYTE* itemInfo, ItemContainer targetContainer,
                                         BYTE targetSlot);

/// C1:26 -- usa un item sobre otro (pociones, joyas sobre equipo).
PMSG_ITEM_USE_RECV BuildItemUseRequest(BYTE sourceSlot, BYTE targetSlot, BYTE type);

/// C1:23 -- tira un item al piso en (x, y).
PMSG_ITEM_DROP_RECV BuildItemDropRequest(BYTE x, BYTE y, BYTE slot);

/// C1:22 -- picks up an item from the ground. `groundIndex` is the item's index within the map, not an
/// inventory slot.
PMSG_ITEM_GET_RECV BuildItemGetRequest(WORD groundIndex);

/// Un item tal como lo necesita el wire, antes de empaquetarlo.
struct ItemWire
{
    WORD Index = 0;      // < full index (section*32 + sub), 0-511
    BYTE Level = 0;      ///< 0-15
    BYTE Durability = 0;
    BYTE Option1 = 0;    ///< "suerte"
    BYTE Option2 = 0;    // < additional option, 0-3
    BYTE Option3 = 0;    // < option level; >3 turns on a separate bit
    BYTE NewOption = 0;  ///< bits de excelente
    BYTE SetOption = 0;
};

/// Packs an item into the 5 bytes that travel in EVERY item packet of this build (inventory, ground, move, buy,
/// shop). The previous dialect uses 12 bytes; mixing them up misaligns the whole packet. The index does not fit
/// in a byte: bit 8 travels separately, in bit 7 of the fourth byte. Five zero bytes mean "no item".
void WriteItemInfo(BYTE* dst, const ItemWire& item);

// -- Skills ----------------------------------------------------------------

/// C1:19 -- lanza un skill sobre `targetIndex`.
PMSG_SKILL_ATTACK_RECV BuildSkillAttackRequest(BYTE skill, WORD targetIndex, BYTE distance);

// -- Party -----------------------------------------------------------------

/// C1:40 -- invita a `targetIndex` al grupo.
PMSG_PARTY_REQUEST_RECV BuildPartyRequest(WORD targetIndex);

/// C1:41 -- answers an invitation. `accepted` false rejects it.
PMSG_PARTY_REQUEST_RESULT_RECV BuildPartyRequestResult(bool accepted, WORD inviterIndex);

/// C1:43 -- kicks from the party the member at position `memberNumber`.
PMSG_PARTY_DEL_MEMBER_RECV BuildPartyDeleteMember(BYTE memberNumber);

// -- NPC y tiendas ---------------------------------------------------------

/// C1:30 -- starts talking to the NPC with that object index. The server answers with the kind of window that
/// applies (shop, warehouse, chaos machine...).
PMSG_NPC_TALK_RECV BuildNpcTalkRequest(WORD npcIndex);

/// C1:31 -- ends the conversation. No body: the server only needs to know the window closed in order to release
/// the NPC state.
PBMSG_HEAD BuildNpcCloseRequest();

/// C1:32 -- buys the item at `slot` of the list the NPC sent.
PMSG_ITEM_BUY_RECV BuildItemBuyRequest(BYTE slot);

/// C1:33 -- sells the NPC the item at `slot` of the inventory.
PMSG_ITEM_SELL_RECV BuildItemSellRequest(BYTE slot);

/// C1:34 -- repairs the item at `slot`. `type` distinguishes repairing at the NPC from doing it yourself with
/// the item in hand.
PMSG_ITEM_REPAIR_RECV BuildItemRepairRequest(BYTE slot, BYTE type);

// -- Movimiento por puertas ------------------------------------------------

/// C1:1C -- teleport. With `gate` at zero it is the Dark Wizard skill and (x, y) is the destination; with a
/// real gate the server uses the gate's coordinates and does not look at these two.
PMSG_TELEPORT_RECV BuildTeleportRequest(BYTE gate, BYTE x, BYTE y);

/// C1:0E -- heartbeat. It carries the client's clock and its two speeds, which is how the original detected
/// speed hacks.
PMSG_LIVE_CLIENT_RECV BuildLiveClientRequest(DWORD tickCount, WORD physicalSpeed,
                                             WORD magicSpeed);

// -- Trade -----------------------------------------------------------------

/// C1:36 -- proposes a trade to the player with that object index.
PMSG_TRADE_REQUEST_RECV BuildTradeRequest(WORD targetIndex);

/// C1:37 -- accepts or rejects the proposal. The rest of the struct is data the server returns from the other
/// side; it is sent as zero, which is what the original does -- it sends the full sizeof() even though it only
/// uses the first byte.
PMSG_TRADE_RESPONSE_RECV BuildTradeResponse(bool accepted);

/// C1:3B -- puts money on the table. The emulator does not declare a struct for this packet, so the layout
/// comes from the server's parser: header and four big-endian amount bytes. It is **not** a native DWORD:
/// writing it as one reverses the bytes and the server reads another figure.
struct TradeMoneyRequest
{
    BYTE Data[7];
    static constexpr size_t Length = sizeof(Data);
};

TradeMoneyRequest BuildTradeMoneyRequest(DWORD money);

/// C1:3C -- checks or unchecks the confirm button.
PMSG_TRADE_OK_BUTTON_RECV BuildTradeOkButton(bool ready);

/// C1:3D -- cancela el intercambio. Sin cuerpo.
PBMSG_HEAD BuildTradeCancelRequest();

// -- Warehouse ------------------------------------------------------------------

/// C1:81 -- moves money between the inventory and the warehouse. `type` says in which direction. The amount is
/// big-endian, the same as in the trade.
PMSG_WAREHOUSE_MONEY_RECV BuildWarehouseMoneyRequest(BYTE type, DWORD money);

/// C1:82 -- closes the warehouse. No body.
PBMSG_HEAD BuildWarehouseCloseRequest();

/// C1:83 -- sets, changes or deletes the warehouse password, depending on `type`.
PMSG_WAREHOUSE_PASSWORD_RECV BuildWarehousePasswordRequest(BYTE type, WORD password,
                                                           const char* personalCode);

// -- Area and duration skills ------------------------------------------

/// C1:1D -- area skill. After the header come `count` targets, two bytes each (big-endian index), so the packet
/// is variable-length.
struct MultiSkillRequest
{
    /// 8-byte header plus up to MaxTargets two-byte targets.
    static constexpr size_t MaxTargets = 10;

    BYTE Data[8 + MaxTargets * 2];
    size_t Length = 0;
};

MultiSkillRequest BuildMultiSkillRequest(BYTE skill, BYTE x, BYTE y, BYTE serial,
                                         const WORD* targets, size_t targetCount);

/// C1:1E -- duration skill (the ones that last while the effect lasts).
PMSG_DURATION_SKILL_ATTACK_RECV BuildDurationSkillRequest(BYTE skill, BYTE x, BYTE y,
                                                          BYTE direction, WORD targetIndex,
                                                          BYTE magicKey);

// -- Amigos ----------------------------------------------------------------

/// C1:C1 -- le pide amistad a alguien.
PMSG_FRIEND_REQUEST_RECV BuildFriendAddRequest(const char* name);

/// C1:C2 -- acepta o rechaza un pedido de amistad.
PMSG_FRIEND_RESULT_RECV BuildFriendResultRequest(bool accepted, const char* name);

/// C1:C3 -- borra a alguien de la lista.
PMSG_FRIEND_DELETE_RECV BuildFriendDeleteRequest(const char* name);

/// C1:C0 -- asks for the friend list. No body, and no struct in the emulator.
PBMSG_HEAD BuildFriendListRequest();

// -- Guild -----------------------------------------------------------------

/// C1:54 -- abre la ventana de maestro de gremio.
PMSG_GUILD_MASTER_OPEN_RECV BuildGuildMasterOpenRequest(BYTE result);

/// C1:55 -- creates a guild. The name is eight characters and the emblem thirty-two bytes: four bits per cell
/// of an eight-by-eight grid.
PMSG_GUILD_CREATE_RECV BuildGuildCreateRequest(const char* guildName, const BYTE* mark);

// -- Stat points -------------------------------------------------

/// C1:F3:06 -- distributes a point. `type` says to which stat: 0 strength, 1 agility, 2 vitality, 3 energy, 4
/// command.
PMSG_LEVEL_UP_POINT_RECV BuildLevelUpPointRequest(BYTE type);

/// C1:F3:12 -- reports that the map finished loading after a teleport. No body: the server only needs to know
/// that the character can now be moved.
PSBMSG_HEAD BuildViewportEnableRequest();

// -- Listas sin cuerpo -----------------------------------------------------

/// C1:42 -- pide la lista del grupo.
PBMSG_HEAD BuildPartyListRequest();

/// C1:52 -- pide la lista del gremio.
PBMSG_HEAD BuildGuildListRequest();

// -- Chaos machine ------------------------------------------------------

/// C1:86 -- ejecuta la mezcla. `type` elige la receta.
PMSG_CHAOS_MIX_RECV BuildChaosMixRequest(BYTE type, BYTE info);

/// C1:88 -- asks for the success rate and cost before mixing.
PMSG_CHAOS_MIX_RATE_RECV BuildChaosMixRateRequest(DWORD type);

// -- Eventos ---------------------------------------------------------------

/// C1:90 -- entra a Devil Square.
PMSG_DEVIL_SQUARE_ENTER_RECV BuildDevilSquareEnterRequest(BYTE level, BYTE slot);

/// C1:91 -- asks how long is left before an event starts.
PMSG_EVENT_REMAIN_TIME_RECV BuildEventRemainTimeRequest(BYTE eventType, BYTE itemLevel);

/// C1:9A -- entra a Blood Castle.
PMSG_BLOOD_CASTLE_ENTER_RECV BuildBloodCastleEnterRequest(BYTE level, BYTE slot);

// -- Quest y mascotas ------------------------------------------------------

/// C1:A2 -- cambia el estado de una quest (aceptarla, entregarla).
PMSG_QUEST_STATE_RECV BuildQuestStateRequest(BYTE questIndex, BYTE questState);

/// C1:A9 -- queries or changes the state of the pet in the slot.
PMSG_PET_ITEM_INFO_RECV BuildPetItemInfoRequest(BYTE type, BYTE flag, BYTE slot);

// -- Los que quedaban ------------------------------------------------------

/// C1:35 -- repairs an item yourself, without an NPC. Same body as C1:34, only the header changes; the server
/// serves both with the same handler.
PMSG_ITEM_REPAIR_RECV BuildSelfRepairRequest(BYTE slot, BYTE type);

/// C1:87 -- closes the chaos machine. No body.
PBMSG_HEAD BuildChaosMixCloseRequest();

/// C1:8E -- moves through the Gatekeeper list. The destination number travels in two big-endian bytes quite
/// deep into the packet (offsets 8 and 9), not right after the header: the server reads there first and only
/// falls back to byte 4 if the packet is short.
struct TeleportMoveRequest
{
    BYTE Data[10];
    static constexpr size_t Length = sizeof(Data);
};

TeleportMoveRequest BuildTeleportMoveRequest(WORD moveIndex);

/// C1:A0 -- asks for the quest information. No body.
PBMSG_HEAD BuildQuestInfoRequest();

/// C1:F3:09 -- hardware identifier. The server stores it and does not answer; the original used it to bind an
/// account to a machine.
PMSG_HARDWARE_ID_INFO_RECV BuildHardwareIdRequest(const char* hardwareId);

}  // namespace Mu099B

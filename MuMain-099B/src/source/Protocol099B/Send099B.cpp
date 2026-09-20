#include "stdafx.h"

#include "Send099B.h"

#include "Data/Translation/MultiLanguage.h"
#include "Dotnet/Connection.h"

namespace Mu099B
{

namespace
{

/// Los campos de nombre del protocolo miden 10 bytes; con el terminador alcanza
/// un buffer de 11 para convertir sin recortar de más.
constexpr size_t NameFieldSize = 10;

/// Los mensajes de chat tienen su propio largo, bastante mayor que el de los
/// nombres.
constexpr size_t MessageFieldSize = 60;

template <typename TPacket>
void SendPacket(Connection& connection, const TPacket& packet)
{
    connection.Send(reinterpret_cast<const BYTE*>(&packet), sizeof(packet));
}

/// Pasa una cadena del cliente (UTF-16) a los bytes que espera el wire. El
/// largo del campo es parte del tipo para que no se pueda mezclar el buffer de
/// un nombre con el de un mensaje.
template <size_t FieldSize>
struct NarrowText
{
    explicit NarrowText(const wchar_t* text)
    {
        Value[0] = 0;
        if (text != nullptr)
        {
            CMultiLanguage::ConvertToUtf8(Value, text, static_cast<int>(FieldSize));
        }
    }

    char Value[FieldSize + 1];
};

using NarrowName = NarrowText<NameFieldSize>;
using NarrowMessage = NarrowText<MessageFieldSize>;

}  // namespace

void SendServerListRequest(Connection& connection)
{
    SendPacket(connection, BuildServerListRequest());
}

void SendServerInfoRequest(Connection& connection, BYTE serverCode)
{
    SendPacket(connection, BuildServerInfoRequest(serverCode));
}

void SendLogin(Connection& connection, const wchar_t* account, const wchar_t* password,
               const BYTE* clientVersion, const BYTE* clientSerial)
{
    const NarrowName narrowAccount(account);
    const NarrowName narrowPassword(password);

    SendPacket(connection, BuildLoginRequest(narrowAccount.Value, narrowPassword.Value,
                                             GetTickCount(), clientVersion, clientSerial));
}

void SendCharacterListRequest(Connection& connection)
{
    SendPacket(connection, BuildCharacterListRequest());
}

void SendCharacterCreate(Connection& connection, const wchar_t* name, BYTE characterClass)
{
    const NarrowName narrowName(name);
    SendPacket(connection, BuildCharacterCreateRequest(narrowName.Value, characterClass));
}

void SendCharacterDelete(Connection& connection, const wchar_t* name, const wchar_t* personalCode)
{
    const NarrowName narrowName(name);
    const NarrowName narrowCode(personalCode);
    SendPacket(connection, BuildCharacterDeleteRequest(narrowName.Value, narrowCode.Value));
}

void SendCharacterSelect(Connection& connection, const wchar_t* name)
{
    const NarrowName narrowName(name);
    SendPacket(connection, BuildCharacterSelectRequest(narrowName.Value));
}

void SendMove(Connection& connection, BYTE x, BYTE y, BYTE direction, const BYTE* steps,
              size_t stepCount)
{
    // El pedido es de largo variable, así que se manda exactamente lo que ocupa
    // en vez del tamaño del struct.
    const auto request = BuildMoveRequest(x, y, direction, steps, stepCount);
    connection.Send(request.Data, request.Length);
}

void SendPosition(Connection& connection, BYTE x, BYTE y)
{
    SendPacket(connection, BuildPositionRequest(x, y));
}

void SendChat(Connection& connection, const wchar_t* name, const wchar_t* message)
{
    const NarrowName narrowName(name);
    const NarrowMessage narrowMessage(message);
    SendPacket(connection, BuildChatRequest(narrowName.Value, narrowMessage.Value));
}

void SendWhisper(Connection& connection, const wchar_t* targetName, const wchar_t* message)
{
    const NarrowName narrowName(targetName);
    const NarrowMessage narrowMessage(message);
    SendPacket(connection, BuildWhisperRequest(narrowName.Value, narrowMessage.Value));
}

void SendAttack(Connection& connection, WORD targetIndex, BYTE action, BYTE direction)
{
    SendPacket(connection, BuildAttackRequest(targetIndex, action, direction));
}

void SendAction(Connection& connection, BYTE direction, BYTE action, WORD targetIndex)
{
    SendPacket(connection, BuildActionRequest(direction, action, targetIndex));
}

void SendItemMove(Connection& connection, ItemContainer sourceContainer, BYTE sourceSlot,
                  const BYTE* itemInfo, ItemContainer targetContainer, BYTE targetSlot)
{
    SendPacket(connection, BuildItemMoveRequest(sourceContainer, sourceSlot, itemInfo,
                                                targetContainer, targetSlot));
}

void SendItemUse(Connection& connection, BYTE sourceSlot, BYTE targetSlot, BYTE type)
{
    SendPacket(connection, BuildItemUseRequest(sourceSlot, targetSlot, type));
}

void SendItemDrop(Connection& connection, BYTE x, BYTE y, BYTE slot)
{
    SendPacket(connection, BuildItemDropRequest(x, y, slot));
}

void SendItemGet(Connection& connection, WORD groundIndex)
{
    SendPacket(connection, BuildItemGetRequest(groundIndex));
}

void SendSkillAttack(Connection& connection, BYTE skill, WORD targetIndex, BYTE distance)
{
    SendPacket(connection, BuildSkillAttackRequest(skill, targetIndex, distance));
}

void SendPartyRequest(Connection& connection, WORD targetIndex)
{
    SendPacket(connection, BuildPartyRequest(targetIndex));
}

void SendPartyRequestResult(Connection& connection, bool accepted, WORD inviterIndex)
{
    SendPacket(connection, BuildPartyRequestResult(accepted, inviterIndex));
}

void SendPartyDeleteMember(Connection& connection, BYTE memberNumber)
{
    SendPacket(connection, BuildPartyDeleteMember(memberNumber));
}

void SendNpcTalk(Connection& connection, WORD npcIndex)
{
    SendPacket(connection, BuildNpcTalkRequest(npcIndex));
}

void SendNpcClose(Connection& connection)
{
    SendPacket(connection, BuildNpcCloseRequest());
}

void SendItemBuy(Connection& connection, BYTE slot)
{
    SendPacket(connection, BuildItemBuyRequest(slot));
}

void SendItemSell(Connection& connection, BYTE slot)
{
    SendPacket(connection, BuildItemSellRequest(slot));
}

void SendItemRepair(Connection& connection, BYTE slot, BYTE type)
{
    SendPacket(connection, BuildItemRepairRequest(slot, type));
}

void SendTeleport(Connection& connection, BYTE gate, BYTE x, BYTE y)
{
    SendPacket(connection, BuildTeleportRequest(gate, x, y));
}

void SendLiveClient(Connection& connection, DWORD tickCount, WORD physicalSpeed,
                    WORD magicSpeed)
{
    SendPacket(connection, BuildLiveClientRequest(tickCount, physicalSpeed, magicSpeed));
}

void SendTradeRequest(Connection& connection, WORD targetIndex)
{
    SendPacket(connection, BuildTradeRequest(targetIndex));
}

void SendTradeResponse(Connection& connection, bool accepted)
{
    SendPacket(connection, BuildTradeResponse(accepted));
}

void SendTradeMoney(Connection& connection, DWORD money)
{
    // No es un struct: se manda el largo declarado, no sizeof del contenedor.
    const auto request = BuildTradeMoneyRequest(money);
    connection.Send(request.Data, static_cast<int32_t>(TradeMoneyRequest::Length));
}

void SendTradeOkButton(Connection& connection, bool ready)
{
    SendPacket(connection, BuildTradeOkButton(ready));
}

void SendTradeCancel(Connection& connection)
{
    SendPacket(connection, BuildTradeCancelRequest());
}

void SendWarehouseMoney(Connection& connection, BYTE type, DWORD money)
{
    SendPacket(connection, BuildWarehouseMoneyRequest(type, money));
}

void SendWarehouseClose(Connection& connection)
{
    SendPacket(connection, BuildWarehouseCloseRequest());
}

void SendWarehousePassword(Connection& connection, BYTE type, WORD password,
                           const wchar_t* personalCode)
{
    const NarrowName narrowCode(personalCode);
    SendPacket(connection, BuildWarehousePasswordRequest(type, password, narrowCode.Value));
}

void SendMultiSkill(Connection& connection, BYTE skill, BYTE x, BYTE y, BYTE serial,
                    const WORD* targets, size_t targetCount)
{
    // Largo variable: se manda lo que ocupa, no el tamaño del contenedor.
    const auto request = BuildMultiSkillRequest(skill, x, y, serial, targets, targetCount);
    connection.Send(request.Data, static_cast<int32_t>(request.Length));
}

void SendDurationSkill(Connection& connection, BYTE skill, BYTE x, BYTE y, BYTE direction,
                       WORD targetIndex, BYTE magicKey)
{
    SendPacket(connection, BuildDurationSkillRequest(skill, x, y, direction, targetIndex,
                                                     magicKey));
}

void SendFriendList(Connection& connection)
{
    SendPacket(connection, BuildFriendListRequest());
}

void SendFriendAdd(Connection& connection, const wchar_t* name)
{
    const NarrowName narrowName(name);
    SendPacket(connection, BuildFriendAddRequest(narrowName.Value));
}

void SendFriendResult(Connection& connection, bool accepted, const wchar_t* name)
{
    const NarrowName narrowName(name);
    SendPacket(connection, BuildFriendResultRequest(accepted, narrowName.Value));
}

void SendFriendDelete(Connection& connection, const wchar_t* name)
{
    const NarrowName narrowName(name);
    SendPacket(connection, BuildFriendDeleteRequest(narrowName.Value));
}

void SendGuildMasterOpen(Connection& connection, BYTE result)
{
    SendPacket(connection, BuildGuildMasterOpenRequest(result));
}

void SendGuildCreate(Connection& connection, const wchar_t* guildName, const BYTE* mark)
{
    // El nombre de gremio son ocho caracteres, no diez como los de personaje.
    const NarrowText<8> narrowName(guildName);
    SendPacket(connection, BuildGuildCreateRequest(narrowName.Value, mark));
}

void SendLevelUpPoint(Connection& connection, BYTE type)
{
    SendPacket(connection, BuildLevelUpPointRequest(type));
}

void SendViewportEnable(Connection& connection)
{
    SendPacket(connection, BuildViewportEnableRequest());
}

void SendPartyListRequest(Connection& connection)
{
    SendPacket(connection, BuildPartyListRequest());
}

void SendGuildListRequest(Connection& connection)
{
    SendPacket(connection, BuildGuildListRequest());
}

void SendChaosMix(Connection& connection, BYTE type, BYTE info)
{
    SendPacket(connection, BuildChaosMixRequest(type, info));
}

void SendChaosMixRate(Connection& connection, DWORD type)
{
    SendPacket(connection, BuildChaosMixRateRequest(type));
}

void SendDevilSquareEnter(Connection& connection, BYTE level, BYTE slot)
{
    SendPacket(connection, BuildDevilSquareEnterRequest(level, slot));
}

void SendEventRemainTime(Connection& connection, BYTE eventType, BYTE itemLevel)
{
    SendPacket(connection, BuildEventRemainTimeRequest(eventType, itemLevel));
}

void SendBloodCastleEnter(Connection& connection, BYTE level, BYTE slot)
{
    SendPacket(connection, BuildBloodCastleEnterRequest(level, slot));
}

void SendQuestState(Connection& connection, BYTE questIndex, BYTE questState)
{
    SendPacket(connection, BuildQuestStateRequest(questIndex, questState));
}

void SendPetItemInfo(Connection& connection, BYTE type, BYTE flag, BYTE slot)
{
    SendPacket(connection, BuildPetItemInfoRequest(type, flag, slot));
}

void SendSelfRepair(Connection& connection, BYTE slot, BYTE type)
{
    SendPacket(connection, BuildSelfRepairRequest(slot, type));
}

void SendChaosMixClose(Connection& connection)
{
    SendPacket(connection, BuildChaosMixCloseRequest());
}

void SendTeleportMove(Connection& connection, WORD moveIndex)
{
    const auto request = BuildTeleportMoveRequest(moveIndex);
    connection.Send(request.Data, static_cast<int32_t>(TeleportMoveRequest::Length));
}

void SendQuestInfo(Connection& connection)
{
    SendPacket(connection, BuildQuestInfoRequest());
}

void SendHardwareId(Connection& connection, const wchar_t* hardwareId)
{
    // El campo mide 45 bytes, mucho más que un nombre.
    const NarrowText<45> narrowId(hardwareId);
    SendPacket(connection, BuildHardwareIdRequest(narrowId.Value));
}

}  // namespace Mu099B

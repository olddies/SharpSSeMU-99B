// Sending 0.99B packets through the client's connection. Deliberately separated from Wire099B.h: packet
// building does not depend on the transport, so the tests can verify the exact bytes without linking the
// network layer. Connection::Send hands over the bytes as they are; on the C# side only the size field is
// filled in before writing to the socket. MU framing (plain C1/C2, encrypted C3/C4, size in byte 1 or in bytes
// 1-2) is identical in 0.99B and in the dialect the library ships, so the transport is reused unchanged while
// the dialect is replaced.

#pragma once

#include "Wire099B.h"

class Connection;

namespace Mu099B
{

/// C1:F4:02 -- pide la lista de servidores al ConnectServer.
void SendServerListRequest(Connection& connection);

/// C1:F4:03 -- pide IP y puerto del servidor `serverCode`.
void SendServerInfoRequest(Connection& connection, BYTE serverCode);

// -- GameServer ------------------------------------------------------------ The client works in UTF-16 but the
// wire is bytes, so these functions take wchar_t and convert at the edge.

/// C3:F1:01 -- login. `clientVersion` son 5 bytes y `clientSerial` 16.
void SendLogin(Connection& connection, const wchar_t* account, const wchar_t* password,
               const BYTE* clientVersion, const BYTE* clientSerial);

/// C1:F3:00 -- pide la lista de personajes de la cuenta.
void SendCharacterListRequest(Connection& connection);

/// C1:F3:01 -- crea un personaje de la clase indicada.
void SendCharacterCreate(Connection& connection, const wchar_t* name, BYTE characterClass);

/// C1:F3:02 -- borra un personaje.
void SendCharacterDelete(Connection& connection, const wchar_t* name, const wchar_t* personalCode);

/// C1:F3:03 -- entra al mundo con ese personaje.
void SendCharacterSelect(Connection& connection, const wchar_t* name);

/// C1:D7 -- asks to walk to (x, y) following `steps` directions (0-7 each). `direction` is where the character
/// ends up facing.
void SendMove(Connection& connection, BYTE x, BYTE y, BYTE direction, const BYTE* steps,
              size_t stepCount);

/// C1:D0 -- corrects the character's position without walking.
void SendPosition(Connection& connection, BYTE x, BYTE y);

/// C1:00 -- public chat.
void SendChat(Connection& connection, const wchar_t* name, const wchar_t* message);

/// C1:02 -- susurro a `targetName`.
void SendWhisper(Connection& connection, const wchar_t* targetName, const wchar_t* message);

/// C1:D9 -- ataque cuerpo a cuerpo.
void SendAttack(Connection& connection, WORD targetIndex, BYTE action, BYTE direction);

/// C1:18 -- pose or animation.
void SendAction(Connection& connection, BYTE direction, BYTE action, WORD targetIndex = 0xFFFF);

/// C1:24 -- mueve un item. `itemInfo` son los 5 bytes ya empaquetados.
void SendItemMove(Connection& connection, ItemContainer sourceContainer, BYTE sourceSlot,
                  const BYTE* itemInfo, ItemContainer targetContainer, BYTE targetSlot);

/// C1:26 -- usa un item sobre otro.
void SendItemUse(Connection& connection, BYTE sourceSlot, BYTE targetSlot, BYTE type);

/// C1:23 -- tira un item al piso.
void SendItemDrop(Connection& connection, BYTE x, BYTE y, BYTE slot);

/// C1:22 -- levanta un item del piso.
void SendItemGet(Connection& connection, WORD groundIndex);

/// C1:19 -- lanza un skill sobre un objetivo.
void SendSkillAttack(Connection& connection, BYTE skill, WORD targetIndex, BYTE distance = 0);

/// C1:40 -- invita a alguien al grupo.
void SendPartyRequest(Connection& connection, WORD targetIndex);

/// C1:41 -- accepts or rejects an invitation.
void SendPartyRequestResult(Connection& connection, bool accepted, WORD inviterIndex);

/// C1:43 -- echa a un miembro del grupo.
void SendPartyDeleteMember(Connection& connection, BYTE memberNumber);

// -- NPC y tiendas ---------------------------------------------------------

/// C1:30 -- talks to the NPC with that object index.
void SendNpcTalk(Connection& connection, WORD npcIndex);

/// C1:31 -- cierra la ventana del NPC.
void SendNpcClose(Connection& connection);

/// C1:32 -- buys the item at `slot` of the NPC's list.
void SendItemBuy(Connection& connection, BYTE slot);

/// C1:33 -- vende el item del `slot` del inventario.
void SendItemSell(Connection& connection, BYTE slot);

/// C1:34 -- repara el item del `slot`.
void SendItemRepair(Connection& connection, BYTE slot, BYTE type = 0);

// -- Gates and heartbeat -----------------------------------------------

/// C1:1C -- teleport. With `gate` at zero, (x, y) is the destination.
void SendTeleport(Connection& connection, BYTE gate, BYTE x, BYTE y);

/// C1:0E -- periodic heartbeat.
void SendLiveClient(Connection& connection, DWORD tickCount, WORD physicalSpeed,
                    WORD magicSpeed);

// -- Trade -----------------------------------------------------------------

/// C1:36 -- propone intercambio a `targetIndex`.
void SendTradeRequest(Connection& connection, WORD targetIndex);

/// C1:37 -- acepta o rechaza una propuesta.
void SendTradeResponse(Connection& connection, bool accepted);

/// C1:3B -- pone dinero en la mesa.
void SendTradeMoney(Connection& connection, DWORD money);

/// C1:3C -- checks or unchecks the confirm button.
void SendTradeOkButton(Connection& connection, bool ready);

/// C1:3D -- cancela el intercambio.
void SendTradeCancel(Connection& connection);

// -- Warehouse ------------------------------------------------------------------

/// C1:81 -- moves money between inventory and warehouse.
void SendWarehouseMoney(Connection& connection, BYTE type, DWORD money);

/// C1:82 -- closes the warehouse.
void SendWarehouseClose(Connection& connection);

/// C1:83 -- operates on the warehouse password.
void SendWarehousePassword(Connection& connection, BYTE type, WORD password,
                           const wchar_t* personalCode);

// -- Area and duration skills ------------------------------------------

/// C1:1D -- area skill over several targets at once.
void SendMultiSkill(Connection& connection, BYTE skill, BYTE x, BYTE y, BYTE serial,
                    const WORD* targets, size_t targetCount);

/// C1:1E -- duration skill.
void SendDurationSkill(Connection& connection, BYTE skill, BYTE x, BYTE y, BYTE direction,
                       WORD targetIndex, BYTE magicKey = 0);

// -- Amigos ----------------------------------------------------------------

/// C1:C0 -- pide la lista de amigos.
void SendFriendList(Connection& connection);

/// C1:C1 -- pide amistad.
void SendFriendAdd(Connection& connection, const wchar_t* name);

/// C1:C2 -- acepta o rechaza un pedido.
void SendFriendResult(Connection& connection, bool accepted, const wchar_t* name);

/// C1:C3 -- borra a alguien de la lista.
void SendFriendDelete(Connection& connection, const wchar_t* name);

// -- Guild -----------------------------------------------------------------

/// C1:54 -- abre la ventana de maestro de gremio.
void SendGuildMasterOpen(Connection& connection, BYTE result);

/// C1:55 -- crea un gremio con su emblema de 32 bytes.
void SendGuildCreate(Connection& connection, const wchar_t* guildName, const BYTE* mark);

// -- Puntos, listas, eventos y quest ---------------------------------------

/// C1:F3:06 -- distributes a stat point.
void SendLevelUpPoint(Connection& connection, BYTE type);

/// C1:F3:12 -- reports that the map finished loading after a teleport.
void SendViewportEnable(Connection& connection);

/// C1:42 -- pide la lista del grupo.
void SendPartyListRequest(Connection& connection);

/// C1:52 -- pide la lista del gremio.
void SendGuildListRequest(Connection& connection);

/// C1:86 -- runs a mix in the chaos machine.
void SendChaosMix(Connection& connection, BYTE type, BYTE info = 0);

/// C1:88 -- asks for success rate and cost before mixing.
void SendChaosMixRate(Connection& connection, DWORD type);

/// C1:90 -- entra a Devil Square.
void SendDevilSquareEnter(Connection& connection, BYTE level, BYTE slot);

/// C1:91 -- asks how long is left for an event.
void SendEventRemainTime(Connection& connection, BYTE eventType, BYTE itemLevel);

/// C1:9A -- entra a Blood Castle.
void SendBloodCastleEnter(Connection& connection, BYTE level, BYTE slot);

/// C1:A2 -- cambia el estado de una quest.
void SendQuestState(Connection& connection, BYTE questIndex, BYTE questState);

/// C1:A9 -- consulta o cambia el estado de una mascota.
void SendPetItemInfo(Connection& connection, BYTE type, BYTE flag, BYTE slot);

/// C1:35 -- repara un item sin NPC.
void SendSelfRepair(Connection& connection, BYTE slot, BYTE type = 0);

/// C1:87 -- closes the chaos machine.
void SendChaosMixClose(Connection& connection);

/// C1:8E -- moves through the Gatekeeper list.
void SendTeleportMove(Connection& connection, WORD moveIndex);

/// C1:A0 -- asks for the quest information.
void SendQuestInfo(Connection& connection);

/// C1:F3:09 -- manda el identificador de hardware.
void SendHardwareId(Connection& connection, const wchar_t* hardwareId);

}  // namespace Mu099B

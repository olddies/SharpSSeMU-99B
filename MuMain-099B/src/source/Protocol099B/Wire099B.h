// Capa de wire del protocolo SSeMU 0.99B: armado de encabezados y de paquetes.
//
// Los structs salen de Protocol099B.generated.h, derivado de las fuentes del
// emulador (ver tools/protogen/README.md). Acá no se escribe ningún layout a
// mano: solo se timbra el encabezado y se llenan campos.
//
// Este archivo no depende del transporte ni del precompilado del cliente, así
// que los tests pueden compilarlo suelto y verificar los bytes exactos que
// salen al socket. El envío vive en Send099B.h.

#pragma once

#include <cstddef>

#include "ItemInfo099B.h"  // ItemInfoSize y el desarmado de los cinco bytes
#include "Protocol099B.generated.h"

namespace Mu099B
{

/// Tipos de paquete del wire. El bit bajo distingue "tamaño de 1 byte"
/// (C1/C3) de "tamaño de 2 bytes" (C2/C4); el par alto es la variante cifrada.
enum class PacketType : BYTE
{
    PlainByteSize = 0xC1,
    PlainWordSize = 0xC2,
    EncryptedByteSize = 0xC3,
    EncryptedWordSize = 0xC4,
};

/// Timbra un PBMSG_HEAD (tamaño de 1 byte, sin sub-código).
void SetHeader(PBMSG_HEAD& header, BYTE head, BYTE size, bool encrypted = false);

/// Timbra un PSBMSG_HEAD (tamaño de 1 byte, con sub-código).
void SetHeader(PSBMSG_HEAD& header, BYTE head, BYTE sub, BYTE size, bool encrypted = false);

/// Timbra un PWMSG_HEAD (tamaño de 2 bytes, sin sub-código).
void SetHeader(PWMSG_HEAD& header, BYTE head, WORD size, bool encrypted = false);

/// Timbra un PSWMSG_HEAD (tamaño de 2 bytes, con sub-código).
void SetHeader(PSWMSG_HEAD& header, BYTE head, BYTE sub, WORD size, bool encrypted = false);

// -- ConnectServer ---------------------------------------------------------

/// C1:F4:02 -- pide la lista de servidores.
PMSG_SERVER_LIST_RECV BuildServerListRequest();

/// C1:F4:03 -- pide IP y puerto de un ServerCode.
/// Ojo: en este build el código de servidor viaja como BYTE, no como WORD.
PMSG_SERVER_INFO_RECV BuildServerInfoRequest(BYTE serverCode);

// -- Login y personajes ----------------------------------------------------

/// XOR de 3 bytes que protege cuenta y contraseña dentro del login (puerto de
/// PacketArgumentDecrypt, Util.cpp). Es involutivo: la misma función cifra y
/// descifra.
void ApplyArgumentCipher(BYTE* buffer, size_t length);

/// C3:F1:01 -- login. Va cifrado por bloques, de ahí el 0xC3.
///
/// `clientVersion` son 5 bytes y `clientSerial` 16; los dos tienen que coincidir
/// exactos con los del .ini del servidor o rebota con "versión incorrecta".
/// Cuenta y contraseña se recortan a 10 bytes y se pasan por el XOR de
/// argumentos.
PMSG_CONNECT_ACCOUNT_RECV BuildLoginRequest(const char* account, const char* password,
                                            DWORD tickCount, const BYTE* clientVersion,
                                            const BYTE* clientSerial);

/// C1:F3:00 -- pide la lista de personajes. No lleva cuerpo.
PSBMSG_HEAD BuildCharacterListRequest();

/// C1:F3:01 -- crea un personaje.
PMSG_CHARACTER_CREATE_RECV BuildCharacterCreateRequest(const char* name, BYTE characterClass);

/// C1:F3:02 -- borra un personaje. `personalCode` es el código de seguridad de
/// la cuenta (10 bytes).
PMSG_CHARACTER_DELETE_RECV BuildCharacterDeleteRequest(const char* name, const char* personalCode);

/// C1:F3:03 -- entra al mundo con el personaje `name`.
PMSG_CHARACTER_INFO_RECV BuildCharacterSelectRequest(const char* name);

// -- Mundo: movimiento y posición -------------------------------------------

/// Cantidad máxima de pasos que entra en un pedido de movimiento.
///
/// El contador es un nibble, así que en teoría llegaría a 15 -- pero path[] mide
/// 8 bytes y el primero se lo lleva dirección + contador, quedando 7 para las
/// direcciones a dos por byte: 14. Pedir 15 se pasaría del struct.
inline constexpr size_t MaxMoveSteps = 14;

/// Un pedido de movimiento ya serializado. El largo es VARIABLE: el struct
/// declara path[8] pero el cliente sólo manda los bytes que necesita según la
/// cantidad de pasos. Mandar siempre los 8 no es incorrecto en el wire, pero
/// leer de más sí rompe a quien asuma el largo fijo -- de hecho es el bug que
/// tiró el servidor con paquetes reales.
struct MoveRequest
{
    BYTE Data[sizeof(PMSG_MOVE_RECV)];
    BYTE Length;
};

/// C1:D7 -- pide moverse a (x, y) siguiendo `steps` direcciones (0-7 cada una).
/// `direction` es hacia dónde mira el personaje al arrancar.
MoveRequest BuildMoveRequest(BYTE x, BYTE y, BYTE direction, const BYTE* steps, size_t stepCount);

/// C1:D0 -- corrige la posición del personaje (sin caminar).
PMSG_POSITION_RECV BuildPositionRequest(BYTE x, BYTE y);

// -- Chat y combate --------------------------------------------------------

/// C1:00 -- chat público. El nombre lo manda igual el cliente y el servidor lo
/// reemplaza por el real, así que no sirve para hacerse pasar por otro.
PMSG_CHAT_RECV BuildChatRequest(const char* name, const char* message);

/// C1:02 -- susurro. Acá `name` es el DESTINATARIO (al recibir, en cambio, es
/// quien lo mandó).
PMSG_CHAT_WHISPER_RECV BuildWhisperRequest(const char* targetName, const char* message);

/// C1:D9 -- ataque cuerpo a cuerpo sobre `targetIndex`.
PMSG_ATTACK_RECV BuildAttackRequest(WORD targetIndex, BYTE action, BYTE direction);

/// C1:18 -- pose o animación. `targetIndex` es a quién se mira, o 0xFFFF si a
/// nadie.
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

/// C1:24 -- mueve un item entre contenedores o dentro del inventario. El campo
/// `itemInfo` son los 5 bytes del item tal como los mandó el servidor; se
/// devuelven tal cual porque el servidor los usa para validar que se está
/// moviendo lo que se cree.
PMSG_ITEM_MOVE_RECV BuildItemMoveRequest(ItemContainer sourceContainer, BYTE sourceSlot,
                                         const BYTE* itemInfo, ItemContainer targetContainer,
                                         BYTE targetSlot);

/// C1:26 -- usa un item sobre otro (pociones, joyas sobre equipo).
PMSG_ITEM_USE_RECV BuildItemUseRequest(BYTE sourceSlot, BYTE targetSlot, BYTE type);

/// C1:23 -- tira un item al piso en (x, y).
PMSG_ITEM_DROP_RECV BuildItemDropRequest(BYTE x, BYTE y, BYTE slot);

/// C1:22 -- levanta un item del piso. `groundIndex` es el índice del item
/// dentro del mapa, no un slot de inventario.
PMSG_ITEM_GET_RECV BuildItemGetRequest(WORD groundIndex);

/// Un item tal como lo necesita el wire, antes de empaquetarlo.
struct ItemWire
{
    WORD Index = 0;      ///< índice completo (sección*32 + sub), 0-511
    BYTE Level = 0;      ///< 0-15
    BYTE Durability = 0;
    BYTE Option1 = 0;    ///< "suerte"
    BYTE Option2 = 0;    ///< opción adicional, 0-3
    BYTE Option3 = 0;    ///< nivel de opción; >3 prende un bit aparte
    BYTE NewOption = 0;  ///< bits de excelente
    BYTE SetOption = 0;
};

/// Empaqueta un item en los 5 bytes que viajan en TODO paquete de item de este
/// build (inventario, piso, mover, comprar, tienda). El dialecto anterior usa
/// 12 bytes; confundirlos desalinea el paquete entero.
///
/// El índice no entra en un byte: el bit 8 viaja aparte, en el bit 7 del cuarto
/// byte. Cinco bytes en cero significan "sin item".
void WriteItemInfo(BYTE* dst, const ItemWire& item);

// -- Skills ----------------------------------------------------------------

/// C1:19 -- lanza un skill sobre `targetIndex`.
PMSG_SKILL_ATTACK_RECV BuildSkillAttackRequest(BYTE skill, WORD targetIndex, BYTE distance);

// -- Party -----------------------------------------------------------------

/// C1:40 -- invita a `targetIndex` al grupo.
PMSG_PARTY_REQUEST_RECV BuildPartyRequest(WORD targetIndex);

/// C1:41 -- responde una invitación. `accepted` en false la rechaza.
PMSG_PARTY_REQUEST_RESULT_RECV BuildPartyRequestResult(bool accepted, WORD inviterIndex);

/// C1:43 -- echa del grupo al miembro en la posición `memberNumber`.
PMSG_PARTY_DEL_MEMBER_RECV BuildPartyDeleteMember(BYTE memberNumber);

// -- NPC y tiendas ---------------------------------------------------------

/// C1:30 -- empieza a hablarle al NPC que tiene ese índice de objeto. El
/// servidor contesta con el tipo de ventana que corresponde (tienda, baúl,
/// máquina del caos...).
PMSG_NPC_TALK_RECV BuildNpcTalkRequest(WORD npcIndex);

/// C1:31 -- cierra la conversación. No lleva cuerpo: el servidor sólo necesita
/// saber que la ventana se cerró para soltar el estado del NPC.
PBMSG_HEAD BuildNpcCloseRequest();

/// C1:32 -- compra el item que está en `slot` de la lista que mandó el NPC.
PMSG_ITEM_BUY_RECV BuildItemBuyRequest(BYTE slot);

/// C1:33 -- le vende al NPC el item que está en `slot` del inventario.
PMSG_ITEM_SELL_RECV BuildItemSellRequest(BYTE slot);

/// C1:34 -- repara el item de `slot`. `type` distingue reparar en el NPC de
/// hacerlo uno mismo con el ítem en la mano.
PMSG_ITEM_REPAIR_RECV BuildItemRepairRequest(BYTE slot, BYTE type);

// -- Movimiento por puertas ------------------------------------------------

/// C1:1C -- teletransporte. Con `gate` en cero es el skill del Dark Wizard y
/// (x, y) son el destino; con una puerta real el servidor usa las coordenadas
/// de la puerta y estas dos no las mira.
PMSG_TELEPORT_RECV BuildTeleportRequest(BYTE gate, BYTE x, BYTE y);

/// C1:0E -- señal de vida. Lleva el reloj del cliente y sus dos velocidades,
/// que es con lo que el original detectaba aceleradores.
PMSG_LIVE_CLIENT_RECV BuildLiveClientRequest(DWORD tickCount, WORD physicalSpeed,
                                             WORD magicSpeed);

// -- Trade -----------------------------------------------------------------

/// C1:36 -- le propone intercambio al jugador con ese índice de objeto.
PMSG_TRADE_REQUEST_RECV BuildTradeRequest(WORD targetIndex);

/// C1:37 -- acepta o rechaza la propuesta. El resto del struct son los datos
/// que el servidor devuelve del otro lado; se mandan en cero, que es lo que
/// hace el original -- manda sizeof() completo aunque sólo use el primer byte.
PMSG_TRADE_RESPONSE_RECV BuildTradeResponse(bool accepted);

/// C1:3B -- pone dinero en la mesa.
///
/// El emulador no declara un struct para este paquete, así que el layout sale
/// del parser del servidor: cabecera y cuatro bytes de monto en big-endian.
/// **No** es un DWORD nativo: escribirlo como tal invierte los bytes y el
/// servidor lee otra cifra.
struct TradeMoneyRequest
{
    BYTE Data[7];
    static constexpr size_t Length = sizeof(Data);
};

TradeMoneyRequest BuildTradeMoneyRequest(DWORD money);

/// C1:3C -- marca o desmarca el botón de confirmar.
PMSG_TRADE_OK_BUTTON_RECV BuildTradeOkButton(bool ready);

/// C1:3D -- cancela el intercambio. Sin cuerpo.
PBMSG_HEAD BuildTradeCancelRequest();

// -- Baúl ------------------------------------------------------------------

/// C1:81 -- mueve dinero entre el inventario y el baúl. `type` dice en qué
/// sentido. El monto va en big-endian, igual que en el trade.
PMSG_WAREHOUSE_MONEY_RECV BuildWarehouseMoneyRequest(BYTE type, DWORD money);

/// C1:82 -- cierra el baúl. Sin cuerpo.
PBMSG_HEAD BuildWarehouseCloseRequest();

/// C1:83 -- pone, cambia o borra la clave del baúl, según `type`.
PMSG_WAREHOUSE_PASSWORD_RECV BuildWarehousePasswordRequest(BYTE type, WORD password,
                                                           const char* personalCode);

// -- Skills de área y de duración ------------------------------------------

/// C1:1D -- skill de área. Después de la cabecera van `count` objetivos, dos
/// bytes cada uno (índice big-endian), así que el paquete es de largo variable.
struct MultiSkillRequest
{
    /// Cabecera de 8 bytes más hasta MaxTargets objetivos de dos bytes.
    static constexpr size_t MaxTargets = 10;

    BYTE Data[8 + MaxTargets * 2];
    size_t Length = 0;
};

MultiSkillRequest BuildMultiSkillRequest(BYTE skill, BYTE x, BYTE y, BYTE serial,
                                         const WORD* targets, size_t targetCount);

/// C1:1E -- skill de duración (los que se mantienen mientras dure el efecto).
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

/// C1:C0 -- pide la lista de amigos. Sin cuerpo, y sin struct en el emulador.
PBMSG_HEAD BuildFriendListRequest();

// -- Guild -----------------------------------------------------------------

/// C1:54 -- abre la ventana de maestro de gremio.
PMSG_GUILD_MASTER_OPEN_RECV BuildGuildMasterOpenRequest(BYTE result);

/// C1:55 -- crea un gremio. El nombre son ocho caracteres y el emblema treinta
/// y dos bytes: cuatro bits por celda de una grilla de ocho por ocho.
PMSG_GUILD_CREATE_RECV BuildGuildCreateRequest(const char* guildName, const BYTE* mark);

// -- Puntos de estadística -------------------------------------------------

/// C1:F3:06 -- reparte un punto. `type` dice a qué estadística: 0 fuerza,
/// 1 agilidad, 2 vitalidad, 3 energía, 4 liderazgo.
PMSG_LEVEL_UP_POINT_RECV BuildLevelUpPointRequest(BYTE type);

/// C1:F3:12 -- avisa que el mapa terminó de cargar después de un teleport. Sin
/// cuerpo: el servidor sólo necesita saber que ya se puede mover al personaje.
PSBMSG_HEAD BuildViewportEnableRequest();

// -- Listas sin cuerpo -----------------------------------------------------

/// C1:42 -- pide la lista del grupo.
PBMSG_HEAD BuildPartyListRequest();

/// C1:52 -- pide la lista del gremio.
PBMSG_HEAD BuildGuildListRequest();

// -- Máquina del caos ------------------------------------------------------

/// C1:86 -- ejecuta la mezcla. `type` elige la receta.
PMSG_CHAOS_MIX_RECV BuildChaosMixRequest(BYTE type, BYTE info);

/// C1:88 -- pregunta la tasa de éxito y el costo antes de mezclar.
PMSG_CHAOS_MIX_RATE_RECV BuildChaosMixRateRequest(DWORD type);

// -- Eventos ---------------------------------------------------------------

/// C1:90 -- entra a Devil Square.
PMSG_DEVIL_SQUARE_ENTER_RECV BuildDevilSquareEnterRequest(BYTE level, BYTE slot);

/// C1:91 -- pregunta cuánto falta para que empiece un evento.
PMSG_EVENT_REMAIN_TIME_RECV BuildEventRemainTimeRequest(BYTE eventType, BYTE itemLevel);

/// C1:9A -- entra a Blood Castle.
PMSG_BLOOD_CASTLE_ENTER_RECV BuildBloodCastleEnterRequest(BYTE level, BYTE slot);

// -- Quest y mascotas ------------------------------------------------------

/// C1:A2 -- cambia el estado de una quest (aceptarla, entregarla).
PMSG_QUEST_STATE_RECV BuildQuestStateRequest(BYTE questIndex, BYTE questState);

/// C1:A9 -- consulta o cambia el estado de la mascota del slot.
PMSG_PET_ITEM_INFO_RECV BuildPetItemInfoRequest(BYTE type, BYTE flag, BYTE slot);

// -- Los que quedaban ------------------------------------------------------

/// C1:35 -- repara un item uno mismo, sin NPC. Mismo cuerpo que C1:34, sólo
/// cambia la cabecera; el servidor los atiende con el mismo handler.
PMSG_ITEM_REPAIR_RECV BuildSelfRepairRequest(BYTE slot, BYTE type);

/// C1:87 -- cierra la máquina del caos. Sin cuerpo.
PBMSG_HEAD BuildChaosMixCloseRequest();

/// C1:8E -- se mueve por la lista del Gatekeeper.
///
/// El número de destino viaja en dos bytes big-endian bastante adentro del
/// paquete (offsets 8 y 9), no justo después de la cabecera: el servidor lee
/// ahí primero y sólo cae al byte 4 si el paquete es corto.
struct TeleportMoveRequest
{
    BYTE Data[10];
    static constexpr size_t Length = sizeof(Data);
};

TeleportMoveRequest BuildTeleportMoveRequest(WORD moveIndex);

/// C1:A0 -- pide la información de las quests. Sin cuerpo.
PBMSG_HEAD BuildQuestInfoRequest();

/// C1:F3:09 -- identificador de hardware. El servidor lo guarda y no responde;
/// el original lo usaba para atar una cuenta a una máquina.
PMSG_HARDWARE_ID_INFO_RECV BuildHardwareIdRequest(const char* hardwareId);

}  // namespace Mu099B

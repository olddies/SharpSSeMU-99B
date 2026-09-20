// Los cinco bytes de item de 0.99B (MAX_ITEM_INFO), y la traducción al índice
// que usan los modelos del cliente.
//
// Estos cinco bytes viajan en todo lo que toca objetos: inventario, item en el
// suelo, mover, comprar, vender, tienda. El dialecto posterior usa doce, con
// sockets y Jewel of Harmony que en este build no existen.
//
// Hay dos numeraciones de item conviviendo, y la diferencia es sólo el paso:
//
//   * **0.99B**: sección * 32 + sub, nueve bits en total (0-511). Es lo que
//     viaja por la red.
//   * **Cliente**: grupo * 512 + número, que es lo que indexan los modelos.
//
// Las dieciséis secciones son los mismos dieciséis grupos y en el mismo orden
// (espada, hacha, maza, lanza, arco, bastón, escudo, casco, armadura,
// pantalón, guantes, botas, alas, ayudante, poción, varios), así que la
// conversión es sólo cambiar el paso -- no hay tabla de equivalencias.
//
// Como el resto del módulo, no depende del transporte ni del PCH: los tests
// verifican los bytes sin linkear la red.

#pragma once

#include <cstddef>
#include <cstdint>

#include "CharSet099B.h"

namespace Mu099B
{

/// Bytes que ocupa un item en el wire de este build (MAX_ITEM_INFO).
inline constexpr size_t ItemInfoSize = 5;

/// Secciones de item que tiene 0.99B (MAX_ITEM_SECTION), que son los mismos
/// dieciséis grupos del cliente y en el mismo orden.
inline constexpr int ItemSectionCount = 16;

/// Cuántos items entran en un grupo del lado del cliente. El de 0.99B es
/// ItemsPerGroup (32); esta es la otra mitad de la conversión.
inline constexpr int ClientItemsPerGroup = 512;

/// Un item ya desarmado de los cinco bytes.
struct ItemInfo
{
    /// Los cinco bytes en cero significan "sin item": el servidor sólo manda
    /// slots ocupados, así que en el inventario esto no aparece, pero sí en los
    /// paquetes donde el campo es opcional.
    bool Present = false;

    /// Índice de 0.99B (sección * 32 + sub, 0-511).
    uint16_t Index = 0;

    /// El mismo índice desarmado, que es como lo quiere el cliente.
    uint8_t Group = 0;
    uint8_t Number = 0;

    uint8_t Level = 0;       ///< 0-15, el "+n" del item.
    uint8_t Durability = 0;
    bool Luck = false;
    bool Skill = false;
    /// El "+4 / +8 / +12 / +16": dos bits en el byte 1 y el tercero en el 3.
    /// El servidor lo topa en 4 (ClientProtocolHandler: "Máximo +16 opción").
    uint8_t OptionLevel = 0;

    /// Máscara de opciones excelentes, un bit por opción. El servidor prende el
    /// brillo del CharSet con (ExcellentFlags & 0x3F) != 0.
    uint8_t ExcellentFlags = 0;

    /// Set-item / antiguo, en el nibble bajo.
    uint8_t SetOption = 0;
};

/// Desarma los cinco bytes. Puerto de CItemManager::ItemByteConvert.
ItemInfo DecodeItemInfo(const uint8_t bytes[ItemInfoSize]);

/// Vuelve a armarlos. Un item con Present en false escribe cinco ceros.
///
/// Hace falta la vuelta completa porque varios pedidos del cliente devuelven el
/// item que creen estar tocando: el servidor los usa para confirmar que
/// cliente y servidor hablan del mismo objeto.
void EncodeItemInfo(const ItemInfo& item, uint8_t bytes[ItemInfoSize]);

/// Tamaño máximo del bloque que arma WriteClientItemBlock: los cinco bytes
/// fijos más opción, excelente y set.
inline constexpr size_t ClientItemBlockSize = 8;

/// Traduce un item al bloque de bytes que consume el inventario del cliente
/// (ParseItemData): grupo y número empaquetados en un WORD, nivel,
/// durabilidad, banderas, y después los campos opcionales que las banderas
/// anuncien.
///
/// Es el mismo truco que WriteExtendedEquipment: en vez de tocar el inventario
/// del cliente, se le da lo que ya sabe leer. Devuelve cuántos bytes escribió.
size_t WriteClientItemBlock(const ItemInfo& item, uint8_t block[ClientItemBlockSize]);

/// Índice de 0.99B -> índice del cliente. Cambia el paso de 32 a 512.
uint32_t ToClientItemIndex(uint16_t wireIndex);

/// Índice del cliente -> índice de 0.99B. Devuelve false si el número dentro
/// del grupo no entra en los 32 que tiene una sección, que es lo que pasa con
/// los items agregados en temporadas posteriores: no tienen equivalente acá y
/// mandarlos igual haría que el servidor interprete otro objeto.
bool ToWireItemIndex(uint32_t clientIndex, uint16_t& wireIndex);

/// El montón de zen tirado en el piso: sección 14, sub 15 (GET_ITEM(14,15) del
/// emulador).
inline constexpr uint16_t ZenItemIndex = 14 * ItemsPerGroup + 15;

/// Desarma un montón de zen del piso. El servidor NO usa el formato normal de
/// item para el dinero: manda el índice del zen y mete el monto crudo en los
/// bytes 1, 2 y 4 (CViewport::GCViewportItemSend, Viewport.cpp:903-911), así
/// que pasarlo por DecodeItemInfo devuelve nivel y durabilidad inventados en
/// vez del monto.
///
/// Devuelve false si estos cinco bytes no son un montón de zen, y en ese caso
/// no toca `amount`.
bool DecodeDroppedMoney(const uint8_t bytes[ItemInfoSize], uint32_t& amount);

}  // namespace Mu099B

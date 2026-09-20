// Apariencia de personaje: CharSet[13] del protocolo 0.99B.
//
// El servidor manda clase, equipo, nivel de brillo, excelente, conjunto, alas y
// mascota empaquetados en trece bytes (CObjectManager::CharacterMakePreviewCharSet).
// El cliente, en cambio, espera el bloque "extendido" de 25 bytes con tres
// bytes por slot. Acá se hace la traducción.
//
// El punto delicado es que el CharSet guarda **el sub-índice** de cada pieza de
// armadura, no el índice completo: el grupo va implícito en el slot (casco = 7,
// armadura = 8, pantalón = 9, guantes = 10, botas = 11). Las armas sí llevan el
// índice completo, del que se derivan grupo y número. Confundir una cosa con la
// otra es lo que hace que todos los personajes se vean iguales.

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

/// En 0.99B cada sección de items tiene 32 entradas (MAX_ITEM_TYPE), así que el
/// índice completo es sección*32 + sub.
inline constexpr int ItemsPerGroup = 32;

/// Valores de "vacío" dentro del CharSet.
inline constexpr int NoWeapon = 0xFF;
inline constexpr int NoItem = ItemsPerGroup - 1;  // 0x1F

/// Una pieza de equipo ya interpretada.
struct AppearanceSlot
{
    bool Present = false;
    uint8_t Group = 0;
    uint16_t Number = 0;
    /// Nivel de brillo tal como lo espera el cliente (0-7); pasa directo al
    /// nibble alto, sin conversión: el empaquetado del servidor
    /// (((Level-1)/2)&7) ya es la inversa exacta de LevelConvert.
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
    /// Casco, armadura, pantalón, guantes, botas -- en ese orden, que es el que
    /// espera el bloque de equipo del cliente.
    AppearanceSlot BodyPart[5];
    AppearanceSlot Wing;
    AppearanceSlot Helper;
};

/// Largo del bloque de equipo extendido del cliente (EQUIPMENT_LENGTH_EXTENDED).
inline constexpr int ExtendedEquipmentSize = 25;

/// Clase y "change up" tal como los empaqueta el servidor en un solo byte:
/// clase en los bits altos, change-up en el bit 4. Se usa tanto en CharSet[0]
/// como en el campo Class de la respuesta de creación de personaje, que llevan
/// el mismo formato (DGCharacterCreateRecv arma el byte igual que
/// CharacterMakePreviewCharSet). Los cuatro bits bajos NO son parte de esto:
/// ahí va el ViewState.
struct ClassByte
{
    uint8_t CharacterClass = 0;
    uint8_t ChangeUp = 0;
};

/// Desarma el byte de clase **del CharSet**, donde la clase ocupa los tres bits
/// altos (base * 32) porque los bits bajos llevan el ViewState.
ClassByte DecodeClassByte(uint8_t value);

/// Desarma el byte de clase **de la base de datos**, que es otro empaquetado:
/// la clase base va en el nibble alto y la evolución en el bajo (0, 16, 32, 48,
/// 64 para DW, DK, FE, MG y DL -- exactamente las filas de default_class_type
/// del servidor).
///
/// Que convivan dos codificaciones no es un descuido del port: el CharSet
/// necesita los bits bajos para otra cosa, así que corre la clase un bit más.
/// Usar la del CharSet acá hace que el servidor no encuentre la clase y
/// rechace la creación con "cuenta llena", que es un mensaje que no ayuda nada
/// a encontrar la causa.
ClassByte DecodeDatabaseClassByte(uint8_t value);

/// Arma el byte de clase de la base de datos a partir del índice de clase base
/// (0-4). Los personajes nuevos siempre nacen sin evolución.
uint8_t MakeDatabaseClassByte(uint8_t baseClass);

/// Interpreta los trece bytes del CharSet.
Appearance DecodeCharSet(const uint8_t charSet[13]);

/// Escribe la apariencia en el bloque de 25 bytes que consume
/// ReadEquipmentExtended: tres bytes para cada arma y cada pieza de armadura,
/// dos para las alas y dos para la mascota.
void WriteExtendedEquipment(const Appearance& appearance, uint8_t equipment[ExtendedEquipmentSize]);

}  // namespace Mu099B

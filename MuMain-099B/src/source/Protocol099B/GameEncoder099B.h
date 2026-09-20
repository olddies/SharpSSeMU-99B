// Codificador de salida del socket de GameServer en 0.99B.
//
// Contraparte de GameFramer099B: toma un paquete lógico ya armado (tipo,
// tamaño, head y cuerpo) y devuelve los bytes exactos que van al socket.
//
// El orden es el inverso del de recepción, y la ofuscación es asimétrica:
//
//   1. XorData sobre el cuerpo. El servidor la deshace al recibir
//      (ExtractPacket), así que acá SÍ hay que aplicarla -- al revés de lo que
//      pasa en el sentido servidor -> cliente, donde nadie ofusca.
//   2. Para C3/C4, cifrado por bloques de [serial][cuerpo], con el número de
//      serie ocupando el lugar del byte de tamaño del paquete lógico.
//   3. Cifrado de flujo sobre todo el paquete, encabezado incluido.
//
// El número de serie lo lleva el codificador porque el original lo incrementa
// por conexión en cada envío cifrado.

#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

#include "BlockCipher099B.h"
#include "StreamCipher099B.h"

namespace Mu099B
{

class GameEncoder
{
public:
    GameEncoder(const StreamCipher& streamCipher, const BlockCipher& blockCipher);

    /// Codifica un paquete lógico. Si su byte de tipo es 0xC3 o 0xC4 se cifra
    /// por bloques; con 0xC1 o 0xC2 va en claro. En los dos casos se ofusca el
    /// cuerpo y se cifra el flujo.
    std::vector<uint8_t> Encode(const uint8_t* logicalPacket, size_t length);

    /// Serie que se usará en el próximo envío cifrado (para diagnóstico).
    uint8_t NextSerial() const { return _sendSerial; }

private:
    StreamCipher _streamCipher;
    BlockCipher _blockCipher;
    uint8_t _sendSerial = 0;
};

}  // namespace Mu099B

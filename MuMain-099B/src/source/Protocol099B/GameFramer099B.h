// Separador de paquetes del socket de GameServer en 0.99B.
//
// Puerto de CSocketManager::OnRecv + DataRecv + CPacketManager::ExtractPacket.
// El orden importa y es el motivo por el que este framing no puede delegarse a
// la librería C#:
//
//   1. Los bytes recién leídos del socket se descifran en el lugar con el
//      cifrado de flujo -- que también tapa los bytes de tipo y tamaño, así que
//      sin este paso no hay forma de saber dónde empieza y termina un paquete.
//   2. Se separan paquetes por cabecera: C1/C3 traen el tamaño en un byte,
//      C2/C4 en dos (big-endian).
//   3. Los C3/C4 se descifran por bloques (11 -> 8) y se reconstruyen como un
//      paquete lógico C1/C2. El primer byte descifrado es un número de serie,
//      no el head real.
//
// El resultado es un paquete lógico listo para el despachador del cliente.
//
// OJO CON XorData: es asimétrico y no se aplica acá. En el emulador XorData vive
// dentro de ExtractPacket, o sea sólo en el camino de RECEPCIÓN, y el envío
// (DataSend) nunca lo aplica. Por lo tanto:
//
//   cliente -> servidor : el cliente ofusca, el servidor des-ofusca
//   servidor -> cliente : no lo aplica nadie
//
// Este framer es el del CLIENTE, así que recibe sin ofuscar. La ofuscación de
// salida vive en el camino de envío. Portar el framer del servidor tal cual
// -- que sí des-ofusca -- deja el cuerpo de cada paquete revuelto.

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

#include "BlockCipher099B.h"
#include "StreamCipher099B.h"

namespace Mu099B
{

/// Un paquete ya listo para despachar.
struct DecodedPacket
{
    std::vector<uint8_t> Data;
    /// Número de serie que venía en el bloque cifrado, o -1 si el paquete
    /// llegó en claro.
    int Serial = -1;
    bool WasEncrypted = false;
};

class GameFramer
{
public:
    GameFramer(const StreamCipher& streamCipher, const BlockCipher& blockCipher,
               size_t maxPacketSize = 8192);

    /// Alimenta bytes crudos del socket y saca los paquetes completos que haya.
    /// Devuelve false ante una violación de protocolo (cabecera desconocida,
    /// tamaño imposible, checksum inválido); en ese caso el flujo quedó
    /// desincronizado y hay que cortar la conexión, igual que hace el servidor.
    bool Feed(const uint8_t* data, size_t length, std::vector<DecodedPacket>& packets);

    /// Descripción del último fallo, para el log.
    const std::string& LastError() const { return _lastError; }

    /// Bytes de un paquete incompleto que quedaron esperando más datos.
    size_t Pending() const { return _size; }

private:
    bool Fail(std::string reason);

    StreamCipher _streamCipher;
    BlockCipher _blockCipher;
    std::vector<uint8_t> _buffer;
    size_t _size = 0;
    std::string _lastError;
};

}  // namespace Mu099B

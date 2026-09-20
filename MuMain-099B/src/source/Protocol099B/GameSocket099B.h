// Socket nativo del GameServer en el protocolo 0.99B.
//
// Reemplaza a la conexión de la librería C# para el socket de juego. No es una
// preferencia de estilo: el cifrado de flujo de 0.99B tapa también los bytes de
// tipo y tamaño, así que ningún framer que trabaje sobre el flujo cifrado puede
// separar paquetes. El framing tiene que ocurrir después de descifrar, y eso
// obliga a manejar el socket acá.
//
// Modelo de uso: socket no bloqueante, consultado una vez por cuadro con
// Poll(). Sin hilos ni callbacks -- el cliente ya drena su cola de paquetes en
// el bucle principal, así que sumar un hilo sólo agregaría sincronización sin
// ganar nada.

#pragma once

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

#include "GameEncoder099B.h"
#include "GameFramer099B.h"

namespace Mu099B
{

class GameSocket
{
public:
    GameSocket();
    ~GameSocket();

    GameSocket(const GameSocket&) = delete;
    GameSocket& operator=(const GameSocket&) = delete;

    /// Conecta cargando las claves de disco. `serverSerial` es el del .ini del
    /// servidor; `encryptionKeyPath` y `decryptionKeyPath` son Enc1.dat y
    /// Dec2.dat del cliente. Devuelve false si no se pudo conectar o si falta
    /// alguna clave -- nunca sigue con claves a medias.
    bool Connect(const std::string& host, uint16_t port, const std::string& serverSerial,
                 const std::string& encryptionKeyPath, const std::string& decryptionKeyPath);

    /// Conecta con las capas de cifrado ya armadas. Es el camino que usa el
    /// test de bucle local y sirve para reusar claves ya cargadas.
    bool Connect(const std::string& host, uint16_t port, const StreamCipher& streamCipher,
                 const BlockCipher& blockCipher);

    /// Codifica y manda un paquete lógico. Devuelve false si la conexión se
    /// cortó o el paquete no se pudo escribir entero.
    bool Send(const uint8_t* logicalPacket, size_t length);

    /// Lee lo que haya llegado y agrega los paquetes completos a `packets`.
    /// Devuelve false si la conexión se cerró o el flujo quedó desincronizado;
    /// en ese caso hay que cerrar, igual que hace el servidor.
    bool Poll(std::vector<DecodedPacket>& packets);

    void Close();
    bool IsConnected() const;

    /// Motivo del último fallo, para el log.
    const std::string& LastError() const { return _lastError; }

private:
    bool Fail(std::string reason);

    struct Impl;
    std::unique_ptr<Impl> _impl;
    std::string _lastError;
};

}  // namespace Mu099B

// Cifrado por bloques (SimpleModulus) y ofuscación XorData del protocolo 0.99B.
//
// Puerto de CPacketManager (PacketManager.cpp) del emulador. Se aplica sólo a
// los paquetes marcados C3/C4; cada bloque lógico de 8 bytes se convierte en 11
// bytes de wire y viceversa, con tablas Modulus/Key/Xor de 4 elementos cargadas
// de archivos binarios.
//
// Del lado del cliente las tablas salen de Data/Enc1.dat (cifrar) y
// Data/Dec2.dat (descifrar) -- los mismos archivos que ya trae el cliente. Se
// leen tal cual: no se pueden inventar ni derivar.
//
// Nota verificada: estas tablas coinciden byte a byte con las claves por
// defecto de SimpleModulus de OpenMU. La incompatibilidad con la librería C#
// no está acá, sino en XorData (tabla distinta y encadenado en sentido
// contrario) y en el cifrado de flujo, que en OpenMU no existe.

#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

namespace Mu099B
{

/// Bloque lógico y bloque de wire del cifrado por bloques.
inline constexpr size_t PlainBlockSize = 8;
inline constexpr size_t CipherBlockSize = 11;

class BlockCipher
{
public:
    struct KeyTable
    {
        uint32_t Modulus[4];
        uint32_t Key[4];
        uint32_t Xor[4];
    };

    BlockCipher(const KeyTable& encryption, const KeyTable& decryption);

    /// Carga las dos tablas de sus archivos. Devuelve `nullopt` si alguno no
    /// existe o no tiene el encabezado esperado, en vez de seguir con basura.
    static std::optional<BlockCipher> LoadFromFiles(const std::string& encryptionKeyPath,
                                                    const std::string& decryptionKeyPath);

    /// Lee una tabla suelta (útil para tests y diagnóstico).
    static std::optional<KeyTable> LoadKey(const std::string& path);

    /// Cifra un largo arbitrario en bloques de 8 -> 11 bytes.
    std::vector<uint8_t> Encrypt(const uint8_t* source, size_t length) const;

    /// Descifra un múltiplo de 11 bytes. Devuelve `nullopt` si algún bloque
    /// falla el checksum, que es lo que en el original desconecta al cliente.
    std::optional<std::vector<uint8_t>> Decrypt(const uint8_t* source, size_t length) const;

private:
    void EncryptBlock(uint8_t* target, const uint8_t* source8, size_t size) const;
    int DecryptBlock(uint8_t* target, const uint8_t* source11) const;

    KeyTable _encryption;
    KeyTable _decryption;
};

/// XorData: des-ofuscación XOR encadenada sobre los bytes que siguen al
/// encabezado. En el emulador vive dentro de ExtractPacket, así que se aplica
/// SÓLO al recibir -- tanto a los C1/C2 planos como al resultado ya descifrado
/// de un bloque C3/C4.
void DeobfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength);

/// Contraparte de envío. No existe en el servidor (allá XorData es sólo de
/// recepción), pero el cliente tiene que aplicarla para que la des-ofuscación
/// del servidor reconstruya el paquete original.
void ObfuscateInPlace(uint8_t* buffer, size_t size, size_t headerLength);

}  // namespace Mu099B

// Cifrado de flujo del socket de GameServer en 0.99B.
//
// Puerto de HackCheck.cpp (InitHackCheck / EncryptData / DecryptData) del
// emulador. Se aplica a TODO lo que entra y sale del socket del GameServer,
// **incluidos los bytes de tipo y tamaño de cada paquete**, así que corre antes
// de cualquier intento de separar paquetes. Ni el ConnectServer ni el
// JoinServer ni el DataServer lo usan.
//
// Es la razón por la que el framer de la librería C# no sirve para este socket:
// vería los encabezados cifrados y no podría segmentar. Por eso el framing de
// esta conexión tiene que ser nativo.
//
// Sin estado: cada byte se transforma de forma independiente, así que no
// importa en cuántos pedazos llegue el flujo.

#pragma once

#include <cstddef>
#include <cstdint>

namespace Mu099B
{

/// El ServerSerial es un campo de 17 bytes: los 16 caracteres configurados más
/// el terminador. El servidor rellena hasta ese largo antes de derivar la
/// clave, así que el byte nulo es material de clave como cualquier otro.
/// Derivar sobre los 16 caracteres da una key1 distinta y el flujo entero sale
/// mal -- pasó, y no lo agarró ningún test offline porque cliente y servidor
/// tienen que coincidir con el *original*, no entre ellos.
constexpr size_t ServerSerialFieldSize = 17;

class StreamCipher
{
public:
    /// Claves ya derivadas. `mhpKey1`/`mhpKey2` en cero desactivan la segunda
    /// capa (es lo habitual: los .ini traen ServerEncDecKey1/2 = 0).
    StreamCipher(uint8_t key1, uint8_t key2, uint8_t mhpKey1 = 0, uint8_t mhpKey2 = 0);

    /// Deriva las claves como InitHackCheck(): del nombre de cliente fijo "SSE"
    /// (32 bytes, el resto en cero) combinado con el ServerSerial del .ini.
    ///
    /// `serialLength` es cuántos caracteres tiene el serial configurado, no el
    /// largo del campo: la función lo copia a un buffer de ServerSerialFieldSize
    /// bytes y completa con ceros, igual que el servidor. Pasar más caracteres
    /// que el campo los descarta.
    static StreamCipher FromServerSerial(const uint8_t* serverSerial, size_t serialLength,
                                         uint8_t mhpKey1 = 0, uint8_t mhpKey2 = 0);

    void Encrypt(uint8_t* data, size_t length) const;
    void Decrypt(uint8_t* data, size_t length) const;

    uint8_t Key1() const { return _key1; }
    uint8_t Key2() const { return _key2; }

private:
    void MhpEncrypt(uint8_t* data, size_t length) const;
    void MhpDecrypt(uint8_t* data, size_t length) const;

    uint8_t _key1;
    uint8_t _key2;
    uint8_t _mhpKey1;
    uint8_t _mhpKey2;
};

}  // namespace Mu099B

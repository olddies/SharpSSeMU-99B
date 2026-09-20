// Sonda del protocolo 0.99B contra un GameServer vivo.
//
// No es un test: los tests de tests/protocol099b/ verifican bytes y cifrado sin
// red, y tienen que poder correr en CI sin servidor. Esta herramienta cubre lo
// que aquellos no pueden -- que las claves, el cifrado de flujo, el de bloque y
// el ofuscado se combinen bien contra el servidor real -- y por eso se ejecuta
// a mano.
//
//   probe099b <host> <puerto> <cuenta> <clave> [serial]
//
// Hace login, pide la lista de personajes e imprime lo que llega. Si el cifrado
// estuviera mal, el servidor cortaría la conexión sin responder nada.

#include <winsock2.h>

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "Protocol099B/GameSocket099B.h"
#include "Protocol099B/CharSet099B.h"
#include "Protocol099B/Wire099B.h"

namespace
{

/// Nombre legible de los opcodes que esta sonda espera ver. Cualquier otro sale
/// en crudo: la idea es reconocer el camino feliz, no decodificar todo.
const char* OpcodeName(uint8_t head, uint8_t sub)
{
    if (head == 0xF1 && sub == 0x00) return "handshake / bienvenida";
    if (head == 0xF1 && sub == 0x01) return "resultado de login";
    if (head == 0xF3 && sub == 0x00) return "lista de personajes";
    if (head == 0xF3 && sub == 0x01) return "personaje creado";
    if (head == 0xF3 && sub == 0x03) return "entrada al mundo";
    if (head == 0xF3 && sub == 0x10) return "inventario";
    if (head == 0xF3 && sub == 0x11) return "lista de skills";
    if (head == 0x12) return "aparece un jugador en la vista";
    if (head == 0x13) return "aparece un monstruo en la vista";
    if (head == 0x27) return "vida y mana";
    if (head == 0xD7) return "movimiento";
    if (head == 0xF3 && sub == 0x13) return "equipo actualizado (CharSet)";
    if (head == 0x24) return "resultado de mover item";
    if (head == 0x0D) return "mensaje del servidor";
    return nullptr;
}

/// Desarma PMSG_ITEM_LIST_SEND: el conteo y despues seis bytes por slot
/// ocupado. Es lo que verifica que DecodeItemInfo entienda al servidor real.
void DumpInventory(const std::vector<uint8_t>& bytes)
{
    // C2 lleva el tamano en dos bytes, asi que el cuerpo empieza en el 5.
    constexpr size_t BodyStart = 5;
    if (bytes.size() <= BodyStart)
    {
        return;
    }

    const uint8_t count = bytes[BodyStart];
    std::printf("     %u slot(s) ocupado(s)\n", count);

    size_t offset = BodyStart + 1;
    for (uint8_t i = 0; i < count; ++i)
    {
        if (offset + 6 > bytes.size())
        {
            std::printf("     !! el conteo no coincide con el tamano\n");
            return;
        }

        const uint8_t slot = bytes[offset];
        const auto item = Mu099B::DecodeItemInfo(&bytes[offset + 1]);
        offset += 6;

        std::printf("     slot %2u: indice %3u (grupo %u num %2u) +%u dur %u",
                    slot, item.Index, item.Group, item.Number, item.Level, item.Durability);
        if (item.Luck) std::printf(" suerte");
        if (item.Skill) std::printf(" skill");
        if (item.OptionLevel != 0) std::printf(" opcion+%u", item.OptionLevel * 4);
        if (item.ExcellentFlags != 0) std::printf(" exc:%02X", item.ExcellentFlags);
        std::printf("  -> indice de cliente %u\n", Mu099B::ToClientItemIndex(item.Index));
    }
}

void DumpPacket(const Mu099B::DecodedPacket& packet)
{
    const auto& bytes = packet.Data;
    if (bytes.empty())
    {
        std::printf("  (paquete vacio)\n");
        return;
    }

    // El sub-opcode existe sólo en las cabeceras extendidas (0xF1 y siguientes);
    // para el resto el byte 3 es ya carga util.
    const size_t headIndex = (bytes[0] == 0xC1 || bytes[0] == 0xC3) ? 2 : 3;
    const uint8_t head = headIndex < bytes.size() ? bytes[headIndex] : 0;
    const uint8_t sub = headIndex + 1 < bytes.size() ? bytes[headIndex + 1] : 0;

    const char* name = OpcodeName(head, sub);
    std::printf("  <- %02X:%02X  %zu bytes  %s  [%s]\n", head, sub, bytes.size(),
                name != nullptr ? name : "(sin identificar)",
                packet.WasEncrypted ? "cifrado" : "en claro");

    std::printf("     ");
    for (size_t i = 0; i < bytes.size() && i < 48; ++i)
    {
        std::printf("%02X ", bytes[i]);
    }
    if (bytes.size() > 48)
    {
        std::printf("...");
    }
    std::printf("\n");

    if (head == 0xF3 && sub == 0x10)
    {
        DumpInventory(bytes);
    }
}

/// Consulta el socket hasta que llegue algo o se agote la espera. Devuelve
/// false si la conexión se cortó, que es como se manifiesta un error de
/// cifrado: el servidor no contesta, cierra.
bool WaitForPackets(Mu099B::GameSocket& socket, int milliseconds)
{
    const int stepMs = 50;
    for (int waited = 0; waited < milliseconds; waited += stepMs)
    {
        std::vector<Mu099B::DecodedPacket> packets;
        if (!socket.Poll(packets))
        {
            std::printf("  !! conexion cerrada: %s\n", socket.LastError().c_str());
            return false;
        }

        if (!packets.empty())
        {
            for (const auto& packet : packets)
            {
                DumpPacket(packet);
            }
            return true;
        }

        ::Sleep(stepMs);
    }

    std::printf("  (sin respuesta en %d ms)\n", milliseconds);
    return true;
}

}  // namespace

int main(int argc, char** argv)
{
    if (argc < 5)
    {
        std::printf("uso: probe099b <host> <puerto> <cuenta> <clave> [serial]\n");
        return 2;
    }

    const std::string host = argv[1];
    const auto port = static_cast<uint16_t>(std::atoi(argv[2]));
    const std::string account = argv[3];
    const std::string password = argv[4];
    const std::string serial = argc > 5 ? argv[5] : "PoweredSetecSoft";

    Mu099B::GameSocket socket;
    std::printf("conectando a %s:%u (serial \"%s\")\n", host.c_str(), port, serial.c_str());

    if (!socket.Connect(host, port, serial, "Data/Enc1.dat", "Data/Dec2.dat"))
    {
        std::printf("no conecto: %s\n", socket.LastError().c_str());
        return 1;
    }
    std::printf("conectado.\n\n");

    // El servidor saluda primero; ese saludo ya viaja cifrado, asi que leerlo
    // bien es la primera prueba real de que las claves sirven.
    std::printf("[saludo del servidor]\n");
    if (!WaitForPackets(socket, 3000))
    {
        return 1;
    }

    const Mu099B::BYTE version[] = {'1', '0', '2', '0', '0'};
    const Mu099B::BYTE clientSerial[] = "PoweredSetecSoft";

    std::printf("\n[login como \"%s\"]\n", account.c_str());
    const auto login = Mu099B::BuildLoginRequest(account.c_str(), password.c_str(),
                                                 ::GetTickCount(), version, clientSerial);
    if (!socket.Send(reinterpret_cast<const uint8_t*>(&login), sizeof(login)))
    {
        std::printf("no se pudo mandar el login: %s\n", socket.LastError().c_str());
        return 1;
    }
    if (!WaitForPackets(socket, 5000))
    {
        return 1;
    }

    std::printf("\n[lista de personajes]\n");
    const auto listRequest = Mu099B::BuildCharacterListRequest();
    if (!socket.Send(reinterpret_cast<const uint8_t*>(&listRequest), sizeof(listRequest)))
    {
        std::printf("no se pudo pedir la lista: %s\n", socket.LastError().c_str());
        return 1;
    }
    if (!WaitForPackets(socket, 5000))
    {
        return 1;
    }

    // Entrar al mundo es lo que ejercita los campos de 32 bits del paquete de
    // join, que en este dialecto son de 32 y no de 16.
    // Crear un personaje es lo que ejercita el byte de clase, que tiene un
    // empaquetado distinto al del CharSet. Si estuviera mal, el servidor
    // contesta result=2 ("cuenta llena") aunque la cuenta esté vacía.
    // Un guion en el nombre significa "no crear nada": los argumentos son
    // posicionales y hay que poder saltear este paso.
    const bool wantsCreate = argc > 7 && argv[6][0] != 0 && argv[6][0] != '-';
    if (wantsCreate)
    {
        std::printf("\n[crear personaje \"%s\" de clase base %s]\n", argv[6], argv[7]);
        const auto classByte = Mu099B::MakeDatabaseClassByte(
            static_cast<Mu099B::BYTE>(std::atoi(argv[7])));
        std::printf("     byte de clase enviado: %u\n", classByte);

        const auto create = Mu099B::BuildCharacterCreateRequest(argv[6], classByte);
        if (!socket.Send(reinterpret_cast<const uint8_t*>(&create), sizeof(create)))
        {
            std::printf("no se pudo crear: %s\n", socket.LastError().c_str());
            return 1;
        }
        if (!WaitForPackets(socket, 5000))
        {
            return 1;
        }
    }

    // Si se pidió crear un personaje se entra con ese; si no, con el que diga
    // argv[8], y en último caso con el nombre de siempre.
    const char* enterName = argc > 8 ? argv[8] : (wantsCreate ? argv[6] : "Hero1");
    std::printf("\n[entrar al mundo como \"%s\"]\n", enterName);
    const auto select = Mu099B::BuildCharacterSelectRequest(enterName);
    if (!socket.Send(reinterpret_cast<const uint8_t*>(&select), sizeof(select)))
    {
        std::printf("no se pudo seleccionar el personaje: %s\n", socket.LastError().c_str());
        return 1;
    }

    // Al entrar, el servidor manda varios paquetes seguidos (datos del
    // personaje, inventario, vista), asi que se sigue leyendo un rato en vez
    // de cortar con el primero.
    for (int round = 0; round < 4; ++round)
    {
        if (!WaitForPackets(socket, 2000))
        {
            return 1;
        }
    }

    if (argc > 10)
    {
        const auto from = static_cast<Mu099B::BYTE>(std::atoi(argv[9]));
        const auto to = static_cast<Mu099B::BYTE>(std::atoi(argv[10]));
        std::printf("\n[mover item del slot %u al %u]\n", from, to);

        // El servidor ignora el ItemInfo del pedido: sólo mira los slots.
        const auto move = Mu099B::BuildItemMoveRequest(
            Mu099B::ItemContainer::Inventory, from, nullptr,
            Mu099B::ItemContainer::Inventory, to);
        if (!socket.Send(reinterpret_cast<const uint8_t*>(&move), sizeof(move)))
        {
            std::printf("no se pudo mover: %s\n", socket.LastError().c_str());
            return 1;
        }
        for (int round = 0; round < 3; ++round)
        {
            if (!WaitForPackets(socket, 2000))
            {
                return 1;
            }
        }
    }

    std::printf("\nlisto.\n");
    socket.Close();
    return 0;
}

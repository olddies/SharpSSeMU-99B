// Probe of the 0.99B protocol against a live GameServer. It is not a test: the tests in tests/protocol099b/
// verify bytes and encryption without a network, and have to be able to run in CI without a server. This tool
// covers what those cannot -- that the keys, the stream cipher, the block cipher and the obfuscation combine
// correctly against the real server -- and that is why it is run by hand. probe099b <host> <port> <account>
// <password> [serial] It logs in, asks for the character list and prints what arrives. If the encryption were
// wrong, the server would cut the connection without answering anything.

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

/// Readable name of the opcodes this probe expects to see. Any other comes out raw: the idea is to recognise
/// the happy path, not to decode everything.
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

/// Unpacks PMSG_ITEM_LIST_SEND: the count and then six bytes per occupied slot. It is what verifies that
/// DecodeItemInfo understands the real server.
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

    // The sub-opcode only exists in the extended headers (0xF1 onwards); for the rest byte 3 is already
    // payload.
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

/// Polls the socket until something arrives or the wait runs out. Returns false if the connection was cut,
/// which is how an encryption error shows up: the server does not answer, it closes.
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
    const std::string serial = argc > 5 ? argv[5] : "SharpSSeMU99B-v1";

    Mu099B::GameSocket socket;
    std::printf("conectando a %s:%u (serial \"%s\")\n", host.c_str(), port, serial.c_str());

    if (!socket.Connect(host, port, serial, "Data/Enc1.dat", "Data/Dec2.dat"))
    {
        std::printf("no conecto: %s\n", socket.LastError().c_str());
        return 1;
    }
    std::printf("conectado.\n\n");

    // The server greets first; that greeting already travels encrypted, so reading it correctly is the first
    // real proof that the keys work.
    std::printf("[saludo del servidor]\n");
    if (!WaitForPackets(socket, 3000))
    {
        return 1;
    }

    const Mu099B::BYTE version[] = {'1', '0', '2', '0', '0'};
    const Mu099B::BYTE clientSerial[] = "SharpSSeMU99B-v1";

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

    // Entering the world is what exercises the 32-bit fields of the join packet, which in this dialect are 32
    // and not 16. Creating a character is what exercises the class byte, which has a different packing from the
    // CharSet's. If it were wrong, the server answers result=2 ("account full") even though the account is
    // empty. A dash in the name means "create nothing": the arguments are positional and this step has to be
    // skippable.
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

    // If a character creation was requested, it enters with that one; otherwise, with the one argv[8] says, and
    // lastly with the usual name.
    const char* enterName = argc > 8 ? argv[8] : (wantsCreate ? argv[6] : "Hero1");
    std::printf("\n[entrar al mundo como \"%s\"]\n", enterName);
    const auto select = Mu099B::BuildCharacterSelectRequest(enterName);
    if (!socket.Send(reinterpret_cast<const uint8_t*>(&select), sizeof(select)))
    {
        std::printf("no se pudo seleccionar el personaje: %s\n", socket.LastError().c_str());
        return 1;
    }

    // On entering, the server sends several packets in a row (character data, inventory, view), so it keeps
    // reading for a while instead of stopping at the first.
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

        // The server ignores the request's ItemInfo: it only looks at the slots.
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

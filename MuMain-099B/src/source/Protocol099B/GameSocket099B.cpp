#include "GameSocket099B.h"

#include <optional>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
using SocketHandle = SOCKET;
constexpr SocketHandle InvalidSocket = INVALID_SOCKET;
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>
using SocketHandle = int;
constexpr SocketHandle InvalidSocket = -1;
#endif

namespace Mu099B
{

namespace
{

/// Tamaño de lectura por llamada. Lo que entre se le pasa entero al framer, que
/// ya sabe acumular paquetes partidos.
constexpr size_t ReceiveChunk = 4096;

bool WouldBlock()
{
#ifdef _WIN32
    return WSAGetLastError() == WSAEWOULDBLOCK;
#else
    return errno == EAGAIN || errno == EWOULDBLOCK;
#endif
}

void CloseSocket(SocketHandle handle)
{
#ifdef _WIN32
    closesocket(handle);
#else
    close(handle);
#endif
}

bool SetNonBlocking(SocketHandle handle)
{
#ifdef _WIN32
    u_long mode = 1;
    return ioctlsocket(handle, FIONBIO, &mode) == 0;
#else
    const int flags = fcntl(handle, F_GETFL, 0);
    return flags != -1 && fcntl(handle, F_SETFL, flags | O_NONBLOCK) != -1;
#endif
}

#ifdef _WIN32
/// Winsock necesita inicializarse una vez por proceso. El cliente ya lo hace
/// para su propia red, pero WSAStartup lleva cuenta de referencias, así que
/// llamarlo de nuevo es correcto y evita depender del orden de arranque.
struct WinsockScope
{
    WinsockScope()
    {
        WSADATA data;
        Ok = WSAStartup(MAKEWORD(2, 2), &data) == 0;
    }

    ~WinsockScope()
    {
        if (Ok)
        {
            WSACleanup();
        }
    }

    bool Ok = false;
};
#endif

}  // namespace

struct GameSocket::Impl
{
#ifdef _WIN32
    WinsockScope Winsock;
#endif
    SocketHandle Handle = InvalidSocket;
    std::optional<GameFramer> Framer;
    std::optional<GameEncoder> Encoder;
};

GameSocket::GameSocket() : _impl(std::make_unique<Impl>()) {}

GameSocket::~GameSocket()
{
    Close();
}

bool GameSocket::Fail(std::string reason)
{
    _lastError = std::move(reason);
    return false;
}

bool GameSocket::Connect(const std::string& host, uint16_t port, const std::string& serverSerial,
                         const std::string& encryptionKeyPath,
                         const std::string& decryptionKeyPath)
{
    const auto blockCipher = BlockCipher::LoadFromFiles(encryptionKeyPath, decryptionKeyPath);
    if (!blockCipher)
    {
        Close();
        return Fail("no se pudieron cargar las claves de cifrado (" + encryptionKeyPath + " / " +
                    decryptionKeyPath + ")");
    }

    const auto streamCipher = StreamCipher::FromServerSerial(
        reinterpret_cast<const uint8_t*>(serverSerial.data()), serverSerial.size());

    return Connect(host, port, streamCipher, *blockCipher);
}

bool GameSocket::Connect(const std::string& host, uint16_t port, const StreamCipher& streamCipher,
                         const BlockCipher& blockCipher)
{
    Close();

#ifdef _WIN32
    if (!_impl->Winsock.Ok)
    {
        return Fail("no se pudo inicializar Winsock");
    }
#endif

    addrinfo hints{};
    hints.ai_family = AF_INET;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* resolved = nullptr;
    const std::string portText = std::to_string(port);
    if (getaddrinfo(host.c_str(), portText.c_str(), &hints, &resolved) != 0 || resolved == nullptr)
    {
        return Fail("no se pudo resolver " + host + ":" + portText);
    }

    SocketHandle handle = InvalidSocket;
    for (addrinfo* it = resolved; it != nullptr; it = it->ai_next)
    {
        handle = socket(it->ai_family, it->ai_socktype, it->ai_protocol);
        if (handle == InvalidSocket)
        {
            continue;
        }

        // Se conecta en modo bloqueante y recién después se pasa a no
        // bloqueante: así el resultado de la conexión es inmediato y no hay que
        // andar sondeando un connect en progreso.
        if (::connect(handle, it->ai_addr, static_cast<int>(it->ai_addrlen)) == 0)
        {
            break;
        }

        CloseSocket(handle);
        handle = InvalidSocket;
    }
    freeaddrinfo(resolved);

    if (handle == InvalidSocket)
    {
        return Fail("no se pudo conectar a " + host + ":" + portText);
    }

    if (!SetNonBlocking(handle))
    {
        CloseSocket(handle);
        return Fail("no se pudo poner el socket en modo no bloqueante");
    }

    // Sin Nagle: los paquetes del protocolo son chicos y se notaría como lag.
    int noDelay = 1;
    setsockopt(handle, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&noDelay),
               sizeof(noDelay));

    _impl->Handle = handle;
    _impl->Framer.emplace(streamCipher, blockCipher);
    _impl->Encoder.emplace(streamCipher, blockCipher);
    _lastError.clear();
    return true;
}

bool GameSocket::Send(const uint8_t* logicalPacket, size_t length)
{
    if (!IsConnected())
    {
        return Fail("no hay conexión");
    }

    const auto wire = _impl->Encoder->Encode(logicalPacket, length);
    if (wire.empty())
    {
        return Fail("el paquete a enviar es inválido");
    }

    size_t sent = 0;
    while (sent < wire.size())
    {
        const int written = ::send(_impl->Handle, reinterpret_cast<const char*>(wire.data() + sent),
                                   static_cast<int>(wire.size() - sent), 0);
        if (written > 0)
        {
            sent += static_cast<size_t>(written);
            continue;
        }

        if (written < 0 && WouldBlock())
        {
            // Buffer de salida lleno. Es raro con paquetes de este tamaño; se
            // reintenta en vez de perder el paquete a medio escribir, que
            // dejaría el flujo del servidor desincronizado.
            continue;
        }

        Close();
        return Fail("se cortó la conexión al enviar");
    }

    return true;
}

bool GameSocket::Poll(std::vector<DecodedPacket>& packets)
{
    if (!IsConnected())
    {
        return false;
    }

    uint8_t chunk[ReceiveChunk];

    while (true)
    {
        const int received =
            ::recv(_impl->Handle, reinterpret_cast<char*>(chunk), static_cast<int>(sizeof(chunk)), 0);

        if (received > 0)
        {
            if (!_impl->Framer->Feed(chunk, static_cast<size_t>(received), packets))
            {
                const std::string reason = _impl->Framer->LastError();
                Close();
                return Fail(reason);
            }
            continue;
        }

        if (received == 0)
        {
            Close();
            return Fail("el servidor cerró la conexión");
        }

        if (WouldBlock())
        {
            return true;  // no hay más datos por ahora
        }

        Close();
        return Fail("error leyendo del socket");
    }
}

void GameSocket::Close()
{
    if (_impl->Handle != InvalidSocket)
    {
        CloseSocket(_impl->Handle);
        _impl->Handle = InvalidSocket;
    }
    _impl->Framer.reset();
    _impl->Encoder.reset();
}

bool GameSocket::IsConnected() const
{
    return _impl->Handle != InvalidSocket;
}

}  // namespace Mu099B

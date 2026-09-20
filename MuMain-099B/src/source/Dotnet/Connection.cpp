#include "stdafx.h"
#include <map>

#include "Connection.h"

#include <string>

#include "Protocol099B/GameSocket099B.h"

// Declared without a size on purpose: the constant lives in WSclient.h, which in turn includes this header, and
// it is not worth creating the circular dependency just for a number.
extern BYTE Serial[];

namespace
{

/// The host comes as wchar_t from the client; getaddrinfo wants bytes. Addresses are ASCII, so truncating is
/// enough.
std::string NarrowHost(const wchar_t* host)
{
    std::string result;
    for (const wchar_t* p = host; p != nullptr && *p != 0; ++p)
    {
        result.push_back(static_cast<char>(*p));
    }
    return result;
}

}  // namespace

#include "PacketBindings_ChatServer.h"
#include "PacketBindings_ConnectServer.h"
#include "PacketBindings_ClientToServer.h"

std::map<int32_t, Connection*> connections;

namespace DotNetBridge
{
bool g_dotnetErrorDisplayed = false;

void ReportDotNetError(const char* detail)
{
    if (g_dotnetErrorDisplayed)
    {
        return;
    }
    g_dotnetErrorDisplayed = true;

    wchar_t buffer[512];
    std::swprintf(buffer, std::size(buffer),
        L"Failed to initialize the managed client library (%hs). The game client cannot connect to the server.",
        detail ? detail : "unknown error");
#ifdef _WIN32
    MessageBoxW(nullptr, buffer, L"MuMainClient", MB_ICONERROR | MB_OK);
#else
    wprintf(L"%ls\n", buffer);
#endif
}

bool IsManagedLibraryAvailable()
{
    if (munique_client_library_handle)
    {
        return true;
    }

    ReportDotNetError("MUnique.Client.Library.dll missing");
    return false;
}
}

using DotNetBridge::ReportDotNetError;
using DotNetBridge::IsManagedLibraryAvailable;

using onPacketReceived = void(int32_t, int32_t, BYTE*);
using onDisconnected = void(int32_t);

typedef int32_t(CORECLR_DELEGATE_CALLTYPE* Connect)(const wchar_t*, int32_t, BYTE, onPacketReceived, onDisconnected);
typedef void(CORECLR_DELEGATE_CALLTYPE* Disconnect)(int32_t);
typedef void(CORECLR_DELEGATE_CALLTYPE* BeginReceive)(int32_t);
typedef void(CORECLR_DELEGATE_CALLTYPE* Send)(int32_t, const BYTE*, int32_t);

Connect dotnet_connect = LoadManagedSymbol<Connect>("ConnectionManager_Connect");

Disconnect dotnet_disconnect = LoadManagedSymbol<Disconnect>("ConnectionManager_Disconnect");

BeginReceive dotnet_beginreceive = LoadManagedSymbol<BeginReceive>("ConnectionManager_BeginReceive");

Send dotnet_send = LoadManagedSymbol<Send>("ConnectionManager_Send");

void Connection::OnPacketReceivedS(const int32_t handle, const int32_t size, BYTE* data)
{
    const auto it = connections.find(handle);
    if (it == connections.end())
    {
        return;
    }

    if (Connection* connection = it->second)
    {
        connection->OnPacketReceived(data, size);
    }
}

void Connection::OnDisconnectedS(const int32_t handle)
{
    const auto it = connections.find(handle);
    if (it == connections.end())
    {
        return;
    }

    if (Connection* connection = it->second)
    {
        connection->OnDisconnected();
    }
}

Connection::Connection(const wchar_t* host, int32_t port, bool isEncrypted, void(*packetHandler)(int32_t, const BYTE*, int32_t))
{
    this->_packetHandler = packetHandler;
    if (!dotnet_connect)
    {
        ReportDotNetError("ConnectionManager_Connect");
        this->_handle = 0;
        return;
    }

    if (isEncrypted)
    {
        // Game connection: native transport. The keys come from the client's own Data/ and the serial has to be
        // the same as the server's ServerSerial in its .ini, because the stream cipher is derived from it.
        auto* native = new Mu099B::GameSocket();

        const std::string hostUtf8 = NarrowHost(host);
        // The 16 characters of the serial; FromServerSerial pads them to the 17 bytes of the field, which is
        // what the server derives the key from.
        constexpr size_t ProtocolSerialSize = 16;
        const std::string serial(reinterpret_cast<const char*>(Serial), ProtocolSerialSize);

        if (native->Connect(hostUtf8, static_cast<uint16_t>(port), serial,
                            "Data/Enc1.dat", "Data/Dec2.dat"))
        {
            _nativeSocket = native;
            this->_handle = 1;  // symbolic handle: the native transport does not use the C# table
        }
        else
        {
            g_ErrorReport.Write(L"[Connection] socket nativo: %hs", native->LastError().c_str());
            delete native;
            this->_handle = 0;
            return;
        }
    }
    else
    {
        this->_handle = dotnet_connect(host, port, 0, &OnPacketReceivedS, &OnDisconnectedS);
    }

    if (IsConnected())
    {
        if (_nativeSocket == nullptr)
        {
            connections[this->_handle] = this;
            if (dotnet_beginreceive)
            {
                dotnet_beginreceive(this->_handle);
            }
        }

        _chatServer = new PacketFunctions_ChatServer();
        _connectServer = new PacketFunctions_ConnectServer();
        _gameServer = new PacketFunctions_ClientToServer();

        _chatServer->SetHandle(this->_handle);
        _connectServer->SetHandle(this->_handle);
        _gameServer->SetHandle(this->_handle);
        // Only the game connection has a native transport; the ConnectServer and chat still go through the C#
        // library.
        _gameServer->SetNativeTransport(_nativeSocket != nullptr);
    }
}

Connection::~Connection()
{
    if (!IsConnected())
    {
        return;
    }

    if (_nativeSocket != nullptr)
    {
        _nativeSocket->Close();
        delete _nativeSocket;
        _nativeSocket = nullptr;
    }
    else if (dotnet_disconnect)
    {
        dotnet_disconnect(_handle);
    }

    SAFE_DELETE(_chatServer);
    SAFE_DELETE(_connectServer);
    SAFE_DELETE(_gameServer);
}

bool Connection::IsConnected()
{
    return this->_handle > 0;
}

void Connection::Send(const BYTE* data, const int32_t size)
{
    if (!IsConnected())
    {
        return;
    }

    if (_nativeSocket != nullptr)
    {
        if (!_nativeSocket->Send(data, static_cast<size_t>(size)))
        {
            g_ErrorReport.Write(L"[Connection] fallo al enviar: %hs",
                _nativeSocket->LastError().c_str());
        }
        return;
    }

    if (!dotnet_send)
    {
        ReportDotNetError("ConnectionManager_Send");
        return;
    }

    dotnet_send(this->_handle, data, size);
}

void Connection::Close()
{
    if (!IsConnected())
    {
        return;
    }

    if (dotnet_disconnect)
    {
        dotnet_disconnect(this->_handle);
    }
}

void Connection::OnDisconnected()
{
    if (!IsConnected())
    {
        return;
    }

    connections.erase(this->_handle);
    this->_handle = 0;
}

void Connection::OnPacketReceived(const BYTE* data, const int32_t size)
{
    wprintf(L"Received packet, size %d", size);
    this->_packetHandler(this->_handle, data, size);
}

void Connection::Poll()
{
    if (_nativeSocket == nullptr || !_nativeSocket->IsConnected())
    {
        return;
    }

    std::vector<Mu099B::DecodedPacket> packets;
    if (!_nativeSocket->Poll(packets))
    {
        g_ErrorReport.Write(L"[Connection] se cortó la conexión: %hs",
            _nativeSocket->LastError().c_str());
        OnDisconnected();
        return;
    }

    for (const auto& packet : packets)
    {
        OnPacketReceived(packet.Data.data(), static_cast<int32_t>(packet.Data.size()));
    }
}

void PacketFunctions_Base::ReportNotPortedSend()
{
    g_ErrorReport.Write(
        L"[Connection] se intento enviar por una funcion que todavia no esta "
        L"portada a 0.99B. Esas van por la libreria C#, que no conoce el socket "
        L"nativo del GameServer: el paquete no se manda.\r\n");
}

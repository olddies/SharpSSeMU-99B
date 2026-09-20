#pragma once

#include "stdafx.h"

#include <coreclr_delegates.h>

#include "PacketFunctions_ChatServer.h"
#include "PacketFunctions_ConnectServer.h"
#include "PacketFunctions_ClientToServer.h"

#include <cwchar>

#ifdef _WIN32
#include "Core/Platform/WinCompat.h"
#define symLoad GetProcAddress
#else
#include "dlfcn.h"
#include <unistd.h>   // readlink
#include <string>
#define symLoad dlsym
#endif

// Construct-on-first-use: the library handle is loaded lazily on first access.
// The inline dotnet_* symbol globals (defined in other translation units) load
// through this handle during their own dynamic initialization, so a plain inline
// global here would risk a static-initialization-order fiasco. A function-local
// static is initialized on first call instead, which is well-defined. The macro
// keeps every existing call site (`munique_client_library_handle`) unchanged.
#ifdef _WIN32
inline HINSTANCE get_munique_client_library_handle()
{
    static const HINSTANCE handle = LoadLibrary(L"MUnique.Client.Library.dll");
    return handle;
}
#else
inline void* get_munique_client_library_handle()
{
    // Native AOT emits a platform-native shared object on Linux (.so), not the
    // Windows .dll, and the build copies it next to the executable. Resolve the
    // executable's real directory (via /proc/self/exe) and load by absolute
    // path, so it works regardless of the working directory the client was
    // launched from; fall back to the loader search path.
    // Not const-qualified return: dlsym() takes a non-const void* handle.
    static void* const handle = []() -> void* {
        char exe[4096];
        const ssize_t n = ::readlink("/proc/self/exe", exe, sizeof(exe) - 1);
        if (n > 0)
        {
            std::string path(exe, static_cast<size_t>(n));
            const std::string::size_type slash = path.find_last_of('/');
            if (slash != std::string::npos)
            {
                path.resize(slash + 1);
                path += "MUnique.Client.Library.so";
                if (void* h = dlopen(path.c_str(), RTLD_LAZY))
                    return h;
            }
        }
        return dlopen("MUnique.Client.Library.so", RTLD_LAZY);
    }();
    return handle;
}
#endif
#define munique_client_library_handle get_munique_client_library_handle()

namespace DotNetBridge
{
void ReportDotNetError(const char* detail);
bool IsManagedLibraryAvailable();

template<typename T>
T LoadManagedSymbol(const char* name)
{
    if (!IsManagedLibraryAvailable())
    {
        return nullptr;
    }

    const auto symbol = reinterpret_cast<T>(symLoad(munique_client_library_handle, name));
    if (!symbol)
    {
        ReportDotNetError(name);
    }

    return symbol;
}
}

using DotNetBridge::LoadManagedSymbol;

namespace Mu099B
{
class GameSocket;
}

/// Conexión al servidor.
///
/// Tiene DOS transportes, y cuál se usa depende de con quién se habla:
///
///  * **ConnectServer**: va por la librería C#. Su protocolo es plano (C1/C2 sin
///    cifrar) y el framing por tamaño es el mismo en los dos dialectos, así que
///    se puede reutilizar tal cual.
///  * **GameServer**: va por Mu099B::GameSocket, nativo. No es una preferencia:
///    el cifrado de flujo de 0.99B tapa también los bytes de tipo y tamaño, así
///    que ningún framer que trabaje sobre el flujo cifrado puede separar
///    paquetes. Hay que descifrar antes de segmentar, y eso obliga a manejar el
///    socket del lado nativo.
///
/// El resto del cliente no se entera: sigue usando Send() y recibiendo por la
/// misma cola de paquetes.
class Connection
{
private:
    static void OnPacketReceivedS(int32_t handle, int32_t size, BYTE* data);
    static void OnDisconnectedS(int32_t handle);

    PacketFunctions_ChatServer* _chatServer = { };
    PacketFunctions_ConnectServer* _connectServer = { };
    PacketFunctions_ClientToServer* _gameServer = { };

    int32_t _handle;
    void(*_packetHandler)(int32_t, const BYTE*, int32_t);

    /// Sólo para la conexión de juego; nulo para el ConnectServer.
    Mu099B::GameSocket* _nativeSocket = nullptr;

    void OnDisconnected();
    void OnPacketReceived(const BYTE* data, const int32_t length);

public:
    Connection(const wchar_t* host, int32_t port, bool isEncrypted, void(*packetHandler)(int32_t, const BYTE*, int32_t));
    ~Connection();

    bool IsConnected();
    void Send(const BYTE* data, const int32_t length);
    void Close();

    /// Lee del socket nativo y encola lo que haya llegado. Hay que llamarlo una
    /// vez por cuadro: a diferencia del transporte C#, que empuja desde su
    /// propio hilo, este socket es no bloqueante y se consulta.
    void Poll();

    /// True si esta conexión usa el transporte nativo de 0.99B.
    bool UsesNativeTransport() const { return _nativeSocket != nullptr; }

    int32_t GetHandle() const { return _handle; }

    PacketFunctions_ChatServer* ToChatServer() const { return _chatServer; }
    PacketFunctions_ConnectServer* ToConnectServer() const { return _connectServer; }
    PacketFunctions_ClientToServer* ToGameServer() const { return _gameServer; }
};

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

/// Connection to the server. It has TWO transports, and which one is used depends on who is being talked to: *
/// **ConnectServer**: goes through the C# library. Its protocol is plain (unencrypted C1/C2) and size framing
/// is the same in both dialects, so it can be reused as is. * **GameServer**: goes through Mu099B::GameSocket,
/// native. This is not a preference: the 0.99B stream cipher also covers the type and size bytes, so no framer
/// working on the encrypted stream can split packets. You have to decrypt before segmenting, and that forces
/// handling the socket on the native side. The rest of the client does not notice: it keeps using Send() and
/// receiving through the same packet queue.
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

    /// Only for the game connection; null for the ConnectServer.
    Mu099B::GameSocket* _nativeSocket = nullptr;

    void OnDisconnected();
    void OnPacketReceived(const BYTE* data, const int32_t length);

public:
    Connection(const wchar_t* host, int32_t port, bool isEncrypted, void(*packetHandler)(int32_t, const BYTE*, int32_t));
    ~Connection();

    bool IsConnected();
    void Send(const BYTE* data, const int32_t length);
    void Close();

    /// Reads from the native socket and queues whatever has arrived. It has to be called once per frame: unlike
    /// the C# transport, which pushes from its own thread, this socket is non-blocking and is polled.
    void Poll();

    /// True if this connection uses the native 0.99B transport.
    bool UsesNativeTransport() const { return _nativeSocket != nullptr; }

    int32_t GetHandle() const { return _handle; }

    PacketFunctions_ChatServer* ToChatServer() const { return _chatServer; }
    PacketFunctions_ConnectServer* ToConnectServer() const { return _connectServer; }
    PacketFunctions_ClientToServer* ToGameServer() const { return _gameServer; }
};

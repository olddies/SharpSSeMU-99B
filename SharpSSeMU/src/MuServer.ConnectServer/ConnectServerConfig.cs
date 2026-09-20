using MuServer.Shared.Config;

namespace MuServer.ConnectServer;

/// <summary>Lee ConnectServer.ini, mismo formato/claves que el original (drop-in compatible).</summary>
public sealed class ConnectServerConfig
{
    public ushort TcpPort { get; private init; }
    public ushort UdpPort { get; private init; }
    public int MaxConnectionPerIp { get; private init; }
    public int MaxPacketPerSecond { get; private init; }
    public int MaxConnectionIdleSeconds { get; private init; }

    public static ConnectServerConfig Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);

        return new ConnectServerConfig
        {
            TcpPort = (ushort)ini.GetInt("ConnectServerInfo", "ConnectServerPortTCP", 44405),
            UdpPort = (ushort)ini.GetInt("ConnectServerInfo", "ConnectServerPortUDP", 55557),
            MaxConnectionPerIp = ini.GetInt("ConnectServerInfo", "MaxConnectionPerIP", 0),
            MaxPacketPerSecond = ini.GetInt("ConnectServerInfo", "MaxPacketPerSecond", 0),
            MaxConnectionIdleSeconds = ini.GetInt("ConnectServerInfo", "MaxConnectionIdle", 60),
        };
    }
}

using MuServer.Shared.Config;

namespace MuServer.JoinServer.Config;

/// <summary>Reads JoinServer.ini (same keys as the original, except the database section: SQL Server's ODBC/DSN
/// is replaced by a PostgreSQL connection string).</summary>
public sealed class JoinServerConfig
{
    public required string PostgresConnectionString { get; init; }
    public ushort JoinServerPort { get; private init; }
    public string ConnectServerAddress { get; private init; } = "127.0.0.1";
    public ushort ConnectServerPort { get; private init; }
    public bool CaseSensitive { get; private init; }
    public bool Md5Encryption { get; private init; }

    public static JoinServerConfig Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);

        // JoinServerODBC in the original was the name of the SQL Server DSN. Here we reuse that same key to
        // store the full Postgres connection string, so as not to introduce a new key in the deployment .ini.
        var postgres = ini.GetString(
            "JoinServerInfo",
            "JoinServerPostgres",
            "Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver");

        return new JoinServerConfig
        {
            PostgresConnectionString = postgres,
            JoinServerPort = (ushort)ini.GetInt("JoinServerInfo", "JoinServerPort", 55970),
            ConnectServerAddress = ini.GetString("JoinServerInfo", "ConnectServerAddress", "127.0.0.1"),
            ConnectServerPort = (ushort)ini.GetInt("JoinServerInfo", "ConnectServerPort", 55557),
            CaseSensitive = ini.GetInt("JoinServerInfo", "CaseSensitive", 0) != 0,
            Md5Encryption = ini.GetInt("JoinServerInfo", "MD5Encryption", 0) != 0,
        };
    }
}

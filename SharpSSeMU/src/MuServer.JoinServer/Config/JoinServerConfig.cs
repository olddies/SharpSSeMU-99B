using MuServer.Shared.Config;

namespace MuServer.JoinServer.Config;

/// <summary>Lee JoinServer.ini (mismas claves que el original, salvo la sección de base de datos:
/// el ODBC/DSN de SQL Server se reemplaza por una cadena de conexión de PostgreSQL).</summary>
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

        // JoinServerODBC en el original era el nombre del DSN de SQL Server. Aquí reutilizamos esa
        // misma clave para guardar la cadena de conexión completa de Postgres, para no introducir
        // una clave nueva en el .ini de despliegue.
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

using MuServer.Shared.Config;

namespace MuServer.DataServer.Config;

public sealed class DataServerConfig
{
    public required string PostgresConnectionString { get; init; }
    public ushort DataServerPort { get; private init; }

    public static DataServerConfig Load(string iniPath)
    {
        var ini = IniFile.Load(iniPath);

        // Same as in JoinServer: we reuse the DataServerODBC key (formerly a SQL Server DSN) for the full
        // Postgres connection string.
        var postgres = ini.GetString(
            "DataServerInfo",
            "DataServerPostgres",
            "Host=127.0.0.1;Port=5432;Database=muonline;Username=muserver;Password=muserver");

        return new DataServerConfig
        {
            PostgresConnectionString = postgres,
            DataServerPort = (ushort)ini.GetInt("DataServerInfo", "DataServerPort", 55960),
        };
    }
}

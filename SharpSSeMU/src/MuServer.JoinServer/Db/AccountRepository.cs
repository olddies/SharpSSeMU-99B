namespace MuServer.JoinServer.Db;

public sealed record CredentialsRow(string Account, string Password);
public sealed record PersonalInfoRow(string PersonalCode, int BlockCode);
public sealed record AccountLevelRow(int AccountLevel, DateTime AccountExpire);

/// <summary> Account data layer: replaces QueryManager (ODBC/SQL Server) + the stored procedures
/// WZ_CONNECT_MEMB / WZ_DISCONNECT_MEMB / WZ_GetAccountLevel / WZ_SetAccountLevel, now against PostgreSQL with
/// parameterised queries (the original built the SQL with sprintf, vulnerable to injection — here it is fixed
/// without changing the observable behaviour). </summary>
public interface IAccountRepository
{
    Task<CredentialsRow?> GetCredentialsAsync(string account, CancellationToken ct);
    Task<PersonalInfoRow?> GetPersonalInfoAsync(string account, CancellationToken ct);

    /// <summary>Port of WZ_GetAccountLevel: if the level expired, it resets it to 0 and persists.</summary>
    Task<AccountLevelRow> GetAccountLevelAsync(string account, CancellationToken ct);

    /// <summary>Puerto de WZ_SetAccountLevel: mismo nivel => extiende vencimiento; nivel distinto => lo reinicia.</summary>
    Task SetAccountLevelAsync(string account, int accountLevel, int accountExpireSeconds, CancellationToken ct);

    /// <summary>Puerto de WZ_CONNECT_MEMB: upsert de memb_stat marcando la cuenta como conectada.</summary>
    Task ConnectMembAsync(string account, string serverName, string ipAddress, CancellationToken ct);

    /// <summary>Puerto de WZ_DISCONNECT_MEMB: marca la cuenta como desconectada si existe en memb_stat.</summary>
    Task DisconnectMembAsync(string account, CancellationToken ct);
}

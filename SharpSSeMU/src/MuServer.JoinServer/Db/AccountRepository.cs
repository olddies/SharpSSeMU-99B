namespace MuServer.JoinServer.Db;

public sealed record CredentialsRow(string Account, string Password);
public sealed record PersonalInfoRow(string PersonalCode, int BlockCode);
public sealed record AccountLevelRow(int AccountLevel, DateTime AccountExpire);

/// <summary>
/// Capa de datos de cuentas: reemplaza a QueryManager (ODBC/SQL Server) + los stored procedures
/// WZ_CONNECT_MEMB / WZ_DISCONNECT_MEMB / WZ_GetAccountLevel / WZ_SetAccountLevel, ahora contra
/// PostgreSQL con consultas parametrizadas (el original armaba el SQL con sprintf, vulnerable a
/// inyección — acá se corrige sin cambiar el comportamiento observable).
/// </summary>
public interface IAccountRepository
{
    Task<CredentialsRow?> GetCredentialsAsync(string account, CancellationToken ct);
    Task<PersonalInfoRow?> GetPersonalInfoAsync(string account, CancellationToken ct);

    /// <summary>Puerto de WZ_GetAccountLevel: si el nivel expiró, lo resetea a 0 y persiste.</summary>
    Task<AccountLevelRow> GetAccountLevelAsync(string account, CancellationToken ct);

    /// <summary>Puerto de WZ_SetAccountLevel: mismo nivel => extiende vencimiento; nivel distinto => lo reinicia.</summary>
    Task SetAccountLevelAsync(string account, int accountLevel, int accountExpireSeconds, CancellationToken ct);

    /// <summary>Puerto de WZ_CONNECT_MEMB: upsert de memb_stat marcando la cuenta como conectada.</summary>
    Task ConnectMembAsync(string account, string serverName, string ipAddress, CancellationToken ct);

    /// <summary>Puerto de WZ_DISCONNECT_MEMB: marca la cuenta como desconectada si existe en memb_stat.</summary>
    Task DisconnectMembAsync(string account, CancellationToken ct);
}

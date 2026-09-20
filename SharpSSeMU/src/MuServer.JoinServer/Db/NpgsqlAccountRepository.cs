using Npgsql;

namespace MuServer.JoinServer.Db;

public sealed class NpgsqlAccountRepository : IAccountRepository
{
    private readonly string _connectionString;

    public NpgsqlAccountRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task<CredentialsRow?> GetCredentialsAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT account, password FROM memb_info WHERE account = @account", conn);
        cmd.Parameters.AddWithValue("account", account);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CredentialsRow(reader.GetString(0), reader.GetString(1));
    }

    public async Task<PersonalInfoRow?> GetPersonalInfoAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT personal_code, block_code FROM memb_info WHERE account = @account", conn);
        cmd.Parameters.AddWithValue("account", account);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new PersonalInfoRow(reader.GetString(0), reader.GetInt32(1));
    }

    public async Task<AccountLevelRow> GetAccountLevelAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        int level;
        DateTime expire;

        await using (var cmd = new NpgsqlCommand(
            "SELECT account_level, account_expire FROM memb_info WHERE account = @account", conn))
        {
            cmd.Parameters.AddWithValue("account", account);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                return new AccountLevelRow(0, DateTime.UnixEpoch);
            }

            level = reader.GetInt32(0);
            expire = reader.GetDateTime(1);
        }

        // Exact port of WZ_GetAccountLevel: if the level is not 0 and has already expired, it is reset to 0.
        if (level != 0 && DateTime.UtcNow > expire)
        {
            level = 0;

            await using var update = new NpgsqlCommand(
                "UPDATE memb_info SET account_level = 0 WHERE account = @account", conn);
            update.Parameters.AddWithValue("account", account);
            await update.ExecuteNonQueryAsync(ct);
        }

        return new AccountLevelRow(level, expire);
    }

    public async Task SetAccountLevelAsync(string account, int accountLevel, int accountExpireSeconds, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        int currentLevel;
        DateTime currentExpire;

        await using (var cmd = new NpgsqlCommand(
            "SELECT account_level, account_expire FROM memb_info WHERE account = @account", conn))
        {
            cmd.Parameters.AddWithValue("account", account);

            await using var reader = await cmd.ExecuteReaderAsync(ct);

            if (!await reader.ReadAsync(ct))
            {
                return;
            }

            currentLevel = reader.GetInt32(0);
            currentExpire = reader.GetDateTime(1);
        }

        // Exact port of WZ_SetAccountLevel: same level => seconds are added to the current expiry (duration
        // stacks); different level => the expiry starts again from now.
        DateTime newExpire = currentLevel == accountLevel
            ? currentExpire.AddSeconds(accountExpireSeconds)
            : DateTime.UtcNow.AddSeconds(accountExpireSeconds);

        await using var update = new NpgsqlCommand(
            "UPDATE memb_info SET account_level = @level, account_expire = @expire WHERE account = @account", conn);
        update.Parameters.AddWithValue("level", accountLevel);
        update.Parameters.AddWithValue("expire", newExpire);
        update.Parameters.AddWithValue("account", account);
        await update.ExecuteNonQueryAsync(ct);
    }

    public async Task ConnectMembAsync(string account, string serverName, string ipAddress, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO memb_stat (account, connect_stat, server_name, ip_address, connected_at)
            VALUES (@account, 1, @serverName, @ip, now())
            ON CONFLICT (account) DO UPDATE SET
                connect_stat = 1,
                server_name = EXCLUDED.server_name,
                ip_address = EXCLUDED.ip_address,
                connected_at = now()
            """, conn);
        cmd.Parameters.AddWithValue("account", account);
        cmd.Parameters.AddWithValue("serverName", serverName);
        cmd.Parameters.AddWithValue("ip", ipAddress);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DisconnectMembAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE memb_stat SET connect_stat = 0, disconnected_at = now() WHERE account = @account", conn);
        cmd.Parameters.AddWithValue("account", account);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

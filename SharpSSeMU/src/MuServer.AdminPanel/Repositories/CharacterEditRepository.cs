using MuServer.DataServer.Db;
using Npgsql;

namespace MuServer.AdminPanel.Repositories;

/// <summary>A row of the character list -- <c>RawClass</c> is the raw DB byte (compact*16 + changeUp, same
/// format that <c>character.class</c> and <c>PlayerObject.Class</c>/ <c>ChangeUp</c> already use in the
/// GameServer, see OnCharacterListFromDataServerAsync).</summary>
public sealed record CharacterSummary(string Account, string Name, byte RawClass, int Level, long Money)
{
    public int CompactClass => RawClass / 16;
    public int ChangeUp => RawClass % 16;

    public string ClassName => CompactClass switch
    {
        0 => "Dark Wizard",
        1 => "Dark Knight",
        2 => "Fairy Elf",
        3 => "Magic Gladiator",
        4 => "Dark Lord",
        _ => $"Clase {CompactClass}",
    };
}

/// <summary> Character editor for the panel -- unlike the rest of the repositories (which read/write
/// <c>Data/</c> files), this one talks directly to the same Postgres database DataServer uses, because that is
/// where the character lives (there is no file in between). It reuses <see
/// cref="NpgsqlCharacterDataRepository"/> (the same repository the real DataServer already uses in production)
/// for everything that already has a method -- full character get/save, inventory, warehouse -- and only adds
/// the query missing from that interface (listing ALL characters: the production interface only looks up one at
/// a time, by account+name, because that is the only thing the real DataServer needs at runtime). </summary>
public sealed class CharacterEditRepository(string connectionString)
{
    private readonly ICharacterDataRepository _repo = new NpgsqlCharacterDataRepository(connectionString);

    public async Task<List<CharacterSummary>> ListCharactersAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT account_id, name, class, clevel, money FROM character ORDER BY name", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<CharacterSummary>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new CharacterSummary(
                Account: reader.GetString(0),
                Name: reader.GetString(1),
                RawClass: (byte)reader.GetInt16(2),
                Level: reader.GetInt32(3),
                Money: reader.GetInt64(4)));
        }

        return list;
    }

    public Task<CharacterFullRow?> GetCharacterAsync(string account, string name, CancellationToken ct)
        => _repo.GetCharacterFullAsync(account, name, ct);

    public Task SaveCharacterAsync(string account, string name, CharacterFullRow row, CancellationToken ct)
        => _repo.SaveCharacterAsync(account, name, row, ct);

    public Task SaveInventoryAsync(string account, string name, byte[] inventory, CancellationToken ct)
        => _repo.SaveInventoryAsync(account, name, inventory, ct);

    public Task<WarehouseRow?> GetWarehouseAsync(string account, CancellationToken ct)
        => _repo.GetWarehouseAsync(account, ct);

    public Task SaveWarehouseAsync(string account, byte[] items, uint money, ushort password, CancellationToken ct)
        => _repo.SaveWarehouseAsync(account, items, money, password, ct);
}

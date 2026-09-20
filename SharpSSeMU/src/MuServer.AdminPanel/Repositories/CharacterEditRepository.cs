using MuServer.DataServer.Db;
using Npgsql;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Una fila de la lista de personajes -- <c>RawClass</c> es el byte crudo de DB
/// (compacto*16 + changeUp, mismo formato que <c>character.class</c> y <c>PlayerObject.Class</c>/
/// <c>ChangeUp</c> ya usan en el GameServer, ver OnCharacterListFromDataServerAsync).</summary>
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

/// <summary>
/// Editor de personajes para el panel -- a diferencia del resto de los repositorios (que leen/escriben
/// archivos de <c>Data/</c>), este habla directo con la misma base Postgres que usa DataServer, porque
/// ahí es donde vive el personaje (no hay archivo de por medio). Reusa
/// <see cref="NpgsqlCharacterDataRepository"/> (el mismo repositorio que ya usa el DataServer real en
/// producción) para todo lo que ya tiene un método -- get/save de personaje completo, inventario,
/// baúl -- y sólo agrega la consulta que le falta a esa interfaz (listar TODOS los personajes: la
/// interfaz de producción sólo busca de a uno, por cuenta+nombre, porque eso es lo único que el
/// DataServer real necesita en runtime).
/// </summary>
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

namespace MuServer.DataServer.Db;

/// <summary>
/// Reemplaza a QueryManager (ODBC/SQL Server) + los stored procedures que usaba DataServer
/// directamente (WZ_CreateCharacter, WZ_DeleteCharacter, WZ_GetItemSerial, WZ_Get/SetResetInfo,
/// WZ_Get/SetMasterResetInfo, WZ_Get/SetEventEntryInfo), ahora contra PostgreSQL con consultas
/// parametrizadas.
/// </summary>
public interface ICharacterDataRepository
{
    Task<long> GetItemCountAsync(CancellationToken ct);
    Task<long> GetNextItemSerialAsync(CancellationToken ct);
    Task EnsurePetItemAsync(long serial, int level, long experience, CancellationToken ct);
    Task<PetItemRow?> GetPetItemAsync(long serial, CancellationToken ct);
    Task SetPetItemAsync(long serial, int level, long experience, CancellationToken ct);

    Task EnsureAccountCharacterAsync(string account, CancellationToken ct);
    Task<AccountSlots?> GetAccountSlotsAsync(string account, CancellationToken ct);
    Task SetSlotNameAsync(string account, int slot, string? name, CancellationToken ct);
    Task SetLastCharacterAsync(string account, string name, CancellationToken ct);
    Task SetExtClassAsync(string account, int extClass, CancellationToken ct);

    Task<CharacterListRow?> GetCharacterListRowAsync(string account, string name, CancellationToken ct);

    /// <summary>Puerto de WZ_CreateCharacter: 1=creado, 0=cuenta/clase inválida, 3=nombre ya existe.</summary>
    Task<byte> CreateCharacterAsync(string account, string name, int characterClass, CancellationToken ct);

    Task<bool> CharacterExistsAsync(string name, CancellationToken ct);

    /// <summary>Puerto de WZ_DeleteCharacter: 1=borrado, 0=no existía.</summary>
    Task<byte> DeleteCharacterAsync(string account, string name, CancellationToken ct);

    Task<CharacterFullRow?> GetCharacterFullAsync(string account, string name, CancellationToken ct);
    Task SaveCharacterAsync(string account, string name, CharacterFullRow row, CancellationToken ct);
    Task SaveInventoryAsync(string account, string name, byte[] inventory, CancellationToken ct);

    Task<OptionDataRow> GetOptionDataAsync(string name, CancellationToken ct);
    Task SaveOptionDataAsync(string name, byte[] skillKey, int gameOption, int qKey, int wKey, int eKey, int chatWindow, CancellationToken ct);

    Task<ResetInfo> GetResetInfoAsync(string account, string name, CancellationToken ct);
    Task SetResetInfoAsync(string account, string name, int reset, int resetDay, int resetWek, int resetMon, CancellationToken ct);

    Task<MasterResetInfo> GetMasterResetInfoAsync(string account, string name, CancellationToken ct);
    Task SetMasterResetInfoAsync(string account, string name, int reset, int masterReset, int masterResetDay, int masterResetWek, int masterResetMon, CancellationToken ct);

    Task<EventEntryInfo> GetEventEntryInfoAsync(string account, string name, CancellationToken ct);
    Task SetEventEntryInfoAsync(string account, string name, int bcCount, int ccCount, int dsCount, CancellationToken ct);

    Task<(int win, int lose)> AddRankingDuelAsync(string name, int winDelta, int loseDelta, CancellationToken ct);
    Task<int> AddRankingScoreAsync(string table, string name, int scoreDelta, CancellationToken ct);

    Task<int> IncrementMonsterKillCountAsync(string name, int monsterClass, CancellationToken ct);

    // ---------------------------------------------------------------- Fase 5 (GameServer): amigos

    /// <summary>Puerto de "SELECT FriendName FROM T_FriendList WHERE GUID=..." -- la lista de
    /// amigos de <paramref name="ownerName"/>.</summary>
    Task<IReadOnlyList<string>> GetFriendListAsync(string ownerName, CancellationToken ct);

    /// <summary>Reverso de lo anterior -- puerto de la consulta que hace CFriend::DGFriendStateSend
    /// (Friend.cpp:502) al conectar/desconectar un personaje: "¿quién tiene a este nombre en SU
    /// lista de amigos?", para saber a quién avisarle el cambio de estado online/offline.</summary>
    Task<IReadOnlyList<string>> GetFriendsOfAsync(string targetName, CancellationToken ct);

    /// <summary>Puerto de WZ_WaitFriendAdd -- guarda una solicitud pendiente (idempotente).</summary>
    Task AddFriendRequestAsync(string ownerName, string requesterName, CancellationToken ct);

    /// <summary>Puerto de WZ_WaitFriendDel + verificación de que la solicitud exista -- borra la
    /// solicitud pendiente y devuelve si realmente existía (para no aceptar una respuesta a una
    /// invitación que ya no está, ej. por timeout o duplicado).</summary>
    Task<bool> TryConsumeFriendRequestAsync(string ownerName, string requesterName, CancellationToken ct);

    /// <summary>Puerto de WZ_FriendAdd -- inserta la amistad en AMBOS sentidos (el original la
    /// modela con GUID/FriendName por fila, pero siempre agrega la entrada simétrica al aceptar).</summary>
    Task AddFriendPairAsync(string nameA, string nameB, CancellationToken ct);

    /// <summary>Puerto de WZ_FriendDel -- borra la amistad en ambos sentidos, devuelve si existía.</summary>
    Task<bool> RemoveFriendPairAsync(string nameA, string nameB, CancellationToken ct);

    // ---------------------------------------------------------------- Fase 3: Warehouse (Baúl)
    Task<WarehouseRow?> GetWarehouseAsync(string account, CancellationToken ct);
    Task SaveWarehouseAsync(string account, byte[] items, uint money, ushort password, CancellationToken ct);

    // ---------------------------------------------------------------- Guilds (Clanes)
    Task<byte> CreateGuildAsync(string guildName, string masterName, byte[] logo, CancellationToken ct);
    Task<bool> SaveGuildMemberAsync(string guildName, string memberName, byte status, CancellationToken ct);
    Task<bool> DeleteGuildMemberAsync(string guildName, string memberName, CancellationToken ct);
    Task<bool> DeleteGuildAsync(string guildName, CancellationToken ct);
    Task<GuildMemberInfo?> GetCharacterGuildInfoAsync(string characterName, CancellationToken ct);
}

public sealed record WarehouseRow(byte[] Items, uint Money, ushort Password);
public sealed record GuildMemberInfo(string GuildName, byte Status, byte[] Logo);

using Npgsql;
using NpgsqlTypes;

namespace MuServer.DataServer.Db;

public sealed class NpgsqlCharacterDataRepository : ICharacterDataRepository
{
    private readonly string _connectionString;

    public NpgsqlCharacterDataRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static byte[] EmptyIfNull(byte[]? value, int size) => value ?? new byte[size];

    // ---------------------------------------------------------------- item serial / game server info

    public async Task<long> GetItemCountAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT item_count FROM game_server_info WHERE number = 0", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : 0;
    }

    /// <summary>Port of WZ_GetItemSerial: atomic increment.</summary>
    public async Task<long> GetNextItemSerialAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE game_server_info SET item_count = item_count + 1 WHERE number = 0 RETURNING item_count", conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is long l ? l : -1;
    }

    // ---------------------------------------------------------------- pet items (T_PetItem_Info)

    public async Task EnsurePetItemAsync(long serial, int level, long experience, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO pet_item_info (item_serial, pet_level, pet_exp) VALUES (@s, @l, @e) ON CONFLICT (item_serial) DO NOTHING",
            conn);
        cmd.Parameters.AddWithValue("s", serial);
        cmd.Parameters.AddWithValue("l", level);
        cmd.Parameters.AddWithValue("e", experience);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PetItemRow?> GetPetItemAsync(long serial, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT pet_level, pet_exp FROM pet_item_info WHERE item_serial = @s", conn);
        cmd.Parameters.AddWithValue("s", serial);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new PetItemRow(reader.GetInt16(0), reader.GetInt64(1)) : null;
    }

    public async Task SetPetItemAsync(long serial, int level, long experience, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO pet_item_info (item_serial, pet_level, pet_exp) VALUES (@s, @l, @e)
            ON CONFLICT (item_serial) DO UPDATE SET pet_level = @l, pet_exp = @e
            """, conn);
        cmd.Parameters.AddWithValue("s", serial);
        cmd.Parameters.AddWithValue("l", level);
        cmd.Parameters.AddWithValue("e", experience);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- account_character (slots)

    public async Task EnsureAccountCharacterAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO account_character (account) VALUES (@a) ON CONFLICT (account) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("a", account);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Null if there is no AccountCharacter row yet for this account (different from "exists with 5 empty slots").</summary>
    public async Task<AccountSlots?> GetAccountSlotsAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT move_cnt, ext_class, game_id1, game_id2, game_id3, game_id4, game_id5 FROM account_character WHERE account = @a",
            conn);
        cmd.Parameters.AddWithValue("a", account);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var names = new string?[5];
        for (int i = 0; i < 5; i++)
        {
            names[i] = reader.IsDBNull(2 + i) ? null : reader.GetString(2 + i);
        }

        return new AccountSlots((byte)reader.GetInt16(0), reader.GetInt32(1), names);
    }

    public async Task SetSlotNameAsync(string account, int slot, string? name, CancellationToken ct)
    {
        if (slot is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        // The column name cannot be parameterised; slot is bounded to 0-4 above, so it is safe.
        var column = $"game_id{slot + 1}";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"UPDATE account_character SET {column} = @n WHERE account = @a", conn);
        cmd.Parameters.AddWithValue("n", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a", account);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetLastCharacterAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE account_character SET game_idc = @n WHERE account = @a", conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("a", account);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetExtClassAsync(string account, int extClass, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("UPDATE account_character SET ext_class = @e WHERE account = @a", conn);
        cmd.Parameters.AddWithValue("e", extClass);
        cmd.Parameters.AddWithValue("a", account);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- character

    public async Task<CharacterListRow?> GetCharacterListRowAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT clevel, class, inventory, ctl_code FROM character WHERE account_id = @a AND name = @n", conn);
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("n", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new CharacterListRow(
            reader.GetInt32(0), reader.GetInt16(1), EmptyIfNull(reader.IsDBNull(2) ? null : (byte[])reader[2], 1728), reader.GetInt16(3));
    }

    /// <summary>Port of WZ_CreateCharacter. 1=created, 0=the name already exists, 2=invalid class/error.</summary>
    public async Task<byte> CreateCharacterAsync(string account, string name, int characterClass, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var exists = new NpgsqlCommand("SELECT 1 FROM character WHERE name = @n", conn))
        {
            exists.Parameters.AddWithValue("n", name);
            if (await exists.ExecuteScalarAsync(ct) != null)
            {
                return 0;
            }
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO character (account_id, name, clevel, level_up_point, class, strength, dexterity, vitality,
                energy, leadership, inventory, magic_list, life, max_life, mana, max_mana, map_number, map_pos_x,
                map_pos_y, mdate, ldate, quest, db_version, effect_list)
            SELECT @account, @name, level, level_up_point, @class, strength, dexterity, vitality, energy, leadership,
                inventory, magic_list, life, max_life, mana, max_mana, map_number, map_pos_x, map_pos_y, now(), now(),
                quest, db_version, effect_list
            FROM default_class_type WHERE class = @class
            """, conn);
        insert.Parameters.AddWithValue("account", account);
        insert.Parameters.AddWithValue("name", name);
        insert.Parameters.AddWithValue("class", characterClass);

        var affected = await insert.ExecuteNonQueryAsync(ct);
        return affected > 0 ? (byte)1 : (byte)2;
    }

    public async Task<bool> CharacterExistsAsync(string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM character WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("n", name);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    /// <summary>Port of WZ_DeleteCharacter (also deletes the related tables by name).</summary>
    public async Task<byte> DeleteCharacterAsync(string account, string name, CancellationToken ct)
    {
        if (!await CharacterExistsAsync(name, ct))
        {
            return 0;
        }

        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var sql in new[]
        {
            "DELETE FROM character WHERE account_id = @a AND name = @n",
            "DELETE FROM option_data WHERE name = @n",
            "DELETE FROM ranking_duel WHERE name = @n",
            "DELETE FROM ranking_blood_castle WHERE name = @n",
            "DELETE FROM ranking_chaos_castle WHERE name = @n",
            "DELETE FROM ranking_devil_square WHERE name = @n",
            "DELETE FROM ranking_illusion_temple WHERE name = @n", // was not in the original WZ_DeleteCharacter (that table did not exist), added for consistency
            "DELETE FROM reset_data WHERE name = @n",
            "DELETE FROM event_entry_count WHERE name = @n",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return 1;
    }

    public async Task<CharacterFullRow?> GetCharacterFullAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            SELECT clevel, class, level_up_point, experience, strength, dexterity, vitality, energy, leadership,
                   inventory, magic_list, money, life, max_life, mana, max_mana, bp, max_bp, map_number, map_pos_x,
                   map_pos_y, map_dir, pk_count, pk_level, pk_time, ctl_code, quest, chat_limit_time, effect_list,
                   fruit_add_point, fruit_sub_point
            FROM character WHERE account_id = @a AND name = @n
            """, conn);
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("n", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        int i = 0;
        return new CharacterFullRow(
            CLevel: reader.GetInt32(i++),
            Class: reader.GetInt16(i++),
            LevelUpPoint: reader.GetInt32(i++),
            Experience: reader.GetInt64(i++),
            Strength: reader.GetInt32(i++),
            Dexterity: reader.GetInt32(i++),
            Vitality: reader.GetInt32(i++),
            Energy: reader.GetInt32(i++),
            Leadership: reader.GetInt32(i++),
            Inventory: EmptyIfNull(reader.IsDBNull(i) ? null : (byte[])reader[i++], 1728),
            MagicList: EmptyIfNull(reader.IsDBNull(i) ? null : (byte[])reader[i++], 180),
            Money: reader.GetInt64(i++),
            Life: reader.GetFloat(i++),
            MaxLife: reader.GetFloat(i++),
            Mana: reader.GetFloat(i++),
            MaxMana: reader.GetFloat(i++),
            BP: reader.GetFloat(i++),
            MaxBP: reader.GetFloat(i++),
            MapNumber: reader.GetInt16(i++),
            MapPosX: reader.GetInt16(i++),
            MapPosY: reader.GetInt16(i++),
            MapDir: reader.GetInt16(i++),
            PkCount: reader.GetInt32(i++),
            PkLevel: reader.GetInt32(i++),
            PkTime: reader.GetInt32(i++),
            CtlCode: reader.GetInt16(i++),
            Quest: EmptyIfNull(reader.IsDBNull(i) ? null : (byte[])reader[i++], 50),
            ChatLimitTime: reader.GetInt32(i++),
            EffectList: EmptyIfNull(reader.IsDBNull(i) ? null : (byte[])reader[i++], 208),
            FruitAddPoint: reader.GetInt32(i++),
            FruitSubPoint: reader.GetInt32(i++));
    }

    public async Task SaveCharacterAsync(string account, string name, CharacterFullRow row, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE character SET
                clevel = @clevel, class = @class, level_up_point = @lup, experience = @exp, strength = @str,
                dexterity = @dex, vitality = @vit, energy = @ene, leadership = @lead, inventory = @inv,
                magic_list = @magic, money = @money, life = @life, max_life = @maxlife, mana = @mana,
                max_mana = @maxmana, bp = @bp, max_bp = @maxbp, map_number = @map, map_pos_x = @x, map_pos_y = @y,
                map_dir = @dir, pk_count = @pkc, pk_level = @pkl, pk_time = @pkt, quest = @quest,
                chat_limit_time = @chat, effect_list = @effect, fruit_add_point = @fap, fruit_sub_point = @fsp
            WHERE account_id = @account AND name = @name
            """, conn);

        cmd.Parameters.AddWithValue("clevel", row.CLevel);
        cmd.Parameters.AddWithValue("class", row.Class);
        cmd.Parameters.AddWithValue("lup", row.LevelUpPoint);
        cmd.Parameters.AddWithValue("exp", row.Experience);
        cmd.Parameters.AddWithValue("str", row.Strength);
        cmd.Parameters.AddWithValue("dex", row.Dexterity);
        cmd.Parameters.AddWithValue("vit", row.Vitality);
        cmd.Parameters.AddWithValue("ene", row.Energy);
        cmd.Parameters.AddWithValue("lead", row.Leadership);
        cmd.Parameters.Add(new NpgsqlParameter("inv", NpgsqlDbType.Bytea) { Value = row.Inventory });
        cmd.Parameters.Add(new NpgsqlParameter("magic", NpgsqlDbType.Bytea) { Value = row.MagicList });
        cmd.Parameters.AddWithValue("money", row.Money);
        cmd.Parameters.AddWithValue("life", row.Life);
        cmd.Parameters.AddWithValue("maxlife", row.MaxLife);
        cmd.Parameters.AddWithValue("mana", row.Mana);
        cmd.Parameters.AddWithValue("maxmana", row.MaxMana);
        cmd.Parameters.AddWithValue("bp", row.BP);
        cmd.Parameters.AddWithValue("maxbp", row.MaxBP);
        cmd.Parameters.AddWithValue("map", row.MapNumber);
        cmd.Parameters.AddWithValue("x", row.MapPosX);
        cmd.Parameters.AddWithValue("y", row.MapPosY);
        cmd.Parameters.AddWithValue("dir", row.MapDir);
        cmd.Parameters.AddWithValue("pkc", row.PkCount);
        cmd.Parameters.AddWithValue("pkl", row.PkLevel);
        cmd.Parameters.AddWithValue("pkt", row.PkTime);
        cmd.Parameters.Add(new NpgsqlParameter("quest", NpgsqlDbType.Bytea) { Value = row.Quest });
        cmd.Parameters.AddWithValue("chat", row.ChatLimitTime);
        cmd.Parameters.Add(new NpgsqlParameter("effect", NpgsqlDbType.Bytea) { Value = row.EffectList });
        cmd.Parameters.AddWithValue("fap", row.FruitAddPoint);
        cmd.Parameters.AddWithValue("fsp", row.FruitSubPoint);
        cmd.Parameters.AddWithValue("account", account);
        cmd.Parameters.AddWithValue("name", name);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveInventoryAsync(string account, string name, byte[] inventory, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE character SET inventory = @inv WHERE account_id = @a AND name = @n", conn);
        cmd.Parameters.Add(new NpgsqlParameter("inv", NpgsqlDbType.Bytea) { Value = inventory });
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("n", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- option_data

    public async Task<OptionDataRow> GetOptionDataAsync(string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT skill_key, game_option, qkey, wkey, ekey, chat_window FROM option_data WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("n", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            // Same as the original when there is no row: SkillKey=0xFF*10, rest of the keys 0xFF, ChatWindow=6.
            return new OptionDataRow(Enumerable.Repeat((byte)0xFF, 10).ToArray(), 0xFF, 0xFF, 0xFF, 0xFF, 0x06);
        }

        return new OptionDataRow(
            EmptyIfNull(reader.IsDBNull(0) ? null : (byte[])reader[0], 10),
            reader.GetInt16(1), reader.GetInt16(2), reader.GetInt16(3), reader.GetInt16(4), reader.GetInt16(5));
    }

    public async Task SaveOptionDataAsync(string name, byte[] skillKey, int gameOption, int qKey, int wKey, int eKey, int chatWindow, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO option_data (name, skill_key, game_option, qkey, wkey, ekey, chat_window)
            VALUES (@n, @sk, @go, @q, @w, @e, @cw)
            ON CONFLICT (name) DO UPDATE SET
                skill_key = @sk, game_option = @go, qkey = @q, wkey = @w, ekey = @e, chat_window = @cw
            """, conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.Add(new NpgsqlParameter("sk", NpgsqlDbType.Bytea) { Value = skillKey });
        cmd.Parameters.AddWithValue("go", gameOption);
        cmd.Parameters.AddWithValue("q", qKey);
        cmd.Parameters.AddWithValue("w", wKey);
        cmd.Parameters.AddWithValue("e", eKey);
        cmd.Parameters.AddWithValue("cw", chatWindow);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- reset / master reset / event entry Note
    // on day/week/month "rollover": the original uses SQL Server's DATEDIFF(day/week/month, ...), whose exact
    // behaviour for week/month depends on the server's regional configuration (DATEFIRST). Here it is
    // approximated with a calendar comparison (same day / same ISO week / same month-year), which serves the
    // same practical purpose (daily/weekly/monthly counters).

    private static bool RolledOverDay(DateTime stored, DateTime now) => stored.Date != now.Date;

    private static bool RolledOverWeek(DateTime stored, DateTime now) =>
        System.Globalization.ISOWeek.GetYear(stored) != System.Globalization.ISOWeek.GetYear(now) ||
        System.Globalization.ISOWeek.GetWeekOfYear(stored) != System.Globalization.ISOWeek.GetWeekOfYear(now);

    private static bool RolledOverMonth(DateTime stored, DateTime now) =>
        stored.Year != now.Year || stored.Month != now.Month;

    public async Task<ResetInfo> GetResetInfoAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO reset_data (account, name) VALUES (@a, @n) ON CONFLICT (account, name) DO NOTHING", conn))
        {
            ensure.Parameters.AddWithValue("a", account);
            ensure.Parameters.AddWithValue("n", name);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        int resetCount;
        await using (var cmd = new NpgsqlCommand("SELECT reset_count FROM character WHERE account_id = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            var v = await cmd.ExecuteScalarAsync(ct);
            resetCount = v is int iv ? iv : 0;
        }

        int day, wek, mon;
        DateTime dDay, dWek, dMon;

        await using (var cmd = new NpgsqlCommand(
            "SELECT reset_day, reset_wek, reset_mon, reset_date_day, reset_date_wek, reset_date_mon FROM reset_data WHERE account = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            day = reader.GetInt32(0);
            wek = reader.GetInt32(1);
            mon = reader.GetInt32(2);
            dDay = reader.GetDateTime(3);
            dWek = reader.GetDateTime(4);
            dMon = reader.GetDateTime(5);
        }

        var now = DateTime.UtcNow;
        bool changed = false;

        if (RolledOverDay(dDay, now)) { day = 0; changed = true; }
        if (RolledOverWeek(dWek, now)) { wek = 0; changed = true; }
        if (RolledOverMonth(dMon, now)) { mon = 0; changed = true; }

        if (changed)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE reset_data SET reset_day = @d, reset_wek = @w, reset_mon = @m WHERE account = @a AND name = @n", conn);
            upd.Parameters.AddWithValue("d", day);
            upd.Parameters.AddWithValue("w", wek);
            upd.Parameters.AddWithValue("m", mon);
            upd.Parameters.AddWithValue("a", account);
            upd.Parameters.AddWithValue("n", name);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return new ResetInfo(resetCount, day, wek, mon);
    }

    public async Task SetResetInfoAsync(string account, string name, int reset, int resetDay, int resetWek, int resetMon, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO reset_data (account, name) VALUES (@a, @n) ON CONFLICT (account, name) DO NOTHING", conn))
        {
            ensure.Parameters.AddWithValue("a", account);
            ensure.Parameters.AddWithValue("n", name);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        DateTime dDay, dWek, dMon;
        await using (var cmd = new NpgsqlCommand(
            "SELECT reset_date_day, reset_date_wek, reset_date_mon FROM reset_data WHERE account = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            dDay = reader.GetDateTime(0);
            dWek = reader.GetDateTime(1);
            dMon = reader.GetDateTime(2);
        }

        var now = DateTime.UtcNow;

        await using (var upd = new NpgsqlCommand(
            """
            UPDATE reset_data SET
                reset_date_day = CASE WHEN @rday THEN @now ELSE reset_date_day END,
                reset_date_wek = CASE WHEN @rwek THEN @now ELSE reset_date_wek END,
                reset_date_mon = CASE WHEN @rmon THEN @now ELSE reset_date_mon END,
                reset_day = @day, reset_wek = @wek, reset_mon = @mon
            WHERE account = @a AND name = @n
            """, conn))
        {
            upd.Parameters.AddWithValue("rday", RolledOverDay(dDay, now));
            upd.Parameters.AddWithValue("rwek", RolledOverWeek(dWek, now));
            upd.Parameters.AddWithValue("rmon", RolledOverMonth(dMon, now));
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("day", resetDay);
            upd.Parameters.AddWithValue("wek", resetWek);
            upd.Parameters.AddWithValue("mon", resetMon);
            upd.Parameters.AddWithValue("a", account);
            upd.Parameters.AddWithValue("n", name);
            await upd.ExecuteNonQueryAsync(ct);
        }

        await using (var updChar = new NpgsqlCommand(
            "UPDATE character SET reset_count = @r WHERE account_id = @a AND name = @n", conn))
        {
            updChar.Parameters.AddWithValue("r", reset);
            updChar.Parameters.AddWithValue("a", account);
            updChar.Parameters.AddWithValue("n", name);
            await updChar.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<MasterResetInfo> GetMasterResetInfoAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO reset_data (account, name) VALUES (@a, @n) ON CONFLICT (account, name) DO NOTHING", conn))
        {
            ensure.Parameters.AddWithValue("a", account);
            ensure.Parameters.AddWithValue("n", name);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        int masterReset;
        await using (var cmd = new NpgsqlCommand("SELECT master_reset_count FROM character WHERE account_id = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            var v = await cmd.ExecuteScalarAsync(ct);
            masterReset = v is int iv ? iv : 0;
        }

        int day, wek, mon;
        DateTime dDay, dWek, dMon;

        await using (var cmd = new NpgsqlCommand(
            "SELECT master_reset_day, master_reset_wek, master_reset_mon, master_reset_date_day, master_reset_date_wek, master_reset_date_mon FROM reset_data WHERE account = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            day = reader.GetInt32(0);
            wek = reader.GetInt32(1);
            mon = reader.GetInt32(2);
            dDay = reader.GetDateTime(3);
            dWek = reader.GetDateTime(4);
            dMon = reader.GetDateTime(5);
        }

        var now = DateTime.UtcNow;
        bool changed = false;

        if (RolledOverDay(dDay, now)) { day = 0; changed = true; }
        if (RolledOverWeek(dWek, now)) { wek = 0; changed = true; }
        if (RolledOverMonth(dMon, now)) { mon = 0; changed = true; }

        if (changed)
        {
            await using var upd = new NpgsqlCommand(
                "UPDATE reset_data SET master_reset_day = @d, master_reset_wek = @w, master_reset_mon = @m WHERE account = @a AND name = @n", conn);
            upd.Parameters.AddWithValue("d", day);
            upd.Parameters.AddWithValue("w", wek);
            upd.Parameters.AddWithValue("m", mon);
            upd.Parameters.AddWithValue("a", account);
            upd.Parameters.AddWithValue("n", name);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return new MasterResetInfo(masterReset, day, wek, mon);
    }

    public async Task SetMasterResetInfoAsync(string account, string name, int reset, int masterReset, int masterResetDay, int masterResetWek, int masterResetMon, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO reset_data (account, name) VALUES (@a, @n) ON CONFLICT (account, name) DO NOTHING", conn))
        {
            ensure.Parameters.AddWithValue("a", account);
            ensure.Parameters.AddWithValue("n", name);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        DateTime dDay, dWek, dMon;
        await using (var cmd = new NpgsqlCommand(
            "SELECT master_reset_date_day, master_reset_date_wek, master_reset_date_mon FROM reset_data WHERE account = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            dDay = reader.GetDateTime(0);
            dWek = reader.GetDateTime(1);
            dMon = reader.GetDateTime(2);
        }

        var now = DateTime.UtcNow;

        await using (var upd = new NpgsqlCommand(
            """
            UPDATE reset_data SET
                master_reset_date_day = CASE WHEN @rday THEN @now ELSE master_reset_date_day END,
                master_reset_date_wek = CASE WHEN @rwek THEN @now ELSE master_reset_date_wek END,
                master_reset_date_mon = CASE WHEN @rmon THEN @now ELSE master_reset_date_mon END,
                master_reset_day = @day, master_reset_wek = @wek, master_reset_mon = @mon
            WHERE account = @a AND name = @n
            """, conn))
        {
            upd.Parameters.AddWithValue("rday", RolledOverDay(dDay, now));
            upd.Parameters.AddWithValue("rwek", RolledOverWeek(dWek, now));
            upd.Parameters.AddWithValue("rmon", RolledOverMonth(dMon, now));
            upd.Parameters.AddWithValue("now", now);
            upd.Parameters.AddWithValue("day", masterResetDay);
            upd.Parameters.AddWithValue("wek", masterResetWek);
            upd.Parameters.AddWithValue("mon", masterResetMon);
            upd.Parameters.AddWithValue("a", account);
            upd.Parameters.AddWithValue("n", name);
            await upd.ExecuteNonQueryAsync(ct);
        }

        await using (var updChar = new NpgsqlCommand(
            "UPDATE character SET reset_count = @r, master_reset_count = @mr WHERE account_id = @a AND name = @n", conn))
        {
            updChar.Parameters.AddWithValue("r", reset);
            updChar.Parameters.AddWithValue("mr", masterReset);
            updChar.Parameters.AddWithValue("a", account);
            updChar.Parameters.AddWithValue("n", name);
            await updChar.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<EventEntryInfo> GetEventEntryInfoAsync(string account, string name, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        await using (var ensure = new NpgsqlCommand(
            "INSERT INTO event_entry_count (account, name) VALUES (@a, @n) ON CONFLICT (account, name) DO NOTHING", conn))
        {
            ensure.Parameters.AddWithValue("a", account);
            ensure.Parameters.AddWithValue("n", name);
            await ensure.ExecuteNonQueryAsync(ct);
        }

        int bc, cc, ds;
        DateTime last;

        await using (var cmd = new NpgsqlCommand(
            "SELECT bc_count, cc_count, ds_count, last_date FROM event_entry_count WHERE account = @a AND name = @n", conn))
        {
            cmd.Parameters.AddWithValue("a", account);
            cmd.Parameters.AddWithValue("n", name);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            bc = reader.GetInt32(0);
            cc = reader.GetInt32(1);
            ds = reader.GetInt32(2);
            last = reader.GetDateTime(3);
        }

        if (RolledOverDay(last, DateTime.UtcNow))
        {
            bc = 0; cc = 0; ds = 0;

            await using var upd = new NpgsqlCommand(
                "UPDATE event_entry_count SET bc_count = 0, cc_count = 0, ds_count = 0, last_date = @now WHERE account = @a AND name = @n", conn);
            upd.Parameters.AddWithValue("now", DateTime.UtcNow);
            upd.Parameters.AddWithValue("a", account);
            upd.Parameters.AddWithValue("n", name);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return new EventEntryInfo(bc, cc, ds);
    }

    public async Task SetEventEntryInfoAsync(string account, string name, int bcCount, int ccCount, int dsCount, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO event_entry_count (account, name, bc_count, cc_count, ds_count, last_date)
            VALUES (@a, @n, @bc, @cc, @ds, now())
            ON CONFLICT (account, name) DO UPDATE SET bc_count = @bc, cc_count = @cc, ds_count = @ds
            """, conn);
        cmd.Parameters.AddWithValue("a", account);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("bc", bcCount);
        cmd.Parameters.AddWithValue("cc", ccCount);
        cmd.Parameters.AddWithValue("ds", dsCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- rankings

    public async Task<(int win, int lose)> AddRankingDuelAsync(string name, int winDelta, int loseDelta, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO ranking_duel (name, win_score, lose_score) VALUES (@n, @w, @l)
            ON CONFLICT (name) DO UPDATE SET win_score = ranking_duel.win_score + @w, lose_score = ranking_duel.lose_score + @l
            RETURNING win_score, lose_score
            """, conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("w", winDelta);
        cmd.Parameters.AddWithValue("l", loseDelta);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static readonly HashSet<string> AllowedRankingTables = new()
    {
        "ranking_blood_castle", "ranking_chaos_castle", "ranking_devil_square", "ranking_illusion_temple",
    };

    public async Task<int> AddRankingScoreAsync(string table, string name, int scoreDelta, CancellationToken ct)
    {
        if (!AllowedRankingTables.Contains(table))
        {
            throw new ArgumentException($"Tabla de ranking no permitida: {table}", nameof(table));
        }

        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"""
            INSERT INTO {table} (name, score) VALUES (@n, @s)
            ON CONFLICT (name) DO UPDATE SET score = {table}.score + @s
            RETURNING score
            """, conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("s", scoreDelta);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : 0;
    }

    // ---------------------------------------------------------------- monster kill count

    public async Task<int> IncrementMonsterKillCountAsync(string name, int monsterClass, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO monster_kill_count (name, monster_class, kill_count) VALUES (@n, @c, 1)
            ON CONFLICT (name, monster_class) DO UPDATE SET kill_count = monster_kill_count.kill_count + 1
            RETURNING kill_count
            """, conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("c", monsterClass);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : 0;
    }

    // ---------------------------------------------------------------- Fase 5 (GameServer): amigos

    public async Task<IReadOnlyList<string>> GetFriendListAsync(string ownerName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT friend_name FROM friend_list WHERE owner_name = @o ORDER BY friend_name", conn);
        cmd.Parameters.AddWithValue("o", ownerName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();

        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetFriendsOfAsync(string targetName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT owner_name FROM friend_list WHERE friend_name = @t", conn);
        cmd.Parameters.AddWithValue("t", targetName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();

        while (await reader.ReadAsync(ct))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task AddFriendRequestAsync(string ownerName, string requesterName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO friend_request (owner_name, requester_name) VALUES (@o, @r)
            ON CONFLICT (owner_name, requester_name) DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("o", ownerName);
        cmd.Parameters.AddWithValue("r", requesterName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryConsumeFriendRequestAsync(string ownerName, string requesterName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM friend_request WHERE owner_name = @o AND requester_name = @r", conn);
        cmd.Parameters.AddWithValue("o", ownerName);
        cmd.Parameters.AddWithValue("r", requesterName);
        int affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task AddFriendPairAsync(string nameA, string nameB, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO friend_list (owner_name, friend_name) VALUES (@a, @b), (@b, @a)
            ON CONFLICT (owner_name, friend_name) DO NOTHING
            """, conn);
        cmd.Parameters.AddWithValue("a", nameA);
        cmd.Parameters.AddWithValue("b", nameB);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RemoveFriendPairAsync(string nameA, string nameB, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            DELETE FROM friend_list WHERE (owner_name = @a AND friend_name = @b) OR (owner_name = @b AND friend_name = @a)
            """, conn);
        cmd.Parameters.AddWithValue("a", nameA);
        cmd.Parameters.AddWithValue("b", nameB);
        int affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    public async Task<WarehouseRow?> GetWarehouseAsync(string account, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmdInit = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS warehouse (
                account text PRIMARY KEY,
                items bytea NOT NULL,
                money bigint NOT NULL DEFAULT 0,
                password integer NOT NULL DEFAULT 0
            )
            """, conn);
        await cmdInit.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT items, money, password FROM warehouse WHERE account = @account", conn);
        cmd.Parameters.AddWithValue("account", account);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            var emptyItems = new byte[1920];
            Array.Fill(emptyItems, (byte)0xFF);
            return new WarehouseRow(emptyItems, 0, 0);
        }

        var items = r.IsDBNull(0) ? new byte[1920] : (byte[])r.GetValue(0);
        if (items.Length < 1920)
        {
            var newItems = new byte[1920];
            Array.Fill(newItems, (byte)0xFF);
            Array.Copy(items, newItems, items.Length);
            items = newItems;
        }

        uint money = (uint)r.GetInt64(1);
        ushort password = (ushort)r.GetInt32(2);
        return new WarehouseRow(items, money, password);
    }

    public async Task SaveWarehouseAsync(string account, byte[] items, uint money, ushort password, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmdInit = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS warehouse (
                account text PRIMARY KEY,
                items bytea NOT NULL,
                money bigint NOT NULL DEFAULT 0,
                password integer NOT NULL DEFAULT 0
            )
            """, conn);
        await cmdInit.ExecuteNonQueryAsync(ct);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO warehouse (account, items, money, password)
            VALUES (@account, @items, @money, @password)
            ON CONFLICT (account) DO UPDATE
            SET items = EXCLUDED.items, money = EXCLUDED.money, password = EXCLUDED.password
            """, conn);
        cmd.Parameters.AddWithValue("account", account);
        cmd.Parameters.AddWithValue("items", items);
        cmd.Parameters.AddWithValue("money", (long)money);
        cmd.Parameters.AddWithValue("password", (int)password);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---------------------------------------------------------------- Guilds (Clanes)

    private static async Task EnsureGuildTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmdInit = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS guilds (
                name text PRIMARY KEY,
                master_name text NOT NULL,
                logo bytea NOT NULL,
                score integer NOT NULL DEFAULT 0,
                notice text
            );
            CREATE TABLE IF NOT EXISTS guild_members (
                guild_name text NOT NULL,
                member_name text PRIMARY KEY,
                status integer NOT NULL DEFAULT 0
            );
            """, conn);
        await cmdInit.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte> CreateGuildAsync(string guildName, string masterName, byte[] logo, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureGuildTablesAsync(conn, ct);

        // Verificar si la guild o el miembro ya existen
        await using var cmdCheck = new NpgsqlCommand(
            "SELECT 1 FROM guilds WHERE name = @g UNION SELECT 1 FROM guild_members WHERE member_name = @m", conn);
        cmdCheck.Parameters.AddWithValue("g", guildName);
        cmdCheck.Parameters.AddWithValue("m", masterName);
        await using (var r = await cmdCheck.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                return 0; // Ya existe
            }
        }

        // Insertar Guild
        await using var cmdGuild = new NpgsqlCommand(
            "INSERT INTO guilds (name, master_name, logo) VALUES (@name, @master, @logo)", conn);
        cmdGuild.Parameters.AddWithValue("name", guildName);
        cmdGuild.Parameters.AddWithValue("master", masterName);
        cmdGuild.Parameters.AddWithValue("logo", logo);
        await cmdGuild.ExecuteNonQueryAsync(ct);

        // Insertar Master (status = 128)
        await using var cmdMember = new NpgsqlCommand(
            "INSERT INTO guild_members (guild_name, member_name, status) VALUES (@g, @m, 128)", conn);
        cmdMember.Parameters.AddWithValue("g", guildName);
        cmdMember.Parameters.AddWithValue("m", masterName);
        await cmdMember.ExecuteNonQueryAsync(ct);

        return 1; // Success
    }

    public async Task<bool> SaveGuildMemberAsync(string guildName, string memberName, byte status, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureGuildTablesAsync(conn, ct);

        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO guild_members (guild_name, member_name, status)
            VALUES (@g, @m, @s)
            ON CONFLICT (member_name) DO UPDATE SET guild_name = EXCLUDED.guild_name, status = EXCLUDED.status
            """, conn);
        cmd.Parameters.AddWithValue("g", guildName);
        cmd.Parameters.AddWithValue("m", memberName);
        cmd.Parameters.AddWithValue("s", (int)status);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteGuildMemberAsync(string guildName, string memberName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureGuildTablesAsync(conn, ct);

        await using var cmd = new NpgsqlCommand(
            "DELETE FROM guild_members WHERE guild_name = @g AND member_name = @m", conn);
        cmd.Parameters.AddWithValue("g", guildName);
        cmd.Parameters.AddWithValue("m", memberName);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteGuildAsync(string guildName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureGuildTablesAsync(conn, ct);

        await using var cmdMembers = new NpgsqlCommand("DELETE FROM guild_members WHERE guild_name = @g", conn);
        cmdMembers.Parameters.AddWithValue("g", guildName);
        await cmdMembers.ExecuteNonQueryAsync(ct);

        await using var cmdGuild = new NpgsqlCommand("DELETE FROM guilds WHERE name = @g", conn);
        cmdGuild.Parameters.AddWithValue("g", guildName);
        return await cmdGuild.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<GuildMemberInfo?> GetCharacterGuildInfoAsync(string characterName, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await EnsureGuildTablesAsync(conn, ct);

        await using var cmd = new NpgsqlCommand(
            """
            SELECT m.guild_name, m.status, g.logo
            FROM guild_members m
            JOIN guilds g ON g.name = m.guild_name
            WHERE m.member_name = @m
            """, conn);
        cmd.Parameters.AddWithValue("m", characterName);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            string gName = r.GetString(0);
            byte status = (byte)r.GetInt32(1);
            byte[] logo = r.IsDBNull(2) ? new byte[32] : (byte[])r.GetValue(2);
            return new GuildMemberInfo(gName, status, logo);
        }
        return null;
    }
}

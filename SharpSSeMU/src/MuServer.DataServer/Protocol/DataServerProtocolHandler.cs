using System.Linq;
using System.Text;
using MuServer.DataServer.Config;
using MuServer.DataServer.Data;
using MuServer.DataServer.Db;
using MuServer.DataServer.Util;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.DataServer.Protocol;

/// <summary>
/// Puerto de DataServerProtocolCore + GD*Recv (DataServerProtocol.cpp). Dispatcha por el byte
/// "head" (posición 2 del buffer, PBMSG_HEAD.head) — a diferencia de ConnectServer/JoinServer acá
/// no hace falta distinguir C1 vs C2 para encontrar el head, siempre está en el mismo offset.
///
/// Subsistemas explícitamente diferidos a un incremento posterior (no rompen el protocolo, solo
/// no se atienden): 0x05 Warehouse, 0x0A/0x0B name check/change, 0x0C Quest, 0x0F CommandManager,
/// 0x13 CustomPick, 0x14/0x1E/0x1F Acheron/Crywolf, 0x24 SNS, 0x49 Crywolf save, 0x4E SNS save,
/// 0x94 GoldenArcher, 0xA0 Guild, 0xB0 Friend. Se loguean como "not implemented" si llegan.
/// </summary>
public sealed class DataServerProtocolHandler
{
    private readonly ICharacterDataRepository _repo;
    private readonly CharacterSessionStore _sessions;
    private readonly GameServerRegistry _registry;
    private readonly BadSyntaxStore _badSyntax;

    public DataServerProtocolHandler(ICharacterDataRepository repo, CharacterSessionStore sessions, GameServerRegistry registry, BadSyntaxStore badSyntax)
    {
        _repo = repo;
        _sessions = sessions;
        _registry = registry;
        _badSyntax = badSyntax;
    }

    public async Task HandlePacketAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        // El head vive en offset 2 para paquetes C1 (PBMSG_HEAD: type,size,head) pero en offset 3
        // para paquetes C2 (PWMSG_HEAD: type,size[2],head) — igual que el subcódigo en Connect/JoinServer.
        byte head = packet[0] == 0xC1 ? packet[2] : packet[3];

        try
        {
            switch (head)
            {
                case 0x00: await OnServerInfoAsync(link, packet, ct); break;
                case 0x01: await OnCharacterListAsync(link, packet, ct); break;
                case 0x02: await OnCharacterCreateAsync(link, packet, ct); break;
                case 0x03: await OnCharacterDeleteAsync(link, packet, ct); break;
                case 0x04: await OnCharacterInfoAsync(link, packet, ct); break;
                case 0x05: await OnWarehouseAsync(link, packet, ct); break;
                case 0x07: await OnCreateItemAsync(link, packet, ct); break;
                case 0x08: await OnOptionDataAsync(link, packet, ct); break;
                case 0x09: await OnPetItemInfoAsync(link, packet, ct); break;
                case 0x20: await OnGlobalPostAsync(packet, ct); break;
                case 0x21: await OnGlobalNoticeAsync(packet, ct); break;
                case 0x30: await OnCharacterInfoSaveAsync(packet, ct); break;
                case 0x31: await OnInventoryItemSaveAsync(packet, ct); break;
                case 0x33: await OnOptionDataSaveAsync(packet, ct); break;
                case 0x34: await OnPetItemInfoSaveAsync(packet, ct); break;
                case 0x39: await OnResetInfoSaveAsync(packet, ct); break;
                case 0x3A: await OnMasterResetInfoSaveAsync(packet, ct); break;
                case 0x3C: await OnRankingDuelSaveAsync(packet, ct); break;
                case 0x3D: await OnRankingScoreSaveAsync(packet, "ranking_blood_castle", ct); break;
                case 0x3E: await OnRankingScoreSaveAsync(packet, "ranking_chaos_castle", ct); break;
                case 0x3F: await OnRankingScoreSaveAsync(packet, "ranking_devil_square", ct); break;
                case 0x40: await OnRankingScoreSaveAsync(packet, "ranking_illusion_temple", ct); break;
                case 0x42: await OnCreationCardSaveAsync(packet, ct); break;
                case 0x50: await OnMonsterKillCountAsync(link, packet, ct); break;
                case 0x70: await OnConnectCharacterAsync(link, packet, ct); break;
                case 0x71: await OnDisconnectCharacterAsync(link, packet, ct); break;
                case 0x72: await OnGlobalWhisperAsync(link, packet, ct); break;
                case 0xA0: await OnGuildAsync(link, packet, ct); break;
                case 0xB0: await HandleFriendAsync(link, packet, ct); break;

                default:
                    Log.Add(LogColor.Green, "[DataServer] Head 0x{0:X2} not implemented yet (Guild/Friend/Warehouse/Quest/CommandManager/CustomPick/GoldenArcher/etc.)", head);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Add(LogColor.Red, "[DataServer] Error processing head 0x{0:X2}: {1}", head, ex);
        }
    }

    // ---------------------------------------------------------------- 0x00 ServerInfo

    private async Task OnServerInfoAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = ServerInfoRecv.Parse(packet);

        var itemCount = await _repo.GetItemCountAsync(ct);

        link.ServerName = recv.ServerName;
        link.ServerPort = recv.ServerPort;
        link.ServerCode = recv.ServerCode;

        await link.SendAsync(DataServerPacketBuilder.ServerInfoSend(1, (uint)itemCount), ct);

        Log.Add(LogColor.Blue, "[DataServer] GameServer registered: {0} ({1}:{2}) code={3}",
            recv.ServerName, link.IpAddress, recv.ServerPort, recv.ServerCode);
    }

    // ---------------------------------------------------------------- 0x01 CharacterList

    private async Task OnCharacterListAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = CharacterListRecv.Parse(packet);

        await _repo.EnsureAccountCharacterAsync(recv.Account, ct);
        var slots = await _repo.GetAccountSlotsAsync(recv.Account, ct) ?? new AccountSlots(0, 2, new string?[5]);

        var entries = new List<CharacterListEntry>();

        for (int n = 0; n < 5; n++)
        {
            var name = slots.Names[n];

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var row = await _repo.GetCharacterListRowAsync(recv.Account, name, ct);

            if (row is null)
            {
                continue;
            }

            entries.Add(new CharacterListEntry((byte)n, name, (ushort)row.CLevel, (byte)row.Class, (byte)row.CtlCode,
                CompactInventory(row.Inventory)));
        }

        await link.SendAsync(DataServerPacketBuilder.CharacterListSend(recv.Index, recv.Account, slots.MoveCnt, (byte)slots.ExtClass, entries), ct);
    }

    /// <summary>
    /// Puerto exacto de la transformación de GDCharacterListRecv: toma los primeros 12 "slots" de
    /// equipo del inventario (16 bytes cada uno) y los comprime a 5 bytes cada uno (bytes 0,1,7,8,9
    /// de cada item), para la vista compacta de selección de personaje. Si el slot está vacío
    /// (0xFF + bit de "sin item" en los bytes 7/9) queda como 0xFF x5.
    /// </summary>
    private static byte[] CompactInventory(byte[] inventory)
    {
        var compact = new byte[60];
        Array.Fill(compact, (byte)0xFF);

        for (int i = 0; i < 12; i++)
        {
            int baseOff = i * 16;

            if (baseOff + 16 > inventory.Length)
            {
                break;
            }

            byte b0 = inventory[baseOff + 0];
            byte b1 = inventory[baseOff + 1];
            byte b7 = inventory[baseOff + 7];
            byte b8 = inventory[baseOff + 8];
            byte b9 = inventory[baseOff + 9];

            int outOff = i * 5;

            if (b0 == 0xFF && (b7 & 0x80) == 0x80 && (b9 & 0xF0) == 0xF0)
            {
                // ya está en 0xFF por el Array.Fill de arriba
                continue;
            }

            compact[outOff + 0] = b0;
            compact[outOff + 1] = b1;
            compact[outOff + 2] = b7;
            compact[outOff + 3] = b8;
            compact[outOff + 4] = b9;
        }

        return compact;
    }

    private async Task OnWarehouseAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        byte subCode = packet[0] == 0xC1 ? packet[3] : packet[4];

        if (subCode == 0x00) // Leer Baúl (SDHP_WAREHOUSE_ITEM_SEND)
        {
            ushort index = (ushort)((packet[4] << 8) | packet[5]);
            string account = Encoding.ASCII.GetString(packet, 6, 10).TrimEnd('\0');

            var row = await _repo.GetWarehouseAsync(account, ct);
            var items = row?.Items ?? new byte[1920];
            if (items.Length < 1920)
            {
                var newItems = new byte[1920];
                Array.Fill(newItems, (byte)0xFF);
                Array.Copy(items, newItems, items.Length);
                items = newItems;
            }

            var w = new PacketWriter();
            w.WriteUInt16(index);
            Span<byte> accountBytes = stackalloc byte[10];
            accountBytes.Clear();
            Encoding.ASCII.GetBytes(account, accountBytes);
            w.WriteBytes(accountBytes.ToArray(), 10);
            w.WriteBytes(items, 1920);
            w.WriteUInt32(row?.Money ?? 0);
            w.WriteUInt16(row?.Password ?? 0);

            var sendBuf = PacketBuilder.BuildC2Sub(0x05, 0x00, w.ToArray());
            await link.SendAsync(sendBuf, ct);
        }
        else if (subCode == 0x30) // Guardar Baúl (SDHP_WAREHOUSE_ITEM_SAVE_SEND)
        {
            ushort index = (ushort)((packet[5] << 8) | packet[6]);
            string account = Encoding.ASCII.GetString(packet, 7, 10).TrimEnd('\0');

            byte[] items = new byte[1920];
            if (packet.Length >= 17 + 1920)
            {
                Array.Copy(packet, 17, items, 0, 1920);
            }
            else
            {
                Array.Fill(items, (byte)0xFF);
            }

            uint money = packet.Length >= 1941 ? (uint)((packet[1937] << 24) | (packet[1938] << 16) | (packet[1939] << 8) | packet[1940]) : 0;
            ushort password = packet.Length >= 1943 ? (ushort)((packet[1941] << 8) | packet[1942]) : (ushort)0;

            await _repo.SaveWarehouseAsync(account, items, money, password, ct);
        }
    }

    // ---------------------------------------------------------------- 0x02 CharacterCreate

    private static int? FindSlot(string?[] names, string target)
    {
        for (int n = 0; n < names.Length; n++)
        {
            if (string.Equals(names[n] ?? string.Empty, target, StringComparison.OrdinalIgnoreCase))
            {
                return n;
            }
        }

        return null;
    }

    private async Task OnCharacterCreateAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = CharacterCreateRecv.Parse(packet);

        byte result;
        byte slot = 0;
        ushort level = 1;

        bool syntaxOk = DataServerUtil.CheckTextSyntax(recv.Name) && _badSyntax.CheckSyntax(recv.Name);

        if (!syntaxOk)
        {
            result = 0;
        }
        else
        {
            var slots = await _repo.GetAccountSlotsAsync(recv.Account, ct);

            if (slots is null)
            {
                result = 0;
            }
            else
            {
                var freeSlot = FindSlot(slots.Names, string.Empty);

                if (freeSlot is null)
                {
                    result = 2; // sin espacio: los 5 slots ocupados
                }
                else
                {
                    slot = (byte)freeSlot.Value;
                    result = await _repo.CreateCharacterAsync(recv.Account, recv.Name, recv.Class, ct);

                    if (result == 1)
                    {
                        await _repo.SetSlotNameAsync(recv.Account, slot, recv.Name, ct);
                    }
                }
            }
        }

        await link.SendAsync(DataServerPacketBuilder.CharacterCreateSend(recv.Index, recv.Account, recv.Name, result, slot, recv.Class, level), ct);
    }

    // ---------------------------------------------------------------- 0x03 CharacterDelete

    private async Task OnCharacterDeleteAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = CharacterDeleteRecv.Parse(packet);

        byte result = DataServerUtil.CheckTextSyntax(recv.Name) ? (byte)1 : (byte)0;

        if (result == 1)
        {
            result = await _repo.DeleteCharacterAsync(recv.Account, recv.Name, ct);

            if (result == 1)
            {
                var slots = await _repo.GetAccountSlotsAsync(recv.Account, ct);
                var foundSlot = slots is null ? null : FindSlot(slots.Names, recv.Name);

                if (foundSlot is not null)
                {
                    await _repo.SetSlotNameAsync(recv.Account, foundSlot.Value, null, ct);
                }
            }
        }

        await link.SendAsync(DataServerPacketBuilder.CharacterDeleteSend(recv.Index, recv.Account, result), ct);
    }

    // ---------------------------------------------------------------- 0x04 CharacterInfo

    private async Task OnCharacterInfoAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = CharacterInfoRecv.Parse(packet);

        bool syntaxOk = DataServerUtil.CheckTextSyntax(recv.Name);
        var full = syntaxOk ? await _repo.GetCharacterFullAsync(recv.Account, recv.Name, ct) : null;

        if (full is null)
        {
            await link.SendAsync(DataServerPacketBuilder.CharacterInfoSend(
                index: recv.Index, account: recv.Account, name: recv.Name, result: 0, cls: 0, level: 0,
                levelUpPoint: 0, experience: 0, money: 0, strength: 0, dexterity: 0, vitality: 0, energy: 0,
                leadership: 0, life: 0, maxLife: 0, mana: 0, maxMana: 0, bp: 0, maxBp: 0,
                inventory: new byte[1728], skill: new byte[180], map: 0, x: 0, y: 0, dir: 0, pkCount: 0,
                pkLevel: 0, pkTime: 0, ctlCode: 0, quest: new byte[50], chatLimitTime: 0, fruitAddPoint: 0,
                fruitSubPoint: 0, effect: new byte[208], reset: 0, masterReset: 0, isNewChar: 0, married: 0,
                marryName: string.Empty, bcCount: 0, ccCount: 0, dsCount: 0), ct);
            return;
        }

        var reset = await _repo.GetResetInfoAsync(recv.Account, recv.Name, ct);
        var masterReset = await _repo.GetMasterResetInfoAsync(recv.Account, recv.Name, ct);
        var eventEntry = await _repo.GetEventEntryInfoAsync(recv.Account, recv.Name, ct);
        await _repo.SetLastCharacterAsync(recv.Account, recv.Name, ct);

        await link.SendAsync(DataServerPacketBuilder.CharacterInfoSend(
            recv.Index, recv.Account, recv.Name, 1, (byte)full.Class, (ushort)full.CLevel, (uint)full.LevelUpPoint,
            (uint)full.Experience, (uint)full.Money, (uint)full.Strength, (uint)full.Dexterity, (uint)full.Vitality,
            (uint)full.Energy, (uint)full.Leadership, (uint)full.Life, (uint)full.MaxLife, (uint)full.Mana,
            (uint)full.MaxMana, (uint)full.BP, (uint)full.MaxBP, full.Inventory, full.MagicList,
            (byte)full.MapNumber, (byte)full.MapPosX, (byte)full.MapPosY, (byte)full.MapDir, (uint)full.PkCount,
            (uint)full.PkLevel, (uint)full.PkTime, (byte)full.CtlCode, full.Quest, (uint)full.ChatLimitTime,
            (ushort)full.FruitAddPoint, (ushort)full.FruitSubPoint, full.EffectList, (uint)reset.Reset,
            (uint)masterReset.MasterReset, 0, 0, string.Empty,
            (ushort)eventEntry.BCCount, (ushort)eventEntry.CCCount, (ushort)eventEntry.DSCount), ct);
    }

    // ---------------------------------------------------------------- 0x07 CreateItem

    private async Task OnCreateItemAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = CreateItemRecv.Parse(packet);

        var serial = await _repo.GetNextItemSerialAsync(ct);

        if (serial < 0)
        {
            serial = 0;
        }

        // ItemIndex 0x1A4/0x1A5 = mascotas (Guardian/Imp) en 0.99: se registran en T_PetItem_Info.
        if (recv.ItemIndex is 0x1A4 or 0x1A5)
        {
            await _repo.EnsurePetItemAsync(serial, 1, 0, ct);
        }

        await link.SendAsync(DataServerPacketBuilder.CreateItemSend(
            recv.Index, recv.Account, recv.X, recv.Y, recv.Map, (uint)serial, recv.ItemIndex, recv.Level, recv.Dur,
            recv.Option1, recv.Option2, recv.Option3, recv.NewOption, recv.LootIndex, recv.SetOption), ct);
    }

    // ---------------------------------------------------------------- 0x08 OptionData

    private async Task OnOptionDataAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = OptionDataRecv.Parse(packet);
        var opt = await _repo.GetOptionDataAsync(recv.Name, ct);

        await link.SendAsync(DataServerPacketBuilder.OptionDataSend(
            recv.Index, recv.Account, recv.Name, opt.SkillKey, (byte)opt.GameOption, (byte)opt.QKey, (byte)opt.WKey,
            (byte)opt.EKey, (byte)opt.ChatWindow), ct);
    }

    // ---------------------------------------------------------------- 0x09 PetItemInfo

    private async Task OnPetItemInfoAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = PetItemInfoRecv.Parse(packet);
        var results = new List<(byte, uint, byte, uint)>();

        foreach (var (slot, serial) in recv.Items)
        {
            var pet = await _repo.GetPetItemAsync(serial, ct);

            if (pet is null)
            {
                await _repo.EnsurePetItemAsync(serial, 1, 0, ct);
                results.Add((slot, serial, 1, 0));
            }
            else
            {
                results.Add((slot, serial, (byte)pet.Level, (uint)pet.Experience));
            }
        }

        await link.SendAsync(DataServerPacketBuilder.PetItemInfoSend(recv.Index, recv.Account, recv.Type, results), ct);
    }

    // ---------------------------------------------------------------- 0x20 / 0x21 broadcast a todos los GameServer

    private async Task OnGlobalPostAsync(byte[] packet, CancellationToken ct)
    {
        var recv = GlobalPostRecv.Parse(packet);
        var send = DataServerPacketBuilder.GlobalPostSend(recv.MapServerGroup, recv.Type, recv.Name, recv.Message);
        await _registry.BroadcastAsync(send, ct);
    }

    private async Task OnGlobalNoticeAsync(byte[] packet, CancellationToken ct)
    {
        var recv = GlobalNoticeRecv.Parse(packet);
        var send = DataServerPacketBuilder.GlobalNoticeSend(recv.MapServerGroup, recv.Type, recv.Count, recv.Opacity, recv.Delay, recv.Color, recv.Speed, recv.Message);
        await _registry.BroadcastAsync(send, ct);
    }

    // ---------------------------------------------------------------- 0x30/0x31/0x33/0x34/0x39/0x3A/0x3C-0x40/0x42 saves (sin respuesta)

    private async Task OnCharacterInfoSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = CharacterInfoSaveRecv.Parse(packet);

        var row = new CharacterFullRow(
            CLevel: recv.Level, Class: recv.Class, LevelUpPoint: (int)recv.LevelUpPoint, Experience: recv.Experience,
            Strength: (int)recv.Strength, Dexterity: (int)recv.Dexterity, Vitality: (int)recv.Vitality,
            Energy: (int)recv.Energy, Leadership: (int)recv.Leadership, Inventory: recv.Inventory, MagicList: recv.Skill,
            Money: recv.Money, Life: recv.Life, MaxLife: recv.MaxLife, Mana: recv.Mana, MaxMana: recv.MaxMana,
            BP: recv.BP, MaxBP: recv.MaxBP, MapNumber: recv.Map, MapPosX: recv.X, MapPosY: recv.Y, MapDir: recv.Dir,
            PkCount: (int)recv.PKCount, PkLevel: (int)recv.PKLevel, PkTime: (int)recv.PKTime, CtlCode: 0,
            Quest: recv.Quest, ChatLimitTime: (int)recv.ChatLimitTime, EffectList: recv.Effect,
            FruitAddPoint: recv.FruitAddPoint, FruitSubPoint: recv.FruitSubPoint);

        await _repo.SaveCharacterAsync(recv.Account, recv.Name, row, ct);
        await _repo.SetEventEntryInfoAsync(recv.Account, recv.Name, recv.BCCount, recv.CCCount, recv.DSCount, ct);
    }

    private async Task OnInventoryItemSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = InventoryItemSaveRecv.Parse(packet);
        await _repo.SaveInventoryAsync(recv.Account, recv.Name, recv.Inventory, ct);
    }

    private async Task OnOptionDataSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = OptionDataSaveRecv.Parse(packet);
        await _repo.SaveOptionDataAsync(recv.Name, recv.SkillKey, recv.GameOption, recv.QKey, recv.WKey, recv.EKey, recv.ChatWindow, ct);
    }

    private async Task OnPetItemInfoSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = PetItemInfoSaveRecv.Parse(packet);

        foreach (var (serial, level, experience) in recv.Items)
        {
            await _repo.SetPetItemAsync(serial, level, experience, ct);
        }
    }

    private async Task OnResetInfoSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = ResetInfoSaveRecv.Parse(packet);
        await _repo.SetResetInfoAsync(recv.Account, recv.Name, (int)recv.Reset, (int)recv.ResetDay, (int)recv.ResetWek, (int)recv.ResetMon, ct);
    }

    private async Task OnMasterResetInfoSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = MasterResetInfoSaveRecv.Parse(packet);
        await _repo.SetMasterResetInfoAsync(recv.Account, recv.Name, (int)recv.Reset, (int)recv.MasterReset, (int)recv.MasterResetDay, (int)recv.MasterResetWek, (int)recv.MasterResetMon, ct);
    }

    private async Task OnRankingDuelSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = RankingDuelSaveRecv.Parse(packet);
        await _repo.AddRankingDuelAsync(recv.Name, (int)recv.WinScore, (int)recv.LoseScore, ct);
    }

    private async Task OnRankingScoreSaveAsync(byte[] packet, string table, CancellationToken ct)
    {
        var recv = RankingScoreSaveRecv.Parse(packet);
        await _repo.AddRankingScoreAsync(table, recv.Name, (int)recv.Score, ct);
    }

    private async Task OnCreationCardSaveAsync(byte[] packet, CancellationToken ct)
    {
        var recv = CreationCardSaveRecv.Parse(packet);
        await _repo.SetExtClassAsync(recv.Account, recv.ExtClass, ct);
    }

    // ---------------------------------------------------------------- 0x50 MonsterKillCount

    private async Task OnMonsterKillCountAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = MonsterKillCountRecv.Parse(packet);
        var count = await _repo.IncrementMonsterKillCountAsync(recv.Name, recv.MonsterClass, ct);

        await link.SendAsync(DataServerPacketBuilder.MonsterKillCountSend(recv.Index, recv.Account, recv.Name, recv.MonsterClass, (ushort)count), ct);
    }

    // ---------------------------------------------------------------- 0x70/0x71 connect/disconnect (en memoria)

    private async Task OnConnectCharacterAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = ConnectCharacterRecv.Parse(packet);

        _sessions.Upsert(new CharacterSession
        {
            Name = recv.Name,
            Account = recv.Account,
            UserIndex = recv.Index,
            GameServerCode = link.ServerCode,
        });

        // gGuild.MemberConnect: diferido (Guild no implementado todavía).
        await PushFriendStateAsync(recv.Name, (byte)link.ServerCode, ct);
    }

    private async Task OnDisconnectCharacterAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = DisconnectCharacterRecv.Parse(packet);

        if (_sessions.TryGet(recv.Name, out var session)
            && session.UserIndex == recv.Index
            && session.GameServerCode == link.ServerCode)
        {
            _sessions.Remove(recv.Name);
        }

        // gGuild.MemberDisconnect: diferido.
        await PushFriendStateAsync(recv.Name, 0xFF, ct);
    }

    /// <summary>Puerto de CFriend::DGFriendStateSend (Friend.cpp:502): a todo el que tenga a
    /// <paramref name="name"/> en SU lista de amigos y esté online ahora mismo, avisarle el cambio
    /// de estado (0xFF=offline, o el ServerCode real).</summary>
    private async Task PushFriendStateAsync(string name, byte server, CancellationToken ct)
    {
        var owners = await _repo.GetFriendsOfAsync(name, ct);

        foreach (var ownerName in owners)
        {
            if (!_sessions.TryGet(ownerName, out var owner))
            {
                continue;
            }

            var ownerLink = _registry.FindByCode(owner.GameServerCode);

            if (ownerLink is not null)
            {
                await ownerLink.SendAsync(
                    DataServerPacketBuilder.FriendStateSend(owner.UserIndex, owner.Account, owner.Name, name, server), ct);
            }
        }
    }

    // ---------------------------------------------------------------- 0xB0 amigos (Fase 5 GameServer)

    private async Task HandleFriendAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        byte subh = packet[3];

        switch (subh)
        {
            case 0x00: await OnFriendListRequestAsync(link, packet, ct); break;
            case 0x01: await OnFriendRequestAsync(link, packet, ct); break;
            case 0x03: await OnFriendResultAsync(link, packet, ct); break;
            case 0x05: await OnFriendDeleteAsync(link, packet, ct); break;

            default:
                Log.Add(LogColor.Green, "[DataServer] Head 0xB0:0x{0:X2} not handled yet (friend mail, Phase 5)", subh);
                break;
        }
    }

    private async Task OnFriendListRequestAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = FriendListRequest.Parse(packet);
        var names = await _repo.GetFriendListAsync(recv.Name, ct);
        var entries = new List<(string Name, byte Server)>(names.Count);

        foreach (var friendName in names)
        {
            byte server = _sessions.TryGet(friendName, out var session) ? (byte)session.GameServerCode : (byte)0xFF;
            entries.Add((friendName, server));
        }

        await link.SendAsync(DataServerPacketBuilder.FriendListSend(recv.Index, recv.Account, recv.Name, entries), ct);
    }

    private async Task OnFriendRequestAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = FriendRequestRecv.Parse(packet);

        if (string.Equals(recv.Name, recv.TargetName, StringComparison.OrdinalIgnoreCase)
            || !await _repo.CharacterExistsAsync(recv.TargetName, ct))
        {
            await link.SendAsync(DataServerPacketBuilder.FriendRequestResultSend(recv.Index, recv.Account, recv.Name, 0, recv.TargetName), ct);
            return;
        }

        var existingFriends = await _repo.GetFriendListAsync(recv.Name, ct);

        if (existingFriends.Any(f => string.Equals(f, recv.TargetName, StringComparison.OrdinalIgnoreCase)))
        {
            await link.SendAsync(DataServerPacketBuilder.FriendRequestResultSend(recv.Index, recv.Account, recv.Name, 0, recv.TargetName), ct);
            return;
        }

        await _repo.AddFriendRequestAsync(recv.TargetName, recv.Name, ct);

        if (_sessions.TryGet(recv.TargetName, out var target))
        {
            var targetLink = _registry.FindByCode(target.GameServerCode);

            if (targetLink is not null)
            {
                await targetLink.SendAsync(
                    DataServerPacketBuilder.FriendRequestIncomingSend(target.UserIndex, target.Account, target.Name, recv.Name, (byte)link.ServerCode), ct);
            }
        }

        await link.SendAsync(DataServerPacketBuilder.FriendRequestResultSend(recv.Index, recv.Account, recv.Name, 1, recv.TargetName), ct);
    }

    private async Task OnFriendResultAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = FriendResultRecv.Parse(packet);
        byte result = 0;

        if (recv.Result != 0 && await _repo.TryConsumeFriendRequestAsync(recv.Name, recv.RequesterName, ct))
        {
            await _repo.AddFriendPairAsync(recv.Name, recv.RequesterName, ct);
            result = 1;
        }
        else
        {
            await _repo.TryConsumeFriendRequestAsync(recv.Name, recv.RequesterName, ct);
        }

        await link.SendAsync(DataServerPacketBuilder.FriendResultAckSend(recv.Index, recv.Account, recv.Name, result, recv.RequesterName), ct);

        if (_sessions.TryGet(recv.RequesterName, out var requester))
        {
            var requesterLink = _registry.FindByCode(requester.GameServerCode);

            if (requesterLink is not null)
            {
                await requesterLink.SendAsync(
                    DataServerPacketBuilder.FriendResultDeliverSend(requester.UserIndex, requester.Account, requester.Name, result, recv.Name), ct);
            }
        }
    }

    private async Task OnFriendDeleteAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = FriendDeleteRecv.Parse(packet);
        bool removed = await _repo.RemoveFriendPairAsync(recv.Name, recv.TargetName, ct);
        await link.SendAsync(
            DataServerPacketBuilder.FriendDeleteResultSend(recv.Index, recv.Account, recv.Name, (byte)(removed ? 1 : 0), recv.TargetName), ct);
    }

    // ---------------------------------------------------------------- 0x72/0x73 whisper entre GameServers

    private async Task OnGlobalWhisperAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        var recv = GlobalWhisperRecv.Parse(packet);

        byte result;

        if (_sessions.TryGet(recv.TargetName, out var target))
        {
            result = 1;
            var targetLink = _registry.FindByCode(target.GameServerCode);

            if (targetLink is not null)
            {
                var echo = DataServerPacketBuilder.GlobalWhisperEchoSend(target.UserIndex, target.Account, target.Name, recv.Name, recv.Message);
                await targetLink.SendAsync(echo, ct);
            }
        }
        else
        {
            result = 0;
        }

        await link.SendAsync(DataServerPacketBuilder.GlobalWhisperSend(recv.Index, recv.Account, recv.Name, result, recv.TargetName, recv.Message), ct);
    }

    /// <summary>Puerto de ClearServerCharacterInfo: al caerse un GameServer, se limpian sus personajes online.</summary>
    public void ClearServerCharacters(int serverCode)
    {
        // No hace falta iterar acá: CharacterSessionStore ya expone ClearByServerCode.
    }

    private async Task OnGuildAsync(GameServerLink link, byte[] packet, CancellationToken ct)
    {
        byte subCode = packet[0] == 0xC1 ? packet[3] : packet[4];

        if (subCode == 0x00) // Crear Guild
        {
            string guildName = Encoding.Latin1.GetString(packet, 7, 8).TrimEnd('\0');
            string masterName = Encoding.Latin1.GetString(packet, 15, 10).TrimEnd('\0');
            byte[] logo = new byte[32];
            if (packet.Length >= 25 + 32)
            {
                Array.Copy(packet, 25, logo, 0, 32);
            }

            await _repo.CreateGuildAsync(guildName, masterName, logo, ct);
        }
        else if (subCode == 0x01) // Guardar Miembro de Guild
        {
            string guildName = Encoding.Latin1.GetString(packet, 5, 8).TrimEnd('\0');
            string memberName = Encoding.Latin1.GetString(packet, 13, 10).TrimEnd('\0');
            byte status = packet.Length > 23 ? packet[23] : (byte)0;

            await _repo.SaveGuildMemberAsync(guildName, memberName, status, ct);
        }
        else if (subCode == 0x02) // Borrar Miembro de Guild
        {
            string guildName = Encoding.Latin1.GetString(packet, 5, 8).TrimEnd('\0');
            string memberName = Encoding.Latin1.GetString(packet, 13, 10).TrimEnd('\0');

            await _repo.DeleteGuildMemberAsync(guildName, memberName, ct);
        }
        else if (subCode == 0x03) // Borrar Guild Completa
        {
            string guildName = Encoding.Latin1.GetString(packet, 5, 8).TrimEnd('\0');

            await _repo.DeleteGuildAsync(guildName, ct);
        }
    }
}

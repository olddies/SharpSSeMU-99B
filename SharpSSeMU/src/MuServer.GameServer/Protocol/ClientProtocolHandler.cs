using System.Collections.Concurrent;
using System.Linq;
using MuServer.GameServer.Config;
using MuServer.GameServer.Net;
using MuServer.GameServer.World;
using MuServer.Shared.Crypto;
using MuServer.Shared.Localization;
using MuServer.Shared.Logging;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary> Port of ProtocolCore (Protocol.cpp) — Phase 1 (login) + Phase 2 (character selection, entering the
/// world, player viewport, movement). The rest of the codes (chat, items, attack, guilds...) are added in later
/// phases; for now they are only logged if they arrive. </summary>
public sealed class ClientProtocolHandler
{
    private readonly GameServerConfig _config;
    private readonly JoinServerConnection _joinServer;
    private readonly DataServerConnection _dataServer;
    private readonly PlayerRegistry _players;
    private readonly MapRegistry _maps;
    private readonly MonsterRegistry _monsters;
    private readonly PartyRegistry _parties;
    private readonly DevilSquareManager? _devilSquare;
    private readonly ItemBalanceTable _itemBalance;
    private readonly ItemValueTable _itemValues;
    private readonly GameServerInfoChaosMix _chaosMixRates;
    private readonly int[] _addLuckSuccessRate2;
    private readonly CharacterBalanceConfig _characterBalance;
    private readonly SkillInfoTable _skills;
    private readonly SkillDamageTable _skillDamage;
    private readonly ShopManagerTable? _shops;
    private readonly GroundItemRegistry? _groundItems;
    private readonly GateTable _gates;
    private readonly MoveTable _moves;
    private readonly BloodCastleManager? _bloodCastle;
    private readonly ConcurrentDictionary<int, ClientSession> _pendingLogins = new();
    private readonly ConcurrentDictionary<int, ClientSession> _pendingCharacterInfo = new();
    private readonly ConcurrentDictionary<int, ClientSession> _pendingCharacterList = new();
    private readonly ConcurrentDictionary<int, ClientSession> _pendingCharacterCreate = new();
    private static readonly Random Rng = Random.Shared;

    /// <summary>Port of MAX_PARTY_DISTANCE (Party.h) -- range (in tiles, not viewport) used to decide which
    /// party members take part in the experience share when a monster is killed.</summary>
    private const int MaxPartyDistance = 10;

    /// <summary>Port of <c>gServerInfo.m_ItemDropTime</c> (<c>GameServerInfo - Common.dat</c>, see <see
    /// cref="ServerInfoConfig.ItemDropTimeSeconds"/>) -- lifetime of an item dropped on the ground before it
    /// disappears on its own. Computed (not <c>readonly</c>) so as to read the real value set in
    /// <c>WorldPacketBuilder.ServerInfo</c> by Program.cs at start-up.</summary>
    private static TimeSpan GroundItemLifetime => TimeSpan.FromSeconds(WorldPacketBuilder.ServerInfo.ItemDropTimeSeconds);

    /// <summary>Exact port of <c>m_ItemDropTime*500</c> (MapItem.cpp:29-99) -- half the total lifetime in
    /// milliseconds, the same calculation as the original.</summary>
    private static TimeSpan GroundItemLootLock => TimeSpan.FromMilliseconds(WorldPacketBuilder.ServerInfo.ItemDropTimeSeconds * 500);

    private readonly MessageTable? _messages;
    private readonly QuestTable _quests;
    private readonly QuestObjectiveTable _questObjectives;
    private readonly QuestRewardTable _questRewards;
    private readonly GameServerInfoCommon? _gsiCommon;

    public ClientProtocolHandler(
        GameServerConfig config, JoinServerConnection joinServer, DataServerConnection dataServer,
        PlayerRegistry players, MapRegistry maps, MonsterRegistry monsters, PartyRegistry parties,
        ItemBalanceTable itemBalance, CharacterBalanceConfig characterBalance,
        SkillInfoTable skills, SkillDamageTable skillDamage,
        DevilSquareManager? devilSquare = null, ShopManagerTable? shops = null, GroundItemRegistry? groundItems = null,
        GateTable? gates = null, MoveTable? moves = null, BloodCastleManager? bloodCastle = null, MessageTable? messages = null,
        QuestTable? quests = null, QuestObjectiveTable? questObjectives = null, QuestRewardTable? questRewards = null,
        GameServerInfoCommon? gsiCommon = null, ItemValueTable? itemValues = null,
        GameServerInfoChaosMix? chaosMixRates = null)
    {
        _config = config;
        _joinServer = joinServer;
        _dataServer = dataServer;
        _players = players;
        _maps = maps;
        _monsters = monsters;
        _parties = parties;
        _itemBalance = itemBalance;
        _itemValues = itemValues ?? new ItemValueTable();
        _chaosMixRates = chaosMixRates ?? new GameServerInfoChaosMix();
        _addLuckSuccessRate2 = gsiCommon?.AddLuckSuccessRate2 ?? new int[4];
        _characterBalance = characterBalance;
        _skills = skills;
        _skillDamage = skillDamage;
        _devilSquare = devilSquare;
        _shops = shops;
        _groundItems = groundItems;
        _gates = gates ?? new GateTable();
        _moves = moves ?? new MoveTable();
        _bloodCastle = bloodCastle;
        _messages = messages;
        _quests = quests ?? QuestTable.Empty;
        _questObjectives = questObjectives ?? QuestObjectiveTable.Empty;
        _questRewards = questRewards ?? QuestRewardTable.Empty;
        _gsiCommon = gsiCommon;

        if (_devilSquare != null)
        {
            _devilSquare.GrantExperience = (member, experience, ct) => ApplyExperienceGainAsync(member, -1, experience, 0, ct);
        }
    }

    public async Task OnConnectAsync(ClientSession session, CancellationToken ct)
    {
        var packet = ClientPacketBuilder.ConnectClientSend(1, (ushort)session.Index, _config.ServerVersion, _config.ServerCode);
        await session.SendAsync(packet, ct);
    }

    /// <summary> Port of ObjectManager.cpp (OBJECT_USER block of CloseClient/DelClient): ALWAYS notifies
    /// JoinServer of the disconnection (GJDisconnectAccountSend), even if the account was left empty (login did
    /// not complete) — the original sends it unconditionally for the user slot, not only after a successful
    /// login. Without this, JoinServer keeps believing the account is "connected" and the same client's next
    /// login attempt returns result 3 (already connected) instead of the real result. If the player had entered
    /// the world, they are also removed from the registry and DataServer is notified (0x71) so that it frees
    /// the slot in memory. </summary>
    public async Task OnDisconnectAsync(ClientSession session, CancellationToken ct)
    {
        _pendingLogins.TryRemove(session.Index, out _);
        _pendingCharacterInfo.TryRemove(session.Index, out _);
        _pendingCharacterList.TryRemove(session.Index, out _);
        _pendingCharacterCreate.TryRemove(session.Index, out _);

        if (session.Player != null)
        {
            // Port of CloseClient -> CParty::DelMember (the original also removes the player from their party
            // on disconnecting, not only when they send an explicit PMSG_PARTY_DEL_MEMBER_RECV).
            // notifyRemoved=false: there is no point sending a packet to a socket that is closing.
            await RemovePlayerFromPartyAsync(session.Player, ct, notifyRemoved: false);

            _players.Remove(session.Index);

            // Exact port of the real order (ObjectManager.cpp:623-627, DelCharacterInfo):
            // GDCharacterInfoSaveSend is ALWAYS sent right BEFORE GDDisconnectCharacterSend, with no exception
            // or throttle -- it is the unconditional "I'm leaving, save my current state now" save (unlike the
            // periodic/combat save, which do have a throttle). See the doc-comment of
            // DataServerCharacterPacketBuilder.CharacterInfoSaveSend.
            await SaveCharacterAsync(session.Player, ct);

            await _dataServer.SendAsync(
                DataServerCharacterPacketBuilder.DisconnectCharacter((ushort)session.Index, session.Account, session.Player.Name), ct);
        }

        if (session.LoginMessageSent)
        {
            await _joinServer.SendAsync(
                JoinServerClientPacketBuilder.DisconnectAccountSend((ushort)session.Index, session.Account ?? "", session.IpAddress),
                ct);
        }
    }

    /// <summary> Port of GDCharacterInfoSaveSend (DSProtocol.cpp:991-1047): sends DataServer the full snapshot
    /// of the character (stats, inventory, skill, quest, effects, PK, and crucially Map/X/Y/Dir) so that
    /// <c>NpgsqlCharacterDataRepository.SaveCharacterAsync</c> persists it. Without calling this somewhere, the
    /// <c>character</c> row is never updated after being created -- this method is the ONLY producer of the
    /// C2:0x30 packet, so any new caller (GM commands, Trade, PersonalShop, etc. when they are ported) should
    /// reuse it instead of building the packet by hand. No throttle in here on purpose -- each caller decides
    /// its own condition (unconditional on disconnect, 60s in <see cref="ApplyExperienceGainAsync"/>, 10min in
    /// the periodic autosave of <see cref="World.ViewportTicker"/>), just as the original spreads that logic
    /// across the call sites instead of centralising it. </summary>
    public async Task SaveCharacterAsync(PlayerObject player, CancellationToken ct)
    {
        await _dataServer.SendAsync(DataServerCharacterPacketBuilder.CharacterInfoSaveSend(player), ct);
    }

    public async Task HandlePacketAsync(ClientSession session, GameClientFramer.DecodedPacket packet, CancellationToken ct)
    {
        var p = packet.Data;
        byte head = p[0] == 0xC1 ? p[2] : p[3];

        try
        {
            switch (head)
            {
                case 0xF1:
                    await HandleF1Async(session, p, ct);
                    break;

                case 0xF3:
                    await HandleF3Async(session, p, ct);
                    break;

                case 0x00:
                    await OnChatAsync(session, p, ct);
                    break;

                case 0x02:
                    await OnChatWhisperAsync(session, p, ct);
                    break;

                case 0x40:
                    await OnPartyRequestAsync(session, p, ct);
                    break;

                case 0x41:
                    await OnPartyRequestResultAsync(session, p, ct);
                    break;

                case 0x42:
                    await OnPartyListRequestAsync(session, ct);
                    break;

                case 0x43:
                    await OnPartyDelMemberAsync(session, p, ct);
                    break;

                case 0xC0:
                    await OnFriendListRequestClientAsync(session, ct);
                    break;

                case 0xC1:
                    await OnFriendRequestClientAsync(session, p, ct);
                    break;

                case 0xC2:
                    await OnFriendResultClientAsync(session, p, ct);
                    break;

                case 0xC3:
                    await OnFriendDeleteClientAsync(session, p, ct);
                    break;

                case 0xD7:
                    await OnMoveAsync(session, p, ct);
                    break;

                case 0x1C:
                    await OnTeleportAsync(session, p, ct);
                    break;

                case 0x24:
                    await OnItemMoveAsync(session, p, ct);
                    break;

                case 0x22:
                    await OnItemGetAsync(session, p, ct);
                    break;

                case 0x23:
                    await OnItemDropAsync(session, p, ct);
                    break;

                case 0x26:
                    await OnItemUseAsync(session, p, ct);
                    break;

                case 0x34:
                    await OnItemRepairAsync(session, p, ct);
                    break;

                case 0x36:
                    await OnTradeRequestAsync(session, p, ct);
                    break;

                case 0x37:
                    await OnTradeResponseAsync(session, p, ct);
                    break;

                // The trade zen is 0x3A: that is how the original dispatches it (Protocol.cpp:124,
                // CGTradeMoneyRecv) and how the header generated from those sources declares it
                // (PMSG_TRADE_MONEY_RECV::kHead). The 0x3B comes from a wrong comment in the original's Trade.h
                // ("// C1:3B"), which was transcribed by hand both here and in the client's builder
                // (Wire099B.cpp), so this project's client and server understood each other but neither spoke
                // the real protocol. Both are accepted so as not to break clients already compiled with the old
                // value.
                case 0x3A:
                case 0x3B:
                    await OnTradeMoneyAsync(session, p, ct);
                    break;

                case 0x3C:
                    await OnTradeOkAsync(session, p, ct);
                    break;

                case 0x3D:
                    await OnTradeCancelAsync(session, p, ct);
                    break;

                case 0x52:
                    await OnGuildListAsync(session, ct);
                    break;

                case 0x54:
                    await OnGuildMasterOpenAsync(session, p, ct);
                    break;

                case 0x55:
                    await OnGuildCreateAsync(session, p, ct);
                    break;

                case 0x81:
                    await OnWarehouseMoneyAsync(session, p, ct);
                    break;

                case 0x82:
                    await OnWarehouseCloseAsync(session, p, ct);
                    break;

                case 0x83:
                    await OnWarehousePasswordAsync(session, p, ct);
                    break;

                case 0x86:
                    await OnChaosMixRecvAsync(session, p, ct);
                    break;

                case 0x87:
                    await OnChaosMixCloseAsync(session, ct);
                    break;

                case 0x88:
                    await OnChaosMixRateAsync(session, p, ct);
                    break;

                case 0x8E:
                    await OnTeleportMoveAsync(session, p, ct);
                    break;

                case 0xD9:
                    await OnAttackAsync(session, p, ct);
                    break;

                case 0x19:
                    await OnSkillAttackAsync(session, p, ct);
                    break;

                case 0x1E:
                    await OnDurationSkillAttackAsync(session, p, ct);
                    break;

                case 0x1B:
                    // Port of CGSkillCancelRecv (SkillManager.cpp:2591-2601): in the original it cancels an
                    // active skill effect (gEffectManager.DelEffect) -- this port has no effect manager yet
                    // (the 0x1E duration-skills are only the animation/ projectile, with no persistent
                    // damage-over-time to cancel), so there is nothing to undo and the no-op is faithful to the
                    // current scope, not a placeholder. The explicit case is added (instead of falling into the
                    // default) so as not to pollute the log with "not implemented" for a packet that is in fact
                    // already handled.
                    break;

                case 0x1D:
                    await OnMultiSkillAttackAsync(session, p, ct);
                    break;

                case 0x18:
                    await OnActionAsync(session, p, ct);
                    break;

                case 0xA0:
                    await OnQuestInfoAsync(session, ct);
                    break;

                case 0xA2:
                    await OnQuestStateAsync(session, p, ct);
                    break;

                case 0xA9:
                    await OnPetItemInfoAsync(session, p, ct);
                    break;

                case 0x30:
                    await OnNpcTalkAsync(session, p, ct);
                    break;

                case 0x31:
                    await OnNpcTalkCloseAsync(session, ct);
                    break;

                case 0x32:
                    await OnItemBuyAsync(session, p, ct);
                    break;

                case 0x33:
                    await OnItemSellAsync(session, p, ct);
                    break;

                case 0x35:
                    await OnItemRepairAsync(session, p, ct);
                    break;

                case 0x90:
                    await OnDevilSquareEnterAsync(session, p, ct);
                    break;

                case 0x91:
                    await OnEventRemainTimeAsync(session, p, ct);
                    break;

                case 0x9A:
                    await OnBloodCastleEnterAsync(session, p, ct);
                    break;

                case 0x0E:
                    break;

                case 0xD0:
                    await OnPositionAsync(session, p, ct);
                    break;

                default:
                    Log.Add(LogColor.Black, "[Protocol][{0}] Head 0x{1:X2} not implemented yet (out of scope for this phase)", session.Index, head);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Add(LogColor.Red, "[Protocol][{0}] Error processing head 0x{1:X2}: {2}", session.Index, head, ex);
        }
    }

    private async Task HandleF1Async(ClientSession session, byte[] p, CancellationToken ct)
    {
        byte subCode = p[3]; // PSBMSG_HEAD: type,size,head,subh

        switch (subCode)
        {
            case 0x01:
                await OnConnectAccountAsync(session, p, ct);
                break;

            case 0x02:
                byte closeType = p.Length > 4 ? p[4] : (byte)1; // 0 = Exit Game, 1 = Select Character, 2 = Select Server
                if (session.Player != null)
                {
                    await SaveCharacterAsync(session.Player, ct);
                    _players.Remove(session.Index);
                    session.Player = null;
                }

                await session.SendAsync(ClientPacketBuilder.CloseClientSend(closeType), ct);

                if (closeType == 0) // Exit Game
                {
                    session.Connected = false;
                    session.Socket.Close();
                }
                break;
        }
    }

    private async Task HandleF3Async(ClientSession session, byte[] p, CancellationToken ct)
    {
        byte subCode = p[3];

        switch (subCode)
        {
            case 0x00:
                await OnCharacterListRequestAsync(session, ct);
                break;

            case 0x01:
                await OnCharacterCreateRequestAsync(session, p, ct);
                break;

            case 0x03:
                await OnCharacterInfoRecvAsync(session, p, ct);
                break;

            case 0x09:
                OnHardwareIdRecv(session, p);
                break;

            case 0x12:
                // PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE -- no body. EXACT port of
                // CGCharacterMoveViewportEnableRecv (Protocol.cpp:1300-1310): in the original this ONLY does
                // "RegenOk = (RegenOk==1) ? 2 : RegenOk" -- that is, it is an ack that the client finished
                // loading the map AFTER a teleport/gate (which is the only thing that sets RegenOk to 1, see
                // User.cpp:2114/2144/2168/2220/2259). On the initial login RegenOk is already 0 since
                // gObjCharZeroSet (User.cpp:368, called in gObjAdd when accepting the connection) and this
                // receive is a no-op. Since the real world does not have portals/gates ported in this build yet
                // (see World/DevilSquareManager.cs, the only place that sends TeleportSend, does not set
                // RegenOk=true either), this handler remains an idempotent no-op -- see PlayerObject.RegenOk
                // for the correct default.
                if (session.Player != null)
                {
                    session.Player.RegenOk = false;
                }

                break;

            case 0x06:
                await OnLevelUpPointAsync(session, p, ct);
                break;

            default:
                Log.Add(LogColor.Black, "[Protocol][{0}] Head 0xF3:0x{1:X2} not implemented yet", session.Index, subCode);
                break;
        }
    }

    /// <summary>Port of CGLevelUpPointRecv (Protocol.cpp:1253-1298) + CObjectManager:: CharacterLevelUpPointAdd
    /// (ObjectManager.cpp:1043-1090) -- adds 1 level-up point to a stat (type:
    /// 0=Strength,1=Dexterity,2=Vitality,3=Energy,4=Leadership). It triggers a full combat recalculation just
    /// like the original (CharacterCalcAttribute is called synchronously after applying the point). The cap
    /// uses CharacterBalanceConfig.MaxStatPoint[player.AccountLevel] -- see
    /// PlayerObject.AccountLevel.</summary>
    private async Task OnLevelUpPointAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = LevelUpPointRecv.Parse(p);

        if (recv.Type > 4)
        {
            return;
        }

        uint current = recv.Type switch
        {
            0 => player.Strength,
            1 => player.Dexterity,
            2 => player.Vitality,
            3 => player.Energy,
            _ => player.Leadership,
        };

        bool ok = player.LevelUpPoint >= 1 && (current + 1) <= _characterBalance.MaxStatPoint[player.AccountLevel];

        if (ok)
        {
            switch (recv.Type)
            {
                case 0: player.Strength++; break;
                case 1: player.Dexterity++; break;
                case 2: player.Vitality++; break;
                case 3: player.Energy++; break;
                default: player.Leadership++; break;
            }

            player.LevelUpPoint--;
            player.RecalcCombatStats(_itemBalance, _characterBalance);
            await session.SendAsync(WorldPacketBuilder.NewCharacterCalcSend(player), ct);

            await SaveCharacterAsync(player, ct);
        }

        await session.SendAsync(CombatPacketBuilder.LevelUpPointSend(player, recv.Type, ok), ct);

        if (ok)
        {
            await session.SendAsync(LifePacketBuilder.LifeSend(0xFE, (int)player.MaxLife), ct);
            await session.SendAsync(LifePacketBuilder.LifeSend(0xFF, (int)player.Life), ct);
            await session.SendAsync(ManaPacketBuilder.ManaSend(0xFE, (int)player.MaxMana, (int)player.MaxBP), ct);
            await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);
        }
    }

    private async Task OnConnectAccountAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var recv = ConnectAccountRecv.Parse(p);

        if (!recv.ClientVersion.AsSpan().SequenceEqual(_config.ServerVersion))
        {
            await session.SendAsync(ClientPacketBuilder.ConnectAccountSend(6), ct);
            return;
        }

        // ServerSerial tiene 17 bytes (16 + nulo) en la config; el cliente manda 16.
        if (!recv.ClientSerial.AsSpan().SequenceEqual(_config.ServerSerial.AsSpan(0, 16)))
        {
            await session.SendAsync(ClientPacketBuilder.ConnectAccountSend(6), ct);
            return;
        }

        if (session.LoginMessageSent)
        {
            return; // the original ignores retries of the same login while one is in progress
        }

        session.LoginMessageSent = true;
        session.Account = recv.Account;

        _pendingLogins[session.Index] = session;

        await _joinServer.SendAsync(
            JoinServerClientPacketBuilder.ConnectAccountSend((ushort)session.Index, recv.Account, recv.Password, session.IpAddress),
            ct);
    }

    /// <summary>Callback from JoinServerConnection when the login result arrives (0x01 JGConnectAccountRecv).</summary>
    public async Task OnJoinAccountResultAsync(JoinAccountResultRecv msg, CancellationToken ct)
    {
        if (!_pendingLogins.TryGetValue(msg.Index, out var session) || !session.Connected)
        {
            return;
        }

        // result: 1=ok, 0=invalid account/password, 2=already connected, 3=full, 4=blocked (see
        // JoinServerProtocolHandler.OnConnectAccountAsync, which already implements this same order).
        await session.SendAsync(ClientPacketBuilder.ConnectAccountSend(msg.Result), ct);

        // Port of JGConnectAccountRecv (JSProtocol.cpp:85): gObj[index].AccountLevel = AccountLevel, with no
        // clamp in the original because it trusts what WZ_GetAccountLevel already validated. Here it is clamped
        // to 0-3 anyway as a safety net -- this port's rate arrays (AddExperienceRate, MoneyAmountDropRate,
        // MaxStatPoint, etc.) are fixed-length-4 C# arrays, so an out-of-range value would throw an
        // IndexOutOfRange instead of reading an invented row as the original would.
        session.AccountLevel = Math.Clamp((int)msg.AccountLevel, 0, 3);

        Log.Add(LogColor.Blue, "[Protocol][{0}] Login '{1}' -> result={2}", msg.Index, session.Account, msg.Result);
    }

    /// <summary>Port of CGCharacterListRecv (Protocol.cpp:1134-1144) -- asks DataServer for the account's
    /// character list for the selection screen. The real client sends it automatically as soon as the login
    /// gives result=1, BEFORE choosing a character (0xF3:0x03); without this it waits forever on the selection
    /// screen -- WorldTestClient did not need it because it simulates a client that already "knows" the name
    /// and sends 0xF3:0x03 directly.</summary>
    private async Task OnCharacterListRequestAsync(ClientSession session, CancellationToken ct)
    {
        if (!session.LoginMessageSent)
        {
            return;
        }

        _pendingCharacterList[session.Index] = session;

        await _dataServer.SendAsync(
            DataServerCharacterPacketBuilder.CharacterListRequest((ushort)session.Index, session.Account ?? string.Empty), ct);
    }

    /// <summary> Callback from DataServerConnection when SDHP_CHARACTER_LIST_RECV (0x01) arrives. Port of
    /// DGCharacterListRecv (DSProtocol.cpp:181-365): converts each character's compact 60-byte Inventory into
    /// CharSet[13], rebuilding real items with Item.FromCompactPreviewBytes + PlayerObject.BuildCharSet -- the
    /// same path a player already uses once inside the world (RebuildCharSet), only fed from the compact format
    /// instead of real Items[] -- and sends PMSG_CHARACTER_LIST_SEND to the client. </summary>
    public async Task OnCharacterListFromDataServerAsync(CharacterListFromDataServer msg, CancellationToken ct)
    {
        if (!_pendingCharacterList.TryRemove(msg.Index, out var session) || !session.Connected)
        {
            return;
        }

        var characters = new List<CharacterListItem>(msg.Entries.Count);

        foreach (var entry in msg.Entries)
        {
            var wear = new Item[Item.InventoryWearSize];

            for (int i = 0; i < Item.InventoryWearSize; i++)
            {
                int off = i * 5;
                wear[i] = Item.FromCompactPreviewBytes(
                    entry.CompactInventory[off + 0], entry.CompactInventory[off + 1],
                    entry.CompactInventory[off + 2], entry.CompactInventory[off + 3], entry.CompactInventory[off + 4]);
            }

            // entry.Class comes in raw DB format (0/16/32/48/64 = DW/DK/FE/MG/DL, with the low nibble reserved
            // for ChangeUp -- see the same pattern already ported in
            // OnCharacterCreateResultFromDataServerAsync). BuildCharSet needs the compact index 0-4 (Class/16)
            // and the real ChangeUp (Class%16), NOT the raw class with a fixed changeUp=0 -- FIXED: before,
            // entry.Class was passed directly, which overflowed a byte in cls*32 for any class other than DW
            // (0*32=0 coincides by chance with the empty case) and showed ALL characters as Dark Wizard on the
            // selection screen.
            var charSet = PlayerObject.BuildCharSet((byte)(entry.Class / 16), (byte)(entry.Class % 16), wear);
            characters.Add(new CharacterListItem(entry.Slot, entry.Name, entry.Level, entry.CtlCode, charSet));
        }

        await session.SendAsync(ClientPacketBuilder.CharacterListSend(msg.MoveCnt, characters), ct);

        Log.Add(LogColor.Blue, "[Protocol][{0}] Character list sent ({1} character(s))", msg.Index, characters.Count);
    }

    /// <summary>Port of CGCharacterCreateRecv (Protocol.cpp:2095-2153) -- asks DataServer to create the
    /// character. The original's prior class/CARD_CODE validation is not replicated here (see the comment of
    /// <c>DataServerCharacterPacketBuilder.CharacterCreateRequest</c>): it is forwarded directly and
    /// DataServer, which is already the real authority on which classes exist (<c>default_class_type</c>),
    /// rejects any non-enabled class with result=2.</summary>
    private async Task OnCharacterCreateRequestAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        if (!session.LoginMessageSent)
        {
            return;
        }

        var recv = CharacterCreateRecv.Parse(p);
        _pendingCharacterCreate[session.Index] = session;

        await _dataServer.SendAsync(
            DataServerCharacterPacketBuilder.CharacterCreateRequest((ushort)session.Index, session.Account ?? string.Empty, recv.Name, recv.Class), ct);
    }

    /// <summary> Callback from DataServerConnection when SDHP_CHARACTER_CREATE_RECV (0x02) arrives. Port of
    /// DGCharacterCreateRecv (DSProtocol.cpp:1148-1176): applies the same Class byte conversion (DB storage
    /// format -&gt; format the client expects) and sends PMSG_CHARACTER_CREATE_SEND. The real client reacts to
    /// this by refreshing the selection screen -- it normally keeps asking for the updated list (0xF3:0x00) on
    /// its own. </summary>
    public async Task OnCharacterCreateResultFromDataServerAsync(CharacterCreateResultFromDataServer msg, CancellationToken ct)
    {
        if (!_pendingCharacterCreate.TryRemove(msg.Index, out var session) || !session.Connected)
        {
            return;
        }

        // Puerto exacto de DGCharacterCreateRecv: pMsg.Class = (Class%16)*16; -= (pMsg.Class/32); += (Class/16)*32.
        int transformed = (msg.Class % 16) * 16;
        transformed -= transformed / 32;
        transformed += (msg.Class / 16) * 32;
        byte clientClass = (byte)(transformed & 0xFF);

        await session.SendAsync(
            ClientPacketBuilder.CharacterCreateSend(msg.Result, msg.Name, msg.Slot, msg.Level, clientClass, msg.Equipment), ct);

        Log.Add(LogColor.Blue, "[Protocol][{0}] Create character '{1}' -> result={2}", msg.Index, msg.Name, msg.Result);
    }

    /// <summary>Port of CConnectionManager::CGHardwareIdRecv (ConnectionManager.cpp:132-162) -- validates that
    /// the HardwareId has GUID format (44 chars, '-' at positions 8/17/26/35) and disconnects if it comes
    /// malformed, like the original (gObjDel). The HWID blacklist and the duplicate account check
    /// (CheckHardwareId) are not ported yet -- for now any well-formed HWID is accepted with no further
    /// validation (it does not affect the normal flow, it just does not detect multi-accounting yet).</summary>
    private void OnHardwareIdRecv(ClientSession session, byte[] p)
    {
        var hardwareId = PacketBuilder.ReadFixedString(p.AsSpan(4, 45));

        bool wellFormed = hardwareId.Length == 44
            && hardwareId[8] == '-' && hardwareId[17] == '-' && hardwareId[26] == '-' && hardwareId[35] == '-';

        if (!wellFormed)
        {
            session.Connected = false;
            session.Socket.Close();
        }
    }

    /// <summary>Puerto de CGCharacterInfoRecv (Protocol.cpp) -- pide el personaje a DataServer.</summary>
    private async Task OnCharacterInfoRecvAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        if (!session.LoginMessageSent)
        {
            return;
        }

        var recv = CharacterInfoRecv.Parse(p);
        _pendingCharacterInfo[session.Index] = session;

        await _dataServer.SendAsync(
            DataServerCharacterPacketBuilder.CharacterInfoRequest((ushort)session.Index, session.Account, recv.Name), ct);
    }

    /// <summary> Callback from DataServerConnection when SDHP_CHARACTER_INFO_RECV (0x04) arrives. Port of
    /// DGCharacterInfoRecv (DSProtocol.cpp): populates the player's state, marks them online, and sends the
    /// world-entry packets. Mutual visibility with other players is not instantaneous here either -- it is
    /// resolved on the next ViewportTicker tick (same as the original, see the note in
    /// gObjViewportListProtocolCreate of the research brief). </summary>
    public async Task OnCharacterInfoFromDataServerAsync(CharacterInfoFromDataServer msg, CancellationToken ct)
    {
        if (!_pendingCharacterInfo.TryRemove(msg.Index, out var session) || !session.Connected)
        {
            return;
        }

        if (msg.Result == 0)
        {
            Log.Add(LogColor.Red, "[Protocol][{0}] DataServer did not find the character '{1}'", msg.Index, msg.Name);
            return;
        }

        var player = new PlayerObject
        {
            Index = msg.Index,
            Session = session,
            Account = msg.Account,
            Name = msg.Name,
            AccountLevel = session.AccountLevel,
            // msg.Class comes in raw DB format (0/16/32/48/64 = DW/DK/FE/MG/DL + ChangeUp in the low nibble) --
            // see the same pattern in OnCharacterListFromDataServerAsync above. All the rest of the port
            // (RecalcCombatStats, DevilSquareManager, ViewportTicker, CharacterBalanceConfig, BuildCharSet)
            // expects the compact index 0-4, so it is decomposed here, at the single real entry point. FIXED:
            // before, the raw msg.Class was stored and ChangeUp stayed at 0 forever -- besides breaking CharSet
            // (see BuildCharSet), this made the Class==ClassFe/ClassMg comparisons never come out true for a
            // real character (only Class==0, Dark Wizard, matched by chance).
            Class = (byte)(msg.Class / 16),
            ChangeUp = (byte)(msg.Class % 16),
            Level = msg.Level,
            LevelUpPoint = msg.LevelUpPoint,
            Experience = msg.Experience,
            Money = msg.Money,
            Strength = msg.Strength,
            Dexterity = msg.Dexterity,
            Vitality = msg.Vitality,
            Energy = msg.Energy,
            Leadership = msg.Leadership,
            Life = msg.Life,
            MaxLife = msg.MaxLife,
            Mana = msg.Mana,
            MaxMana = msg.MaxMana,
            BP = msg.BP,
            MaxBP = msg.MaxBP,
            Inventory = msg.Inventory,
            Skill = msg.Skill,
            Quest = msg.Quest,
            Effect = msg.Effect,
            Map = msg.Map,
            X = msg.X,
            Y = msg.Y,
            TX = msg.X,
            TY = msg.Y,
            OldX = msg.X,
            OldY = msg.Y,
            Dir = msg.Dir,
            PKLevel = (byte)msg.PkLevel,
            PKCount = msg.PkCount,
            PKTime = msg.PkTime,
            CtlCode = msg.CtlCode,
            FruitAddPoint = msg.FruitAddPoint,
            FruitSubPoint = msg.FruitSubPoint,
            Reset = msg.Reset,
            MasterReset = msg.MasterReset,
            ChatLimitTime = msg.ChatLimitTime,
            BCCount = msg.BcCount,
            CCCount = msg.CcCount,
            DSCount = msg.DsCount,
        };

        player.DecodeInventory(); // Fase 3: blob crudo de DataServer -> Items[108]
        if (player.ChangeUp == 0 && (player.Quest == null || player.Quest.Length == 0 || player.Quest[0] == 0x00))
        {
            player.Quest = Enumerable.Repeat((byte)0xFF, 50).ToArray();
        }
        player.RebuildCharSet();
        player.RecalcCombatStats(_itemBalance, _characterBalance); // Fase 4 (balance real, ver PlayerObject.RecalcCombatStats)

        if (!_maps.IsValidMap(player.Map))
        {
            Log.Add(LogColor.Red, "[Protocol][{0}] Character '{1}' had an invalid map {2}, resetting to Lorencia (0, 125, 125)",
                session.Index, player.Name, player.Map);
            player.Map = 0;
            player.X = 125;
            player.Y = 125;
            player.TX = 125;
            player.TY = 125;
        }

        // Port of gObjSetCharacter (ObjectManager.cpp:2739-2745): a character entering the world with Life == 0
        // is sent to the death flow (OBJECT_DYING + DieRegen), which is what revives them. Without this the
        // character stayed alive but at zero forever: it is saved like that when they die and disconnect before
        // respawning, and on re-entering nothing recovered them -- IsDying is runtime state, so
        // RespawnDyingPlayersAsync did not even look at them. With 0 life monsters also ignore them completely
        // (the AI filters Life > 0), so the character was in limbo: it could not fight and nothing attacked it.
        if (player.Life == 0)
        {
            player.IsDying = true;
            player.DiedAt = DateTime.UtcNow;
            Log.Add(LogColor.Green, "[Protocol][{0}] '{1}' entered with 0 life -- reviving at the respawn point",
                session.Index, player.Name);
        }

        var map = _maps.GetMap(player.Map);
        map?.SetStandAttr(player.X, player.Y);

        session.Player = player;
        player.WorldEntered = true;
        _players.Add(player);

        await session.SendEncryptedAsync(WorldPacketBuilder.CharacterInfoSend(player), ct);
        await session.SendAsync(WorldPacketBuilder.NewCharacterInfoSend(player), ct);
        await session.SendAsync(WorldPacketBuilder.NewCharacterCalcSend(player), ct);

        // Port of DGCharacterInfoRecv (DSProtocol.cpp:414-499): the full inventory list (C4:F3:10) is sent as a
        // separate packet, after CHARACTER_INFO/NEW_CHARACTER_INFO.
        await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        await SendSkillListAsync(player, ct);

        // Port of DSProtocol.cpp:503 (DGCharacterInfoRecv): gQuest.GCQuestInfoSend(lpObj->Index) is sent
        // PROACTIVELY on entering the world, together with the rest of the initial burst (item/skill list).
        // Before, this port never sent it on world-enter -- only reactively when the player talked to a quest
        // NPC -- which left the client without the 50-byte state blob of ALL quests ahead of time.
        // SendQuestInfo=true is set here so that OnNpcTalkAsync/OnQuestInfoAsync replicate the real guard
        // (GCQuestInfoSend is a no-op after the first time per session).
        if (!player.SendQuestInfo)
        {
            await session.SendAsync(QuestPacketBuilder.QuestInfoSend(player.Quest!, _quests.Entries.Count), ct);
            player.SendQuestInfo = true;
        }

        await _dataServer.SendAsync(
            DataServerCharacterPacketBuilder.ConnectCharacter((ushort)session.Index, session.Account, player.Name), ct);

        Log.Add(LogColor.Blue, "[Protocol][{0}] '{1}' entered the world (Map={2} X={3} Y={4})",
            session.Index, player.Name, player.Map, player.X, player.Y);
    }

    public async Task SendSkillListAsync(PlayerObject player, CancellationToken ct)
    {
        var activeSkills = GetActiveSkills(player);
        await player.Session.SendAsync(SkillPacketBuilder.SkillListSend(activeSkills), ct);
    }

    private List<(byte slot, ushort skillIndex, byte level)> GetActiveSkills(PlayerObject player)
    {
        var result = new List<(byte slot, ushort skillIndex, byte level)>();
        var addedSkillIds = new HashSet<ushort>();

        // 1. Skills aprendidos del blob de 180 bytes (60 slots x 3 bytes)
        for (byte slot = 0; slot < 60; slot++)
        {
            int offset = slot * 3;
            if (offset + 2 >= player.Skill.Length) break;

            byte b0 = player.Skill[offset];
            byte b1 = player.Skill[offset + 1];
            byte b2 = player.Skill[offset + 2];

            if (b0 == 0xFF || (b0 == 0 && b2 == 0))
            {
                continue;
            }

            ushort skillId = (ushort)(b0 | (b2 << 8));
            if (skillId > 0 && addedSkillIds.Add(skillId))
            {
                result.Add((slot, skillId, b1));
            }
        }

        // 2. Skill default por clase si no estaba agregado
        ushort defaultClassSkill = player.Class switch
        {
            0 => 17, // Dark Wizard: Energy Ball
            4 => 60, // Dark Lord: Force
            _ => 0
        };

        if (defaultClassSkill > 0 && addedSkillIds.Add(defaultClassSkill))
        {
            result.Add(((byte)result.Count, defaultClassSkill, 0));
        }

        // 3. Skills de armas / escudos equipados (slots 0 y 1)
        for (int s = 0; s <= 1; s++)
        {
            var item = player.Items[s];
            if (!item.IsItem()) continue;

            var info = _itemBalance.Get(item.Index);
            if (info != null && info.Skill > 0 && (item.Option1 != 0 || item.Option2 != 0 || info.Skill > 0))
            {
                ushort wSkill = GetWeaponSkillId(item.Index, info.Skill);
                if (wSkill > 0 && addedSkillIds.Add(wSkill))
                {
                    result.Add(((byte)result.Count, wSkill, 0));
                }
            }
        }

        return result;
    }

    private static ushort GetWeaponSkillId(short itemIndex, int infoSkill)
    {
        if (infoSkill >= 18)
        {
            return (ushort)infoSkill;
        }

        if (infoSkill > 0)
        {
            int section = itemIndex / 32;
            return section switch
            {
                0 => 22, // Swords: Cyclone
                1 => 22, // Axes: Cyclone
                2 => 21, // Maces: Uppercut
                3 => 20, // Spears: Lunge
                4 => 24, // Bows/Crossbows: Triple Shot
                6 => 18, // Shields: Defense
                _ => (ushort)infoSkill
            };
        }

        return 0;
    }

    /// <summary> Simplified port of CGMoveRecv (Protocol.cpp:901-1076): validates against the map (±15 box,
    /// blocked tiles) and broadcasts PMSG_MOVE_SEND. The counter-based anti-speedhack of Protocol.cpp is not
    /// ported -- it comes off by default in the original package (CheckMoveHack=0 in GameServerInfo -
    /// Common.dat), so omitting it does not change the default behaviour. </summary>
    private async Task OnMoveAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || player.IsDying || player.Life == 0)
        {
            return;
        }

        var recv = MoveRecv.Parse(p);

        byte dir = (byte)(recv.Path[0] >> 4);
        int rawCount = recv.Path[0] & 0x0F;
        int pathCount = rawCount > 0 ? rawCount + 1 : 0; // puerto exacto de Protocol.cpp:972-983

        // RoadPathTable (Util.cpp) -- 8 directions, dx/dy per pair, order N,NE,E,SE,S,SW,W,NW. (plain arrays,
        // not Span/stackalloc: this method is async and stackalloc is not allowed there)
        int[] dx = { -1, 0, 1, 1, 1, 0, -1, -1 };
        int[] dy = { -1, -1, -1, 0, 1, 1, 1, 0 };

        // TX/TY start at the packet's anchor (x,y) -- the first direction nibble (path[0]>>4) is only the
        // orientation (Dir), it does NOT shift the position (exact port of Protocol.cpp:980-987: TX=x,TY=y on
        // input, only the loop from n=1 adds RoadPathTable offsets).
        int prevX = recv.X;
        int prevY = recv.Y;
        int tx = recv.X;
        int ty = recv.Y;

        for (int n = 1; n < pathCount; n++)
        {
            int nibble = (n % 2 == 0)
                ? recv.Path[(n + 1) / 2] & 0x0F
                : recv.Path[(n + 1) / 2] >> 4;

            nibble &= 7;
            tx = prevX + dx[nibble];
            ty = prevY + dy[nibble];
            prevX = tx;
            prevY = ty;
        }

        bool withinRange = Math.Abs(tx - player.X) <= 15 && Math.Abs(ty - player.Y) <= 15;
        var map = _maps.GetMap(player.Map);
        bool blocked = map == null || tx < 0 || ty < 0 || map.IsBlocked(tx, ty);

        if (!withinRange || blocked)
        {
            // Correction: re-send the client its current position (equivalent to gObjSetPosition).
            await session.SendAsync(WorldPacketBuilder.PositionSend(player.Index, player.X, player.Y), ct);
            return;
        }

        map!.DelStandAttr(player.OldX, player.OldY);

        // BUG fixed: here player.X/Y used to be stored as recv.X/Y (the packet's ANCHOR, that is, the START
        // position of the movement), instead of the ARRIVAL position (tx,ty). In the original, lpObj->X
        // (confirmed position) is NOT touched at all by CGMoveRecv (Protocol.cpp: 901-987 only writes
        // TX/TY/PathX/PathY/PathCount) -- lpObj->X is updated gradually, tile by tile, by a separate periodic
        // tick (CObjectManager::ObjectMoveProc, ObjectManager.cpp:419-482, with different speed for
        // diagonal/straight) as the character visually "walks". This port resolves the movement instantly
        // (without that tick in between) and NOWHERE else does the code advance X towards TX -- therefore X has
        // to be left at the ARRIVAL position right away, or else everything that uses player.X as "current
        // position" (this same range check on the next movement, the attack range in OnAttackAsync,
        // ViewportTicker's view range) ends up comparing against an old position from one movement back. That
        // "one movement behind" lag is exactly what caused that, after walking to attack a monster, the next
        // movement almost always fell outside the allowed 15-tile radius (measured against the old position)
        // and the server answered with PositionSend(player.X,player.Y) -- the old position, often very close to
        // where the character originally appeared -- which the real client showed as "it sends me back to my
        // spawn spot" when trying to approach to attack.
        player.X = (byte)tx;
        player.Y = (byte)ty;
        player.TX = (byte)tx;
        player.TY = (byte)ty;
        player.OldX = player.TX;
        player.OldY = player.TY;
        player.Dir = dir;

        map.SetStandAttr(player.TX, player.TY);

        // pMsg.dir = lpObj->Dir << 4 (Protocol.cpp:1059) -- va en el nibble alto.
        var movePacket = WorldPacketBuilder.MoveSend(player.Index, player.TX, player.TY, (byte)(dir << 4));

        await session.SendAsync(movePacket, ct);

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(movePacket, ct);
            }
        }
    }

    /// <summary>
    /// Puerto de CGTeleportRecv (Move.cpp:220-277) + gObjMoveGate (User.cpp:2045-2125) -- teletransporte por portal/puerta.
    /// </summary>
    private async Task OnTeleportAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = TeleportRecv.Parse(p);

        if (recv.Gate == 0) // DW Teleport Skill
        {
            player.X = recv.X;
            player.Y = recv.Y;
            player.TX = recv.X;
            player.TY = recv.Y;
            await session.SendEncryptedAsync(
                WorldPacketBuilder.TeleportSend(0, player.Map, player.X, player.Y, player.Dir), ct);
            return;
        }

        var gate = _gates.Get(recv.Gate);
        if (gate == null)
        {
            return;
        }

        if (gate.MinLevel != -1 && player.Level < gate.MinLevel)
        {
            return;
        }

        var target = (gate.TargetGate != 0) ? _gates.Get(gate.TargetGate) : gate;
        target ??= gate;

        byte targetX = (byte)(target.EndX > target.StartX ? Rng.Next(target.StartX, target.EndX + 1) : target.StartX);
        byte targetY = (byte)(target.EndY > target.StartY ? Rng.Next(target.StartY, target.EndY + 1) : target.StartY);
        byte targetMap = (byte)target.Map;
        byte targetDir = (byte)target.TargetDir;

        if (!_maps.IsValidMap(targetMap))
        {
            Log.Add(LogColor.Red, "[Protocol][{0}] Target map {1} is invalid for gate {2}",
                session.Index, targetMap, recv.Gate);
            return;
        }

        player.Map = targetMap;
        player.X = targetX;
        player.Y = targetY;
        player.TX = targetX;
        player.TY = targetY;
        player.Dir = targetDir;

        await session.SendEncryptedAsync(
            WorldPacketBuilder.TeleportSend(1, targetMap, targetX, targetY, targetDir), ct);

        var appearPacket = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });
        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.WorldEntered && other.Map == targetMap)
            {
                await other.Session.SendAsync(appearPacket, ct);
            }
        }

        await SaveCharacterAsync(player, ct);
    }

    private async Task OnTeleportMoveAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        var recv = TeleportMoveRecv.Parse(p);

        var move = _moves.Get(recv.MoveIndex);
        int gateNumber = move?.GateNumber ?? recv.MoveIndex;

        var gate = _gates.Get(gateNumber);
        if (gate == null) return;

        var targetGate = (gate.TargetGate != 0) ? _gates.Get(gate.TargetGate) ?? gate : gate;

        if (move != null)
        {
            if (player.Level < move.MinLevel)
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("Required level: {0}", move.MinLevel)), ct);
                return;
            }

            if (player.Money < move.RequireMoney)
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("Required zen: {0}", move.RequireMoney)), ct);
                return;
            }

            player.Money -= move.RequireMoney;
            await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        }

        player.Map = (byte)targetGate.Map;
        byte targetX = (byte)(targetGate.EndX > targetGate.StartX ? Rng.Next(targetGate.StartX, targetGate.EndX + 1) : targetGate.StartX);
        byte targetY = (byte)(targetGate.EndY > targetGate.StartY ? Rng.Next(targetGate.StartY, targetGate.EndY + 1) : targetGate.StartY);

        player.X = targetX;
        player.Y = targetY;
        player.TX = targetX;
        player.TY = targetY;
        player.Dir = (byte)targetGate.TargetDir;
        player.VisibleMonsters.Clear();

        await session.SendEncryptedAsync(
            WorldPacketBuilder.TeleportSend(1, player.Map, player.X, player.Y, player.Dir), ct);
    }

    /// <summary> Simplified port of CGItemMoveRecv (ItemManager.cpp:2853-3025) for the only container supported
    /// for now: Inventory→Inventory (moving from one slot to another, including equipping/ unequipping when the
    /// destination/source slot falls in the equipment range 0-11). Trade/Warehouse/ ChaosBox/PersonalShop
    /// (SourceFlag/TargetFlag other than 0) are left out of this pass of Phase 3 -- they are rejected with
    /// result=0xFF instead of being half-implemented. Two real bugs FIXED, found this session (cause of "items
    /// disappear when moved"): 1) <c>result</c> is NOT a generic boolean "0=fail/1=success" -- the original
    /// (<c>MoveItemToInventoryFromInventory</c>, ItemManager.cpp:1920-1978) returns <c>TargetFlag</c> (0 for
    /// Inventory) on success and <c>0xFF</c> on any failure (all the validation branches explicitly return
    /// <c>0xFF</c>, and <c>pMsg.result</c> starts at <c>0xFF</c> by default before trying anything). This port
    /// had the semantics inverted (0=fail, 1=success) -- with <c>0</c> as "fail" the real client probably
    /// interprets "success with TargetFlag=Inventory" (since it only checks <c>!= 0xFF</c>), accepting a move
    /// that the server actually rejected, a direct desync between what the client thinks happened and what the
    /// server really did. 2) <c>InventoryAddItem</c> (ItemManager.cpp:1052-1102) rejects the move with
    /// <c>0xFF</c> if the destination slot ALREADY has an item (<c>if(lpObj->Inventory[slot].IsItem() != 0)
    /// return 0xFF;</c>) -- there is NO exchange/swap at protocol level. <c>PMSG_ITEM_MOVE_SEND</c> only has
    /// room for ONE item (the one that ended up in <c>TargetSlot</c>); if the server swaps anyway (as this port
    /// did) the client never learns what happened to the item that was at the destination -- it loses it from
    /// the UI even though the server still tracks it in the source slot. Fixed: if the destination slot has an
    /// item, the whole move is rejected (like the original), with no swap. </summary>
    private async Task OnItemMoveAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = ItemMoveRecv.Parse(p);

        // Handling of item moves with Trade (Flag 1 = Trade Window)
        if (recv.SourceFlag == 0 && recv.TargetFlag == 1) // Inventario -> Trade
        {
            if (!player.InTrade || recv.SourceSlot >= Item.InventorySize || recv.TargetSlot >= 32)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.Items[recv.SourceSlot];
            if (!item.IsItem() || player.TradeItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.TradeItems[recv.TargetSlot] = item;
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(1, recv.TargetSlot, item), ct);

            player.TradeOk = false;
            if (_players.TryGet(player.TradeTargetIndex, out var target) && target.InTrade)
            {
                target.TradeOk = false;
                await target.Session.SendAsync(TradePacketBuilder.TradeItemAddSend(recv.TargetSlot, item), ct);
                await session.SendAsync(TradePacketBuilder.TradeOkButtonSend(0), ct);
                await target.Session.SendAsync(TradePacketBuilder.TradeOkButtonSend(2), ct);
            }
            return;
        }

        if (recv.SourceFlag == 1 && recv.TargetFlag == 0) // Trade -> Inventario
        {
            if (!player.InTrade || recv.SourceSlot >= 32 || recv.TargetSlot >= Item.InventorySize)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.TradeItems[recv.SourceSlot];
            if (!item.IsItem() || player.Items[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.SetItem(recv.TargetSlot, item);
            player.TradeItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, recv.TargetSlot, item), ct);

            player.TradeOk = false;
            if (_players.TryGet(player.TradeTargetIndex, out var target) && target.InTrade)
            {
                target.TradeOk = false;
                await target.Session.SendAsync(TradePacketBuilder.TradeItemDelSend(recv.SourceSlot), ct);
                await session.SendAsync(TradePacketBuilder.TradeOkButtonSend(0), ct);
                await target.Session.SendAsync(TradePacketBuilder.TradeOkButtonSend(2), ct);
            }
            return;
        }

        if (recv.SourceFlag == 1 && recv.TargetFlag == 1) // Trade -> Trade
        {
            if (!player.InTrade || recv.SourceSlot >= 32 || recv.TargetSlot >= 32)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.TradeItems[recv.SourceSlot];
            if (!item.IsItem() || player.TradeItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.TradeItems[recv.TargetSlot] = item;
            player.TradeItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(1, recv.TargetSlot, item), ct);

            player.TradeOk = false;
            if (_players.TryGet(player.TradeTargetIndex, out var target) && target.InTrade)
            {
                target.TradeOk = false;
                await target.Session.SendAsync(TradePacketBuilder.TradeItemDelSend(recv.SourceSlot), ct);
                await target.Session.SendAsync(TradePacketBuilder.TradeItemAddSend(recv.TargetSlot, item), ct);
                await session.SendAsync(TradePacketBuilder.TradeOkButtonSend(0), ct);
                await target.Session.SendAsync(TradePacketBuilder.TradeOkButtonSend(2), ct);
            }
            return;
        }

        // Handling of item moves with Warehouse (Flag 2 = Warehouse)
        if (recv.SourceFlag == 0 && recv.TargetFlag == 2) // Inventory -> Warehouse
        {
            if (!player.InWarehouse || player.WarehouseLock != 0 || recv.SourceSlot >= Item.InventorySize || recv.TargetSlot >= 120)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.Items[recv.SourceSlot];
            if (!item.IsItem() || player.WarehouseItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.WarehouseItems[recv.TargetSlot] = item;
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(2, recv.TargetSlot, item), ct);
            return;
        }

        if (recv.SourceFlag == 2 && recv.TargetFlag == 0) // Warehouse -> Inventory
        {
            if (!player.InWarehouse || player.WarehouseLock != 0 || recv.SourceSlot >= 120 || recv.TargetSlot >= Item.InventorySize)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.WarehouseItems[recv.SourceSlot];
            if (!item.IsItem() || player.Items[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.SetItem(recv.TargetSlot, item);
            player.WarehouseItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, recv.TargetSlot, item), ct);
            return;
        }

        if (recv.SourceFlag == 2 && recv.TargetFlag == 2) // Warehouse -> Warehouse
        {
            if (!player.InWarehouse || player.WarehouseLock != 0 || recv.SourceSlot >= 120 || recv.TargetSlot >= 120)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.WarehouseItems[recv.SourceSlot];
            if (!item.IsItem() || player.WarehouseItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.WarehouseItems[recv.TargetSlot] = item;
            player.WarehouseItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(2, recv.TargetSlot, item), ct);
            return;
        }

        // Item moves in the Chaos Box (Flag 3 = Chaos Box)
        if (recv.SourceFlag == 0 && recv.TargetFlag == 3) // Inventario -> Chaos Box
        {
            if (!player.InChaosBox || recv.SourceSlot >= Item.InventorySize || recv.TargetSlot >= 32)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.Items[recv.SourceSlot];
            if (!item.IsItem() || player.ChaosBoxItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.ChaosBoxItems[recv.TargetSlot] = item;
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(3, recv.TargetSlot, item), ct);
            return;
        }

        if (recv.SourceFlag == 3 && recv.TargetFlag == 0) // Chaos Box -> Inventario
        {
            if (!player.InChaosBox || recv.SourceSlot >= 32 || recv.TargetSlot >= Item.InventorySize)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.ChaosBoxItems[recv.SourceSlot];
            if (!item.IsItem() || player.Items[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.SetItem(recv.TargetSlot, item);
            player.ChaosBoxItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, recv.TargetSlot, item), ct);
            return;
        }

        if (recv.SourceFlag == 3 && recv.TargetFlag == 3) // Chaos Box -> Chaos Box
        {
            if (!player.InChaosBox || recv.SourceSlot >= 32 || recv.TargetSlot >= 32)
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            var item = player.ChaosBoxItems[recv.SourceSlot];
            if (!item.IsItem() || player.ChaosBoxItems[recv.TargetSlot].IsItem())
            {
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
                return;
            }

            player.ChaosBoxItems[recv.TargetSlot] = item;
            player.ChaosBoxItems[recv.SourceSlot] = Item.Empty();
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(3, recv.TargetSlot, item), ct);
            return;
        }

        bool ContainerOk(byte flag) => flag == 0; // solo Inventory

        bool slotsInRange = recv.SourceSlot < Item.InventorySize && recv.TargetSlot < Item.InventorySize;

        if (!ContainerOk(recv.SourceFlag) || !ContainerOk(recv.TargetFlag) || !slotsInRange)
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
            return;
        }

        var sourceItem = player.Items[recv.SourceSlot];

        if (!sourceItem.IsItem() || recv.SourceSlot == recv.TargetSlot)
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
            return;
        }

        var targetItem = player.Items[recv.TargetSlot];

        if (targetItem.IsItem())
        {
            // Puerto de InventoryAddItem: el slot destino ocupado rechaza el move entero, no hay
            // swap a nivel de protocolo (ver doc-comment de arriba).
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
            return;
        }

        // Port of CheckItemMoveToInventory (ItemManager.cpp:711-780): validates level, strength, agility,
        // vitality, energy, command, character class and slot compatibility before equipping.
        var info = _itemBalance.Get(sourceItem.Index);

        if (recv.TargetSlot < Item.InventoryWearSize && (info == null || !ItemCombatMath.CheckItemMoveToInventory(player, sourceItem, recv.TargetSlot, info, _itemBalance)))
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
            return;
        }

        player.SetItem(recv.TargetSlot, sourceItem);
        player.SetItem(recv.SourceSlot, Item.Empty());

        // result = TargetFlag (always 0 here, the only supported container is Inventory) -- exact port of
        // "return TargetFlag;" in MoveItemToInventoryFromInventory.
        await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, recv.TargetSlot, sourceItem), ct);

        bool touchedEquip = recv.SourceSlot < Item.InventoryWearSize || recv.TargetSlot < Item.InventoryWearSize;

        if (!touchedEquip)
        {
            return;
        }

        // Simplified port of CItemManager::UpdateInventoryViewport (ItemManager.cpp:1759-1779): rebuild the
        // CharSet, tell the client itself (ITEM_EQUIPMENT_SEND) and refresh the appearance for the current
        // observers by re-sending a VIEWPORT_PLAYER_APPEAR -- the original uses a dedicated viewport "change"
        // packet (gObjViewportListProtocolCreate) that has not been ported yet; reappearing with the new data
        // achieves the same visual result.
        player.RebuildCharSet();

        // Port of the CharacterCalcAttribute call the original triggers on equipping/unequipping
        // (ObjectManager.cpp, inside CGItemMoveRecv) -- the player's damage/defense have to reflect the change
        // immediately, not only on re-entering the world.
        player.RecalcCombatStats(_itemBalance, _characterBalance);
        await session.SendAsync(WorldPacketBuilder.NewCharacterCalcSend(player), ct);

        await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);
        await SendSkillListAsync(player, ct);

        var refreshPacket = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(refreshPacket, ct);
            }
        }
    }

    /// <summary> Port of CGItemRepairRecv (ItemManager.cpp:3373-3428) -- repair one item or repair all
    /// (slot=0xFF). </summary>
    private async Task OnItemRepairAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemRepairRecv.Parse(p);

        if (recv.Type == 1 && player.Level < 50)
        {
            await session.SendAsync(ItemPacketBuilder.ItemRepairSend(player.Money), ct);
            return;
        }

        bool anyRepaired = false;

        if (recv.Slot == 0xFF)
        {
            for (int n = 0; n < Item.InventorySize; n++)
            {
                var item = player.Items[n];
                if (!item.IsItem()) continue;

                var info = _itemBalance.Get(item.Index);
                if (info == null) continue;

                int cost = ItemCombatMath.GetItemRepairMoney(item, recv.Type, info);
                if (cost > 0 && player.Money >= (uint)cost)
                {
                    player.Money -= (uint)cost;
                    int maxDur = ItemCombatMath.GetItemDurability(item, info);
                    item.Durability = (byte)maxDur;
                    player.SetItem(n, item);
                    await session.SendAsync(ItemPacketBuilder.ItemDurSend((byte)n, (byte)maxDur, 0), ct);
                    anyRepaired = true;
                }
            }
        }
        else if (recv.Slot < Item.InventorySize)
        {
            var item = player.Items[recv.Slot];
            if (item.IsItem())
            {
                var info = _itemBalance.Get(item.Index);
                if (info != null)
                {
                    int cost = ItemCombatMath.GetItemRepairMoney(item, recv.Type, info);
                    if (cost > 0 && player.Money >= (uint)cost)
                    {
                        player.Money -= (uint)cost;
                        int maxDur = ItemCombatMath.GetItemDurability(item, info);
                        item.Durability = (byte)maxDur;
                        player.SetItem(recv.Slot, item);
                        await session.SendAsync(ItemPacketBuilder.ItemDurSend(recv.Slot, (byte)maxDur, 0), ct);
                        anyRepaired = true;
                    }
                }
            }
        }

        if (anyRepaired)
        {
            player.RecalcCombatStats(_itemBalance, _characterBalance);
            await SaveCharacterAsync(player, ct);
        }

        await session.SendAsync(ItemPacketBuilder.ItemRepairSend(player.Money), ct);
    }

    /// <summary> Simplified port of CGItemGetRecv (ItemManager.cpp:3289-3528) -- picking up an item from the
    /// ground. See World/GroundItem.cs for the data model. Documented simplifications (outside this pass): no
    /// quest-items/event-items/Muun (CItem::IsEventItem/IsMuunItem, unported systems), no full party money
    /// split (here whoever picks up the money keeps all of it -- the party money split,
    /// gServerInfo.m_PartyMoneyDistribute, is an unported server config), no stacking onto existing
    /// arrows/potions (InventoryInsertItemStack, result 0xFD -- this port has no stacking mechanic, each slot
    /// is a whole item). </summary>
    private async Task OnItemGetAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemGetRecv.Parse(p);
        var ground = _groundItems?.Get(player.Map, recv.GroundIndex);

        // Port of CMap::CheckItemGive (Map.cpp:363-419): range of 2 tiles on each axis (a 5x5 box, not
        // Euclidean distance) + loot-lock (owner/owner's party until LootLockUntil expires).
        bool inRange = ground != null
            && Math.Abs(ground.X - player.X) <= 2 && Math.Abs(ground.Y - player.Y) <= 2;

        bool lootAllowed = ground != null
            && (DateTime.UtcNow >= ground.LootLockUntil
                || ground.OwnerIndex == player.Index
                || (ground.OwnerPartyId != null && ground.OwnerPartyId == player.PartyNumber));

        if (ground == null || !inRange || !lootAllowed)
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemGetSend(0xFF, null, 0), ct);
            return;
        }

        if (ground.MoneyAmount is uint money)
        {
            _groundItems!.Remove(ground);
            player.Money = Math.Min(player.Money + money, 2_000_000_000u); // MAX_MONEY, ver ComputeGeneralPrice
            await session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
            return;
        }

        var info = _itemBalance.Get(ground.Item.Index);
        int width = info?.Width ?? 1;
        int height = info?.Height ?? 1;

        if (!TryFindEmptyInventoryRect(player, width, height, out int freeSlot))
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemGetSend(0xFF, null, 0), ct);
            return;
        }

        _groundItems!.Remove(ground);
        player.SetItem(freeSlot, ground.Item);

        await session.SendEncryptedAsync(ItemPacketBuilder.ItemGetSend((byte)freeSlot, ground.Item, ground.Index), ct);

        // Tell everyone who saw the ground item that it is gone (CMap::ItemGive removes it from everybody's
        // viewport sweep, not only the one who picked it up) -- the next ViewportTicker tick already does it by
        // itself (Live=false), but sending the destroy here also for the one who picked it up avoids waiting
        // until the next tick (~200ms) for their own client to remove it from the ground.
        await session.SendAsync(WorldPacketBuilder.ViewportItemDestroy(new[] { ground.Index }), ct);
    }

    /// <summary> Simplified port of CGItemDropRecv (ItemManager.cpp:3530-3718) -- dropping an inventory item on
    /// the ground. Documented simplifications (outside this pass): without the high-level anti-dupe/anti-scam
    /// rules (blocking +5/+6 non-wings, excellent, set, JewelOfHarmony --
    /// <c>IsExcItem/IsSetItem/IsJewelOfHarmonyItem</c> not relevant because this port does not generate those
    /// items yet), without lucky/periodic-item checks (those flags are always false/default in this port),
    /// without the special items with their own effect (Siege Summon, Life Stone, Lost Map, etc. --
    /// CGItemDropRecv chain of special cases, ItemManager.cpp:3620-3701). </summary>
    private async Task OnItemDropAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemDropRecv.Parse(p);

        if (recv.Slot >= Item.InventorySize)
        {
            await session.SendAsync(ItemPacketBuilder.ItemDropSend(0, recv.Slot), ct);
            return;
        }

        var item = player.Items[recv.Slot];

        if (!item.IsItem() || _groundItems == null)
        {
            await session.SendAsync(ItemPacketBuilder.ItemDropSend(0, recv.Slot), ct);
            return;
        }

        var map = _maps.GetMap(player.Map);

        if (map != null && map.IsBlocked(recv.X, recv.Y))
        {
            await session.SendAsync(ItemPacketBuilder.ItemDropSend(0, recv.Slot), ct); // pared / no-drop
            return;
        }

        var ground = _groundItems.Drop(
            player.Map, item, recv.X, recv.Y, player.Index, player.PartyNumber == -1 ? null : player.PartyNumber,
            GroundItemLifetime, GroundItemLootLock);

        if (ground == null)
        {
            await session.SendAsync(ItemPacketBuilder.ItemDropSend(0, recv.Slot), ct); // mapa lleno de items en el piso
            return;
        }

        player.SetItem(recv.Slot, Item.Empty());
        await session.SendAsync(ItemPacketBuilder.ItemDropSend(1, recv.Slot), ct);

        if (recv.Slot >= Item.InventoryWearSize)
        {
            return;
        }

        // Igual que OnItemMoveAsync: tirar un item EQUIPADO requiere refrescar CharSet/apariencia.
        player.RebuildCharSet();
        player.RecalcCombatStats(_itemBalance, _characterBalance);
        await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);

        var refreshPacket = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(refreshPacket, ct);
            }
        }
    }

    /// <summary> Port of CGItemUseRecv (ItemManager.cpp:3038-3179) -- use of potions, town portal scroll,
    /// antidote and jewels (Bless, Soul, Life). </summary>
    private async Task OnItemUseAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        var recv = ItemUseRecv.Parse(p);

        if (recv.SourceSlot >= Item.InventorySize) return;

        var item = player.Items[recv.SourceSlot];
        if (!item.IsItem()) return;

        int section = item.Index / 32;
        int sub = item.Index % 32;

        // 1. HP / MP potions (Category 14, sub 0..6)
        if (section == 14 && sub >= 0 && sub <= 6)
        {
            int hpPercent = sub switch
            {
                0 => 10, // Apple
                1 => 20, // Small Life Potion
                2 => 30, // Medium Life Potion
                3 => 40, // Large Life Potion
                _ => 0
            };

            int mpPercent = sub switch
            {
                4 => 20, // Small Mana Potion
                5 => 30, // Medium Mana Potion
                6 => 40, // Large Mana Potion
                _ => 0
            };

            if (hpPercent > 0)
            {
                uint restoreHp = (uint)(player.MaxLife * hpPercent / 100.0);
                player.Life = Math.Min(player.MaxLife, player.Life + restoreHp);
                await session.SendAsync(LifePacketBuilder.LifeSend(0xFF, (int)player.Life), ct);
            }

            if (mpPercent > 0)
            {
                uint restoreMp = (uint)(player.MaxMana * mpPercent / 100.0);
                player.Mana = Math.Min(player.MaxMana, player.Mana + restoreMp);
                await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);
            }

            // Consume potion
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            return;
        }

        // 2. Antidote (14,8)
        if (section == 14 && sub == 8)
        {
            // Limpia veneno/hielo
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            return;
        }

        // 3. Town Portal Scroll (14,10)
        if (section == 14 && sub == 10)
        {
            ushort gate = player.Map switch
            {
                0 or 1 => 17, // Lorencia / Dungeon -> Lorencia
                2 => 22,      // Devias -> Devias
                3 => 27,      // Noria -> Noria
                4 or 10 => 42, // Lost Tower / Icarus -> Lost Tower 1
                6 => 115,     // Arena -> Arena
                7 => 49,      // Atlans -> Atlans 1
                8 => 57,      // Tarkan -> Tarkan 1
                _ => 17
            };

            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);

            var gInfo = _gates.Get(gate);
            if (gInfo != null)
            {
                var target = gInfo.TargetGate != 0 ? _gates.Get(gInfo.TargetGate) ?? gInfo : gInfo;
                player.Map = (byte)target.Map;
                player.X = (byte)target.StartX;
                player.Y = (byte)target.StartY;
                player.TX = (byte)target.StartX;
                player.TY = (byte)target.StartY;
                await session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(1, player.Map, player.X, player.Y, (byte)target.TargetDir), ct);
            }
            return;
        }

        // 4. Jewel of Bless (14,13)
        if (section == 14 && sub == 13)
        {
            if (recv.TargetSlot >= Item.InventorySize) return;

            var targetItem = player.Items[recv.TargetSlot];
            if (!targetItem.IsItem()) return;

            if (targetItem.Level >= 6) return; // Bless solo sube hasta +6

            // Incrementar nivel
            targetItem.Level++;
            player.SetItem(recv.TargetSlot, targetItem);

            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            await session.SendAsync(ItemPacketBuilder.ItemModifySend(recv.TargetSlot, targetItem), ct);

            if (recv.TargetSlot <= 11)
            {
                player.RebuildCharSet();
                player.RecalcCombatStats(_itemBalance, _characterBalance);
                await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);
                await SendSkillListAsync(player, ct);
            }
            return;
        }

        // 5. Jewel of Soul (14,14)
        if (section == 14 && sub == 14)
        {
            if (recv.TargetSlot >= Item.InventorySize) return;

            var targetItem = player.Items[recv.TargetSlot];
            if (!targetItem.IsItem()) return;

            if (targetItem.Level >= 9) return; // Soul solo sube hasta +9

            int successRate = 50;
            if (targetItem.Option1 != 0 || targetItem.Option2 != 0)
            {
                successRate += 25; // 75% if it has the Luck option
            }

            bool success = Rng.Next(100) < successRate;

            if (success)
            {
                targetItem.Level++;
            }
            else
            {
                if (targetItem.Level >= 7)
                {
                    targetItem.Level = 0; // Si falla a +7 o +8, baja a +0
                }
                else
                {
                    targetItem.Level = (byte)Math.Max(0, targetItem.Level - 1); // Si falla a +6 o menos, baja 1 nivel
                }
            }

            player.SetItem(recv.TargetSlot, targetItem);

            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            await session.SendAsync(ItemPacketBuilder.ItemModifySend(recv.TargetSlot, targetItem), ct);

            if (recv.TargetSlot <= 11)
            {
                player.RebuildCharSet();
                player.RecalcCombatStats(_itemBalance, _characterBalance);
                await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);
                await SendSkillListAsync(player, ct);
            }
            return;
        }

        // 6. Jewel of Life (14,16)
        if (section == 14 && sub == 16)
        {
            if (recv.TargetSlot >= Item.InventorySize) return;

            var targetItem = player.Items[recv.TargetSlot];
            if (!targetItem.IsItem()) return;

            if (targetItem.Option3 >= 4) return; // Maximum +16 option (+4 x 4)

            bool success = Rng.Next(100) < 50;

            if (success)
            {
                targetItem.Option3++;
            }
            else
            {
                targetItem.Option3 = 0; // If it fails, the additional option is lost
            }

            player.SetItem(recv.TargetSlot, targetItem);

            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            await session.SendAsync(ItemPacketBuilder.ItemModifySend(recv.TargetSlot, targetItem), ct);

            if (recv.TargetSlot <= 11)
            {
                player.RebuildCharSet();
                player.RecalcCombatStats(_itemBalance, _characterBalance);
                await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);
            }
            return;
        }

        // 7. Pergaminos (Scrolls 15,0..18) y Orbes/Libros (Orbs 12,7..24) para aprender Habilidades/Magias
        if ((section == 15 && sub <= 18) || (section == 12 && sub >= 7 && sub <= 24))
        {
            short skillId = GetSkillNumberFromItem((short)item.Index, item.Level);
            if (skillId <= 0) return;

            // Verificar si el jugador ya conoce el skill
            if (player.HasSkill(skillId))
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("You have already learned this skill.")), ct);
                return;
            }

            // Check level, energy, command and class requirements
            var skillInfo = _skills?.Get((ushort)skillId);
            if (skillInfo != null)
            {
                if (!skillInfo.CanUse(player.Class, player.ChangeUp) ||
                    player.Level < skillInfo.RequireLevel ||
                    player.Energy < skillInfo.RequireEnergy ||
                    player.Leadership < skillInfo.RequireLeadership)
                {
                    await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("You do not meet the requirements to learn this skill.")), ct);
                    return;
                }
            }

            int learnedSlot = player.AddSkill(skillId, item.Level);
            if (learnedSlot >= 0)
            {
                // Consumir el pergamino/orbe
                player.SetItem(recv.SourceSlot, Item.Empty());
                await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);

                // Notify the client of the skill being added to the bar
                await session.SendAsync(SkillPacketBuilder.SkillAddSend((byte)learnedSlot, (ushort)skillId, item.Level), ct);
                await SaveCharacterAsync(player, ct);
                Log.Add(LogColor.Blue, "[Skill][{0}] '{1}' learned skill #{2} (slot {3}) from item ({4},{5})",
                    player.Index, player.Name, skillId, learnedSlot, section, sub);
            }
            return;
        }
    }

    private static short GetSkillNumberFromItem(short itemIndex, byte level)
    {
        int section = itemIndex / 32;
        int sub = itemIndex % 32;

        if (section == 15 && sub <= 15)
        {
            return (short)(sub + 1);
        }

        if (section == 15 && sub == 16) return 38; // Decay
        if (section == 15 && sub == 17) return 39; // Ice Storm
        if (section == 15 && sub == 18) return 40; // Nova

        if (section == 12)
        {
            return sub switch
            {
                7 => 41,  // Twisting Slash
                8 => 26,  // Heal
                9 => 27,  // Greater Defense
                10 => 28, // Greater Damage
                11 => (short)(20 + level), // Summon Goblin/Golem/Gargoyle
                12 => 42, // Rageful Blow
                13 => 47, // Impale
                14 => 48, // Greater Life (Swell Life)
                16 => 45, // Fire Slash
                17 => 52, // Penetration
                18 => 51, // Ice Arrow
                19 => 43, // Death Stab
                21 => 61, // Fire Burst
                22 => 62, // Summon Party
                23 => 63, // Critical Damage
                24 => 64, // Electric Spark
                _ => -1
            };
        }

        return -1;
    }

    private async Task OnTradeRequestAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || player.InTrade) return;

        var recv = TradeRequestRecv.Parse(p);
        if (!_players.TryGet(recv.TargetIndex, out var target) || !target.WorldEntered || target.InTrade)
        {
            await session.SendAsync(TradePacketBuilder.TradeResponseSend(0, string.Empty), ct);
            return;
        }

        if (Math.Abs(player.X - target.X) > 3 || Math.Abs(player.Y - target.Y) > 3)
        {
            await session.SendAsync(TradePacketBuilder.TradeResponseSend(0, target.Name), ct);
            return;
        }

        player.InTrade = true;
        player.TradeTargetIndex = target.Index;
        player.ClearTrade();

        target.InTrade = true;
        target.TradeTargetIndex = player.Index;
        target.ClearTrade();

        await target.Session.SendEncryptedAsync(TradePacketBuilder.TradeRequestSend(player.Name), ct);
    }

    private async Task OnTradeResponseAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InTrade) return;

        var recv = TradeResponseRecv.Parse(p);

        if (!_players.TryGet(player.TradeTargetIndex, out var target) || !target.WorldEntered)
        {
            player.ClearTrade();
            await session.SendAsync(TradePacketBuilder.TradeResponseSend(0, string.Empty), ct);
            return;
        }

        if (recv.Response == 0)
        {
            player.ClearTrade();
            target.ClearTrade();
            await target.Session.SendAsync(TradePacketBuilder.TradeResponseSend(0, player.Name), ct);
            await session.SendAsync(TradePacketBuilder.TradeResponseSend(0, target.Name), ct);
            return;
        }

        player.TradeOk = false;
        target.TradeOk = false;

        await session.SendAsync(TradePacketBuilder.TradeResponseSend(1, target.Name, target.Level, 0), ct);
        await target.Session.SendAsync(TradePacketBuilder.TradeResponseSend(1, player.Name, player.Level, 0), ct);
    }

    private async Task OnTradeMoneyAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InTrade) return;

        if (!_players.TryGet(player.TradeTargetIndex, out var target) || !target.WorldEntered)
        {
            await CancelTradeAsync(player, null, ct);
            return;
        }

        var recv = TradeMoneyRecv.Parse(p);
        if (recv.Money > 1_000_000_000 || player.Money < recv.Money) return;

        player.TradeMoney = recv.Money;
        player.TradeOk = false;
        target.TradeOk = false;

        await session.SendAsync(TradePacketBuilder.TradeResultSend(1), ct);
        await session.SendAsync(TradePacketBuilder.TradeOkButtonSend(0), ct);
        await target.Session.SendAsync(TradePacketBuilder.TradeOkButtonSend(2), ct);
        await target.Session.SendAsync(TradePacketBuilder.TradeMoneySend(recv.Money), ct);
    }

    private async Task OnTradeOkAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InTrade) return;

        if (!_players.TryGet(player.TradeTargetIndex, out var target) || !target.WorldEntered)
        {
            await CancelTradeAsync(player, null, ct);
            return;
        }

        var recv = TradeOkRecv.Parse(p);
        player.TradeOk = recv.Flag != 0;

        await target.Session.SendAsync(TradePacketBuilder.TradeOkButtonSend((byte)(player.TradeOk ? 1 : 0)), ct);

        if (!player.TradeOk || !target.TradeOk) return;

        if ((long)player.Money - player.TradeMoney + target.TradeMoney > 2_000_000_000 ||
            (long)target.Money - target.TradeMoney + player.TradeMoney > 2_000_000_000)
        {
            await CancelTradeAsync(player, target, ct, resultFlag: 5);
            return;
        }

        player.Money = (uint)((long)player.Money - player.TradeMoney + target.TradeMoney);
        target.Money = (uint)((long)target.Money - target.TradeMoney + player.TradeMoney);

        bool FitAndPlaceItems(PlayerObject source, PlayerObject dest)
        {
            for (int t = 0; t < source.TradeItems.Length; t++)
            {
                var tradeItem = source.TradeItems[t];
                if (!tradeItem.IsItem()) continue;

                int freeSlot = -1;
                for (int slot = 12; slot < Item.InventorySize; slot++)
                {
                    if (!dest.Items[slot].IsItem())
                    {
                        freeSlot = slot;
                        break;
                    }
                }

                if (freeSlot == -1) return false;

                dest.SetItem(freeSlot, tradeItem);
                source.TradeItems[t] = Item.Empty();
            }
            return true;
        }

        if (!FitAndPlaceItems(player, target) || !FitAndPlaceItems(target, player))
        {
            await CancelTradeAsync(player, target, ct, resultFlag: 2);
            return;
        }

        player.ClearTrade();
        target.ClearTrade();

        await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        await SaveCharacterAsync(player, ct);
        await session.SendAsync(TradePacketBuilder.TradeResultSend(1), ct);

        await target.Session.SendAsync(ItemPacketBuilder.MoneySend(target.Money), ct);
        await target.Session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(target), ct);
        await SaveCharacterAsync(target, ct);
        await target.Session.SendAsync(TradePacketBuilder.TradeResultSend(1), ct);
    }

    private async Task OnTradeCancelAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InTrade) return;

        _players.TryGet(player.TradeTargetIndex, out var target);
        await CancelTradeAsync(player, target, ct);
    }

    private async Task CancelTradeAsync(PlayerObject player, PlayerObject? target, CancellationToken ct, byte resultFlag = 0)
    {
        void ReturnTradeItems(PlayerObject p)
        {
            for (int t = 0; t < p.TradeItems.Length; t++)
            {
                var tradeItem = p.TradeItems[t];
                if (!tradeItem.IsItem()) continue;

                for (int slot = 12; slot < Item.InventorySize; slot++)
                {
                    if (!p.Items[slot].IsItem())
                    {
                        p.SetItem(slot, tradeItem);
                        break;
                    }
                }
            }
            p.ClearTrade();
        }

        ReturnTradeItems(player);
        await player.Session.SendAsync(TradePacketBuilder.TradeResultSend(resultFlag), ct);
        await player.Session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);

        if (target != null && target.InTrade)
        {
            ReturnTradeItems(target);
            await target.Session.SendAsync(TradePacketBuilder.TradeResultSend(resultFlag), ct);
            await target.Session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(target), ct);
        }
    }

    // ---------------------------------------------------------------- Fase 4: combate (primera pasada)

    private const int ClassFe = 2; // DB_CLASS_FE/16 -- ver nota de Class/ChangeUp en PlayerObject.RebuildCharSet

    /// <summary> Simplified port of CAttack::CGAttackRecv + CAttack::Attack (Attack.cpp:44-581) -- ONLY basic
    /// player melee attack against a monster (no skills, no PvP, no combo). Damage formula faithful to this
    /// phase's research (miss/dodge, defense, per-level damage floor), except for two explicitly documented
    /// simplifications: 1) The player's PhysiDamageMin/Max/Defense/AttackSuccessRate/DefenseSuccessRate come
    /// from PlayerObject.RecalcCombatStats() -- Phase 4 second pass: it is already the real port of
    /// CharacterCalcAttribute + the real Item.txt balance (the equipped weapon/armor DO matter), see the
    /// comment of that function for the simplifications that remain (critical/excellent/ set-item, speed,
    /// magic, PvP). 2) No critical/excellent (they depend on %s that come from
    /// ItemOption.txt/SetItemOption.txt, not ported yet). Monster-attacks-player and the chase/patrol AI are
    /// left for the next pass of this phase (confirmed safe at protocol level to leave monsters static for now,
    /// see the class comment in World/Monster.cs). </summary> <summary>Port of CGActionRecv
    /// (Protocol.cpp:611-666) -- pose/emote/sit. Minimal validation like the original (only "is connected", no
    /// distance/state check or rate limit); it persists dir/ActionNumber on the player and forwards as is to
    /// whoever has them in their viewport. Unlike the original, here it is also sent back to the sender itself
    /// (the original does NOT echo to whoever originated it, it assumes the client plays its own animation.
    /// Port of CGActionRecv (Protocol.cpp:611-666) -- pose/emote/sit. Exact port of GCActionSend
    /// (Protocol.cpp:1488-1507): broadcasts PMSG_ACTION_SEND (0x18) via MsgSendV2 ONLY to the observers in the
    /// sender's viewport, NEVER to the sender itself (sending 0x18 back to the client itself makes main.exe
    /// interrupt its local animation/movement and reset the position).
    private async Task OnActionAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ActionRecv.Parse(p);
        player.Dir = recv.Dir;
        player.ActionNumber = recv.Action;

        var actionPacket = CombatPacketBuilder.ActionSend(player.Index, recv.Dir, recv.Action, recv.TargetIndex);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != player.Index && viewer.VisibleTo.Contains(player.Index))
            {
                await viewer.Session.SendAsync(actionPacket, ct);
            }
        }
    }

    /// <summary>Port of CQuest::CGQuestInfoRecv (Quest.cpp:221-231) -&gt; GCQuestInfoSend (Quest.cpp:397-417):
    /// no-op if the full blob was already sent this session (see <see cref="PlayerObject.SendQuestInfo"/>,
    /// normally already true thanks to the proactive world-enter push in
    /// <c>OnCharacterInfoFromDataServerAsync</c>, DSProtocol.cpp:503).</summary>
    private async Task OnQuestInfoAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || player.SendQuestInfo)
        {
            return;
        }

        await session.SendAsync(QuestPacketBuilder.QuestInfoSend(player.Quest, _quests.Entries.Count), ct);
        player.SendQuestInfo = true;
    }

    /// <summary>Port of CGPetItemInfoRecv (Protocol.cpp:797-819) -- only the flag==0 branch (Inventory);
    /// Warehouse/Trade/ChaosBox (flag 1+) are not ported yet and, like the original on any failed validation,
    /// simply do not answer (confirmed in the earlier research that the real client tolerates the absence of an
    /// answer here, unlike QuestInfo).</summary>
    private async Task OnPetItemInfoAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = PetItemInfoRecv.Parse(p);

        if (recv.Type > 1 || recv.Flag != 0 || recv.Slot >= Item.InventorySize)
        {
            return;
        }

        await session.SendAsync(QuestPacketBuilder.PetItemInfoSend(recv.Type, recv.Flag, recv.Slot), ct);
    }

    /// <summary>Port of CNpcTalk::CGNpcTalkRecv (NpcTalk.cpp:1416-1524) -- ONLY the "shop" branch (the
    /// <c>NpcTalk()</c> result is always 0 in this port, since there are no quest/special-class NPCs --
    /// Trainer/Charon/GuildMaster/etc -- implemented, see README). Deliberate simplifications compared to the
    /// original: <list type="bullet"> <item>The original's real distance check is
    /// <c>gObjCalcDistance(lpObj,lpObj)</c> -- literally the distance of the player TO THEMSELVES, which is
    /// always 0 and therefore ALWAYS passes. It is a bug in the original (probably meant to be
    /// <c>gObjCalcDistance(lpObj,lpNpc)</c>); it is replicated as is (no real distance check) to keep
    /// behavioural fidelity.</item> <item>PKLevel/AccountLevel/GameMasterLevel are not tracked in this port
    /// yet, so the original's <c>m_PKLimitShop</c>/<c>CheckShopGameMasterLevel</c>/<c>CheckShopAccountLevel</c>
    /// checks are omitted (equivalent to having them always permissive, the default of a server with no
    /// restrictions configured).</item> <item>The original's
    /// <c>GCShopItemPriceSendByIndex</c>/<c>GCTaxInfoSend</c> are NOT ported because they are no-ops in this
    /// real build: <c>GAMESERVER_SHOP==0</c> in stdafx.h (confirmed in the source code) makes
    /// <c>GCShopItemPriceSend</c>/<c>GCShopItemCoinPriceSend</c> compile empty (the whole system is about shops
    /// with an alternative "Coin" currency, not used in this build), and <c>GCTaxInfoSend</c> is pure Castle
    /// Siege tax UI (unported system). The real client computes the price to show in the shop window with its
    /// own copy of Item.bmd, it does not need the server to send it for normal Zen shops.</item>
    /// </list></summary>
    private async Task OnNpcTalkAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        player.TargetShopNumber = null;

        var recv = NpcTalkRecv.Parse(p);

        if (!_monsters.TryGet(recv.NpcIndex, out var npc))
        {
            Log.Add(LogColor.Red, "[Quest][{0}] OnNpcTalk: NpcIndex={1} not found in _monsters (registry/viewport out of sync?)", player.Name, recv.NpcIndex);
            return;
        }

        if (npc.Map != player.Map)
        {
            Log.Add(LogColor.Red, "[Quest][{0}] OnNpcTalk: NPC class={1} map={2} != player map={3}", player.Name, npc.MonsterClass, npc.Map, player.Map);
            return;
        }

        // EXACT port of CQuest::NpcTalk (Quest.cpp:195-219), called from CNpcTalk::NpcTalk BEFORE the
        // switch(lpNpc->Class) of special cases (NpcTalk.cpp:55-58) -- it replaces an earlier version that
        // hardcoded "Sebina"(235)/"Marlon"(229) with a fixed minimum level (150) and an ad-hoc 2-quest model,
        // instead of reading the real requirements of Quest.txt. That old version sent
        // QuestResultSend(...,0xFF,...) for ANY player who did not match exactly level 150 or the expected
        // class -- the real client interprets that packet as "conversation over" (reported bug: "Conversation
        // is over" when talking to the Priest). The original sends NO packet when there is no quest available
        // for that NPC/player (GetInfoByIndex returns null -> NpcTalk returns false -> it continues to the
        // switch of special cases below, and if
        var questMatch = _quests.NpcTalk(npc.MonsterClass, player.Quest!, player.Level, player.Class, player.ChangeUp);

        Log.Add(LogColor.Blue, "[Quest][{0}] OnNpcTalk: npc.Class={1} level={2} class={3} changeUp={4} => questMatch={5}",
            player.Name, npc.MonsterClass, player.Level, player.Class, player.ChangeUp,
            questMatch == null ? "null" : $"index={questMatch.Index} state={questMatch.CurrentState}");

        if (questMatch != null)
        {
            // EXACT port of CQuest::GCQuestStateSend (Quest.cpp:419-432): it always calls GCQuestInfoSend
            // first, but that function is a no-op if it was already sent once this session (see
            // PlayerObject.SendQuestInfo -- normally already sent proactively on entering the world,
            // DSProtocol.cpp:503). The C1:A1 state packet is ALWAYS sent.
            if (!player.SendQuestInfo)
            {
                await session.SendAsync(QuestPacketBuilder.QuestInfoSend(player.Quest!, _quests.Entries.Count), ct);
                player.SendQuestInfo = true;
            }

            await session.SendAsync(QuestPacketBuilder.QuestStateSend((byte)questMatch.Index, QuestTable.GetQuestState(player.Quest!, questMatch.Index)), ct);
            return;
        }

        if (npc.MonsterClass == 232) // Messenger of Archangel (Blood Castle NPC en Devias)
        {
            await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(6), ct);
            return;
        }

        if (npc.MonsterClass == 236) // Golden Archer (Lorencia)
        {
            var w = new PacketWriter();
            w.WriteByte(0);
            w.WriteUInt32(0);
            w.WriteBytes(new byte[5], 5);
            await session.SendAsync(PacketBuilder.BuildC1(0x94, w.ToArray()), ct);
            return;
        }

        if (npc.MonsterClass == 237) // Charon (Devil Square NPC en Noria)
        {
            await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(2), ct);
            return;
        }

        if (npc.MonsterClass == 238) // Chaos Goblin (Chaos Machine NPC en Noria)
        {
            // WATCH OUT: here ClearChaosBox() used to be called, which DELETES whatever was left in the box.
            // Since moving an item from the inventory to the box takes it out of the inventory
            // (MoveItemToChaosBoxFromInventory does InventoryDelItem in the original, and this port replicates
            // it), any item left inside --for example on closing the window with the generic 0x31, which was
            // not ported-- was lost when reopening the machine. The original's NpcChaosGoblin does not clear
            // anything on opening: the items stay in the box. Here they are returned to the inventory instead
            // of being left in the box because this port does not persist the Chaos Box in the DataServer, so
            // leaving them inside would lose them anyway on disconnecting.
            await ReturnChaosBoxItemsToInventoryAsync(session, player, ct);

            player.InChaosBox = true;
            await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(3), ct);
            await session.SendAsync(ChaosBoxPacketBuilder.ChaosBoxItemListSend(player.ChaosBoxItems), ct);
            return;
        }

        if (npc.MonsterClass == 240) // Vault Keeper (Warehouse NPC)
        {
            // Port of the NpcWarehouse guard (NpcTalk.cpp:189-198): the warehouse does not open if there are
            // items in the Chaos Box, so that the same item cannot be "in two places" at once.
            if (player.ChaosBoxItems.Any(i => i.IsItem()))
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("Take the items out of the Chaos Machine first.")), ct);
                return;
            }

            player.InWarehouse = true;
            await _dataServer.SendAsync(DataServerCharacterPacketBuilder.WarehouseInfoRequest((ushort)player.Index, player.Account), ct);
            return;
        }

        if (npc.MonsterClass == 241) // Guild Master / Leader (Devias)
        {
            await OnGuildMasterOpenAsync(session, new byte[] { 0xC1, 0x04, 0x54, 0x00 }, ct);
            return;
        }

        if (npc.MonsterClass == 226 || npc.MonsterClass == 234 || npc.MonsterClass == 257) // Trainer / Pet Trainer (Lorencia)
        {
            await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(7), ct);
            return;
        }

        if (npc.ShopNumber == null || _shops == null)
        {
            return;
        }

        var shop = _shops.Get(npc.ShopNumber.Value);

        if (shop == null || shop.ItemCount == 0)
        {
            return;
        }

        player.TargetShopNumber = npc.ShopNumber;

        await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(0), ct);
        await session.SendAsync(ShopPacketBuilder.ShopItemListSend(shop), ct);
    }

    /// <summary>Port of CNpcTalk::CGNpcTalkCloseRecv (NpcTalk.cpp:325-360) -- releases the player's interface
    /// state when the NPC window is closed. The original sends no answer. <para>The Chaos Box needs an extra
    /// step that used to be missing: moving an item from the inventory to the box TAKES IT OUT of the inventory
    /// (the original does <c>InventoryDelItem</c> in <c>MoveItemToChaosBoxFromInventory</c>, and this port
    /// replicates it), so if the player closed the window with something inside the item stayed only in
    /// <c>ChaosBoxItems</c> -- and was deleted when talking to the Chaos Goblin again. Now it is returned to
    /// the inventory. The original does not do this (it has no case for INTERFACE_CHAOS_BOX here, the items
    /// stay in the box), but there the box is persisted and here it is not, so leaving them inside would lose
    /// them anyway on disconnecting.</para></summary>
    private async Task OnNpcTalkCloseAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        player.TargetShopNumber = null;

        if (player.InChaosBox)
        {
            await ReturnChaosBoxItemsToInventoryAsync(session, player, ct);
            player.InChaosBox = false;
        }

        if (player.InWarehouse)
        {
            await _dataServer.SendAsync(DataServerCharacterPacketBuilder.WarehouseSaveRequest((ushort)player.Index, player.Account, player), ct);
            player.InWarehouse = false;
        }
    }

    public async Task OnWarehouseFromDataServerAsync(byte[] packet, CancellationToken ct)
    {
        ushort index = (ushort)((packet[5] << 8) | packet[6]);
        if (!_players.TryGet(index, out var player) || !player.WorldEntered) return;

        player.InWarehouse = true;

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

        player.WarehouseMoney = money;
        player.WarehousePassword = password;

        for (int slot = 0; slot < 120; slot++)
        {
            var info = items.AsSpan(slot * 16, 16);
            player.WarehouseItems[slot] = Item.FromDbBytes(info);
        }

        if (password != 0)
        {
            player.WarehouseLock = 1;
            await player.Session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(1), ct);
        }
        else
        {
            player.WarehouseLock = 0;
            await player.Session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(0), ct);
            await player.Session.SendEncryptedC4Async(WarehousePacketBuilder.WarehouseListSend(player), ct);
            await player.Session.SendAsync(WarehousePacketBuilder.WarehouseMoneySend(1, player.Money, player.WarehouseMoney), ct);
        }
    }

    private async Task OnWarehouseMoneyAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InWarehouse || player.WarehouseLock != 0) return;

        var recv = WarehouseMoneyRecv.Parse(p);

        if (recv.Type == 0)
        {
            if (player.Money < recv.Money || (long)player.WarehouseMoney + recv.Money > 2_000_000_000) return;

            player.Money -= recv.Money;
            player.WarehouseMoney += recv.Money;
        }
        else if (recv.Type == 1)
        {
            if (player.WarehouseMoney < recv.Money || (long)player.Money + recv.Money > 2_000_000_000) return;

            player.WarehouseMoney -= recv.Money;
            player.Money += recv.Money;
        }

        await session.SendAsync(WarehousePacketBuilder.WarehouseMoneySend(1, player.Money, player.WarehouseMoney), ct);
        await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
    }

    private async Task OnWarehousePasswordAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InWarehouse) return;

        var recv = WarehousePasswordRecv.Parse(p);

        if (recv.Type == 0)
        {
            if (recv.Password == player.WarehousePassword)
            {
                player.WarehouseLock = 0;
                await session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(12), ct);
                await session.SendEncryptedC4Async(WarehousePacketBuilder.WarehouseListSend(player), ct);
                await session.SendAsync(WarehousePacketBuilder.WarehouseMoneySend(1, player.Money, player.WarehouseMoney), ct);
            }
            else
            {
                await session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(10), ct);
            }
        }
        else if (recv.Type == 1)
        {
            player.WarehousePassword = recv.Password;
            player.WarehouseLock = 1;
            await session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(1), ct);
        }
        else if (recv.Type == 2)
        {
            player.WarehousePassword = 0;
            player.WarehouseLock = 0;
            await session.SendAsync(WarehousePacketBuilder.WarehouseStateSend(0), ct);
        }
    }

    private async Task OnWarehouseCloseAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InWarehouse) return;

        await _dataServer.SendAsync(DataServerCharacterPacketBuilder.WarehouseSaveRequest((ushort)player.Index, player.Account, player), ct);
        player.InWarehouse = false;
    }


    /// <summary>Port of CItemManager::CGItemBuyRecv (ItemManager.cpp:4309-4519) -- ONLY the normal money (Zen)
    /// branch: the original also supports buying with an alternative "Coin" (type 1-3) and two special items
    /// with their own logic (Dark Horse/Dark Reaven "instant" via GDCreateItemSend, and gacha "Random Item"
    /// items via CMossMerchant) -- neither system is ported (outside the scope of this pass, see README), so
    /// those indices are bought like any normal item (they go to the inventory instead of being generated
    /// separately). <c>InventoryInsertItemStack</c> (stacking onto potions/arrows already in the inventory) is
    /// not ported either -- it always looks for a new empty slot, as if the player had nothing stackable yet.
    /// The Castle Siege tax (<c>tax</c> in the original) is always 0 here (unported system, equivalent to
    /// having the castle without an owner).</summary>
    private async Task OnItemBuyAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemBuyRecv.Parse(p);

        if (player.TargetShopNumber == null || _shops == null)
        {
            await session.SendAsync(ShopPacketBuilder.ItemBuySend(0xFF, Item.Empty()), ct);
            return;
        }

        var shop = _shops.Get(player.TargetShopNumber.Value);

        if (shop == null || recv.Slot >= ShopInfo.Size || shop.Slots[recv.Slot] is not { } shopItem || !shopItem.IsItem())
        {
            await session.SendAsync(ShopPacketBuilder.ItemBuySend(0xFF, Item.Empty()), ct);
            return;
        }

        var info = _itemBalance.Get(shopItem.Index);
        uint price = ComputeShopBuyPrice(shopItem, info);

        if (player.Money < price)
        {
            await session.SendAsync(ShopPacketBuilder.ItemBuySend(0xFF, Item.Empty()), ct);
            return;
        }

        int width = Math.Max(info?.Width ?? 1, 1);
        int height = Math.Max(info?.Height ?? 1, 1);

        if (!TryFindEmptyInventoryRect(player, width, height, out int freeSlot))
        {
            await session.SendAsync(ShopPacketBuilder.ItemBuySend(0xFF, Item.Empty()), ct);
            return;
        }

        var purchased = new Item
        {
            Index = shopItem.Index,
            Level = shopItem.Level,
            Durability = shopItem.Durability,
            Option1 = shopItem.Option1,
            Option2 = shopItem.Option2,
            Option3 = shopItem.Option3,
            NewOption = shopItem.NewOption,
            SetOption = shopItem.SetOption,
        };

        player.SetItem(freeSlot, purchased);
        player.Money -= price;

        await session.SendAsync(ShopPacketBuilder.ItemBuySend((byte)freeSlot, purchased), ct);
        await session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
    }

    /// <summary>Port of CItemManager::CGItemSellRecv (ItemManager.cpp:4521-4623) -- unlike buying, the slot is
    /// from the player's OWN INVENTORY (full range 0-107, including worn equipment --
    /// <c>INVENTORY_FULL_RANGE</c> in the original, not <c>INVENTORY_RANGE</c>) and does not depend on which
    /// shop is open, only that ONE shop is open. <c>gItemMove.CheckItemMoveAllowSell</c> ("not sellable" flag
    /// of some special items) and <c>gServerInfo.m_TradeItemBlockSell</c> are not ported (equivalent to
    /// allowing any item to be sold, the default with no restrictions configured). Price: <see
    /// cref="ComputeShopSellPrice"/>, port of CItem::Value().</summary>
    private async Task OnItemSellAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemSellRecv.Parse(p);

        if (player.TargetShopNumber == null)
        {
            await session.SendAsync(ShopPacketBuilder.ItemSellSend(0, 0), ct);
            return;
        }

        if (recv.Slot >= Item.InventorySize)
        {
            await session.SendAsync(ShopPacketBuilder.ItemSellSend(0, 0), ct);
            return;
        }

        var item = player.Items[recv.Slot];

        if (!item.IsItem())
        {
            await session.SendAsync(ShopPacketBuilder.ItemSellSend(0, 0), ct);
            return;
        }

        var info = _itemBalance.Get(item.Index);
        uint sellPrice = ComputeShopSellPrice(item, info);

        const uint maxMoney = 2_000_000_000; // MAX_MONEY (User.h:24)
        player.Money = sellPrice > maxMoney - player.Money ? maxMoney : player.Money + sellPrice;

        player.SetItem(recv.Slot, Item.Empty());

        await session.SendAsync(ShopPacketBuilder.ItemSellSend(1, player.Money), ct);

        // Port of CItemManager::UpdateInventoryViewport (ItemManager.cpp:2062-2084) -- the original ONLY does
        // something here if the sold slot was WORN equipment (INVENTORY_WEAR_RANGE); for the common case
        // (selling from the backpack) it sends no extra packet beyond the 0x33 above -- the real client clears
        // its own inventory slot from that same result=1 (it remembers which slot it asked to sell), with no
        // need for a server echo.
        if (recv.Slot < Item.InventoryWearSize)
        {
            player.RebuildCharSet();
            player.RecalcCombatStats(_itemBalance, _characterBalance);

            await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);

            var refreshPacket = WorldPacketBuilder.ViewportPlayerAppear(new[] { player });

            foreach (var other in _players.All)
            {
                if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
                {
                    await other.Session.SendAsync(refreshPacket, ct);
                }
            }
        }
    }

    /// <summary>Port of the "without explicit BuyMoney" portion of CItem::Value() (Item.cpp:916-975) -- used
    /// for buying. Final price the same as the original: if <see cref="ItemBalance.BuyMoney"/> is set
    /// (wings/orbs in this port, see ItemBalanceTable) it is used directly with the original's 2-stage rounding
    /// (≥100 → multiple of 10, then ≥1000 → multiple of 100); if not, it falls back to <see
    /// cref="ComputeShopSellPrice"/>*3 (the original computes Buy and Sell together in the same function; here
    /// they are separated for clarity but the value is identical).</summary>
    private uint ComputeShopBuyPrice(Item item, ItemBalance? info)
    {
        if (info == null)
        {
            return 0;
        }

        if (info.BuyMoney != 0)
        {
            return RoundPriceTwoStage(info.BuyMoney);
        }

        int? declarado = LookupItemValue(item, info);

        if (declarado.HasValue)
        {
            return RoundPriceTwoStage(declarado.Value);
        }

        return ComputeGeneralPrice(item, info).buy;
    }

    /// <summary>The explicit price from Data/Item/ItemValue.txt, if the file mentions this item. <para>It goes
    /// after <see cref="ItemBalance.BuyMoney"/> and before the general formula. The order relative to BuyMoney
    /// is immaterial in practice --none of the file's 72 rows corresponds to an item with a BuyMoney other than
    /// 0-- and this way the price of wings and orbs, which was already right, is not touched. Relative to the
    /// general formula the order does matter: five rows (the two Siege Potions, Ale, Bless and Soul) point to
    /// items that also carry the <c>Value</c> column, and it is the file that has the correct number -- by
    /// Value, the Jewel of Bless gave 18,700 instead of 9,000,000.</para> <para>The "grade" is the item's
    /// special-options mask. Today only the Horn of Dinorant uses it.</para></summary>
    private int? LookupItemValue(Item item, ItemBalance info)
    {
        return _itemValues.Get(info.Index, item.Level, item.NewOption);
    }

    /// <summary>Port of CItem::Value() (Item.cpp:916-975) -- sell branch. Deliberate simplifications compared
    /// to the original (outside the scope of this pass, see README): Muun items, Pentagram items, sockets,
    /// "380" items (build level bonus), custom jewels/wings via Lua, and the price bonuses for special options
    /// (Luck/Skill/Excellent/Additional) -- the "base" price of an item is computed without any of those
    /// options. Enough for a functional shop economy; the sign of the price (dearer the better the base item)
    /// is correct.</summary>
    private uint ComputeShopSellPrice(Item item, ItemBalance? info)
    {
        if (info == null)
        {
            return 0;
        }

        if (info.BuyMoney != 0)
        {
            return RoundPriceTwoStage(info.BuyMoney / 3);
        }

        int? declarado = LookupItemValue(item, info);

        if (declarado.HasValue)
        {
            return RoundPriceTwoStage(declarado.Value / 3);
        }

        return ComputeGeneralPrice(item, info).sell;
    }

    private static (uint buy, uint sell) ComputeGeneralPrice(Item item, ItemBalance info)
    {
        const long maxMoney = 2_000_000_000;

        if (info.Value > 0)
        {
            long price = ((long)info.Value * info.Value * 10) / 12;

            // Jewels (section 14, sub 0-8) -- special branch of the original that scales by level/durability
            // and uses a single-stage rounding (≥10 → multiple of 10, without the 2nd stage of ≥1000).
            if (info.Section == 14 && info.Sub is >= 0 and <= 8)
            {
                if (info.Sub == 3 || info.Sub == 6)
                {
                    price *= 2;
                }

                price *= 1L << Math.Clamp((int)item.Level, 0, 15);
                price *= Math.Max(item.Durability, (byte)1);
                price = Math.Min(price, maxMoney);

                return (RoundPriceOneStage(price), RoundPriceOneStage(price / 3));
            }

            price = Math.Min(price, maxMoney);
            return (RoundPriceTwoStage(price), RoundPriceTwoStage(price / 3));
        }

        // General formula based on ItemLevel (Item.cpp:1008-1125) -- only the branch without special options
        // (see the doc-comment of ComputeShopSellPrice).
        int itemLevel = info.Level + (Math.Clamp((int)item.Level, 0, 15) * 3);

        itemLevel += item.Level switch
        {
            5 => 4, 6 => 10, 7 => 25, 8 => 45, 9 => 65, 10 => 95,
            11 => 135, 12 => 185, 13 => 245, 14 => 305, 15 => 365, _ => 0,
        };

        long generalPrice;

        if (info.Section == 13) // pets/ring-pendant jewels/miscellaneous -- simple cubic formula
        {
            generalPrice = ((long)itemLevel * itemLevel * itemLevel) + 100;
        }
        else if (info.Section == 12) // alas -- en este puerto normalmente ya tienen BuyMoney seteado,
                                      // kept for robustness against rows without BuyMoney in the file
        {
            generalPrice = ((((long)itemLevel + 40) * itemLevel) * itemLevel * 11) + 40_000_000;
        }
        else
        {
            generalPrice = ((((long)itemLevel + 40) * itemLevel) * itemLevel / 8) + 100;

            if (info.IsWeapon && !info.TwoHand)
            {
                generalPrice = (generalPrice * 80) / 100;
            }
        }

        generalPrice = Math.Min(generalPrice, maxMoney);
        return (RoundPriceTwoStage(generalPrice), RoundPriceTwoStage(generalPrice / 3));
    }

    /// <summary>2-stage rounding of CItem::Value() -- first a multiple of 10 if ≥100, THEN a multiple of 100 if
    /// the (already rounded) result is ≥1000.</summary>
    private static uint RoundPriceTwoStage(long v)
    {
        if (v >= 100)
        {
            v = (v / 10) * 10;
        }

        if (v >= 1000)
        {
            v = (v / 100) * 100;
        }

        return (uint)Math.Max(v, 0);
    }

    /// <summary>Single-stage rounding (only a multiple of 10 if ≥10) -- used by the special jewel branch of
    /// CItem::Value().</summary>
    private static uint RoundPriceOneStage(long v)
    {
        if (v >= 10)
        {
            v = (v / 10) * 10;
        }

        return (uint)Math.Max(v, 0);
    }

    /// <summary>Simplified port of InventoryRectCheck/InventoryInsertItem (ItemManager.cpp:1226-1256) --
    /// first-free-spot scanning the main inventory grid (8 columns, rows 12-107) with the same algorithm as
    /// <see cref="ShopManagerTable"/> (there is no stacking of existing consumables, see the doc-comment of
    /// OnItemBuyAsync).</summary>
    private static bool TryFindEmptyInventoryRect(PlayerObject player, int width, int height, out int slot)
    {
        const int columns = 8;
        int rows = (Item.InventorySize - Item.InventoryWearSize) / columns;

        for (int y = 0; y <= rows - height; y++)
        {
            for (int x = 0; x <= columns - width; x++)
            {
                if (InventoryRectFree(player, x, y, width, height))
                {
                    slot = Item.InventoryWearSize + (y * columns) + x;
                    return true;
                }
            }
        }

        slot = -1;
        return false;
    }

    private static bool InventoryRectFree(PlayerObject player, int x, int y, int width, int height)
    {
        for (int dy = 0; dy < height; dy++)
        {
            for (int dx = 0; dx < width; dx++)
            {
                int checkSlot = Item.InventoryWearSize + ((y + dy) * 8) + (x + dx);

                if (checkSlot >= Item.InventorySize || player.Items[checkSlot].IsItem())
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task OnAttackAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = AttackRecv.Parse(p);

        // ShopNumber != null => it is a shop NPC, not a real monster -- the original never lets one end up here
        // because OBJECT_NPC is not a valid attack target (CAttack::Attack validates lpTarget->Type ==
        // OBJECT_MONSTER), here it is replicated with the same field OnNpcTalkAsync already uses to identify
        // NPCs (see the comment of Monster.ShopNumber).
        if (!_monsters.TryGet(recv.TargetIndex, out var monster) || monster.IsDead || monster.ShopNumber != null)
        {
            return;
        }

        if (monster.Map != player.Map)
        {
            return;
        }

        var map = _maps.GetMap(player.Map);

        // Port of CGAttackRecv (Attack.cpp:1565): you cannot attack while standing in a safe zone (bit 1), nor
        // a target that is standing in one.
        if ((map?.CheckAttr(player.X, player.Y, 1) ?? false) || (map?.CheckAttr(monster.X, monster.Y, 1) ?? false))
        {
            return;
        }

        double distance = Math.Sqrt(Math.Pow(player.X - monster.X, 2) + Math.Pow(player.Y - monster.Y, 2));
        double maxRange = player.Class == ClassFe ? 6 : 3;

        if (distance > maxRange)
        {
            return;
        }

        player.Dir = recv.Dir;

        // Broadcast of the attack animation (0x18) -- exact port of GCActionSend (Protocol.cpp: 1488-1507):
        // broadcasts PMSG_ACTION_SEND (0x18) via MsgSendV2 ONLY to the observers in the attacker's viewport,
        // NEVER to the attacker itself (sending 0x18 back to the client itself makes main.exe interrupt its
        // local animation/movement and reset the position).
        var actionPacket = CombatPacketBuilder.ActionSend(player.Index, player.Dir, recv.Action, monster.Index);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != player.Index && viewer.VisibleTo.Contains(player.Index))
            {
                await viewer.Session.SendAsync(actionPacket, ct);
            }
        }

        // ---- Paso 1: miss/dodge (puerto de CAttack::MissCheck, Attack.cpp:987-1031) ----
        int attackSuccess = Math.Max(player.AttackSuccessRate, 0);
        int defenseSuccess = Math.Max(monster.DefenseSuccessRate, 0);
        bool graze = false; // "miss=1 but it was not cancelled" -- see the formula comment below

        if (attackSuccess < defenseSuccess)
        {
            if (Rng.Next(100) >= 5)
            {
                await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, 0, 0, missFlag: true, monster.Life), ct);
                return;
            }

            graze = true;
        }
        else
        {
            int denom = attackSuccess == 0 ? 1 : attackSuccess;

            if (Rng.Next(denom) < defenseSuccess)
            {
                await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, 0, 0, missFlag: true, monster.Life), ct);
                return;
            }
        }

        // ---- Step 2: target defense (CAttack::GetTargetDefense, Attack.cpp:1117-1154) ---- Without the
        // halving reduction that applies when the target is an OBJECT_USER -- here the target is always a
        // monster, so its Defense is used as is.
        int targetDefense = Math.Max(monster.Defense, 0);

        // ---- Step 3: raw damage (CAttack::GetAttackDamage, Attack.cpp:1156-1307, player branch) ----
        int range = Math.Max(player.PhysiDamageMax - player.PhysiDamageMin, 1);
        int damage = player.PhysiDamageMin + Rng.Next(range);

        if (graze)
        {
            damage = (damage * 30) / 100; // "golpe de gracia" pese al mal roll de acierto/esquiva
        }

        damage -= targetDefense;
        damage = Math.Max(damage, 0);

        // ---- Step 4: per-level damage floor (Attack.cpp:365-366) ----
        int minDamage = Math.Max(player.Level / 10, 1);

        if (damage < minDamage)
        {
            damage = minDamage + Rng.Next(minDamage);
        }

        // Global multipliers (m_GeneralDamageRatePvM, per map/level DamageTable) not ported yet -- equivalent
        // to 100% (no change), which is the default of an untouched package.

        monster.Life = Math.Max(monster.Life - damage, 0);
        monster.DamageByAttacker.TryGetValue(player.Index, out var accumulated);
        monster.DamageByAttacker[player.Index] = accumulated + damage;

        await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, damage, 0, missFlag: false, monster.Life), ct);

        Log.Add(LogColor.Black, "[Combat][{0}] '{1}' hits {2}(#{3}) for {4} (remaining life {5}/{6})",
            player.Index, player.Name, monster.Name, monster.Index, damage, monster.Life, monster.MaxLife);

        if (monster.Life > 0)
        {
            return;
        }

        await OnMonsterDeathAsync(player, monster, ct);
    }

    /// <summary> Simplified port of CSkillManager::CGSkillAttackRecv + UseAttackSkill + CAttack::
    /// GetAttackDamageWizard (SkillManager.cpp:2418-2515,770-832; Attack.cpp:1309-1384) -- ONLY the casting of
    /// a single-target attack skill (C3:19) by a player against a monster (no PvP, no area/duration skills
    /// C3:1E, no multi-hit C3:1D, no Teleport Ally). Explicitly documented simplifications compared to the
    /// original: 1) No "learned skills" system (CSkillManager::GetSkill looks in lpObj->Skill[], a list
    /// populated by a learning flow/skill tree that is not ported) -- here it is validated directly against
    /// SkillList.txt at cast time (class+level), instead of against a list of previously learned skills. Any
    /// player of the right class/level can cast any skill that applies to them without having "learned" it
    /// first. 2) CheckSkillRequireClass follows the same check (RequireClass[class] != 0 && ChangeUp+1 >=
    /// RequireClass[class]) -- <see cref="PlayerObject.ChangeUp"/> is now derived from the real DB value (see
    /// OnCharacterInfoFromDataServerAsync), but with the current seed data (all characters in 1st class) it
    /// still is 0 in practice, so only the skills with RequireClass==1 for the player's class are reachable
    /// until there is data for a character with a real class change to test it. 3) No SkillUseArea.txt
    /// (per-skill map restriction) -- only the safe-zone check (map.CheckAttr bit 1) already used by the melee
    /// attack is reused. 4) No critical/excellent/PvP damage/Dark Knight combo (same reasons as the melee
    /// attack -- they depend on unported systems). 5) MPConsumptionRate/BPConsumptionRate (cost reduction by
    /// item/effect) are assumed 100% always -- there is no item-option system or active effects yet. 6)
    /// Hit/dodge and the target's defense reuse exactly the same calculation as the melee attack
    /// (AttackSuccessRate/DefenseSuccessRate/Defense) -- the original does not document a separate "magic hit"
    /// formula in the parts of Attack.cpp that were reviewed. </summary>
    private async Task OnSkillAttackAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = SkillAttackRecv.Parse(p);

        // See the equivalent comment in OnAttackAsync -- a shop NPC is not a valid target.
        if (!_monsters.TryGet(recv.TargetIndex, out var monster) || monster.IsDead || monster.ShopNumber != null)
        {
            return;
        }

        if (monster.Map != player.Map)
        {
            return;
        }

        var map = _maps.GetMap(player.Map);

        // Port of CGSkillAttackRecv (SkillManager.cpp:2456-2472, simplified without SkillUseArea.txt): you
        // cannot cast while standing in a safe zone, nor against a target standing in one.
        if ((map?.CheckAttr(player.X, player.Y, 1) ?? false) || (map?.CheckAttr(monster.X, monster.Y, 1) ?? false))
        {
            return;
        }

        var skill = _skills.Get(recv.Skill);

        if (skill == null)
        {
            return;
        }

        // ---- Class/level validation (replaces the "learned skill" check, see point 1 of this method's
        // doc-comment) ----
        if (!skill.CanUse(player.Class, player.ChangeUp) || player.Level < skill.RequireLevel)
        {
            return;
        }

        // ---- Cooldown por skill (CheckSkillDelay, SkillManager.cpp:440-454) ----
        var now = DateTime.UtcNow;

        if (player.SkillDelay.TryGetValue(skill.Index, out var lastCast)
            && (now - lastCast).TotalMilliseconds < skill.Delay)
        {
            return;
        }

        double distance = Math.Sqrt(Math.Pow(player.X - monster.X, 2) + Math.Pow(player.Y - monster.Y, 2));

        if (distance > Math.Max(skill.Range, 1))
        {
            return; // out of range: "free cast" in the original (consumes no mana, see point 5)
        }

        player.SkillDelay[skill.Index] = now;

        // ---- Mana/BP (CheckSkillMana/CheckSkillBP, SkillManager.cpp:333-365) ----
        if ((int)player.Mana < skill.Mana || (int)player.BP < skill.BP)
        {
            return; // insuficiente: casteo totalmente silencioso, igual que el original (ver punto 5)
        }

        player.Mana = (uint)Math.Max((int)player.Mana - skill.Mana, 0);
        player.BP = (uint)Math.Max((int)player.BP - skill.BP, 0);

        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);

        // ---- Step 1: miss/dodge (same calculation as CAttack::MissCheck used in OnAttackAsync) ----
        int attackSuccess = Math.Max(player.AttackSuccessRate, 0);
        int defenseSuccess = Math.Max(monster.DefenseSuccessRate, 0);
        bool graze = false;
        bool missed;

        if (attackSuccess < defenseSuccess)
        {
            missed = Rng.Next(100) >= 5;
            graze = !missed;
        }
        else
        {
            int denom = attackSuccess == 0 ? 1 : attackSuccess;
            missed = Rng.Next(denom) < defenseSuccess;
        }

        if (missed)
        {
            await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, 0, 0, missFlag: true, monster.Life), ct);
            return;
        }

        // ---- Step 2: raw magic damage (CAttack::GetAttackDamageWizard, Attack.cpp:1309-1384) ----
        int damageMin = player.MagicDamageMin + skill.DamageMin;
        int damageMax = player.MagicDamageMax + skill.DamageMax;
        int range = Math.Max(damageMax - damageMin, 1);
        int damage = damageMin + Rng.Next(range);

        if (graze)
        {
            damage = (damage * 30) / 100;
        }

        damage -= Math.Max(monster.Defense, 0);
        damage = Math.Max(damage, 0);

        // ---- Step 3: per-level damage floor (same as the melee attack, Attack.cpp:365-366) ----
        int minDamage = Math.Max(player.Level / 10, 1);

        if (damage < minDamage)
        {
            damage = minDamage + Rng.Next(minDamage);
        }

        // ---- Step 4: optional per-skill multiplier (SkillDamage.txt -- no-op with the real data) ----
        damage = _skillDamage.Apply(skill.Index, damage);

        monster.Life = Math.Max(monster.Life - damage, 0);
        monster.DamageByAttacker.TryGetValue(player.Index, out var accumulated);
        monster.DamageByAttacker[player.Index] = accumulated + damage;

        await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, damage, 0, missFlag: false, monster.Life), ct);

        // Port of GCSkillAttackSend (SkillManager.cpp:2665-2685): unicast to the caster itself + fan-out by
        // viewport to whoever is watching, block-encrypted (C3).
        var skillPacket = SkillPacketBuilder.SkillAttackSend((byte)skill.Index, player.Index, monster.Index);
        await session.SendEncryptedAsync(skillPacket, ct);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != player.Index && viewer.VisibleTo.Contains(player.Index))
            {
                await viewer.Session.SendEncryptedAsync(skillPacket, ct);
            }
        }

        Log.Add(LogColor.Black, "[Skill][{0}] '{1}' casts '{2}' on {3}(#{4}) for {5} (mana {6}/{7}, remaining life {8}/{9})",
            player.Index, player.Name, skill.Name, monster.Name, monster.Index, damage, player.Mana, player.MaxMana, monster.Life, monster.MaxLife);

        if (monster.Life > 0)
        {
            return;
        }

        await OnMonsterDeathAsync(player, monster, ct);
    }

    private async Task OnDurationSkillAttackAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || player.IsDying) return;

        var recv = DurationSkillAttackRecv.Parse(p);

        var skill = _skills?.Get(recv.Skill);
        if (skill != null)
        {
            var now = DateTime.UtcNow;
            if (player.SkillDelay.TryGetValue(skill.Index, out var lastCast) && (now - lastCast).TotalMilliseconds < skill.Delay)
            {
                return;
            }
            player.SkillDelay[skill.Index] = now;

            if ((int)player.Mana >= skill.Mana && (int)player.BP >= skill.BP)
            {
                player.Mana = (uint)Math.Max(0, (int)player.Mana - skill.Mana);
                player.BP = (uint)Math.Max(0, (int)player.BP - skill.BP);
                await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);
            }
        }

        var sendPacket = SkillPacketBuilder.DurationSkillAttackSend(player.Index, recv.Skill, recv.X, recv.Y, recv.Dir);
        await session.SendEncryptedAsync(sendPacket, ct);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != player.Index && viewer.WorldEntered && viewer.VisibleTo.Contains(player.Index))
            {
                await viewer.Session.SendEncryptedAsync(sendPacket, ct);
            }
        }

        if (skill != null)
        {
            if (recv.TargetIndex != 0xFFFF && _monsters.TryGet(recv.TargetIndex, out var monster) && monster.Map == player.Map && !monster.IsDead)
            {
                await ProcessSkillDamageAsync(player, monster, skill, ct);
            }
            else
            {
                int radius = skill.Range > 0 ? skill.Range : 2;
                if (skill.Index == 41) radius = 3; // Twisting Slash

                foreach (var m in _monsters.All)
                {
                    if (m.Map == player.Map && !m.IsDead && m.ShopNumber == null &&
                        Math.Abs(m.X - player.X) <= radius && Math.Abs(m.Y - player.Y) <= radius)
                    {
                        await ProcessSkillDamageAsync(player, m, skill, ct);
                    }
                }
            }
        }
    }

    private async Task OnMultiSkillAttackAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || player.IsDying) return;

        var recv = MultiSkillAttackRecv.Parse(p);

        var skill = _skills?.Get(recv.Skill);
        if (skill == null) return;

        foreach (var targetIndex in recv.Targets)
        {
            if (_monsters.TryGet(targetIndex, out var monster) && monster.Map == player.Map && !monster.IsDead && monster.ShopNumber == null)
            {
                await ProcessSkillDamageAsync(player, monster, skill, ct);
            }
        }
    }

    private async Task ProcessSkillDamageAsync(PlayerObject player, Monster monster, SkillInfo skill, CancellationToken ct)
    {
        int attackSuccess = Math.Max(player.AttackSuccessRate, 0);
        int defenseSuccess = Math.Max(monster.DefenseSuccessRate, 0);

        bool missed;
        bool graze = false;

        if (player.Level >= monster.Level)
        {
            int margin = player.Level - monster.Level;
            int denom = Math.Max(attackSuccess + defenseSuccess, 1);
            int rate = (attackSuccess * 100) / denom;

            if (margin < 10)
            {
                rate += margin * 2;
            }
            else
            {
                rate += 20;
            }

            int roll = Rng.Next(100);
            missed = roll >= rate;
            graze = !missed && roll >= (rate * 80) / 100;
        }
        else
        {
            int denom = attackSuccess == 0 ? 1 : attackSuccess;
            missed = Rng.Next(denom) < defenseSuccess;
        }

        if (missed)
        {
            await player.Session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, 0, 0, missFlag: true, monster.Life), ct);
            return;
        }

        bool isMagic = player.Class == 0 || skill.Index < 30 || skill.Index == 38 || skill.Index == 39;

        int baseMin = isMagic ? player.MagicDamageMin : player.PhysiDamageMin;
        int baseMax = isMagic ? player.MagicDamageMax : player.PhysiDamageMax;

        int damageMin = baseMin + skill.DamageMin;
        int damageMax = baseMax + skill.DamageMax;
        int range = Math.Max(damageMax - damageMin, 1);
        int damage = damageMin + Rng.Next(range);

        if (graze) damage = (damage * 30) / 100;

        damage -= Math.Max(monster.Defense, 0);
        damage = Math.Max(damage, 0);

        int minDamage = Math.Max(player.Level / 10, 1);
        if (damage < minDamage) damage = minDamage + Rng.Next(minDamage);

        damage = _skillDamage.Apply(skill.Index, damage);

        monster.Life = Math.Max(monster.Life - damage, 0);
        monster.DamageByAttacker.TryGetValue(player.Index, out var accumulated);
        monster.DamageByAttacker[player.Index] = accumulated + damage;

        await player.Session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, damage, 0, missFlag: false, monster.Life), ct);

        if (monster.Life <= 0)
        {
            await OnMonsterDeathAsync(player, monster, ct);
        }
    }

    /// <summary> Port of CGPositionRecv (Protocol.cpp:557-610) -- player position synchronisation (0xD0).
    /// </summary>
    private async Task OnPositionAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        var recv = PositionRecv.Parse(p);

        player.X = recv.X;
        player.Y = recv.Y;
        player.TX = recv.X;
        player.TY = recv.Y;

        var posPacket = SkillPacketBuilder.PositionSend(player.Index, recv.X, recv.Y);
        await session.SendAsync(posPacket, ct);

        foreach (var other in _players.All)
        {
            if (other.Index != player.Index && other.VisibleTo.Contains(player.Index))
            {
                await other.Session.SendAsync(posPacket, ct);
            }
        }
    }

    /// <summary> Simplified port of CObjectManager::CharacterLifeCheck (the monster death branch,
    /// ObjectManager.cpp:2815-2929) + CharacterCalcExperienceSplit/Alone (789-865) + CharacterLevelUp
    /// (983-1041). Individual split only (no party -- Social/Party not ported yet): each attacker takes
    /// experience proportional to THEIR accumulated damage on this monster, just as the original does even
    /// outside a party. </summary>
    private async Task OnMonsterDeathAsync(PlayerObject killer, Monster monster, CancellationToken ct)
    {
        monster.Live = false;
        monster.DiedAt = DateTime.UtcNow;

        // Port of CObjectManager::CharacterLifeCheck (ObjectManager.cpp:2893-2903): on dying, the original ONLY
        // sets Live=0/State=OBJECT_DYING and sends the death packet (GCUserDieSend) -- it does NOT remove the
        // object from the viewport yet. The corpse is still seen (playing its death animation) until the
        // respawn timer elapses and gObjMonsterRegen calls gObjClearViewport, which only then removes it from
        // everybody's view (see RespawnDeadMonsters in ViewportTicker). Removing it immediately here -- as this
        // method used to do -- is what made monsters "vanish" with no visible death effect.
        var dieSend = CombatPacketBuilder.UserDieSend(monster.Index, 0, killer.Index);
        var viewers = monster.VisibleTo.ToList();

        foreach (var viewerIndex in viewers)
        {
            if (_players.TryGet(viewerIndex, out var viewer))
            {
                await viewer.Session.SendAsync(dieSend, ct);
            }
        }

        Log.Add(LogColor.Blue, "[Combat][{0}] '{1}' killed {2}(#{3}) -- respawn in {4}ms", killer.Index, killer.Name, monster.Name, monster.Index, monster.MaxRegenMillis + 1000);

        await TryDropLootAsync(killer, monster, viewers, ct);

        // Port of CDevilSquare::MonsterDieProc (Phase 6) -- independent of the experience share below, it only
        // applies if the monster belongs to an active Devil Square run.
        if (_devilSquare != null)
        {
            await _devilSquare.OnMonsterKilledAsync(monster, ct);
        }

        // Port of the part of CObjectManager::CharacterLifeCheck that decides between solo vs. party share
        // (ObjectManager.cpp:4817): when the attacker is in a party with >=2 members, the experience share of
        // the WHOLE party is computed only once per party (not per attacker) -- hence processedParties, so as
        // not to process the same party twice if more than one member hit the monster.
        var processedParties = new HashSet<int>();

        foreach (var (attackerIndex, damageDealt) in monster.DamageByAttacker)
        {
            if (!_players.TryGet(attackerIndex, out var attacker))
            {
                continue;
            }

            if (attacker.PartyNumber != -1 && _parties.TryGet(attacker.PartyNumber, out var group) && group.MemberIndices.Count > 1)
            {
                if (processedParties.Add(group.Id))
                {
                    await GrantPartyExperienceAsync(group, monster, ct);
                }

                continue;
            }

            await GrantExperienceAsync(attacker, monster, damageDealt, ct);
        }
    }

    /// <summary> VERY simplified port of gObjMonsterDieGiveItem (Monster.cpp:54-284) -- of the real cascade of
    /// ~13 drop subsystems (per-monster-class ItemBag, boss/event drop tables, scheduled drop-events, random
    /// set item, etc., see the research), this pass ONLY ports the "generic" path that covers the vast majority
    /// of common field monsters: an item roll (according to <see cref="Monster.ItemRate"/>, reusing <see
    /// cref="ItemBalanceTable.PickRandomDropItem"/> -- the same Item.txt/DropItem data Phase 4 of balance
    /// already loads) OR, if no item came out, a money roll (according to <see cref="Monster.MoneyRate"/>).
    /// Both rates are interpreted as "1 in N" (<c>Rng.Next(rate)==0</c>), the standard interpretation of these
    /// two columns in MonsterList.txt. Outside this pass (documented in detail in the README): ItemBagManager
    /// (special drops per monster class/event), random sets, random level/excellent/socket options on the
    /// dropped item (it comes out "from the factory", +0 with no options), unique persistent serial via
    /// DataServer (left at 0, the same as items bought in the Phase 8 shop -- the same level of simplification
    /// already accepted there). </summary> <summary>Port of CQuestObjective::MonsterItemDrop
    /// (QuestObjective.cpp:213-269) -- hooked into TryDropLootAsync BEFORE the generic item/money roll (the
    /// same as Monster.cpp:59-67). It is evaluated against whoever did the most damage to the monster
    /// (gObjMonsterGetTopHitDamageUser), not necessarily who dealt the final blow. The party-share variant
    /// (MonsterItemDropParty, gServerInfo.m_QuestMonsterItemDropParty) is not ported -- it is evaluated only
    /// against the top-damager, a documented simplification.</summary>
    private GroundItem? TryQuestItemDrop(PlayerObject killer, Monster monster)
    {
        if (_groundItems == null || _questObjectives.Entries.Count == 0)
        {
            return null;
        }

        PlayerObject topDamager = killer;
        int topDamage = -1;

        foreach (var (attackerIndex, damageDealt) in monster.DamageByAttacker)
        {
            if (damageDealt > topDamage && _players.TryGet(attackerIndex, out var attacker))
            {
                topDamage = damageDealt;
                topDamager = attacker;
            }
        }

        foreach (var info in _questObjectives.Entries)
        {
            if (info.Type != QuestObjectiveType.Item)
            {
                continue;
            }

            if (!QuestObjectiveTable.CheckRequisite(info, topDamager.Quest!, topDamager.Class, topDamager.ChangeUp))
            {
                continue;
            }

            if (info.MapNumber != -1 && info.MapNumber != monster.Map)
            {
                continue;
            }

            // EXACT port of QuestObjective.cpp:236-244 (includes the special case: if there is only
            // DropMaxLevel and no DropMinLevel, it is compared against monster.Class instead of the level).
            if (info.DropMinLevel != -1 && info.DropMinLevel > monster.Level)
            {
                continue;
            }

            if (info.DropMinLevel != -1 && info.DropMaxLevel != -1 && info.DropMaxLevel < monster.Level)
            {
                continue;
            }

            if (info.DropMinLevel == -1 && info.DropMaxLevel != -1 && info.DropMaxLevel != monster.MonsterClass)
            {
                continue;
            }

            if (info.ItemDropRate <= Rng.Next(10000))
            {
                continue;
            }

            if (GetQuestObjectiveCount(topDamager, info) >= info.Quantity)
            {
                continue;
            }

            var item = new Item
            {
                Index = (short)info.Index,
                Level = (byte)Math.Max(info.Level, 0),
                Durability = 1,
                Option1 = (byte)info.Option1,
                Option2 = (byte)info.Option2,
                Option3 = (byte)info.Option3,
                NewOption = (byte)info.NewOption,
            };

            int? ownerParty = killer.PartyNumber == -1 ? null : killer.PartyNumber;
            return _groundItems.Drop(monster.Map, item, monster.X, monster.Y, killer.Index, ownerParty, GroundItemLifetime, GroundItemLootLock);
        }

        return null;
    }

    private async Task TryDropLootAsync(PlayerObject killer, Monster monster, IReadOnlyList<int> viewerIndexes, CancellationToken ct)
    {
        if (_groundItems == null)
        {
            return;
        }

        int? ownerParty = killer.PartyNumber == -1 ? null : killer.PartyNumber;

        // Port of Monster.cpp:59-67: gQuestObjective.MonsterItemDrop is checked BEFORE the generic item/money
        // drop (which follows below) -- if a quest item drops, THAT is this kill's drop, and neither of the
        // other two rolls runs (see the "dropped == null" guard that follows).
        GroundItem? dropped = TryQuestItemDrop(killer, monster);

        int itemRate = monster.ItemRate <= 0 ? 10 : monster.ItemRate;
        int moneyRate = monster.MoneyRate <= 0 ? 10 : monster.MoneyRate;

        // In C++ (Monster.cpp:121,157): ItemRate/MoneyRate of MonsterList.txt define the roll. If
        // Rng.Next(ItemRate) < 10 (base ItemDropRate 10), there is an item drop.
        if (dropped == null && Rng.Next(itemRate) < 10)
        {
            var pick = _itemBalance.PickRandomDropItem(monster.Level, Rng);

            if (pick != null)
            {
                byte itemLevel = 0;
                if (pick.Section < 12)
                {
                    int maxLvl = Math.Min(4, monster.Level / 20);
                    if (maxLvl > 0) itemLevel = (byte)Rng.Next(0, maxLvl + 1);
                }

                bool luck = pick.Section < 12 && Rng.Next(100) < 15;
                bool skill = pick.Section < 6 && Rng.Next(100) < 30;
                byte option3 = 0;
                if (pick.Section < 12)
                {
                    int optRoll = Rng.Next(100);
                    if (optRoll < 5) option3 = 2; // +8
                    else if (optRoll < 20) option3 = 1; // +4
                }

                byte newOption = 0;
                if (monster.Level >= 25 && Rng.Next(1500) == 0) // Excellent item roll
                {
                    byte[] excOpts = { 1, 2, 4, 8, 16, 32 };
                    newOption = excOpts[Rng.Next(excOpts.Length)];
                    itemLevel = 0;
                }

                var item = new Item
                {
                    Index = (short)pick.Index,
                    Level = itemLevel,
                    Durability = (byte)Math.Clamp(pick.Durability == 0 ? 1 : pick.Durability, 1, 255),
                    Option1 = (byte)(luck ? 1 : 0),
                    Option2 = (byte)(skill ? 1 : 0),
                    Option3 = option3,
                    NewOption = newOption
                };

                dropped = _groundItems.Drop(monster.Map, item, monster.X, monster.Y, killer.Index, ownerParty, GroundItemLifetime, GroundItemLootLock);
            }
        }
        
        if (dropped == null && Rng.Next(moneyRate) < 10)
        {
            long baseMoney = ((long)(monster.Level + 25) * monster.Level) / 3;
            baseMoney += baseMoney / 4;

            int dropRateConfig = WorldPacketBuilder.ServerInfo.MoneyAmountDropRate.Length > killer.AccountLevel
                ? WorldPacketBuilder.ServerInfo.MoneyAmountDropRate[killer.AccountLevel]
                : 100;

            if (dropRateConfig <= 0) dropRateConfig = 100;

            long calcMoney = (baseMoney * dropRateConfig) / 100;
            calcMoney = (calcMoney * Rng.Next(80, 121)) / 100;

            uint money = (uint)Math.Max(1, calcMoney);
            dropped = _groundItems.DropMoney(monster.Map, monster.X, monster.Y, money, GroundItemLifetime);
        }

        if (dropped == null)
        {
            return;
        }

        var appearPacket = WorldPacketBuilder.ViewportItemAppear(new[] { dropped });

        foreach (var viewerIndex in viewerIndexes)
        {
            if (_players.TryGet(viewerIndex, out var viewer))
            {
                await viewer.Session.SendAsync(appearPacket, ct);
            }
        }

        if (!viewerIndexes.Contains(killer.Index))
        {
            // The killer must also see it even if for some reason they were not in the monster's "viewers" list
            // (e.g. they finished it with a ranged hit right at the edge of view range) -- it is sent once more
            // directly, without duplicating if it was already there.
            await killer.Session.SendAsync(appearPacket, ct);
        }

        dropped.JustDropped = false; // the "just fallen" appearance was already sent once
    }

    private async Task GrantExperienceAsync(PlayerObject attacker, Monster monster, int damageDealt, CancellationToken ct)
    {
        // Puerto de CharacterCalcExperienceAlone (ObjectManager.cpp:823-865).
        long level = ((long)(monster.Level + 25) * monster.Level) / 3;

        if (monster.Level + 10 < attacker.Level)
        {
            level = (level * (monster.Level + 10)) / attacker.Level;
        }

        if (monster.Level >= 65)
        {
            level += (monster.Level - 64) * (monster.Level / 4);
        }

        int damageCredit = Math.Min(damageDealt, (int)monster.MaxLife);
        long baseExperience = level + (level / 4);
        long experience = monster.MaxLife <= 0 ? 0 : (damageCredit * baseExperience) / (long)monster.MaxLife;
        // Port of CharacterCalcExperienceAlone (ObjectManager.cpp:845): m_AddExperienceRate is a DIRECT
        // multiplier (not a percentage) indexed by AccountLevel. The other global multipliers that do apply
        // /100 (map, bonus, reset) read from unported systems and still equal 100% (no change).
        experience *= WorldPacketBuilder.ServerInfo.AddExperienceRate[attacker.AccountLevel];

        await ApplyExperienceGainAsync(attacker, monster.Index, experience, damageCredit, ct);
    }

    /// <summary> Port of CharacterCalcExperienceParty (ObjectManager.cpp:1345): unlike the solo split (by own
    /// damage), here the experience "pot" is computed over the TOTAL damage the whole party did to the monster
    /// (adding up that of all members, not only of whoever dealt the final blow), and shared among the members
    /// who are on the SAME map and within <= <see cref="MaxPartyDistance"/> tiles of the monster (not of the
    /// attacker) -- proportional to each one's level, NOT to the damage each one did individually (so a member
    /// who did not manage to hit it still takes their share if in range). The bonus tables by party size and
    /// class diversity (m_PartyGeneralExperience/m_PartySpecialExperience) are not ported yet -- equivalent to
    /// no bonus (documented in the README, the same kind of technical debt as the rest of this phase's global
    /// multipliers). </summary>
    private async Task GrantPartyExperienceAsync(PartyGroup group, Monster monster, CancellationToken ct)
    {
        var members = new List<PlayerObject>();

        foreach (var idx in group.MemberIndices)
        {
            if (_players.TryGet(idx, out var m))
            {
                members.Add(m);
            }
        }

        var inRange = members.Where(m =>
            m.Map == monster.Map
            && Math.Abs(m.X - monster.X) <= MaxPartyDistance
            && Math.Abs(m.Y - monster.Y) <= MaxPartyDistance).ToList();

        if (inRange.Count == 0)
        {
            return;
        }

        long partyDamageSum = 0;

        foreach (var m in members)
        {
            if (monster.DamageByAttacker.TryGetValue(m.Index, out var d))
            {
                partyDamageSum += d;
            }
        }

        int damageCredit = (int)Math.Min(partyDamageSum, monster.MaxLife);
        int totalLevel = inRange.Sum(m => (int)m.Level);

        if (totalLevel <= 0)
        {
            return;
        }

        int partyLevel = totalLevel / inRange.Count;

        long level = ((long)(monster.Level + 25) * monster.Level) / 3;

        if (monster.Level + 10 < partyLevel)
        {
            level = (level * (monster.Level + 10)) / partyLevel;
        }

        if (monster.Level >= 65)
        {
            level += (monster.Level - 64) * (monster.Level / 4);
        }

        long baseExperience = level + (level / 4);
        long totalExperience = monster.MaxLife <= 0 ? 0 : (damageCredit * baseExperience) / (long)monster.MaxLife;

        foreach (var member in inRange)
        {
            long share = (totalExperience * member.Level) / totalLevel;
            await ApplyExperienceGainAsync(member, monster.Index, share, damageCredit, ct);
        }
    }

    /// <summary>Common tail of both paths above (solo and party): add experience, resolve chained level-ups
    /// (CharacterLevelUp, ObjectManager.cpp:983-1041) and send the corresponding packets. <paramref
    /// name="monsterIndex"/> is only for the packet's informational field (what was killed to gain this) --
    /// Phase 6 reuses it for event experience rewards (Devil Square), which has no associated real monster,
    /// with the sentinel -1 (0xFFFF on the wire, no real monster/player uses that index).</summary>
    private async Task ApplyExperienceGainAsync(PlayerObject member, int monsterIndex, long experience, int damageCredit, CancellationToken ct)
    {
        long addExperience = Math.Max(experience, 0);
        int maxLevelUp = WorldPacketBuilder.ServerInfo.MaxLevelUp;
        int maxLevel = WorldPacketBuilder.ServerInfo.MaxLevel;

        bool leveledUp = false;

        if (member.Level >= maxLevel)
        {
            // Port of CharacterLevelUp (ObjectManager.cpp:985-989): at the cap, the experience gained is not
            // even saved -- it is discarded entirely.
        }
        else if (member.Experience + addExperience < WorldPacketBuilder.NextExperience(member.Level))
        {
            member.Experience += (uint)addExperience;
        }
        else
        {
            while (true)
            {
                member.Level++;
                int pointsPerLevel = (member.Class == 3 || member.Class == 4) ? 7 : 5;
                member.LevelUpPoint += (uint)pointsPerLevel;
                leveledUp = true;

                // Exact port of ObjectManager.cpp:1005: when the per-event level cap (MaxLevelUp) is exhausted,
                // whatever experience is left over is discarded -- it is not saved for the next kill.
                if (--maxLevelUp == 0)
                {
                    addExperience = 0;
                }
                else
                {
                    addExperience -= WorldPacketBuilder.NextExperience(member.Level - 1) - member.Experience;
                }

                member.Experience = WorldPacketBuilder.NextExperience(member.Level - 1);

                if (member.Level >= maxLevel)
                {
                    addExperience = 0;
                    break;
                }

                if (member.Experience + addExperience < WorldPacketBuilder.NextExperience(member.Level))
                {
                    member.Experience += (uint)addExperience;
                    break;
                }
            }
        }

        // Port of ObjectManager.cpp:857-864: if there was a level-up, this packet's experience popup sends 0
        // (the notice is already given by GCLevelUpSend/LevelUpSend further below) -- otherwise, it sends the
        // real experience gained.
        await member.Session.SendAsync(
            CombatPacketBuilder.MonsterDieSend(monsterIndex, leveledUp ? 0u : (uint)Math.Max(experience, 0), damageCredit,
                (uint)Math.Min(member.Experience, uint.MaxValue), WorldPacketBuilder.NextExperience(member.Level)),
            ct);

        if (!leveledUp)
        {
            return;
        }

        member.Life = member.MaxLife;
        member.Mana = member.MaxMana;

        await member.Session.SendAsync(CombatPacketBuilder.LevelUpSend(member), ct);

        Log.Add(LogColor.Blue, "[Combat][{0}] '{1}' reached level {2} (Points: {3})", member.Index, member.Name, member.Level, member.LevelUpPoint);

        member.CharSaveTime = DateTime.UtcNow;
        await SaveCharacterAsync(member, ct);
    }

    // ---------------------------------------------------------------- Fase 5: chat y whisper (primera pasada)

    /// <summary> Simplified port of CGChatRecv (Protocol.cpp:1164) for public chat without a sigil: echo to the
    /// speaker + broadcast to everyone who has them in their VisibleTo (the same viewport mechanism movement
    /// already uses -- confirmed that the original does exactly this via MsgSendV2/VpPlayer2[], see the Phase 5
    /// research brief). Commands ('/') and the other sigil channels (party '~', guild '@'/'@@'/'@>', gens '$')
    /// are not ported yet -- they are silently ignored instead of being treated as public chat (like the
    /// original, which excludes them from the "no sigil" branch and routes them to separate systems).
    /// </summary>
    private async Task OnChatAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = ChatRecv.Parse(p);

        // Anti-spoof: the client always sends its own real name (port of the check at the start of CGChatRecv).
        if (!string.Equals(recv.Name, player.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (recv.Message.Length == 0)
        {
            return;
        }

        char first = recv.Message[0];

        // Port of the part of CGChatRecv that multiplexes by initial sigil (Protocol.cpp:1164): '~' = party
        // chat (sends the message AS IS, sigil included, to each member via direct DataSend -- it does NOT go
        // through the viewport, so it arrives regardless of map/distance, just like the original iterating
        // m_PartyInfo[...].Index[0..4]). '/' (GM/user commands), '@'/'@@'/'@>' (guild) and '$' (Gens) are not
        // ported yet -- they are silently ignored instead of being treated as public chat.
        if (first == '/')
        {
            await OnCommandAsync(session, recv.Message, ct);
            return;
        }

        if (first == '~')
        {
            if (player.PartyNumber != -1 && _parties.TryGet(player.PartyNumber, out var partyGroup))
            {
                var partyChatPacket = ChatPacketBuilder.ChatSend(player.Name, recv.Message);

                foreach (var memberIndex in partyGroup.MemberIndices)
                {
                    if (_players.TryGet(memberIndex, out var member))
                    {
                        await member.Session.SendAsync(partyChatPacket, ct);
                    }
                }
            }

            return;
        }

        if (first == '/' || first == '@' || first == '$')
        {
            return;
        }

        var chatPacket = ChatPacketBuilder.ChatSend(player.Name, recv.Message);
        await session.SendAsync(chatPacket, ct);

        foreach (var viewer in _players.All)
        {
            if (viewer.Index != player.Index && viewer.VisibleTo.Contains(player.Index))
            {
                await viewer.Session.SendAsync(chatPacket, ct);
            }
        }
    }

    /// <summary>
    /// Puerto de CCommandManager (CommandManager.cpp:1-2516) -- comandos in-game (/addstr, /addagi, /addvit, /addene, /addcmd, /add, /post).
    /// </summary>
    private async Task OnCommandAsync(ClientSession session, string message, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null) return;

        var parts = message.TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        string cmd = parts[0].ToLower();
        int statType = -1; // 0=str, 1=agi, 2=vit, 3=ene, 4=cmd
        int amount = 0;

        if (cmd is "addstr" or "str")
        {
            statType = 0;
            if (parts.Length > 1) int.TryParse(parts[1], out amount);
        }
        else if (cmd is "addagi" or "agi")
        {
            statType = 1;
            if (parts.Length > 1) int.TryParse(parts[1], out amount);
        }
        else if (cmd is "addvit" or "vit")
        {
            statType = 2;
            if (parts.Length > 1) int.TryParse(parts[1], out amount);
        }
        else if (cmd is "addene" or "ene")
        {
            statType = 3;
            if (parts.Length > 1) int.TryParse(parts[1], out amount);
        }
        else if (cmd is "addcmd" or "cmd")
        {
            statType = 4;
            if (parts.Length > 1) int.TryParse(parts[1], out amount);
        }
        else if (cmd == "add" && parts.Length >= 2)
        {
            string sub = parts[1].ToLower();
            statType = sub switch
            {
                "str" or "0" => 0,
                "agi" or "1" => 1,
                "vit" or "2" => 2,
                "ene" or "3" => 3,
                "cmd" or "4" => 4,
                _ => -1
            };
            if (parts.Length > 2) int.TryParse(parts[2], out amount);
        }

        if (statType != -1)
        {
            if (statType == 4 && player.Class != 4)
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("[Server] Only Dark Lord can add points to Command.")), ct);
                return;
            }

            if (amount <= 0 || player.LevelUpPoint < amount)
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("[Server] Not enough points. You have {0} point(s).", player.LevelUpPoint)), ct);
                return;
            }

            switch (statType)
            {
                case 0: player.Strength += (uint)amount; break;
                case 1: player.Dexterity += (uint)amount; break;
                case 2: player.Vitality += (uint)amount; break;
                case 3: player.Energy += (uint)amount; break;
                case 4: player.Leadership += (uint)amount; break;
            }

            player.LevelUpPoint -= (uint)amount;
            player.RecalcCombatStats(_itemBalance, _characterBalance);

            await session.SendAsync(WorldPacketBuilder.NewCharacterInfoSend(player), ct);
            await SaveCharacterAsync(player, ct);

            string statName = statType switch { 0 => Loc.T("Strength"), 1 => Loc.T("Agility"), 2 => Loc.T("Vitality"), 3 => Loc.T("Energy"), 4 => Loc.T("Command"), _ => "" };
            await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("[Server] +{0} added to {1}. Remaining points: {2}", amount, statName, player.LevelUpPoint)), ct);
            return;
        }

        if (cmd == "zen")
        {
            player.Money = 10_000_000u;
            await session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
            await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("[Server] You have granted yourself 10,000,000 Zen.")), ct);
            return;
        }

        if (cmd is "scroll" or "questitem" or "item")
        {
            int targetItemIndex = Item.GetItem(14, 23); // Scroll of Emperor por defecto
            if (cmd == "item" && parts.Length >= 3 && int.TryParse(parts[1], out int sec) && int.TryParse(parts[2], out int subIdx))
            {
                targetItemIndex = Item.GetItem(sec, subIdx);
            }

            var newItem = new Item { Index = (short)targetItemIndex, Level = 0, Durability = 255 };
            var info = _itemBalance.Get(targetItemIndex);
            int width = info?.Width ?? 1;
            int height = info?.Height ?? 1;

            if (TryFindEmptyInventoryRect(player, width, height, out int slot))
            {
                player.SetItem(slot, newItem);
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, (byte)slot, newItem), ct);
                await SaveCharacterAsync(player, ct);
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("[Server] Item #{0} added to your inventory.", targetItemIndex)), ct);
            }
            else
            {
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("[Server] Inventory full.")), ct);
            }
            return;
        }

        if (cmd == "post" && parts.Length > 1)
        {
            string postMsg = string.Join(' ', parts.Skip(1));
            var postPacket = ChatPacketBuilder.NoticeSend($"[POST] {player.Name}: {postMsg}");
            foreach (var other in _players.All)
            {
                await other.Session.SendAsync(postPacket, ct);
            }
            return;
        }

        if ((cmd == "move" || cmd == "warp") && parts.Length > 1)
        {
            string destination = parts[1];
            var move = _moves.GetByName(destination);
            if (move != null)
            {
                var gate = _gates.Get(move.GateNumber);
                if (gate != null)
                {
                    var targetGate = (gate.TargetGate != 0) ? _gates.Get(gate.TargetGate) ?? gate : gate;

                    if (player.Level < move.MinLevel)
                    {
                        await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("Required level: {0}", move.MinLevel)), ct);
                        return;
                    }

                    if (player.Money < move.RequireMoney)
                    {
                        await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("Required zen: {0}", move.RequireMoney)), ct);
                        return;
                    }

                    player.Money -= move.RequireMoney;
                    await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);

                    player.Map = (byte)targetGate.Map;
                    byte targetX = (byte)(targetGate.EndX > targetGate.StartX ? Rng.Next(targetGate.StartX, targetGate.EndX + 1) : targetGate.StartX);
                    byte targetY = (byte)(targetGate.EndY > targetGate.StartY ? Rng.Next(targetGate.StartY, targetGate.EndY + 1) : targetGate.StartY);

                    player.X = targetX;
                    player.Y = targetY;
                    player.TX = targetX;
                    player.TY = targetY;
                    player.Dir = (byte)targetGate.TargetDir;
                    player.VisibleMonsters.Clear();

                    await session.SendEncryptedAsync(
                        WorldPacketBuilder.TeleportSend(1, player.Map, player.X, player.Y, player.Dir), ct);
                    return;
                }
            }
        }

        await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("[Server] Command '/{0}' not recognised.", cmd)), ct);
    }

    /// <summary> Port of CGChatWhisperRecv (Protocol.cpp:1290): looks for the recipient first on THIS
    /// GameServer (equivalent to gObjFind, a linear scan of online players); if not here, it asks DataServer
    /// (cross-GameServer whisper, protocol 0x72/0x73 already implemented on the DataServer side since before
    /// this phase). </summary>
    private async Task OnChatWhisperAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = ChatWhisperRecv.Parse(p);

        if (string.Equals(recv.TargetName, player.Name, StringComparison.OrdinalIgnoreCase))
        {
            // Self-whisper: the original rejects it with notice code 270 (GCServerMsgSend); that
            // short-notification system is not ported yet, so here nothing is simply sent.
            return;
        }

        var localTarget = _players.All.FirstOrDefault(
            x => string.Equals(x.Name, recv.TargetName, StringComparison.OrdinalIgnoreCase));

        if (localTarget != null)
        {
            await localTarget.Session.SendAsync(ChatPacketBuilder.ChatWhisperSend(player.Name, recv.Message), ct);
            return;
        }

        await _dataServer.SendAsync(
            SocialDataServerPacketBuilder.GlobalWhisperRequest(
                (ushort)player.Index, player.Account, player.Name, recv.TargetName, recv.Message), ct);
    }

    /// <summary>Callback from DataServerConnection when SDHP_GLOBAL_WHISPER_SEND comes back (0x72) -- confirms
    /// to the SENDER whether the whisper could be delivered on some GameServer.</summary>
    public async Task OnGlobalWhisperResultFromDataServerAsync(GlobalWhisperResultFromDataServer msg, CancellationToken ct)
    {
        if (!_players.TryGet(msg.Index, out var sender))
        {
            return;
        }

        if (msg.Result == 0)
        {
            // Recipient not found on any GameServer -- the original sends a notice code (270 via
            // GCServerMsgSend); that short-notification system is not ported yet, so for now the sender just
            // notices no answer arrived.
            Log.Add(LogColor.Black, "[Chat][{0}] Whisper from '{1}' to '{2}' -- recipient not found",
                sender.Index, sender.Name, msg.TargetName);
        }

        await Task.CompletedTask;
    }

    /// <summary>Callback from DataServerConnection when SDHP_GLOBAL_WHISPER_ECHO_SEND (0x73) arrives -- this
    /// GameServer DOES have the real recipient connected; the whisper is delivered to them.</summary>
    public async Task OnGlobalWhisperEchoFromDataServerAsync(GlobalWhisperEchoFromDataServer msg, CancellationToken ct)
    {
        if (!_players.TryGet(msg.Index, out var target))
        {
            return;
        }

        await target.Session.SendAsync(ChatPacketBuilder.ChatWhisperSend(msg.SourceName, msg.Message), ct);
    }

    // ---------------------------------------------------------------- Fase 5: party (primera pasada)

    /// <summary> Port of CGPartyRequestRecv (Party.cpp:332): validates that the target exists, is not oneself,
    /// is not already in a party, and that neither side has another pending invitation (simplified version of
    /// the original's busy Interface.use check). The maximum level difference check (m_PartyMaxGapLevel) and
    /// the Gens-lock one are not ported yet -- they block nothing for now (equivalent to having them disabled).
    /// AutoAcceptPartyRequest (the PartyMatching path) neither -- see the "safe to defer" note of the research
    /// brief. </summary>
    private async Task OnPartyRequestAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = PartyRequestRecv.Parse(p);

        if (!_players.TryGet(recv.TargetIndex, out var target) || target.Index == player.Index)
        {
            return;
        }

        if (player.PartyInviteTargetIndex != -1 || player.PartyInviterIndex != -1
            || target.PartyInviteTargetIndex != -1 || target.PartyInviterIndex != -1)
        {
            return; // one of the two already has a pending party invitation
        }

        if (target.PartyNumber != -1)
        {
            await session.SendAsync(PartyPacketBuilder.PartyResultSend(4), ct); // objetivo ya en un grupo
            return;
        }

        if (player.PartyNumber != -1 && _parties.TryGet(player.PartyNumber, out var existingGroup)
            && existingGroup.MemberIndices.Count >= PartyGroup.MaxMembers)
        {
            await session.SendAsync(PartyPacketBuilder.PartyResultSend(2), ct); // grupo propio lleno
            return;
        }

        player.PartyInviteTargetIndex = target.Index;
        target.PartyInviterIndex = player.Index;

        await target.Session.SendAsync(PartyPacketBuilder.PartyRequestSend(player.Index), ct);
    }

    /// <summary> Port of CGPartyRequestResultRecv (Party.cpp:439): validates that the reply corresponds to a
    /// really pending invitation (recv.InviterIndex must match what was stored when inviting), creates the
    /// party if the inviter did not have one yet, adds the invitee, and re-broadcasts the list to all members.
    /// It clears the invitation state on both sides at the end no matter what (equivalent to the original's
    /// CLEAR_JUMP). </summary>
    private async Task OnPartyRequestResultAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var invitee = session.Player;

        if (invitee == null)
        {
            return;
        }

        var recv = PartyRequestResultRecv.Parse(p);

        if (invitee.PartyInviterIndex != recv.InviterIndex || !_players.TryGet(recv.InviterIndex, out var inviter))
        {
            invitee.PartyInviterIndex = -1;
            return;
        }

        invitee.PartyInviterIndex = -1;
        inviter.PartyInviteTargetIndex = -1;

        if (recv.Result == 0)
        {
            await inviter.Session.SendAsync(PartyPacketBuilder.PartyResultSend(0), ct);
            return;
        }

        PartyGroup group;

        if (inviter.PartyNumber == -1)
        {
            group = _parties.Create();
            group.MemberIndices.Add(inviter.Index);
            inviter.PartyNumber = group.Id;
        }
        else if (_parties.TryGet(inviter.PartyNumber, out var existing))
        {
            group = existing;
        }
        else
        {
            inviter.PartyNumber = -1;
            await inviter.Session.SendAsync(PartyPacketBuilder.PartyResultSend(2), ct);
            return;
        }

        if (group.MemberIndices.Count >= PartyGroup.MaxMembers)
        {
            await inviter.Session.SendAsync(PartyPacketBuilder.PartyResultSend(2), ct);
            return;
        }

        group.MemberIndices.Add(invitee.Index);
        invitee.PartyNumber = group.Id;

        await BroadcastPartyListAsync(group, ct);

        Log.Add(LogColor.Blue, "[Party][{0}] '{1}' joined the party of '{2}' (party #{3}, {4} member(s))",
            invitee.Index, invitee.Name, inviter.Name, group.Id, group.MemberIndices.Count);
    }

    /// <summary>Puerto de CGPartyListRecv (pedido on-demand de la lista actual, ej. al abrir la
    /// ventana de grupo) -- PMSG_PARTY_LIST_SEND result=0 si no tiene grupo.</summary>
    private async Task OnPartyListRequestAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        if (player.PartyNumber == -1 || !_parties.TryGet(player.PartyNumber, out var group))
        {
            await session.SendAsync(PartyPacketBuilder.PartyListSend(0, Array.Empty<PartyListEntry>()), ct);
            return;
        }

        await session.SendAsync(PartyPacketBuilder.PartyListSend(1, BuildPartyListEntries(group)), ct);
    }

    /// <summary> Port of CGPartyDelMemberRecv (Party.cpp:599): number = own slot (leave) or another member's
    /// (kick -- only if the sender is the leader, slot 0). </summary>
    private async Task OnPartyDelMemberAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || player.PartyNumber == -1 || !_parties.TryGet(player.PartyNumber, out var group))
        {
            return;
        }

        var recv = PartyDelMemberRecv.Parse(p);

        if (recv.Number >= group.MemberIndices.Count)
        {
            return;
        }

        int targetPlayerIndex = group.MemberIndices[recv.Number];
        bool isLeader = group.MemberIndices.Count > 0 && group.MemberIndices[0] == player.Index;

        if (targetPlayerIndex != player.Index && !isLeader)
        {
            return; // only the leader can kick others
        }

        if (!_players.TryGet(targetPlayerIndex, out var target))
        {
            group.MemberIndices.Remove(targetPlayerIndex);
            return;
        }

        await RemovePlayerFromPartyAsync(target, ct);
    }

    /// <summary> Shared core of "remove a player from their current party" -- used both by
    /// PMSG_PARTY_DEL_MEMBER_RECV (leave/kick) and by disconnection (the original also removes the player from
    /// their party on disconnecting, CloseClient -> CParty::DelMember). Port of CParty::DelMember/ChangeLeader
    /// (Party.cpp:246,599+): if the party is left with &lt;=1 member after the removal it is dissolved entirely
    /// (same threshold as the original: going from 2 to 1 member always dissolves the party); if not, the new
    /// leader is automatically whoever is left in slot 0 (List&lt;int&gt;.Remove already shifts the remaining
    /// indices, there is no need for a separate "promotion" step as in the original's flat array with gaps).
    /// </summary>
    private async Task RemovePlayerFromPartyAsync(PlayerObject player, CancellationToken ct, bool notifyRemoved = true)
    {
        if (player.PartyNumber == -1 || !_parties.TryGet(player.PartyNumber, out var group))
        {
            player.PartyNumber = -1;
            return;
        }

        group.MemberIndices.Remove(player.Index);
        player.PartyNumber = -1;

        if (notifyRemoved && player.Session.Connected)
        {
            await player.Session.SendAsync(PartyPacketBuilder.PartyDelMemberSend(), ct);
        }

        if (group.MemberIndices.Count <= 1)
        {
            foreach (var remainingIndex in group.MemberIndices.ToList())
            {
                if (_players.TryGet(remainingIndex, out var remaining))
                {
                    remaining.PartyNumber = -1;
                    await remaining.Session.SendAsync(PartyPacketBuilder.PartyDelMemberSend(), ct);
                }
            }

            _parties.Remove(group.Id);
            return;
        }

        await BroadcastPartyListAsync(group, ct);
    }

    private List<PartyListEntry> BuildPartyListEntries(PartyGroup group)
    {
        var entries = new List<PartyListEntry>();

        for (int i = 0; i < group.MemberIndices.Count; i++)
        {
            if (_players.TryGet(group.MemberIndices[i], out var m))
            {
                entries.Add(new PartyListEntry(m.Name, (byte)i, m.Map, m.X, m.Y, m.Life, m.MaxLife));
            }
        }

        return entries;
    }

    /// <summary>Port of GCPartyListSend: sent to ALL members every time the party composition changes
    /// (join/leave/kick/leader migration).</summary>
    private async Task BroadcastPartyListAsync(PartyGroup group, CancellationToken ct)
    {
        var entries = BuildPartyListEntries(group);
        var packet = PartyPacketBuilder.PartyListSend(1, entries);

        foreach (var entry in entries)
        {
            if (_players.TryGet(group.MemberIndices[entry.Number], out var member))
            {
                await member.Session.SendAsync(packet, ct);
            }
        }
    }

    /// <summary>Port of GCPartyLifeSend (Party.cpp, called periodically from User.cpp:3488) -- invoked from
    /// ViewportTicker every ~2s (see ViewportTicker.TickPartyLifeAsync) for all parties with >1
    /// member.</summary>
    public async Task BroadcastPartyLifeAsync(PartyGroup group, CancellationToken ct)
    {
        var members = new List<(byte Number, uint Life, uint MaxLife)>();
        var sessions = new List<ClientSession>();

        for (int i = 0; i < group.MemberIndices.Count; i++)
        {
            if (!_players.TryGet(group.MemberIndices[i], out var m))
            {
                continue;
            }

            members.Add(((byte)i, m.Life, m.MaxLife));
            sessions.Add(m.Session);
        }

        if (members.Count == 0)
        {
            return;
        }

        var packet = PartyPacketBuilder.PartyLifeSend(members);

        foreach (var s in sessions)
        {
            await s.SendAsync(packet, ct);
        }
    }

    // ---------------------------------------------------------------- Phase 5: friends (first pass) Thin port
    // of GameServer/Friend.cpp: almost all the real logic lives in DataServer (see
    // DataServerProtocolHandler.HandleFriendAsync) -- here the client's request is only repackaged towards
    // DataServer and vice versa, as the original does.

    private async Task OnFriendListRequestClientAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        await _dataServer.SendAsync(
            FriendDataServerPacketBuilder.FriendListRequest((ushort)player.Index, player.Account, player.Name), ct);
    }

    private async Task OnFriendRequestClientAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = FriendRequestClientRecv.Parse(p);

        await _dataServer.SendAsync(
            FriendDataServerPacketBuilder.FriendRequestRequest((ushort)player.Index, player.Account, player.Name, recv.TargetName), ct);
    }

    private async Task OnFriendResultClientAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = FriendResultClientRecv.Parse(p);

        await _dataServer.SendAsync(
            FriendDataServerPacketBuilder.FriendResultRequest((ushort)player.Index, player.Account, player.Name, recv.Result, recv.RequesterName), ct);
    }

    private async Task OnFriendDeleteClientAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = FriendDeleteClientRecv.Parse(p);

        await _dataServer.SendAsync(
            FriendDataServerPacketBuilder.FriendDeleteRequest((ushort)player.Index, player.Account, player.Name, recv.TargetName), ct);
    }

    /// <summary>Callback from DataServerConnection for any packet with head 0xB0 (friends) -- dispatches by
    /// sub-code just like HandleF3Async on the client side.</summary>
    public async Task OnFriendFromDataServerAsync(byte[] packet, CancellationToken ct)
    {
        byte subh = packet[3];

        switch (subh)
        {
            case 0x00:
            {
                var msg = FriendListFromDataServer.Parse(packet);

                if (_players.TryGet(msg.Index, out var player))
                {
                    var entries = msg.Friends.Select(f => (f.Name, f.Server)).ToList();
                    await player.Session.SendAsync(FriendPacketBuilder.FriendListSend(entries), ct);
                }

                break;
            }

            case 0x02:
            {
                var msg = FriendRequestIncomingFromDataServer.Parse(packet);

                if (_players.TryGet(msg.TargetIndex, out var target))
                {
                    await target.Session.SendAsync(FriendPacketBuilder.FriendRequestSend(1, msg.RequesterName, msg.RequesterServer), ct);
                }

                break;
            }

            case 0x03:
            {
                var msg = FriendResultAckFromDataServer.Parse(packet);

                if (msg.Result == 1 && _players.TryGet(msg.Index, out var accepter))
                {
                    // Confirmation to whoever ACCEPTED: they are now a friend of RequesterName.
                    await accepter.Session.SendAsync(FriendPacketBuilder.FriendResultSend(msg.RequesterName), ct);
                }

                break;
            }

            case 0x04:
            {
                var msg = FriendResultDeliverFromDataServer.Parse(packet);

                if (msg.Result == 1 && _players.TryGet(msg.RequesterIndex, out var requester))
                {
                    // Symmetric notice to whoever sent the ORIGINAL request: it was accepted.
                    await requester.Session.SendAsync(FriendPacketBuilder.FriendResultSend(msg.AccepterName), ct);
                }

                break;
            }

            case 0x05:
            {
                var msg = FriendDeleteResultFromDataServer.Parse(packet);

                if (_players.TryGet(msg.Index, out var player))
                {
                    await player.Session.SendAsync(FriendPacketBuilder.FriendDeleteSend(msg.Result, msg.TargetName), ct);
                }

                break;
            }

            case 0x06:
            {
                var msg = FriendStateFromDataServer.Parse(packet);

                if (_players.TryGet(msg.OwnerIndex, out var owner))
                {
                    await owner.Session.SendAsync(FriendPacketBuilder.FriendStateSend(msg.FriendName, msg.Server), ct);
                }

                break;
            }

            // sub 0x01 (FriendRequestResultFromDataServer, ack to whoever SENT the request) has no dedicated
            // C->G opcode today -- the original does not define one either beyond the short notifications
            // (GCServerMsgSend) that are not ported yet, the same case already documented for whisper (see
            // OnGlobalWhisperResultFromDataServerAsync).
        }
    }

    // ---------------------------------------------------------------- Phase 6: Devil Square (first pass) The
    // whole state/entry/score/reward engine lives in World/DevilSquareManager.cs -- here only the client
    // packets are parsed and delegated, the same pattern as Party/Friend above.

    private async Task OnDevilSquareEnterAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || _devilSquare == null)
        {
            return;
        }

        var recv = DevilSquareEnterRecv.Parse(p);
        await _devilSquare.HandleEnterAsync(session, player, recv.Level, recv.Slot, ct);
    }

    private async Task OnEventRemainTimeAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered || _devilSquare == null)
        {
            return;
        }

        var recv = EventRemainTimeRecv.Parse(p);
        await _devilSquare.HandleRemainTimeQueryAsync(session, player, recv.EventType, ct);
    }

    // ---------------------------------------------------------------- Phase 4: Chaos Machine (Chaos Box / Combinations)

    /// <summary>CURRENT purchase price of an item -- the same formula the shop uses (<see
    /// cref="ComputeShopBuyPrice"/>), reused by <see cref="ChaosMixLogic"/> for the mixes that add up the value
    /// of what is in the Chaos Box. An item with no balance row (it should not happen for anything entering the
    /// box, but just in case) is worth 0 instead of blowing up.</summary>
    private int GetChaosBoxItemBuyMoney(Item item)
    {
        var info = _itemBalance.Get(item.Index);
        return (int)ComputeShopBuyPrice(item, info);
    }

    private async Task OnChaosMixRecvAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || !player.InChaosBox) return;

        byte mixTypeByte = p[3];
        var mixType = (ChaosMixType)mixTypeByte;

        var result = ChaosMixLogic.CalculateAndExecuteMix(player, mixType, _chaosMixRates,
            _addLuckSuccessRate2, GetChaosBoxItemBuyMoney);

        if (result.Success && result.DeliverViaInventory && result.Item != null)
        {
            // The original delivers these items through GDCreateItemSend (a message to DataServer, not the same
            // packet as the mix) -- here the equivalent is the same path the "item" debug command already uses:
            // find a free slot and notify via ItemMoveSend. The PMSG_CHAOS_MIX_SEND below does not carry the
            // item in this case (see the doc-comment of ChaosMixResult.DeliverViaInventory); result 1 anyway so
            // that the window knows something happened and goes to look at the inventory.
            var info = _itemBalance.Get(result.Item.Index);
            int width = info?.Width ?? 1;
            int height = info?.Height ?? 1;

            if (TryFindEmptyInventoryRect(player, width, height, out int slot))
            {
                player.SetItem(slot, result.Item);
                await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, (byte)slot, result.Item), ct);
            }
        }

        var embeddedItem = result.DeliverViaInventory ? null : result.Item;
        await session.SendAsync(ChaosBoxPacketBuilder.ChaosMixSend(result.ResultCode, embeddedItem), ct);
        await session.SendAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
        await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        await SaveCharacterAsync(player, ct);
    }

    private async Task OnChaosMixCloseAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.InChaosBox) return;

        bool returned = await ReturnChaosBoxItemsToInventoryAsync(session, player, ct);
        player.InChaosBox = false;

        await session.SendAsync(ChaosBoxPacketBuilder.ChaosMixCloseSend(), ct);

        if (!returned)
        {
            await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        }
    }

    /// <summary>Returns to the inventory whatever was left in the Chaos Box and empties it. Returns true if it
    /// moved something (and already sent the updated inventory list to the client). <para>If the inventory is
    /// full the item stays in the box instead of being discarded: losing an item is much worse than leaving the
    /// window with something inside, and the warehouse guard (<c>CheckItemInChaosBox</c>) already prevents that
    /// from enabling having the same item in two places.</para></summary>
    private async Task<bool> ReturnChaosBoxItemsToInventoryAsync(ClientSession session, PlayerObject player, CancellationToken ct)
    {
        bool movedAny = false;

        for (int i = 0; i < player.ChaosBoxItems.Length; i++)
        {
            var item = player.ChaosBoxItems[i];

            if (!item.IsItem())
            {
                continue;
            }

            if (TryFindEmptyInventoryRect(player, 1, 1, out int freeSlot))
            {
                player.SetItem(freeSlot, item);
                player.ChaosBoxItems[i] = Item.Empty();
                player.ChaosBoxMap[i] = 0xFF;
                movedAny = true;
            }
        }

        if (movedAny)
        {
            await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        }

        return movedAny;
    }

    /// <summary>0x88: ask the success rate before deciding whether to combine. It passes <c>execute: false</c>
    /// -- it used to call <see cref="ChaosMixLogic.CalculateAndExecuteMix"/> without that, which is the same
    /// function that executes the real combination (0x86): every time a client asked for the rate, it charged
    /// the zen, emptied the Chaos Box and rolled the dice as if it had already combined. This client (MuMain)
    /// does not call this opcode yet -- its combine window is Season 6's (mix.bmd recipes, never loaded in
    /// 0.99B) and I did not port it -- but the bug existed all the same for any other 0.99B client that does
    /// ask before combining.</summary>
    private async Task OnChaosMixRateAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.InChaosBox) return;

        var reader = new PacketReader(p, 3);
        uint mixTypeInt = reader.ReadUInt32();
        var mixType = (ChaosMixType)mixTypeInt;

        var result = ChaosMixLogic.CalculateAndExecuteMix(player, mixType, _chaosMixRates,
            _addLuckSuccessRate2, GetChaosBoxItemBuyMoney, execute: false);
        await session.SendAsync(ChaosBoxPacketBuilder.ChaosMixRateSend(result.SuccessRate, result.RequiredZen), ct);
    }

    private async Task OnBloodCastleEnterAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered || _bloodCastle == null) return;

        byte level = p[3];
        byte cloakSlot = p[4];

        await _bloodCastle.HandleEnterAsync(session, player, level, cloakSlot, ct);
    }

    private async Task OnGuildMasterOpenAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        if (player.Level < 100)
        {
            await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("You need level 100 to create a Guild.")), ct);
            return;
        }

        // C1:55 -- Opens the Guild creation window in the main.exe client
        await session.SendAsync(PacketBuilder.BuildC1(0x55, Array.Empty<byte>()), ct);
    }

    private async Task OnGuildCreateAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        if (p.Length < 11) return;

        string guildName = System.Text.Encoding.Latin1.GetString(p, 3, Math.Min(8, p.Length - 3)).TrimEnd('\0');
        if (guildName.Length < 3)
        {
            // C1:56:02 -- Nombre demasiado corto
            await session.SendAsync(PacketBuilder.BuildC1(0x56, new byte[] { 2 }), ct);
            return;
        }

        byte[] logo = new byte[32];
        if (p.Length >= 11 + 32)
        {
            Array.Copy(p, 11, logo, 0, 32);
        }

        player.GuildName = guildName;
        player.GuildStatus = 0x80; // Master
        player.RebuildCharSet();

        // Enviar guardado a DataServer
        var w = new PacketWriter();
        w.WriteUInt16((ushort)player.Index);
        Span<byte> gBytes = stackalloc byte[8];
        gBytes.Clear();
        System.Text.Encoding.Latin1.GetBytes(guildName, gBytes);
        w.WriteBytes(gBytes.ToArray(), 8);
        Span<byte> mBytes = stackalloc byte[10];
        mBytes.Clear();
        System.Text.Encoding.Latin1.GetBytes(player.Name, mBytes);
        w.WriteBytes(mBytes.ToArray(), 10);
        w.WriteBytes(logo, 32);

        await _dataServer.SendAsync(PacketBuilder.BuildC2Sub(0xA0, 0x00, w.ToArray()), ct);

        // C1:56:01 -- Guild created successfully
        await session.SendAsync(PacketBuilder.BuildC1(0x56, new byte[] { 1 }), ct);
        await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.F("Guild [{0}] created successfully!", guildName)), ct);

        await OnGuildListAsync(session, ct);
    }

    private async Task OnGuildListAsync(ClientSession session, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        if (string.IsNullOrEmpty(player.GuildName))
        {
            var wNoGuild = new PacketWriter();
            wNoGuild.WriteByte(0);
            wNoGuild.WriteByte(0);
            wNoGuild.WriteUInt32(0);
            wNoGuild.WriteByte(0);
            await session.SendAsync(PacketBuilder.BuildC1(0x52, wNoGuild.ToArray()), ct);
            return;
        }

        var w = new PacketWriter();
        w.WriteByte(1);
        w.WriteByte(1);
        w.WriteUInt32(0);
        w.WriteByte(0);

        byte[] nameBytes = new byte[10];
        System.Text.Encoding.Latin1.GetBytes(player.Name).CopyTo(nameBytes, 0);
        w.WriteBytes(nameBytes, 10);
        w.WriteByte(0);
        w.WriteByte(0x80);
        w.WriteByte(player.GuildStatus != 0 ? player.GuildStatus : (byte)0x80);

        await session.SendAsync(PacketBuilder.BuildC1(0x52, w.ToArray()), ct);
    }

    /// <summary> EXACT port of CQuest::CGQuestStateRecv (Quest.cpp:233-290) -- "accept/continue" button of the
    /// quest dialog. It replaces an earlier version that hardcoded two ad-hoc quests (Emperor's Scroll / a
    /// class-specific item with level 150 and guessed items) with the real data-driven engine: <see
    /// cref="Config.QuestTable.GetInfoByIndex"/> finds the row that matches the requested INDEX AND the
    /// player's current stored state (if there is none, the original answers nothing -- <c>lpInfo==0 -&gt;
    /// return</c> -- replicated here); if there is one, the objective (Zen or item, according to
    /// <c>QuestObjective.txt</c>) is validated, charged/consumed, the reward is applied (<c>QuestReward.txt</c>
    /// -- this is where the real 2nd-class change lives, type CHANGE1) and the state advances
    /// (NORMAL-&gt;ACCEPT-&gt;FINISH, or CANCEL-&gt;ACCEPT). </summary>
    private async Task OnQuestStateAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;
        if (player == null || !player.WorldEntered) return;

        byte questIndex = p.Length > 3 ? p[3] : (byte)0;
        byte reqState = p.Length > 4 ? p[4] : (byte)0;

        byte curState = QuestTable.GetQuestState(player.Quest!, questIndex);
        Log.Add(LogColor.Red, "[Quest][{0}] OnQuestState RECV: packet={1} questIndex={2} reqState={3} curState={4} level={5} class={6} changeUp={7}",
            player.Name, BitConverter.ToString(p), questIndex, reqState, curState, player.Level, player.Class, player.ChangeUp);

        var info = _quests.GetInfoByIndex(questIndex, player.Quest!, player.Level, player.Class, player.ChangeUp);

        if (info == null)
        {
            Log.Add(LogColor.Red, "[Quest][{0}] OnQuestState: GetInfoByIndex was NULL for questIndex={1} curState={2}!", player.Name, questIndex, curState);
            return;
        }

        Log.Add(LogColor.Green, "[Quest][{0}] OnQuestState: Match info! Index={1} CurrentState={2}", player.Name, info.Index, info.CurrentState);

        if (!CheckQuestObjective(player, info.Index))
        {
            Log.Add(LogColor.Red, "[Quest][{0}] OnQuestState: CheckQuestObjective returned FALSE for questIndex={1}!", player.Name, info.Index);
            await session.SendAsync(QuestPacketBuilder.QuestResultSend((byte)info.Index, 0xFF, QuestTable.GetQuestState(player.Quest!, info.Index)), ct);
            return;
        }

        byte newState = (byte)(info.CurrentState switch
        {
            0 => 1,
            1 => 2,
            2 => 2,
            3 => 1,
            _ => info.CurrentState,
        });

        await RemoveQuestObjectiveAsync(player, info.Index, session, ct);
        await InsertQuestRewardAsync(player, info.Index, session, ct);

        QuestTable.SetQuestState(player.Quest!, info.Index, newState);
        await SaveCharacterAsync(player, ct);

        byte result = info.CurrentState == 2 ? (byte)0xFF : (byte)0x00;
        await session.SendAsync(QuestPacketBuilder.QuestResultSend((byte)info.Index, result, newState), ct);

        Log.Add(LogColor.Green, "[Quest][{0}] OnQuestState SUCCESS: index={1} {2}->{3} (result=0x{4:X2})",
            player.Name, info.Index, info.CurrentState, newState, result);
    }

    private bool CheckQuestObjective(PlayerObject player, int questIndex)
    {
        foreach (var info in _questObjectives.Entries)
        {
            if (info.RequireIndex != questIndex)
            {
                continue;
            }

            if (!QuestObjectiveTable.CheckRequisite(info, player.Quest!, player.Class, player.ChangeUp))
            {
                continue;
            }

            long currentCount = GetQuestObjectiveCount(player, info);
            Log.Add(LogColor.Red, "[Quest][{0}] CheckQuestObjective: objType={1} objIndex={2} currentCount={3} reqQuantity={4}",
                player.Name, info.Type, info.Index, currentCount, info.Quantity);

            if (currentCount < info.Quantity)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Puerto EXACTO de CQuestObjective::GetQuestObjectiveCount (QuestObjective.cpp:110-121).</summary>
    private static long GetQuestObjectiveCount(PlayerObject player, QuestObjectiveInfo info) => info.Type switch
    {
        QuestObjectiveType.Item => CountInventoryItem(player, info.Index, info.Level),
        QuestObjectiveType.Money => player.Money,
        _ => 0,
    };

    /// <summary>Simplified port of CItemManager::GetInventoryItemCount (ItemManager.cpp:548-571) -- ONLY the
    /// non-stackable branch (counts whole instances). The 4 real quest items (471-474) are not stackable in
    /// Item.txt, so this simplification does not change the result for the only real consumer of this function;
    /// the stackable branch (counting by Durability) is not ported because this build does not model item
    /// stacks anywhere else yet.</summary>
    private static int CountInventoryItem(PlayerObject player, int index, int level)
    {
        int count = 0;

        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            var item = player.Items[slot];

            if (item.IsItem() && item.Index == index && (level <= 0 || item.Level == level))
            {
                count++;
            }
        }

        Log.Add(LogColor.Blue, "[Quest][{0}] CountInventoryItem: looking for index={1} (level={2}) -> found {3}",
            player.Name, index, level, count);

        if (count == 0)
        {
            for (int slot = 0; slot < Item.InventorySize; slot++)
            {
                var it = player.Items[slot];
                if (it.IsItem())
                {
                    Log.Add(LogColor.Blue, "[Quest][{0}] Inventory slot {1}: index={2} level={3} dur={4}",
                        player.Name, slot, it.Index, it.Level, it.Durability);
                }
            }
        }

        return count;
    }

    /// <summary>Puerto EXACTO de CQuestObjective::RemoveQuestObjective (QuestObjective.cpp:184-211).</summary>
    private async Task RemoveQuestObjectiveAsync(PlayerObject player, int questIndex, ClientSession session, CancellationToken ct)
    {
        foreach (var info in _questObjectives.Entries)
        {
            if (info.RequireIndex != questIndex)
            {
                continue;
            }

            if (!QuestObjectiveTable.CheckRequisite(info, player.Quest!, player.Class, player.ChangeUp))
            {
                continue;
            }

            if (info.Type == QuestObjectiveType.Item)
            {
                await DeleteInventoryItemAsync(player, info.Index, info.Level, info.Quantity, session, ct);
                continue;
            }

            if (info.Type == QuestObjectiveType.Money)
            {
                player.Money = player.Money >= (uint)info.Quantity ? player.Money - (uint)info.Quantity : 0;
                await session.SendEncryptedAsync(ItemPacketBuilder.MoneySend(player.Money), ct);
                continue;
            }
        }
    }

    private static async Task DeleteInventoryItemAsync(PlayerObject player, int index, int level, int quantity, ClientSession session, CancellationToken ct)
    {
        for (int slot = 0; slot < Item.InventorySize && quantity > 0; slot++)
        {
            var item = player.Items[slot];

            if (item.IsItem() && item.Index == index && (level <= 0 || item.Level == level))
            {
                player.SetItem(slot, Item.Empty());
                await session.SendAsync(ItemPacketBuilder.ItemDeleteSend((byte)slot, 1), ct);
                quantity--;
            }
        }
    }

    /// <summary>EXACT port of CQuestReward::InsertQuestReward (QuestReward.cpp:116-171). The CHANGE1 type is
    /// the real change from 1st to 2nd class (see <see cref="PlayerObject.ChangeUp"/>); HERO uses the real
    /// <c>PlusStatMinLevel</c>/<c>PlusStatPoint</c> from Common.dat (<see cref="_gsiCommon"/>, already loaded
    /// from before but without a consumer -- this is the first).</summary>
    private async Task InsertQuestRewardAsync(PlayerObject player, int questIndex, ClientSession session, CancellationToken ct)
    {
        foreach (var info in _questRewards.Entries)
        {
            if (info.RequireIndex != questIndex)
            {
                continue;
            }

            if (!QuestRewardTable.CheckRequisite(info, player.Quest!, player.Class, player.ChangeUp))
            {
                continue;
            }

            if (info.Type == QuestRewardType.Point)
            {
                player.LevelUpPoint += (uint)Math.Max(info.Quantity, 0);
                await session.SendAsync(QuestPacketBuilder.QuestRewardSend(player.Index, (byte)info.Index, (byte)Math.Clamp(info.Quantity, 0, 255), player.LevelUpPoint), ct);
                continue;
            }

            if (info.Type == QuestRewardType.Change1)
            {
                if (player.ChangeUp < 1)
                {
                    player.ChangeUp = 1;
                }

                player.RebuildCharSet();
                player.RecalcCombatStats(_itemBalance, _characterBalance);

                // Exact port of QuestReward.cpp:147-149 (byte arithmetic with intentional overflow).
                byte classByte = (byte)(player.ChangeUp * 16);
                classByte -= (byte)(classByte / 32);
                classByte += (byte)(player.Class * 32);

                await session.SendAsync(QuestPacketBuilder.QuestRewardSend(player.Index, (byte)info.Index, classByte, player.LevelUpPoint), ct);
                await session.SendAsync(WorldPacketBuilder.NewCharacterCalcSend(player), ct);
                await session.SendAsync(ItemPacketBuilder.ItemEquipmentSend(player), ct);
                await session.SendAsync(ChatPacketBuilder.NoticeSend(Loc.T("Congratulations! You have evolved to the 2nd Class.")), ct);
                continue;
            }

            if (info.Type == QuestRewardType.Hero)
            {
                int minLevel = _gsiCommon?.PlusStatMinLevel ?? 0;
                int perLevel = _gsiCommon?.PlusStatPoint ?? 0;
                int addPoint = (player.Level > minLevel ? player.Level - minLevel : 0) * perLevel;
                player.LevelUpPoint += (uint)Math.Max(addPoint, 0);
                await session.SendAsync(QuestPacketBuilder.QuestRewardSend(player.Index, (byte)info.Index, (byte)Math.Clamp(addPoint, 0, 255), player.LevelUpPoint), ct);
                continue;
            }

            if (info.Type == QuestRewardType.Combo)
            {
                await session.SendAsync(QuestPacketBuilder.QuestRewardSend(player.Index, (byte)info.Index, (byte)Math.Clamp(info.Quantity, 0, 255), player.LevelUpPoint), ct);
                continue;
            }
        }
    }
}

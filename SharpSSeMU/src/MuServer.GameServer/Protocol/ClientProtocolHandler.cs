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

/// <summary>
/// Puerto de ProtocolCore (Protocol.cpp) — Fase 1 (login) + Fase 2 (selección de personaje, entrar
/// al mundo, viewport de jugadores, movimiento). El resto de los códigos (chat, items, ataque,
/// gremios...) se agregan en fases posteriores; por ahora solo se loguean si llegan.
/// </summary>
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

    /// <summary>Puerto de MAX_PARTY_DISTANCE (Party.h) -- rango (en tiles, no viewport) usado para
    /// decidir qué miembros del grupo participan del reparto de experiencia al matar un monstruo.</summary>
    private const int MaxPartyDistance = 10;

    /// <summary>Puerto de <c>gServerInfo.m_ItemDropTime</c> (<c>GameServerInfo - Common.dat</c>, ver
    /// <see cref="ServerInfoConfig.ItemDropTimeSeconds"/>) -- tiempo de vida de un item tirado
    /// en el piso antes de desaparecer solo. Calculado (no <c>readonly</c>) para leer el valor real
    /// seteado en <c>WorldPacketBuilder.ServerInfo</c> por Program.cs al arrancar.</summary>
    private static TimeSpan GroundItemLifetime => TimeSpan.FromSeconds(WorldPacketBuilder.ServerInfo.ItemDropTimeSeconds);

    /// <summary>Puerto exacto de <c>m_ItemDropTime*500</c> (MapItem.cpp:29-99) -- la mitad del tiempo
    /// de vida total en milisegundos, mismo cálculo que el original.</summary>
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

    /// <summary>
    /// Puerto de ObjectManager.cpp (bloque OBJECT_USER de CloseClient/DelClient): SIEMPRE avisa a
    /// JoinServer de la desconexión (GJDisconnectAccountSend), incluso si la cuenta quedó vacía (login
    /// no llegó a completarse) — el original lo manda incondicionalmente para el slot de usuario, no
    /// solo tras un login exitoso. Sin esto, JoinServer sigue creyendo la cuenta "conectada" y el
    /// siguiente intento de login del mismo cliente devuelve resultado 3 (ya conectada) en vez del
    /// resultado real. Si el jugador había entrado al mundo, también se lo saca del registro y se
    /// avisa a DataServer (0x71) para que libere el slot en memoria.
    /// </summary>
    public async Task OnDisconnectAsync(ClientSession session, CancellationToken ct)
    {
        _pendingLogins.TryRemove(session.Index, out _);
        _pendingCharacterInfo.TryRemove(session.Index, out _);
        _pendingCharacterList.TryRemove(session.Index, out _);
        _pendingCharacterCreate.TryRemove(session.Index, out _);

        if (session.Player != null)
        {
            // Puerto de CloseClient -> CParty::DelMember (el original también saca al jugador de su
            // grupo al desconectarse, no solo cuando manda PMSG_PARTY_DEL_MEMBER_RECV explícito).
            // notifyRemoved=false: no tiene sentido mandarle un paquete a un socket que se está cerrando.
            await RemovePlayerFromPartyAsync(session.Player, ct, notifyRemoved: false);

            _players.Remove(session.Index);

            // Puerto exacto del orden real (ObjectManager.cpp:623-627, DelCharacterInfo):
            // GDCharacterInfoSaveSend SIEMPRE se manda justo ANTES de GDDisconnectCharacterSend, sin
            // excepción ni throttle -- es el guardado incondicional de "me estoy yendo, grabá mi
            // estado actual ya" (a diferencia del guardado periódico/por combate, que sí tienen
            // throttle). Ver doc-comment de DataServerCharacterPacketBuilder.CharacterInfoSaveSend.
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

    /// <summary>
    /// Puerto de GDCharacterInfoSaveSend (DSProtocol.cpp:991-1047): manda a DataServer el snapshot
    /// completo del personaje (stats, inventario, skill, quest, efectos, PK, y crucialmente
    /// Map/X/Y/Dir) para que <c>NpgsqlCharacterDataRepository.SaveCharacterAsync</c> lo persista.
    /// Sin llamar a esto en algún lado, la fila de <c>character</c> nunca se actualiza después de
    /// creada -- este método es el ÚNICO productor del paquete C2:0x30, así que cualquier llamador
    /// nuevo (comandos GM, Trade, PersonalShop, etc. cuando se porten) debería reusarlo en vez de
    /// construir el paquete a mano. Sin throttle acá adentro a propósito -- cada llamador decide su
    /// propia condición (incondicional en desconexión, 60s en <see cref="ApplyExperienceGainAsync"/>,
    /// 10min en el autoguardado periódico de <see cref="World.ViewportTicker"/>), igual que el
    /// original reparte esa lógica entre los call sites en vez de centralizarla.
    /// </summary>
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

                // El zen del trade es 0x3A: así lo despacha el original (Protocol.cpp:124,
                // CGTradeMoneyRecv) y así lo declara el header generado desde esas fuentes
                // (PMSG_TRADE_MONEY_RECV::kHead). El 0x3B viene de un comentario equivocado en
                // Trade.h del original ("// C1:3B"), que se transcribió a mano tanto acá como en el
                // builder del cliente (Wire099B.cpp), así que cliente y servidor de este proyecto se
                // entendían entre ellos pero ninguno de los dos hablaba el protocolo real. Se aceptan
                // los dos para no romper los clientes ya compilados con el valor viejo.
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
                    // Puerto de CGSkillCancelRecv (SkillManager.cpp:2591-2601): en el original cancela
                    // un efecto activo del skill (gEffectManager.DelEffect) -- este puerto no tiene
                    // gestor de efectos todavía (los duration-skills de 0x1E son sólo la animación/
                    // proyectil, sin daño-en-el-tiempo persistente que cancelar), así que no hay nada
                    // que deshacer y el no-op es fiel al alcance actual, no un placeholder. Se agrega el
                    // case explícito (en vez de caer al default) para no ensuciar el log con "no
                    // implementado" por un paquete que en los hechos ya está atendido.
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
                // PMSG_CHARACTER_MOVE_VIEWPORT_ENABLE -- sin cuerpo. Puerto EXACTO de
                // CGCharacterMoveViewportEnableRecv (Protocol.cpp:1300-1310): en el original esto
                // SOLO hace "RegenOk = (RegenOk==1) ? 2 : RegenOk" -- es decir, es un ack de que el
                // cliente terminó de cargar el mapa DESPUÉS de un teleport/gate (que es lo único que
                // pone RegenOk en 1, ver User.cpp:2114/2144/2168/2220/2259). En el login inicial
                // RegenOk ya vale 0 desde gObjCharZeroSet (User.cpp:368, llamado en gObjAdd al
                // aceptar la conexión) y esta recepción es un no-op. Como el mundo real todavía no
                // tiene portales/gates portados en este build (ver World/DevilSquareManager.cs, el
                // único lugar que manda TeleportSend, tampoco setea RegenOk=true), este handler queda
                // como no-op idempotente -- ver PlayerObject.RegenOk para el default correcto.
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

    /// <summary>Puerto de CGLevelUpPointRecv (Protocol.cpp:1253-1298) + CObjectManager::
    /// CharacterLevelUpPointAdd (ObjectManager.cpp:1043-1090) -- agrega 1 punto de level-up a un stat
    /// (type: 0=Strength,1=Dexterity,2=Vitality,3=Energy,4=Leadership). Dispara un recálculo completo
    /// de combate igual que el original (CharacterCalcAttribute se llama synchronamente tras aplicar
    /// el punto). El tope usa CharacterBalanceConfig.MaxStatPoint[player.AccountLevel] -- ver
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
            return; // el original ignora reintentos del mismo login mientras uno está en curso
        }

        session.LoginMessageSent = true;
        session.Account = recv.Account;

        _pendingLogins[session.Index] = session;

        await _joinServer.SendAsync(
            JoinServerClientPacketBuilder.ConnectAccountSend((ushort)session.Index, recv.Account, recv.Password, session.IpAddress),
            ct);
    }

    /// <summary>Callback desde JoinServerConnection cuando llega el resultado del login (0x01 JGConnectAccountRecv).</summary>
    public async Task OnJoinAccountResultAsync(JoinAccountResultRecv msg, CancellationToken ct)
    {
        if (!_pendingLogins.TryGetValue(msg.Index, out var session) || !session.Connected)
        {
            return;
        }

        // result: 1=ok, 0=cuenta/clave inválida, 2=ya conectada, 3=llena, 4=bloqueada (ver
        // JoinServerProtocolHandler.OnConnectAccountAsync, que ya implementa este mismo orden).
        await session.SendAsync(ClientPacketBuilder.ConnectAccountSend(msg.Result), ct);

        // Puerto de JGConnectAccountRecv (JSProtocol.cpp:85): gObj[index].AccountLevel = AccountLevel,
        // sin clamp en el original porque confía en lo que ya validó WZ_GetAccountLevel. Acá se
        // recorta a 0-3 igual como red de seguridad -- los arrays de tasas de este puerto (AddExperienceRate,
        // MoneyAmountDropRate, MaxStatPoint, etc.) son arrays C# de largo fijo 4, así que un valor fuera
        // de rango tiraría un IndexOutOfRange en vez de leer una fila inventada como haría el original.
        session.AccountLevel = Math.Clamp((int)msg.AccountLevel, 0, 3);

        Log.Add(LogColor.Blue, "[Protocol][{0}] Login '{1}' -> result={2}", msg.Index, session.Account, msg.Result);
    }

    /// <summary>Puerto de CGCharacterListRecv (Protocol.cpp:1134-1144) -- pide a DataServer la
    /// lista de personajes de la cuenta para la pantalla de selección. El cliente real la manda
    /// automáticamente apenas el login da result=1, ANTES de elegir personaje (0xF3:0x03); sin
    /// esto se queda esperando para siempre en la pantalla de selección -- WorldTestClient no lo
    /// necesitaba porque simula un cliente que ya "sabe" el nombre y manda 0xF3:0x03 directo.</summary>
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

    /// <summary>
    /// Callback desde DataServerConnection cuando llega SDHP_CHARACTER_LIST_RECV (0x01). Puerto de
    /// DGCharacterListRecv (DSProtocol.cpp:181-365): convierte el Inventory compacto de 60 bytes de
    /// cada personaje a CharSet[13] reconstruyendo items reales con Item.FromCompactPreviewBytes +
    /// PlayerObject.BuildCharSet -- el mismo camino que ya usa un jugador una vez dentro del mundo
    /// (RebuildCharSet), solo que alimentado desde el formato compacto en vez de Items[] real -- y
    /// manda PMSG_CHARACTER_LIST_SEND al cliente.
    /// </summary>
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

            // entry.Class viene en formato crudo de DB (0/16/32/48/64 = DW/DK/FE/MG/DL, con el nibble
            // bajo reservado para ChangeUp -- ver el mismo patrón ya portado en
            // OnCharacterCreateResultFromDataServerAsync). BuildCharSet necesita el índice compacto
            // 0-4 (Class/16) y el ChangeUp real (Class%16), NO la clase cruda con changeUp=0 fijo --
            // CORREGIDO: antes se pasaba entry.Class directo, lo que hacía overflow de byte en
            // cls*32 para cualquier clase que no fuera DW (0*32=0 por casualidad coincide con el
            // caso vacío) y mostraba a TODOS los personajes como Dark Wizard en la pantalla de
            // selección.
            var charSet = PlayerObject.BuildCharSet((byte)(entry.Class / 16), (byte)(entry.Class % 16), wear);
            characters.Add(new CharacterListItem(entry.Slot, entry.Name, entry.Level, entry.CtlCode, charSet));
        }

        await session.SendAsync(ClientPacketBuilder.CharacterListSend(msg.MoveCnt, characters), ct);

        Log.Add(LogColor.Blue, "[Protocol][{0}] Character list sent ({1} character(s))", msg.Index, characters.Count);
    }

    /// <summary>Puerto de CGCharacterCreateRecv (Protocol.cpp:2095-2153) -- pide a DataServer crear
    /// el personaje. La validación previa de clase/CARD_CODE del original no se replica acá (ver
    /// comentario de <c>DataServerCharacterPacketBuilder.CharacterCreateRequest</c>): se reenvía
    /// directo y DataServer, que ya es la autoridad real sobre qué clases existen
    /// (<c>default_class_type</c>), rechaza cualquier clase no habilitada con result=2.</summary>
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

    /// <summary>
    /// Callback desde DataServerConnection cuando llega SDHP_CHARACTER_CREATE_RECV (0x02). Puerto de
    /// DGCharacterCreateRecv (DSProtocol.cpp:1148-1176): aplica la misma conversión de bytes de Class
    /// (formato de almacenamiento en DB -&gt; formato que espera el cliente) y manda
    /// PMSG_CHARACTER_CREATE_SEND. El cliente real reacciona a esto refrescando la pantalla de
    /// selección -- normalmente sigue pidiendo la lista actualizada (0xF3:0x00) por su cuenta.
    /// </summary>
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

    /// <summary>Puerto de CConnectionManager::CGHardwareIdRecv (ConnectionManager.cpp:132-162) --
    /// valida que el HardwareId tenga formato de GUID (44 chars, '-' en las posiciones 8/17/26/35)
    /// y desconecta si viene mal formado, igual que el original (gObjDel). La blacklist de HWID y
    /// el chequeo de duplicados de cuenta (CheckHardwareId) no están portados todavía -- de momento
    /// se acepta cualquier HWID bien formado sin más validación (no afecta el flujo normal, solo no
    /// detecta multi-cuentas todavía).</summary>
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

    /// <summary>
    /// Callback desde DataServerConnection cuando llega SDHP_CHARACTER_INFO_RECV (0x04). Puerto de
    /// DGCharacterInfoRecv (DSProtocol.cpp): puebla el estado del jugador, lo marca online, y manda
    /// los paquetes de entrada al mundo. La visibilidad mutua con otros jugadores no es instantánea
    /// acá tampoco -- se resuelve en el próximo tick de ViewportTicker (igual que el original, ver
    /// nota en gObjViewportListProtocolCreate del brief de investigación).
    /// </summary>
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
            // msg.Class viene en formato crudo de DB (0/16/32/48/64 = DW/DK/FE/MG/DL + ChangeUp en el
            // nibble bajo) -- ver el mismo patrón en OnCharacterListFromDataServerAsync más arriba.
            // Todo el resto del puerto (RecalcCombatStats, DevilSquareManager, ViewportTicker,
            // CharacterBalanceConfig, BuildCharSet) espera el índice compacto 0-4, así que se
            // descompone acá, en el único punto de entrada real. CORREGIDO: antes se guardaba
            // msg.Class crudo y ChangeUp quedaba siempre en 0 -- además de romper CharSet (ver
            // BuildCharSet), esto hacía que las comparaciones Class==ClassFe/ClassMg nunca dieran
            // true para un personaje real (solo Class==0, Dark Wizard, coincidía por casualidad).
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

        // Puerto de gObjSetCharacter (ObjectManager.cpp:2739-2745): un personaje que entra al mundo
        // con Life == 0 se manda al flujo de muerte (OBJECT_DYING + DieRegen), que es el que lo
        // revive. Sin esto el personaje quedaba vivo pero en cero para siempre: se guarda así cuando
        // muere y se desconecta antes de reaparecer, y al volver a entrar nada lo recuperaba --
        // IsDying es estado de runtime, así que RespawnDyingPlayersAsync ni lo miraba. Con 0 de vida
        // los monstruos además lo ignoran por completo (la IA filtra Life > 0), así que el personaje
        // quedaba en un limbo: no podía pelear y nada lo atacaba.
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

        // Puerto de DGCharacterInfoRecv (DSProtocol.cpp:414-499): la lista completa de inventario
        // (C4:F3:10) se manda como paquete aparte, después de CHARACTER_INFO/NEW_CHARACTER_INFO.
        await session.SendEncryptedC4Async(ItemPacketBuilder.ItemListSend(player), ct);
        await SendSkillListAsync(player, ct);

        // Puerto de DSProtocol.cpp:503 (DGCharacterInfoRecv): gQuest.GCQuestInfoSend(lpObj->Index) se
        // manda PROACTIVAMENTE al entrar al mundo, junto con el resto de la ráfaga inicial (item/skill
        // list). Antes este puerto no lo mandaba nunca en el world-enter -- solo reactivamente cuando
        // el jugador hablaba con un NPC de misión -- lo que dejaba al cliente sin el blob de 50 bytes
        // de estado de TODAS las misiones antes de tiempo. Se marca SendQuestInfo=true acá para que
        // OnNpcTalkAsync/OnQuestInfoAsync repliquen el guard real (GCQuestInfoSend es no-op después de
        // la primera vez por sesión).
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

    /// <summary>
    /// Puerto simplificado de CGMoveRecv (Protocol.cpp:901-1076): valida contra el mapa (caja
    /// ±15, tiles bloqueados) y difunde PMSG_MOVE_SEND. El anti-speedhack basado en contador de
    /// Protocol.cpp no se porta -- viene apagado por defecto en el paquete original
    /// (CheckMoveHack=0 en GameServerInfo - Common.dat), así que omitirlo no cambia el
    /// comportamiento default.
    /// </summary>
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

        // RoadPathTable (Util.cpp) -- 8 direcciones, dx/dy por par, orden N,NE,E,SE,S,SW,W,NW.
        // (arrays normales, no Span/stackalloc: este método es async y stackalloc no está permitido ahí)
        int[] dx = { -1, 0, 1, 1, 1, 0, -1, -1 };
        int[] dy = { -1, -1, -1, 0, 1, 1, 1, 0 };

        // TX/TY arrancan en el ancla (x,y) del paquete -- el primer nibble de dirección (path[0]>>4)
        // es solo la orientación (Dir), NO desplaza la posición (puerto exacto de Protocol.cpp:980-987:
        // TX=x,TY=y de entrada, recién el loop desde n=1 va sumando offsets de RoadPathTable).
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
            // Corrección: re-mandarle al cliente su posición actual (equivalente a gObjSetPosition).
            await session.SendAsync(WorldPacketBuilder.PositionSend(player.Index, player.X, player.Y), ct);
            return;
        }

        map!.DelStandAttr(player.OldX, player.OldY);

        // BUG corregido: acá antes se guardaba player.X/Y = recv.X/Y (el ANCLA del paquete, o sea la
        // posición de ARRANQUE del movimiento), en vez de la posición de LLEGADA (tx,ty). En el
        // original, lpObj->X (posición confirmada) NO lo toca CGMoveRecv en absoluto (Protocol.cpp:
        // 901-987 solo escribe TX/TY/PathX/PathY/PathCount) -- lpObj->X se actualiza gradualmente,
        // tile por tile, por un tick periódico separado (CObjectManager::ObjectMoveProc,
        // ObjectManager.cpp:419-482, con velocidad distinta según diagonal/recto) a medida que el
        // personaje "camina" visualmente. Este puerto resuelve el movimiento de forma instantánea (sin
        // ese tick de por medio) y NINGÚN otro lugar del código avanza X hacia TX -- por lo tanto X
        // tiene que quedar en la posición de LLEGADA ya mismo, o si no todo lo que usa player.X como
        // "posición actual" (este mismo chequeo de rango en el próximo movimiento, el rango de ataque
        // en OnAttackAsync, el rango de visión de ViewportTicker) queda comparando contra una posición
        // vieja de un movimiento atrás. Ese desfase de "un movimiento de atraso" es exactamente lo que
        // causaba que, tras caminar para atacar a un monstruo, el siguiente movimiento casi siempre
        // cayera fuera del radio de 15 tiles permitido (medido contra la posición vieja) y el servidor
        // le contestara con PositionSend(player.X,player.Y) -- la posición vieja, frecuentemente muy
        // cerca de donde el personaje apareció originalmente -- lo que el cliente real mostraba como
        // "me manda de vuelta a mi sitio de aparición" al intentar acercarse para atacar.
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

    /// <summary>
    /// Puerto simplificado de CGItemMoveRecv (ItemManager.cpp:2853-3025) para el único container
    /// soportado por ahora: Inventory→Inventory (mover de un slot a otro, incluye equipar/
    /// desequipar cuando el slot destino/origen cae en el rango de equipo 0-11). Trade/Warehouse/
    /// ChaosBox/PersonalShop (SourceFlag/TargetFlag distintos de 0) quedan fuera de esta pasada de
    /// la Fase 3 -- se rechazan con result=0xFF en vez de implementarse a medias.
    ///
    /// CORREGIDOS dos bugs reales encontrados esta sesión (causa de "los items desaparecen al
    /// moverlos"):
    ///
    /// 1) <c>result</c> NO es un booleano genérico "0=falla/1=éxito" -- el original
    /// (<c>MoveItemToInventoryFromInventory</c>, ItemManager.cpp:1920-1978) devuelve
    /// <c>TargetFlag</c> (0 para Inventory) en éxito y <c>0xFF</c> en cualquier falla (todas las
    /// ramas de validación devuelven <c>0xFF</c> explícitamente, y <c>pMsg.result</c> arranca en
    /// <c>0xFF</c> por default antes de intentar nada). Este puerto tenía la semántica invertida
    /// (0=falla, 1=éxito) -- con <c>0</c> de "falla" el cliente real probablemente interpreta
    /// "éxito con TargetFlag=Inventory" (ya que solo chequea <c>!= 0xFF</c>), aceptando un move que
    /// el servidor en realidad rechazó, un desincronismo directo entre lo que el cliente cree que
    /// pasó y lo que el servidor realmente hizo.
    ///
    /// 2) <c>InventoryAddItem</c> (ItemManager.cpp:1052-1102) rechaza el move con <c>0xFF</c> si el
    /// slot destino YA tiene un item (<c>if(lpObj->Inventory[slot].IsItem() != 0) return 0xFF;</c>)
    /// -- NO HAY intercambio/swap a nivel de protocolo. <c>PMSG_ITEM_MOVE_SEND</c> solo tiene lugar
    /// para UN item (el que quedó en <c>TargetSlot</c>); si el servidor swapea igual (como hacía
    /// este puerto) el cliente nunca se entera qué pasó con el item que estaba en destino -- lo
    /// pierde de la UI aunque el servidor lo siga trackeando en el slot origen. Corregido: si el
    /// slot destino tiene un item, se rechaza el move entero (igual que el original), sin swap.
    /// </summary>
    private async Task OnItemMoveAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = ItemMoveRecv.Parse(p);

        // Manejo de movimientos de ítems con Trade (Flag 1 = Trade Window)
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

        // Manejo de movimientos de ítems con Warehouse (Flag 2 = Warehouse)
        if (recv.SourceFlag == 0 && recv.TargetFlag == 2) // Inventario -> Baúl
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

        if (recv.SourceFlag == 2 && recv.TargetFlag == 0) // Baúl -> Inventario
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

        if (recv.SourceFlag == 2 && recv.TargetFlag == 2) // Baúl -> Baúl
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

        // Movimiento de Ítems en Chaos Box (Flag 3 = Chaos Box)
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

        // Puerto de CheckItemMoveToInventory (ItemManager.cpp:711-780): valida nivel, fuerza, agilidad,
        // vitalidad, energía, liderazgo, clase del personaje y compatibilidad de slot antes de equipar.
        var info = _itemBalance.Get(sourceItem.Index);

        if (recv.TargetSlot < Item.InventoryWearSize && (info == null || !ItemCombatMath.CheckItemMoveToInventory(player, sourceItem, recv.TargetSlot, info, _itemBalance)))
        {
            await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0xFF, recv.TargetSlot, Item.Empty()), ct);
            return;
        }

        player.SetItem(recv.TargetSlot, sourceItem);
        player.SetItem(recv.SourceSlot, Item.Empty());

        // result = TargetFlag (siempre 0 acá, único container soportado es Inventory) -- puerto
        // exacto de "return TargetFlag;" en MoveItemToInventoryFromInventory.
        await session.SendEncryptedAsync(ItemPacketBuilder.ItemMoveSend(0, recv.TargetSlot, sourceItem), ct);

        bool touchedEquip = recv.SourceSlot < Item.InventoryWearSize || recv.TargetSlot < Item.InventoryWearSize;

        if (!touchedEquip)
        {
            return;
        }

        // Puerto simplificado de CItemManager::UpdateInventoryViewport (ItemManager.cpp:1759-1779):
        // rearmar CharSet, avisarle al propio cliente (ITEM_EQUIPMENT_SEND) y refrescar la
        // apariencia para los observadores actuales re-mandando un VIEWPORT_PLAYER_APPEAR -- el
        // original usa un paquete dedicado de "cambio" de viewport (gObjViewportListProtocolCreate)
        // que no se portó todavía; reaparecer con los datos nuevos logra el mismo resultado visual.
        player.RebuildCharSet();

        // Puerto de la llamada a CharacterCalcAttribute que dispara el original al equipar/desequipar
        // (ObjectManager.cpp, dentro de CGItemMoveRecv) -- el daño/defensa del jugador tienen que
        // reflejar el cambio de inmediato, no solo al volver a entrar al mundo.
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

    /// <summary>
    /// Puerto de CGItemRepairRecv (ItemManager.cpp:3373-3428) -- reparar un ítem o reparar todos (slot=0xFF).
    /// </summary>
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

    /// <summary>
    /// Puerto simplificado de CGItemGetRecv (ItemManager.cpp:3289-3528) -- recoger un item del piso.
    /// Ver World/GroundItem.cs para el modelo de datos. Simplificaciones documentadas (fuera de esta
    /// pasada): sin quest-items/event-items/Muun (CItem::IsEventItem/IsMuunItem, sistemas no
    /// portados), sin el reparto de dinero por grupo completo (acá el que junta la plata se la queda
    /// entero -- el reparto de dinero de grupo, gServerInfo.m_PartyMoneyDistribute, es una config de
    /// servidor no portada), sin apilado de flechas/pociones existentes (InventoryInsertItemStack,
    /// resultado 0xFD -- este puerto no tiene mecánica de apilado, cada slot es un item entero).
    /// </summary>
    private async Task OnItemGetAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = ItemGetRecv.Parse(p);
        var ground = _groundItems?.Get(player.Map, recv.GroundIndex);

        // Puerto de CMap::CheckItemGive (Map.cpp:363-419): rango de 2 tiles en cada eje (una caja de
        // 5x5, no distancia euclídea) + loot-lock (dueño/grupo del dueño hasta que venza LootLockUntil).
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

        // Avisarle a todos los que veían el item de piso que ya no está (CMap::ItemGive lo saca del
        // barrido de viewport de todo el mundo, no solo del que lo recogió) -- el próximo tick de
        // ViewportTicker ya lo hace solo (Live=false), pero mandar el destroy acá también para el
        // que lo recogió evita esperar hasta el próximo tick (~200ms) para que su propio cliente lo
        // borre del piso.
        await session.SendAsync(WorldPacketBuilder.ViewportItemDestroy(new[] { ground.Index }), ct);
    }

    /// <summary>
    /// Puerto simplificado de CGItemDropRecv (ItemManager.cpp:3530-3718) -- tirar un item del
    /// inventario al piso. Simplificaciones documentadas (fuera de esta pasada): sin las reglas
    /// anti-dupe/anti-scam de nivel alto (bloquear +5/+6 no-alas, excelente, set, JewelOfHarmony --
    /// <c>IsExcItem/IsSetItem/IsJewelOfHarmonyItem</c> no relevantes porque este puerto no genera esos
    /// items todavía), sin lucky/periodic-item checks (esos flags siempre están en false/default en
    /// este puerto), sin los ítems especiales con efecto propio (Siege Summon, Life Stone, Lost Map,
    /// etc. -- CGItemDropRecv cadena de casos especiales, ItemManager.cpp:3620-3701).
    /// </summary>
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

    /// <summary>
    /// Puerto de CGItemUseRecv (ItemManager.cpp:3038-3179) -- uso de pociones, town portal scroll,
    /// antídoto y joyas (Bless, Soul, Life).
    /// </summary>
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

        // 1. Pociones HP / MP (Categoría 14, sub 0..6)
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

            // Consumir poción
            player.SetItem(recv.SourceSlot, Item.Empty());
            await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(recv.SourceSlot, 1), ct);
            return;
        }

        // 2. Antídoto (14,8)
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
                successRate += 25; // 75% si tiene Opción de Luck
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

            if (targetItem.Option3 >= 4) return; // Máximo +16 opción (+4 x 4)

            bool success = Rng.Next(100) < 50;

            if (success)
            {
                targetItem.Option3++;
            }
            else
            {
                targetItem.Option3 = 0; // Si falla, se pierde la opción adicional
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

            // Verificar requerimientos de nivel, energía, liderazgo y clase
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

                // Notificar al cliente la adición de la habilidad en la barra
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

    /// <summary>
    /// Puerto simplificado de CAttack::CGAttackRecv + CAttack::Attack (Attack.cpp:44-581) -- SOLO
    /// ataque cuerpo a cuerpo básico de jugador contra monstruo (sin skills, sin PvP, sin combo).
    /// Fórmula de daño fiel a la investigación de esta fase (miss/dodge, defensa, piso de daño por
    /// nivel), salvo por dos simplificaciones documentadas explícitamente:
    ///   1) PhysiDamageMin/Max/Defense/AttackSuccessRate/DefenseSuccessRate del jugador salen de
    ///      PlayerObject.RecalcCombatStats() -- Fase 4 segunda pasada: ya es el puerto real de
    ///      CharacterCalcAttribute + balance real de Item.txt (arma/armadura equipada SÍ importa),
    ///      ver el comentario de esa función para las simplificaciones que quedan (crítico/excelente/
    ///      set-item, velocidad, magia, PvP).
    ///   2) Sin crítico/excelente (dependen de %s que vienen de ItemOption.txt/SetItemOption.txt,
    ///      todavía no portados).
    /// Monstruo-ataca-jugador y la IA de persecución/patrulla quedan para la siguiente pasada de
    /// esta fase (confirmado seguro a nivel de protocolo dejar los monstruos estáticos por ahora,
    /// ver comentario de clase en World/Monster.cs).
    /// </summary>
    /// <summary>Puerto de CGActionRecv (Protocol.cpp:611-666) -- pose/emote/sentarse. Validación
    /// mínima igual que el original (solo "está conectado", sin chequeo de distancia/estado ni rate
    /// limit); persiste dir/ActionNumber en el jugador y reenvía tal cual a quien lo tenga en su
    /// viewport. A diferencia del original, acá también se manda de vuelta al propio emisor (el
    /// original NO hace eco al que lo originó, asume que el cliente reproduce su propia animación
    /// Puerto de CGActionRecv (Protocol.cpp:611-666) -- pose/emote/sentarse. Puerto exacto de
    /// GCActionSend (Protocol.cpp:1488-1507): difunde PMSG_ACTION_SEND (0x18) vía MsgSendV2 ÚNICAMENTE
    /// a los observadores en el viewport del emisor, NUNCA al propio emisor (enviar 0x18 de vuelta
    /// al propio cliente hace que main.exe interrumpa su animación/movimiento local y resetee
    /// posición).
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

    /// <summary>Puerto de CQuest::CGQuestInfoRecv (Quest.cpp:221-231) -&gt; GCQuestInfoSend
    /// (Quest.cpp:397-417): no-op si ya se mandó el blob completo esta sesión (ver
    /// <see cref="PlayerObject.SendQuestInfo"/>, normalmente ya en true por el push proactivo de
    /// world-enter en <c>OnCharacterInfoFromDataServerAsync</c>, DSProtocol.cpp:503).</summary>
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

    /// <summary>Puerto de CGPetItemInfoRecv (Protocol.cpp:797-819) -- solo la rama flag==0
    /// (Inventory); Warehouse/Trade/ChaosBox (flag 1+) no están portados todavía y, igual que el
    /// original ante cualquier validación fallida, simplemente no responden nada (confirmado en la
    /// investigación previa que el cliente real tolera la ausencia de respuesta acá, a diferencia de
    /// QuestInfo).</summary>
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

    /// <summary>Puerto de CNpcTalk::CGNpcTalkRecv (NpcTalk.cpp:1416-1524) -- SOLO la rama "tienda"
    /// (result de <c>NpcTalk()</c> siempre 0 en este puerto, ya que no hay NPCs de quest/clase
    /// especial -- Trainer/Charon/GuildMaster/etc -- implementados, ver README). Simplificaciones
    /// deliberadas frente al original:
    /// <list type="bullet">
    /// <item>El chequeo de distancia real del original es <c>gObjCalcDistance(lpObj,lpObj)</c> --
    /// literalmente la distancia de el jugador A SÍ MISMO, que siempre da 0 y por lo tanto SIEMPRE
    /// pasa. Es un bug del original (probablemente debía ser <c>gObjCalcDistance(lpObj,lpNpc)</c>);
    /// se replica tal cual (sin chequeo real de distancia) para mantener fidelidad de comportamiento.</item>
    /// <item>PKLevel/AccountLevel/GameMasterLevel no se trackean en este puerto todavía, así que los
    /// chequeos <c>m_PKLimitShop</c>/<c>CheckShopGameMasterLevel</c>/<c>CheckShopAccountLevel</c> del
    /// original se omiten (equivale a tenerlos siempre permisivos, el default de un server sin
    /// restricciones configuradas).</item>
    /// <item><c>GCShopItemPriceSendByIndex</c>/<c>GCTaxInfoSend</c> del original NO se portan porque
    /// son no-ops en este build real: <c>GAMESERVER_SHOP==0</c> en stdafx.h (confirmado en el código
    /// fuente) hace que <c>GCShopItemPriceSend</c>/<c>GCShopItemCoinPriceSend</c> compilen vacíos
    /// (todo el sistema es de tiendas con moneda alternativa "Coin", no usado en esta build), y
    /// <c>GCTaxInfoSend</c> es pura UI de impuestos de Castle Siege (sistema no portado). El cliente
    /// real calcula el precio a mostrar en la ventana de tienda con su propia copia de Item.bmd, no
    /// necesita que el servidor se lo mande para tiendas normales de Zen.</item>
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

        // Puerto EXACTO de CQuest::NpcTalk (Quest.cpp:195-219), llamado desde CNpcTalk::NpcTalk ANTES
        // que el switch(lpNpc->Class) de casos especiales (NpcTalk.cpp:55-58) -- reemplaza una versión
        // anterior que hardcodeaba "Sebina"(235)/"Marlon"(229) con un nivel mínimo fijo (150) y un
        // modelo ad-hoc de 2 misiones, en vez de leer los requisitos reales de Quest.txt. Esa versión
        // vieja mandaba QuestResultSend(...,0xFF,...) para CUALQUIER jugador que no calzara el nivel
        // 150 exacto o la clase esperada -- el cliente real interpreta ese paquete como "conversación
        // terminada" (bug reportado: "Conversation is over" al hablar con la Priest). El original NO
        // manda ningún paquete cuando no hay ninguna misión disponible para ese NPC/jugador (GetInfoByIndex
        // devuelve null -> NpcTalk devuelve false -> sigue al switch de casos especiales de abajo, y si
        var questMatch = _quests.NpcTalk(npc.MonsterClass, player.Quest!, player.Level, player.Class, player.ChangeUp);

        Log.Add(LogColor.Blue, "[Quest][{0}] OnNpcTalk: npc.Class={1} level={2} class={3} changeUp={4} => questMatch={5}",
            player.Name, npc.MonsterClass, player.Level, player.Class, player.ChangeUp,
            questMatch == null ? "null" : $"index={questMatch.Index} state={questMatch.CurrentState}");

        if (questMatch != null)
        {
            // Puerto EXACTO de CQuest::GCQuestStateSend (Quest.cpp:419-432): siempre llama a
            // GCQuestInfoSend primero, pero esa función es no-op si ya se mandó una vez esta sesión
            // (ver PlayerObject.SendQuestInfo -- normalmente ya se mandó proactivamente al entrar al
            // mundo, DSProtocol.cpp:503). El C1:A1 de estado SIEMPRE se manda.
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
            // OJO: antes acá se llamaba a ClearChaosBox(), que BORRA lo que hubiera quedado en la
            // caja. Como mover un ítem del inventario a la caja lo saca del inventario
            // (MoveItemToChaosBoxFromInventory hace InventoryDelItem en el original, y este puerto lo
            // replica), cualquier ítem que quedara adentro --por ejemplo al cerrar la ventana con el
            // 0x31 genérico, que no estaba portado-- se perdía al volver a abrir la máquina.
            // NpcChaosGoblin del original no limpia nada al abrir: los ítems siguen en la caja. Acá se
            // devuelven al inventario en vez de dejarlos en la caja porque este puerto no persiste la
            // Chaos Box en el DataServer, así que dejarlos adentro los perdería igual al desconectar.
            await ReturnChaosBoxItemsToInventoryAsync(session, player, ct);

            player.InChaosBox = true;
            await session.SendEncryptedAsync(ShopPacketBuilder.NpcTalkSend(3), ct);
            await session.SendAsync(ChaosBoxPacketBuilder.ChaosBoxItemListSend(player.ChaosBoxItems), ct);
            return;
        }

        if (npc.MonsterClass == 240) // Vault Keeper (Warehouse NPC)
        {
            // Puerto del guard de NpcWarehouse (NpcTalk.cpp:189-198): el baúl no abre si hay ítems
            // en la Chaos Box, para no poder tener el mismo ítem "en dos lados" a la vez.
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

    /// <summary>Puerto de CNpcTalk::CGNpcTalkCloseRecv (NpcTalk.cpp:325-360) -- libera el estado de
    /// interfaz del jugador al cerrar la ventana del NPC. El original no manda ninguna respuesta.
    ///
    /// <para>La Chaos Box necesita un paso extra que antes faltaba: mover un ítem del inventario a la
    /// caja lo SACA del inventario (el original hace <c>InventoryDelItem</c> en
    /// <c>MoveItemToChaosBoxFromInventory</c>, y este puerto lo replica), así que si el jugador cerraba
    /// la ventana con algo adentro el ítem quedaba sólo en <c>ChaosBoxItems</c> -- y al volver a hablar
    /// con el Chaos Goblin se borraba. Ahora se devuelve al inventario. El original no hace esto (no
    /// tiene case para INTERFACE_CHAOS_BOX acá, los ítems se quedan en la caja), pero allá la caja se
    /// persiste y acá no, así que dejarlos adentro los perdería igual al desconectar.</para></summary>
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


    /// <summary>Puerto de CItemManager::CGItemBuyRecv (ItemManager.cpp:4309-4519) -- SOLO la rama de
    /// dinero normal (Zen): el original también soporta comprar con "Coin" alternativa (type 1-3) y
    /// dos ítems especiales con lógica propia (Dark Horse/Dark Reaven "instantáneos" vía
    /// GDCreateItemSend, e ítems de gacha "Random Item" vía CMossMerchant) -- ninguno de los dos
    /// sistemas está portado (fuera del alcance de esta pasada, ver README), así que esos índices se
    /// compran como cualquier ítem normal (van al inventario en vez de generarse aparte). Tampoco se
    /// porta <c>InventoryInsertItemStack</c> (apilado de pociones/flechas ya existentes en el
    /// inventario) -- siempre busca un slot vacío nuevo, igual que si el jugador no tuviera nada
    /// apilable todavía. El impuesto de Castle Siege (<c>tax</c> en el original) es siempre 0 acá
    /// (sistema no portado, equivale a tener el castillo sin dueño).</summary>
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

    /// <summary>Puerto de CItemManager::CGItemSellRecv (ItemManager.cpp:4521-4623) -- a diferencia de
    /// la compra, el slot es del INVENTARIO propio del jugador (rango completo 0-107, incluye equipo
    /// puesto -- <c>INVENTORY_FULL_RANGE</c> en el original, no <c>INVENTORY_RANGE</c>) y no depende
    /// de qué tienda esté abierta, solo de que haya UNA tienda abierta. <c>gItemMove.CheckItemMoveAllowSell</c>
    /// (flag "no vendible" de algunos ítems especiales) y <c>gServerInfo.m_TradeItemBlockSell</c> no
    /// están portados (equivale a permitir vender cualquier ítem, el default sin restricciones
    /// configuradas). Precio: <see cref="ComputeShopSellPrice"/>, puerto de CItem::Value().</summary>
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

        // Puerto de CItemManager::UpdateInventoryViewport (ItemManager.cpp:2062-2084) -- el original
        // SOLO hace algo acá si el slot vendido era de EQUIPO puesto (INVENTORY_WEAR_RANGE); para el
        // caso común (vender desde la mochila) no manda ningún paquete extra más allá del 0x33 de
        // arriba -- el cliente real limpia su propio slot de inventario a partir de ese mismo result=1
        // (recuerda qué slot pidió vender), sin necesidad de eco del servidor.
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

    /// <summary>Puerto de la porción "sin BuyMoney explícito" de CItem::Value() (Item.cpp:916-975) --
    /// usado para comprar. Precio final igual al original: si <see cref="ItemBalance.BuyMoney"/> está
    /// seteado (alas/orbes en este puerto, ver ItemBalanceTable) se usa directo con el redondeo de
    /// 2 etapas del original (≥100 → múltiplo de 10, luego ≥1000 → múltiplo de 100); si no, se cae a
    /// <see cref="ComputeShopSellPrice"/>*3 (el original computa Buy y Sell juntos en la misma
    /// función; acá se separan por claridad pero el valor es idéntico).</summary>
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

    /// <summary>El precio explícito de Data/Item/ItemValue.txt, si el archivo menciona este item.
    ///
    /// <para>Va después de <see cref="ItemBalance.BuyMoney"/> y antes de la fórmula general. El
    /// orden frente a BuyMoney es indistinto en la práctica --ninguna de las 72 filas del archivo
    /// corresponde a un item con BuyMoney distinto de 0-- y así el precio de alas y orbes, que ya
    /// estaba bien, no se toca. Frente a la fórmula general el orden sí importa: cinco filas
    /// (las dos Siege Potion, Ale, Bless y Soul) apuntan a items que además traen la columna
    /// <c>Value</c>, y es el archivo el que tiene el número correcto -- por Value, el Jewel of
    /// Bless daba 18.700 en lugar de 9.000.000.</para>
    ///
    /// <para>El "grado" es la máscara de opciones especiales del item. Hoy la usa sólo el Horn of
    /// Dinorant.</para></summary>
    private int? LookupItemValue(Item item, ItemBalance info)
    {
        return _itemValues.Get(info.Index, item.Level, item.NewOption);
    }

    /// <summary>Puerto de CItem::Value() (Item.cpp:916-975) -- rama de venta. Simplificaciones
    /// deliberadas frente al original (fuera del alcance de esta pasada, ver README): items Muun,
    /// items Pentagram, sockets, ítems "380" (bonus de nivel de build), joyas/alas custom vía Lua, y
    /// los bonos de precio por opción especial (Luck/Skill/Excelente/Adicional) -- se calcula el
    /// precio "base" de un ítem sin ninguna de esas opciones. Suficiente para una economía de tienda
    /// funcional; el signo del precio (más caro cuanto mejor el ítem base) es correcto.</summary>
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

            // Joyas (sección 14, sub 0-8) -- rama especial del original que escala por nivel/durabilidad
            // y usa un redondeo de 1 sola etapa (≥10 → múltiplo de 10, sin la 2da etapa de ≥1000).
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

        // Fórmula general basada en ItemLevel (Item.cpp:1008-1125) -- solo la rama sin opciones
        // especiales (ver doc-comment de ComputeShopSellPrice).
        int itemLevel = info.Level + (Math.Clamp((int)item.Level, 0, 15) * 3);

        itemLevel += item.Level switch
        {
            5 => 4, 6 => 10, 7 => 25, 8 => 45, 9 => 65, 10 => 95,
            11 => 135, 12 => 185, 13 => 245, 14 => 305, 15 => 365, _ => 0,
        };

        long generalPrice;

        if (info.Section == 13) // mascotas/joyas de anillo-pendiente/misceláneo -- fórmula cúbica simple
        {
            generalPrice = ((long)itemLevel * itemLevel * itemLevel) + 100;
        }
        else if (info.Section == 12) // alas -- en este puerto normalmente ya tienen BuyMoney seteado,
                                      // se mantiene por robustez ante filas sin BuyMoney en el archivo
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

    /// <summary>Redondeo de 2 etapas de CItem::Value() -- primero múltiplo de 10 si ≥100, LUEGO
    /// múltiplo de 100 si el resultado (ya redondeado) es ≥1000.</summary>
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

    /// <summary>Redondeo de 1 sola etapa (solo múltiplo de 10 si ≥10) -- usado por la rama especial de
    /// joyas de CItem::Value().</summary>
    private static uint RoundPriceOneStage(long v)
    {
        if (v >= 10)
        {
            v = (v / 10) * 10;
        }

        return (uint)Math.Max(v, 0);
    }

    /// <summary>Puerto simplificado de InventoryRectCheck/InventoryInsertItem (ItemManager.cpp:1226-1256)
    /// -- primer-hueco-libre escaneando la grilla principal de inventario (8 columnas, filas 12-107)
    /// con el mismo algoritmo de <see cref="ShopManagerTable"/> (no hay apilado de consumibles
    /// existentes, ver doc-comment de OnItemBuyAsync).</summary>
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

        // ShopNumber != null => es un NPC de tienda, no un monstruo real -- el original nunca deja
        // que uno termine ahí porque OBJECT_NPC no es un objetivo válido de ataque (CAttack::Attack
        // valida lpTarget->Type == OBJECT_MONSTER), acá se replica con el mismo campo que ya usa
        // OnNpcTalkAsync para identificar NPCs (ver comentario de Monster.ShopNumber).
        if (!_monsters.TryGet(recv.TargetIndex, out var monster) || monster.IsDead || monster.ShopNumber != null)
        {
            return;
        }

        if (monster.Map != player.Map)
        {
            return;
        }

        var map = _maps.GetMap(player.Map);

        // Puerto de CGAttackRecv (Attack.cpp:1565): no se puede atacar parado en zona segura (bit 1),
        // ni a un objetivo que esté parado en una.
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

        // Broadcast de la animación de ataque (0x18) -- puerto exacto de GCActionSend (Protocol.cpp:
        // 1488-1507): difunde PMSG_ACTION_SEND (0x18) vía MsgSendV2 ÚNICAMENTE a los observadores en
        // el viewport del atacante, NUNCA al propio atacante (enviar 0x18 de vuelta al propio cliente
        // hace que main.exe interrumpa su animación/movimiento local y resetee posición).
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
        bool graze = false; // "miss=1 pero no se anuló" -- ver comentario de la fórmula abajo

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

        // ---- Paso 2: defensa del objetivo (CAttack::GetTargetDefense, Attack.cpp:1117-1154) ----
        // Sin la mitad de reducción que aplica cuando el objetivo es OBJECT_USER -- acá el objetivo
        // siempre es un monstruo, así que se usa su Defense tal cual.
        int targetDefense = Math.Max(monster.Defense, 0);

        // ---- Paso 3: daño crudo (CAttack::GetAttackDamage, Attack.cpp:1156-1307, rama jugador) ----
        int range = Math.Max(player.PhysiDamageMax - player.PhysiDamageMin, 1);
        int damage = player.PhysiDamageMin + Rng.Next(range);

        if (graze)
        {
            damage = (damage * 30) / 100; // "golpe de gracia" pese al mal roll de acierto/esquiva
        }

        damage -= targetDefense;
        damage = Math.Max(damage, 0);

        // ---- Paso 4: piso de daño por nivel (Attack.cpp:365-366) ----
        int minDamage = Math.Max(player.Level / 10, 1);

        if (damage < minDamage)
        {
            damage = minDamage + Rng.Next(minDamage);
        }

        // Multiplicadores globales (m_GeneralDamageRatePvM, DamageTable por mapa/nivel) no portados
        // todavía -- equivalen a 100% (sin cambio), que es el default de un paquete sin tocar.

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

    /// <summary>
    /// Puerto simplificado de CSkillManager::CGSkillAttackRecv + UseAttackSkill + CAttack::
    /// GetAttackDamageWizard (SkillManager.cpp:2418-2515,770-832; Attack.cpp:1309-1384) -- SOLO
    /// casteo de skill de ataque de un solo objetivo (C3:19) de jugador contra monstruo (sin PvP, sin
    /// skills de área/duración C3:1E, sin multi-hit C3:1D, sin Teleport Ally). Simplificaciones
    /// documentadas explícitamente frente al original:
    ///   1) Sin sistema de "skills aprendidos" (CSkillManager::GetSkill busca en lpObj->Skill[], una
    ///      lista poblada por un flujo de aprendizaje/árbol de skills que no está portado) -- acá se
    ///      valida directamente contra SkillList.txt en el momento del casteo (clase+nivel), en vez
    ///      de contra una lista de skills previamente aprendidos. Cualquier jugador de la clase/nivel
    ///      correctos puede castear cualquier skill que le corresponda sin haberlo "aprendido" antes.
    ///   2) CheckSkillRequireClass sigue el mismo chequeo (RequireClass[clase] != 0 &&
    ///      ChangeUp+1 >= RequireClass[clase]) -- <see cref="PlayerObject.ChangeUp"/> ahora se
    ///      deriva del valor real de DB (ver OnCharacterInfoFromDataServerAsync), pero con los datos
    ///      de semilla actuales (todos los personajes en 1ra clase) sigue valiendo 0 en la práctica,
    ///      así que solo son alcanzables los skills con RequireClass==1 para la clase del jugador
    ///      hasta que haya datos de un personaje con cambio de clase real para probarlo.
    ///   3) Sin SkillUseArea.txt (restricción de mapas por skill) -- solo se reusa el chequeo de zona
    ///      segura (map.CheckAttr bit 1) ya usado por el ataque cuerpo a cuerpo.
    ///   4) Sin crítico/excelente/daño PvP/combo de Dark Knight (mismas razones que el ataque cuerpo
    ///      a cuerpo -- dependen de sistemas no portados).
    ///   5) MPConsumptionRate/BPConsumptionRate (reducción de costo por item/efecto) se asumen 100%
    ///      siempre -- no hay sistema de opciones de item ni de efectos activos todavía.
    ///   6) El acierto/esquiva y la defensa del objetivo reusan exactamente el mismo cálculo que el
    ///      ataque cuerpo a cuerpo (AttackSuccessRate/DefenseSuccessRate/Defense) -- el original no
    ///      documenta una fórmula de "acierto mágico" separada en las partes revisadas de Attack.cpp.
    /// </summary>
    private async Task OnSkillAttackAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null || !player.WorldEntered)
        {
            return;
        }

        var recv = SkillAttackRecv.Parse(p);

        // Ver comentario equivalente en OnAttackAsync -- un NPC de tienda no es un objetivo válido.
        if (!_monsters.TryGet(recv.TargetIndex, out var monster) || monster.IsDead || monster.ShopNumber != null)
        {
            return;
        }

        if (monster.Map != player.Map)
        {
            return;
        }

        var map = _maps.GetMap(player.Map);

        // Puerto de CGSkillAttackRecv (SkillManager.cpp:2456-2472, simplificado sin SkillUseArea.txt):
        // no se puede castear parado en zona segura, ni contra un objetivo parado en una.
        if ((map?.CheckAttr(player.X, player.Y, 1) ?? false) || (map?.CheckAttr(monster.X, monster.Y, 1) ?? false))
        {
            return;
        }

        var skill = _skills.Get(recv.Skill);

        if (skill == null)
        {
            return;
        }

        // ---- Validación de clase/nivel (sustituye el chequeo de "skill aprendido", ver punto 1 del
        // doc-comment de este método) ----
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
            return; // fuera de rango: "casteo gratis" en el original (no consume maná, ver punto 5)
        }

        player.SkillDelay[skill.Index] = now;

        // ---- Maná/BP (CheckSkillMana/CheckSkillBP, SkillManager.cpp:333-365) ----
        if ((int)player.Mana < skill.Mana || (int)player.BP < skill.BP)
        {
            return; // insuficiente: casteo totalmente silencioso, igual que el original (ver punto 5)
        }

        player.Mana = (uint)Math.Max((int)player.Mana - skill.Mana, 0);
        player.BP = (uint)Math.Max((int)player.BP - skill.BP, 0);

        await session.SendAsync(ManaPacketBuilder.ManaSend(0xFF, (int)player.Mana, (int)player.BP), ct);

        // ---- Paso 1: miss/dodge (mismo cálculo que CAttack::MissCheck usado en OnAttackAsync) ----
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

        // ---- Paso 2: daño mágico crudo (CAttack::GetAttackDamageWizard, Attack.cpp:1309-1384) ----
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

        // ---- Paso 3: piso de daño por nivel (mismo que el ataque cuerpo a cuerpo, Attack.cpp:365-366) ----
        int minDamage = Math.Max(player.Level / 10, 1);

        if (damage < minDamage)
        {
            damage = minDamage + Rng.Next(minDamage);
        }

        // ---- Paso 4: multiplicador opcional por skill (SkillDamage.txt -- no-op con los datos reales) ----
        damage = _skillDamage.Apply(skill.Index, damage);

        monster.Life = Math.Max(monster.Life - damage, 0);
        monster.DamageByAttacker.TryGetValue(player.Index, out var accumulated);
        monster.DamageByAttacker[player.Index] = accumulated + damage;

        await session.SendAsync(CombatPacketBuilder.DamageSend(monster.Index, damage, 0, missFlag: false, monster.Life), ct);

        // Puerto de GCSkillAttackSend (SkillManager.cpp:2665-2685): unicast al propio casteador +
        // fan-out por viewport a quienes lo estén viendo, cifrado por bloques (C3).
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

    /// <summary>
    /// Puerto de CGPositionRecv (Protocol.cpp:557-610) -- sincronización de posición de jugador (0xD0).
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

    /// <summary>
    /// Puerto simplificado de CObjectManager::CharacterLifeCheck (la rama de muerte de monstruo,
    /// ObjectManager.cpp:2815-2929) + CharacterCalcExperienceSplit/Alone (789-865) + CharacterLevelUp
    /// (983-1041). Solo reparto individual (sin grupo -- Social/Party no portado todavía): cada
    /// atacante se lleva experiencia proporcional a SU daño acumulado sobre este monstruo, igual que
    /// el original hace incluso fuera de un grupo.
    /// </summary>
    private async Task OnMonsterDeathAsync(PlayerObject killer, Monster monster, CancellationToken ct)
    {
        monster.Live = false;
        monster.DiedAt = DateTime.UtcNow;

        // Puerto de CObjectManager::CharacterLifeCheck (ObjectManager.cpp:2893-2903): al morir, el
        // original SOLO pone Live=0/State=OBJECT_DYING y manda el paquete de muerte (GCUserDieSend) --
        // NO saca al objeto del viewport todavía. El cadáver se sigue viendo (jugando su animación de
        // muerte) hasta que el timer de respawn cumple y gObjMonsterRegen llama gObjClearViewport, que
        // recién ahí lo saca de la vista de todos (ver RespawnDeadMonsters en ViewportTicker). Sacarlo
        // acá de inmediato -- como hacía este método antes -- es lo que causaba que los monstruos
        // "desaparecieran" sin ningún efecto de muerte visible.
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

        // Puerto de CDevilSquare::MonsterDieProc (Fase 6) -- independiente del reparto de experiencia
        // de abajo, solo aplica si el monstruo pertenece a una corrida activa de Devil Square.
        if (_devilSquare != null)
        {
            await _devilSquare.OnMonsterKilledAsync(monster, ct);
        }

        // Puerto de la parte de CObjectManager::CharacterLifeCheck que decide entre reparto solo vs.
        // en grupo (ObjectManager.cpp:4817): cuando el atacante está en un grupo con >=2 miembros, el
        // reparto de experiencia de TODO el grupo se calcula una sola vez por grupo (no por
        // atacante) -- de ahí processedParties, para no procesar el mismo grupo dos veces si más de
        // un miembro pegó al monstruo.
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

    /// <summary>
    /// Puerto MUY simplificado de gObjMonsterDieGiveItem (Monster.cpp:54-284) -- de la cascada real de
    /// ~13 subsistemas de drop (ItemBag por clase de monstruo, drop-tables de boss/evento, drop-events
    /// programados, set-item aleatorio, etc., ver investigación), esta pasada SOLO porta el camino
    /// "genérico" que cubre la enorme mayoría de monstruos de campo comunes: un roll de item (según
    /// <see cref="Monster.ItemRate"/>, reusando <see cref="ItemBalanceTable.PickRandomDropItem"/> --
    /// mismos datos de Item.txt/DropItem que ya carga la Fase 4 de balance) O, si no salió item, un
    /// roll de dinero (según <see cref="Monster.MoneyRate"/>). Ambos ratees se interpretan como
    /// "1 en N" (<c>Rng.Next(rate)==0</c>), la interpretación estándar de estas dos columnas en
    /// MonsterList.txt. Fuera de esta pasada (documentado en detalle en el README): ItemBagManager
    /// (drops especiales por clase de monstruo/evento), sets aleatorios, opciones aleatorias de
    /// nivel/excelente/socket en el item dropeado (sale "de fábrica", +0 sin opciones), serial
    /// persistente único vía DataServer (se deja en 0, igual que los items comprados en tienda de
    /// Fase 8 -- mismo nivel de simplificación ya aceptado ahí).
    /// </summary>
    /// <summary>Puerto de CQuestObjective::MonsterItemDrop (QuestObjective.cpp:213-269) -- enganchado
    /// en TryDropLootAsync ANTES del roll de item/dinero genérico (igual que Monster.cpp:59-67).
    /// Se evalúa contra quien más daño le hizo al monstruo (gObjMonsterGetTopHitDamageUser), no
    /// necesariamente el que dio el golpe final. La variante de reparto en grupo
    /// (MonsterItemDropParty, gServerInfo.m_QuestMonsterItemDropParty) no está portada -- se evalúa
    /// solo contra el top-damager, simplificación documentada.</summary>
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

            // Puerto EXACTO de QuestObjective.cpp:236-244 (incluye el caso especial: si sólo hay
            // DropMaxLevel y no DropMinLevel, se compara contra monster.Class en vez del nivel).
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

        // Puerto de Monster.cpp:59-67: gQuestObjective.MonsterItemDrop se chequea ANTES que el drop
        // genérico de item/dinero (que sigue abajo) -- si un ítem de misión cae, ESE es el drop de
        // este kill, ninguno de los otros dos rolls corre (ver el guard "dropped == null" que sigue).
        GroundItem? dropped = TryQuestItemDrop(killer, monster);

        int itemRate = monster.ItemRate <= 0 ? 10 : monster.ItemRate;
        int moneyRate = monster.MoneyRate <= 0 ? 10 : monster.MoneyRate;

        // En C++ (Monster.cpp:121,157): ItemRate/MoneyRate de MonsterList.txt definen la tirada.
        // Si Rng.Next(ItemRate) < 10 (ItemDropRate base 10), hay drop de ítem.
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
                if (monster.Level >= 25 && Rng.Next(1500) == 0) // Roll de ítem Excelente
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
            // El propio matador también tiene que verlo aunque por algún motivo no estuviera en la
            // lista de "viewers" del monstruo (ej. lo remató de un golpe a distancia justo al límite
            // del rango de visión) -- se manda una vez más de forma directa, sin duplicar si ya estaba.
            await killer.Session.SendAsync(appearPacket, ct);
        }

        dropped.JustDropped = false; // ya se mandó la aparición "recién caído" una vez
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
        // Puerto de CharacterCalcExperienceAlone (ObjectManager.cpp:845): m_AddExperienceRate es un
        // multiplicador DIRECTO (no porcentaje) indexado por AccountLevel. Los otros multiplicadores
        // globales que sí aplican /100 (mapa, bonus, reset) leen de sistemas no portados y siguen
        // equivaliendo a 100% (sin cambio).
        experience *= WorldPacketBuilder.ServerInfo.AddExperienceRate[attacker.AccountLevel];

        await ApplyExperienceGainAsync(attacker, monster.Index, experience, damageCredit, ct);
    }

    /// <summary>
    /// Puerto de CharacterCalcExperienceParty (ObjectManager.cpp:1345): a diferencia del reparto
    /// solo (por daño propio), acá el "botín" de experiencia se calcula sobre el daño TOTAL que le
    /// hizo el grupo entero al monstruo (sumando el de todos los miembros, no solo de quien dio el
    /// golpe final), y se reparte entre los miembros que estén en el MISMO mapa y a <=
    /// <see cref="MaxPartyDistance"/> tiles del monstruo (no del atacante) -- proporcional al nivel
    /// de cada uno, NO al daño que haya hecho cada uno individualmente (así que un miembro que no
    /// llegó a pegarle igual se lleva su parte si está en rango). Las tablas de bonus por tamaño y
    /// diversidad de clases del grupo (m_PartyGeneralExperience/m_PartySpecialExperience) no están
    /// portadas todavía -- equivalen a sin bonus (documentado en README, misma clase de deuda técnica
    /// que el resto de multiplicadores globales de esta fase).
    /// </summary>
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

    /// <summary>Cola común de ambos caminos de arriba (solo y grupo): sumar experiencia, resolver
    /// subidas de nivel en cadena (CharacterLevelUp, ObjectManager.cpp:983-1041) y mandar los
    /// paquetes correspondientes. <paramref name="monsterIndex"/> es solo para el campo informativo
    /// del paquete (qué mató para ganar esto) -- Fase 6 lo reusa para recompensa de experiencia de
    /// evento (Devil Square), que no tiene un monstruo real asociado, con el sentinel -1 (0xFFFF en
    /// el wire, ningún monstruo/jugador real usa ese índice).</summary>
    private async Task ApplyExperienceGainAsync(PlayerObject member, int monsterIndex, long experience, int damageCredit, CancellationToken ct)
    {
        long addExperience = Math.Max(experience, 0);
        int maxLevelUp = WorldPacketBuilder.ServerInfo.MaxLevelUp;
        int maxLevel = WorldPacketBuilder.ServerInfo.MaxLevel;

        bool leveledUp = false;

        if (member.Level >= maxLevel)
        {
            // Puerto de CharacterLevelUp (ObjectManager.cpp:985-989): al tope, la experiencia
            // ganada ni siquiera se guarda -- se descarta entera.
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

                // Puerto exacto de ObjectManager.cpp:1005: al agotar el tope de niveles por
                // evento (MaxLevelUp), lo que sobre de experiencia se descarta -- no queda
                // guardado para el próximo kill.
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

        // Puerto de ObjectManager.cpp:857-864: si hubo level-up, el popup de experiencia de este
        // paquete manda 0 (el aviso ya lo da GCLevelUpSend/LevelUpSend más abajo) -- si no, manda
        // la experiencia real ganada.
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

    /// <summary>
    /// Puerto simplificado de CGChatRecv (Protocol.cpp:1164) para el chat público sin sigilo: eco al
    /// propio hablante + broadcast a todo el que lo tenga en su VisibleTo (mismo mecanismo de
    /// viewport que ya usa movimiento -- confirmado que el original hace exactamente esto vía
    /// MsgSendV2/VpPlayer2[], ver brief de investigación de la Fase 5). Comandos ('/') y los demás
    /// canales por sigilo (party '~', guild '@'/'@@'/'@>', gens '$') no están portados todavía --
    /// se ignoran en silencio en vez de tratarse como chat público (igual que el original, que los
    /// excluye del branch "sin sigilo" y los rutea a sistemas aparte).
    /// </summary>
    private async Task OnChatAsync(ClientSession session, byte[] p, CancellationToken ct)
    {
        var player = session.Player;

        if (player == null)
        {
            return;
        }

        var recv = ChatRecv.Parse(p);

        // Anti-spoof: el cliente siempre manda su propio nombre real (puerto de la verificación al
        // principio de CGChatRecv).
        if (!string.Equals(recv.Name, player.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (recv.Message.Length == 0)
        {
            return;
        }

        char first = recv.Message[0];

        // Puerto de la parte de CGChatRecv que multiplexa por sigilo inicial (Protocol.cpp:1164):
        // '~' = chat de grupo (manda el mensaje TAL CUAL, sigilo incluido, a cada miembro vía
        // DataSend directo -- NO pasa por el viewport, así que llega sin importar mapa/distancia,
        // igual que el original iterando m_PartyInfo[...].Index[0..4]). '/' (comandos de
        // GM/usuario), '@'/'@@'/'@>' (guild) y '$' (Gens) no están portados todavía -- se ignoran en
        // silencio en vez de tratarse como chat público.
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

    /// <summary>
    /// Puerto de CGChatWhisperRecv (Protocol.cpp:1290): busca al destinatario primero en ESTE
    /// GameServer (equivalente a gObjFind, un scan lineal de jugadores online); si no está acá, le
    /// pregunta a DataServer (whisper cruzado entre GameServers, protocolo 0x72/0x73 ya implementado
    /// del lado DataServer desde antes de esta fase).
    /// </summary>
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
            // Auto-whisper: el original lo rechaza con notice code 270 (GCServerMsgSend); ese sistema
            // de notificaciones cortas no está portado todavía, así que acá simplemente no se manda nada.
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

    /// <summary>Callback desde DataServerConnection cuando llega SDHP_GLOBAL_WHISPER_SEND de vuelta
    /// (0x72) -- confirma al REMITENTE si el whisper se pudo entregar en algún GameServer.</summary>
    public async Task OnGlobalWhisperResultFromDataServerAsync(GlobalWhisperResultFromDataServer msg, CancellationToken ct)
    {
        if (!_players.TryGet(msg.Index, out var sender))
        {
            return;
        }

        if (msg.Result == 0)
        {
            // Destinatario no encontrado en ningún GameServer -- el original manda un notice code
            // (270 vía GCServerMsgSend); ese sistema de notificaciones cortas no está portado
            // todavía, así que por ahora el remitente solo nota que no le llegó respuesta.
            Log.Add(LogColor.Black, "[Chat][{0}] Whisper from '{1}' to '{2}' -- recipient not found",
                sender.Index, sender.Name, msg.TargetName);
        }

        await Task.CompletedTask;
    }

    /// <summary>Callback desde DataServerConnection cuando llega SDHP_GLOBAL_WHISPER_ECHO_SEND
    /// (0x73) -- este GameServer SÍ tiene conectado al destinatario real; se le entrega el whisper.</summary>
    public async Task OnGlobalWhisperEchoFromDataServerAsync(GlobalWhisperEchoFromDataServer msg, CancellationToken ct)
    {
        if (!_players.TryGet(msg.Index, out var target))
        {
            return;
        }

        await target.Session.SendAsync(ChatPacketBuilder.ChatWhisperSend(msg.SourceName, msg.Message), ct);
    }

    // ---------------------------------------------------------------- Fase 5: party (primera pasada)

    /// <summary>
    /// Puerto de CGPartyRequestRecv (Party.cpp:332): valida que el objetivo exista, no sea uno mismo,
    /// no esté ya en un grupo, y que ninguno de los dos lados tenga otra invitación pendiente (versión
    /// simplificada del chequeo de Interface.use ocupada del original). El chequeo de diferencia de
    /// nivel máxima (m_PartyMaxGapLevel) y el de Gens-lock no están portados todavía -- no bloquean
    /// nada por ahora (equivalen a tenerlos desactivados). AutoAcceptPartyRequest (el camino de
    /// PartyMatching) tampoco -- ver nota de "safe to defer" del brief de investigación.
    /// </summary>
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
            return; // alguno de los dos ya tiene una invitación de party pendiente
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

    /// <summary>
    /// Puerto de CGPartyRequestResultRecv (Party.cpp:439): valida que la respuesta corresponda a una
    /// invitación realmente pendiente (recv.InviterIndex debe matchear lo que se guardó al invitar),
    /// crea el grupo si el invitador todavía no tenía uno, agrega al invitado, y retransmite la lista
    /// a todos los miembros. Limpia el estado de invitación de ambos lados al final pase lo que pase
    /// (equivalente al CLEAR_JUMP del original).
    /// </summary>
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

    /// <summary>
    /// Puerto de CGPartyDelMemberRecv (Party.cpp:599): number = slot propio (salir) o de otro miembro
    /// (expulsar -- solo si quien lo manda es el líder, slot 0).
    /// </summary>
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
            return; // solo el líder puede expulsar a otros
        }

        if (!_players.TryGet(targetPlayerIndex, out var target))
        {
            group.MemberIndices.Remove(targetPlayerIndex);
            return;
        }

        await RemovePlayerFromPartyAsync(target, ct);
    }

    /// <summary>
    /// Núcleo compartido de "sacar a un jugador de su grupo actual" -- usado tanto por
    /// PMSG_PARTY_DEL_MEMBER_RECV (salir/expulsar) como por la desconexión (el original también saca
    /// al jugador de su grupo al desconectarse, CloseClient -> CParty::DelMember). Puerto de
    /// CParty::DelMember/ChangeLeader (Party.cpp:246,599+): si el grupo queda en &lt;=1 miembro tras
    /// la remoción se disuelve entero (mismo umbral que el original: pasar de 2 a 1 miembro siempre
    /// disuelve el grupo); si no, el nuevo líder es automáticamente el que haya quedado en el slot 0
    /// (List&lt;int&gt;.Remove ya corre los índices restantes, no hace falta un paso de "ascenso"
    /// aparte como en el array plano con huecos del original).
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

    /// <summary>Puerto de GCPartyListSend: se manda a TODOS los miembros cada vez que la composición
    /// del grupo cambia (join/leave/kick/leader migration).</summary>
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

    /// <summary>Puerto de GCPartyLifeSend (Party.cpp, llamado periódicamente desde User.cpp:3488) --
    /// invocado desde ViewportTicker cada ~2s (ver ViewportTicker.TickPartyLifeAsync) para todos los
    /// grupos con >1 miembro.</summary>
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

    // ---------------------------------------------------------------- Fase 5: amigos (primera pasada)
    // Puerto delgado de GameServer/Friend.cpp: casi toda la lógica real vive en DataServer (ver
    // DataServerProtocolHandler.HandleFriendAsync) -- acá solo se reempaqueta la solicitud del
    // cliente hacia DataServer y viceversa, igual que hace el original.

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

    /// <summary>Callback desde DataServerConnection para cualquier paquete con head 0xB0 (amigos) --
    /// despacha por el sub-código igual que HandleF3Async del lado cliente.</summary>
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
                    // Confirmación al que ACEPTÓ: ahora es amigo de RequesterName.
                    await accepter.Session.SendAsync(FriendPacketBuilder.FriendResultSend(msg.RequesterName), ct);
                }

                break;
            }

            case 0x04:
            {
                var msg = FriendResultDeliverFromDataServer.Parse(packet);

                if (msg.Result == 1 && _players.TryGet(msg.RequesterIndex, out var requester))
                {
                    // Aviso simétrico al que mandó la solicitud ORIGINAL: se la aceptaron.
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

            // sub 0x01 (FriendRequestResultFromDataServer, ack al que MANDÓ la solicitud) no tiene
            // hoy un opcode C->G dedicado -- el original tampoco define uno más allá de las
            // notificaciones cortas (GCServerMsgSend) que todavía no están portadas, mismo caso ya
            // documentado para whisper (ver OnGlobalWhisperResultFromDataServerAsync).
        }
    }

    // ---------------------------------------------------------------- Fase 6: Devil Square (primera pasada)
    // Todo el motor de estados/entrada/puntaje/recompensa vive en World/DevilSquareManager.cs -- acá
    // solo se parsean los paquetes de cliente y se delega, mismo patrón que Party/Friend de arriba.

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

    // ---------------------------------------------------------------- Fase 4: Máquina de Chaos (Chaos Box / Combinaciones)

    /// <summary>Precio de compra ACTUAL de un item -- la misma fórmula que usa la tienda
    /// (<see cref="ComputeShopBuyPrice"/>), reutilizada por <see cref="ChaosMixLogic"/> para las
    /// mezclas que suman el valor de lo que hay en la Chaos Box. Un item sin fila de balance (no
    /// debería pasar para nada que entra a la caja, pero por las dudas) vale 0 en vez de reventar.</summary>
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
            // El original entrega estos items por GDCreateItemSend (un mensaje al DataServer, no el
            // mismo paquete de la mezcla) -- acá el equivalente es el mismo camino que ya usa el
            // comando de depuración "item": buscar hueco libre y notificar por ItemMoveSend. El
            // PMSG_CHAOS_MIX_SEND de abajo no lleva el item en este caso (ver doc-comment de
            // ChaosMixResult.DeliverViaInventory); resultado 1 igual para que la ventana sepa que
            // paso algo y vaya a mirar el inventario.
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

    /// <summary>Devuelve al inventario lo que haya quedado en la Chaos Box y la vacía. Devuelve true
    /// si movió algo (y ya mandó la lista de inventario actualizada al cliente).
    ///
    /// <para>Si el inventario está lleno el ítem se queda en la caja en vez de descartarse: perder un
    /// ítem es mucho peor que dejar la ventana con algo adentro, y el guard del baúl
    /// (<c>CheckItemInChaosBox</c>) ya impide que eso habilite tener el mismo ítem en dos lados.</para></summary>
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

    /// <summary>0x88: preguntar la tasa de éxito antes de decidir si combinar. Pasa
    /// <c>execute: false</c> -- antes llamaba a <see cref="ChaosMixLogic.CalculateAndExecuteMix"/> sin
    /// eso, que es la misma función que ejecuta la combinación real (0x86): cada vez que un cliente
    /// preguntaba la tasa, cobraba el zen, vaciaba la Chaos Box y tiraba el dado como si ya hubiera
    /// combinado. Este cliente (MuMain) todavía no llama a este opcode -- su ventana de combinar es
    /// la de Season 6 (recetas de mix.bmd, nunca cargado en 0.99B) y no la porté -- pero el bug
    /// existía igual para cualquier otro cliente 0.99B que sí pregunte antes de combinar.</summary>
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

        // C1:55 -- Abre la ventana de creación de Guild en el cliente main.exe
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

        // C1:56:01 -- Éxito al crear Guild
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

    /// <summary>
    /// Puerto EXACTO de CQuest::CGQuestStateRecv (Quest.cpp:233-290) -- botón de "aceptar/continuar"
    /// del diálogo de misión. Reemplaza una versión anterior que hardcodeaba dos misiones ad-hoc
    /// (Pergamino del Emperador / ítem específico de clase con nivel-150 e items adivinados) por el
    /// motor real data-driven: <see cref="Config.QuestTable.GetInfoByIndex"/> encuentra la fila que
    /// coincide con el ÍNDICE pedido Y el estado actual guardado del jugador (si no hay ninguna, el
    /// original no contesta nada -- <c>lpInfo==0 -&gt; return</c> -- replicado acá); si la hay, se
    /// valida el objetivo (Zen o ítem, según <c>QuestObjective.txt</c>), se cobra/consume, se aplica
    /// la recompensa (<c>QuestReward.txt</c> -- acá es donde vive el cambio real de 2da clase, tipo
    /// CHANGE1) y se avanza el estado (NORMAL-&gt;ACCEPT-&gt;FINISH, o CANCEL-&gt;ACCEPT).
    /// </summary>
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

    /// <summary>Puerto simplificado de CItemManager::GetInventoryItemCount (ItemManager.cpp:548-571) --
    /// SOLO la rama no-apilable (cuenta instancias completas). Los 4 ítems de misión reales
    /// (471-474) no son apilables en Item.txt, así que esta simplificación no cambia el resultado
    /// para el único consumidor real de esta función; la rama apilable (contar por Durability) no
    /// está portada porque este build no modela stacks de items en ningún otro lado todavía.</summary>
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

    /// <summary>Puerto EXACTO de CQuestReward::InsertQuestReward (QuestReward.cpp:116-171). El tipo
    /// CHANGE1 es el cambio real de 1ra a 2da clase (ver <see cref="PlayerObject.ChangeUp"/>); HERO
    /// usa <c>PlusStatMinLevel</c>/<c>PlusStatPoint</c> reales de Common.dat (<see cref="_gsiCommon"/>,
    /// ya cargados desde antes pero sin consumidor -- éste es el primero).</summary>
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

                // Puerto exacto de QuestReward.cpp:147-149 (aritmética de byte con overflow intencional).
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

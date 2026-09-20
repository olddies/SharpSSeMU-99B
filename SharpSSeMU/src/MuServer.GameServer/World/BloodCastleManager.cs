using MuServer.GameServer.Net;
using MuServer.GameServer.Protocol;
using MuServer.Shared.Logging;

namespace MuServer.GameServer.World;

public enum BloodCastleState
{
    Blank = 0,
    Empty = 1,
    Stand = 2,
    Start = 3,
    Clean = 4,
}

public sealed class BloodCastleLevel
{
    public int Level { get; }
    public byte Map { get; }
    public BloodCastleState State { get; set; } = BloodCastleState.Blank;
    public DateTime StateStartTime { get; set; } = DateTime.UtcNow;
    public int TimeRemainingSeconds { get; set; }
    public ushort MaxMonsters { get; set; } = 100;
    public ushort CurrentMonsters { get; set; }
    public ushort ArchangelWeaponOwnerIndex { get; set; } = 0xFFFF;
    public HashSet<int> PlayerIndices { get; } = new();

    public BloodCastleLevel(int level, byte map)
    {
        Level = level;
        Map = map;
    }
}

/// <summary>
/// Puerto exacto de CBloodCastle (BloodCastle.cpp:1-1200) -- gestor del evento Blood Castle (niveles 1 al 6).
/// </summary>
public sealed class BloodCastleManager
{
    private readonly BloodCastleLevel[] _levels = new BloodCastleLevel[6];
    private readonly PlayerRegistry _players;
    private bool _running;

    public BloodCastleManager(PlayerRegistry players)
    {
        _players = players;
        for (int i = 0; i < 6; i++)
        {
            _levels[i] = new BloodCastleLevel(i + 1, (byte)(11 + i));
        }
    }

    public void Start(CancellationToken ct)
    {
        _running = true;
        _ = Task.Run(() => TickerLoopAsync(ct), ct);
        Log.Add(LogColor.Blue, "[BloodCastle] Evento inicializado para niveles BC 1 a BC 6.");
    }

    private async Task TickerLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var lvl in _levels)
                {
                    TickLevel(lvl, now);
                }
            }
            catch (Exception ex)
            {
                Log.Add(LogColor.Red, "[BloodCastle] Error en TickerLoopAsync: {0}", ex.Message);
            }

            await Task.Delay(1000, ct);
        }
    }

    private void TickLevel(BloodCastleLevel lvl, DateTime now)
    {
        var elapsed = (int)(now - lvl.StateStartTime).TotalSeconds;

        switch (lvl.State)
        {
            case BloodCastleState.Blank:
                if (now.Minute % 60 == 50) // Abre a los X:50 minutos cada hora
                {
                    SetState(lvl, BloodCastleState.Stand);
                }
                break;

            case BloodCastleState.Stand:
                lvl.TimeRemainingSeconds = Math.Max(0, 300 - elapsed); // 5 min para entrar
                if (elapsed >= 300)
                {
                    if (lvl.PlayerIndices.Count > 0)
                    {
                        SetState(lvl, BloodCastleState.Start);
                    }
                    else
                    {
                        SetState(lvl, BloodCastleState.Blank);
                    }
                }
                break;

            case BloodCastleState.Start:
                lvl.TimeRemainingSeconds = Math.Max(0, 900 - elapsed); // 15 min de evento
                if (elapsed >= 900)
                {
                    SetState(lvl, BloodCastleState.Clean);
                }
                break;

            case BloodCastleState.Clean:
                if (elapsed >= 15)
                {
                    SetState(lvl, BloodCastleState.Blank);
                }
                break;
        }
    }

    private void SetState(BloodCastleLevel lvl, BloodCastleState newState)
    {
        lvl.State = newState;
        lvl.StateStartTime = DateTime.UtcNow;

        if (newState == BloodCastleState.Start)
        {
            lvl.CurrentMonsters = lvl.MaxMonsters;
        }

        if (newState == BloodCastleState.Clean || newState == BloodCastleState.Blank)
        {
            lvl.PlayerIndices.Clear();
            lvl.ArchangelWeaponOwnerIndex = 0xFFFF;
        }

        BroadcastState(lvl);
    }

    public async Task HandleEnterAsync(ClientSession session, PlayerObject player, byte levelByte, byte cloakSlot, CancellationToken ct)
    {
        int level = levelByte;
        if (level < 1 || level > 6)
        {
            await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(1), ct);
            return;
        }

        var lvl = _levels[level - 1];

        if (lvl.State != BloodCastleState.Stand)
        {
            await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(2), ct);
            return;
        }

        // Verificar Nivel
        int minLevel = GetMinLevel(player.Class, level);
        int maxLevel = GetMaxLevel(player.Class, level);

        if (player.Level < minLevel || player.Level > maxLevel)
        {
            await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(3), ct);
            return;
        }

        // Verificar Invisibility Cloak (+1..+6)
        if (cloakSlot >= Item.InventorySize)
        {
            await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(4), ct);
            return;
        }

        var cloak = player.Items[cloakSlot];
        int cloakIndex = Item.GetItem(13, 18); // Invisibility Cloak

        if (cloak.Index != cloakIndex || cloak.Level != level)
        {
            await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(4), ct);
            return;
        }

        // Consumir Ticket
        player.SetItem(cloakSlot, Item.Empty());
        await session.SendAsync(ItemPacketBuilder.ItemDeleteSend(cloakSlot, 1), ct);

        // Teletransportar a la entrada del mapa de Blood Castle
        lvl.PlayerIndices.Add(player.Index);
        player.Map = lvl.Map;
        player.X = 13;
        player.Y = 15;
        player.TX = 13;
        player.TY = 15;

        await session.SendAsync(BloodCastlePacketBuilder.BloodCastleEnterSend(0), ct);
        await session.SendEncryptedAsync(WorldPacketBuilder.TeleportSend(1, player.Map, player.X, player.Y, player.Dir), ct);

        BroadcastState(lvl);
    }

    private void BroadcastState(BloodCastleLevel lvl)
    {
        byte stateByte = lvl.State switch
        {
            BloodCastleState.Stand => 1,
            BloodCastleState.Start => 2,
            BloodCastleState.Clean => 3,
            _ => 0
        };

        var packet = BloodCastlePacketBuilder.BloodCastleStateSend(
            stateByte, (ushort)lvl.TimeRemainingSeconds, lvl.MaxMonsters, lvl.CurrentMonsters, lvl.ArchangelWeaponOwnerIndex, (byte)lvl.Level);

        foreach (var pIndex in lvl.PlayerIndices)
        {
            if (_players.TryGet(pIndex, out var p) && p.WorldEntered)
            {
                _ = p.Session.SendAsync(packet, CancellationToken.None);
            }
        }
    }

    private static int GetMinLevel(int heroClass, int bcLevel)
    {
        bool isSpecial = heroClass == 3 || heroClass == 4; // MG / DL
        return bcLevel switch
        {
            1 => isSpecial ? 10 : 15,
            2 => isSpecial ? 61 : 81,
            3 => isSpecial ? 111 : 131,
            4 => isSpecial ? 161 : 181,
            5 => isSpecial ? 211 : 231,
            6 => isSpecial ? 261 : 281,
            _ => 15
        };
    }

    private static int GetMaxLevel(int heroClass, int bcLevel)
    {
        bool isSpecial = heroClass == 3 || heroClass == 4; // MG / DL
        return bcLevel switch
        {
            1 => isSpecial ? 60 : 80,
            2 => isSpecial ? 110 : 130,
            3 => isSpecial ? 160 : 180,
            4 => isSpecial ? 210 : 230,
            5 => isSpecial ? 260 : 280,
            6 => 400,
            _ => 400
        };
    }
}

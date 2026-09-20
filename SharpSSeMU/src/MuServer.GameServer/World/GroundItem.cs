using System.Collections.Concurrent;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto simplificado de CMapItem (MapItem.h:11-30) -- un item tirado en el piso de un mapa.
/// A diferencia de jugadores/monstruos (registrados en un índice GLOBAL, ver
/// <see cref="MonsterRegistry"/>), los items de piso se indexan POR MAPA (<see cref="Index"/> es la
/// posición dentro del array de 300 slots de <em>ese</em> mapa, <c>gMap[map].m_Item[300]</c> en el
/// original, ver Map.h:12,159-171) -- el protocolo real (PMSG_VIEWPORT_ITEM.index, ver
/// WorldPacketBuilder.ViewportItemAppear) también asume esta indexación por mapa, así que replicarla
/// tal cual evita tener que traducir índices en el paquete.
///
/// Simplificaciones respecto al original (documentadas en detalle en el README): sin el estado
/// "m_Give" de dos fases para evitar doble-pickup en el mismo tick (acá <see cref="Live"/> se pone en
/// false inmediatamente al recoger, y el barrido de <see cref="GroundItemRegistry.Sweep"/> libera el
/// slot en el tick siguiente iguel que el original, pero sin la ventana de carrera intermedia -- en la
/// práctica cada pickup ya se procesa en el hilo único del protocolo, así que la condición de carrera
/// que <c>m_Give</c> prevenía en el original multihilo no puede pasar acá).
/// </summary>
public sealed class GroundItem
{
    public required int Index { get; init; }
    public required int Map { get; init; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public required Item Item { get; init; }

    /// <summary>Puerto del caso especial "dinero tirado en el piso" (CMap::MoneyItemDrop,
    /// Map.cpp:256-290): el original reusa <c>CItem</c> con <c>m_Index=GET_ITEM(14,15)</c> y guarda
    /// el monto en <c>m_BuyMoney</c> -- acá se separa en un campo dedicado para no forzar el "monto"
    /// dentro de <see cref="Item"/> (que no tiene un campo de cantidad dinámica). null = item normal,
    /// no-null = este slot es dinero (<see cref="Item"/> igual se deja con Index=GetItem(14,15) para
    /// que el resto del código que compara por índice funcione igual que el original).</summary>
    public uint? MoneyAmount { get; init; }

    /// <summary>Puerto de m_Live -- true mientras el slot tiene un item real tirado.</summary>
    public bool Live { get; set; } = true;

    /// <summary>Puerto de la condición <c>m_State==OBJECT_CREATE</c> que prende el bit "recién
    /// apareció" (0x80 en el byte alto del índice) en <see cref="WorldPacketBuilder.ViewportItemAppear"/>
    /// -- solo se manda así la primera vez que un jugador lo ve (el barrido de viewport lo apaga
    /// después de mandarlo una vez, ver <see cref="ViewportTicker"/>).</summary>
    public bool JustDropped { get; set; } = true;

    /// <summary>Puerto de m_Time -- momento en que el item desaparece solo (timeout), sin importar si
    /// alguien lo recogió o no.</summary>
    public DateTime ExpireAt { get; set; }

    /// <summary>Puerto de m_LootTime + m_UserIndex/m_LootUserIndex (MapItem.cpp:29-99): mientras
    /// ahora &lt; LootLockUntil, solo <see cref="OwnerIndex"/> (o su grupo, ver
    /// <see cref="OwnerPartyId"/>) puede recogerlo -- CItemManager::CGItemGetRecv, vía
    /// CMap::CheckItemGive (Map.cpp:363-419).</summary>
    public DateTime LootLockUntil { get; set; }

    /// <summary>-1 = sin dueño (nadie tiene prioridad de loot, cualquiera puede recogerlo apenas
    /// aparece -- no hay caso real de esto en este puerto ya que todo drop viene de un jugador o
    /// monstruo con un "dueño" identificable, pero se deja el sentinel por si acaso).</summary>
    public int OwnerIndex { get; set; } = -1;

    /// <summary>Grupo del dueño al momento del drop -- si el dueño estaba en un grupo, todo el grupo
    /// comparte la prioridad de loot (CMap::CheckItemGive, Map.cpp:363-419, salvo items de quest,
    /// <c>m_QuestItem</c>, no portado -- se ignora esa excepción acá).</summary>
    public int? OwnerPartyId { get; set; }
}

/// <summary>
/// Puerto simplificado de <c>gMap[map].m_Item[MAX_MAP_ITEM=300]</c> + el ring-buffer de asignación de
/// slot de <c>CMap::MonsterItemDrop</c>/<c>ItemDrop</c>/<c>MoneyItemDrop</c> (Map.cpp:256-361) -- un
/// array de 300 slots POR MAPA, reusados en orden cuando se liberan.
/// </summary>
public sealed class GroundItemRegistry
{
    /// <summary>MAX_MAP_ITEM (Map.h:12).</summary>
    public const int SlotsPerMap = 300;

    private readonly ConcurrentDictionary<int, GroundItem?[]> _byMap = new();
    private readonly ConcurrentDictionary<int, int> _cursorByMap = new();
    private readonly object _dropLock = new();

    public IEnumerable<GroundItem> AllOnMap(int map)
    {
        if (!_byMap.TryGetValue(map, out var arr))
        {
            yield break;
        }

        foreach (var item in arr)
        {
            if (item is { Live: true })
            {
                yield return item;
            }
        }
    }

    public GroundItem? Get(int map, int index)
    {
        if (index < 0 || index >= SlotsPerMap || !_byMap.TryGetValue(map, out var arr))
        {
            return null;
        }

        var item = arr[index];
        return item is { Live: true } ? item : null;
    }

    /// <summary>Puerto de CMap::MonsterItemDrop/ItemDrop/MoneyItemDrop (Map.cpp:256-361) -- busca el
    /// próximo slot libre en el ring-buffer de este mapa a partir del cursor, ocupa el primero que
    /// encuentre. Devuelve null si el mapa ya tiene los 300 slots ocupados (drop se descarta en
    /// silencio, igual que el original).</summary>
    public GroundItem? Drop(int map, Item item, int x, int y, int ownerIndex, int? ownerPartyId, TimeSpan lifetime, TimeSpan lootLock)
    {
        lock (_dropLock)
        {
            var arr = _byMap.GetOrAdd(map, static _ => new GroundItem?[SlotsPerMap]);
            int cursor = _cursorByMap.GetOrAdd(map, 0);

            for (int n = 0; n < SlotsPerMap; n++)
            {
                int idx = (cursor + n) % SlotsPerMap;

                if (arr[idx] is not { Live: true })
                {
                    var now = DateTime.UtcNow;

                    var ground = new GroundItem
                    {
                        Index = idx,
                        Map = map,
                        X = (byte)x,
                        Y = (byte)y,
                        Item = item,
                        ExpireAt = now + lifetime,
                        LootLockUntil = now + lootLock,
                        OwnerIndex = ownerIndex,
                        OwnerPartyId = ownerPartyId,
                    };

                    arr[idx] = ground;
                    _cursorByMap[map] = (idx + 1) % SlotsPerMap;
                    return ground;
                }
            }

            return null; // mapa lleno de items en el piso -- drop descartado, igual que el original
        }
    }

    /// <summary>Puerto de CMap::MoneyItemDrop (Map.cpp:256-290): mismo mecanismo de slot que
    /// <see cref="Drop"/> pero sin loot-lock (<c>m_LootTime=0</c> en el original -- cualquiera puede
    /// levantarlo desde que aparece, no solo quien lo generó).</summary>
    public GroundItem? DropMoney(int map, int x, int y, uint amount, TimeSpan lifetime)
    {
        var moneyItem = new Item { Index = (short)Item.GetItem(14, 15) };
        var ground = Drop(map, moneyItem, x, y, ownerIndex: -1, ownerPartyId: null, lifetime, lootLock: TimeSpan.Zero);

        if (ground == null)
        {
            return null;
        }

        // GroundItem.MoneyAmount es 'init'-only -- Drop() ya construyó el objeto, así que se arma de
        // nuevo acá con el monto puesto (mismo slot, se pisa el que Drop() acaba de guardar).
        var withMoney = new GroundItem
        {
            Index = ground.Index, Map = ground.Map, X = ground.X, Y = ground.Y, Item = ground.Item,
            MoneyAmount = amount, ExpireAt = ground.ExpireAt, LootLockUntil = ground.LootLockUntil,
            OwnerIndex = ground.OwnerIndex, OwnerPartyId = ground.OwnerPartyId,
        };

        ReplaceSlot(map, ground.Index, withMoney);
        return withMoney;
    }

    private void ReplaceSlot(int map, int index, GroundItem item)
    {
        if (_byMap.TryGetValue(map, out var arr))
        {
            arr[index] = item;
        }
    }

    /// <summary>Puerto de CMap::ItemGive (Map.cpp:421-431) -- marca el item recogido/expirado. El
    /// slot queda libre para <see cref="Drop"/> de inmediato (ver doc-comment de
    /// <see cref="GroundItem"/> sobre por qué no hace falta la ventana de dos fases del original).</summary>
    public void Remove(GroundItem item) => item.Live = false;

    /// <summary>Puerto de CMap::StateSetDestroy (Map.cpp:433-474), simplificado a una sola pasada:
    /// libera cualquier item cuyo timeout ya pasó. Llamado desde <see cref="ViewportTicker"/> en cada
    /// tick.</summary>
    public IEnumerable<GroundItem> SweepExpired()
    {
        var now = DateTime.UtcNow;
        var expired = new List<GroundItem>();

        foreach (var arr in _byMap.Values)
        {
            foreach (var item in arr)
            {
                if (item is { Live: true } && now > item.ExpireAt)
                {
                    item.Live = false;
                    expired.Add(item);
                }
            }
        }

        return expired;
    }
}

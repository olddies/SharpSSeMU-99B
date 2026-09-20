using System.Collections.Concurrent;

namespace MuServer.GameServer.World;

/// <summary> Simplified port of CMapItem (MapItem.h:11-30) -- an item lying on the ground of a map. Unlike
/// players/monsters (registered in a GLOBAL index, see <see cref="MonsterRegistry"/>), ground items are indexed
/// PER MAP (<see cref="Index"/> is the position within the 300-slot array of <em>that</em> map,
/// <c>gMap[map].m_Item[300]</c> in the original, see Map.h:12,159-171) -- the real protocol
/// (PMSG_VIEWPORT_ITEM.index, see WorldPacketBuilder.ViewportItemAppear) also assumes this per-map indexing, so
/// replicating it as is avoids having to translate indices in the packet. Simplifications relative to the
/// original (documented in detail in the README): without the two-phase "m_Give" state to avoid a double-pickup
/// in the same tick (here <see cref="Live"/> is set to false immediately on pickup, and the <see
/// cref="GroundItemRegistry.Sweep"/> sweep frees the slot on the next tick just like the original, but without
/// the intermediate race window -- in practice each pickup is already processed on the protocol's single
/// thread, so the race condition that <c>m_Give</c> prevented in the multi-threaded original cannot happen
/// here). </summary>
public sealed class GroundItem
{
    public required int Index { get; init; }
    public required int Map { get; init; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public required Item Item { get; init; }

    /// <summary>Port of the special case "money dropped on the ground" (CMap::MoneyItemDrop, Map.cpp:256-290):
    /// the original reuses <c>CItem</c> with <c>m_Index=GET_ITEM(14,15)</c> and stores the amount in
    /// <c>m_BuyMoney</c> -- here it is separated into a dedicated field so as not to force the "amount" inside
    /// <see cref="Item"/> (which has no dynamic quantity field). null = normal item, non-null = this slot is
    /// money (<see cref="Item"/> is still left with Index=GetItem(14,15) so that the rest of the code that
    /// compares by index works the same as the original).</summary>
    public uint? MoneyAmount { get; init; }

    /// <summary>Puerto de m_Live -- true mientras el slot tiene un item real tirado.</summary>
    public bool Live { get; set; } = true;

    /// <summary>Port of the <c>m_State==OBJECT_CREATE</c> condition that turns on the "just appeared" bit (0x80
    /// in the high byte of the index) in <see cref="WorldPacketBuilder.ViewportItemAppear"/> -- it is only sent
    /// like that the first time a player sees it (the viewport sweep turns it off after sending it once, see
    /// <see cref="ViewportTicker"/>).</summary>
    public bool JustDropped { get; set; } = true;

    /// <summary>Port of m_Time -- moment when the item disappears on its own (timeout), regardless of whether
    /// someone picked it up or not.</summary>
    public DateTime ExpireAt { get; set; }

    /// <summary>Port of m_LootTime + m_UserIndex/m_LootUserIndex (MapItem.cpp:29-99): while now &lt;
    /// LootLockUntil, only <see cref="OwnerIndex"/> (or their party, see <see cref="OwnerPartyId"/>) can pick
    /// it up -- CItemManager::CGItemGetRecv, via CMap::CheckItemGive (Map.cpp:363-419).</summary>
    public DateTime LootLockUntil { get; set; }

    /// <summary>-1 = no owner (nobody has loot priority, anyone can pick it up as soon as it appears -- there
    /// is no real case of this in this port since every drop comes from a player or monster with an
    /// identifiable "owner", but the sentinel is left just in case).</summary>
    public int OwnerIndex { get; set; } = -1;

    /// <summary>Owner's party at the moment of the drop -- if the owner was in a party, the whole party shares
    /// the loot priority (CMap::CheckItemGive, Map.cpp:363-419, except quest items, <c>m_QuestItem</c>, not
    /// ported -- that exception is ignored here).</summary>
    public int? OwnerPartyId { get; set; }
}

/// <summary> Simplified port of <c>gMap[map].m_Item[MAX_MAP_ITEM=300]</c> + the slot-allocation ring buffer of
/// <c>CMap::MonsterItemDrop</c>/<c>ItemDrop</c>/<c>MoneyItemDrop</c> (Map.cpp:256-361) -- an array of 300 slots
/// PER MAP, reused in order as they are freed. </summary>
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

    /// <summary>Port of CMap::MonsterItemDrop/ItemDrop/MoneyItemDrop (Map.cpp:256-361) -- looks for the next
    /// free slot in this map's ring buffer starting from the cursor, taking the first one it finds. Returns
    /// null if the map already has all 300 slots occupied (the drop is silently discarded, like the
    /// original).</summary>
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

    /// <summary>Port of CMap::MoneyItemDrop (Map.cpp:256-290): same slot mechanism as <see cref="Drop"/> but
    /// without loot-lock (<c>m_LootTime=0</c> in the original -- anyone can pick it up from the moment it
    /// appears, not only whoever generated it).</summary>
    public GroundItem? DropMoney(int map, int x, int y, uint amount, TimeSpan lifetime)
    {
        var moneyItem = new Item { Index = (short)Item.GetItem(14, 15) };
        var ground = Drop(map, moneyItem, x, y, ownerIndex: -1, ownerPartyId: null, lifetime, lootLock: TimeSpan.Zero);

        if (ground == null)
        {
            return null;
        }

        // GroundItem.MoneyAmount is 'init'-only -- Drop() already built the object, so it is built again here
        // with the amount set (same slot, it overwrites the one Drop() just stored).
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

    /// <summary>Port of CMap::ItemGive (Map.cpp:421-431) -- marks the item as picked up/expired. The slot is
    /// left free for <see cref="Drop"/> immediately (see the doc-comment of <see cref="GroundItem"/> about why
    /// the original's two-phase window is not needed).</summary>
    public void Remove(GroundItem item) => item.Live = false;

    /// <summary>Port of CMap::StateSetDestroy (Map.cpp:433-474), simplified to a single pass: frees any item
    /// whose timeout has passed. Called from <see cref="ViewportTicker"/> on every tick.</summary>
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

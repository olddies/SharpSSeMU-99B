using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> Port of the "NPC shop" (ShopManager.h/.cpp + Shop.h/.cpp) -- first pass, minimal scope to buy/sell
/// at a fixed NPC. Without Trade (player-to-player), Warehouse nor Personal Shop yet (same scope criterion as
/// the rest of the project -- see README). A "shop" in the original is ALWAYS owned by a specific NPC,
/// identified by its monster class index (NPCIndex column of ShopManager.txt, which matches the class index in
/// MonsterList.txt for that NPC row) -- that same number is used as <see
/// cref="ShopManagerTable.GetShopNumber"/> (the original resolves by class+map+position because an NPC can have
/// its shop reassigned by Lua at runtime; here, without Lua ported, the class index is enough and is 1:1 with
/// the shop). </summary>
public sealed class ShopInfo
{
    public required int NpcClass { get; init; } // = ShopNumber en este puerto (ver comentario de arriba)
    public required int Map { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required int Dir { get; init; }
    public string Name { get; init; } = string.Empty;

    public const int Columns = 8;
    public const int Rows = 15;
    public const int Size = Columns * Rows; // 120, SHOP_SIZE del original

    /// <summary>Already packed slot grid (see <see cref="ShopManagerTable.PackItems"/>) -- null = empty
    /// slot/occupied by the top-left corner of another larger item.</summary>
    public Item?[] Slots { get; } = new Item?[Size];

    public int ItemCount => Slots.Count(s => s != null);
}

/// <summary> Port of CShopManager::Load/ReloadShop (ShopManager.cpp:29-150) + CShop::Load/InsertItem
/// (Shop.cpp:35-95) -- loads ShopManager.txt (flat list of shop NPCs) and, for each one, the item file
/// Data/Shop/&lt;ShopPath&gt;.txt, packing them into an 8x15 grid with the same first-free-slot algorithm
/// (top-left first-fit) that the original's <c>ShopRectCheck</c> uses, based on the Width/Height of <see
/// cref="ItemBalanceTable"/> (already loaded by Phase 4 second pass, real item balance). </summary>
public sealed class ShopManagerTable
{
    private readonly Dictionary<int, ShopInfo> _byNpcClass = new();

    public ShopInfo? Get(int npcClass) => _byNpcClass.GetValueOrDefault(npcClass);

    public IEnumerable<ShopInfo> All => _byNpcClass.Values;

    /// <summary>Port of CMonsterSetBase::LoadSpawn -- the same as the monster spawn files, although here
    /// <paramref name="shopManagerPath"/> is a single flat file (not one per map), so there is no need to scan
    /// a directory.</summary>
    public int Load(string shopManagerPath, string shopDataDir, ItemBalanceTable items)
    {
        var script = new MemScript();

        if (!script.SetBuffer(shopManagerPath))
        {
            Log.Add(LogColor.Red, "[ShopManagerTable] {0}", script.GetLastError());
            return 0;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            if (script.GetString() == "end")
            {
                break;
            }

            int npcClass = script.GetNumber();
            int map = script.GetAsNumber();
            int x = script.GetAsNumber();
            int y = script.GetAsNumber();
            int dir = script.GetAsNumber();
            script.GetAsNumber(); // AL0 -- NPC visibility restriction by account level, not ported
            script.GetAsNumber(); // AL1
            script.GetAsNumber(); // AL2
            script.GetAsNumber(); // AL3
            script.GetAsNumber(); // GMLevel ('*' = -1 = no restriction)
            string shopPath = script.GetAsString();

            var shop = new ShopInfo { NpcClass = npcClass, Map = map, X = x, Y = y, Dir = dir, Name = shopPath };

            var itemFile = Path.Combine(shopDataDir, shopPath + ".txt");
            PackItems(shop, LoadShopItems(itemFile), items);

            _byNpcClass[npcClass] = shop;
        }

        Log.Add(LogColor.Blue, "[ShopManagerTable] {0} shop(s) loaded from {1}", _byNpcClass.Count, shopManagerPath);
        return _byNpcClass.Count;
    }

    /// <summary>Port of CShop::Load (Shop.cpp:35-95) -- ItemIndex comes as "section,sub-index" WITHOUT a space
    /// around the comma (e.g. "14,000"): the tokenizer (MemScript.GetTokenNumber) does not recognise ',' as
    /// part of a number, so it cuts at "14" and leaves the comma as the next character -- it is discarded with
    /// an extra GetToken() before reading the sub-index.</summary>
    private static List<Item> LoadShopItems(string path)
    {
        var result = new List<Item>();
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ShopManagerTable] {0}", script.GetLastError());
            return result;
        }

        while (true)
        {
            if (script.GetToken() == TokenResult.End)
            {
                break;
            }

            if (script.GetString() == "end")
            {
                break;
            }

            int section = script.GetNumber();
            script.GetToken(); // discards the ',' between section and sub-index
            int sub = script.GetAsNumber();
            int level = script.GetAsNumber();
            int durability = script.GetAsNumber();
            int skill = script.GetAsNumber();
            int luck = script.GetAsNumber();
            int additional = script.GetAsNumber();
            int excellent = script.GetAsNumber();
            int setOption = script.GetAsNumber();

            byte option3 = 0;

            if (luck != 0)
            {
                option3 |= 1;
            }

            // Excellent: bits 0-5 of the file -> bits 0-1 in Option3, bit ">3" (that is, any bit >= 4th) also
            // goes to Option3 (see Item.ToWireBytes) -- simplified port of CItem::SetExcellentOption, enough to
            // represent any combination on the wire.
            option3 |= (byte)(excellent & 3);

            result.Add(new Item
            {
                Index = (short)Item.GetItem(section, sub),
                Level = (byte)Math.Clamp(level, 0, 15),
                Durability = (byte)Math.Clamp(durability, 0, 255),
                Option1 = (byte)(luck != 0 ? 1 : 0),
                Option2 = (byte)(skill != 0 ? 1 : 0),
                Option3 = option3,
                NewOption = (byte)(additional & 0x3F),
                SetOption = (byte)(setOption & 15),
            });
        }

        return result;
    }

    /// <summary>Port of ShopRectCheck/ShopItemSet (Shop.cpp) -- first-free-slot scanning the grid in reading
    /// order (row by row, left to right), like the original. An item that does not fit in any free slot is
    /// discarded (the same behaviour as the original when the shop file has more rows than fit in
    /// 8x15).</summary>
    private static void PackItems(ShopInfo shop, List<Item> itemsToPlace, ItemBalanceTable balance)
    {
        foreach (var item in itemsToPlace)
        {
            var info = balance.Get(item.Index);
            int w = Math.Max(info?.Width ?? 1, 1);
            int h = Math.Max(info?.Height ?? 1, 1);

            if (!TryFindFreeRect(shop, w, h, out int slot))
            {
                continue;
            }

            shop.Slots[slot] = item;

            int baseX = slot % ShopInfo.Columns;
            int baseY = slot / ShopInfo.Columns;

            for (int dy = 0; dy < h; dy++)
            {
                for (int dx = 0; dx < w; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue; // la esquina ya tiene el item real
                    }

                    int occupiedSlot = ((baseY + dy) * ShopInfo.Columns) + (baseX + dx);

                    if (occupiedSlot < ShopInfo.Size)
                    {
                        // "Occupied by another item" marker -- Durability=0/Index=-2 so as not to be confused
                        // with a really empty slot (Index=-1) nor with a real item.
                        shop.Slots[occupiedSlot] = OccupiedMarker;
                    }
                }
            }
        }
    }

    private static readonly Item OccupiedMarker = new() { Index = -2 };

    private static bool TryFindFreeRect(ShopInfo shop, int w, int h, out int slot)
    {
        for (int y = 0; y <= ShopInfo.Rows - h; y++)
        {
            for (int x = 0; x <= ShopInfo.Columns - w; x++)
            {
                if (RectFree(shop, x, y, w, h))
                {
                    slot = (y * ShopInfo.Columns) + x;
                    return true;
                }
            }
        }

        slot = -1;
        return false;
    }

    private static bool RectFree(ShopInfo shop, int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
        {
            for (int dx = 0; dx < w; dx++)
            {
                if (shop.Slots[((y + dy) * ShopInfo.Columns) + (x + dx)] != null)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

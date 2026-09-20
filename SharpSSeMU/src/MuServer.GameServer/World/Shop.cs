using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto de "tienda de NPC" (ShopManager.h/.cpp + Shop.h/.cpp) -- primera pasada, alcance mínimo
/// para comprar/vender en un NPC fijo. Sin Trade (jugador-a-jugador), Warehouse ni Personal Shop
/// todavía (mismo criterio de alcance que el resto del proyecto -- ver README).
///
/// Un "shop" en el original es SIEMPRE dueño de un NPC específico, identificado por su índice de
/// clase de monstruo (columna NPCIndex de ShopManager.txt, que coincide con el índice de clase en
/// MonsterList.txt para esa fila de NPC) -- ese mismo número se usa como <see cref="ShopManagerTable.GetShopNumber"/>
/// (el original resuelve por clase+mapa+posición porque un NPC puede tener el shop reasignado por
/// Lua en runtime; acá, sin Lua portado, el índice de clase alcanza y es 1:1 con el shop).
/// </summary>
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

    /// <summary>Grilla de slots ya empaquetada (ver <see cref="ShopManagerTable.PackItems"/>) -- null =
    /// slot vacío/ocupado por la esquina superior-izquierda de otro item más grande.</summary>
    public Item?[] Slots { get; } = new Item?[Size];

    public int ItemCount => Slots.Count(s => s != null);
}

/// <summary>
/// Puerto de CShopManager::Load/ReloadShop (ShopManager.cpp:29-150) + CShop::Load/InsertItem
/// (Shop.cpp:35-95) -- carga ShopManager.txt (lista plana de NPCs-tienda) y, para cada uno, el
/// archivo de items de Data/Shop/&lt;ShopPath&gt;.txt, empaquetándolos en una grilla de 8x15 con el
/// mismo algoritmo de primer-hueco-libre (top-left first-fit) que usa <c>ShopRectCheck</c> del
/// original, basado en Width/Height de <see cref="ItemBalanceTable"/> (ya cargada por la Fase 4
/// segunda pasada, balance real de items).
/// </summary>
public sealed class ShopManagerTable
{
    private readonly Dictionary<int, ShopInfo> _byNpcClass = new();

    public ShopInfo? Get(int npcClass) => _byNpcClass.GetValueOrDefault(npcClass);

    public IEnumerable<ShopInfo> All => _byNpcClass.Values;

    /// <summary>Puerto de CMonsterSetBase::LoadSpawn -- igual que los archivos de spawn de monstruos,
    /// aunque acá <paramref name="shopManagerPath"/> es un único archivo plano (no uno por mapa), así
    /// que no hace falta escanear un directorio.</summary>
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
            script.GetAsNumber(); // AL0 -- restricción de visibilidad del NPC por nivel de cuenta, no portada
            script.GetAsNumber(); // AL1
            script.GetAsNumber(); // AL2
            script.GetAsNumber(); // AL3
            script.GetAsNumber(); // GMLevel ('*' = -1 = sin restricción)
            string shopPath = script.GetAsString();

            var shop = new ShopInfo { NpcClass = npcClass, Map = map, X = x, Y = y, Dir = dir, Name = shopPath };

            var itemFile = Path.Combine(shopDataDir, shopPath + ".txt");
            PackItems(shop, LoadShopItems(itemFile), items);

            _byNpcClass[npcClass] = shop;
        }

        Log.Add(LogColor.Blue, "[ShopManagerTable] {0} shop(s) loaded from {1}", _byNpcClass.Count, shopManagerPath);
        return _byNpcClass.Count;
    }

    /// <summary>Puerto de CShop::Load (Shop.cpp:35-95) -- ItemIndex viene como "sección,subíndice" SIN
    /// espacio alrededor de la coma (ej. "14,000"): el tokenizer (MemScript.GetTokenNumber) no
    /// reconoce ',' como parte de un número, así que corta en "14" y deja la coma como próximo
    /// carácter -- se la descarta con un GetToken() extra antes de leer el subíndice.</summary>
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
            script.GetToken(); // descarta la ',' entre sección y subíndice
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

            // Excelente: bits 0-5 del archivo -> bits 0-1 en Option3, bit ">3" (o sea, cualquier bit
            // >= 4to) también va a Option3 (ver Item.ToWireBytes) -- puerto simplificado de
            // CItem::SetExcellentOption, alcanza para representar cualquier combinación en el wire.
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

    /// <summary>Puerto de ShopRectCheck/ShopItemSet (Shop.cpp) -- primer-hueco-libre escaneando la
    /// grilla en orden de lectura (fila por fila, izquierda a derecha), igual que el original. Un
    /// item que no entra en ningún hueco libre se descarta (mismo comportamiento que el original
    /// cuando el archivo de tienda tiene más filas de las que caben en 8x15).</summary>
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
                        // Marcador de "ocupado por otro item" -- Durability=0/Index=-2 para no
                        // confundirse con un slot realmente vacío (Index=-1) ni con un item real.
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

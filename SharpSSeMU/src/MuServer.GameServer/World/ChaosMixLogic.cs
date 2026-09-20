using MuServer.GameServer.Config;

namespace MuServer.GameServer.World;

/// <summary>
/// Los valores tal como viajan en <c>PMSG_CHAOS_MIX_RECV</c>/<c>_RATE_RECV</c> (<c>type</c>). Nombrados
/// como en <c>ChaosBox.h</c> del emulador real -- <b>ojo</b>, hay una trampa de nombres ahí: la
/// constante <c>CHAOS_MIX_WING1</c> (7) dispara la función <c>Wing2Mix(tipo=0)</c>, y
/// <c>CHAOS_MIX_WING2</c> (11) dispara <c>Wing1Mix()</c> -- están cruzadas. Acá el valor del enum es
/// el del WIRE (lo que manda el cliente), y el comentario de cada caso en el switch de abajo dice qué
/// función del original ejecuta de verdad.
/// </summary>
public enum ChaosMixType
{
    None = 0,
    ChaosItem = 1,
    DevilSquare = 2,
    PlusItem10 = 3,
    PlusItem11 = 4,
    Dinorant = 5,
    Fruit = 6,
    Wing1 = 7,
    BloodCastle = 8,
    Wing2 = 11,
    Wing3 = 24, // "Cape" en el original -- no estaba en el puerto anterior, se agrega acá.
}

/// <param name="Item">El item nuevo o mejorado, si hubo éxito.</param>
/// <param name="DeliverViaInventory">true si <see cref="Item"/> se entrega poniéndolo en un slot
/// vacío del inventario (el original lo hace por <c>GDCreateItemSend</c>, un mensaje al DataServer,
/// no por el mismo paquete de la mezcla); false si va embebido en la respuesta de combinar
/// (<c>PMSG_CHAOS_MIX_SEND.ItemInfo</c>), que es como el original entrega la mejora de
/// Plus Item Level -- ahí el item es el MISMO que pusiste, no uno nuevo.</param>
public sealed record ChaosMixResult(bool Success, byte ResultCode, Item? Item, bool DeliverViaInventory,
    int SuccessRate, int RequiredZen);

/// <summary>
/// Puerto de las fórmulas de combinaciones de <c>CChaosBox</c> (ChaosBox.cpp de
/// <c>Source/Emulator 0.99 (2.1.7)/GameServer</c>, el árbol fuente real del emulador -- no una
/// reconstrucción). La versión anterior de esta clase decía ser un puerto exacto y no lo era: la
/// tasa de éxito salía de una fórmula inventada (`10 + Σ nivel×5`) en vez de la tabla de
/// configuración real (<see cref="GameServerInfoChaosMix"/>, cargada desde hace tiempo pero nunca
/// conectada a nada), el item de éxito salía de un array de 3 armas fijas en vez de la lista real
/// de <c>Data/EventItemBag/Special/*.txt</c>, y el resultado de éxito se empaquetaba distinto según
/// el tipo de mezcla y esta clase lo hacía siempre igual.
///
/// <para><b>Lo que SÍ es fiel:</b> las condiciones de ingredientes, las fórmulas de tasa y de zen
/// requerido, y de qué tabla de configuración sale cada una -- verificado línea por línea contra el
/// original, incluida la trampa de nombres Wing1/Wing2 de arriba.</para>
///
/// <para><b>Lo que NO es fiel, y por qué:</b> el item que sale en las mezclas que crean un item
/// NUEVO (Chaos Item, Wing1/2/3, Fruit) se elige al azar entre los candidatos reales de
/// <c>Data/EventItemBag/Special/*.txt</c> -- esos SÍ son los candidatos reales, verificados contra
/// el archivo -- pero se ignora la puerta de <c>DropRate</c> del motor de bolsas de item
/// (<c>ItemBagEx</c>): en los cuatro archivos que hacen falta para esto, ese campo viene en 0 de
/// fábrica, lo que en el original haría que la bolsa NUNCA devuelva nada aun con la tirada de éxito
/// ya ganada. Replicar ese comportamiento literal dejaría la Chaos Box "gana la tirada, no pasa
/// nada" tal como viene el archivo -- casi seguro no es el comportamiento real de un servidor en
/// producción (nadie shippea una máquina del caos que nunca entrega nada), y el motor completo de
/// <c>ItemBagEx</c> (pesos por sección, filtro por clase, drop rate) es un sistema aparte, compartido
/// con Devil Square/Blood Castle/drops de monstruo, que merece su propio puerto en vez de uno
/// apurado como dependencia de esto.</para>
///
/// <para>Devil Square, Dinorant, Blood Castle y las dos mezclas de mascota siguen sin portar (como
/// antes): requieren estado de eventos en vivo que este servidor no trackea todavía.</para>
/// </summary>
public static class ChaosMixLogic
{
    private static readonly int ItemChaos = Item.GetItem(12, 15);   // Jewel of Chaos
    private static readonly int ItemBless = Item.GetItem(14, 13);   // Jewel of Bless
    private static readonly int ItemSoul = Item.GetItem(14, 14);    // Jewel of Soul
    private static readonly int ItemCreation = Item.GetItem(14, 22); // Jewel of Creation
    private static readonly int ItemLife = Item.GetItem(14, 16);    // Jewel of Life
    private static readonly int ItemDinorant = Item.GetItem(13, 3);
    private static readonly int ItemFeatherLevel0 = Item.GetItem(13, 14); // Loch's Feather / Crest of Monarch (Level distingue cuál)

    private static readonly int[] ChaosWeapons = { Item.GetItem(2, 6), Item.GetItem(4, 6), Item.GetItem(5, 7) };
    private static readonly int[] Wing1Candidates = { Item.GetItem(12, 0), Item.GetItem(12, 1), Item.GetItem(12, 2) };
    private static readonly int[] Wing2Candidates = { Item.GetItem(12, 3), Item.GetItem(12, 4), Item.GetItem(12, 5), Item.GetItem(12, 6) };
    private static readonly int[] CapeCandidates = { Item.GetItem(13, 30) };

    /// <param name="getBuyMoney">Precio de compra ACTUAL del item (el que ya calcula
    /// <c>ComputeShopBuyPrice</c> para la tienda) -- lo pasa quien llama para no duplicar esa
    /// fórmula acá.</param>
    /// <param name="execute">true para combinar de verdad (0x86). false para sólo calcular la tasa
    /// (0x88): misma fórmula, no cobra ni vacía la caja ni tira el dado.</param>
    public static ChaosMixResult CalculateAndExecuteMix(PlayerObject player, ChaosMixType mixType,
        GameServerInfoChaosMix rates, int[] addLuckSuccessRate2, Func<Item, int> getBuyMoney, bool execute = true)
    {
        var box = player.ChaosBoxItems;
        var items = box.Where(i => i.IsItem()).ToList();

        if (items.Count == 0)
        {
            return new ChaosMixResult(false, 0, null, false, 0, 0);
        }

        return mixType switch
        {
            // CHAOS_MIX_CHAOS_ITEM -> CChaosBox::ChaosItemMix
            ChaosMixType.ChaosItem => MixChaosItem(player, items, rates, getBuyMoney, execute),

            // CHAOS_MIX_PLUS_ITEM_LEVEL1 (3) -> PlusItemLevelMix(tipo=0): tira +9 a +10
            ChaosMixType.PlusItem10 => MixPlusItemLevel(player, items, rates, addLuckSuccessRate2, getBuyMoney, 0, execute),

            // CHAOS_MIX_PLUS_ITEM_LEVEL2 (4) -> PlusItemLevelMix(tipo=1): tira +10 a +11
            ChaosMixType.PlusItem11 => MixPlusItemLevel(player, items, rates, addLuckSuccessRate2, getBuyMoney, 1, execute),

            // CHAOS_MIX_FRUIT -> CChaosBox::FruitMix
            ChaosMixType.Fruit => MixFruit(player, items, rates, execute),

            // CHAOS_MIX_WING1 (7) -> ¡OJO! en el original esto ejecuta Wing2Mix(tipo=0): Chaos +
            // Loch's Feather (nivel 0) + un ala de 1ra generación -> ala de 2da (Spirits/Soul/
            // Dragon/Darkness).
            ChaosMixType.Wing1 => MixWing2Family(player, items, rates, getBuyMoney, wingTier: 0, execute),

            // CHAOS_MIX_WING2 (11) -> ¡OJO! esto ejecuta Wing1Mix(): Chaos + un arma del caos
            // excelente -> ala de 1ra generación (Elf/Heaven/Satan).
            ChaosMixType.Wing2 => MixWing1(player, items, rates, getBuyMoney, execute),

            // "Cape" en el original -- Wing2Mix(tipo=1): Chaos + Crest of Monarch (nivel 1 del
            // mismo item que Loch's Feather) + un ala de 2da -> Cape of Lord.
            ChaosMixType.Wing3 => MixWing2Family(player, items, rates, getBuyMoney, wingTier: 1, execute),

            _ => new ChaosMixResult(false, 0, null, false, 0, 0), // DevilSquare/Dinorant/BloodCastle: no portado
        };
    }

    /// <summary>Puerto de <c>CItem::OldValue()</c> (Item.cpp:925-951): la fórmula de tasa de la
    /// Chaos Box usa estos precios "viejos" para cinco joyas puntuales, NO el precio actual de
    /// <c>ItemValue.txt</c> -- son deliberadamente distintos (más bajos). Usar el precio actual acá
    /// haría que cualquier combinación con un Chaos/Bless/Soul llegue al 100% de una sola vez, porque
    /// esos precios subieron mucho desde que se escribió esta fórmula.</summary>
    private static int OldBuyMoney(Item item, Func<Item, int> getBuyMoney)
    {
        if (item.Index == ItemBless) return 100_000;
        if (item.Index == ItemSoul) return 70_000;
        if (item.Index == ItemChaos) return 40_000;
        if (item.Index == ItemCreation) return 450_000;
        if (item.Index == ItemLife) return 450_000;
        return getBuyMoney(item);
    }

    /// <summary>Puerto de <c>CItem::IsExcItem()</c> (Item.cpp:76-90).</summary>
    private static bool IsExcItem(Item item) => item.Index != ItemDinorant && item.NewOption != 0;

    /// <summary>Aproximación de <c>CItem::IsSetItem()</c>, que delega en
    /// <c>CSetItemOption::IsSetItem</c> (SetItemOption.txt, no portado). Un <see cref="Item.SetOption"/>
    /// distinto de 0 es exactamente lo que ese campo representa en este wire (nibble bajo = set-item),
    /// así que alcanza sin necesitar la tabla completa.</summary>
    private static bool IsSetItem(Item item) => item.SetOption != 0;

    /// <summary>Después de cualquier intento ejecutado (éxito o fracaso) la Chaos Box queda vacía.
    /// El original sólo llama <c>ChaosBoxInit</c> explícitamente en la rama de FRACASO -- en la de
    /// éxito de <c>ChaosItemMix</c>/<c>FruitMix</c>/etc. los ingredientes consumidos no se limpian
    /// ahí mismo. No se pudo confirmar qué otro camino los limpia en ese caso (posiblemente un bug
    /// del original de 20 años, posiblemente algo que pasa en otro lado que no se alcanzó a leer);
    /// se optó por limpiar siempre, que es la opción segura -- lo contrario arriesgaría que el
    /// jugador recupere sus ingredientes gratis al cerrar la ventana.</summary>
    private static void ClearBox(PlayerObject player) => player.ClearChaosBox();

    private static ChaosMixResult MixChaosItem(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, bool execute)
    {
        int chaosCount = 0, itemCount = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Index == ItemBless || it.Index == ItemSoul)
            {
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Level >= 4 && it.Option3 >= 1)
            {
                itemCount++;
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount == 0 || itemCount == 0)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.ChaosItemMixRate[player.AccountLevel];
        int rate = Math.Min(configured == -1 ? itemMoney / 20_000 : configured, 100);
        int zen = rate * 10_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            int index = ChaosWeapons[Random.Shared.Next(ChaosWeapons.Length)];
            bool luck = Random.Shared.Next(100) < 50;
            byte level = (byte)Random.Shared.Next(0, 3);
            var newItem = new Item { Index = (short)index, Level = level, Durability = 255, Option1 = (byte)(luck ? 1 : 0), Option3 = 1 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>type=0 -> +9 a +10 (CHAOS_MIX_PLUS_ITEM_LEVEL1); type=1 -> +10 a +11
    /// (CHAOS_MIX_PLUS_ITEM_LEVEL2). Puerto de <c>PlusItemLevelMix</c> (ChaosBox.cpp:276-377).</summary>
    private static ChaosMixResult MixPlusItemLevel(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        int[] addLuckSuccessRate2, Func<Item, int> getBuyMoney, int type, bool execute)
    {
        int chaosCount = 0, blessCount = 0, soulCount = 0, itemCount = 0;
        Item? targetItem = null;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos) chaosCount++;
            else if (it.Index == ItemBless) blessCount++;
            else if (it.Index == ItemSoul) soulCount++;
            else if (it.Level == 9 + type) { itemCount++; targetItem = it; }
        }

        if (chaosCount != 1 || soulCount < type + 1 || blessCount < type + 1 || itemCount != 1 || targetItem == null)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int rate = (IsExcItem(targetItem) || IsSetItem(targetItem))
            ? rates.PlusExcSetItemLevelMixRate[type, player.AccountLevel]
            : rates.PlusCommonItemLevelMixRate[type, player.AccountLevel];

        if (targetItem.Option2 != 0) // Skill/Luck option del item -- ver comentario de Item.Option1/2
        {
            rate += addLuckSuccessRate2[player.AccountLevel];
        }

        rate = Math.Min(rate, 100);
        int zen = 2_000_000 * (type + 1);

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            var upgraded = new Item
            {
                Index = targetItem.Index,
                Level = (byte)(targetItem.Level + 1),
                Durability = targetItem.Durability,
                Serial = targetItem.Serial,
                Option1 = targetItem.Option1,
                Option2 = targetItem.Option2,
                Option3 = targetItem.Option3,
                NewOption = targetItem.NewOption,
                SetOption = targetItem.SetOption,
            };
            ClearBox(player);
            return new ChaosMixResult(true, 1, upgraded, false, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    private static ChaosMixResult MixFruit(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates, bool execute)
    {
        int chaosCount = items.Count(i => i.Index == ItemChaos);
        int creationCount = items.Count(i => i.Index == ItemCreation);

        if (chaosCount != 1 || creationCount != 1)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int rate = Math.Min(rates.FruitMixRate[player.AccountLevel], 100);
        int zen = 3_000_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            byte level = (byte)Random.Shared.Next(0, 4); // 0-3, no 0-4: GetLargeRand()%4 en el original
            var newItem = new Item { Index = (short)Item.GetItem(13, 15), Level = level, Durability = 0 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>Puerto de <c>Wing1Mix</c> (ChaosBox.cpp:744-839) -- Chaos + un arma del caos
    /// excelente (nivel>=4, Option3>=1) -> ala de 1ra generación (Elf/Heaven/Satan). En el wire esto
    /// se dispara con <see cref="ChaosMixType.Wing2"/> (11), no Wing1 -- ver el comentario de
    /// <see cref="ChaosMixType"/>.</summary>
    private static ChaosMixResult MixWing1(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, bool execute)
    {
        int chaosCount = 0, chaosWeaponCount = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (it.Index == ItemBless || it.Index == ItemSoul)
            {
                itemMoney += OldBuyMoney(it, getBuyMoney);
            }
            else if (Array.IndexOf(ChaosWeapons, (int)it.Index) >= 0 && it.Level >= 4 && it.Option3 >= 1)
            {
                chaosWeaponCount++;
                itemMoney += getBuyMoney(it);
            }
            else if (it.Level >= 4 && it.Option3 >= 1)
            {
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount == 0 || chaosWeaponCount == 0)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.Wing1MixRate[player.AccountLevel];
        int rate = Math.Min(configured == -1 ? itemMoney / 20_000 : configured, 100);
        int zen = rate * 10_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            int index = Wing1Candidates[Random.Shared.Next(Wing1Candidates.Length)];
            var newItem = new Item { Index = (short)index, Level = 0, Durability = 255 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }

    /// <summary>Puerto de <c>Wing2Mix</c> (ChaosBox.cpp:515-636). <paramref name="wingTier"/>=0 ->
    /// Chaos + Loch's Feather (nivel 0) + ala de 1ra -> ala de 2da (dispara con
    /// <see cref="ChaosMixType.Wing1"/>=7 en el wire); =1 -> Chaos + Crest of Monarch (mismo índice
    /// de item, nivel 1) + ala de 2da -> Cape of Lord (dispara con <see cref="ChaosMixType.Wing3"/>
    /// =24).</summary>
    private static ChaosMixResult MixWing2Family(PlayerObject player, List<Item> items, GameServerInfoChaosMix rates,
        Func<Item, int> getBuyMoney, int wingTier, bool execute)
    {
        int chaosCount = 0, featherCount = 0, wingItemCount = 0, wingItemMoney = 0, itemMoney = 0;

        foreach (var it in items)
        {
            if (it.Index == ItemChaos)
            {
                chaosCount++;
            }
            else if (it.Index == ItemFeatherLevel0 && it.Level == wingTier)
            {
                featherCount++;
            }
            // El ingrediente "ala" que pide esta mezcla es SIEMPRE de 1ra generación
            // (12,0-12,2), para los dos niveles -- el original no lo condiciona por `type` acá
            // (Wing2Mix, el bloque de conteo de WingItemCount usa ese rango fijo). Lo que cambia
            // con `wingTier` es la Feather/Crest de arriba y el resultado: 2da generación de
            // alas para tier=0, Cape of Lord para tier=1.
            else if (Array.IndexOf(Wing1Candidates, (int)it.Index) >= 0)
            {
                wingItemCount++;
                wingItemMoney += getBuyMoney(it);
            }
            else if (IsExcItem(it) && it.Level >= 4)
            {
                itemMoney += getBuyMoney(it);
            }
        }

        if (chaosCount != 1 || featherCount != 1 || wingItemCount != 1)
        {
            return new ChaosMixResult(false, 7, null, false, 0, 0);
        }

        int configured = rates.Wing2MixRate[player.AccountLevel];
        int rate;
        int cap;
        if (configured == -1)
        {
            rate = (wingItemMoney / 4_000_000) + (itemMoney / 40_000);
            cap = 90;
        }
        else
        {
            rate = configured;
            cap = 100;
        }
        rate = Math.Min(rate, cap);
        int zen = 5_000_000;

        if (!execute)
        {
            return new ChaosMixResult(false, 0, null, false, rate, zen);
        }

        if (player.Money < zen)
        {
            return new ChaosMixResult(false, 2, null, false, rate, zen);
        }

        player.Money -= (uint)zen;

        if (Random.Shared.Next(100) < rate)
        {
            var candidates = wingTier == 0 ? Wing2Candidates : CapeCandidates;
            int index = candidates[Random.Shared.Next(candidates.Length)];
            var newItem = new Item { Index = (short)index, Level = 0, Durability = 255 };
            ClearBox(player);
            return new ChaosMixResult(true, 1, newItem, true, rate, zen);
        }

        ClearBox(player);
        return new ChaosMixResult(false, 0, null, false, rate, zen);
    }
}

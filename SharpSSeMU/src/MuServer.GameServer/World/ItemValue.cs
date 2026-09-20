using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary> A row of Data/Item/ItemValue.txt: an item's explicit price, which overrides the one that would
/// come from the general formula of <c>CItem::Value()</c>. <para>The file came with the server from the start
/// and nobody read it. Without it, the special-price objects --jewels, event tickets, siege potions-- were
/// priced with the general formula, which gives anything for them: the Jewel of Bless came out at 18,700
/// instead of 9,000,000, and the Jewel of Chaos at 40,082,300 instead of 810,000. Those are the file's 72 rows,
/// none matched. The client, meanwhile, showed the correct values (it has them written by hand in
/// <c>ItemValue</c>, ZzzInfomation.cpp), so in the shop one number was seen and another was charged.</para>
/// <para><see cref="Level"/> and <see cref="Grade"/> are -1 when the file carries "*", which means "any". The
/// level is the instance's +0..+15. The grade is the special-options mask: today only the Horn of Dinorant uses
/// it, whose three options add 300,000 each (960,000 base, 1,260,000 with one, 1,560,000 with two), which is
/// exactly what its seven rows declare.</para> </summary>
public sealed class ItemValueRow
{
    public required int Index { get; init; } // Item.GetItem(Section, Sub)
    public required int Level { get; init; } // -1 = cualquiera
    public required int Grade { get; init; } // -1 = cualquiera
    public required int Money { get; init; }
}

/// <summary> Port of the explicit price table of Data/Item/ItemValue.txt. <para><b>What this port does not
/// do:</b> the original scales some of these prices by the stacked quantity or by the remaining durability
/// --the Symbol of Kundun is worth 30,000 per unit, the Siege Potion 900,000 per unit, and arrows and bolts are
/// worth their price times the fraction of durability they have left-- and each item does it its own way. Here
/// the table's value is returned as is, unscaled, because the file does not say which ones scale and guessing
/// would be inventing. A stack is priced as one unit: low, but of the right order, which is an enormous
/// improvement over the 300x to 6000x factors of the general formula.</para> </summary>
public sealed class ItemValueTable
{
    private readonly Dictionary<int, List<ItemValueRow>> _byIndex = new();

    public int Count { get; private set; }

    /// <summary>The price declared for this item, or null if the file does not mention it (in which case the
    /// caller must fall back to the general formula, as before). <para>Among several rows of the same item the
    /// most specific wins: one that fixes level and grade beats one that only fixes the level, and that one
    /// beats one with both at "*". Without that order, the Horn of Dinorant without options would take the
    /// price of any of its seven rows depending on the file order.</para></summary>
    public int? Get(int index, int level, int grade)
    {
        if (!_byIndex.TryGetValue(index, out var filas))
        {
            return null;
        }

        ItemValueRow? mejor = null;
        int mejorPuntaje = -1;

        foreach (var fila in filas)
        {
            if (fila.Level >= 0 && fila.Level != level)
            {
                continue;
            }

            if (fila.Grade >= 0 && fila.Grade != grade)
            {
                continue;
            }

            int puntaje = (fila.Level >= 0 ? 2 : 0) + (fila.Grade >= 0 ? 1 : 0);

            if (puntaje > mejorPuntaje)
            {
                mejor = fila;
                mejorPuntaje = puntaje;
            }
        }

        return mejor?.Money;
    }

    public int Load(string path)
    {
        var script = new MemScript();

        if (!script.SetBuffer(path))
        {
            Log.Add(LogColor.Red, "[ItemValueTable] {0}", script.GetLastError());
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

            // The index comes as "04,007": the comma is a token of its own and is discarded, the same as in the
            // shop files' parser (ShopManagerTable.LoadShopItems).
            int section = script.GetNumber();
            script.GetToken();
            int sub = script.GetAsNumber();

            int level = script.GetAsNumber(); // "*" -> -1
            int grade = script.GetAsNumber(); // "*" -> -1
            int money = script.GetAsNumber();

            if (section < 0 || sub < 0 || money < 0)
            {
                continue;
            }

            int index = Item.GetItem(section, sub);

            if (!_byIndex.TryGetValue(index, out var filas))
            {
                filas = new List<ItemValueRow>();
                _byIndex[index] = filas;
            }

            filas.Add(new ItemValueRow
            {
                Index = index,
                Level = level,
                Grade = grade,
                Money = money,
            });

            Count++;
        }

        Log.Add(LogColor.Green, "[ItemValueTable] {0} explicit prices loaded", Count);
        return Count;
    }
}

using MuServer.Shared.Logging;
using MuServer.Shared.Scripting;

namespace MuServer.GameServer.World;

/// <summary>
/// Una fila de Data/Item/ItemValue.txt: el precio explícito de un item, que pisa el que saldría de
/// la fórmula general de <c>CItem::Value()</c>.
///
/// <para>El archivo venía con el servidor desde el principio y no lo leía nadie. Sin él, los objetos
/// de precio especial --joyas, entradas de evento, pociones de asedio-- se cotizaban con la fórmula
/// general, que para ellos da cualquier cosa: el Jewel of Bless salía 18.700 en vez de 9.000.000, y
/// el Jewel of Chaos 40.082.300 en vez de 810.000. Son las 72 filas del archivo, ninguna coincidía.
/// El cliente, mientras tanto, mostraba los valores correctos (los tiene escritos a mano en
/// <c>ItemValue</c>, ZzzInfomation.cpp), así que en la tienda se veía un número y se cobraba otro.</para>
///
/// <para><see cref="Level"/> y <see cref="Grade"/> valen -1 cuando el archivo trae "*", que
/// significa "cualquiera". El nivel es el +0..+15 de la instancia. El grado es la máscara de
/// opciones especiales: hoy lo usa únicamente el Horn of Dinorant, cuyas tres opciones suman
/// 300.000 cada una (960.000 base, 1.260.000 con una, 1.560.000 con dos), que es exactamente lo que
/// declaran sus siete filas.</para>
/// </summary>
public sealed class ItemValueRow
{
    public required int Index { get; init; } // Item.GetItem(Section, Sub)
    public required int Level { get; init; } // -1 = cualquiera
    public required int Grade { get; init; } // -1 = cualquiera
    public required int Money { get; init; }
}

/// <summary>
/// Puerto de la tabla de precios explícitos de Data/Item/ItemValue.txt.
///
/// <para><b>Lo que este puerto no hace:</b> el original escala algunos de estos precios por la
/// cantidad apilada o por la durabilidad restante --el Symbol of Kundun vale 30.000 por unidad, la
/// Siege Potion 900.000 por unidad, y las flechas y pernos valen su precio por la fracción de
/// durabilidad que les queda-- y cada item lo hace a su manera. Acá se devuelve el valor de la
/// tabla tal cual, sin escalar, porque el archivo no dice cuáles escalan y adivinarlo sería
/// inventar. Un stack se cotiza como una unidad: bajo, pero del orden correcto, que es una mejora
/// enorme frente a los factores de 300x a 6000x de la fórmula general.</para>
/// </summary>
public sealed class ItemValueTable
{
    private readonly Dictionary<int, List<ItemValueRow>> _byIndex = new();

    public int Count { get; private set; }

    /// <summary>El precio declarado para este item, o null si el archivo no lo menciona (en cuyo
    /// caso el que llama debe caer a la fórmula general, como antes).
    ///
    /// <para>Entre varias filas del mismo item gana la más específica: una que fija nivel y grado
    /// le gana a una que fija sólo el nivel, y ésa a una con los dos en "*". Sin ese orden, el
    /// Horn of Dinorant sin opciones tomaría el precio de cualquiera de sus siete filas según el
    /// orden del archivo.</para></summary>
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

            // El índice viene como "04,007": la coma es un token propio y se descarta, igual que en
            // el parser de los archivos de tienda (ShopManagerTable.LoadShopItems).
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

        Log.Add(LogColor.Green, "[ItemValueTable] {0} precios explicitos cargados", Count);
        return Count;
    }
}

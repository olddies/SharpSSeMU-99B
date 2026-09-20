using MuServer.GameServer.World;

namespace MuServer.AdminPanel.Repositories;

/// <summary>Un slot editable de inventario o baúl para el panel -- los mismos campos que
/// <see cref="World.Item"/> (el modelo real que usa el GameServer), pero aplanados en vez de
/// envueltos: MudBlazor edita celdas de <c>MudDataGrid</c> por expresión de propiedad, y una
/// propiedad anidada (<c>x => x.Data.Index</c>) es una fuente de bugs de binding innecesaria acá --
/// más simple convertir a/desde <see cref="World.Item"/> sólo en los bordes (decodificar el blob de
/// DB, volver a codificarlo al guardar), ver <c>Characters.razor</c>.</summary>
public sealed class ItemSlotRow
{
    public required int Slot { get; init; }
    public required string Label { get; init; }

    /// <summary>Índice de 0.99B (sección*32+sub, wire/DB), -1 = slot vacío.</summary>
    public int Index { get; set; } = -1;
    public int Level { get; set; }
    public int Durability { get; set; }
    public int Luck { get; set; } // Item.Option1, 0/1
    public int Skill { get; set; } // Item.Option2, 0/1 (sólo armas)
    public int OptionLevel { get; set; } // Item.Option3: nivel de opción (+4/+8/+12/+16)
    public int Excellent { get; set; } // Item.NewOption: bits de excelente / nivel extra
    public int SetItem { get; set; } // Item.SetOption: set-item, nibble bajo

    /// <summary>Serial único del item (Item.Serial) -- no se muestra en la grilla (es un ID interno,
    /// no algo que un admin necesite tocar a mano). Se conserva SÓLO si <see cref="Index"/> sigue
    /// siendo el mismo item que había al cargar (<see cref="OriginalIndex"/>): es la clave que usa
    /// <c>pet_item_info</c> para mascotas, así que tocar sólo el nivel/durabilidad de una mascota
    /// existente no debe perder su fila en esa tabla -- pero si el admin puso un item DISTINTO en el
    /// slot, ese serial viejo no le pertenece (arrastrarlo igual haría que dos items apunten a la
    /// misma fila de mascota).</summary>
    public uint Serial { get; set; }

    /// <summary>Índice que tenía el slot al cargar, para decidir en <see cref="ToItem"/> si el
    /// serial sigue siendo válido. -1 = estaba vacío.</summary>
    public int OriginalIndex { get; private init; } = -1;

    public static ItemSlotRow FromItem(int slot, string label, Item item)
    {
        var index = item.IsItem() ? item.Index : -1;
        return new ItemSlotRow
        {
            Slot = slot,
            Label = label,
            Index = index,
            OriginalIndex = index,
            Level = item.Level,
            Durability = item.Durability,
            Luck = item.Option1,
            Skill = item.Option2,
            OptionLevel = item.Option3,
            Excellent = item.NewOption,
            SetItem = item.SetOption,
            Serial = item.Serial,
        };
    }

    public Item ToItem() => Index < 0
        ? Item.Empty()
        : new Item
        {
            Index = (short)Index,
            Level = (byte)Math.Clamp(Level, 0, 15),
            Durability = (byte)Math.Clamp(Durability, 0, 255),
            Option1 = (byte)Math.Clamp(Luck, 0, 1),
            Option2 = (byte)Math.Clamp(Skill, 0, 1),
            Option3 = (byte)Math.Clamp(OptionLevel, 0, 255),
            NewOption = (byte)Math.Clamp(Excellent, 0, 255),
            SetOption = (byte)Math.Clamp(SetItem, 0, 255),
            Serial = Index == OriginalIndex ? Serial : 0,
        };
}

using MuServer.GameServer.World;

namespace MuServer.AdminPanel.Repositories;

/// <summary>An editable inventory or warehouse slot for the panel -- the same fields as <see
/// cref="World.Item"/> (the real model the GameServer uses), but flattened instead of wrapped: MudBlazor edits
/// <c>MudDataGrid</c> cells by property expression, and a nested property (<c>x => x.Data.Index</c>) is an
/// unnecessary source of binding bugs here -- simpler to convert to/from <see cref="World.Item"/> only at the
/// edges (decoding the DB blob, encoding it again on save), see <c>Characters.razor</c>.</summary>
public sealed class ItemSlotRow
{
    public required int Slot { get; init; }
    public required string Label { get; init; }

    /// <summary>0.99B index (section*32+sub, wire/DB), -1 = empty slot.</summary>
    public int Index { get; set; } = -1;
    public int Level { get; set; }
    public int Durability { get; set; }
    public int Luck { get; set; } // Item.Option1, 0/1
    public int Skill { get; set; } // Item.Option2, 0/1 (weapons only)
    public int OptionLevel { get; set; } // Item.Option3: option level (+4/+8/+12/+16)
    public int Excellent { get; set; } // Item.NewOption: bits de excelente / nivel extra
    public int SetItem { get; set; } // Item.SetOption: set-item, nibble bajo

    /// <summary>Unique item serial (Item.Serial) -- not shown in the grid (it is an internal ID, not something
    /// an admin needs to touch by hand). It is kept ONLY if <see cref="Index"/> is still the same item that was
    /// there on load (<see cref="OriginalIndex"/>): it is the key <c>pet_item_info</c> uses for pets, so
    /// touching only the level/durability of an existing pet must not lose its row in that table -- but if the
    /// admin put a DIFFERENT item in the slot, that old serial does not belong to it (dragging it along would
    /// make two items point to the same pet row).</summary>
    public uint Serial { get; set; }

    /// <summary>Index the slot had on load, to decide in <see cref="ToItem"/> whether the serial is still
    /// valid. -1 = it was empty.</summary>
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

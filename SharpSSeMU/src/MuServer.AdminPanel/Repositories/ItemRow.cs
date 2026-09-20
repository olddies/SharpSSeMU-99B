namespace MuServer.AdminPanel.Repositories;

/// <summary>A balance row of Data/Item/Item.txt, mutable -- unlike <c>MuServer.GameServer.World.ItemBalance</c>
/// (which uses <c>init</c> everywhere because it is the read-only model the GameServer already loads), this is
/// the panel's editable version. It gathers all possible fields of any of the 16 sections into a single class
/// -- which fields matter depends on <see cref="Section"/>, see the header comment of <see
/// cref="ItemFileRepository.Load"/> for the exact column layout of each one.</summary>
public sealed class ItemRow
{
    public required int Section { get; set; }
    public required int Sub { get; set; }
    public int Slot { get; set; } // -1 = "*" = cualquier slot
    public int Skill { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HaveSerial { get; set; }
    public bool HaveOption { get; set; }
    public bool DropItem { get; set; }
    public required string Name { get; set; }

    public int Level { get; set; }
    public int DamageMin { get; set; }
    public int DamageMax { get; set; }
    public int AttackSpeed { get; set; }
    public int Durability { get; set; }
    public int MagicDurability { get; set; }
    public int MagicDamageRate { get; set; }
    public int Defense { get; set; }
    public int DefenseSuccessRate { get; set; }
    public int MagicDefense { get; set; }
    public int WalkSpeed { get; set; }
    public int Value { get; set; }

    public int RequireLevel { get; set; }
    public int RequireStrength { get; set; }
    public int RequireDexterity { get; set; }
    public int RequireEnergy { get; set; }
    public int RequireVitality { get; set; }
    public int RequireLeadership { get; set; }
    public int BuyMoney { get; set; }
    public int SetAttr { get; set; }
    public int[] Resistance { get; set; } = new int[7];
    public int[] RequireClass { get; set; } = new int[5]; // DW, DK, FE, MG, DL

    public int Index => Sub + Section * 512; // Item.GetItem(Section, Sub) -- ver World/Item.cs
}

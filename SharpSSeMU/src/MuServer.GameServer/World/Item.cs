namespace MuServer.GameServer.World;

/// <summary>
/// Puerto reducido de CItem (Item.h/.cpp) — solo los campos que viajan por la red y se persisten
/// en DataServer (no el struct completo del original, que además carga ~30 campos de balance desde
/// Item.txt en runtime; eso queda para cuando la Fase 3 necesite calcular daño/defensa de verdad).
///
/// Constantes de identificación (ItemManager.h:11-16, árbol fuente correcto
/// "Emulator 0.99 (2.1.7)/GameServer" -- confirmado por stdafx.h:7,
/// GAMESERVER_VERSION "[ 2.1.7 ] %s (0.99B CHS) [%s]", que coincide con el nombre de este proyecto):
/// un item se identifica con un único WORD "Index" empaquetado como sección*32 + subíndice
/// (sección = categoría: espada/hacha/.../casco/armadura/etc., 0-15; subíndice = item dentro de la
/// categoría, 0-31) -- GET_ITEM(sección,sub) = sección*MAX_ITEM_TYPE+sub con MAX_ITEM_TYPE=32,
/// MAX_ITEM=MAX_ITEM_SECTION*MAX_ITEM_TYPE=512 (16*32), MAX_ITEM_INFO=5.
///
/// NOTA HISTÓRICA: una pasada anterior de este puerto migró estas constantes a MaxItemType=512 /
/// ItemInfo de 12 bytes basándose en `Source/Source/Emulator/GameServer/` (sin sufijo de versión),
/// que es un árbol de una temporada MUY posterior (tiene GAMESERVER_UPDATE, sockets, JewelOfHarmony,
/// MAX_ITEM_TYPE=512, MAX_ITEM_INFO=12) y NO es el código real de este servidor. Ese árbol no tiene
/// absolutamente ninguna relación con "0.99B CHS SSeMU_2.1.7" -- fue un error de investigación. Esta
/// clase fue revertida al formato real de 32-stride/5-bytes, verificado línea por línea contra
/// ItemManager.h/.cpp y Viewport.cpp del árbol correcto. No existen sockets, JewelOfHarmony,
/// pentagrama, Muun ni items periódicos en este build -- esos campos y su lógica fueron eliminados.
/// </summary>
public sealed class Item
{
    public const int MaxItemSection = 16;
    public const int MaxItemType = 32;
    public const int MaxItem = MaxItemSection * MaxItemType; // 512

    public const int WireByteSize = 5; // MAX_ITEM_INFO
    public const int DbByteSize = 16; // slot completo en DataServer (solo bytes 0-8 con datos)

    public const int InventoryWearSize = 12; // slots 0-11: equipo puesto
    public const int InventoryMainSize = 76;
    public const int InventorySize = InventoryWearSize + InventoryMainSize + 20; // 108 (INVENTORY_FULL_SIZE)

    public static int GetItem(int section, int sub) => (section * MaxItemType) + sub;

    // eInventorySlot (ItemManager.h:29-43)
    public const int SlotWeapon1 = 0;
    public const int SlotWeapon2 = 1;
    public const int SlotHelm = 2;
    public const int SlotArmor = 3;
    public const int SlotPants = 4;
    public const int SlotGloves = 5;
    public const int SlotBoots = 6;
    public const int SlotWing = 7;
    public const int SlotHelper = 8;
    public const int SlotAmulet = 9;
    public const int SlotRing1 = 10;
    public const int SlotRing2 = 11;

    public short Index { get; set; } = -1; // -1 = slot vacío (equivalente a IsItem()==false)
    public byte Level { get; set; } // 0-15
    public byte Durability { get; set; }
    public uint Serial { get; set; }
    public byte Option1 { get; set; } // Luck (0/1)
    public byte Option2 { get; set; } // Skill (0/1, solo armas)
    public byte Option3 { get; set; } // Excelente: bits 0-1 en byte1, bit ">3" en byte3 (ver ToWireBytes)
    public byte NewOption { get; set; } // +nivel adicional / flags de excelente (bits 0-5)
    public byte SetOption { get; set; } // set-item, nibble bajo

    public bool IsItem() => Index >= 0 && Index < MaxItem;

    public static Item Empty() => new();

    /// <summary>
    /// Puerto de CItemManager::ItemByteConvert (ItemManager.cpp:1593-1612) -- formato de 5 bytes
    /// (MAX_ITEM_INFO=5) que viaja en todo paquete cliente-servidor (inventario, ítem en el suelo,
    /// get/move/buy, tienda, etc.). El C++ real escribe además un byte fantasma lpMsg[5]=0 un byte
    /// más allá del array declarado de 5 (pisando el siguiente campo del struct, que en nuestros
    /// packet builders ya se escribe explícitamente aparte) -- por eso acá solo se emiten los 5
    /// bytes reales.
    /// </summary>
    public void ToWireBytes(Span<byte> dst)
    {
        dst.Clear();

        if (!IsItem())
        {
            return; // 5 bytes en 0 = "sin item"
        }

        dst[0] = (byte)(Index & 0xFF);

        dst[1] = 0;
        dst[1] |= (byte)(Level * 8);
        dst[1] |= (byte)(Option1 * 128);
        dst[1] |= (byte)(Option2 * 4);
        dst[1] |= (byte)(Option3 & 3);

        dst[2] = Durability;

        dst[3] = 0;
        dst[3] |= (byte)((Index & 256) >> 1);
        dst[3] |= (byte)(Option3 > 3 ? 64 : 0);
        dst[3] |= NewOption;

        dst[4] = SetOption;
    }

    /// <summary>Puerto de CItemManager::DBItemByteConvert (ItemManager.cpp:1615-1650) -- formato de
    /// 16 bytes/slot que usa DataServer para persistencia (Inventory[INVENTORY_SIZE][16] en
    /// DSProtocol.h), de los cuales solo los bytes 0-8 llevan datos reales; byte9 siempre es 0 (con
    /// MAX_ITEM_TYPE=32 el índice completo (0-511) ya entra en 9 bits -- byte0 completo + 1 bit más
    /// en byte7 -- así que no hacen falta más bits de índice) y bytes10-15 nunca se escriben (no hay
    /// sockets/JewelOfHarmony/etc. en este build). Slot vacío = 16 bytes en 0xFF
    /// (memset(lpMsg,0xFF,16) en el original). También replica el caso especial
    /// `m_Index==GET_ITEM(13,19)` (ItemManager.cpp:1622) que fuerza el slot vacío en DB incluso si el
    /// item existe en memoria.</summary>
    public void ToDbBytes(Span<byte> dst)
    {
        if (!IsItem() || Index == GetItem(13, 19))
        {
            dst.Slice(0, 16).Fill(0xFF);
            return;
        }

        dst.Slice(0, 16).Clear();

        dst[0] = (byte)(Index & 0xFF);
        dst[1] = (byte)((Level * 8) | (Option1 * 128) | (Option2 * 4) | (Option3 & 3));
        dst[2] = Durability;

        dst[3] = (byte)((Serial >> 24) & 0xFF);
        dst[4] = (byte)((Serial >> 16) & 0xFF);
        dst[5] = (byte)((Serial >> 8) & 0xFF);
        dst[6] = (byte)(Serial & 0xFF);

        dst[7] = (byte)(((Index & 256) >> 1) | (Option3 > 3 ? 64 : 0) | NewOption);
        dst[8] = (byte)(SetOption & 15);

        dst[9] = 0;
        // bytes 10-15 quedan en 0 (Clear() arriba) -- no hay campos reales que escribir ahí.
    }

    /// <summary>Puerto de CItemManager::ConvertItemByte (ItemManager.cpp:1653-1688). Slot vacío se
    /// detecta igual que el original: byte0==0xFF && (byte7&0x80)==0x80 && (byte9&0xF0)==0xF0. El
    /// índice se reconstruye SOLO desde byte0 + el bit alto en byte7 (Index = b0 | ((b7&0x80)&lt;&lt;1));
    /// el término `(byte9&0xF0)*32` que aparece en el C++ original siempre vale 0 en este build
    /// (DBItemByteConvert nunca escribe nada distinto de 0 en byte9), así que se omite directamente
    /// en vez de leerlo.</summary>
    public static Item FromDbBytes(ReadOnlySpan<byte> src)
    {
        byte b0 = src[0], b1 = src[1], b2 = src[2], b7 = src[7], b8 = src[8], b9 = src[9];

        if (b0 == 0xFF && (b7 & 0x80) == 0x80 && (b9 & 0xF0) == 0xF0)
        {
            return Empty();
        }

        int index = b0 | ((b7 & 0x80) << 1);
        uint serial = ((uint)src[3] << 24) | ((uint)src[4] << 16) | ((uint)src[5] << 8) | src[6];

        var item = new Item
        {
            Index = (short)index,
            Level = (byte)((b1 / 8) & 0x0F),
            Option1 = (byte)((b1 / 128) & 1),
            Option2 = (byte)((b1 / 4) & 1),
            Option3 = (byte)((b1 & 3) | (((b7 & 64) != 0) ? 4 : 0)),
            Durability = b2,
            Serial = serial,
            NewOption = (byte)(b7 & 0x3F),
            SetOption = (byte)(b8 & 15),
        };

        return item;
    }

    /// <summary>
    /// Reconstruye un item a partir del formato compacto de 5 bytes {b0,b1,b7,b8,b9} que usa
    /// SDHP_CHARACTER_LIST.Inventory (DataServerProtocol.cpp::GDCharacterListRecv, y el puerto C#
    /// en DataServerProtocolHandler.CompactInventory) para la vista previa de selección de
    /// personaje -- son exactamente los mismos 5 bytes que <see cref="FromDbBytes"/> lee del
    /// formato de 16 bytes completo (los demás bytes no afectan el índice/nivel/brillo, así que se
    /// arma un buffer de 16 bytes "sintético" con el resto en 0 y se reusa la misma lectura.</summary>
    public static Item FromCompactPreviewBytes(byte b0, byte b1, byte b7, byte b8, byte b9)
    {
        Span<byte> synthetic = stackalloc byte[16];
        synthetic.Clear();
        synthetic[0] = b0;
        synthetic[1] = b1;
        synthetic[7] = b7;
        synthetic[8] = b8;
        synthetic[9] = b9;
        return FromDbBytes(synthetic);
    }
}

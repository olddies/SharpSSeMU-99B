using MuServer.GameServer.Config;
using MuServer.GameServer.Net;

namespace MuServer.GameServer.World;

/// <summary>
/// Puerto parcial de OBJECTSTRUCT (User.h) para un jugador -- solo los campos que hacen falta para
/// "entrar al mundo" (Fase 2): identidad/stats para el paquete de info de personaje, posición para
/// mapa/movimiento/viewport, y los bytes crudos de inventario/skill/quest/efecto que DataServer ya
/// manda completos (se guardan como blobs opacos hasta que la Fase 3 los interprete de verdad).
///
/// A diferencia de los paquetes de red (que sí se portan byte a byte), esto es una clase C# idiomática
/// -- el gObj[10000] global del original se reemplaza acá por PlayerRegistry + esta clase por jugador.
/// </summary>
public sealed class PlayerObject
{
    public required int Index { get; init; }
    public required ClientSession Session { get; init; }

    public string Account { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GuildName { get; set; } = string.Empty;
    public byte GuildStatus { get; set; } = 0; // 0x80 = Master, 0x00 = Member
    public byte Class { get; set; }
    public ushort Level { get; set; }
    public uint LevelUpPoint { get; set; }
    public uint Experience { get; set; }
    public uint Money { get; set; }

    /// <summary>Nivel de cuenta (0-3, AL0..AL3 en los .dat de GameServerInfo) -- en el original
    /// distingue cuentas normales/premium para varias tasas (ChaosMixRate, drop rate, MaxStatPoint,
    /// etc.). Puerto de <c>gObj[index].AccountLevel = lpMsg->AccountLevel</c> (JSProtocol.cpp:85):
    /// JoinServer ya calcula este valor de verdad (WZ_GetAccountLevel, con expiración) y se lo manda a
    /// GameServer al conectar la cuenta -- ver <see cref="ClientSession.AccountLevel"/>, guardado ahí
    /// porque llega antes de que exista este objeto. Se copia una sola vez al entrar al mundo, igual
    /// que el resto de los campos de identidad (no cambia mientras la sesión sigue conectada).</summary>
    public int AccountLevel { get; set; }
    public uint Strength { get; set; }
    public uint Dexterity { get; set; }
    public uint Vitality { get; set; }
    public uint Energy { get; set; }
    public uint Leadership { get; set; }
    public uint Life { get; set; }
    public uint MaxLife { get; set; }
    public uint Mana { get; set; }
    public uint MaxMana { get; set; }
    public uint BP { get; set; }
    public uint MaxBP { get; set; }
    public byte PKLevel { get; set; }
    public uint PKCount { get; set; }
    public uint PKTime { get; set; }
    public byte CtlCode { get; set; }
    public ushort FruitAddPoint { get; set; }
    public ushort FruitSubPoint { get; set; }
    public uint Reset { get; set; }
    public uint MasterReset { get; set; }

    /// <summary>Puerto de lpObj->ChatLimitTime -- no se consume todavía (sin sistema de mute), pero
    /// se guarda tal cual se recibió de DataServer para poder devolverlo sin cambios en
    /// <see cref="ClientProtocolHandler.SaveCharacterAsync"/> (ver doc-comment ahí: sin este campo,
    /// cada guardado pisaría el valor real de la fila con 0).</summary>
    public uint ChatLimitTime { get; set; }

    /// <summary>Contadores de entradas a eventos (Blood/Chaos/Devil Square) tal como los manda
    /// DataServer al entrar al mundo -- ninguno de los tres sistemas está portado todavía (Devil
    /// Square tiene su propio límite de tickets en <see cref="World.DevilSquareManager"/>, separado
    /// de este contador histórico), así que se guardan sin tocar por el mismo motivo que
    /// <see cref="ChatLimitTime"/>.</summary>
    public ushort BCCount { get; set; }
    public ushort CCCount { get; set; }
    public ushort DSCount { get; set; }

    /// <summary>Puerto de lpObj->CharSaveTime (ObjectManager.cpp:1034-1038): throttle de 60s para el
    /// guardado disparado por ganar experiencia/subir de nivel. <see cref="DateTime.MinValue"/> =
    /// "nunca guardado" (equivalente al campo en 0 de un OBJECTSTRUCT recién zereado), que hace que
    /// el primer chequeo tras entrar al mundo ya pueda disparar un guardado.</summary>
    public DateTime CharSaveTime { get; set; } = DateTime.MinValue;

    /// <summary>Puerto de lpObj->AutoSaveTime (User.cpp:2537-2541): throttle de 10 minutos para el
    /// autoguardado periódico incondicional (corre para cualquier jugador conectado, no solo tras
    /// combate) -- éste es el mecanismo real que garantiza que la posición/progreso se persista aun
    /// en una sesión sin matar monstruos. Mismo sentinel que <see cref="CharSaveTime"/>.</summary>
    public DateTime AutoSaveTime { get; set; } = DateTime.MinValue;

    // Blobs opacos tal como los manda DataServer -- Skill/Quest/Effect se interpretan recién en
    // fases posteriores (combate = skills, quest/eventos = fases sociales/especiales). Inventory
    // se mantiene como el blob crudo de 1728 bytes (fuente de verdad para el guardado en
    // DataServer) PERO además se decodifica a <see cref="Items"/> en cuanto llega (Fase 3) -- los
    // dos se mantienen sincronizados por <see cref="SetItem"/>, que escribe en ambos lados a la vez
    // en vez de tener que re-serializar los 108 slots enteros cada vez que cambia uno solo.
    public byte[] Inventory { get; set; } = new byte[1728];
    public byte[] Skill { get; set; } = new byte[180];
    public byte[] Quest { get; set; } = Enumerable.Repeat((byte)0xFF, 50).ToArray();
    public byte[] Effect { get; set; } = new byte[208];

    /// <summary>Puerto simplificado de lpObj->Interface (use/type/state, User.h) + TargetShopNumber --
    /// no-nulo mientras el jugador tiene abierta la ventana de compra de un NPC (entre CGNpcTalkRecv y
    /// CGNpcTalkCloseRecv/CGItemBuyRecv/CGItemSellRecv). Solo INTERFACE_SHOP está portado en esta
    /// pasada (Trade/Warehouse/PersonalShop quedan para más adelante, ver README).</summary>
    public int? TargetShopNumber { get; set; }

    /// <summary>Puerto de lpObj->Inventory[INVENTORY_SIZE] (CItem por slot) -- Fase 3. Se llena
    /// decodificando <see cref="Inventory"/> (ver <see cref="DecodeInventory"/>) apenas se recibe
    /// SDHP_CHARACTER_INFO_RECV.</summary>
    public Item[] Items { get; } = CreateEmptyItems();

    private static Item[] CreateEmptyItems()
    {
        var items = new Item[Item.InventorySize];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public bool InTrade { get; set; }
    public int TradeTargetIndex { get; set; } = -1;
    public bool TradeOk { get; set; }
    public uint TradeMoney { get; set; }
    public Item[] TradeItems { get; } = CreateEmptyTradeItems();

    private static Item[] CreateEmptyTradeItems()
    {
        var items = new Item[32];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearTrade()
    {
        InTrade = false;
        TradeTargetIndex = -1;
        TradeOk = false;
        TradeMoney = 0;
        for (int n = 0; n < TradeItems.Length; n++)
        {
            TradeItems[n] = Item.Empty();
        }
    }

    public bool InWarehouse { get; set; }
    public byte WarehouseLock { get; set; }
    public ushort WarehousePassword { get; set; }
    public uint WarehouseMoney { get; set; }
    public Item[] WarehouseItems { get; } = CreateEmptyWarehouseItems();

    private static Item[] CreateEmptyWarehouseItems()
    {
        var items = new Item[120];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearWarehouse()
    {
        InWarehouse = false;
        WarehouseLock = 0;
        WarehousePassword = 0;
        WarehouseMoney = 0;
        for (int n = 0; n < WarehouseItems.Length; n++)
        {
            WarehouseItems[n] = Item.Empty();
        }
    }

    public bool InChaosBox { get; set; }
    public Item[] ChaosBoxItems { get; } = CreateEmptyChaosBoxItems();
    public byte[] ChaosBoxMap { get; } = new byte[32];

    private static Item[] CreateEmptyChaosBoxItems()
    {
        var items = new Item[32];
        for (int n = 0; n < items.Length; n++) items[n] = Item.Empty();
        return items;
    }

    public void ClearChaosBox()
    {
        InChaosBox = false;
        for (int n = 0; n < ChaosBoxItems.Length; n++)
        {
            ChaosBoxItems[n] = Item.Empty();
        }
        Array.Fill(ChaosBoxMap, (byte)0xFF);
    }

    public bool HasSkill(short skillId)
    {
        for (int i = 0; i < 60; i++)
        {
            int s = Skill[i * 3] | ((Skill[i * 3 + 1] & 7) * 255);
            if (s == skillId && Skill[i * 3] != 0xFF) return true;
        }
        return false;
    }

    public int AddSkill(short skillId, byte level = 0)
    {
        if (HasSkill(skillId)) return -1;

        for (int i = 0; i < 60; i++)
        {
            int s = Skill[i * 3] | ((Skill[i * 3 + 1] & 7) * 255);
            if (s <= 0 || Skill[i * 3] == 0xFF)
            {
                Skill[i * 3] = (byte)(skillId & 0xFF);
                Skill[i * 3 + 1] = (byte)((level << 3) | ((skillId / 255) & 7));
                Skill[i * 3 + 2] = 0;
                return i;
            }
        }

        return -1;
    }

    /// <summary>Decodifica <see cref="Inventory"/> (blob crudo de DataServer, 16 bytes/slot) en
    /// <see cref="Items"/> -- puerto del loop de CharacterInfoSet que llama ConvertItemByte por
    /// cada slot (ObjectManager.cpp, justo antes de CharacterMakePreviewCharSet).</summary>
    public void DecodeInventory()
    {
        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            int offset = slot * 16;

            if (offset + 16 > Inventory.Length)
            {
                Items[slot] = Item.Empty();
                continue;
            }

            Items[slot] = Item.FromDbBytes(Inventory.AsSpan(offset, 16));
        }
    }

    /// <summary>Escribe un item en un slot, manteniendo <see cref="Items"/> y el blob crudo
    /// <see cref="Inventory"/> sincronizados (para que un guardado posterior a DataServer refleje
    /// el cambio sin tener que re-decodificar todo).</summary>
    public void SetItem(int slot, Item item)
    {
        Items[slot] = item;

        int offset = slot * 16;

        if (offset + 16 <= Inventory.Length)
        {
            item.ToDbBytes(Inventory.AsSpan(offset, 16));
        }
    }

    // Posición / mundo
    public byte Map { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte TX { get; set; }
    public byte TY { get; set; }
    public byte OldX { get; set; }
    public byte OldY { get; set; }
    public byte Dir { get; set; }

    public bool IsDying { get; set; }
    public DateTime? DiedAt { get; set; }

    public int PhysiSpeed { get; set; }
    public int MagicSpeed { get; set; }

    /// <summary>Puerto de lpObj->ActionNumber (User.h, seteado en CGActionRecv, Protocol.cpp:611-666)
    /// -- último código de acción recibido (120=ataque, 128=sentarse, 129=saludo/pose, 130=curación).
    /// El original lo usa también para reproducir la pose al armar el paquete de viewport de un
    /// jugador que recién entra en rango de otro (VpPlayer2[]/CharacterMakePreviewCharSet) -- esa
    /// reproducción NO está portada todavía (el paquete de "jugador apareció" de este puerto no lleva
    /// pose), así que por ahora este campo solo se guarda para uso futuro; el efecto visible inmediato
    /// (animación de sentarse/saludar) ya funciona vía el broadcast de ActionSend en tiempo real.</summary>
    public byte ActionNumber { get; set; }

    /// <summary>
    /// Puerto de CharSet[13] (ver CObjectManager::CharacterMakePreviewCharSet, ObjectManager.cpp:1139-
    /// 1269 del árbol fuente correcto, "Emulator 0.99 (2.1.7)/GameServer" -- ver el doc-comment de
    /// <see cref="World.Item"/> para la explicación de por qué este repo tiene dos árboles de C++ y
    /// cuál es el real). REVERTIDO de un tamaño fabricado de 18 bytes (con bits de extensión en
    /// índices 12-17 y ramas de alas/mascota de temporadas posteriores) que una pasada de porting
    /// anterior investigó contra el árbol de fuente EQUIVOCADO (`Source/Source/Emulator/GameServer`
    /// sin sufijo de versión, una temporada mucho más tardía) -- el CharSet real de este build es de
    /// 13 bytes, sin esos bits de extensión ni esas alas/mascotas (no existen en 0.99B).
    /// </summary>
    public byte[] CharSet { get; } = new byte[13];

    /// <summary>ChangeUp (2da/3ra evolución de clase) -- DataServer manda la clase en formato crudo
    /// de DB (<c>Class*16 + ChangeUp</c>, ej. 0/16/32/48/64 para 1ra clase DW/DK/FE/MG/DL); se
    /// descompone en <see cref="Class"/> (índice compacto 0-4) y este campo en el único punto de
    /// entrada real (ver ClientProtocolHandler.OnCharacterInfoFromDataServerAsync). Ningún camino de
    /// este puerto todavía permite un cambio de clase real (2do/3er change), así que en la práctica
    /// siempre vale 0 con los datos de semilla actuales, pero el campo ya está correctamente
    /// derivado si algún personaje real tuviera un valor distinto guardado.</summary>
    public byte ChangeUp { get; set; }

    /// <summary>Puerto completo (Fase 3) de CObjectManager::CharacterMakePreviewCharSet
    /// (ObjectManager.cpp:1139-1268) -- arma los 13 bytes de apariencia a partir del equipo real
    /// puesto en <see cref="Items"/> (slots 0-11, ver constantes Slot* de <see cref="Item"/>). Sin
    /// la parte de sentado/posado (no hay acciones todavía) ni el bit de "full-set" (depende de
    /// CharacterCalcAttribute, que es cálculo de atributos -- Fase 4).</summary>
    public void RebuildCharSet()
    {
        var built = BuildCharSet(Class, ChangeUp, Items);

        for (int n = 0; n < 13; n++)
        {
            CharSet[n] = built[n];
        }
    }

    /// <summary>
    /// Puerto EXACTO de CObjectManager::CharacterMakePreviewCharSet (ObjectManager.cpp:1139-1269 del
    /// árbol fuente correcto, "Emulator 0.99 (2.1.7)/GameServer" -- ver el doc-comment de
    /// <see cref="World.Item"/> para la explicación completa de por qué este repo tiene dos árboles de
    /// C++ y por qué una pasada de porting anterior investigó este método contra el árbol EQUIVOCADO
    /// (una temporada muy posterior con CharSet[18], alas/mascotas de esa temporada y bits de
    /// extensión que no existen en 0.99B). Factorizado como método estático para poder reusarlo tanto
    /// desde <see cref="RebuildCharSet"/> (jugador ya en el mundo, con <see cref="Items"/> reales)
    /// como desde la pantalla de selección de personaje (formato compacto que manda DataServer -- ver
    /// ClientProtocolHandler.OnCharacterListFromDataServerAsync, que reconstruye Item[9] equivalentes
    /// con Item.FromCompactPreviewBytes antes de llamar acá; el original hace lo mismo en
    /// DSProtocol.cpp, DGCharacterListRecv, con la MISMA lógica byte a byte). Sin la parte de
    /// sentado/posado (CharSet[0] bits 0-1 con ActionNumber==ACTION_SIT1/POSE1 -- no hay acciones con
    /// eco de viewport todavía) ni el bit de "set completo" (CharSet[11] bit0, depende de
    /// CharacterCalcAttribute -- Fase 4, sin el bono de "mismo set visual" documentado ahí).
    /// </summary>
    public static byte[] BuildCharSet(byte cls, byte changeUp, IReadOnlyList<Item> wear)
    {
        var charSet = new byte[13];

        byte b0 = (byte)(changeUp * 16);
        b0 -= (byte)(b0 / 32);
        b0 += (byte)(cls * 32);
        charSet[0] = b0;

        // TempInventory: armas (slots 0-1) guardan el índice completo (m_Index, 0-511), "sin arma" =
        // 0xFF; el resto de slots de equipo (2-11) guardan solo el subíndice dentro de su sección
        // (m_Index%MAX_ITEM_TYPE), "slot vacío" = MAX_ITEM_TYPE-1 = 0x1F -- puerto exacto de
        // ObjectManager.cpp:1159-1185. Item.MaxItemType = 32 (MAX_ITEM_TYPE real de este build,
        // ItemManager.h:12).
        const int noWeapon = 0xFF;
        int noItem = Item.MaxItemType - 1; // 0x1F

        Span<int> temp = stackalloc int[9];

        for (int n = 0; n < 9; n++)
        {
            var item = wear[n];

            if (n == Item.SlotWeapon1 || n == Item.SlotWeapon2)
            {
                temp[n] = item.IsItem() ? item.Index : noWeapon;
            }
            else
            {
                temp[n] = item.IsItem() ? (item.Index % Item.MaxItemType) : noItem;
            }
        }

        charSet[1] = (byte)(temp[Item.SlotWeapon1] % 256);
        charSet[2] = (byte)(temp[Item.SlotWeapon2] % 256);

        charSet[3] |= (byte)((temp[Item.SlotHelm] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotHelm] & 0x10) << 3);

        charSet[3] |= (byte)(temp[Item.SlotArmor] & 0x0F);
        charSet[9] |= (byte)((temp[Item.SlotArmor] & 0x10) << 2);

        charSet[4] |= (byte)((temp[Item.SlotPants] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotPants] & 0x10) << 1);

        charSet[4] |= (byte)(temp[Item.SlotGloves] & 0x0F);
        charSet[9] |= (byte)(temp[Item.SlotGloves] & 0x10);

        charSet[5] |= (byte)((temp[Item.SlotBoots] & 0x0F) << 4);
        charSet[9] |= (byte)((temp[Item.SlotBoots] & 0x10) >> 1);

        // Nivel empacado en 3 bits/slot (weapon1,weapon2,helm,armor,pants,gloves,boots) + flags de
        // brillo excelente/set -- puerto exacto de ObjectManager.cpp:1206-1219.
        int level = 0;
        int[] table = { 1, 0, 6, 5, 4, 3, 2 };

        for (int n = 0; n < 7; n++)
        {
            if (temp[n] == noItem || temp[n] == noWeapon)
            {
                continue;
            }

            var item = wear[n];
            level |= (((item.Level - 1) / 2) & 7) << (n * 3);
            charSet[10] |= (byte)((((item.NewOption & 0x3F) != 0) ? 2 : 0) << table[n]);
            charSet[11] |= (byte)((((item.SetOption & 0x03) != 0) ? 2 : 0) << table[n]);
        }

        // Bit de "set completo" (CharacterCalcAttribute) diferido a la Fase 4 (cálculo de
        // atributos) -- no se prende charSet[11] bit0 todavía.

        charSet[6] = (byte)(level >> 16);
        charSet[7] = (byte)(level >> 8);
        charSet[8] = (byte)level;

        // Alas (slot 7) -- puerto exacto de ObjectManager.cpp:1232-1249. Solo estos 3 casos existen en
        // este build (0.99B); sin alas o cualquier índice fuera de esta lista no prende ningún bit.
        int wing = temp[Item.SlotWing];

        if (wing is >= 0 and <= 2)
        {
            charSet[5] |= (byte)(wing << 2);
        }
        else if (wing is >= 3 and <= 6)
        {
            charSet[5] |= 12;
            charSet[9] |= (byte)(wing - 2);
        }
        else if (wing == 30)
        {
            charSet[5] |= 12;
            charSet[9] |= 5;
        }

        // Mascota/montura (slot 8, "helper") -- puerto exacto de ObjectManager.cpp:1251-1268. Solo
        // estos 5 casos existen en este build.
        int helper = temp[Item.SlotHelper];

        if (helper == noItem)
        {
            charSet[5] |= 3;
        }
        else if (helper is >= 0 and <= 2)
        {
            charSet[5] |= (byte)helper;
        }
        else if (helper == 3)
        {
            charSet[5] |= 3;
            charSet[10] |= 1;
        }
        else if (helper == 4)
        {
            charSet[5] |= 3;
            charSet[12] |= 1;
        }

        return charSet;
    }

    // Estado de conexión al mundo
    /// <summary>
    /// Puerto de <c>char RegenOk</c> (User.h:509). El original es un contador de 4 estados
    /// (0=normal/visible, 1=recién-teleportado, 2/3=transición) pero para este build alcanza con un
    /// booleano porque el único lugar que lo pone en 1 es <c>gObjMoveGate</c>/<c>gObjTeleport</c>/
    /// <c>gObjSummonAlly</c> (User.cpp:2114,2144,2168,2220,2259) -- y NINGUNO de esos caminos
    /// (portales de mapa, hechizo de teleport) está portado todavía en este build (el único emisor de
    /// <see cref="Protocol.WorldPackets.TeleportSend"/> es World/DevilSquareManager.cs, que tampoco
    /// bloquea). El valor inicial real es 0 (=false, NO bloqueado), seteado por
    /// <c>gObjCharZeroSet</c> (User.cpp:368) al aceptar la conexión, y NO se toca en ningún punto del
    /// flujo de login/selección de personaje/entrada al mundo (confirmado leyendo la función completa
    /// que arma el personaje desde DataServer, ObjectManager.cpp:2733-2736: solo toca Live/Type/
    /// State/Connected). Un default de "true" (bloqueado hasta que el cliente mande 0xF3:0x12) fue un
    /// bug de esta porta: <see cref="MuServer.GameServer.WorldTestClient"/> manda ese paquete
    /// incondicionalmente y por eso el regression test nunca lo detectó, pero un cliente real de MU
    /// probablemente solo lo manda como ack de haber cargado el mapa DESPUÉS de un teleport/gate real
    /// -- si nunca hace ninguno (como al recién entrar al mundo la primera vez), jamás lo manda, y con
    /// el default viejo (true) el jugador quedaba bloqueado para siempre viendo el mapa vacío (sin
    /// jugadores NI monstruos NI NPCs, ya que <see cref="ViewportTicker"/> salta enteramente el
    /// barrido de un observador con RegenOk==true). Corregido a false para que coincida con el
    /// comportamiento real.
    /// </summary>
    public bool RegenOk { get; set; } // false = visible/normal (default real), true = bloqueado tras teleport
    public bool WorldEntered { get; set; }

    /// <summary>Puerto del flag <c>lpObj->SendQuestInfo</c> (User.h) -- CQuest::GCQuestInfoSend (Quest.cpp:397-417)
    /// solo manda el paquete C1:A0 (blob completo de 50 bytes de estado de misiones) la PRIMERA vez
    /// por sesión; todas las llamadas posteriores (incluida la que dispara CQuest::NpcTalk en cada
    /// diálogo con un NPC de misión) son no-op para esta parte y solo mandan el C1:A1 de estado. Ver
    /// DSProtocol.cpp:503 -- se manda proactivamente al entrar al mundo, junto con ItemListSend/SkillListSend.</summary>
    public bool SendQuestInfo { get; set; }

    /// <summary>Índices de otros jugadores actualmente visibles para este jugador (equivalente
    /// simplificado de VpPlayer[] -- ver ViewportTicker).</summary>
    public HashSet<int> VisibleTo { get; } = new();

    /// <summary>Índices de monstruos actualmente visibles para este jugador (mismo mecanismo que
    /// <see cref="VisibleTo"/> pero para el registro de monstruos -- ver ViewportTicker).</summary>
    public HashSet<int> VisibleMonsters { get; } = new();

    /// <summary>Índices (dentro del mapa actual del jugador, ver <see cref="GroundItem.Index"/>) de
    /// items de piso actualmente visibles -- mismo mecanismo que <see cref="VisibleMonsters"/>. Como
    /// este puerto no tiene cambio de mapa en runtime (sin portales/teletransporte todavía), no hace
    /// falta limpiar este set al cambiar de mapa.</summary>
    public HashSet<int> VisibleGroundItems { get; } = new();

    // ---------------------------------------------------------------- Fase 4: combate (primera pasada)

    /// <summary>Puerto de los campos de combate de OBJECTSTRUCT (PhysiDamageMin/Max, Defense,
    /// AttackSuccessRate, DefenseSuccessRate) -- ver <see cref="RecalcCombatStats"/> para el puerto
    /// real de CObjectManager::CharacterCalcAttribute (Fase 4, segunda pasada, balance real de
    /// items).</summary>
    public int PhysiDamageMin { get; set; }
    public int PhysiDamageMax { get; set; }
    public int Defense { get; set; }
    public int AttackSuccessRate { get; set; }
    public int DefenseSuccessRate { get; set; }

    /// <summary>Daño mágico base (Energy/const, ver <see cref="RecalcCombatStats"/>) -- usado por los
    /// skills de ataque (Fase "skills"), ver ClientProtocolHandler.OnSkillAttackAsync.</summary>
    public int MagicDamageMin { get; set; }
    public int MagicDamageMax { get; set; }

    /// <summary>Índice de clase 0-4 = DW/DK/FE/MG/DL (mismo orden que <see cref="Class"/> crudo, ver
    /// ClientProtocolHandler.ClassFe=2 y CharacterBalanceConfig).</summary>
    private const int ClassDw = 0, ClassDk = 1, ClassFe = 2, ClassMg = 3, ClassDl = 4;

    /// <summary>
    /// Puerto de CObjectManager::CharacterCalcAttribute (ObjectManager.cpp:1887-2523) -- Fase 4,
    /// segunda pasada (balance real de combate, reemplaza el placeholder Str/Nivel de la primera
    /// pasada). Cubre daño físico base por clase, aporte de arma(s) equipada(s) (con el escalado por
    /// nivel +0..+15 de <see cref="ItemCombatMath"/>), bono de flecha/perno, penalización de doble
    /// empuñadura, acierto de ataque y defensa/tasa de defensa (dexterity + piezas de armadura/escudo/
    /// alas equipadas). Simplificaciones documentadas explícitamente frente al original:
    ///   1) Sin crítico/excelente/set-item (dependen de ItemOption.txt/SetItemOption.txt, no
    ///      portados -- ver comentario de cabecera de ItemCombatMath).
    ///   2) Sin el bono de +5%..+30% de Defensa por "5 piezas de armadura al mismo nivel alto" ni el
    ///      +10% de DefenseSuccessRate por "mismo set visual" (ObjectManager.cpp:2314-2421) -- ambos
    ///      dependen de comparar SetItemOption/visual-index entre piezas, fuera de alcance de esta
    ///      pasada.
    ///   3) Sin PhysiSpeed/MagicSpeed (velocidad de ataque) ni HP/MP/BP por Vitalidad/Energía -- no
    ///      hay cooldown de ataque server-side todavía (el cliente ya se autolimita) y Life/MaxLife
    ///      vienen de DataServer, así que no hacen falta para que el combate funcione.
    ///   4) Sin daño mágico (DW/MG/DL con hechizos) -- no hay sistema de skills portado todavía.
    ///   5) Sin las variantes PvP de acierto/defensa (Attack.cpp: MissCheckPvP/GetTargetDefense al
    ///      50% contra jugadores) -- esta fase de combate solo cubre jugador-contra-monstruo.
    /// </summary>
    public void RecalcCombatStats(ItemBalanceTable items, CharacterBalanceConfig cfg)
    {
        int cls = Class switch
        {
            0 => ClassDw, 1 => ClassDk, 2 => ClassFe, 3 => ClassMg, _ => ClassDl,
        };

        var right = Items[Item.SlotWeapon1];
        var left = Items[Item.SlotWeapon2];
        var rightInfo = right.IsItem() ? items.Get(right.Index) : null;
        var leftInfo = left.IsItem() ? items.Get(left.Index) : null;

        // La munición (flecha/perno) NO cuenta como "arma" para doble-empuñadura/suma de daño --
        // solo aporta el bono porcentual de ObjectManager.cpp:2444-2459 (ver más abajo).
        bool rightIsWeapon = rightInfo is { IsWeapon: true } && !rightInfo.IsAmmo;
        bool leftIsWeapon = leftInfo is { IsWeapon: true } && !leftInfo.IsAmmo;

        // ---- Paso 1: daño físico base por clase (ObjectManager.cpp:1974-2053) ----
        int baseMin, baseMax;

        switch (cls)
        {
            case ClassDw:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDw]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDw]);
                break;

            case ClassDk:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDk]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDk]);
                break;

            case ClassFe:
                // Puerto de ObjectManager.cpp:1999-2012: fórmula alternativa si el arma en la mano
                // derecha es un arco/ballesta (sección 4 de Item.txt), sea cual sea el hueco donde
                // esté (algunos arcos van en Weapon2 según Item.txt, ver comentario de ItemBalance).
                bool bow = rightInfo is { Section: 4 };

                if (bow)
                {
                    baseMin = SafeDiv(Strength, cfg.FePhysiDamageMinBowConstA) + SafeDiv(Dexterity, cfg.FePhysiDamageMinBowConstB);
                    baseMax = SafeDiv(Strength, cfg.FePhysiDamageMaxBowConstA) + SafeDiv(Dexterity, cfg.FePhysiDamageMaxBowConstB);
                }
                else
                {
                    baseMin = SafeDiv(Strength + Dexterity, cfg.PhysiDamageMinConstA[ClassFe]);
                    baseMax = SafeDiv(Strength + Dexterity, cfg.PhysiDamageMaxConstA[ClassFe]);
                }
                break;

            case ClassMg:
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassMg]) + SafeDiv(Energy, cfg.PhysiDamageMinConstB[ClassMg]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassMg]) + SafeDiv(Energy, cfg.PhysiDamageMaxConstB[ClassMg]);
                break;

            default: // ClassDl
                baseMin = SafeDiv(Strength, cfg.PhysiDamageMinConstA[ClassDl]) + SafeDiv(Energy, cfg.PhysiDamageMinConstB[ClassDl]);
                baseMax = SafeDiv(Strength, cfg.PhysiDamageMaxConstA[ClassDl]) + SafeDiv(Energy, cfg.PhysiDamageMaxConstB[ClassDl]);
                break;
        }

        // ---- Paso 2: aporte de cada mano equipada (ObjectManager.cpp:2055-2085) ----
        // El báculo (sección 5) solo suma la MITAD de su daño a la rama física (es un arma "mágica").
        int minRight = baseMin, maxRight = baseMax, minLeft = baseMin, maxLeft = baseMax;

        if (rightInfo is { IsWeapon: true })
        {
            int wMin = ItemCombatMath.GetDamageMin(right, rightInfo);
            int wMax = ItemCombatMath.GetDamageMax(right, rightInfo);
            bool staff = rightInfo.Section == 5;
            minRight += staff ? wMin / 2 : wMin;
            maxRight += staff ? wMax / 2 : wMax;
        }

        if (leftInfo is { IsWeapon: true })
        {
            int wMin = ItemCombatMath.GetDamageMin(left, leftInfo);
            int wMax = ItemCombatMath.GetDamageMax(left, leftInfo);
            bool staff = leftInfo.Section == 5;
            minLeft += staff ? wMin / 2 : wMin;
            maxLeft += staff ? wMax / 2 : wMax;
        }

        // ---- Paso 3: bono de flecha/perno (ObjectManager.cpp:2444-2459) ----
        // Arco/ballesta en la derecha + munición con nivel de mejora en la izquierda -- el bono usa
        // el NIVEL CRUDO de la munición (Item.Level), no su daño escalado (que es 0).
        if (rightIsWeapon && rightInfo!.Section == 4 && leftInfo is { IsAmmo: true })
        {
            int rate = (left.Level * 2) + 1;
            minRight += (minRight * rate / 100) + 1;
            maxRight += (maxRight * rate / 100) + 1;
        }

        // ---- Paso 4: penalización de doble empuñadura (ObjectManager.cpp:2461-2473) ----
        // DK/MG/DL con dos armas cuerpo a cuerpo (secciones 0-3) a la vez -- ambas manos al 55%.
        bool meleeDual = rightIsWeapon && leftIsWeapon && rightInfo!.Section <= 3 && leftInfo!.Section <= 3
            && cls is ClassDk or ClassMg or ClassDl;

        if (meleeDual)
        {
            minRight = minRight * 55 / 100;
            maxRight = maxRight * 55 / 100;
            minLeft = minLeft * 55 / 100;
            maxLeft = maxLeft * 55 / 100;
        }

        // ---- Paso 5: total (Attack.cpp:1191-1258, simplificado a un solo golpe combinado) ----
        int totalMin, totalMax;

        if (rightIsWeapon && leftIsWeapon)
        {
            totalMin = minRight + minLeft;
            totalMax = maxRight + maxLeft;
        }
        else if (rightIsWeapon)
        {
            totalMin = minRight;
            totalMax = maxRight;
        }
        else if (leftIsWeapon)
        {
            totalMin = minLeft;
            totalMax = maxLeft;
        }
        else
        {
            totalMin = baseMin;
            totalMax = baseMax;
        }

        PhysiDamageMin = Math.Max(totalMin, 0);
        PhysiDamageMax = Math.Max(totalMax, PhysiDamageMin + 1);

        // ---- Paso 6: acierto de ataque (ObjectManager.cpp:2096-2136, rama PvM) ----
        int asr = ((int)Level * cfg.AttackSuccessRateConstA[cls])
            + SafeDiv(Dexterity * (uint)cfg.AttackSuccessRateConstB[cls], cfg.AttackSuccessRateConstC[cls])
            + SafeDiv(Strength, cfg.AttackSuccessRateConstD[cls]);

        if (cls == ClassDl)
        {
            asr += SafeDiv(Leadership, cfg.DlAttackSuccessRateConstE);
        }

        AttackSuccessRate = Math.Max(asr, 0);

        // ---- Paso 7: defensa y tasa de defensa (ObjectManager.cpp:2230-2422) ----
        // Dexterity/const + suma de GetDefense()/GetDefenseSuccessRate() de Weapon2 (si es escudo),
        // Helm, Armor, Pants, Gloves, Boots y Wing -- cada pieza devuelve 0 si está rota o si esa
        // sección no tiene la columna correspondiente (ej. un arma en Weapon2 no tiene
        // DefenseSuccessRate, ver World/ItemBalance.cs).
        int def = SafeDiv(Dexterity, cfg.DefenseConstA[cls]);
        int dsr = SafeDiv(Dexterity, cfg.DefenseSuccessRateConstA[cls]);

        Span<int> armorSlots = stackalloc[]
        {
            Item.SlotWeapon2, Item.SlotHelm, Item.SlotArmor, Item.SlotPants, Item.SlotGloves, Item.SlotBoots, Item.SlotWing,
        };

        foreach (var slot in armorSlots)
        {
            var piece = Items[slot];

            if (!piece.IsItem())
            {
                continue;
            }

            var info = items.Get(piece.Index);

            if (info == null)
            {
                continue;
            }

            def += ItemCombatMath.GetDefense(piece, info);
            dsr += ItemCombatMath.GetDefenseSuccessRate(piece, info);
        }

        Defense = Math.Max(def, 0);
        DefenseSuccessRate = Math.Max(dsr, 0);

        // ---- Paso 8: daño mágico base (ObjectManager.cpp:1980-1994 etc, Fase "skills") ----
        // Energy/const, idéntico para las 5 clases con los valores reales (9,4) pero cargado por
        // clase igual (ver CharacterBalanceConfig). Bono de arma mágica (espada/báculo con columna
        // MagicDamageRate en Item.txt, ej. "Dark Reign Blade"/"Rune Blade"/cualquier báculo) --
        // puerto de Attack.cpp:1341-1345, sin el factor de "durabilidad actual fraccionaria" del
        // original (se usa 1.0 si el arma no está rota, mismo tipo de simplificación que el resto de
        // este puerto).
        int magicMin = SafeDiv(Energy, cfg.MagicDamageMinConstA[cls]);
        int magicMax = SafeDiv(Energy, cfg.MagicDamageMaxConstA[cls]);

        if (rightInfo != null && right.Durability > 0 && (rightInfo.Section == 0 || rightInfo.Section == 5) && rightInfo.MagicDamageRate > 0)
        {
            int rise = (rightInfo.MagicDamageRate / 2) + (right.Level * 2);
            magicMin += (magicMin * rise) / 100;
            magicMax += (magicMax * rise) / 100;
        }

        MagicDamageMin = Math.Max(magicMin, 0);
        MagicDamageMax = Math.Max(magicMax, MagicDamageMin + 1);

        // ---- Attack Speed y Magic Speed (ObjectManager.cpp:2138-2228) ----
        int basePhysiSpeed = cls switch
        {
            ClassDw => SafeDiv(Dexterity, 20),
            ClassDk => SafeDiv(Dexterity, 15),
            ClassFe => SafeDiv(Dexterity, 50),
            ClassMg => SafeDiv(Dexterity, 15),
            _ => SafeDiv(Dexterity, 10),
        };

        int baseMagicSpeed = cls switch
        {
            ClassDw => SafeDiv(Dexterity, 10),
            ClassDk => SafeDiv(Dexterity, 20),
            ClassFe => SafeDiv(Dexterity, 50),
            ClassMg => SafeDiv(Dexterity, 20),
            _ => SafeDiv(Dexterity, 10),
        };

        int bonusSpeed = 0;

        int rightSpeed = (rightIsWeapon && rightInfo != null && right.Durability > 0) ? rightInfo.AttackSpeed : 0;
        int leftSpeed = (leftIsWeapon && leftInfo != null && left.Durability > 0) ? leftInfo.AttackSpeed : 0;

        if (rightIsWeapon && leftIsWeapon)
        {
            bonusSpeed += (rightSpeed + leftSpeed) / 2;
        }
        else if (rightIsWeapon)
        {
            bonusSpeed += rightSpeed;
        }
        else if (leftIsWeapon)
        {
            bonusSpeed += leftSpeed;
        }

        if (rightIsWeapon && (right.NewOption & 8) != 0) bonusSpeed += 7;
        if (leftIsWeapon && (left.NewOption & 8) != 0) bonusSpeed += 7;

        var gloves = Items[Item.SlotGloves];
        if (gloves.IsItem() && gloves.Durability > 0)
        {
            var gInfo = items.Get(gloves.Index);
            if (gInfo != null) bonusSpeed += gInfo.AttackSpeed;
        }

        var helper = Items[Item.SlotHelper];
        if (helper.IsItem() && helper.Durability > 0)
        {
            var hInfo = items.Get(helper.Index);
            if (hInfo != null) bonusSpeed += hInfo.AttackSpeed;
        }

        var amulet = Items[Item.SlotRing1];
        if (amulet.IsItem() && amulet.Durability > 0)
        {
            var aInfo = items.Get(amulet.Index);
            if (aInfo != null)
            {
                bonusSpeed += aInfo.AttackSpeed;
                if ((amulet.NewOption & 8) != 0) bonusSpeed += 7;
            }
        }

        PhysiSpeed = basePhysiSpeed + bonusSpeed;
        MagicSpeed = baseMagicSpeed + bonusSpeed;

        // ---- Paso 9: recalculación de MaxLife y MaxMana (ObjectManager.cpp:2475-2508) ----
        // Defaults base y multiplicadores de DefaultClassInfo.txt:
        // DW (0): BaseHP=60, LevelHP=1.0, VitHP=2.0; BaseMP=60, LevelMP=2.0, EneMP=2.0
        // DK (1): BaseHP=110, LevelHP=2.0, VitHP=3.0; BaseMP=20, LevelMP=0.5, EneMP=1.0
        // FE (2): BaseHP=80, LevelHP=1.0, VitHP=2.0; BaseMP=30, LevelMP=1.5, EneMP=1.5
        // MG (3): BaseHP=110, LevelHP=1.0, VitHP=2.0; BaseMP=60, LevelMP=1.0, EneMP=2.0
        // DL (4): BaseHP=90, LevelHP=1.5, VitHP=2.0; BaseMP=40, LevelMP=1.0, EneMP=1.5
        float[] baseHp = { 60f, 110f, 80f, 110f, 90f };
        float[] levelHp = { 1.0f, 2.0f, 1.0f, 1.0f, 1.5f };
        float[] vitHp = { 2.0f, 3.0f, 2.0f, 2.0f, 2.0f };
        uint[] baseVit = { 15, 25, 20, 26, 20 };

        float[] baseMp = { 60f, 20f, 30f, 60f, 40f };
        float[] levelMp = { 2.0f, 0.5f, 1.5f, 1.0f, 1.0f };
        float[] eneMp = { 2.0f, 1.0f, 1.5f, 2.0f, 1.5f };
        uint[] baseEne = { 30, 10, 15, 26, 15 };

        float maxLife = baseHp[cls] + (levelHp[cls] * Math.Max((int)Level - 1, 0)) + ((float)Math.Max((int)Vitality - (int)baseVit[cls], 0) * vitHp[cls]);
        float maxMana = baseMp[cls] + (levelMp[cls] * Math.Max((int)Level - 1, 0)) + ((float)Math.Max((int)Energy - (int)baseEne[cls], 0) * eneMp[cls]);

        MaxLife = (uint)Math.Max(maxLife, 1f);
        MaxMana = (uint)Math.Max(maxMana, 1f);

        // ---- Paso 10: recalculación de MaxBP / AG (CharacterCalcBP, ObjectManager.cpp:1865-1884) ----
        double maxBp = cls switch
        {
            ClassDw => (Strength * 0.20) + (Dexterity * 0.40) + (Vitality * 0.30) + (Energy * 0.20),
            ClassDk => (Strength * 0.15) + (Dexterity * 0.20) + (Vitality * 0.30) + (Energy * 1.00),
            ClassFe => (Strength * 0.30) + (Dexterity * 0.20) + (Vitality * 0.30) + (Energy * 0.20),
            ClassMg => (Strength * 0.20) + (Dexterity * 0.25) + (Vitality * 0.30) + (Energy * 0.15),
            _ => (Strength * 0.30) + (Dexterity * 0.20) + (Vitality * 0.10) + (Energy * 0.15) + (Leadership * 0.30),
        };

        MaxBP = (uint)Math.Max(maxBp, 1.0);

        Life = Math.Min(Life, MaxLife);
        Mana = Math.Min(Mana, MaxMana);
        BP = Math.Min(BP, MaxBP);
    }

    private static int SafeDiv(uint value, int div) => div <= 0 ? 0 : (int)(value / (uint)div);

    // ---------------------------------------------------------------- Fase "skills": magia y maná

    /// <summary>Puerto de lpObj->SkillDelay[MAX_SKILL] (CheckSkillDelay, SkillManager.cpp:440-454) --
    /// último instante en el que se casteó cada skill (por índice), para el cooldown por skill de la
    /// columna "Delay" (milisegundos) de SkillList.txt.</summary>
    public Dictionary<int, DateTime> SkillDelay { get; } = new();

    // ---------------------------------------------------------------- Fase 5: party (primera pasada)

    /// <summary>Puerto de lpObj->PartyNumber -- ID del grupo en PartyRegistry, -1 = sin grupo (igual
    /// convención que el original).</summary>
    public int PartyNumber { get; set; } = -1;

    /// <summary>Puerto MUY simplificado de Interface.type==INTERFACE_PARTY/TargetNumber (Party.cpp):
    /// solo se necesitan estos dos campos (uno por lado de la invitación) para validar que la
    /// respuesta de "aceptar/rechazar" corresponda a una invitación realmente pendiente -- el
    /// original además bloquea otras interfaces (tienda, diálogo NPC, etc.) mientras hay una
    /// invitación de party abierta, cosa que no aplica todavía porque esas interfaces no están
    /// portadas. -1 = sin invitación pendiente en ese rol.</summary>
    public int PartyInviteTargetIndex { get; set; } = -1; // yo invité a este índice, esperando su respuesta
    public int PartyInviterIndex { get; set; } = -1;      // este índice me invitó a mí, esperando mi respuesta
}

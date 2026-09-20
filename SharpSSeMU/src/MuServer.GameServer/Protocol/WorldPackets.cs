using System.Text;
using MuServer.GameServer.Config;
using MuServer.GameServer.World;
using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

// Puerto de los paquetes de Fase 2 (Protocol.h/DSProtocol.h/Viewport.h) -- selección de personaje,
// entrada al mundo, viewport (aparecer/desaparecer OTROS JUGADORES, no monstruos/NPCs todavía) y
// movimiento. Layouts confirmados byte a byte contra el .h original.

// ---------------------------------------------------------------- GameServer -> DataServer (0x04)

public static class DataServerCharacterPacketBuilder
{
    /// <summary>SDHP_CHARACTER_INFO_SEND (GS->DS), C1:04: index+account+name -- espejo exacto de lo
    /// que MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterInfoRecv.Parse espera leer.</summary>
    public static byte[] CharacterInfoRequest(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x04, w.ToArray());
    }

    /// <summary>SDHP_WAREHOUSE_ITEM_SEND (GS->DS), C1:05:00: index+account+WarehouseNumber.</summary>
    public static byte[] WarehouseInfoRequest(ushort index, string account)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        Span<byte> accountBytes = stackalloc byte[10];
        accountBytes.Clear();
        Encoding.ASCII.GetBytes(account, accountBytes);
        w.WriteBytes(accountBytes.ToArray(), 10);
        w.WriteUInt32(0);
        return PacketBuilder.BuildC1Sub(0x05, 0x00, w.ToArray());
    }

    /// <summary>SDHP_WAREHOUSE_ITEM_SAVE_SEND (GS->DS), C2:05:30: index+account+1920bytes+money+password+WarehouseNumber.</summary>
    public static byte[] WarehouseSaveRequest(ushort index, string account, PlayerObject player)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        Span<byte> accountBytes = stackalloc byte[10];
        accountBytes.Clear();
        Encoding.ASCII.GetBytes(account, accountBytes);
        w.WriteBytes(accountBytes.ToArray(), 10);

        byte[] itemsBlob = new byte[1920];
        Span<byte> itemInfo = stackalloc byte[Item.WireByteSize];
        for (int slot = 0; slot < player.WarehouseItems.Length && slot < 120; slot++)
        {
            var item = player.WarehouseItems[slot];
            if (item.IsItem())
            {
                item.ToWireBytes(itemInfo);
                itemInfo.CopyTo(itemsBlob.AsSpan(slot * 16, 16));
            }
            else
            {
                itemsBlob.AsSpan(slot * 16, 16).Fill(0xFF);
            }
        }
        w.WriteBytes(itemsBlob, 1920);
        w.WriteUInt32(player.WarehouseMoney);
        w.WriteUInt32(player.WarehousePassword);
        w.WriteUInt32(0);
        return PacketBuilder.BuildC2Sub(0x05, 0x30, w.ToArray());
    }

    /// <summary>C1:0x70 -- avisa a DataServer que este índice ahora controla este personaje (en
    /// memoria, ver DataServerProtocolHandler.OnConnectCharacterAsync ya implementado).</summary>
    public static byte[] ConnectCharacter(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x70, w.ToArray());
    }

    /// <summary>C1:0x71 -- contraparte de desconexión.</summary>
    public static byte[] DisconnectCharacter(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1(0x71, w.ToArray());
    }

    /// <summary>
    /// SDHP_CHARACTER_INFO_SAVE_SEND (GS->DS), C2:0x30 -- puerto de GDCharacterInfoSaveSend
    /// (DSProtocol.cpp:991-1047). Espejo exacto de lo que
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterInfoSaveRecv.Parse espera leer
    /// (que ya estaba implementado y conectado a NpgsqlCharacterDataRepository.SaveCharacterAsync
    /// desde antes -- lo que faltaba era este lado GameServer->DataServer, nunca se mandaba este
    /// paquete). Bug real confirmado esta sesión y causa raíz de "el progreso no se guarda"/"todos
    /// los personajes aparecen siempre en la misma posición X=182 Y=128": sin este paquete, la fila
    /// de <c>character</c> en la base nunca se actualiza después de la creación (que sí graba la
    /// posición default de <c>DefaultClassType</c>, 182/128 para DW/DK/MG/DL -- ese valor coincide
    /// byte a byte con el INSERT real de MuOnline.sql, no es hardcodeo nuestro), así que CADA login
    /// vuelve a leer para siempre esa misma fila sin tocar, sin importar cuánto haya caminado el
    /// personaje en sesiones anteriores.
    /// </summary>
    public static byte[] CharacterInfoSaveSend(World.PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16((ushort)p.Index);
        w.WriteFixedString(p.Account, 11);
        w.WriteFixedString(p.Name, 11);
        w.WriteUInt16(p.Level);
        w.WriteByte((byte)((p.Class * 16) + p.ChangeUp)); // DBClass, mismo patrón que CharacterInfoSend
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(p.Money);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteBytes(p.Inventory, 1728); // ya sincronizado por SetItem, ver doc-comment de PlayerObject.Inventory
        w.WriteBytes(p.Skill, 180);
        w.WriteByte(p.Map);
        w.WriteByte(p.X); // posición VIVA (actualizada por OnMoveAsync) -- no la de login
        w.WriteByte(p.Y);
        w.WriteByte(p.Dir);
        w.WriteUInt32(p.PKCount);
        w.WriteUInt32(p.PKLevel);
        w.WriteUInt32(p.PKTime);
        w.WriteBytes(p.Quest, 50);
        w.WriteUInt32(p.ChatLimitTime);
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteBytes(p.Effect, 208);
        w.WriteUInt16(p.BCCount);
        w.WriteUInt16(p.CCCount);
        w.WriteUInt16(p.DSCount);
        return PacketBuilder.BuildC2(0x30, w.ToArray());
    }

    /// <summary>SDHP_CHARACTER_LIST_SEND (GS->DS), C1:01: index+account -- pide a DataServer la
    /// lista de personajes de la cuenta (espejo exacto de lo que
    /// MuServer.DataServer\Protocol\DataServerPackets.cs::CharacterListRecv.Parse espera leer).
    /// Puerto de GDCharacterListSend (DSProtocol.cpp).</summary>
    public static byte[] CharacterListRequest(ushort index, string account)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        return PacketBuilder.BuildC1(0x01, w.ToArray());
    }

    /// <summary>SDHP_CHARACTER_CREATE_SEND (GS->DS), C1:02 -- espejo exacto de lo que
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::CharacterCreateRecv.Parse espera leer.
    /// Puerto de GDCharacterCreateSend (DSProtocol.cpp). A diferencia del original, acá NO se
    /// replica la validación previa de clase/CARD_CODE de CGCharacterCreateRecv (esa lógica depende
    /// de un sistema de desbloqueo de MG/DL/SU/RF por nivel de cuenta que no está portado) -- se
    /// reenvía directo y se deja que DataServer sea la autoridad: su CreateCharacterAsync ya rechaza
    /// cualquier clase que no tenga fila en default_class_type (hoy solo DW/DK/FE/MG/SUM, ver
    /// db/postgres/003_default_class_seed.sql) con result=2, así que el efecto visible es el mismo
    /// (no se puede crear una clase no habilitada) sin duplicar la lógica de validación.</summary>
    public static byte[] CharacterCreateRequest(ushort index, string account, string name, byte characterClass)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(characterClass);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }
}

/// <summary>SDHP_CHARACTER_CREATE_RECV (DS->GS), C1:02 -- espejo exacto de
/// MuServer.DataServer/Protocol/DataServerPackets.cs::DataServerPacketBuilder.CharacterCreateSend.
/// Puerto de DGCharacterCreateRecv (DSProtocol.cpp) -- solo la parte de parseo; la conversión de
/// Class al formato que espera el cliente vive en ClientProtocolHandler (mismo lugar que el resto de
/// conversiones DataServer-a-cliente de esta fase).</summary>
public sealed record CharacterCreateResultFromDataServer(ushort Index, string Account, string Name, byte Result, byte Slot, byte Class, byte[] Equipment, ushort Level)
{
    public static CharacterCreateResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3); // C1 header: type,size,head = 3 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var slot = r.ReadByte();
        var cls = r.ReadByte();
        var equipment = r.ReadBytes(24);
        var level = r.ReadUInt16();
        return new CharacterCreateResultFromDataServer(index, account, name, result, slot, cls, equipment, level);
    }
}

/// <summary>Un personaje dentro de SDHP_CHARACTER_LIST_RECV (DS->GS) -- el Inventory viaja todavía
/// en el formato compacto de 60 bytes (5 bytes/slot de equipo, ver Item.FromCompactPreviewBytes);
/// OnCharacterListFromDataServerAsync es quien lo convierte a CharSet[13] para el cliente.</summary>
public sealed record CharacterListEntryFromDataServer(byte Slot, string Name, ushort Level, byte Class, byte CtlCode, byte[] CompactInventory);

/// <summary>SDHP_CHARACTER_LIST_RECV (DS->GS), C2:01 -- espejo exacto de
/// MuServer.DataServer\Protocol\DataServerPackets.cs::DataServerPacketBuilder.CharacterListSend.
/// Puerto de DGCharacterListRecv (DSProtocol.cpp), solo la parte de parseo -- la conversión a
/// CharSet vive en ClientProtocolHandler (mismo lugar que ya porta CharacterMakePreviewCharSet).</summary>
public sealed record CharacterListFromDataServer(
    ushort Index, string Account, byte MoveCnt, byte ExtClass, IReadOnlyList<CharacterListEntryFromDataServer> Entries)
{
    public static CharacterListFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C2 header: type,size(2),head = 4 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var moveCnt = r.ReadByte();
        var extClass = r.ReadByte();
        var count = r.ReadByte();

        var entries = new List<CharacterListEntryFromDataServer>(count);

        for (int n = 0; n < count; n++)
        {
            var slot = r.ReadByte();
            var name = r.ReadFixedString(11);
            var level = r.ReadUInt16();
            var cls = r.ReadByte();
            var ctlCode = r.ReadByte();
            var compact = r.ReadBytes(60);
            entries.Add(new CharacterListEntryFromDataServer(slot, name, level, cls, ctlCode, compact));
        }

        return new CharacterListFromDataServer(index, account, moveCnt, extClass, entries);
    }
}

/// <summary>SDHP_CHARACTER_INFO_RECV (DS->GS), C2:04 -- espejo exacto de
/// MuServer.DataServer/Protocol/DataServerPackets.cs::DataServerPacketBuilder.CharacterInfoSend.</summary>
public sealed record CharacterInfoFromDataServer(
    ushort Index, string Account, string Name, byte Result, byte Class, ushort Level, uint LevelUpPoint,
    uint Experience, uint Money, uint Strength, uint Dexterity, uint Vitality, uint Energy, uint Leadership,
    uint Life, uint MaxLife, uint Mana, uint MaxMana, uint BP, uint MaxBP, byte[] Inventory, byte[] Skill,
    byte Map, byte X, byte Y, byte Dir, uint PkCount, uint PkLevel, uint PkTime, byte CtlCode, byte[] Quest,
    uint ChatLimitTime, ushort FruitAddPoint, ushort FruitSubPoint, byte[] Effect, uint Reset, uint MasterReset,
    uint IsNewChar, ushort Married, string MarryName, ushort BcCount, ushort CcCount, ushort DsCount)
{
    public static CharacterInfoFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C2 header: type,size(2),head = 4 bytes
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var cls = r.ReadByte();
        var level = r.ReadUInt16();
        var levelUpPoint = r.ReadUInt32();
        var experience = r.ReadUInt32();
        var money = r.ReadUInt32();
        var strength = r.ReadUInt32();
        var dexterity = r.ReadUInt32();
        var vitality = r.ReadUInt32();
        var energy = r.ReadUInt32();
        var leadership = r.ReadUInt32();
        var life = r.ReadUInt32();
        var maxLife = r.ReadUInt32();
        var mana = r.ReadUInt32();
        var maxMana = r.ReadUInt32();
        var bp = r.ReadUInt32();
        var maxBp = r.ReadUInt32();
        var inventory = r.ReadBytes(1728);
        var skill = r.ReadBytes(180);
        var map = r.ReadByte();
        var x = r.ReadByte();
        var y = r.ReadByte();
        var dir = r.ReadByte();
        var pkCount = r.ReadUInt32();
        var pkLevel = r.ReadUInt32();
        var pkTime = r.ReadUInt32();
        var ctlCode = r.ReadByte();
        var quest = r.ReadBytes(50);
        var chatLimitTime = r.ReadUInt32();
        var fruitAddPoint = r.ReadUInt16();
        var fruitSubPoint = r.ReadUInt16();
        var effect = r.ReadBytes(208);
        var reset = r.ReadUInt32();
        var masterReset = r.ReadUInt32();
        var isNewChar = r.ReadUInt32();
        var married = r.ReadUInt16();
        var marryName = r.ReadFixedString(11);
        var bcCount = r.ReadUInt16();
        var ccCount = r.ReadUInt16();
        var dsCount = r.ReadUInt16();

        return new CharacterInfoFromDataServer(
            index, account, name, result, cls, level, levelUpPoint, experience, money, strength, dexterity,
            vitality, energy, leadership, life, maxLife, mana, maxMana, bp, maxBp, inventory, skill, map, x, y,
            dir, pkCount, pkLevel, pkTime, ctlCode, quest, chatLimitTime, fruitAddPoint, fruitSubPoint, effect,
            reset, masterReset, isNewChar, married, marryName, bcCount, ccCount, dsCount);
    }
}

// ---------------------------------------------------------------- GameServer <-> Client

public sealed record CharacterInfoRecv(string Name)
{
    // PMSG_CHARACTER_INFO_RECV (Protocol.h:252-256): PSBMSG_HEAD(4) + char name[10] -- OJO, 10 bytes
    // acá, no 11 como en la mayoría de los otros campos de nombre del protocolo.
    public static CharacterInfoRecv Parse(byte[] p) => new(PacketBuilder.ReadFixedString(p.AsSpan(4, 10)));
}

public sealed record MoveRecv(byte X, byte Y, byte[] Path)
{
    // PMSG_MOVE_RECV declara path[8] fijo en el struct C++, pero el cliente real NO manda los 8
    // bytes siempre -- header.size refleja el tamaño real enviado (que depende de cuántos pasos
    // tiene el movimiento: 1 paso no necesita los 8 bytes de path, alcanza con path[0]). En C++ leer
    // más allá de header.size es "seguro" porque el struct está mapeado sobre un buffer más grande
    // que igual existe en memoria (con basura, pero no crashea); acá el array ya viene del tamaño
    // exacto que puso el framer, así que pedir 8 bytes fijos cuando el paquete es más corto tira
    // ArgumentOutOfRangeException -- confirmado con el cliente real (WorldTestClient nunca lo
    // exponía porque siempre arma un path de 8 bytes completo). Se rellena con 0 lo que falte,
    // exactamente el mismo efecto práctico que "leer basura no inicializada" tenía en el original
    // (esos bytes no se usan si pathCount no llega a cubrirlos, ver OnMoveAsync).
    public static MoveRecv Parse(byte[] p)
    {
        var path = new byte[8];
        int available = Math.Clamp(p.Length - 5, 0, 8);

        if (available > 0)
        {
            p.AsSpan(5, available).CopyTo(path);
        }

        return new MoveRecv(p[3], p[4], path);
    }
}

public static class WorldPacketBuilder
{
    /// <summary>Constantes reales de <c>GameServerInfo - Common.dat</c> (ver
    /// <see cref="MuServer.GameServer.Config.ServerInfoConfig"/>) que <see cref="NextExperience"/>
    /// necesita -- seteado una vez en Program.cs al arrancar. Si nunca se setea (ej. algún test viejo
    /// que no pasa por Program.cs), se usa una instancia con los defaults de fábrica, así que el
    /// puerto sigue andando igual que antes de que este archivo se cargara.</summary>
    public static MuServer.GameServer.Config.ServerInfoConfig ServerInfo { get; set; } = new();

    /// <summary>PMSG_CHARACTER_INFO_SEND, C3:F3:03 -- construida como "lógica" C1Sub (el tipo real
    /// C3 lo pone ClientSession.SendEncryptedAsync al cifrar). GAMESERVER_EXTRA==1 en este build,
    /// así que van los 13 DWORD "View*" extra.</summary>
    public static byte[] CharacterInfoSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte(p.X);
        w.WriteByte(p.Y);
        w.WriteByte(p.Map);
        w.WriteByte(p.Dir);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(NextExperience(p.Level));
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Strength, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Dexterity, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Vitality, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Energy, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        // PMSG_CHARACTER_INFO_SEND no tiene #pragma pack(1) en el original (Protocol.h:589-632), así
        // que MSVC inserta 2 bytes de relleno acá para alinear el DWORD Money a 4 bytes (offset 38
        // tras MaxBP no es múltiplo de 4). Sin este relleno, Money y TODO lo que sigue (PKLevel,
        // CtlCode, FruitAddPoint... y los 13 DWORD "View*" del final) le llega desplazado 2 bytes al
        // cliente real -- confirmado como la causa de los stats con "números infinitos" reportados
        // jugando con el cliente real (WorldTestClient nunca lo detectó porque no valida los bytes
        // exactos de este paquete, solo que llegue).
        w.WriteUInt16(0); // relleno de alineación (no existe en el struct, es padding del compilador)
        w.WriteUInt32(p.Money);
        w.WriteByte(p.PKLevel);
        w.WriteByte(p.CtlCode);
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0); // MaxFruitAddPoint -- config de balance, no portado todavía (Fase 3+)
        w.WriteUInt16((ushort)Math.Min(p.Leadership, ushort.MaxValue));
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0); // MaxFruitSubPoint (idem)
        // GAMESERVER_EXTRA==1 -- offset acá ya cae en múltiplo de 4 (52), no hace falta más relleno.
        w.WriteUInt32(p.Reset);
        w.WriteUInt32(0); // ViewPoint (puntos de reset acumulados para gastar -- Fase 3+)
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0x03, w.ToArray());
    }

    /// <summary>PMSG_NEW_CHARACTER_CALC_SEND (C1:F3:E1) -- Actualiza todos los stats visuales ampliados en el cliente (Attack Speed, Daño, Defensa, etc.).</summary>
    public static byte[] NewCharacterCalcSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(0); // ViewAddStrength
        w.WriteUInt32(0); // ViewAddDexterity
        w.WriteUInt32(0); // ViewAddVitality
        w.WriteUInt32(0); // ViewAddEnergy
        w.WriteUInt32(0); // ViewAddLeadership
        w.WriteUInt32((uint)Math.Max(0, p.PhysiDamageMin));
        w.WriteUInt32((uint)Math.Max(0, p.PhysiDamageMax));
        w.WriteUInt32((uint)Math.Max(0, p.MagicDamageMin));
        w.WriteUInt32((uint)Math.Max(0, p.MagicDamageMax));
        w.WriteUInt32(0); // ViewCurseDamageMin
        w.WriteUInt32(0); // ViewCurseDamageMax
        w.WriteUInt32(100); // ViewMulPhysiDamage
        w.WriteUInt32(100); // ViewDivPhysiDamage
        w.WriteUInt32(100); // ViewMulMagicDamage
        w.WriteUInt32(100); // ViewDivMagicDamage
        w.WriteUInt32(100); // ViewMulCurseDamage
        w.WriteUInt32(100); // ViewDivCurseDamage
        w.WriteUInt32(0); // ViewMagicDamageRate
        w.WriteUInt32(0); // ViewCurseDamageRate
        w.WriteUInt32((uint)Math.Max(0, p.PhysiSpeed));
        w.WriteUInt32((uint)Math.Max(0, p.MagicSpeed));
        w.WriteUInt32((uint)Math.Max(0, p.AttackSuccessRate));
        w.WriteUInt32((uint)Math.Max(0, p.AttackSuccessRate)); // PvP
        w.WriteUInt32((uint)Math.Max(0, p.Defense));
        w.WriteUInt32((uint)Math.Max(0, p.DefenseSuccessRate));
        w.WriteUInt32((uint)Math.Max(0, p.DefenseSuccessRate)); // PvP
        w.WriteUInt32(0); // ViewDamageMultiplier
        w.WriteUInt32(0); // ViewRFDamageMultiplierA
        w.WriteUInt32(0); // ViewRFDamageMultiplierB
        w.WriteUInt32(0); // ViewRFDamageMultiplierC
        w.WriteUInt32(0); // ViewDarkSpiritAttackDamageMin
        w.WriteUInt32(0); // ViewDarkSpiritAttackDamageMax
        w.WriteUInt32(0); // ViewDarkSpiritAttackSpeed
        w.WriteUInt32(0); // ViewDarkSpiritAttackSuccessRate
        return PacketBuilder.BuildC1Sub(0xF3, 0xE1, w.ToArray());
    }

    /// <summary>Puerto exacto de <c>gObjSetExperienceTable</c> (User.cpp:276-297): <c>gLevelExperience[n]
    /// = (n+9)*n*n*ExperienceMultiplierConstA</c> para <c>n&lt;=255</c>, más un término extra para
    /// niveles por encima de 255 usando <see cref="Config.ServerInfoConfig.ExperienceMultiplierConstB"/>
    /// (<c>over=n-255</c>, incrementando cada nivel: <c>+= (over+9)*over*over*ConstB</c>). Antes de
    /// portar <c>GameServerInfo - Common.dat</c> esto era un placeholder <c>nivel²*1000</c>.</summary>
    internal static uint NextExperience(int level)
    {
        long constA = ServerInfo.ExperienceMultiplierConstA;
        long constB = ServerInfo.ExperienceMultiplierConstB;
        long n = Math.Max(level, 0);

        long baseN = Math.Min(n, 255);
        long exp = (baseN + 9) * baseN * baseN * constA;

        if (n > 255)
        {
            long over = n - 255;
            exp += (over + 9) * over * over * constB;
        }

        return (uint)Math.Clamp(exp, 0, uint.MaxValue);
    }

    /// <summary>PMSG_CHARACTER_REGEN_SEND, C3:F3:04 (Protocol.h:634-651) -- enviado al jugador cuando muere
    /// y reaparece (respawn). Ordena al cliente main.exe levantar al personaje del suelo, reubicar la cámara
    /// y el modelo en (X, Y, Map, Dir) y restablecer los stats de vida/maná/experiencia/dinero.</summary>
    public static byte[] CharacterRegenSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte(p.X);
        w.WriteByte(p.Y);
        w.WriteByte(p.Map);
        w.WriteByte(p.Dir);
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16(0); // 2 bytes padding de alineación de MSVC antes de DWORD Experience
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(p.Money);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.BP);
        return PacketBuilder.BuildC1Sub(0xF3, 0x04, w.ToArray());
    }

    /// <summary>PMSG_NEW_CHARACTER_INFO_SEND, C1:F3:E0 -- stats derivados para la UI del propio
    /// jugador (no viewport). Se manda una vez al entrar, con los mismos valores base (sin bonos de
    /// items/skills, que se agregan en fases posteriores).</summary>
    public static byte[] NewCharacterInfoSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16(p.Level);
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(NextExperience(p.Level));
        w.WriteUInt16((ushort)Math.Min(p.Strength, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Dexterity, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Vitality, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Energy, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Leadership, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.Life, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.Mana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.BP, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0);
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0);
        // Igual que en CharacterInfoSend: PMSG_NEW_CHARACTER_INFO_SEND (Protocol.h:744-780) tampoco
        // tiene pack(1) -- acá el DWORD ViewReset necesita 2 bytes de relleno para caer alineado a 4
        // (offset 42 tras MaxFruitSubPoint no es múltiplo de 4).
        w.WriteUInt16(0); // relleno de alineación
        w.WriteUInt32(p.Reset);
        w.WriteUInt32(0);
        w.WriteUInt32(p.Life);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.Mana);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.BP);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0xE0, w.ToArray());
    }

    /// <summary>PMSG_MOVE_SEND, C1:D7.</summary>
    public static byte[] MoveSend(int index, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0xD7, w.ToArray());
    }

    /// <summary>PMSG_POSITION_SEND, C1:D0 -- usado para forzar/corregir la posición del cliente
    /// (Move rechazado por colisión o fuera de rango). CORREGIDO: el struct real (Protocol.h:339-345)
    /// es <c>header + index[2] + x + y</c> -- este puerto mandaba SOLO x,y (2 bytes de payload) sin el
    /// <c>index[2]</c> inicial (4 bytes de payload reales). El cliente, que lee este paquete a offset
    /// fijo esperando 4 bytes de payload, terminaba interpretando los 2 bytes que sí mandábamos (nuestro
    /// x,y) como si fueran <c>index[0..1]</c>, y leía basura (bytes del siguiente paquete en el mismo
    /// bloque, o nada) como el x,y real -- la posición de corrección le llegaba corrompida. Esto se
    /// manda EXACTAMENTE cuando un movimiento se rechaza (colisión/fuera de rango, ver
    /// ClientProtocolHandler.OnMoveAsync), que es precisamente el momento típico de "caminar para
    /// acercarse a atacar" -- coincide con el reporte de "atacar me manda de vuelta a mi sitio de
    /// aparición" con el cliente real.</summary>
    public static byte[] PositionSend(int index, byte x, byte y)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        return PacketBuilder.BuildC1(0xD0, w.ToArray());
    }

    /// <summary>PMSG_TELEPORT_SEND, C3:1C -- puerto EXACTO de CMove::GCTeleportSend (Move.cpp:279-292)
    /// y su struct real (Move.h:36-44 del árbol fuente correcto, "Emulator 0.99 (2.1.7)/GameServer" --
    /// ver el doc-comment de <see cref="World.Item"/> para la explicación de por qué este repo tiene
    /// dos árboles de C++ y cuál es el real). REVERTIDO: una pasada anterior, investigando contra el
    /// árbol equivocado, había cambiado <c>gate</c> de BYTE a WORD -- el real es BYTE (clamp a 0/1 en
    /// GCTeleportSend: <c>pMsg.gate = ((gate&gt;0)?1:gate);</c>). Usado por gObjMoveGate para
    /// avisarle al cliente que cambió de mapa/posición fuera de un movimiento normal (entrada/salida
    /// de Devil Square, Fase 6). Se manda cifrado por bloques (ver ClientSession.SendEncryptedAsync)
    /// -- acá se arma como paquete "lógico" C1 sin sub-código, igual que el resto de los C3 de este
    /// puerto (el tipo real 0xC3 lo pone SendEncryptedAsync).</summary>
    public static byte[] TeleportSend(byte gate, byte map, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(gate);
        w.WriteByte(map);
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0x1C, w.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_SEND (aparecer jugadores), C2:12. Uno o más PMSG_VIEWPORT_PLAYER
    /// concatenados atrás del header. REVERTIDO: una pasada anterior de porting investigó este
    /// paquete contra el árbol de fuente EQUIVOCADO (ver doc-comment de <see cref="World.Item"/>) y
    /// "corrigió" el struct a una versión de temporada muy posterior (CharSet[18] +
    /// attribute/MuunItem/level/MaxHP/CurHP inventados, count movido al final) que no existe en este
    /// build. El struct real (Viewport.h:45-54 del árbol correcto, confirmado también contra el
    /// builder real <c>CViewport::GCViewportPlayerSend</c>, Viewport.cpp:601-676) es:
    /// <c>index[2]+x+y+CharSet[13]+count(WORD, lista de efectos)+name[10]+tx+ty+DirAndPkLevel</c> --
    /// el campo <c>count</c> va ANTES de <c>name</c>, no al final, y es la cuenta de una lista de
    /// efectos/buffs visuales que se apendan después del struct fijo (no portada, count=0 siempre).
    /// El ViewState real ocupa los 4 bits bajos de CharSet[0] (máscara <c>0x0F</c>/<c>&amp;15</c>,
    /// Viewport.cpp:657-658: <c>CharSet[0] &amp;= 0xF0; CharSet[0] |= ViewState &amp; 15;</c>) --
    /// REVERTIDO de una máscara de 3 bits (0xF8) que una pasada anterior había introducido "corrigiendo"
    /// algo que ya estaba bien.</summary>
    public static byte[] ViewportPlayerAppear(IReadOnlyList<PlayerObject> players)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)players.Count);

        foreach (var target in players)
        {
            byte indexHi = (byte)((target.Index >> 8) & 0xFF);
            // "Recién apareció" (spawn flag) -- en esta fase no distinguimos aparición real de
            // reaparición por rango, así que se deja siempre en 0 (no imprescindible para que el
            // cliente lo renderice bien).
            body.WriteByte(indexHi);
            body.WriteByte((byte)(target.Index & 0xFF));
            body.WriteByte(target.X);
            body.WriteByte(target.Y);

            // ViewState (GM oculto/otros estados de visibilidad, lpObj->ViewState) no está trackeado
            // en este puerto todavía -- se manda siempre 0 (visible normal), igual que un jugador sin
            // ningún estado especial en el original.
            target.CharSet[0] &= 0xF0;
            body.Write(target.CharSet, 0, 13); // CharSet[13] real (ver PlayerObject.CharSet)

            // PMSG_VIEWPORT_PLAYER no tiene #pragma pack(1) (Viewport.h:45-56), así que MSVC alinea
            // el WORD count a offset par: tras CharSet[13] vamos en el offset 17, impar, y el
            // compilador mete 1 byte de relleno. El original manda el struct COMPLETO
            // (memcpy(&send[size],&info,sizeof(info)), Viewport.cpp:672, con InfoSize inicializado a
            // sizeof(info)), así que ese byte viaja en el wire. Sin él, count/name/tx/ty/
            // DirAndPkLevel le llegan corridos 1 byte al cliente real.
            body.WriteByte(0); // relleno de alineación (offset 17)

            body.WriteByte(0); // count (WORD, lista de efectos) low -- sin efectos visuales portados
            body.WriteByte(0); // high

            body.Write(PacketBuilder.FixedString(target.Name, 10));

            body.WriteByte(target.TX);
            body.WriteByte(target.TY);
            body.WriteByte((byte)((target.Dir * 16) | (target.PKLevel & 0x0F)));

            // Relleno de cola: el struct alinea a 2 (por el WORD count) y su último campo termina en
            // el offset 32, así que sizeof() = 34, no 33. Sin este byte, cada jugador siguiente del
            // viewport arranca 2 bytes antes de donde el cliente lo espera -- error acumulativo que
            // desalinea todo el resto de la lista.
            body.WriteByte(0);
        }

        return BuildC2NoSub(0x12, body.ToArray());
    }

    /// <summary>Puerto del patrón de empaquetado "SET_NUMBERHB(SET_NUMBERHW(x))" usado por este
    /// build para BYTE MaxHP[4]/CurHP[4] (Viewport.cpp:1698-1706) -- NO es big-endian estándar: el
    /// orden de bytes resultante es [bits24-31, bits8-15, bits16-23, bits0-7] (byte más
    /// significativo, luego el byte bajo de la mitad baja, luego el byte bajo de la mitad alta,
    /// luego el byte menos significativo). Mismo patrón usado en varias otras versiones de MU para
    /// HP/Money -- confirmado leyendo la macro aplicada byte a byte, no una suposición.</summary>
    private static void WritePackedUInt32Swapped(Stream s, uint v)
    {
        ushort hw = (ushort)((v >> 16) & 0xFFFF);
        ushort lw = (ushort)(v & 0xFFFF);
        s.WriteByte((byte)(hw >> 8));
        s.WriteByte((byte)(lw >> 8));
        s.WriteByte((byte)(hw & 0xFF));
        s.WriteByte((byte)(lw & 0xFF));
    }

    /// <summary>PMSG_VIEWPORT_SEND no lleva sub-código (es PWMSG_HEAD, no PSWMSG_HEAD) -- se arma a
    /// mano en vez de usar BuildC2Sub (que agrega un byte de sub-código de más).</summary>
    private static byte[] BuildC2NoSub(byte head, byte[] payload)
    {
        var buff = new byte[4 + payload.Length];
        int size = buff.Length;
        buff[0] = 0xC2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        payload.CopyTo(buff, 4);
        return buff;
    }

    /// <summary>PMSG_VIEWPORT_DESTROY_SEND (desaparecer jugadores O monstruos -- mismo paquete para
    /// ambos, ver Viewport.cpp:496-546), C1:14.</summary>
    public static byte[] ViewportDestroy(IReadOnlyList<int> indexes)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)indexes.Count);

        foreach (var idx in indexes)
        {
            body.WriteByte((byte)((idx >> 8) & 0xFF));
            body.WriteByte((byte)(idx & 0xFF));
        }

        return PacketBuilder.BuildC1(0x14, body.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_SEND variante monstruo (GCViewportMonsterSend/GCViewportSimpleMonsterSend,
    /// Viewport.cpp:690-773,1263-1321), C2:13. <paramref name="justSpawned"/> prende el bit de
    /// "recién apareció" (animación de spawn) -- se usa al revivir un monstruo tras su respawn, no
    /// cuando un jugador simplemente entra al rango de vista de uno que ya estaba parado ahí.</summary>
    public static byte[] ViewportMonsterAppear(IReadOnlyList<Monster> monsters, bool justSpawned = false)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)monsters.Count);

        foreach (var m in monsters)
        {
            byte indexHi = (byte)((m.Index >> 8) & 0xFF);

            if (justSpawned)
            {
                indexHi |= 0x80;
            }

            body.WriteByte(indexHi);
            body.WriteByte((byte)(m.Index & 0xFF));
            body.WriteByte((byte)((m.MonsterClass >> 8) & 0xFF));
            body.WriteByte((byte)(m.MonsterClass & 0xFF));
            body.WriteByte(0); // count (WORD, lista de efectos) low -- sin efectos activos todavía
            body.WriteByte(0); // high
            body.WriteByte(m.X);
            body.WriteByte(m.Y);
            body.WriteByte(m.TX);
            body.WriteByte(m.TY);
            body.WriteByte((byte)(m.Dir * 16)); // PKLevel no aplica a monstruos, nibble bajo en 0
            // Relleno de cola: el struct alinea a 2 (por el WORD count) y su último campo termina en
            // el offset 10, así que sizeof(PMSG_VIEWPORT_MONSTER) = 12 en el wire (MSVC pack(8)), no 11.
            body.WriteByte(0);
        }

        return BuildC2NoSub(0x13, body.ToArray());
    }

    /// <summary>PMSG_MOVE_SEND, C1:D7 -- notifica el movimiento de un objeto (monstruo/jugador) en el mapa.</summary>
    public static byte[] ViewportMonsterMove(int monsterIndex, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((monsterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(monsterIndex & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte((byte)(dir << 4));
        return PacketBuilder.BuildC1(0xD7, w.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_ITEM dentro del batch PMSG_VIEWPORT_SEND (GCViewportItemSend,
    /// Viewport.cpp:~905-909), C2:20. Bit 0x80 del byte alto del índice = "recién apareció"
    /// (<see cref="GroundItem.JustDropped"/>, anima la caída/aparición en el cliente). El dinero
    /// (<see cref="GroundItem.MoneyAmount"/> no nulo) usa un empaquetado de ItemInfo distinto al de
    /// un item normal -- puerto byte a byte verificado contra Viewport.cpp: byte0=index&0xFF,
    /// byte1=SET_NUMBERLB(SET_NUMBERHW(money))=bits16-23, byte2=SET_NUMBERHB(SET_NUMBERLW(money))=
    /// bits8-15, byte3=(index&256)>>1, byte4=SET_NUMBERLB(SET_NUMBERLW(money))=bits0-7. Los bits
    /// 24-31 del monto NO se envían nunca para un item de dinero en el suelo en este build -- es una
    /// limitación real del C++ original, replicada tal cual (no "arreglada").</summary>
    public static byte[] ViewportItemAppear(IReadOnlyList<GroundItem> items)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)items.Count);

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        foreach (var g in items)
        {
            byte indexHi = (byte)((g.Index >> 8) & 0xFF);

            if (g.JustDropped)
            {
                indexHi |= 0x80;
            }

            body.WriteByte(indexHi);
            body.WriteByte((byte)(g.Index & 0xFF));
            body.WriteByte(g.X);
            body.WriteByte(g.Y);

            if (g.MoneyAmount is uint money)
            {
                info.Clear();
                info[0] = (byte)(g.Item.Index & 0xFF);
                info[1] = (byte)((money >> 16) & 0xFF); // SET_NUMBERLB(SET_NUMBERHW(money))
                info[2] = (byte)((money >> 8) & 0xFF); // SET_NUMBERHB(SET_NUMBERLW(money))
                info[3] = (byte)((g.Item.Index & 256) >> 1);
                info[4] = (byte)(money & 0xFF); // SET_NUMBERLB(SET_NUMBERLW(money))
            }
            else
            {
                g.Item.ToWireBytes(info);
            }

            body.Write(info);
        }

        return BuildC2NoSub(0x20, body.ToArray());
    }

    /// <summary>PMSG_VIEWPORT_DESTROY_ITEM_SEND (GCViewportDestroyItemSend, Viewport.cpp:579-630),
    /// C2:21 -- paquete DISTINTO del que usan jugadores/monstruos (C1:14, <see cref="ViewportDestroy"/>);
    /// los items de piso tienen su propio head. 2 bytes por item (solo el índice, sin bit de flag).</summary>
    public static byte[] ViewportItemDestroy(IReadOnlyList<int> indexes)
    {
        using var body = new MemoryStream();
        body.WriteByte((byte)indexes.Count);

        foreach (var idx in indexes)
        {
            body.WriteByte((byte)((idx >> 8) & 0xFF));
            body.WriteByte((byte)(idx & 0xFF));
        }

        return BuildC2NoSub(0x21, body.ToArray());
    }
}

// ---------------------------------------------------------------- Fase 4: combate (primera pasada)

/// <summary>PMSG_ATTACK_RECV (Attack.h:13-19), C1:D9 -- pedido de ataque cuerpo a cuerpo del
/// cliente (sin skill, ver CAttack::CGAttackRecv).</summary>
public sealed record AttackRecv(int TargetIndex, byte Action, byte Dir)
{
    public static AttackRecv Parse(byte[] p)
    {
        int index = (p[3] << 8) | p[4];
        return new AttackRecv(index, p[5], p[6]);
    }
}

/// <summary>PMSG_ACTION_RECV (Protocol.h:155-161), C1:18 -- pose/emote/sentarse (dir+action) más un
/// índice de "objetivo" opcional que el original ni siquiera valida (CGActionRecv, Protocol.cpp:611-
/// 666, lo reenvía tal cual al construir PMSG_ACTION_SEND).</summary>
public sealed record ActionRecv(byte Dir, byte Action, int TargetIndex)
{
    /// <summary>Regresión de producción (mismo patrón que el path truncado de MoveRecv): el cliente
    /// real a veces manda este paquete SIN el campo final "index[2]" (que el original ni siquiera
    /// usa/valida, ver doc-comment de arriba) -- el struct C++ declara un tamaño "techo" de 7 bytes,
    /// pero lo efectivamente enviado puede ser más corto. Se lee defensivamente byte a byte en vez de
    /// asumir el largo completo, igual que MoveRecv.Parse.</summary>
    public static ActionRecv Parse(byte[] p)
    {
        byte dir = p.Length > 3 ? p[3] : (byte)0;
        byte action = p.Length > 4 ? p[4] : (byte)0;
        int index = p.Length > 6 ? ((p[5] << 8) | p[6]) : 0xFFFF;
        return new ActionRecv(dir, action, index);
    }
}

/// <summary>PMSG_LEVEL_UP_POINT_RECV (Protocol.h:258-262), C1:F3:06 -- pedir agregar 1 punto de
/// level-up a un stat. type: 0=Strength,1=Dexterity,2=Vitality,3=Energy,4=Leadership.</summary>
public sealed record LevelUpPointRecv(byte Type)
{
    public static LevelUpPointRecv Parse(byte[] p) => new(p[4]);
}

public static class CombatPacketBuilder
{
    /// <summary>PMSG_ACTION_SEND (Protocol.h:355-362), C1:18 -- animación de ataque, se manda a
    /// todos los que ven al atacante (acá: solo al propio atacante, ver nota en
    /// ClientProtocolHandler.OnAttackAsync sobre el broadcast simplificado de esta primera pasada).</summary>
    public static byte[] ActionSend(int attackerIndex, byte dir, byte action, int targetIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((attackerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(attackerIndex & 0xFF));
        w.WriteByte(dir);
        w.WriteByte(action);
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x18, w.ToArray());
    }

    /// <summary>PMSG_DAMAGE_SEND (Protocol.h:327-337), C1:D9 -- número de daño mostrado sobre el
    /// objetivo. <paramref name="targetCurrentLife"/>/<paramref name="damage"/> alimentan los campos
    /// GAMESERVER_EXTRA (ViewCurHP/ViewDamageHP), presentes en este build.</summary>
    public static byte[] DamageSend(int targetIndex, int damage, byte type, bool missFlag, float targetCurrentLife)
    {
        var w = new PacketWriter();
        int clampedDamage = Math.Min(damage, 65000);
        byte hi = (byte)((targetIndex >> 8) & 0xFF);

        if (missFlag)
        {
            hi |= 0x80;
        }

        w.WriteByte(hi);
        w.WriteByte((byte)(targetIndex & 0xFF));
        w.WriteByte((byte)((clampedDamage >> 8) & 0xFF));
        w.WriteByte((byte)(clampedDamage & 0xFF));
        w.WriteByte(type);
        // CORREGIDO (bug introducido en una pasada anterior de esta misma sesión): el comentario decía
        // "header de 4 bytes -> offset 9 -> 3 bytes de relleno", pero PMSG_DAMAGE_SEND usa PBMSG_HEAD
        // (Protocol.h:22-41: type+size+head, BYTE los 3, sin campos que fuercen alineación > 1), que
        // mide 3 bytes, NO 4 (el de 4 bytes es PSBMSG_HEAD, con un subh extra, usado por los paquetes
        // C1:F3:xx como CharacterInfoSend/LevelUpSend -- PMSG_DAMAGE_SEND es C1:D9 directo, sin
        // sub-código). Offset real tras index[2]+damage[2]+type = 3+2+2+1 = 8, que YA es múltiplo de 4
        // -- CERO bytes de relleno hacen falta acá. Los 3 bytes que este puerto agregaba de más
        // corrían ViewCurHP/ViewDamageHP 3 bytes, y como el paquete total también quedaba 3 bytes más
        // largo de lo que el cliente real espera, TODO lo que el cliente leía de ahí en más (el número
        // de daño mostrado, y potencialmente el framing de paquetes subsiguientes en el mismo bloque
        // cifrado) salía basura -- coincide exactamente con los "números de daño ilógicos tipo
        // 9998989898" reportados jugando con el cliente real. PMSG_MANA_SEND (ver ManaPacketBuilder)
        // tenía el mismo error por el mismo motivo, también corregido.
        w.WriteUInt32((uint)targetCurrentLife);
        w.WriteUInt32((uint)damage);
        return PacketBuilder.BuildC1(0xD9, w.ToArray());
    }

    /// <summary>PMSG_USER_DIE_SEND (Protocol.h:347-353), C1:17 -- muerte (de jugador O monstruo,
    /// mismo paquete, ver GCUserDieSend).</summary>
    public static byte[] UserDieSend(int deadIndex, byte skill, int killerIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((deadIndex >> 8) & 0xFF));
        w.WriteByte((byte)(deadIndex & 0xFF));
        w.WriteByte(skill);
        w.WriteByte((byte)((killerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(killerIndex & 0xFF));
        return PacketBuilder.BuildC1(0x17, w.ToArray());
    }

    /// <summary>PMSG_SKILL_ATTACK_SEND (SkillManager.h:144-150), C3:19 -- ataque con magia/skill
    /// de monstruos a distancia (Lich, Gorgon, etc.) o jugadores.</summary>
    public static byte[] SkillAttackSend(int attackerIndex, byte skill, int targetIndex, bool hit)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((attackerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(attackerIndex & 0xFF));

        byte targetHi = (byte)((targetIndex >> 8) & 0xFF);
        if (hit)
        {
            targetHi |= 0x80;
        }

        w.WriteByte(targetHi);
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x19, w.ToArray());
    }

    /// <summary>PMSG_REWARD_EXPERIENCE_SEND (Protocol.h:449-460), C1:9C -- popup de experiencia
    /// ganada, unicast al jugador que se la llevó (ver GCMonsterDieSend).
    /// CORREGIDO: al struct real (PBMSG_HEAD=3 bytes + index[2] + WORD experience[2] (4 bytes, NO 2 --
    /// es un array de 2 WORDs) + damage[2]) le faltaba el relleno de alineación antes de los 3 DWORD
    /// "View*" de GAMESERVER_EXTRA: offset tras esos campos = 3+2+4+2 = 11, no múltiplo de 4 -- hace
    /// falta 1 byte de relleno para llegar a 12. Sin él, ViewDamageHP/ViewExperience/
    /// ViewNextExperience (y el popup de experiencia que arma el cliente con esos valores) llegaban
    /// desplazados 1 byte -- explica el "no dan experiencia" reportado (el cliente probablemente
    /// descarta o ignora el popup si los DWORD no calzan con lo esperado).</summary>
    public static byte[] MonsterDieSend(int monsterIndex, uint experience, int damage, uint currentExperience, uint nextExperience)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((monsterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(monsterIndex & 0xFF));
        w.WriteUInt16((ushort)((experience >> 16) & 0xFFFF)); // SET_NUMBERHW
        w.WriteUInt16((ushort)(experience & 0xFFFF));          // SET_NUMBERLW
        int clampedDamage = Math.Min(damage, 65000);
        w.WriteByte((byte)((clampedDamage >> 8) & 0xFF));
        w.WriteByte((byte)(clampedDamage & 0xFF));
        w.WriteByte(0); // relleno de alineación (offset 11 -> 12)
        w.WriteUInt32((uint)damage);
        w.WriteUInt32(currentExperience);
        w.WriteUInt32(nextExperience);
        return PacketBuilder.BuildC1(0x9C, w.ToArray());
    }

    /// <summary>PMSG_LEVEL_UP_SEND (Protocol.h:653-673), C1:F3:05.</summary>
    public static byte[] LevelUpSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteUInt16(p.Level);
        w.WriteUInt16((ushort)Math.Min(p.LevelUpPoint, ushort.MaxValue));
        w.WriteUInt16((ushort)Math.Min(p.MaxLife, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        w.WriteUInt16(p.FruitAddPoint);
        w.WriteUInt16(0); // MaxFruitAddPoint -- balance de fruits no portado
        w.WriteUInt16(p.FruitSubPoint);
        w.WriteUInt16(0); // MaxFruitSubPoint
        // Mismo problema de alineación que CharacterInfoSend/NewCharacterInfoSend (PMSG_LEVEL_UP_SEND,
        // Protocol.h:653-673, sin pack(1)): 9 WORD = 18 bytes tras el header de 4, offset 22 no es
        // múltiplo de 4 -- 2 bytes de relleno antes del primer DWORD "View*".
        w.WriteUInt16(0); // relleno de alineación
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Experience);
        w.WriteUInt32(WorldPacketBuilder.NextExperience(p.Level));
        return PacketBuilder.BuildC1Sub(0xF3, 0x05, w.ToArray());
    }

    /// <summary>PMSG_LEVEL_UP_POINT_SEND (Protocol.h:675-692), C1:F3:06 -- respuesta a
    /// LevelUpPointRecv. <paramref name="ok"/>=false manda result=0 (fallo, sin el resto de los
    /// campos poblados -- igual que el original, que solo llena result/MaxLifeAndMana/MaxBP/View*
    /// dentro del if de éxito). result de éxito = 16+type (16=Str,17=Dex,18=Vit,19=Ene,
    /// 20=Leadership, ver CGLevelUpPointRecv).</summary>
    public static byte[] LevelUpPointSend(PlayerObject p, byte type, bool ok)
    {
        var w = new PacketWriter();
        w.WriteByte(ok ? (byte)(16 + type) : (byte)0);
        // result(1) tras el header(4) -> offset 5, no múltiplo de 2 -- 1 byte de relleno antes del
        // primer WORD (MaxLifeAndMana).
        w.WriteByte(0); // relleno de alineación
        uint maxLifeAndMana = type switch
        {
            2 => p.MaxLife, // Vitality sube MaxLife
            3 => p.MaxMana, // Energy sube MaxMana
            _ => 0,
        };
        w.WriteUInt16((ushort)Math.Min(maxLifeAndMana, (uint)65000));
        w.WriteUInt16((ushort)Math.Min(p.MaxBP, (uint)65000));
        // MaxLifeAndMana(2)+MaxBP(2) = 4 bytes tras offset 6 -> offset 10, no múltiplo de 4 -- 2 bytes
        // de relleno antes del primer DWORD "View*".
        w.WriteUInt16(0); // relleno de alineación
        w.WriteUInt32(p.LevelUpPoint);
        w.WriteUInt32(p.MaxLife);
        w.WriteUInt32(p.MaxMana);
        w.WriteUInt32(p.MaxBP);
        w.WriteUInt32(p.Strength);
        w.WriteUInt32(p.Dexterity);
        w.WriteUInt32(p.Vitality);
        w.WriteUInt32(p.Energy);
        w.WriteUInt32(p.Leadership);
        return PacketBuilder.BuildC1Sub(0xF3, 0x06, w.ToArray());
    }
}

// ---------------------------------------------------------------- Fase 3: items e inventario
// Puerto de ItemManager.h/.cpp -- solo el subconjunto de paquetes de "mover/equipar dentro del
// inventario propio" (Trade/Warehouse/Shop/PersonalShop y recoger/tirar del suelo quedan para una
// pasada posterior de la Fase 3, documentado en el README).

/// <summary>PMSG_ITEM_MOVE_RECV, C1:24 (ItemManager.h:63-71) -- header(3) + SourceFlag(1) +
/// SourceSlot(1) + ItemInfo[5] (offsets 5-9, echo del cliente, IGNORADO -- el servidor es la
/// autoridad sobre qué item hay realmente en SourceSlot, ItemManager.cpp:2853-3025) + TargetFlag(1,
/// offset 10) + TargetSlot(1, offset 11). Paquete completo de 12 bytes. SourceFlag/TargetFlag:
/// 0=Inventory (único container soportado por ahora).</summary>
public sealed record ItemMoveRecv(byte SourceFlag, byte SourceSlot, byte TargetFlag, byte TargetSlot)
{
    public static ItemMoveRecv Parse(byte[] p) => new(p[3], p[4], p[10], p[11]);
}

// ---------------------------------------------------------------- Fase 3 (segunda pasada): recoger/tirar del suelo
// Puerto de CGItemGetRecv/CGItemDropRecv (ItemManager.cpp:3289-3718) -- items tirados en el piso por
// muerte de monstruo o por el propio jugador (PMSG_ITEM_DROP_RECV), y recogidos de vuelta
// (PMSG_ITEM_GET_RECV). Ver World/GroundItem.cs para el modelo de datos y el resto del doc-comment
// de alcance/simplificaciones (sin ItemBag/eventos/Muun/quest-items, sin el hop a DataServer para
// asignar Serial -- se genera localmente).

/// <summary>PMSG_ITEM_GET_RECV, C1:22 -- <paramref name="GroundIndex"/> es el slot dentro del array
/// de 300 items del MAPA del jugador (no un índice global), igual que <see cref="GroundItem.Index"/>.</summary>
public sealed record ItemGetRecv(int GroundIndex)
{
    public static ItemGetRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_ITEM_DROP_RECV, C1:23.</summary>
public sealed record ItemDropRecv(byte X, byte Y, byte Slot)
{
    public static ItemDropRecv Parse(byte[] p) => new(p[3], p[4], p[5]);
}

public static class ItemPacketBuilder
{
    /// <summary>PMSG_ITEM_LIST_SEND, C4:F3:10 (tamaño de 2 bytes, cifrado por bloques -- ver
    /// ClientSession.SendEncryptedC4Async). Entrada armada como paquete "lógico" C2Sub de cabecera
    /// de 2 bytes; el tipo real C4 lo pone SendEncryptedC4Async. count+repetido{slot,ItemInfo[5]}
    /// -- 6 bytes por slot ocupado (ItemManager.h:187-198, PMSG_ITEM_LIST).</summary>
    public static byte[] ItemListSend(PlayerObject p)
    {
        using var body = new MemoryStream();
        var countPos = body.Position;
        body.WriteByte(0); // se corrige más abajo
        int count = 0;

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        for (int slot = 0; slot < Item.InventorySize; slot++)
        {
            var item = p.Items[slot];

            if (!item.IsItem())
            {
                continue;
            }

            item.ToWireBytes(info);
            body.WriteByte((byte)slot);
            body.Write(info);
            count++;
        }

        var bytes = body.ToArray();
        bytes[countPos] = (byte)count;

        return PacketBuilder.BuildC2Sub(0xF3, 0x10, bytes);
    }

    /// <summary>PMSG_ITEM_MOVE_SEND, C3:24 (ItemManager.h:121-127) -- CORREGIDO: <paramref name="result"/>
    /// NO es un booleano genérico -- puerto exacto de <c>MoveItemToInventoryFromInventory</c>
    /// (ItemManager.cpp:1920-1978), que devuelve <c>TargetFlag</c> (0 para Inventory, el único
    /// container soportado acá) en éxito y <c>0xFF</c> en cualquier falla (ver doc-comment de
    /// ClientProtocolHandler.OnItemMoveAsync para el detalle completo, incluyendo el bug de "swap"
    /// que esto arregla). slot = destino real donde quedó el item, + ItemInfo[5]. Se manda cifrado
    /// por bloques vía SendEncryptedAsync (de ahí el C3), armado acá como paquete "lógico" C1 sin
    /// sub-código (head=0x24 directo, igual que MoveSend/0xD7).</summary>
    public static byte[] ItemMoveSend(byte result, byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x24, w.ToArray());
    }

    /// <summary>PMSG_ITEM_CHANGE_SEND, C1:25 (ItemManager.h:129-134) -- notifica a otros/al propio
    /// cliente que el item de un slot cambió (usado acá tras un move/equip exitoso). Byte1 de
    /// ItemInfo se pisa con slot*16 | ((level-1)/2)&0xF, puerto exacto de ItemManager.cpp:3445-3462.
    /// Header + index[2] + ItemInfo[5], SIN byte "attribute" final -- ese campo no existe en este
    /// build (era de una rama GAMESERVER_UPDATE de una temporada posterior que no aplica acá).</summary>
    public static byte[] ItemChangeSend(int index, byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));

        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        info[1] = (byte)((slot * 16) | (((item.Level - 1) / 2) & 0x0F));

        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x25, w.ToArray());
    }

    /// <summary>PMSG_ITEM_EQUIPMENT_SEND, C1:F3:13 -- CharSet[13] actualizado del propio jugador (ver
    /// PlayerObject.CharSet) (se manda al propio cliente tras equipar/desequipar; el resto de
    /// jugadores ven el cambio vía un nuevo ViewportPlayerAppear, ver
    /// ClientProtocolHandler.OnItemMoveAsync).</summary>
    public static byte[] ItemEquipmentSend(PlayerObject p)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((p.Index >> 8) & 0xFF));
        w.WriteByte((byte)(p.Index & 0xFF));
        w.WriteBytes(p.CharSet, 13);
        return PacketBuilder.BuildC1Sub(0xF3, 0x13, w.ToArray());
    }

    /// <summary>PMSG_ITEM_REPAIR_SEND, C1:34 (ItemManager.h:175-180) -- puerto de CGItemRepairRecv.</summary>
    public static byte[] ItemRepairSend(uint money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD (3 bytes) + DWORD money alineado a 4 -> MSVC mete 1 byte de relleno en el
        // offset 3 (sizeof = 8, no 7). El original manda sizeof(pMsg), así que sin esto el cliente
        // lee money desde el offset 4 y recibe un valor corrido un byte.
        w.WriteByte(0); // relleno de alineación (offset 3 -> 4)
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x34, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DUR_SEND, C1:2A (ItemManager.h:182-187) -- puerto de GCItemDurSend.</summary>
    public static byte[] ItemDurSend(byte slot, byte dur, byte flag)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        w.WriteByte(dur);
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x2A, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DELETE_SEND, C1:28 (ItemManager.h:3464-3475) -- notifica la eliminación de un ítem consumido o usado.</summary>
    public static byte[] ItemDeleteSend(byte slot, byte flag = 1)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x28, w.ToArray());
    }

    /// <summary>PMSG_ITEM_MODIFY_SEND, C1:F3:14 (ItemManager.h:3564-3582) -- notifica que un ítem en un slot fue modificado (ej. con joya).</summary>
    public static byte[] ItemModifySend(byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1Sub(0xF3, 0x14, w.ToArray());
    }

    /// <summary>PMSG_ITEM_GET_SEND reusado como "cambió el dinero", C3:22 (ItemManager.h:105-112,
    /// result=0xFE es el marcador especial que usa la rama de dinero de CGItemGetRecv en vez de un
    /// item real, ItemManager.cpp:2605-2669) -- los 4 bytes de money van empaquetados BIG-ENDIAN
    /// dentro de ItemInfo[0..3] (SET_NUMBERHB/LB de cada mitad de 16 bits), no little-endian como el
    /// resto del protocolo -- confirmado contra las macros SET_NUMBER* del original, no es un error
    /// de puerto. ItemInfo[4] queda sin uso (relleno 0). Se manda cifrado por bloques (ver
    /// ClientSession.SendEncryptedAsync). REVERTIDO: una nota anterior de este puerto afirmaba que
    /// <c>GAMESERVER_EXTRA</c> "nunca se define como 1" en este árbol -- FALSO, `stdafx.h:9-10` del
    /// árbol fuente correcto ("Emulator 0.99 (2.1.7)/GameServer" -- ver el doc-comment de
    /// <see cref="World.Item"/>) tiene <c>#ifndef GAMESERVER_EXTRA #define GAMESERVER_EXTRA 1
    /// #endif</c> incondicional, así que SÍ está activo, y el struct real
    /// (<c>ItemManager.h:105-112</c>) SÍ tiene el DWORD final <c>ViewIndex</c>. La rama de dinero de
    /// <c>CGItemGetRecv</c> no lo setea explícitamente (queda en lo que tenga el stack, replicado acá
    /// como 0). CORREGIDO: faltaba el relleno de alineación antes del DWORD final -- PMSG_ITEM_GET_SEND
    /// usa PBMSG_HEAD (3 bytes, es C3:22 directo sin sub-código, ver doc-comment de
    /// CombatPacketBuilder.DamageSend para la explicación completa de PBMSG_HEAD vs PSBMSG_HEAD).
    /// Offset tras result(1)+ItemInfo[5] = 3+1+5 = 9, no múltiplo de 4 -- hacen falta 3 bytes de
    /// relleno para llegar a 12 antes de ViewIndex.</summary>
    public static byte[] MoneySend(uint money)
    {
        var w = new PacketWriter();
        w.WriteByte(0xFE);

        Span<byte> info = stackalloc byte[Item.WireByteSize];
        info.Clear();
        info[0] = (byte)((money >> 24) & 0xFF);
        info[1] = (byte)((money >> 16) & 0xFF);
        info[2] = (byte)((money >> 8) & 0xFF);
        info[3] = (byte)(money & 0xFF);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        w.WriteBytes(new byte[3], 3); // relleno de alineación (offset 9 -> 12)
        w.WriteUInt32(0); // ViewIndex (GAMESERVER_EXTRA==1) -- rama de dinero no lo setea en el original

        return PacketBuilder.BuildC1(0x22, w.ToArray());
    }

    /// <summary>PMSG_ITEM_GET_SEND, C3:22 (ItemManager.h:105-112) -- misma estructura que
    /// <see cref="MoneySend"/> (mismo head, el original literalmente reusa el struct), pero para el
    /// caso "recogí un item real del piso" (CGItemGetRecv, ItemManager.cpp:3289-3528).
    /// <paramref name="result"/>: 0xFF=falló (denegado por cualquiera de las validaciones -- fuera de
    /// rango, bloqueado, sin espacio), 0-234ish=slot del inventario donde quedó (éxito). El caso
    /// 0xFD ("se apiló con un item existente") del original NO está portado -- este puerto no tiene
    /// lógica de apilado de items (flechas/pociones no se acumulan en un slot), así que ese código de
    /// resultado nunca sale de acá. <paramref name="groundIndex"/> SÍ se envía como el DWORD final
    /// <c>ViewIndex</c> (<c>pMsg.ViewIndex = index;</c> en el original, GAMESERVER_EXTRA==1 -- ver el
    /// doc-comment de <see cref="MoneySend"/> para la corrección de por qué este campo está activo).</summary>
    public static byte[] ItemGetSend(byte result, Item? item, int groundIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(result);

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        if (result != 0xFF && item != null)
        {
            item.ToWireBytes(info);
        }
        else
        {
            info.Clear();
        }

        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        w.WriteBytes(new byte[3], 3); // relleno de alineación (offset 9 -> 12, ver doc-comment de MoneySend)
        w.WriteUInt32((uint)groundIndex); // ViewIndex (GAMESERVER_EXTRA==1)
        return PacketBuilder.BuildC1(0x22, w.ToArray());
    }

    /// <summary>PMSG_ITEM_DROP_SEND, C1:23 -- result: 0=falló (cualquiera de las validaciones de
    /// CGItemDropRecv, ItemManager.cpp:3530-3718; este puerto solo implementa el subconjunto genérico
    /// documentado en GroundItem.cs -- sin lucky/periodic/set/harmony/excelente-nivel-alto ni los
    /// ítems especiales con efecto propio), 1=éxito.</summary>
    public static byte[] ItemDropSend(byte result, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte(slot);
        return PacketBuilder.BuildC1(0x23, w.ToArray());
    }
}

public sealed record ItemRepairRecv(byte Slot, byte Type)
{
    public static ItemRepairRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0);
}

public sealed record ItemUseRecv(byte SourceSlot, byte TargetSlot, byte Type)
{
    public static ItemUseRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0,
        p.Length > 5 ? p[5] : (byte)0);
}

public static class TradePacketBuilder
{
    /// <summary>PMSG_TRADE_REQUEST_SEND, C3:36 -- envía solicitud de Trade al objetivo.</summary>
    public static byte[] TradeRequestSend(string name)
    {
        var w = new PacketWriter();
        Span<byte> nameBytes = stackalloc byte[10];
        nameBytes.Clear();
        Encoding.ASCII.GetBytes(name, nameBytes);
        w.WriteBytes(nameBytes.ToArray(), 10);
        return PacketBuilder.BuildC1(0x36, w.ToArray());
    }

    /// <summary>PMSG_TRADE_RESPONSE_SEND, C1:37 -- responde resultado de solicitud Trade.</summary>
    public static byte[] TradeResponseSend(byte response, string name, ushort level = 0, uint guildNumber = 0)
    {
        var w = new PacketWriter();
        w.WriteByte(response);
        Span<byte> nameBytes = stackalloc byte[10];
        nameBytes.Clear();
        Encoding.ASCII.GetBytes(name, nameBytes);
        w.WriteBytes(nameBytes.ToArray(), 10);
        w.WriteUInt16(level);
        w.WriteUInt32(guildNumber);
        return PacketBuilder.BuildC1(0x37, w.ToArray());
    }

    /// <summary>PMSG_TRADE_ITEM_DEL_SEND, C1:38 -- notifica que se quitó un ítem del Trade.</summary>
    public static byte[] TradeItemDelSend(byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        return PacketBuilder.BuildC1(0x38, w.ToArray());
    }

    /// <summary>PMSG_TRADE_ITEM_ADD_SEND, C1:39 -- notifica que se añadió un ítem al Trade.</summary>
    public static byte[] TradeItemAddSend(byte slot, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(slot);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x39, w.ToArray());
    }

    /// <summary>PMSG_TRADE_MONEY_SEND, C1:3B -- notifica cambio de Zen en Trade.</summary>
    public static byte[] TradeMoneySend(uint money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD (3 bytes) + DWORD money alineado a 4 -> 1 byte de relleno en el offset 3
        // (sizeof = 8, no 7). Verificado contra CTrade::GCTradeMoneySend (Trade.cpp:550-559), que
        // hace header.set(0x3B,sizeof(pMsg)) y manda esos 8 bytes.
        w.WriteByte(0); // relleno de alineación (offset 3 -> 4)
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x3B, w.ToArray());
    }

    /// <summary>PMSG_TRADE_OK_BUTTON_SEND, C1:3C -- notifica estado de botón OK (0=normal, 1=OK, 2=Yellow/Uncheck).</summary>
    public static byte[] TradeOkButtonSend(byte flag)
    {
        var w = new PacketWriter();
        w.WriteByte(flag);
        return PacketBuilder.BuildC1(0x3C, w.ToArray());
    }

    /// <summary>PMSG_TRADE_RESULT_SEND, C1:3D -- notifica resultado final de Trade (0=cancel, 1=exito, 2=inv lleno, 5=zen max).</summary>
    public static byte[] TradeResultSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x3D, w.ToArray());
    }
}

public sealed record TradeRequestRecv(int TargetIndex)
{
    public static TradeRequestRecv Parse(byte[] p)
    {
        int index = p.Length > 4 ? ((p[3] << 8) | p[4]) : 0;
        return new TradeRequestRecv(index);
    }
}

public sealed record TradeResponseRecv(byte Response)
{
    public static TradeResponseRecv Parse(byte[] p)
    {
        byte response = p.Length > 3 ? p[3] : (byte)0;
        return new TradeResponseRecv(response);
    }
}

public sealed record TradeMoneyRecv(uint Money)
{
    public static TradeMoneyRecv Parse(byte[] p)
    {
        uint money = p.Length > 6 ? ((uint)p[3] << 24) | ((uint)p[4] << 16) | ((uint)p[5] << 8) | p[6] : 0;
        return new TradeMoneyRecv(money);
    }
}

public sealed record TradeOkRecv(byte Flag)
{
    public static TradeOkRecv Parse(byte[] p)
    {
        byte flag = p.Length > 3 ? p[3] : (byte)0;
        return new TradeOkRecv(flag);
    }
}

public static class WarehousePacketBuilder
{
    /// <summary>PMSG_WAREHOUSE_STATE_SEND, C1:83 -- notifica el estado del baúl (0=desbloqueado, 1=bloqueado/contraseña, 10=pw incorrecta, 12=pw correcta).</summary>
    public static byte[] WarehouseStateSend(byte state)
    {
        var w = new PacketWriter();
        w.WriteByte(state);
        return PacketBuilder.BuildC1(0x83, w.ToArray());
    }

    /// <summary>PMSG_WAREHOUSE_MONEY_SEND, C1:81 -- notifica el dinero del inventario y del baúl tras un depósito/retiro.</summary>
    public static byte[] WarehouseMoneySend(byte result, uint inventoryMoney, uint warehouseMoney)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteUInt32(warehouseMoney);
        w.WriteUInt32(inventoryMoney);
        return PacketBuilder.BuildC1(0x81, w.ToArray());
    }

    /// <summary>PMSG_SHOP_ITEM_LIST_SEND reusado para Warehouse, C2:31 -- lista de ítems en el baúl (Warehouse.cpp:250-291).</summary>
    public static byte[] WarehouseListSend(PlayerObject player)
    {
        var w = new PacketWriter();
        w.WriteByte(0);
        int count = 0;
        for (int slot = 0; slot < player.WarehouseItems.Length; slot++)
        {
            if (player.WarehouseItems[slot].IsItem()) count++;
        }
        w.WriteByte((byte)count);

        Span<byte> itemInfo = stackalloc byte[Item.WireByteSize];
        for (int slot = 0; slot < player.WarehouseItems.Length; slot++)
        {
            var item = player.WarehouseItems[slot];
            if (!item.IsItem()) continue;
            w.WriteByte((byte)slot);
            item.ToWireBytes(itemInfo);
            w.WriteBytes(itemInfo.ToArray(), Item.WireByteSize);
        }

        return PacketBuilder.BuildC2(0x31, w.ToArray());
    }
}

public sealed record WarehouseMoneyRecv(byte Type, uint Money)
{
    public static WarehouseMoneyRecv Parse(byte[] p)
    {
        byte type = p.Length > 3 ? p[3] : (byte)0;
        uint money = p.Length > 7 ? ((uint)p[4] << 24) | ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7] : 0;
        return new WarehouseMoneyRecv(type, money);
    }
}

public sealed record WarehousePasswordRecv(byte Type, ushort Password, string PersonalCode)
{
    public static WarehousePasswordRecv Parse(byte[] p)
    {
        byte type = p.Length > 3 ? p[3] : (byte)0;
        ushort pw = p.Length > 5 ? (ushort)((p[4] << 8) | p[5]) : (ushort)0;
        string code = p.Length >= 16 ? Encoding.ASCII.GetString(p, 6, 10).TrimEnd('\0') : string.Empty;
        return new WarehousePasswordRecv(type, pw, code);
    }
}

public sealed record TeleportRecv(byte Gate, byte X, byte Y, byte Dir)
{
    public static TeleportRecv Parse(byte[] p) => new(
        p.Length > 3 ? p[3] : (byte)0,
        p.Length > 4 ? p[4] : (byte)0,
        p.Length > 5 ? p[5] : (byte)0,
        p.Length > 6 ? p[6] : (byte)0);
}

public sealed record TeleportMoveRecv(int MoveIndex)
{
    public static TeleportMoveRecv Parse(byte[] p)
    {
        int number = p.Length >= 10 ? ((p[8] << 8) | p[9]) : (p.Length >= 5 ? p[4] : 0);
        return new TeleportMoveRecv(number);
    }
}

// ---------------------------------------------------------------- Fase 5: chat y whisper (primera pasada)
// Puerto de CGChatRecv/CGChatWhisperRecv (Protocol.cpp) + GDGlobalWhisperRecv/DGGlobalWhisperRecv/
// DGGlobalWhisperEchoRecv (DataServer/DSProtocol.cpp, ya implementado del lado DataServer). Esta
// pasada cubre: chat público (sin sigilos ~/@/@@/@>/$ -- party/guild/gens quedan para cuando esas
// fases existan, documentado en README) y whisper (local + cruzado entre GameServers vía
// DataServer, mecanismo que el DataServer ya tenía completo desde antes de esta fase).

/// <summary>PMSG_CHAT_RECV/SEND, C1:00 -- mismo layout en ambas direcciones (Protocol.h). El cliente
/// manda su propio nombre (se verifica contra el real, anti-spoof) + el mensaje.</summary>
public sealed record ChatRecv(string Name, string Message)
{
    public static ChatRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3); // C1 header: type,size,head = 3 bytes
        var name = r.ReadFixedString(10);
        var message = r.ReadFixedString(Math.Max(p.Length - 13, 0));
        return new ChatRecv(name, message);
    }
}

/// <summary>PMSG_CHAT_WHISPER_RECV/SEND, C1:02 -- RECV: name=nombre del destinatario. SEND:
/// name=nombre de quien susurra (origen).</summary>
public sealed record ChatWhisperRecv(string TargetName, string Message)
{
    public static ChatWhisperRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var target = r.ReadFixedString(10);
        var message = r.ReadFixedString(Math.Max(p.Length - 13, 0));
        return new ChatWhisperRecv(target, message);
    }
}

public static class ChatPacketBuilder
{
    /// <summary>C1:00 -- chat público, se manda tal cual al propio hablante (eco) y a todo el que lo
    /// tenga en su VisibleTo (mismo mecanismo de viewport que ya usa movimiento, ver MsgSendV2 en el
    /// brief de investigación de la Fase 5).</summary>
    public static byte[] ChatSend(string name, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x00, w.ToArray());
    }

    /// <summary>C1:02 -- entrega de un whisper al destinatario (local o vía DataServer).</summary>
    public static byte[] ChatWhisperSend(string sourceName, string message)
    {
        var w = new PacketWriter();
        w.WriteFixedString(sourceName, 10);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x02, w.ToArray());
    }

    /// <summary>C1:0D -- NoticeSend (notificaciones/mensajes del servidor en pantalla o chat).</summary>
    public static byte[] NoticeSend(string notice, byte type = 0)
    {
        byte[] textBytes = System.Text.Encoding.Latin1.GetBytes(notice);
        var w = new PacketWriter();
        w.WriteByte(type);   // 0=Noticia dorada en pantalla, 1=Mensaje azul en chatbox
        w.WriteByte(0);      // count
        w.WriteByte(0);      // opacity
        w.WriteUInt16(0);    // delay
        w.WriteUInt32(0);    // color
        w.WriteByte(0);      // speed
        w.WriteBytes(textBytes, textBytes.Length);
        w.WriteByte(0);      // null-terminator (evita el carácter parásito ý en el cliente)
        return PacketBuilder.BuildC1(0x0D, w.ToArray());
    }
}

public static class ChaosBoxPacketBuilder
{
    /// <summary>PMSG_SHOP_ITEM_LIST_SEND (C2:31), type=3 -- Abre la ventana de Chaos Box en el cliente y sincroniza sus ítems.</summary>
    public static byte[] ChaosBoxItemListSend(IReadOnlyList<Item> items)
    {
        using var body = new MemoryStream();
        byte itemCount = 0;
        Span<byte> itemWire = stackalloc byte[5];

        for (int slot = 0; slot < items.Count; slot++)
        {
            var item = items[slot];
            if (item.IsItem())
            {
                body.WriteByte((byte)slot);
                item.ToWireBytes(itemWire);
                body.Write(itemWire);
                itemCount++;
            }
        }

        var header = new PacketWriter();
        header.WriteByte(3); // Type 3 = Chaos Box
        header.WriteByte(itemCount);
        header.WriteBytes(body.ToArray(), (int)body.Length);

        return PacketBuilder.BuildC2(0x31, header.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_SEND (C1:86) -- Notifica el resultado de la mezcla de Chaos Box.</summary>
    public static byte[] ChaosMixSend(byte result, Item? createdItem)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        if (createdItem != null && createdItem.IsItem())
        {
            Span<byte> itemWire = stackalloc byte[5];
            createdItem.ToWireBytes(itemWire);
            w.WriteBytes(itemWire.ToArray(), 5);
            w.WriteByte(0xFF);
            w.WriteByte(0xFF);
        }
        else
        {
            for (int i = 0; i < 7; i++) w.WriteByte(0xFF);
        }
        return PacketBuilder.BuildC1(0x86, w.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_RATE_SEND (C1:88) -- Envía el % de éxito y costo en Zen a la UI de la Chaos Box.</summary>
    public static byte[] ChaosMixRateSend(int rate, int money)
    {
        var w = new PacketWriter();
        // PBMSG_HEAD mide 3 bytes y el int rate necesita alineación a 4, así que MSVC inserta 1 byte
        // de relleno en el offset 3 (sizeof = 12, no 11). El original manda sizeof(pMsg) entero
        // (ChaosBox.cpp:1024), así que sin este byte rate y money le llegan corridos al cliente.
        w.WriteByte(0); // relleno de alineación (offset 3 -> 4)
        w.WriteUInt32((uint)rate);
        w.WriteUInt32((uint)money);
        return PacketBuilder.BuildC1(0x88, w.ToArray());
    }

    /// <summary>PMSG_CHAOS_MIX_CLOSE (C1:87) -- Notifica el cierre de la ventana de Chaos Box.</summary>
    public static byte[] ChaosMixCloseSend()
    {
        return PacketBuilder.BuildC1(0x87, Array.Empty<byte>());
    }
}

public static class BloodCastlePacketBuilder
{
    /// <summary>PMSG_BLOOD_CASTLE_ENTER_SEND (C1:9A) -- Respuesta a solicitud de entrada a Blood Castle.</summary>
    public static byte[] BloodCastleEnterSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x9A, w.ToArray());
    }

    /// <summary>PMSG_BLOOD_CASTLE_STATE_SEND (C1:9B) -- Estado y reloj de Blood Castle.</summary>
    public static byte[] BloodCastleStateSend(byte state, ushort time, ushort maxMonster, ushort curMonster, ushort eventItemOwner, byte eventItemLevel)
    {
        var w = new PacketWriter();
        w.WriteByte(state);
        w.WriteUInt16(time);
        w.WriteUInt16(maxMonster);
        w.WriteUInt16(curMonster);
        w.WriteUInt16(eventItemOwner);
        w.WriteByte(eventItemLevel);
        // Relleno de cola: el struct alinea a 2 (por los WORD) y su último campo termina en el
        // offset 12, así que sizeof = 14, no 13. Los campos caen bien sin esto, pero el tamaño
        // declarado no coincide con el del original y un cliente que valide sizeof lo rechaza.
        w.WriteByte(0);
        return PacketBuilder.BuildC1(0x9B, w.ToArray());
    }

    /// <summary>PMSG_BLOOD_CASTLE_SCORE_SEND (C1:93) -- Puntuación final y entrega de recompensa.</summary>
    public static byte[] BloodCastleScoreSend(byte type, byte flag, string name, uint score, uint exp, uint zen)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(flag);
        w.WriteFixedString(name, 10);
        w.WriteUInt16(0);
        w.WriteUInt32(score);
        w.WriteUInt32(exp);
        w.WriteUInt32(zen);
        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }
}

// ---------------------------------------------------------------- GameServer <-> DataServer (whisper cruzado)

public static class SocialDataServerPacketBuilder
{
    /// <summary>SDHP_GLOBAL_WHISPER_SEND (GS->DS), C1:72 -- espejo exacto de lo que
    /// MuServer.DataServer/Protocol/DataServerPackets.cs::GlobalWhisperRecv.Parse espera leer.
    /// Puerto de GDGlobalWhisperSend (Protocol.cpp), usado cuando gObjFind no encuentra al
    /// destinatario en ESTE GameServer.</summary>
    public static byte[] GlobalWhisperRequest(ushort index, string account, string name, string targetName, string message)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        w.WriteFixedString(message, 60);
        return PacketBuilder.BuildC1(0x72, w.ToArray());
    }
}

/// <summary>SDHP_GLOBAL_WHISPER_SEND (DS->GS de vuelta al remitente), C1:72 -- espejo exacto de
/// DataServerPacketBuilder.GlobalWhisperSend. Puerto de DGGlobalWhisperRecv (Protocol.cpp):
/// result=0 -> "destinatario no encontrado en ningún GameServer" (GCServerMsgSend); result=1 ->
/// ya se le entregó vía DGGlobalWhisperEchoRecv en el GameServer del destinatario.</summary>
public sealed record GlobalWhisperResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName, string Message)
{
    public static GlobalWhisperResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        var message = r.ReadFixedString(60);
        return new GlobalWhisperResultFromDataServer(index, account, name, result, targetName, message);
    }
}

/// <summary>SDHP_GLOBAL_WHISPER_ECHO_SEND (DS->GS del destinatario), C1:73 -- espejo exacto de
/// DataServerPacketBuilder.GlobalWhisperEchoSend. Puerto de DGGlobalWhisperEchoRecv
/// (Protocol.cpp): llega al GameServer donde el destinatario está conectado de verdad, con su
/// propio Index/Account/Name (no los del remitente) + el nombre de quien susurra + el mensaje.</summary>
public sealed record GlobalWhisperEchoFromDataServer(ushort Index, string Account, string Name, string SourceName, string Message)
{
    public static GlobalWhisperEchoFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var sourceName = r.ReadFixedString(11);
        var message = r.ReadFixedString(60);
        return new GlobalWhisperEchoFromDataServer(index, account, name, sourceName, message);
    }
}

// ---------------------------------------------------------------- Fase 5: amigos (primera pasada)
// Puerto simplificado de GameServer/Friend.cpp (adaptador delgado hacia DataServer) + Friend.h
// (opcodes/structs del lado cliente). El correo entre amigos (T_FriendMail) no está portado -- ver
// comentario de db/postgres/004_friends.sql. El protocolo DataServer<->GameServer (head 0xB0) usa un
// esquema de sub-códigos propio, más simple que el PSBMSG_HEAD 1-a-1 del original -- ver comentario
// en MuServer.DataServer/Protocol/DataServerPackets.cs.

/// <summary>PMSG_FRIEND_LIST_RECV, C1:C0 -- sin cuerpo. No documentado explícitamente como opcode
/// separado en Friend.h (el research solo confirma el _SEND), pero hace falta algún disparador para
/// que el cliente pida la lista -- se asume que comparte head con el _SEND (mismo patrón ya usado en
/// este puerto para CHARACTER_LIST y PARTY_LIST, ambos comparten un único head en las dos direcciones).</summary>
public sealed record FriendListClientRecv;

/// <summary>PMSG_FRIEND_REQUEST_RECV, C1:C1 -- Name[10] = a quién se quiere agregar.</summary>
public sealed record FriendRequestClientRecv(string TargetName)
{
    public static FriendRequestClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new FriendRequestClientRecv(r.ReadFixedString(10));
    }
}

/// <summary>PMSG_FRIEND_RESULT_RECV, C1:C2 -- result=aceptar/rechazar, Name[10]=quien mandó la
/// solicitud original.</summary>
public sealed record FriendResultClientRecv(byte Result, string RequesterName)
{
    public static FriendResultClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        var result = r.ReadByte();
        var name = r.ReadFixedString(10);
        return new FriendResultClientRecv(result, name);
    }
}

/// <summary>PMSG_FRIEND_DELETE_RECV, C1:C3 -- Name[10] = a quién se quiere borrar.</summary>
public sealed record FriendDeleteClientRecv(string TargetName)
{
    public static FriendDeleteClientRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 3);
        return new FriendDeleteClientRecv(r.ReadFixedString(10));
    }
}

public static class FriendPacketBuilder
{
    /// <summary>PMSG_FRIEND_LIST_SEND, C1:C0 -- MailCount/MailTotal fijos en 0 (mailbox no portado).</summary>
    public static byte[] FriendListSend(IReadOnlyList<(string Name, byte Server)> friends)
    {
        var w = new PacketWriter();
        w.WriteByte(0); // MailCount -- correo entre amigos no portado
        w.WriteByte(0); // MailTotal
        w.WriteByte((byte)Math.Min(friends.Count, 255));

        foreach (var (name, server) in friends)
        {
            w.WriteFixedString(name, 10);
            w.WriteByte(server);
        }

        return PacketBuilder.BuildC1(0xC0, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_REQUEST_SEND, C1:C1 -- push al DESTINATARIO avisándole que alguien lo
    /// quiere agregar.</summary>
    public static byte[] FriendRequestSend(byte result, string name, byte server)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        w.WriteByte(server);
        return PacketBuilder.BuildC1(0xC1, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_RESULT_SEND, C1:C2 -- confirmación de que ahora son amigos (se manda a
    /// ambos lados: al que aceptó, con el nombre de quien pidió la amistad, y a quien la pidió, con
    /// el nombre de quien la aceptó).</summary>
    public static byte[] FriendResultSend(string name)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        return PacketBuilder.BuildC1(0xC2, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_DELETE_SEND, C1:C3.</summary>
    public static byte[] FriendDeleteSend(byte result, string name)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        return PacketBuilder.BuildC1(0xC3, w.ToArray());
    }

    /// <summary>PMSG_FRIEND_STATE_SEND, C1:C4 -- push no solicitado de cambio de estado online/offline.</summary>
    public static byte[] FriendStateSend(string name, byte server)
    {
        var w = new PacketWriter();
        w.WriteFixedString(name, 10);
        w.WriteByte(server);
        return PacketBuilder.BuildC1(0xC4, w.ToArray());
    }
}

// ---------------------------------------------------------------- GameServer <-> DataServer (amigos, head 0xB0)

public static class FriendDataServerPacketBuilder
{
    public static byte[] FriendListRequest(ushort index, string account, string name)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x00, w.ToArray());
    }

    public static byte[] FriendRequestRequest(ushort index, string account, string name, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x01, w.ToArray());
    }

    public static byte[] FriendResultRequest(ushort index, string account, string name, byte result, string requesterName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteByte(result);
        w.WriteFixedString(requesterName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x03, w.ToArray());
    }

    public static byte[] FriendDeleteRequest(ushort index, string account, string name, string targetName)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteFixedString(targetName, 11);
        return PacketBuilder.BuildC1Sub(0xB0, 0x05, w.ToArray());
    }
}

public sealed record FriendListEntryFromDataServer(string Name, byte Server);

public sealed record FriendListFromDataServer(ushort Index, string Account, string Name, IReadOnlyList<FriendListEntryFromDataServer> Friends)
{
    public static FriendListFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var count = r.ReadByte();
        var friends = new List<FriendListEntryFromDataServer>(count);

        for (int n = 0; n < count; n++)
        {
            var friendName = r.ReadFixedString(11);
            var server = r.ReadByte();
            friends.Add(new FriendListEntryFromDataServer(friendName, server));
        }

        return new FriendListFromDataServer(index, account, name, friends);
    }
}

public sealed record FriendRequestResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName)
{
    public static FriendRequestResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        return new FriendRequestResultFromDataServer(index, account, name, result, targetName);
    }
}

public sealed record FriendRequestIncomingFromDataServer(ushort TargetIndex, string TargetAccount, string TargetName, string RequesterName, byte RequesterServer)
{
    public static FriendRequestIncomingFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var requesterName = r.ReadFixedString(11);
        var requesterServer = r.ReadByte();
        return new FriendRequestIncomingFromDataServer(index, account, name, requesterName, requesterServer);
    }
}

public sealed record FriendResultAckFromDataServer(ushort Index, string Account, string Name, byte Result, string RequesterName)
{
    public static FriendResultAckFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var requesterName = r.ReadFixedString(11);
        return new FriendResultAckFromDataServer(index, account, name, result, requesterName);
    }
}

public sealed record FriendResultDeliverFromDataServer(ushort RequesterIndex, string RequesterAccount, string RequesterName, byte Result, string AccepterName)
{
    public static FriendResultDeliverFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var accepterName = r.ReadFixedString(11);
        return new FriendResultDeliverFromDataServer(index, account, name, result, accepterName);
    }
}

public sealed record FriendDeleteResultFromDataServer(ushort Index, string Account, string Name, byte Result, string TargetName)
{
    public static FriendDeleteResultFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var result = r.ReadByte();
        var targetName = r.ReadFixedString(11);
        return new FriendDeleteResultFromDataServer(index, account, name, result, targetName);
    }
}

public sealed record FriendStateFromDataServer(ushort OwnerIndex, string OwnerAccount, string OwnerName, string FriendName, byte Server)
{
    public static FriendStateFromDataServer Parse(byte[] p)
    {
        var r = new PacketReader(p, 4);
        var index = r.ReadUInt16();
        var account = r.ReadFixedString(11);
        var name = r.ReadFixedString(11);
        var friendName = r.ReadFixedString(11);
        var server = r.ReadByte();
        return new FriendStateFromDataServer(index, account, name, friendName, server);
    }
}

// ---------------------------------------------------------------- Fase 5: party (primera pasada)
// Puerto de Party.h/Party.cpp -- invitar/aceptar/salir/expulsar/lista/vida periódica + reparto de
// experiencia en grupo (ver ClientProtocolHandler.GrantPartyExperienceAsync). PartyMatching (tablón
// de búsqueda de grupo) queda fuera de esta pasada -- ver nota de "safe to defer" en el brief de
// investigación de la Fase 5 (no es una dependencia del party básico, es una UI de browsing aparte).

/// <summary>PMSG_PARTY_REQUEST_RECV, C1:40 -- index = objetivo de la invitación.</summary>
public sealed record PartyRequestRecv(int TargetIndex)
{
    public static PartyRequestRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_PARTY_REQUEST_RESULT_RECV, C1:41 -- result=aceptar/rechazar, index = quien invitó
/// (para que el servidor valide que la respuesta corresponde a una invitación realmente pendiente).</summary>
public sealed record PartyRequestResultRecv(byte Result, int InviterIndex)
{
    public static PartyRequestResultRecv Parse(byte[] p) => new(p[3], (p[4] << 8) | p[5]);
}

/// <summary>PMSG_PARTY_DEL_MEMBER_RECV, C1:43 -- number = slot propio (salir) o de otro miembro
/// (expulsar, solo si quien lo manda es el líder).</summary>
public sealed record PartyDelMemberRecv(byte Number)
{
    public static PartyDelMemberRecv Parse(byte[] p) => new(p[3]);
}

/// <summary>Un renglón de PMSG_PARTY_LIST (24 bytes en el wire: 22 de campos + 2 de relleno de
/// alineación que MSVC inserta antes de <c>CurLife</c>) -- puerto EXACTO de <c>Party.h</c> del árbol
/// fuente correcto ("Emulator 0.99 (2.1.7)/GameServer" -- ver el doc-comment de
/// <see cref="World.Item"/> para la explicación de por qué este repo tiene dos árboles de C++ y cuál
/// es el real): <c>name[10],number,map,x,y,CurLife(DWORD),MaxLife(DWORD)</c>. REVERTIDO: no existe
/// ServerCode ni mana en este struct -- eran campos fabricados de una pasada de porting anterior que
/// investigó contra el árbol equivocado (una temporada posterior). Confirmado también contra el
/// builder real <c>CParty::GCPartyListSend</c> (Party.cpp:594-650), que solo llena estos 7 campos.</summary>
public sealed record PartyListEntry(string Name, byte Number, byte Map, byte X, byte Y, uint CurLife, uint MaxLife);

public static class PartyPacketBuilder
{
    /// <summary>PMSG_PARTY_REQUEST_SEND, C1:40 -- avisa al invitado quién lo está invitando.</summary>
    public static byte[] PartyRequestSend(int inviterIndex)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((inviterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(inviterIndex & 0xFF));
        return PacketBuilder.BuildC1(0x40, w.ToArray());
    }

    /// <summary>PMSG_PARTY_RESULT_SEND, C1:41 -- resultado al que invitó (0=falló/rechazado,
    /// 2=grupo lleno/no se pudo crear, 4=el objetivo ya estaba en un grupo).</summary>
    public static byte[] PartyResultSend(byte result)
    {
        return PacketBuilder.BuildC1(0x41, new[] { result });
    }

    /// <summary>PMSG_PARTY_LIST_SEND, C1:42 -- result=0 (sin grupo) o 1 (con grupo) + lista de
    /// miembros. Se manda a TODOS los miembros cada vez que la composición del grupo cambia (puerto
    /// de GCPartyListSend), y también a quien la pida on-demand.</summary>
    public static byte[] PartyListSend(byte result, IReadOnlyList<PartyListEntry> members)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)members.Count);

        foreach (var m in members)
        {
            w.WriteFixedString(m.Name, 10);
            w.WriteByte(m.Number);
            w.WriteByte(m.Map);
            w.WriteByte(m.X);
            w.WriteByte(m.Y);

            // Party.h no usa #pragma pack(1): tras name[10]+number+map+x+y vamos en el offset 14 y
            // el DWORD CurLife necesita alineación a 4, así que MSVC inserta 2 bytes de relleno
            // (sizeof(PMSG_PARTY_LIST) = 24, no 22). El original copia el struct entero por miembro
            // (memcpy(&send[size],&info,sizeof(info)), Party.cpp:535), así que van en el wire.
            w.WriteUInt16(0); // relleno de alineación (offset 14 -> 16)

            w.WriteUInt32(m.CurLife);
            w.WriteUInt32(m.MaxLife);
        }

        return PacketBuilder.BuildC1(0x42, w.ToArray());
    }

    /// <summary>PMSG_PARTY_DEL_MEMBER_SEND, C1:43 -- sin cuerpo, le dice al cliente REMOVIDO que
    /// limpie su interfaz de grupo (puerto de GCPartyDelMemberSend).</summary>
    public static byte[] PartyDelMemberSend()
    {
        return PacketBuilder.BuildC1(0x43, Array.Empty<byte>());
    }

    /// <summary>PMSG_PARTY_LIFE_SEND, C1:44 -- puerto EXACTO de <c>CParty::GCPartyLifeSend</c>
    /// (Party.cpp:666-700 del árbol fuente correcto). REVERTIDO: una pasada anterior había fabricado
    /// un formato de 13 bytes/miembro (vida%+maná%+nombre) investigando contra el árbol equivocado.
    /// El real es 1 BYTE por miembro: nibble alto = slot (<c>número*16</c>), nibble bajo = vida en
    /// "décimos" (<c>Life/((MaxLife+AddLife)/10)</c>, 0-9) -- sin maná, sin nombre (el cliente ya
    /// tiene la lista de nombres de PMSG_PARTY_LIST y solo necesita refrescar la barra de vida por
    /// slot).</summary>
    public static byte[] PartyLifeSend(IReadOnlyList<(byte Number, uint Life, uint MaxLife)> members)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)members.Count);

        foreach (var (number, life, maxLife) in members)
        {
            uint tenth = maxLife == 0 ? 0 : Math.Max(1u, maxLife / 10);
            byte lifeBucket = (byte)Math.Min(15u, life / tenth);
            w.WriteByte((byte)(((number * 16) & 0xF0) | (lifeBucket & 0x0F)));
        }

        return PacketBuilder.BuildC1(0x44, w.ToArray());
    }
}

// ---------------------------------------------------------------- Fase 6: Devil Square (primera pasada)
// Puerto de DevilSquare.h/.cpp + la porción "Devil Square" de Protocol.h/.cpp (CGDevilSquareEnterRecv,
// CGEventRemainTimeRecv) + DSProtocol.h (GDRankingDevilSquareSaveSend, head 0x3F -- el lado DataServer
// ya estaba completo desde antes de esta fase, ver DataServerProtocolHandler.OnRankingScoreSaveAsync).
// Motor de estados y toda la lógica de entrada/puntaje/recompensa viven en World/DevilSquareManager.cs;
// acá solo el layout de paquetes, igual que el resto de este archivo.

/// <summary>PMSG_DEVIL_SQUARE_ENTER_RECV, C1:90 -- level = bracket pedido (0-based), slot = slot de
/// INVENTARIO COMPLETO (incluye equipo, el servidor resta INVENTORY_WEAR_SIZE=12 antes de usarlo).</summary>
public sealed record DevilSquareEnterRecv(byte Level, byte Slot)
{
    public static DevilSquareEnterRecv Parse(byte[] p) => new(p[3], p[4]);
}

/// <summary>PMSG_EVENT_REMAIN_TIME_RECV, C1:91 -- EventType=1 es Devil Square (2/3/4 son Blood/Chaos/
/// Illusion Temple, no portados). El servidor ignora ItemLevel y recalcula el bracket del propio
/// jugador (ver DevilSquareManager.HandleRemainTimeQueryAsync) -- se parsea igual para no romper el
/// framing, aunque no se use.</summary>
public sealed record EventRemainTimeRecv(byte EventType, byte ItemLevel)
{
    public static EventRemainTimeRecv Parse(byte[] p) => new(p[3], p[4]);
}

/// <summary>Un renglón de PMSG_DEVIL_SQUARE_SCORE (24 bytes en el wire: name[10]+2 de relleno de
/// alineación+score(4)+rewardExp(4)+
/// rewardMoney(4)) -- ver DevilSquareManager.BuildScoreEntry.</summary>
public sealed record DevilSquareScoreEntry(string Name, uint Score, uint RewardExperience, uint RewardMoney);

public static class DevilSquarePacketBuilder
{
    /// <summary>PMSG_DEVIL_SQUARE_ENTER_SEND, C1:90 -- result: 0=ok, 1=nivel/slot/item inválido,
    /// 2=ventana cerrada, 3=personaje muy alto de nivel para este bracket, 4=muy bajo, 5=lleno.</summary>
    public static byte[] EnterSend(byte result) => PacketBuilder.BuildC1(0x90, new[] { result });

    /// <summary>PMSG_EVENT_REMAIN_TIME_SEND, C1:91 -- RemainTimeH = minutos restantes (hasta que abra,
    /// si todavía está cerrado) O EnteredUser = participantes actuales (si ya está abierto) -- mutuamente
    /// excluyentes, igual que el original (ver investigación de esta fase). RemainTimeL siempre 0 en la
    /// rama de Devil Square (el original tampoco lo escribe ahí).</summary>
    public static byte[] RemainTimeSend(byte eventType, byte remainTimeH, byte enteredUser)
    {
        var w = new PacketWriter();
        w.WriteByte(eventType);
        w.WriteByte(remainTimeH);
        w.WriteByte(enteredUser);
        w.WriteByte(0);
        return PacketBuilder.BuildC1(0x91, w.ToArray());
    }

    /// <summary>PMSG_TIME_COUNT_SEND, C1:92 -- klaxon de 30 segundos, sin texto (el catálogo de
    /// mensajes no está portado, ver deuda técnica documentada en DevilSquareManager). type: 0=fase
    /// EMPTY (broadcast a TODO el server), 1=fase STAND, 2=fase START (broadcast solo a participantes).</summary>
    public static byte[] TimeCountSend(byte type) => PacketBuilder.BuildC1(0x92, new[] { type });

    /// <summary>PMSG_DEVIL_SQUARE_SCORE_SEND, C1:93 -- rank = puesto final del receptor (1-based),
    /// seguido de hasta MAX_DS_RANK(10) entradas donde la #0 es SIEMPRE el propio receptor (repetido
    /// más abajo en su posición real si entra en el top 9 -- quirk documentado en la investigación,
    /// ver DevilSquareManager.SendScoreListAsync).</summary>
    public static byte[] ScoreSend(byte rank, IReadOnlyList<DevilSquareScoreEntry> entries)
    {
        var w = new PacketWriter();
        w.WriteByte(rank);
        w.WriteByte((byte)entries.Count);

        foreach (var e in entries)
        {
            w.WriteFixedString(e.Name, 10);
            // DevilSquare.h no usa #pragma pack(1): tras name[10] vamos en el offset 10 y el DWORD
            // score se alinea a 4, así que MSVC inserta 2 bytes de relleno (sizeof = 24, no 22). El
            // original copia el struct entero por entrada (memcpy(&send[size],&info,sizeof(info)),
            // DevilSquare.cpp:1307-1308), así que van en el wire.
            w.WriteUInt16(0); // relleno de alineación (offset 10 -> 12)
            w.WriteUInt32(e.Score);
            w.WriteUInt32(e.RewardExperience);
            w.WriteUInt32(e.RewardMoney);
        }

        return PacketBuilder.BuildC1(0x93, w.ToArray());
    }
}

/// <summary>GameServer -> DataServer, cabecera compartida por 0x3D/0x3E/0x3F/0x40 (Blood/Chaos/Devil/
/// Illusion Temple -- ver DSProtocol.h) -- espejo exacto de lo que
/// MuServer.DataServer/Protocol/DataServerPackets.cs::RankingScoreSaveRecv.Parse espera leer (ya
/// implementado del lado DataServer para los 4 heads, ver DataServerProtocolHandler:62-65). Fire-and-
/// forget, sin respuesta.</summary>
public static class EventDataServerPacketBuilder
{
    public const byte HeadDevilSquare = 0x3F;

    public static byte[] RankingScoreSave(byte head, ushort index, string account, string name, uint score)
    {
        var w = new PacketWriter();
        w.WriteUInt16(index);
        w.WriteFixedString(account, 11);
        w.WriteFixedString(name, 11);
        w.WriteUInt32(score);
        return PacketBuilder.BuildC1(head, w.ToArray());
    }
}

// ---------------------------------------------------------------- Fase "skills": magia y maná

/// <summary>PMSG_SKILL_ATTACK_RECV (SkillManager.h:102-108), C3:19 -- pedido de casteo de un skill de
/// ataque de un solo objetivo (CSkillManager::CGSkillAttackRecv). Igual que el resto de los C3 de
/// este puerto: GameClientFramer ya lo sintetiza a un paquete lógico C1 antes de llegar acá (el tipo
/// real 0xC3/cifrado por bloques queda resuelto en la capa de framing), así que se parsea con el
/// mismo layout de 3 bytes de cabecera que un C1 normal.</summary>
public sealed record SkillAttackRecv(byte Skill, int TargetIndex, byte Dis)
{
    public static SkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        int index = p.Length > 5 ? ((p[4] << 8) | p[5]) : (p.Length > 4 ? p[4] : 0);
        byte dis = p.Length > 6 ? p[6] : (byte)0;
        return new SkillAttackRecv(skill, index, dis);
    }
}

public sealed record DurationSkillAttackRecv(byte Skill, byte X, byte Y, byte Dir, byte Dis, byte Angle, int TargetIndex, byte MagicKey)
{
    public static DurationSkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        byte x = p.Length > 4 ? p[4] : (byte)0;
        byte y = p.Length > 5 ? p[5] : (byte)0;
        byte dir = p.Length > 6 ? p[6] : (byte)0;
        byte dis = p.Length > 7 ? p[7] : (byte)0;
        byte angle = p.Length > 8 ? p[8] : (byte)0;
        int targetIndex = p.Length > 10 ? ((p[9] << 8) | p[10]) : 0xFFFF;
        byte magicKey = p.Length > 11 ? p[11] : (byte)0;
        return new DurationSkillAttackRecv(skill, x, y, dir, dis, angle, targetIndex, magicKey);
    }
}

public sealed record MultiSkillAttackRecv(byte Skill, byte X, byte Y, byte Serial, byte Count, IReadOnlyList<int> Targets)
{
    public static MultiSkillAttackRecv Parse(byte[] p)
    {
        byte skill = p.Length > 3 ? p[3] : (byte)0;
        byte x = p.Length > 4 ? p[4] : (byte)0;
        byte y = p.Length > 5 ? p[5] : (byte)0;
        byte serial = p.Length > 6 ? p[6] : (byte)0;
        byte count = p.Length > 7 ? p[7] : (byte)0;
        count = Math.Min(count, (byte)10);

        var targets = new List<int>();
        int offset = 8;
        for (int i = 0; i < count && offset + 2 <= p.Length; i++)
        {
            int index = (p[offset] << 8) | p[offset + 1];
            targets.Add(index);
            offset += 3;
        }

        return new MultiSkillAttackRecv(skill, x, y, serial, count, targets);
    }
}

public static class SkillPacketBuilder
{
    /// <summary>PMSG_SKILL_ATTACK_SEND (SkillManager.h:144-150), C3:19 -- puerto de
    /// CSkillManager::GCSkillAttackSend (SkillManager.cpp:2665-2685): se manda tanto al propio
    /// casteador (unicast) como, por viewport, a los que lo estén viendo -- ver
    /// ClientProtocolHandler.OnSkillAttackAsync para el fan-out. Cifrado por bloques
    /// (ClientSession.SendEncryptedAsync), igual que WorldPacketBuilder.TeleportSend.</summary>
    public static byte[] SkillAttackSend(byte skill, int casterIndex, int targetIndex)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((casterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(casterIndex & 0xFF));
        w.WriteByte((byte)((targetIndex >> 8) & 0xFF));
        w.WriteByte((byte)(targetIndex & 0xFF));
        return PacketBuilder.BuildC1(0x19, w.ToArray());
    }

    /// <summary>PMSG_DURATION_SKILL_ATTACK_SEND (C3:1E) -- Emite la animación y casteo del hechizo a los observadores del mapa.</summary>
    public static byte[] DurationSkillAttackSend(int casterIndex, byte skill, byte x, byte y, byte dir)
    {
        var w = new PacketWriter();
        w.WriteByte(skill);
        w.WriteByte((byte)((casterIndex >> 8) & 0xFF));
        w.WriteByte((byte)(casterIndex & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        w.WriteByte(dir);
        return PacketBuilder.BuildC1(0x1E, w.ToArray());
    }

    /// <summary>PMSG_SKILL_LIST_SEND (SkillManager.h:177-189), C1:F3:11 -- puerto de
    /// CSkillManager::GCSkillListSend (SkillManager.cpp:2789-2827): manda la lista completa de skills
    /// activos del personaje (aprendidos + default de clase + skills de armas equipadas).</summary>
    public static byte[] SkillListSend(IReadOnlyList<(byte slot, ushort skillIndex, byte level)> skills)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)skills.Count);

        foreach (var (slot, skillIndex, level) in skills)
        {
            w.WriteByte(slot);
            byte b1 = (byte)(skillIndex & 0xFF);
            byte b2 = (byte)(((skillIndex / 255) & 7) | (level << 3));
            w.WriteByte(b1);
            w.WriteByte(b2);
        }

        return PacketBuilder.BuildC1Sub(0xF3, 0x11, w.ToArray());
    }

    /// <summary>PMSG_SKILL_LIST_SEND (C1:F3:11) count=0xFE -- Agrega una habilidad a la barra del cliente en tiempo real.</summary>
    public static byte[] SkillAddSend(byte slot, ushort skillIndex, byte level = 0)
    {
        var w = new PacketWriter();
        w.WriteByte(0xFE);
        w.WriteByte(slot);
        byte b1 = (byte)(skillIndex & 0xFF);
        byte b2 = (byte)(((skillIndex / 255) & 7) | (level << 3));
        w.WriteByte(b1);
        w.WriteByte(b2);
        return PacketBuilder.BuildC1Sub(0xF3, 0x11, w.ToArray());
    }

    /// <summary>PMSG_POSITION_SEND (Protocol.h:593), C1:D0 -- puerto de CGPositionRecv.</summary>
    public static byte[] PositionSend(int index, byte x, byte y)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteByte(x);
        w.WriteByte(y);
        return PacketBuilder.BuildC1(0xD0, w.ToArray());
    }
}

public sealed record PositionRecv(byte X, byte Y)
{
    public static PositionRecv Parse(byte[] p)
    {
        byte x = p.Length > 3 ? p[3] : (byte)0;
        byte y = p.Length > 4 ? p[4] : (byte)0;
        return new PositionRecv(x, y);
    }
}

public static class LifePacketBuilder
{
    /// <summary>PMSG_LIFE_SEND (Protocol.h:364-373), C1:26 -- puerto de GCLifeSend (Protocol.cpp:1527):
    /// type=0xFE (MaxLife), type=0xFF (Current Life). En C++, ViewHP (DWORD) está alineado a 4 bytes,
    /// quedando en el offset 8 (1 byte de relleno tras flag). Sin ese byte de relleno, el cliente lee
    /// ViewHP corrido 8 bits produciendo números basura negativos.</summary>
    public static byte[] LifeSend(byte type, int life)
    {
        var w = new PacketWriter();
        int clampedLife = Math.Clamp(life, 0, 65535);
        w.WriteByte(type);
        w.WriteByte((byte)((clampedLife >> 8) & 0xFF));
        w.WriteByte((byte)(clampedLife & 0xFF));
        w.WriteByte(0); // flag (offset 6)
        w.WriteByte(0); // PADDING (offset 7) para alinear ViewHP a offset 8
        w.WriteUInt32((uint)clampedLife); // ViewHP (offset 8)
        return PacketBuilder.BuildC1(0x26, w.ToArray());
    }
}

public static class ManaPacketBuilder
{
    /// <summary>PMSG_MANA_SEND (Protocol.h:375-386), C1:27 -- puerto de GCManaSend (Protocol.cpp:1547):
    /// mana/bp van empaquetados como WORD big-endian (SET_NUMBERHB/LB, igual que el resto de los
    /// campos de 2 bytes "armados a mano" de este protocolo, NO como un WriteUInt16 little-endian
    /// normal), clampeados a 0-65535 igual que GET_MAX_WORD_VALUE del original. GAMESERVER_EXTRA==1
    /// en este build agrega ViewMP/ViewBP (DWORD) al final.
    /// CORREGIDO (bug introducido en una pasada anterior de esta misma sesión): PMSG_MANA_SEND usa
    /// PBMSG_HEAD (3 bytes: type+size+head, ver doc-comment de CombatPacketBuilder.DamageSend para la
    /// explicación completa), NO PSBMSG_HEAD (4 bytes) -- es C1:27 directo, sin sub-código. Offset real
    /// tras type+mana[2]+bp[2] = 3+1+2+2 = 8, que YA es múltiplo de 4 -- CERO bytes de relleno hacen
    /// falta. Los 3 bytes que se agregaban de más corrían ViewMP/ViewBP y alargaban el paquete,
    /// produciendo los mismos números de maná/BP basura que <see cref="CombatPacketBuilder.DamageSend"/>
    /// producía con la vida/daño.</summary>
    public static byte[] ManaSend(byte type, int mana, int bp)
    {
        var w = new PacketWriter();
        int clampedMana = Math.Clamp(mana, 0, 65535);
        int clampedBp = Math.Clamp(bp, 0, 65535);
        w.WriteByte(type);
        w.WriteByte((byte)((clampedMana >> 8) & 0xFF));
        w.WriteByte((byte)(clampedMana & 0xFF));
        w.WriteByte((byte)((clampedBp >> 8) & 0xFF));
        w.WriteByte((byte)(clampedBp & 0xFF));
        w.WriteUInt32((uint)clampedMana); // ViewMP
        w.WriteUInt32((uint)clampedBp); // ViewBP
        return PacketBuilder.BuildC1(0x27, w.ToArray());
    }
}

// ---------------------------------------------------------------- Quest info / pet item info
// (respuestas mínimas para que el cliente real no se quede reintentando -- sistemas de quests/pets
// en sí no portados todavía, ver README).

/// <summary>PMSG_PET_ITEM_INFO_RECV (Protocol.h:178-184), C1:A9 -- consulta de nivel/experiencia de
/// un pet (Dark Horse/Dark Reaven) guardado en un slot de inventario. type: 0/1 (cuál de los dos
/// pets), flag: 0=Inventory (único container soportado en esta pasada, igual que el resto del
/// inventario -- ver ItemPacketBuilder), slot: índice dentro de ese container.</summary>
public sealed record PetItemInfoRecv(byte Type, byte Flag, byte Slot)
{
    public static PetItemInfoRecv Parse(byte[] p) => new(p[3], p[4], p[5]);
}

public static class QuestPacketBuilder
{
    /// <summary>PMSG_QUEST_INFO_SEND (Quest.h:39-44), C1:A0 -- respuesta a CGQuestInfoRecv y a
    /// GCQuestStateSend/NpcTalk (Quest.cpp:397-417,419-432: GCQuestStateSend siempre manda este
    /// paquete primero). <paramref name="totalCount"/> es <c>m_QuestInfo.size()</c> real (total de
    /// filas cargadas de Quest.txt, NO "1 misión disponible" -- ese hardcodeo de una pasada anterior
    /// quedaba fijo en 1 sin importar cuántas misiones hubiera realmente).</summary>
    public static byte[] QuestInfoSend(byte[] questBlob, int totalCount = 1)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)Math.Clamp(totalCount, 0, 255));
        w.WriteBytes(questBlob, 50);
        return PacketBuilder.BuildC1(0xA0, w.ToArray());
    }

    /// <summary>PMSG_QUEST_STATE_SEND (Quest.h:46-51), C1:A1 -- respuesta a CGQuestStateRecv.</summary>
    public static byte[] QuestStateSend(byte questIndex, byte questState)
    {
        var w = new PacketWriter();
        w.WriteByte(questIndex);
        w.WriteByte(questState);
        return PacketBuilder.BuildC1(0xA1, w.ToArray());
    }

    /// <summary>PMSG_QUEST_RESULT_SEND (Quest.h:53-59), C1:A2 -- respuesta al diálogo de Sebina.</summary>
    public static byte[] QuestResultSend(byte questIndex, byte questResult, byte questState)
    {
        var w = new PacketWriter();
        w.WriteByte(questIndex);
        w.WriteByte(questResult);
        w.WriteByte(questState);
        return PacketBuilder.BuildC1(0xA2, w.ToArray());
    }

    /// <summary>PMSG_QUEST_REWARD_SEND (Quest.h:61-70), C1:A3 -- puerto de CQuest::GCQuestRewardSend
    /// (Quest.cpp:449-471), llamado desde CQuestReward::InsertQuestReward por cada recompensa
    /// aplicada (POINT/CHANGE1/HERO/COMBO). <c>index</c> es el índice del propio jugador (no de la
    /// misión); <paramref name="rewardType"/>/<paramref name="amount"/> son <c>lpInfo.Index</c>
    /// (identifica QUÉ recompensa, ver <see cref="Config.QuestRewardType"/>) y el segundo parámetro
    /// de cada llamada real (Quantity para POINT/COMBO, la clase recién armada para CHANGE1, el punto
    /// calculado para HERO) respectivamente -- el original reusa el mismo paquete para los 4 tipos
    /// con semántica distinta en <c>QuestAmount</c> según el tipo, replicado tal cual acá.</summary>
    public static byte[] QuestRewardSend(int playerIndex, byte rewardType, byte amount, uint viewPoint)
    {
        var w = new PacketWriter();
        w.WriteByte((byte)((playerIndex >> 8) & 0xFF));
        w.WriteByte((byte)(playerIndex & 0xFF));
        w.WriteByte(rewardType);
        w.WriteByte(amount);
        // Tras header(3)+index[2]+QuestReward+QuestAmount vamos en el offset 7, y el DWORD ViewPoint
        // se alinea a 4 -> 1 byte de relleno (sizeof = 12, no 11).
        w.WriteByte(0); // relleno de alineación (offset 7 -> 8)
        w.WriteUInt32(viewPoint); // GAMESERVER_EXTRA==1 (siempre en este build, ver stdafx.h)
        return PacketBuilder.BuildC1(0xA3, w.ToArray());
    }

    /// <summary>PMSG_PET_ITEM_INFO_SEND (Protocol.h:462-470), C1:A9 -- eco de nivel/experiencia de un
    /// pet. Este puerto no trackea PetItemLevel/PetItemExp todavía (ningún item de pet real llega a
    /// spawnear con el balance actual), así que siempre contesta level=0/experience=0 -- suficiente
    /// para que el cliente real no se quede esperando una respuesta que nunca llega. Nota: el propio
    /// GCPetItemInfoSend original recibe un parámetro "durability" que NUNCA usa (no está en el
    /// struct del paquete, Protocol.cpp:1779-1796) -- se omite acá por la misma razón, no es una
    /// simplificación de este puerto.</summary>
    public static byte[] PetItemInfoSend(byte type, byte flag, byte slot)
    {
        var w = new PacketWriter();
        w.WriteByte(type);
        w.WriteByte(flag);
        w.WriteByte(slot);
        w.WriteByte(0); // level
        // Tras header(3)+type+flag+slot+level vamos en el offset 7, y el UINT experience se alinea a
        // 4 -> 1 byte de relleno (sizeof = 12, no 11).
        w.WriteByte(0); // relleno de alineación (offset 7 -> 8)
        w.WriteUInt32(0); // experience
        return PacketBuilder.BuildC1(0xA9, w.ToArray());
    }
}

// ---------------------------------------------------------------- NPCs y tiendas (primera pasada)
// Puerto de NpcTalk.h/.cpp (solo CGNpcTalkRecv/CGNpcTalkCloseRecv, sin quests ni las clases
// especiales de NPC -- Trainer/AngelKing/Charon/Warehouse/GuildMaster, ver README) + Shop.h/.cpp/
// ShopManager.h/.cpp (comprar/vender, sin Trade/Warehouse/PersonalShop todavía).

/// <summary>PMSG_NPC_TALK_RECV (NpcTalk.h:14-19), C1:30 -- index es el slot de <em>gObj[]</em> del
/// NPC (el mismo espacio de índices que usa MonsterRegistry/PlayerRegistry en este puerto, ver
/// comentario de Monster.ShopNumber), no un "número de tienda".</summary>
public sealed record NpcTalkRecv(int NpcIndex)
{
    public static NpcTalkRecv Parse(byte[] p) => new((p[3] << 8) | p[4]);
}

/// <summary>PMSG_ITEM_BUY_RECV (ItemManager.h:81-85), C1:32 -- slot dentro de la grilla de 8x15 de
/// la tienda actualmente abierta (PlayerObject.TargetShopNumber).</summary>
public sealed record ItemBuyRecv(byte Slot)
{
    public static ItemBuyRecv Parse(byte[] p) => new(p[3]);
}

/// <summary>PMSG_ITEM_SELL_RECV (ItemManager.h:87-91), C1:33 -- slot del INVENTARIO del jugador (no
/// de la tienda).</summary>
public sealed record ItemSellRecv(byte Slot)
{
    public static ItemSellRecv Parse(byte[] p) => new(p[3]);
}

public static class ShopPacketBuilder
{
    private static byte[] BuildC2NoSub(byte head, byte[] payload)
    {
        var buff = new byte[4 + payload.Length];
        int size = buff.Length;
        buff[0] = 0xC2;
        buff[1] = (byte)((size >> 8) & 0xFF);
        buff[2] = (byte)(size & 0xFF);
        buff[3] = head;
        payload.CopyTo(buff, 4);
        return buff;
    }

    /// <summary>PMSG_NPC_TALK_SEND (NpcTalk.h:21-25), C3:30 -- confirma que se puede hablar con el
    /// NPC. result=0 siempre en esta pasada (solo se portó la rama "tienda" de CNpcTalk::NpcTalk,
    /// que en el original manda result=0; las clases especiales -- Trainer/Charon/etc -- usan otros
    /// valores de result que este puerto no genera porque no las implementa).</summary>
    public static byte[] NpcTalkSend(byte result)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        return PacketBuilder.BuildC1(0x30, w.ToArray());
    }

    /// <summary>PMSG_SHOP_ITEM_LIST_SEND (Shop.h:19-30), C2:31 (¡ojo!: mismo valor de head que
    /// NpcTalkCloseRecv, pero es un paquete DISTINTO -- éste va server->client sin cifrar, C2 en vez
    /// de C1, namespace de heads independiente por dirección igual que el resto del protocolo). type
    /// siempre 0 en el original (no distingue variantes de tienda). count+repetido{slot,ItemInfo[5]}
    /// -- mismo layout de 6 bytes por entrada que ItemListSend.</summary>
    public static byte[] ShopItemListSend(ShopInfo shop)
    {
        using var body = new MemoryStream();
        body.WriteByte(0); // type
        var countPos = body.Position;
        body.WriteByte(0); // se corrige más abajo
        int count = 0;

        Span<byte> info = stackalloc byte[Item.WireByteSize];

        for (int slot = 0; slot < ShopInfo.Size; slot++)
        {
            var item = shop.Slots[slot];

            if (item == null || !item.IsItem())
            {
                continue;
            }

            item.ToWireBytes(info);
            body.WriteByte((byte)slot);
            body.Write(info);
            count++;
        }

        var bytes = body.ToArray();
        bytes[countPos] = (byte)count;

        return BuildC2NoSub(0x31, bytes);
    }

    /// <summary>PMSG_ITEM_BUY_SEND (ItemManager.h:151-157), C1:32 -- result=0xFF es el marcador de
    /// fallo (igual que CGItemBuyRecv del original: todas las validaciones fallidas mandan este mismo
    /// paquete con result=0xFF en vez de cerrar la conexión o no responder). result de éxito = el
    /// slot del inventario donde quedó el item.</summary>
    public static byte[] ItemBuySend(byte result, Item item)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        Span<byte> info = stackalloc byte[Item.WireByteSize];
        item.ToWireBytes(info);
        w.WriteBytes(info.ToArray(), Item.WireByteSize);
        return PacketBuilder.BuildC1(0x32, w.ToArray());
    }

    /// <summary>PMSG_ITEM_SELL_SEND (ItemManager.h:159-163), C1:33 -- result=0 fallo/1 éxito (igual
    /// que el resto de los booleanos de este protocolo), money=dinero TOTAL tras la venta (no el
    /// delta).</summary>
    public static byte[] ItemSellSend(byte result, uint money)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteUInt32(money);
        return PacketBuilder.BuildC1(0x33, w.ToArray());
    }
}

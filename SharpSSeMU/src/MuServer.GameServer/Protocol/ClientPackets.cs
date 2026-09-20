using MuServer.Shared.Protocol;

namespace MuServer.GameServer.Protocol;

/// <summary>Puerto de Protocol.h — solo los paquetes de login/desconexión necesarios para la Fase 1.</summary>
public sealed record ConnectAccountRecv(string Account, string Password, uint TickCount, byte[] ClientVersion, byte[] ClientSerial)
{
    /// <summary>Layout lógico ya sintetizado por GameClientFramer: C1,size,head,subh,account[10],password[10],TickCount(4),ClientVersion[5],ClientSerial[16].</summary>
    public static ConnectAccountRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // tras header(2)+head(1)+subh(1)
        var accountRaw = r.ReadBytes(10);
        var passwordRaw = r.ReadBytes(10);
        var tick = r.ReadUInt32();
        var version = r.ReadBytes(5);
        var serial = r.ReadBytes(16);

        var accountBuf = new byte[10];
        var passwordBuf = new byte[10];
        ClientArgumentCipher.Decrypt(accountBuf, accountRaw);
        ClientArgumentCipher.Decrypt(passwordBuf, passwordRaw);

        return new ConnectAccountRecv(
            PacketBuilder.ReadFixedString(accountBuf),
            PacketBuilder.ReadFixedString(passwordBuf),
            tick, version, serial);
    }
}

public sealed record CloseClientRecv(byte Type)
{
    public static CloseClientRecv Parse(byte[] p) => new(p[4]);
}

/// <summary>PMSG_CHARACTER_CREATE_RECV, C1:F3:01 -- name[10]+Class(1). Puerto de la pantalla de
/// creación de personaje (faltaba del todo hasta ahora: el cliente real la manda apenas el usuario
/// confirma nombre/clase en la pantalla de selección cuando la cuenta tiene 0 personajes, y sin
/// respuesta el cliente se queda esperando para siempre en esa pantalla).</summary>
public sealed record CharacterCreateRecv(string Name, byte Class)
{
    public static CharacterCreateRecv Parse(byte[] p)
    {
        var r = new PacketReader(p, 4); // C1Sub header: type,size,head,subh = 4 bytes
        var name = r.ReadFixedString(10);
        var cls = r.ReadByte();
        return new CharacterCreateRecv(name, cls);
    }
}

/// <summary>Puerto de PacketArgumentDecrypt (Util.cpp): XOR de 3 bytes aplicado a account/password dentro del login.</summary>
public static class ClientArgumentCipher
{
    private static readonly byte[] XorTable = { 0xFC, 0xCF, 0xAB };

    public static void Decrypt(byte[] outBuff, byte[] inBuff)
    {
        for (int n = 0; n < outBuff.Length && n < inBuff.Length; n++)
        {
            outBuff[n] = (byte)(inBuff[n] ^ XorTable[n % 3]);
        }
    }
}

public static class ClientPacketBuilder
{
    /// <summary>C1:F1:00 — primer paquete que manda el servidor al aceptar la conexión.</summary>
    public static byte[] ConnectClientSend(byte result, ushort index, byte[] serverVersion, ushort serverCode)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteByte((byte)((index >> 8) & 0xFF));
        w.WriteByte((byte)(index & 0xFF));
        w.WriteBytes(serverVersion, 5);
        w.WriteUInt16(serverCode); // GAMESERVER_EXTRA==1
        return PacketBuilder.BuildC1Sub(0xF1, 0x00, w.ToArray());
    }

    /// <summary>C1:F1:01 — resultado del login (1=ok, 0=cuenta/clave inválida, 2=ya conectada, 3=bloqueada, 6=version/serial inválido, etc).</summary>
    public static byte[] ConnectAccountSend(byte result)
    {
        return PacketBuilder.BuildC1Sub(0xF1, 0x01, new[] { result });
    }

    /// <summary>C1:F1:02</summary>
    public static byte[] CloseClientSend(byte result)
    {
        return PacketBuilder.BuildC1Sub(0xF1, 0x02, new[] { result });
    }

    /// <summary>PMSG_CHARACTER_LIST_SEND, C1:F3:00 -- lista de personajes de la cuenta para la
    /// pantalla de selección (puerto de DGCharacterListRecv, DSProtocol.cpp:181-365). ClassCode
    /// (habilita crear MG/DL según el nivel máximo alcanzado por algún personaje de la cuenta)
    /// queda fijo en 2 (ninguna clase extra desbloqueada) hasta que las tablas de balance
    /// m_MGCreateLevel/m_DLCreateLevel se porten -- no bloquea nada, solo no ofrece crear MG/DL
    /// todavía (igual que una cuenta nueva en el original).
    ///
    /// CORREGIDO (revirtiendo una "corrección" anterior basada en el árbol de fuente EQUIVOCADO --
    /// ver el doc-comment de <see cref="World.Item"/> para la explicación completa de por qué este
    /// repo tiene dos árboles de C++ y cuál es el real para este proyecto). El struct real,
    /// `Protocol.h` del árbol correcto (`Emulator 0.99 (2.1.7)/GameServer`), es:
    /// <c>PMSG_CHARACTER_LIST_SEND { header; BYTE ClassCode; BYTE MoveCnt; BYTE count; }</c> -- SIN
    /// ExtWarehouse (ese byte es de una temporada posterior con almacén extendido, no existe acá) --
    /// y cada entrada <c>PMSG_CHARACTER_LIST { BYTE slot; char Name[10]; WORD Level; BYTE CtlCode;
    /// BYTE CharSet[13]; }</c> -- CharSet real es de 13 bytes (no 18) y NO existe GuildStatus (sin
    /// guilds portado, y el original tampoco manda ese byte en este build). Sí es real el byte de
    /// relleno de alineación entre Name[10] y el WORD Level (el struct no tiene #pragma pack(1) en
    /// esta región del header, así que el compilador alinea el WORD a 2 bytes) -- eso fue lo único
    /// que de verdad hacía falta para el bug original ("solo se veía el primer personaje": con 2+
    /// personajes, la falta de este único byte de relleno desalineaba las entradas siguientes).</summary>
    public static byte[] CharacterListSend(byte moveCnt, IReadOnlyList<CharacterListItem> characters)
    {
        var w = new PacketWriter();
        w.WriteByte(2); // ClassCode
        w.WriteByte(moveCnt);
        w.WriteByte((byte)characters.Count);

        foreach (var c in characters)
        {
            // PMSG_CHARACTER_LIST (Protocol.h) NO tiene #pragma pack(1) -- el compilador alinea el
            // WORD Level a 2 bytes, insertando 1 byte de relleno tras slot(1)+Name[10] (offset 11,
            // impar) antes de Level. Mismo bug de alineación de struct que CharacterInfoSend/
            // DamageSend/LevelUpSend (ver comentarios ahí).
            w.WriteByte(c.Slot);
            w.WriteFixedString(c.Name, 10);
            w.WriteByte(0); // relleno de alineación (no existe en el struct, es padding del compilador)
            w.WriteUInt16(c.Level);
            w.WriteByte(c.CtlCode);
            w.WriteBytes(c.CharSet, 13);
        }

        return PacketBuilder.BuildC1Sub(0xF3, 0x00, w.ToArray());
    }

    /// <summary>PMSG_CHARACTER_CREATE_SEND, C1:F3:01 -- result(1)+name[10]+slot(1)+level(2)+
    /// Class(1)+equipment[24]+relleno(1) (40 bytes de cuerpo; sizeof = 44 con el header de 4, puerto
    /// exacto de Protocol.h). result: 0=falló
    /// (nombre inválido/prohibido/clase no disponible), 1=ok, 2=sin espacio (cuenta llena).</summary>
    public static byte[] CharacterCreateSend(byte result, string name, byte slot, ushort level, byte cls, byte[] equipment)
    {
        var w = new PacketWriter();
        w.WriteByte(result);
        w.WriteFixedString(name, 10);
        w.WriteByte(slot);
        w.WriteUInt16(level);
        w.WriteByte(cls);
        w.WriteBytes(equipment, 24);
        // Relleno de cola: el struct alinea a 2 (por el WORD level) y su último campo termina en el
        // offset 42, así que sizeof = 44, no 43. Los campos caen bien sin esto, pero el tamaño
        // declarado no coincide con el del original.
        w.WriteByte(0);
        return PacketBuilder.BuildC1Sub(0xF3, 0x01, w.ToArray());
    }
}

/// <summary>Un renglón de PMSG_CHARACTER_LIST_SEND ya en formato cliente (CharSet ya convertido
/// desde el formato compacto de DataServer -- ver PlayerObject.BuildCharSet).</summary>
public sealed record CharacterListItem(byte Slot, string Name, ushort Level, byte CtlCode, byte[] CharSet);

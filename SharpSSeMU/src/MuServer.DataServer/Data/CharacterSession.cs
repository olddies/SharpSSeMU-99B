namespace MuServer.DataServer.Data;

/// <summary>Puerto de CHARACTER_INFO: registro en memoria de qué personaje está online y en qué GameServer.</summary>
public sealed class CharacterSession
{
    public required string Name { get; init; }
    public required string Account { get; init; }
    public ushort UserIndex { get; init; }
    public ushort GameServerCode { get; init; }
}

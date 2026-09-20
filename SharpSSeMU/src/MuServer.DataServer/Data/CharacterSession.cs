namespace MuServer.DataServer.Data;

/// <summary>Port of CHARACTER_INFO: in-memory record of which character is online and on which GameServer.</summary>
public sealed class CharacterSession
{
    public required string Name { get; init; }
    public required string Account { get; init; }
    public ushort UserIndex { get; init; }
    public ushort GameServerCode { get; init; }
}

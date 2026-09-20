namespace MuServer.JoinServer.Data;

/// <summary>Puerto de ACCOUNT_INFO: estado en memoria de una cuenta actualmente logueada.</summary>
public sealed class AccountSession
{
    public required string Account { get; init; }
    public required string IpAddress { get; init; }
    public ushort UserIndex { get; init; }
    public ushort GameServerCode { get; init; }
    public bool MapServerMove { get; set; }
    public DateTime MapServerMoveTime { get; set; }
}

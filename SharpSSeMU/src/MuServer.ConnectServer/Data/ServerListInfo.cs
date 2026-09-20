namespace MuServer.ConnectServer.Data;

/// <summary>Equivalente a SERVER_LIST_INFO del ConnectServer original.</summary>
public sealed class ServerListInfo
{
    public required int ServerCode { get; init; }
    public required string ServerName { get; init; }
    public required string ServerAddress { get; init; }
    public required ushort ServerPort { get; init; }
    public required bool ServerShow { get; init; }

    public bool ServerState { get; set; }
    public DateTime ServerStateTime { get; set; }
    public uint UserCount { get; set; }
    public uint UserTotal { get; set; }
}

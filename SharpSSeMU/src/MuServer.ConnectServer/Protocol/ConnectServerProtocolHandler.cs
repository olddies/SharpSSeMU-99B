using MuServer.ConnectServer.Data;
using MuServer.ConnectServer.Net;
using MuServer.Shared.Protocol;

namespace MuServer.ConnectServer.Protocol;

/// <summary> Port of ConnectServerProtocolCore + CCServerXxxSend: processes the packets the client sends
/// (PMSG_SERVER_LIST_RECV / PMSG_SERVER_INFO_RECV) and builds the answers. </summary>
public sealed class ConnectServerProtocolHandler
{
    private readonly ServerListStore _serverList;

    public ConnectServerProtocolHandler(ServerListStore serverList)
    {
        _serverList = serverList;
    }

    /// <summary>PMSG_SERVER_INIT_SEND (C1:00) — sent as soon as the connection is accepted.</summary>
    public static byte[] BuildInitPacket(bool result) => PacketBuilder.BuildC1(0x00, new[] { (byte)(result ? 1 : 0) });

    /// <summary>PMSG_SERVER_NAME_LIST_SEND (C2 F3:EA) — sent as soon as the connection is accepted.</summary>
    public byte[] BuildNameListPacket() => _serverList.BuildServerNameListPacket();

    public async Task HandlePacketAsync(ClientSession session, byte[] packet, CancellationToken ct)
    {
        if (packet.Length < 3)
        {
            return;
        }

        byte head = packet[2];

        if (head != 0xF4)
        {
            return;
        }

        if (packet.Length < 4)
        {
            return;
        }

        byte subh = packet[3];

        switch (subh)
        {
            case 0x02: // PMSG_SERVER_LIST_RECV
                await session.SendAsync(_serverList.BuildServerListPacket(), ct);
                break;

            case 0x03: // PMSG_SERVER_INFO_RECV
                if (packet.Length < 5)
                {
                    return;
                }

                int serverCode = packet[4];
                var response = _serverList.BuildServerInfoPacket(serverCode);

                if (response != null)
                {
                    await session.SendAsync(response, ct);
                }
                break;
        }
    }
}

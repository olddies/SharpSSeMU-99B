using System;
using System.IO;
using System.Text;
using System.Net.Sockets;
using System.Runtime.InteropServices;

if (args.Length > 0 && args[0] == "testcs")
{
    Console.WriteLine("Connecting to ConnectServer 127.0.0.1:44405...");
    using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    await s.ConnectAsync("127.0.0.1", 44405);
    Console.WriteLine("Connected!");

    var buf = new byte[1024];
    int read = await s.ReceiveAsync(buf, SocketFlags.None);
    Console.WriteLine($"Received {read} bytes init: {Convert.ToHexString(buf, 0, read)}");

    // Request server list (C1 04 F4 02)
    var req = new byte[] { 0xC1, 0x04, 0xF4, 0x02 };
    await s.SendAsync(req, SocketFlags.None);
    Console.WriteLine("Sent 0xF4:02 server list request");

    read = await s.ReceiveAsync(buf, SocketFlags.None);
    Console.WriteLine($"Received {read} bytes list: {Convert.ToHexString(buf, 0, read)}");

    s.Close();
    return 0;
}

Console.WriteLine("Done");
return 0;

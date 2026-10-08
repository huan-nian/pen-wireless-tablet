using System;
using System.Net;
using System.Net.Sockets;

namespace PenReceiver;

public class UdpPenReceiver
{
    private const uint Magic = 0x50454E31;
    private const int Port = 8888;

    private readonly PenInjector _injector;

    public UdpPenReceiver(PenInjector injector)
    {
        _injector = injector;
    }

    public void Run()
    {
        var udp = new UdpClient(Port);
        udp.Client.ReceiveBufferSize = 1024 * 64;

        Console.WriteLine($"监听 UDP 端口 {Port}，等待平板连接...");
        Console.WriteLine("按 Ctrl + C 退出。");

        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (true)
        {
            try
            {
                var data = udp.Receive(ref remote);
                Console.WriteLine($"收到来自 {remote} 的数据，长度 {data.Length}");

                if (data.Length < 32) continue;

                uint magic = BitConverter.ToUInt32(data, 0);
                if (magic != Magic) continue;

                int action = BitConverter.ToInt32(data, 4);
                float nx = BitConverter.ToSingle(data, 8);
                float ny = BitConverter.ToSingle(data, 12);
                float pressure = BitConverter.ToSingle(data, 16);
                float tilt = BitConverter.ToSingle(data, 20);
                float orientation = BitConverter.ToSingle(data, 24);

                _injector.Inject(action, nx, ny, pressure, tilt, orientation);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"接收异常: {ex.Message}");
            }
        }
    }
}
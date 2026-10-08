using System;
using System.Buffers.Binary;
using System.Net;

namespace PenReceiver;

/// <summary>笔动作。与 Android 端 PenProtocol.Action 必须保持一致。</summary>
public enum PenAction
{
    Down = 0,
    Move = 1,
    Up = 2,
    Hover = 3,
}

/// <summary>
/// 线协议：定长报文、小端序。与 Android 端 PenProtocol.kt 一一对应，改一处必须改两处。
///
/// PEN 报文（36 字节）
///   0  uint32 magic = 0x504E4232 "PNB2"
///   4  uint32 seq
///   8  int32  action
///  12  float  x        归一化 [0,1]
///  16  float  y        归一化 [0,1]
///  20  float  pressure [0,1]
///  24  float  tiltX    度
///  28  float  tiltY    度
///  32  int32  flags    位 0 = 橡皮端
///
/// HELLO 报文（8 字节）magic = 0x504E4230 "PNB0"，用于 Android 端扫描发现本机。
/// REPLY 报文（12 字节）magic = 0x504E4231 "PNB1"，本机对探测的回复，携带端口。
/// </summary>
public static class PenProtocol
{
    public const uint MagicPen = 0x504E4232;
    public const uint MagicHello = 0x504E4230;
    public const uint MagicReply = 0x504E4231;

    public const int PenPacketSize = 36;
    public const int HelloPacketSize = 8;
    public const int ReplyPacketSize = 12;

    public const int FlagEraser = 1;

    /// <summary>端口探测/回复固定的目标端口，Android 端写死在 8888 上。</summary>
    public const int DefaultPort = 8888;

    public static bool TryParsePen(ReadOnlySpan<byte> data, out PenPacket packet)
    {
        packet = default;
        if (data.Length < PenPacketSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != MagicPen) return false;

        packet = new PenPacket
        {
            Seq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)),
            Action = (PenAction)BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8)),
            X = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(12)),
            Y = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(16)),
            Pressure = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(20)),
            TiltX = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(24)),
            TiltY = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(28)),
            Flags = (PenFlags)BinaryPrimitives.ReadInt32LittleEndian(data.Slice(32)),
        };
        return true;
    }

    public static bool IsHello(ReadOnlySpan<byte> data)
    {
        return data.Length >= HelloPacketSize &&
               BinaryPrimitives.ReadUInt32LittleEndian(data) == MagicHello;
    }

    /// <summary>构造对探测报文的回复，告诉 Android 端本机在监听哪个端口。</summary>
    public static byte[] BuildReply(int port)
    {
        var buffer = new byte[ReplyPacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MagicReply);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), port);
        return buffer;
    }

    /// <summary>构造一个 PEN 报文。主要供自检使用，保证与 Android 端字节级一致。</summary>
    public static byte[] BuildPen(
        uint seq, PenAction action, float x, float y,
        float pressure, float tiltX, float tiltY, bool eraser, bool rightClick = false)
    {
        var buffer = new byte[PenPacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MagicPen);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), seq);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), (int)action);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(12), x);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(16), y);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(20), pressure);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(24), tiltX);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(28), tiltY);

        var flags = PenFlags.None;
        if (eraser) flags |= PenFlags.Eraser;
        if (rightClick) flags |= PenFlags.RightClick;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(32), (int)flags);
        return buffer;
    }

    /// <summary>构造一个探测报文，供自检/连通性测试使用。</summary>
    public static byte[] BuildHello()
    {
        var buffer = new byte[HelloPacketSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MagicHello);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0);
        return buffer;
    }

    /// <summary>本机所有活动网卡的 IPv4 地址，用于在界面上提示「填这个地址」。</summary>
    public static string DescribeLocalAddresses()
    {
        var parts = new System.Collections.Generic.List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var text = info.Address.ToString();
                    if (text.StartsWith("169.254.")) continue; // 自动私有地址，不可用
                    parts.Add(text);
                }
            }
        }
        catch
        {
            // 取不到网卡信息不影响功能，界面上留空即可
        }

        return parts.Count == 0 ? "（未检测到局域网地址）" : string.Join(" / ", parts);
    }
}

[Flags]
public enum PenFlags
{
    None = 0,

    /// <summary>笔尾橡皮。</summary>
    Eraser = 1,

    /// <summary>
    /// 平板端检测到「笔尖静止长按」，要求在这一笔结束时于该位置弹出右键菜单。
    ///
    /// 为什么不让系统自己识别：实测发现注入的笔输入**不会**触发 Windows 的长按
    /// 右键手势（笔按下静止 1.1 秒也不产生右键），所以必须由平板端识别、显式注入。
    /// </summary>
    RightClick = 2,
}

/// <summary>一个解析后的笔事件。</summary>
public struct PenPacket
{
    public uint Seq;
    public PenAction Action;
    public float X;
    public float Y;
    public float Pressure;
    public float TiltX;
    public float TiltY;
    public PenFlags Flags;

    public bool IsEraser => (Flags & PenFlags.Eraser) != 0;

    public bool IsRightClick => (Flags & PenFlags.RightClick) != 0;
}

/// <summary>UDP 收到的一个数据报。</summary>
public readonly struct PenDatagram
{
    public PenDatagram(PenPacket packet, IPEndPoint source)
    {
        Packet = packet;
        Source = source;
    }

    public PenPacket Packet { get; }
    public IPEndPoint Source { get; }
}

using System;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PenReceiver;

/// <summary>
/// 无界面自检：验证协议解析、坐标映射、UDP 收发与统计计数。
///
/// 不注入任何输入，所以**不需要管理员权限**，可以在远程会话或 CI 里跑：
///   PenReceiver.exe --selftest
/// 退出码 0 表示全部通过。真实笔输入的注入需要管理员，这一层无法在自检里覆盖。
/// </summary>
internal static class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run(string[] args)
    {
        var port = ParsePort(args) ?? FindFreePort();

        Console.WriteLine("=== 无线手写板 · 接收端自检 ===");
        Console.WriteLine($"使用端口 {port}（本机回环，不注入任何输入）");
        Console.WriteLine();

        TestProtocolRoundTrip();
        TestMappingStretch();
        TestMappingAspectFit();
        TestTiltSign();
        TestPressureScaling();
        TestLongPressGate();
        TestProtocolRoundTripRightClick();
        TestCommandProtocol();
        TestCommandKeyMapping();
        TestUdpPipeline(port);
        TestHelloProbe(port);

        Console.WriteLine();
        Console.WriteLine($"结果：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 用例

    private static void TestProtocolRoundTrip()
    {
        var bytes = PenProtocol.BuildPen(
            seq: 42, action: PenAction.Move, x: 0.25f, y: 0.75f,
            pressure: 0.5f, tiltX: -12f, tiltY: 34f, eraser: true);

        Check("报文长度固定 36 字节", bytes.Length == PenProtocol.PenPacketSize, $"实际 {bytes.Length}");
        Check("magic 为 PNB2", PenProtocol.TryParsePen(bytes, out var packet));
        Check("序号解析正确", packet.Seq == 42, $"实际 {packet.Seq}");
        Check("动作解析正确", packet.Action == PenAction.Move, $"实际 {packet.Action}");
        Check("X 坐标解析正确", Math.Abs(packet.X - 0.25f) < 1e-6, $"实际 {packet.X}");
        Check("Y 坐标解析正确", Math.Abs(packet.Y - 0.75f) < 1e-6, $"实际 {packet.Y}");
        Check("压力解析正确", Math.Abs(packet.Pressure - 0.5f) < 1e-6, $"实际 {packet.Pressure}");
        Check("倾斜角解析正确", Math.Abs(packet.TiltX + 12f) < 1e-6 && Math.Abs(packet.TiltY - 34f) < 1e-6,
            $"实际 {packet.TiltX}/{packet.TiltY}");
        Check("橡皮标记解析正确", packet.IsEraser);

        var shortPacket = new byte[PenProtocol.PenPacketSize - 1];
        Check("长度不足时报文被拒绝", !PenProtocol.TryParsePen(shortPacket, out _));
    }

    private static void TestMappingStretch()
    {
        var injector = CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 1920, 1080));

        var topLeft = injector.MapToScreen(0f, 0f);
        var bottomRight = injector.MapToScreen(1f, 1f);
        var center = injector.MapToScreen(0.5f, 0.5f);

        Check("铺满模式：左上角映射到 (0,0)", topLeft is { X: 0, Y: 0 }, $"实际 {topLeft}");
        Check("铺满模式：右下角映射到屏幕边界内", bottomRight is { X: 1919, Y: 1079 }, $"实际 {bottomRight}");
        Check("铺满模式：中心点居中", center is { X: 960, Y: 540 }, $"实际 {center}");

        injector.Dispose();
    }

    private static void TestMappingAspectFit()
    {
        var injector = CreateUnboundInjector(MappingMode.AspectFit, 1.6, new Rectangle(0, 0, 1920, 1080));

        var topLeft = injector.MapToScreen(0f, 0f);
        var bottomRight = injector.MapToScreen(1f, 1f);
        var center = injector.MapToScreen(0.5f, 0.5f);

        // 平板 16:10（1.6）比 16:9（1.778）更「方」，所以是左右留边、高度铺满。
        // 可用像素跨度是 1919×1079，内接矩形按比例算出宽度约 1726，左右各留约 96。
        const int spanX = 1919;
        const int spanY = 1079;
        var expectedWidth = (int)Math.Round(spanY * 1.6);          // 1726
        var expectedMargin = (spanX - expectedWidth) / 2;          // 96

        Check("等比模式：左右留边、高度铺满", topLeft.X == expectedMargin && topLeft.Y == 0, $"实际 {topLeft}");
        Check("等比模式：左右留边对称",
            topLeft.X == 1919 - bottomRight.X, $"左 {topLeft.X} 右 {1919 - bottomRight.X}");
        Check("等比模式：中心仍然是屏幕中心", center is { X: 960, Y: 540 }, $"实际 {center}");
        Check("等比模式：右下角仍在屏幕内", bottomRight.X <= 1919 && bottomRight.Y == 1079, $"实际 {bottomRight}");
        Check("等比模式：内接矩形宽度符合像素跨度高 × 平板比例（允许取整误差）",
            Math.Abs((bottomRight.X - topLeft.X + 1) - expectedWidth) <= 2,
            $"期望 {expectedWidth}±2，实际 {bottomRight.X - topLeft.X + 1}");

        injector.Dispose();
    }

    private static void TestTiltSign()
    {
        var injector = CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 100, 100));

        // 通过反射读回 BuildInfo 的结果太重，这里改为验证公开展示的换算契约：
        // Android 的 tiltX 正方向与 Windows 一致，tiltY 相反，注入时取反。
        var packet = new PenPacket { X = 0.5f, Y = 0.5f, TiltX = 20f, TiltY = -30f, Action = PenAction.Down };
        var info = injector.BuildInfoForTest(packet, PenAction.Down, 0.5f);

        Check("倾斜 TiltX 直接透传", info.TiltX == 20, $"实际 {info.TiltX}");
        Check("倾斜 TiltY 取反", info.TiltY == 30, $"实际 {info.TiltY}");
        Check("压力满量程按 1023 映射", info.Pressure == 512, $"实际 {info.Pressure}");
        Check("按下时带 InContact", info.PointerInfo.PointerOptions.HasFlag(
            Windows.UI.Input.Preview.Injection.InjectedInputPointerOptions.InContact));

        injector.Dispose();
    }

    /// <summary>
    /// 压力缩放必须跟着**实测出来的上界**走。
    ///
    /// 这一条来自一个真实故障：原来写死 0..1023，而系统只接受 0..1，
    /// 于是每次注入都被整包拒绝，表现为「平板上写得出来、电脑上毫无反应」。
    /// </summary>
    private static void TestPressureScaling()
    {
        var tablet = new PenPacket { X = 0.5f, Y = 0.5f, Action = PenAction.Down };

        // 满量程 1023 的机器：0.5 应该映射到 512
        var full = CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 100, 100), maxPressure: 1023);
        Check("上界 1023 时 0.5 → 512",
            full.BuildInfoForTest(tablet, PenAction.Down, 0.5f).Pressure == 512,
            $"实际 {full.BuildInfoForTest(tablet, PenAction.Down, 0.5f).Pressure}");
        Check("上界 1023 时判定为支持压感", full.PressureSupported);
        full.Dispose();

        // 只接受 0/1 的机器：绝不能算出 512，否则注入会被拒绝
        var binary = CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 100, 100), maxPressure: 1);
        Check("上界 1 时 0.5 → 1（阈值判定，不会算出超界的 512，也不会舍成 0）",
            binary.BuildInfoForTest(tablet, PenAction.Down, 0.5f).Pressure == 1,
            $"实际 {binary.BuildInfoForTest(tablet, PenAction.Down, 0.5f).Pressure}");
        Check("上界 1 时 0.02 → 1",
            binary.BuildInfoForTest(tablet, PenAction.Down, 0.02f).Pressure == 1,
            $"实际 {binary.BuildInfoForTest(tablet, PenAction.Down, 0.02f).Pressure}");
        Check("上界 1 时 0.005（近似无压力）→ 0",
            binary.BuildInfoForTest(tablet, PenAction.Down, 0.005f).Pressure == 0,
            $"实际 {binary.BuildInfoForTest(tablet, PenAction.Down, 0.005f).Pressure}");
        Check("上界 1 时 1.0 → 1",
            binary.BuildInfoForTest(tablet, PenAction.Down, 1f).Pressure == 1,
            $"实际 {binary.BuildInfoForTest(tablet, PenAction.Down, 1f).Pressure}");
        Check("上界 1 时 0.0 → 0",
            binary.BuildInfoForTest(tablet, PenAction.Down, 0f).Pressure == 0,
            $"实际 {binary.BuildInfoForTest(tablet, PenAction.Down, 0f).Pressure}");
        Check("上界 1 时判定为不支持压感（只能表达有/无）", !binary.PressureSupported);
        binary.Dispose();

        // 上界为 0：压感完全不可用时也要保证不超界
        var none = CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 100, 100), maxPressure: 0);
        Check("上界 0 时压力恒为 0",
            none.BuildInfoForTest(tablet, PenAction.Down, 1f).Pressure == 0,
            $"实际 {none.BuildInfoForTest(tablet, PenAction.Down, 1f).Pressure}");
        none.Dispose();
    }

    /// <summary>长按（= 右键）的状态机。</summary>
    private static void TestLongPressGate()
    {
        var gate = new LongPressGate();
        var down = new PenPacket { Action = PenAction.Down, X = 0.4f, Y = 0.6f };
        var move = new PenPacket { Action = PenAction.Move, X = 0.41f, Y = 0.6f };
        var up = new PenPacket { Action = PenAction.Up, X = 0.41f, Y = 0.6f };
        var longDown = new PenPacket
        {
            Action = PenAction.Down, X = 0.4f, Y = 0.6f,
            Flags = PenFlags.RightClick,
        };
        var longUp = new PenPacket
        {
            Action = PenAction.Up, X = 0.4f, Y = 0.6f,
            Flags = PenFlags.RightClick,
        };

        Check("普通笔按下照常注入", gate.Decide(down) == StrokeDecision.Inject);
        Check("普通笔移动照常注入", gate.Decide(move) == StrokeDecision.Inject);
        Check("普通笔抬起照常注入", gate.Decide(up) == StrokeDecision.Inject);
        Check("普通笔不进入丢弃状态", !gate.IsSuppressing);

        Check("长按按下被丢弃（不留墨点）", gate.Decide(longDown) == StrokeDecision.Suppress);
        Check("长按期间处于丢弃状态", gate.IsSuppressing);
        Check("长按期间的移动也被丢弃", gate.Decide(move) == StrokeDecision.Suppress);
        Check("长按抬起触发右键", gate.Decide(longUp) == StrokeDecision.RightClick);
        Check("触发右键后丢弃状态被清除", !gate.IsSuppressing);
        Check("右键之后恢复正常注入", gate.Decide(down) == StrokeDecision.Inject);

        // 丢包场景：长按的抬起丢了，之后普通笔的抬起要能收尾
        gate.Decide(longDown);
        Check("长按抬起丢失后，后续抬起能收尾",
            gate.Decide(up) == StrokeDecision.Suppress && !gate.IsSuppressing);

        gate.Reset();
        Check("Reset 后恢复正常", gate.Decide(down) == StrokeDecision.Inject);
    }

    private static void TestProtocolRoundTripRightClick()    {
        var bytes = PenProtocol.BuildPen(
            seq: 7, action: PenAction.Up, x: 0.5f, y: 0.5f,
            pressure: 0f, tiltX: 0f, tiltY: 0f, eraser: false, rightClick: true);

        Check("右键标记能往返", PenProtocol.TryParsePen(bytes, out var packet) && packet.IsRightClick);

        var plain = PenProtocol.BuildPen(
            seq: 8, action: PenAction.Up, x: 0.5f, y: 0.5f,
            pressure: 0f, tiltX: 0f, tiltY: 0f, eraser: false);
        Check("普通报文不会被误判为右键",
            PenProtocol.TryParsePen(plain, out var p2) && !p2.IsRightClick);

        var eraser = PenProtocol.BuildPen(
            seq: 9, action: PenAction.Move, x: 0.5f, y: 0.5f,
            pressure: 0f, tiltX: 0f, tiltY: 0f, eraser: true, rightClick: true);
        Check("橡皮与右键标记可以共存",
            PenProtocol.TryParsePen(eraser, out var p3) && p3.IsEraser && p3.IsRightClick);
    }

    /// <summary>系统命令报文的编解码。</summary>
    private static void TestCommandProtocol()
    {
        foreach (var command in new[]
        {
            SystemCommand.ShowDesktop, SystemCommand.TaskView, SystemCommand.Save,
            SystemCommand.Undo, SystemCommand.Redo, SystemCommand.ScrollUp, SystemCommand.ScrollDown,
        })
        {
            var bytes = PenProtocol.BuildCommand(command);
            Check($"{command} 报文长度固定 {PenProtocol.CommandPacketSize} 字节",
                bytes.Length == PenProtocol.CommandPacketSize, $"实际 {bytes.Length}");
            Check($"{command} 能往返解析",
                PenProtocol.TryParseCommand(bytes, out var parsed) && parsed == command);
        }

        // 编号越界的报文必须被拒绝，否则会执行到未定义的操作
        var bogus = new byte[PenProtocol.CommandPacketSize];
        BitConverter.GetBytes(PenProtocol.MagicCommand).CopyTo(bogus, 0);
        BitConverter.GetBytes(999).CopyTo(bogus, 4);
        Check("越界命令编号被拒绝", !PenProtocol.TryParseCommand(bogus, out _));

        // 命令报文不能被笔事件解析器误认
        var commandBytes = PenProtocol.BuildCommand(SystemCommand.Undo);
        Check("命令报文不会被当成笔事件", !PenProtocol.TryParsePen(commandBytes, out _));
    }

    /// <summary>
    /// 命令 → 快捷键的映射。
    ///
    /// 这是最容易写错、也最难在运行中发现的一层：按错一个键可能悄悄触发别的操作。
    /// MapCommand 是纯函数，所以可以在没有注入权限的环境里直接断言。
    /// </summary>
    private static void TestCommandKeyMapping()
    {
        void Expect(string name, SystemCommand command, VirtualKey modifier, VirtualKey key)
        {
            var chord = SystemCommandInjector.MapCommand(command);
            Check(name,
                chord is { } c && c.Modifier == modifier && c.Key == key,
                $"实际 {chord?.ToString() ?? "null"}");
        }

        Expect("桌面 = Win + D", SystemCommand.ShowDesktop, VirtualKey.LeftWindows, VirtualKey.D);
        Expect("多任务 = Win + Tab", SystemCommand.TaskView, VirtualKey.LeftWindows, VirtualKey.Tab);
        Expect("保存 = Ctrl + S", SystemCommand.Save, VirtualKey.Control, VirtualKey.S);
        Expect("撤销 = Ctrl + Z", SystemCommand.Undo, VirtualKey.Control, VirtualKey.Z);
        Expect("取消撤销 = Ctrl + Y", SystemCommand.Redo, VirtualKey.Control, VirtualKey.Y);

        // 滚轮不是按键命令，必须返回 null，否则 Execute 会走进按键分支注入无意义的键
        Check("上滚不是按键命令", SystemCommandInjector.MapCommand(SystemCommand.ScrollUp) is null);
        Check("下滚不是按键命令", SystemCommandInjector.MapCommand(SystemCommand.ScrollDown) is null);
        Check("None 不是按键命令", SystemCommandInjector.MapCommand(SystemCommand.None) is null);

        // 映射表必须覆盖协议里所有「按键类」命令，漏一个就会在运行时才报未知命令
        var keyCommands = new[]
        {
            SystemCommand.ShowDesktop, SystemCommand.TaskView, SystemCommand.Save,
            SystemCommand.Undo, SystemCommand.Redo,
        };
        foreach (var command in keyCommands)
        {
            Check($"{command} 有快捷键映射", SystemCommandInjector.MapCommand(command) is not null);
        }

        Check("滚轮一格 = 120", SystemCommandInjector.WheelNotch == 120,
            $"实际 {SystemCommandInjector.WheelNotch}");
    }

    private static void TestUdpPipeline(int port)
    {
        using var session = new PenSession(CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 1920, 1080)));
        session.InjectionEnabled = false;
        session.Start(port);

        Thread.Sleep(100); // 等接收线程真正 bind 好

        using var sender = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, port);

        var count = 0;
        const int moves = 20;
        Send(sender, target, ref count, PenAction.Hover, 0.10f, 0.10f, 0f, 0f, 0f, false);
        Send(sender, target, ref count, PenAction.Down, 0.10f, 0.10f, 0.42f, 5f, 0f, false);
        for (var i = 1; i <= moves; i++)
        {
            Send(sender, target, ref count, PenAction.Move, 0.10f + i * 0.02f, 0.20f, 0.5f, 0f, 0f, false);
        }
        Send(sender, target, ref count, PenAction.Up, 0.50f, 0.20f, 0f, 0f, 0f, false);

        // 第二笔用橡皮，最后一段故意把序号跳过一格来模拟丢包
        Send(sender, target, ref count, PenAction.Down, 0.30f, 0.30f, 0.3f, 0f, 0f, true);
        count++; // 跳过的序号：模拟这个报文在路上丢了
        Send(sender, target, ref count, PenAction.Up, 0.30f, 0.30f, 0f, 0f, 0f, false);

        var expectedPackets = moves + 5; // hover + down + moves + up + down + up
        Thread.Sleep(350);
        var stats = WaitForPackets(session, expectedPackets, 2000);

        Check("UDP 报文全部收到", stats.Packets == expectedPackets,
            $"期望 {expectedPackets}，实际 {stats.Packets}");
        Check("没有解析失败的报文", stats.InvalidPackets == 0, $"实际 {stats.InvalidPackets}");
        Check("识别到序号缺口", stats.SequenceGaps >= 1, $"实际 {stats.SequenceGaps}");
        Check("记录了来源地址", stats.LastSender == "127.0.0.1", $"实际 {stats.LastSender}");

        // 来源限制：允许列表设成别的地址后，报文应该被拒收
        session.AllowedSender = "10.0.0.1";
        Send(sender, target, ref count, PenAction.Move, 0.4f, 0.4f, 0.5f, 0f, 0f, false);
        Thread.Sleep(200);
        var restricted = session.SnapshotForTest();
        Check("来源限制生效", restricted.RejectedSenders >= 1, $"实际 {restricted.RejectedSenders}");

        // 系统命令报文：走同一条 UDP 通道，但统计在单独的计数器上，
        // 不能混进笔事件计数（否则界面的「数据速率」会被按钮点击污染）
        session.AllowedSender = null;
        var commandBytes = PenProtocol.BuildCommand(SystemCommand.Undo);
        sender.Send(commandBytes, commandBytes.Length, target);
        Thread.Sleep(200);

        var withCommand = session.SnapshotForTest();
        Check("命令报文被单独计数", withCommand.Commands >= 1, $"实际 {withCommand.Commands}");
        Check("命令报文不混入笔事件计数", withCommand.Packets == expectedPackets,
            $"期望 {expectedPackets}，实际 {withCommand.Packets}");

        session.Stop();
    }

    private static void TestHelloProbe(int port)
    {
        using var session = new PenSession(CreateUnboundInjector(MappingMode.Stretch, 1.6, new Rectangle(0, 0, 100, 100)));
        session.InjectionEnabled = false;
        session.Start(port + 1);
        Thread.Sleep(100);

        using var probe = new UdpClient();
        probe.Client.ReceiveTimeout = 1500;
        var hello = PenProtocol.BuildHello();
        probe.Send(hello, hello.Length, new IPEndPoint(IPAddress.Loopback, port + 1));

        var replyReceived = false;
        var advertisedPort = -1;
        try
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var data = probe.Receive(ref remote);
            if (data.Length >= PenProtocol.ReplyPacketSize &&
                BitConverter.ToUInt32(data, 0) == PenProtocol.MagicReply)
            {
                replyReceived = true;
                advertisedPort = BitConverter.ToInt32(data, 8);
            }
        }
        catch (SocketException)
        {
            // 超时即视为失败
        }

        Check("探测报文收到回复", replyReceived);
        Check("回复里带上了监听端口", advertisedPort == port + 1, $"实际 {advertisedPort}");

        session.Stop();
    }

    // ------------------------------------------------------------------ 工具

    private static void Send(UdpClient sender, IPEndPoint target, ref int count,
        PenAction action, float x, float y, float pressure, float tiltX, float tiltY, bool eraser)
    {
        var bytes = PenProtocol.BuildPen((uint)count, action, x, y, pressure, tiltX, tiltY, eraser);
        sender.Send(bytes, bytes.Length, target);
        count++;
    }

    private static ReceiverStats WaitForPackets(PenSession session, long expected, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var stats = session.SnapshotForTest();
        while (stats.Packets < expected && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(50);
            stats = session.SnapshotForTest();
        }
        return stats;
    }

    /// <summary>
    /// 建一个不依赖真实注入器的检查实例。映射与协议换算不需要权限，
    /// 所以这里绕过 InputInjector，只测纯计算部分。
    /// </summary>
    private static PenInjector CreateUnboundInjector(MappingMode mode, double aspect, Rectangle bounds,
        int maxPressure = 1023)
    {
        return PenInjector.CreateUnbound(new DisplayTarget("selftest", bounds, true), mode, aspect, maxPressure);
    }

    private static int? ParsePort(string[] args)
    {
        var index = Array.IndexOf(args, "--port");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var port))
        {
            return port;
        }
        return null;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name}{(detail is null ? string.Empty : $"（{detail}）")}");
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PenReceiver;

/// <summary>
/// 把接收、注入、兜底三件事串起来的一层。
///
/// 「兜底」是这一版最重要的稳定性改动：UDP 丢包时最后那个「抬起」报文可能永远到不了，
/// 结果 Windows 上笔尖会一直按着，整个桌面被拖着画。这里的做法是——只要超过
/// <see cref="StuckTimeoutMs"/> 毫秒没有收到任何笔报文，就主动补一次抬起。
/// </summary>
public sealed class PenSession : IDisposable
{
    /// <summary>多久没收到笔报文就认为「抬起丢了」。取 400ms 是为了不误伤正常的长停顿。</summary>
    public const int StuckTimeoutMs = 400;

    private readonly object _sync = new();
    private readonly PenInjector _injector;
    private readonly UdpPenReceiver _receiver;
    private readonly Timer _watchdog;
    private readonly Timer _uiTicker;
    private readonly UdpClient _probeReplier = new();

    private DateTime _lastPacketAtUtc = DateTime.UtcNow;
    private long _forcedUps;
    private long _lastUiPackets;
    private DateTime _lastUiAt = DateTime.UtcNow;
    private double _uiRate;
    private bool _enabled = true;
    private IPEndPoint? _lastSource;
    private bool _disposed;

    /// <summary>长按（= 右键）的状态判定。</summary>
    private readonly LongPressGate _longPress = new();

    public PenSession(PenInjector injector)
    {
        _injector = injector;
        _receiver = new UdpPenReceiver();
        _receiver.PenReceived += OnPenReceived;
        _receiver.ProbeReceived += OnProbeReceived;

        _watchdog = new Timer(_ => WatchdogTick(), null, 100, 100);
        _uiTicker = new Timer(_ => UiTick(), null, 500, 250);
    }

    /// <summary>是否把收到的笔事件真正注入到系统。关掉后仍然统计，便于排查。 </summary>
    public bool InjectionEnabled
    {
        get { lock (_sync) return _enabled; }
        set
        {
            lock (_sync)
            {
                _enabled = value;
                if (!value) _injector.ForcePenUp();
            }
        }
    }

    /// <summary>只接受这个来源 IP 的报文。null 表示不过滤。</summary>
    public string? AllowedSender
    {
        get => _receiver.AllowedSender;
        set => _receiver.AllowedSender = value;
    }

    public bool IsRunning => _receiver.IsRunning;
    public int Port => _receiver.Port;

    /// <summary>界面刷新回调，大约 4 次/秒。回调在计时器线程。</summary>
    public event Action<PenSessionStatus>? StatusChanged;

    /// <summary>最近的注入错误，供界面提示。</summary>
    public string? LastInjectionError { get; private set; }

    public void Start(int port)
    {
        _receiver.Start(port);
        _lastPacketAtUtc = DateTime.UtcNow;
        _lastUiPackets = 0;
        _lastUiAt = DateTime.UtcNow;
    }

    public void Stop()
    {
        _receiver.Stop();
        _injector.ForcePenUp();
        _longPress.Reset();
    }

    /// <summary>供自检读取当前统计。</summary>
    public ReceiverStats SnapshotForTest() => _receiver.Snapshot();

    private void OnPenReceived(PenDatagram datagram)
    {
        _lastPacketAtUtc = DateTime.UtcNow;
        _lastSource = datagram.Source;

        lock (_sync)
        {
            if (!_enabled) return;
        }

        var packet = datagram.Packet;

        // 「长按 = 右键」的状态判定。长按那一笔的笔迹要整体丢弃，
        // 否则长按过程会先在屏幕上留下一个墨点。
        switch (_longPress.Decide(packet))
        {
            case StrokeDecision.RightClick:
                // 必须先把按下的笔解开，再弹右键；否则笔还按着，菜单收不到点击
                _injector.ForcePenUp();
                _injector.InjectRightClick(packet.X, packet.Y);
                return;

            case StrokeDecision.Suppress:
                return;
        }

        try
        {
            _injector.Inject(packet);
            LastInjectionError = null;
        }
        catch (Exception ex)
        {
            LastInjectionError = ex.Message;
        }
    }

    /// <summary>回应 Android 端的扫描探测，让对方能自动发现本机地址与端口。</summary>
    private void OnProbeReceived(IPEndPoint source, int port)
    {
        try
        {
            var reply = PenProtocol.BuildReply(port);
            _probeReplier.Send(reply, reply.Length, source);
        }
        catch (Exception)
        {
            // 探测回复失败不影响正常书写
        }
    }

    private void WatchdogTick()
    {
        if (_disposed) return;

        lock (_sync)
        {
            if (!_enabled) return;
        }

        if (!_injector.IsPenDown) return;

        var idleMs = (DateTime.UtcNow - _lastPacketAtUtc).TotalMilliseconds;
        if (idleMs < StuckTimeoutMs) return;

        // 笔还按着但已经很久没有报文：判定「抬起」丢了，主动补一刀
        if (_injector.ForcePenUp())
        {
            Interlocked.Increment(ref _forcedUps);
        }
    }

    private void UiTick()
    {
        var handler = StatusChanged;
        if (handler == null) return;

        var stats = _receiver.Snapshot();
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastUiAt).TotalSeconds;
        if (elapsed >= 0.25)
        {
            _uiRate = (stats.Packets - _lastUiPackets) / elapsed;
            _lastUiPackets = stats.Packets;
            _lastUiAt = now;
        }

        handler(new PenSessionStatus
        {
            IsRunning = IsRunning,
            Port = Port,
            Stats = stats,
            Rate = _uiRate,
            PenDown = _injector.IsPenDown,
            ForcedUps = Interlocked.Read(ref _forcedUps),
            LastSender = _lastSource?.Address.ToString(),
            LastInjectionError = LastInjectionError,
            HasInjected = _injector.HasInjectedAnything,
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog.Dispose();
        _uiTicker.Dispose();
        _receiver.Dispose();
        try
        {
            _probeReplier.Close();
        }
        catch
        {
            // 退出路径不抛异常
        }
    }
}

/// <summary>界面每秒刷新用的状态快照。</summary>
public sealed class PenSessionStatus
{
    public bool IsRunning;
    public int Port;
    public ReceiverStats Stats = new();
    public double Rate;
    public bool PenDown;
    public long ForcedUps;
    public string? LastSender;
    public string? LastInjectionError;
    public bool HasInjected;
}

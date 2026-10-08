using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PenReceiver;

/// <summary>接收端的实时统计，界面每秒读一次。</summary>
public sealed class ReceiverStats
{
    public long Packets;
    public long InvalidPackets;
    public long DroppedPackets;
    public long SequenceGaps;
    public long HelloProbes;
    public long RejectedSenders;
    public double PacketsPerSecond;
    public DateTime? LastPacketAt;
    public string? LastSender;
}

/// <summary>
/// UDP 笔事件监听器。
///
/// 设计上刻意不做任何耗时操作：收到即解析、解析完立刻回调注入。
/// 上一版在每个报文里打印一行控制台日志，高刷笔下光控制台输出就能把延迟拖到几百毫秒，
/// 这次只在界面上做 1 秒一次的汇总刷新。
/// </summary>
public sealed class UdpPenReceiver : IDisposable
{
    private readonly object _sync = new();

    private Thread? _thread;
    private UdpClient? _client;
    private volatile bool _running;

    private int _port = PenProtocol.DefaultPort;
    private uint _lastSeq;
    private bool _hasLastSeq;

    private long _packets;
    private long _invalid;
    private long _dropped;
    private long _gaps;
    private long _hellos;
    private long _rejected;
    private long _windowCount;
    private DateTime _windowStart = DateTime.UtcNow;
    private double _rate;
    private DateTime? _lastPacketAt;
    private string? _lastSender;

    /// <summary>收到并解析成功的笔事件。回调发生在后台接收线程，实现方需要自己保证线程安全。</summary>
    public event Action<PenDatagram>? PenReceived;

    /// <summary>收到探测报文时触发，用于告诉 Android 端本机在监听。</summary>
    public event Action<IPEndPoint, int>? ProbeReceived;

    /// <summary>只接受这个来源 IP 的报文。为 null 表示不过滤（局域网内任何设备都能注入，慎用）。</summary>
    public string? AllowedSender { get; set; }

    public int Port
    {
        get { lock (_sync) return _port; }
    }

    public bool IsRunning => _running;

    public void Start(int port)
    {
        lock (_sync)
        {
            if (_running) throw new InvalidOperationException("已经在监听中。");

            var client = new UdpClient(new IPEndPoint(IPAddress.Any, port))
            {
                // 手写场景报文小而密，缓冲区开大一点避免突发时丢包
            };
            client.Client.ReceiveBufferSize = 1 << 20;

            _client = client;
            _port = port;
            _running = true;
            _lastSeq = 0;
            _hasLastSeq = false;

            _thread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = "pen-udp-receiver",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
            thread = _thread;
            _thread = null;
            try
            {
                _client?.Close();
            }
            catch
            {
                // 关闭失败不影响退出
            }
            _client = null;
        }

        thread?.Join(TimeSpan.FromMilliseconds(500));
    }

    public ReceiverStats Snapshot()
    {
        lock (_sync)
        {
            var elapsed = (DateTime.UtcNow - _windowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _rate = _windowCount / elapsed;
                _windowCount = 0;
                _windowStart = DateTime.UtcNow;
            }

            return new ReceiverStats
            {
                Packets = _packets,
                InvalidPackets = _invalid,
                DroppedPackets = _dropped,
                SequenceGaps = _gaps,
                HelloProbes = _hellos,
                RejectedSenders = _rejected,
                PacketsPerSecond = _rate,
                LastPacketAt = _lastPacketAt,
                LastSender = _lastSender,
            };
        }
    }

    private void ReceiveLoop()
    {
        var client = _client;
        if (client == null) return;

        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            byte[] data;
            try
            {
                data = client.Receive(ref remote);
            }
            catch (SocketException)
            {
                // Stop() 关掉 socket 时会走到这里，属于正常退出路径
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                if (!_running) break;
                continue;
            }

            try
            {
                Handle(data, remote);
            }
            catch (Exception)
            {
                // 单个报文处理失败不能拖垮接收循环
                lock (_sync) _invalid++;
            }
        }
    }

    private void Handle(byte[] data, IPEndPoint remote)
    {
        if (PenProtocol.IsHello(data))
        {
            lock (_sync) _hellos++;
            ProbeReceived?.Invoke(remote, Port);
            return;
        }

        if (!PenProtocol.TryParsePen(data, out var packet))
        {
            lock (_sync) _invalid++;
            return;
        }

        var allowed = AllowedSender;
        if (!string.IsNullOrWhiteSpace(allowed) &&
            !string.Equals(remote.Address.ToString(), allowed.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            lock (_sync) _rejected++;
            return;
        }

        lock (_sync)
        {
            _packets++;
            _windowCount++;
            _lastPacketAt = DateTime.UtcNow;
            _lastSender = remote.Address.ToString();
            TrackSequence(packet.Seq);
        }

        PenReceived?.Invoke(new PenDatagram(packet, remote));
    }

    /// <summary>
    /// 统计序号缺口。UDP 丢包是常态，这里只用来在界面上给出「链路质量」，
    /// 真正防止卡笔靠的是 PenInjector 的超时兜底。
    /// </summary>
    private void TrackSequence(uint seq)
    {
        if (_hasLastSeq)
        {
            var delta = unchecked((int)(seq - _lastSeq));
            if (delta > 1)
            {
                _dropped += delta - 1;
                _gaps++;
            }
            else if (delta <= 0)
            {
                // 乱序或重复：不计入丢包，避免把序号回绕误判成大量丢包
                return;
            }
        }

        _lastSeq = seq;
        _hasLastSeq = true;
    }

    public void Dispose()
    {
        Stop();
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;
using Windows.UI.Input.Preview.Injection;

namespace PenReceiver;

/// <summary>平板画面如何映射到目标显示器。</summary>
public enum MappingMode
{
    /// <summary>整块平板 = 整块屏幕，坐标等比拉伸，任何位置都能点到。</summary>
    Stretch = 0,

    /// <summary>保持比例居中（平板比屏幕更方时上下留黑边），笔迹不被拉伸但边缘点不到。</summary>
    AspectFit = 1,
}

/// <summary>注入目标显示器。</summary>
public sealed class DisplayTarget
{
    public DisplayTarget(string name, Rectangle bounds, bool isPrimary)
    {
        Name = name;
        Bounds = bounds;
        IsPrimary = isPrimary;
    }

    public string Name { get; }
    public Rectangle Bounds { get; }
    public bool IsPrimary { get; }

    public override string ToString()
    {
        return $"{Name}  {Bounds.Width}×{Bounds.Height}{(IsPrimary ? "（主屏）" : string.Empty)}";
    }

    /// <summary>列出当前所有显示器，主屏排在第一个。</summary>
    public static DisplayTarget[] Enumerate()
    {
        var screens = Screen.AllScreens;
        var result = new DisplayTarget[screens.Length];
        var index = 0;

        // 主屏放最前，界面上默认选中它
        foreach (var screen in screens)
        {
            if (screen.Primary) result[index++] = From(screen);
        }
        foreach (var screen in screens)
        {
            if (!screen.Primary) result[index++] = From(screen);
        }
        return result;
    }

    public static DisplayTarget From(Screen screen)
    {
        var bounds = screen.Bounds;
        var name = screen.DeviceName?.Replace(@"\\.\", string.Empty) ?? "DISPLAY";
        return new DisplayTarget(name, bounds, screen.Primary);
    }

    public static DisplayTarget Primary()
    {
        return From(Screen.PrimaryScreen!);
    }
}

/// <summary>
/// 把归一化坐标的笔事件通过 Windows 的系统笔注入接口还原成本机笔输入。
///
/// 两个容易踩的坑：
///  1. 必须在创建时调用 TryCreate 成功（需要管理员权限），否则整个类不可用。
///  2. 坐标必须是**物理像素**。WinForms 的 Screen.Bounds 在进程声明了 PerMonitorV2
///     之后就是物理像素，所以清单文件里的 DPI 设置不能删。
/// </summary>
public sealed class PenInjector : IDisposable
{
    private readonly object _sync = new();
    private readonly InputInjector? _injector;
    private readonly bool _bound;

    private DisplayTarget _display;
    private MappingMode _mode;
    private int _pointerId = 1;

    /// <summary>上一次真正注入下去的动作，用来判断笔现在是不是「按着」。</summary>
    private PenAction _lastAction = PenAction.Up;
    private bool _hasInjectedAnything;

    /// <summary>系统接受的压感上界，构造时探测。0 表示压感不可用。</summary>
    private int _maxPressure = 1;

    public PenInjector(DisplayTarget display, MappingMode mode)
    {
        // 注意：没有管理员权限时 TryCreate 不是返回 null，而是抛 COM 异常
        // （0x80040111 ClassFactory 无法供应请求的类）。两种都要处理，
        // 并统一抛成带中文说明的异常，免得用户看到一串 HRESULT 无从下手。
        try
        {
            _injector = InputInjector.TryCreate();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法创建输入注入器（InputInjector）：{ex.Message}\n请以管理员身份运行本程序。", ex);
        }

        if (_injector is null)
        {
            throw new InvalidOperationException(
                "无法创建输入注入器（InputInjector）。请以管理员身份运行本程序。");
        }

        // 显式声明笔注入通道。即使省略，第一次 InjectPenInput 也会隐式初始化，
        // 但显式调用能让首次注入不用承担初始化开销（少了第一笔的顿感）。
        try
        {
            _injector.InitializePenInjection(InjectedInputVisualizationMode.None);
        }
        catch (Exception)
        {
            // 初始化失败不致命：后续 InjectPenInput 仍可能成功
        }

        _bound = true;
        _display = display;
        _mode = mode;
        TabletAspect = 0;

        // 压力上界必须实测：有的机器只接受 0/1，写死 1023 会导致每次注入都被拒绝
        ProbePressureRange();
    }

    private PenInjector(DisplayTarget display, MappingMode mode, double tabletAspect, int maxPressure)
    {
        _injector = null;
        _bound = false;
        _display = display;
        _mode = mode;
        TabletAspect = tabletAspect;
        _maxPressure = maxPressure;
    }

    /// <summary>
    /// 建一个只做坐标换算、不连接系统注入接口的实例，供自检与预览使用。
    /// 这条路径不需要管理员权限。maxPressure 默认按满量程 1023 计算。
    /// </summary>
    public static PenInjector CreateUnbound(DisplayTarget display, MappingMode mode, double tabletAspect,
        int maxPressure = 1023)
    {
        return new PenInjector(display, mode, tabletAspect, maxPressure);
    }

    /// <summary>是否连接了真实的系统注入接口。</summary>
    public bool IsBound => _bound;

    /// <summary>是否已经真正注入过输入。用于区分「需要管理员」和「只是还没连接」。</summary>
    public bool HasInjectedAnything
    {
        get { lock (_sync) return _hasInjectedAnything; }
    }

    /// <summary>笔现在是否处于按下状态。</summary>
    public bool IsPenDown
    {
        get { lock (_sync) return _lastAction is PenAction.Down or PenAction.Move; }
    }

    public DisplayTarget Display
    {
        get { lock (_sync) return _display; }
        set { lock (_sync) _display = value; }
    }

    public MappingMode Mode
    {
        get { lock (_sync) return _mode; }
        set { lock (_sync) _mode = value; }
    }

    /// <summary>平板采集区的长宽比（宽/高），仅等比模式用到。0 或负数表示未知，退化为拉伸。</summary>
    public double TabletAspect { get; set; }

    /// <summary>把归一化坐标换算成目标显示器上的物理像素。线程安全。</summary>
    public Point MapToScreen(float nx, float ny)
    {
        lock (_sync)
        {
            return MapLocked(nx, ny);
        }
    }

    /// <summary>调用方必须已经持有 _sync。</summary>
    private Point MapLocked(float nx, float ny)
    {
        var bounds = _display.Bounds;
        var aspect = TabletAspect;

        // 像素跨度用「最后一个像素的下标」而不是宽度：宽度 1920 的屏幕有效像素是
        // [0, 1919]。按 width 缩放会让右/下边缘多出半像素，在等比模式下表现为
        // 左右留边 96 与 95 不对称、以及最大坐标被钳制后损失一个像素。
        var spanX = bounds.Width - 1;
        var spanY = bounds.Height - 1;

        double x, y;
        if (_mode == MappingMode.Stretch || aspect <= 0.01)
        {
            x = bounds.Left + nx * spanX;
            y = bounds.Top + ny * spanY;
        }
        else
        {
            // 等比缩放居中：以平板的长宽比在屏幕内取最大的内接矩形，多余的一边留边。
            // 平板 16:10 投到 16:9 屏幕上时左右留边，但笔迹不会被拉扁。
            var screenAspect = spanX / (double)spanY;
            double w, h;
            if (aspect > screenAspect)
            {
                w = spanX;
                h = spanX / aspect;
            }
            else
            {
                h = spanY;
                w = spanY * aspect;
            }

            var offsetX = bounds.Left + (spanX - w) / 2.0;
            var offsetY = bounds.Top + (spanY - h) / 2.0;
            x = offsetX + nx * w;
            y = offsetY + ny * h;
        }

        var px = (int)Math.Round(x, MidpointRounding.AwayFromZero);
        var py = (int)Math.Round(y, MidpointRounding.AwayFromZero);

        px = Math.Clamp(px, bounds.Left, bounds.Right - 1);
        py = Math.Clamp(py, bounds.Top, bounds.Bottom - 1);
        return new Point(px, py);
    }

    /// <summary>注入一个笔事件。线程安全，可被接收线程与超时线程同时调用。</summary>
    public void Inject(PenPacket packet)
    {
        var action = packet.Action;
        var pressure = Math.Clamp(packet.Pressure, 0f, 1f);

        lock (_sync)
        {
            // 丢掉重复/无意义的动作，避免把「抬起」之后又来的移动当成按下
            if (action == PenAction.Move && !IsPenDownLocked() && _hasInjectedAnything)
            {
                action = PenAction.Down;
            }

            if (!TryInjectLocked(packet, action, pressure))
            {
                _lastAction = PenAction.Up;
                throw new InvalidOperationException("输入注入失败。");
            }

            _lastAction = action;
            _hasInjectedAnything = true;
        }
    }

    /// <summary>补一次「抬起」，用于丢包兜底。</summary>
    public bool ForcePenUp()
    {
        lock (_sync)
        {
            if (!IsPenDownLocked()) return false;

            TryInjectLocked(default, PenAction.Up, 0f);
            _lastAction = PenAction.Up;
            return true;
        }
    }

    /// <summary>
    /// 在指定位置注入一次鼠标右键点击。
    ///
    /// 为什么用鼠标注入而不是笔注入：实测表明注入的笔输入不会触发 Windows 的
    /// 「笔静止长按 → 右键」手势（静止 1.1 秒也不产生右键），所以长按右键只能显式注入。
    ///
    /// 坐标语义：InjectedInputMouseOptions.Absolute 用的是 **0..65535 归一化值**，
    /// 不是像素。传像素会让光标落在完全错误的位置（实测传 480 落到 14）。
    /// </summary>
    public void InjectRightClick(float nx, float ny)
    {
        if (!_bound || _injector is null) return;

        lock (_sync)
        {
            var point = MapLocked(nx, ny);
            var vs = SystemInformation.VirtualScreen;
            var absX = NormalizeToAbsolute(point.X - vs.Left, vs.Width);
            var absY = NormalizeToAbsolute(point.Y - vs.Top, vs.Height);

            try
            {
                _injector.InjectMouseInput(new[]
                {
                    new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.Move
                                       | InjectedInputMouseOptions.Absolute
                                       | InjectedInputMouseOptions.VirtualDesk,
                        DeltaX = absX,
                        DeltaY = absY,
                        TimeOffsetInMilliseconds = 0,
                    },
                    new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.RightDown,
                        DeltaX = 0,
                        DeltaY = 0,
                        TimeOffsetInMilliseconds = 0,
                    },
                    new InjectedInputMouseInfo
                    {
                        MouseOptions = InjectedInputMouseOptions.RightUp,
                        DeltaX = 0,
                        DeltaY = 0,
                        TimeOffsetInMilliseconds = 0,
                    },
                });
            }
            catch (Exception)
            {
                // 右键失败不应影响后续书写
            }
        }
    }

    private static int NormalizeToAbsolute(int pixel, int extent)
    {
        if (extent <= 1) return 0;
        var value = (int)Math.Round(pixel / (double)(extent - 1) * 65535.0);
        return Math.Clamp(value, 0, 65535);
    }

    private bool TryInjectLocked(PenPacket packet, PenAction action, float pressure)
    {
        if (!_bound || _injector is null) return false;

        try
        {
            _injector.InjectPenInput(BuildInfo(packet, action, pressure));
            return true;
        }
        catch (Exception)
        {
            // 注入失败通常意味着权限不足或系统拒绝；保持状态不推进，避免卡笔
            return false;
        }
    }

    /// <summary>供自检使用：把报文换算成待注入结构，但不真的注入。</summary>
    public InjectedInputPenInfo BuildInfoForTest(PenPacket packet, PenAction action, float pressure)
    {
        lock (_sync)
        {
            return BuildInfo(packet, action, pressure);
        }
    }

    private bool IsPenDownLocked()
    {
        return _lastAction is PenAction.Down or PenAction.Move;
    }

    private InjectedInputPenInfo BuildInfo(PenPacket packet, PenAction action, float pressure)
    {
        var point = MapLocked(packet.X, packet.Y);

        var options = InjectedInputPointerOptions.InRange;
        switch (action)
        {
            case PenAction.Down:
                options |= InjectedInputPointerOptions.PointerDown
                           | InjectedInputPointerOptions.InContact;
                break;
            case PenAction.Move:
                options |= InjectedInputPointerOptions.InContact;
                break;
            case PenAction.Up:
                options |= InjectedInputPointerOptions.PointerUp;
                break;
            case PenAction.Hover:
                // 悬停：在范围内但未接触，Windows 会显示笔光标
                break;
        }

        var pointerInfo = new InjectedInputPointerInfo
        {
            PointerId = (uint)_pointerId,
            PixelLocation = new InjectedInputPoint
            {
                PositionX = point.X,
                PositionY = point.Y,
            },
            PointerOptions = options,
            TimeOffsetInMilliseconds = 0,
        };

        var parameters = InjectedInputPenParameters.Pressure;
        if (action is PenAction.Down or PenAction.Move && Math.Abs(packet.TiltX) + Math.Abs(packet.TiltY) > 0.5f)
        {
            parameters |= InjectedInputPenParameters.TiltX | InjectedInputPenParameters.TiltY;
        }

        // 注意：InjectedInputPenParameters 里没有「橡皮」这一位，笔尾橡皮必须走
        // PenButtons 的 Inverted | Eraser。这两个按钮组合起来才是压感橡皮。
        var buttons = packet.IsEraser
            ? InjectedInputPenButtons.Inverted | InjectedInputPenButtons.Eraser
            : InjectedInputPenButtons.None;

        return new InjectedInputPenInfo
        {
            PointerInfo = pointerInfo,
            PenButtons = buttons,
            PenParameters = parameters,
            Pressure = ScalePressure(pressure),
            // Windows 的 TiltX/TiltY 与 Android 的 AXIS_ORIENTATION 方向约定相反，这里取反 TiltY
            TiltX = (int)Math.Clamp(Math.Round(packet.TiltX), -90, 90),
            TiltY = (int)Math.Clamp(Math.Round(-packet.TiltY), -90, 90),
            Rotation = 0,
        };
    }

    /// <summary>
    /// 把归一化压力换算成系统接受的整数压力。
    ///
    /// 这里不能写死 1023（MSDN 上的说法）。实测发现压感的上界取决于当前生效的
    /// 笔设备：没有真实笔数字化器的机器只接受 0 和 1，一旦传 2 或更大，
    /// InjectPenInput 会整包抛 ArgumentException——表现就是「平板上写得出来、
    /// 电脑上毫无反应」。所以上界必须运行时探测（见 <see cref="ProbePressureRange"/>）。
    /// </summary>
    private uint ScalePressure(float normalized)
    {
        var clamped = Math.Clamp(normalized, 0f, 1f);

        // 上界为 1 时只有「有压力/无压力」两种表达，此时必须用**阈值**而不是四舍五入。
        // 否则 0.5 会被舍入成 0（无压力），手写笔的笔迹就完全没有压感了。
        if (_maxPressure <= 1)
        {
            return clamped > 0.01f ? (uint)_maxPressure : 0u;
        }

        var value = (int)Math.Round(clamped * _maxPressure);
        return (uint)Math.Clamp(value, 0, _maxPressure);
    }

    /// <summary>当前探测到的压力上界。</summary>
    public int MaxPressure
    {
        get { lock (_sync) return _maxPressure; }
    }

    /// <summary>压感是否可用。上界为 1 说明只能表达「有/无压力」，画不出粗细变化。</summary>
    public bool PressureSupported
    {
        get { lock (_sync) return _maxPressure > 1; }
    }

    /// <summary>
    /// 探测系统接受的压感上界：从常规值 1023 往下试，第一个被接受的即为上界。
    ///
    /// 用「试到被接受为止」而不是「试到被拒绝为止」是有意的：探测用的注入会真的
    /// 落到当前焦点窗口上，所以尽量用最少的尝试次数，并且先试最可能的值。
    /// </summary>
    private void ProbePressureRange()
    {
        foreach (var candidate in new[] { 1023, 1 })
        {
            if (TryProbePressure(candidate))
            {
                _maxPressure = candidate;
                return;
            }
        }

        // 连 1 都不接受：退化为不传压力，只保证笔迹能出来
        _maxPressure = 0;
    }

    private bool TryProbePressure(int pressure)
    {
        if (!_bound || _injector is null) return false;

        try
        {
            _injector.InjectPenInput(new InjectedInputPenInfo
            {
                PointerInfo = new InjectedInputPointerInfo
                {
                    PointerId = (uint)_pointerId,
                    // 探测点尽量取屏幕左上角，落在标题栏/空白处的副作用最小
                    PixelLocation = new InjectedInputPoint { PositionX = 1, PositionY = 1 },
                    PointerOptions = InjectedInputPointerOptions.InRange,
                    TimeOffsetInMilliseconds = 0,
                },
                PenButtons = InjectedInputPenButtons.None,
                PenParameters = pressure > 0
                    ? InjectedInputPenParameters.Pressure
                    : InjectedInputPenParameters.None,
                Pressure = (uint)Math.Max(pressure, 0),
                TiltX = 0,
                TiltY = 0,
                Rotation = 0,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>换一支笔（例如重新连接了平板）时换个指针 id，避免和上一次的残留状态串味。</summary>
    public void ResetPointer()
    {
        lock (_sync)
        {
            ForcePenUp();
            _pointerId = _pointerId == 1 ? 2 : 1;
            _lastAction = PenAction.Up;
        }
    }

    public void Dispose()
    {
        try
        {
            ForcePenUp();
        }
        catch
        {
            // 退出路径不抛异常
        }

        if (_injector is not null)
        {
            try
            {
                _injector.UninitializePenInjection();
            }
            catch
            {
                // 未初始化或系统已回收，忽略
            }
        }
    }
}

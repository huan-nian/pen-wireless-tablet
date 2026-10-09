using System;
using Windows.UI.Input.Preview.Injection;

namespace PenReceiver;

/// <summary>一个待注入的键盘动作：修饰键（可空）+ 主键。</summary>
/// <param name="Modifier">修饰键，如 Ctrl / Win；没有则为 null。</param>
/// <param name="Key">主键。</param>
public readonly record struct KeyChord(VirtualKey? Modifier, VirtualKey Key)
{
    public override string ToString() =>
        Modifier is null ? Key.ToString() : $"{Modifier}+{Key}";
}

/// <summary>
/// 把平板上那排快捷按钮翻译成 Windows 的键盘/鼠标操作。
///
/// 关键设计：[MapCommand] 是**纯函数**，把命令翻译成按键组合，
/// 不碰任何系统接口。这样自检就能在无管理员权限、无注入通道的环境下
/// 验证「哪个按钮发哪组快捷键」是否正确——这部分逻辑最容易写错，
/// 而真正注入那一层只有几行胶水代码。
///
/// 实现说明：
///   - 修饰键与主键必须**在同一个数组里一次提交**，分两次调用会在中间产生
///     松开修饰键的时机差，系统可能只收到裸键；
///   - 每个键都要配上对应的 KeyUp，否则修饰键会一直按住，之后的输入全被污染。
/// </summary>
public sealed class SystemCommandInjector : IDisposable
{
    private readonly InputInjector? _injector;

    /// <summary>创建失败的原因（没提权 / 系统不支持），供界面显示。</summary>
    public string? CreationError { get; private set; }

    public SystemCommandInjector()
    {
        try
        {
            _injector = InputInjector.TryCreate();
            if (_injector is null) CreationError = "系统未提供输入注入通道。";
        }
        catch (Exception ex)
        {
            // 关键：没有管理员权限时，TryCreate 并不是返回 null，而是抛 COM 异常
            // （0x80040111 ClassFactory 无法供应请求的类）。这里必须接住，
            // 否则整个程序会在构造会话时直接崩溃。
            CreationError = ex.Message;
            _injector = null;
        }
    }

    /// <summary>是否连接上了系统注入接口（需要管理员权限）。</summary>
    public bool IsAvailable => _injector is not null;

    /// <summary>最近一次失败的原因，供界面显示。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 命令到按键的映射表。这里只处理**键盘组合键**类命令；
    /// 滚轮类命令不产生按键，由 <see cref="SendWheel"/> 单独处理，返回 null 表示不是按键命令。
    /// </summary>
    public static KeyChord? MapCommand(SystemCommand command) => command switch
    {
        // Win + D：最小化所有窗口回到桌面
        SystemCommand.ShowDesktop => new KeyChord(VirtualKey.LeftWindows, VirtualKey.D),

        // Win + Tab：任务视图（各窗口与虚拟桌面）
        SystemCommand.TaskView => new KeyChord(VirtualKey.LeftWindows, VirtualKey.Tab),

        // Ctrl + S：保存
        SystemCommand.Save => new KeyChord(VirtualKey.Control, VirtualKey.S),

        // Ctrl + Z：撤销
        SystemCommand.Undo => new KeyChord(VirtualKey.Control, VirtualKey.Z),

        // Windows 上「取消撤销」普遍是 Ctrl + Y
        SystemCommand.Redo => new KeyChord(VirtualKey.Control, VirtualKey.Y),

        // 滚轮不是按键命令
        _ => null,
    };

    /// <summary>执行一个系统命令。</summary>
    public bool Execute(SystemCommand command)
    {
        if (_injector is null)
        {
            LastError = CreationError is null ? "没有注入权限。" : $"没有注入权限：{CreationError}";
            return false;
        }

        try
        {
            switch (command)
            {
                case SystemCommand.ScrollUp:
                    return SendWheel(+WheelNotch);
                case SystemCommand.ScrollDown:
                    return SendWheel(-WheelNotch);
                default:
                    var chord = MapCommand(command);
                    if (chord is null)
                    {
                        LastError = $"未知命令：{command}";
                        return false;
                    }
                    return SendChord(chord.Value);
            }
        }
        catch (Exception ex)
        {
            LastError = $"{command} 执行失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>Windows 中一个滚轮「格」的增量。</summary>
    public const int WheelNotch = 120;

    /// <summary>按下修饰键 + 主键，然后按相反顺序释放。一次提交，避免时序问题。</summary>
    private bool SendChord(KeyChord chord)
    {
        var sequence = new System.Collections.Generic.List<InjectedInputKeyboardInfo>();

        if (chord.Modifier is { } modifier)
        {
            sequence.Add(new InjectedInputKeyboardInfo
            {
                VirtualKey = (ushort)modifier,
                KeyOptions = InjectedInputKeyOptions.None,
            });
        }

        sequence.Add(new InjectedInputKeyboardInfo
        {
            VirtualKey = (ushort)chord.Key,
            KeyOptions = InjectedInputKeyOptions.None,
        });
        sequence.Add(new InjectedInputKeyboardInfo
        {
            VirtualKey = (ushort)chord.Key,
            KeyOptions = InjectedInputKeyOptions.KeyUp,
        });

        if (chord.Modifier is { } modUp)
        {
            sequence.Add(new InjectedInputKeyboardInfo
            {
                VirtualKey = (ushort)modUp,
                KeyOptions = InjectedInputKeyOptions.KeyUp,
            });
        }

        _injector!.InjectKeyboardInput(sequence);
        LastError = null;
        return true;
    }

    /// <summary>
    /// 注入滚轮。正数向上、负数向下（Windows 约定）。
    /// MouseData 是无符号字段，向下滚要用 unchecked 保留负数的位型。
    /// </summary>
    private bool SendWheel(int delta)
    {
        _injector!.InjectMouseInput(new[]
        {
            new InjectedInputMouseInfo
            {
                MouseOptions = InjectedInputMouseOptions.Wheel,
                MouseData = unchecked((uint)delta),
                TimeOffsetInMilliseconds = 0,
            },
        });
        LastError = null;
        return true;
    }

    public void Dispose()
    {
        // InputInjector 没有 Dispose，通道由系统回收
    }
}

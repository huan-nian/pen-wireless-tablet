using System;

namespace PenReceiver;

/// <summary>长按这一笔该怎么处理。</summary>
public enum StrokeDecision
{
    /// <summary>正常注入。</summary>
    Inject,

    /// <summary>丢弃（长按过程中的笔迹，不该留墨）。</summary>
    Suppress,

    /// <summary>先把按下的笔解开，再在指定位置弹出右键菜单。</summary>
    RightClick,
}

/// <summary>
/// 「长按 = 右键」的状态机。
///
/// 因为实测发现注入的笔输入不会触发 Windows 的长按手势，所以长按由平板端判定，
/// 并通过报文标记告知接收端。这一笔的笔迹必须整体丢弃——否则长按期间会先留下
/// 一个墨点，右键菜单弹出后看起来像误画了一笔。
///
/// 单独抽出来是为了能被自检直接覆盖（不需要真的注入鼠标）。
/// </summary>
public sealed class LongPressGate
{
    private bool _suppressing;

    /// <summary>当前是否正在丢弃长按笔迹。</summary>
    public bool IsSuppressing => _suppressing;

    /// <summary>
    /// 决定一个收到的笔事件该如何处理。必须是线程安全的调用序列，
    /// 由 PenSession 在接收线程上串行调用。
    /// </summary>
    public StrokeDecision Decide(PenPacket packet)
    {
        if (packet.IsRightClick)
        {
            if (packet.Action == PenAction.Up)
            {
                // 抬起报文：结束丢弃状态，并要求弹右键
                _suppressing = false;
                return StrokeDecision.RightClick;
            }

            // 长按过程中的按下/移动：开始丢弃，不注入
            _suppressing = true;
            return StrokeDecision.Suppress;
        }

        if (_suppressing)
        {
            if (packet.Action == PenAction.Up)
            {
                // 正常情况下长按那一笔不会走到这里（它的抬起带标记），
                // 但丢包时可能出现，此时收尾即可
                _suppressing = false;
            }
            return StrokeDecision.Suppress;
        }

        return StrokeDecision.Inject;
    }

    /// <summary>连接断开或停止时复位，避免残留状态吞掉后面的笔迹。</summary>
    public void Reset()
    {
        _suppressing = false;
    }
}

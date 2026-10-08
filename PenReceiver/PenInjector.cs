using System;
using System.Windows.Forms;
using Windows.UI.Input.Preview.Injection;

namespace PenReceiver;

public class PenInjector
{
    private readonly InputInjector _injector;
    private readonly Screen _targetScreen;

    public PenInjector()
    {
        _injector = InputInjector.TryCreate()
            ?? throw new InvalidOperationException(
                "无法创建 InputInjector，请以管理员身份运行程序。");

        _targetScreen = Screen.PrimaryScreen!;
    }

    public void Inject(int action, float nx, float ny,
                       float pressure, float tilt, float orientation)
    {
        var bounds = _targetScreen.Bounds;

        int px = bounds.Left + (int)(nx * bounds.Width);
        int py = bounds.Top + (int)(ny * bounds.Height);

        px = Math.Clamp(px, bounds.Left, bounds.Right - 1);
        py = Math.Clamp(py, bounds.Top, bounds.Bottom - 1);

        var pointerInfo = new InjectedInputPointerInfo
        {
            PointerId = 1,
            PixelLocation = new InjectedInputPoint
            {
                PositionX = px,
                PositionY = py
            },
            PointerOptions = action switch
            {
                0 => InjectedInputPointerOptions.PointerDown
                     | InjectedInputPointerOptions.InRange
                     | InjectedInputPointerOptions.InContact,
                1 => InjectedInputPointerOptions.InRange
                     | InjectedInputPointerOptions.InContact,
                2 => InjectedInputPointerOptions.PointerUp
                     | InjectedInputPointerOptions.InRange,
                _ => InjectedInputPointerOptions.None
            },
            TimeOffsetInMilliseconds = 0
        };

        uint winPressure = (uint)Math.Clamp((int)(pressure * 1023), 0, 1023);

        var penInfo = new InjectedInputPenInfo
        {
            PointerInfo = pointerInfo,
            PenButtons = InjectedInputPenButtons.None,
            PenParameters = InjectedInputPenParameters.Pressure,
            Pressure = winPressure,
            TiltX = 0,
            TiltY = 0,
            Rotation = 0
        };

        _injector.InjectPenInput(penInfo);
    }
}
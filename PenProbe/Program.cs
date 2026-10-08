using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using PenReceiver;

namespace PenProbe;

/// <summary>
/// 端到端验证：把一笔真实笔迹注入到 **Windows 画图（mspaint）**，并截图对比。
///
/// 这是「平板写字能不能同步到画图」这件事最接近真实的自动化验证：
///   - 用真实的 PenInjector（也就是接收端用的那份代码）；
///   - 目标是一个真正的第三方绘图程序，而不是我们自己的窗口；
///   - 通过比较注入前后的截图来判断笔迹有没有真的落到画布上。
///
/// 注意：本探针会把画图窗口调整到屏幕左上角并置顶，全程约 5 秒。
/// </summary>
internal static class Program
{
    private static readonly string ShotDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenProbe");

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        System.IO.Directory.CreateDirectory(ShotDir);

        var work = Screen.PrimaryScreen!.WorkingArea;

        Console.WriteLine("=== 端到端：向 Windows 画图注入笔迹 ===");
        Console.WriteLine($"工作区：{work}");
        Console.WriteLine();

        Console.WriteLine("1) 启动画图…");
        var paint = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "mspaint.exe",
            UseShellExecute = true,
        });

        if (paint is null)
        {
            Console.WriteLine("   无法启动 mspaint.exe。");
            return 2;
        }

        // 等窗口真正出来
        IntPtr handle = IntPtr.Zero;
        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            paint.Refresh();
            if (paint.MainWindowHandle != IntPtr.Zero)
            {
                handle = paint.MainWindowHandle;
                break;
            }
        }

        if (handle == IntPtr.Zero)
        {
            Console.WriteLine("   画图窗口没有出现，放弃。");
            return 2;
        }

        Console.WriteLine($"   窗口句柄：0x{handle:X}");
        Thread.Sleep(1500);

        // 把画图放到左上角，尺寸固定，方便计算画布区域
        ShowWindow(handle, 9); // SW_RESTORE
        MoveWindow(handle, work.Left, work.Top, 1100, 800, true);
        SetForegroundWindow(handle);
        Thread.Sleep(1200);

        var before = Path("paint-before.png");
        Capture(work, before);
        Console.WriteLine($"2) 注入前截图：{before}");

        Console.WriteLine("3) 注入一笔（划过画布中部）…");
        var injector = new PenInjector(DisplayTarget.Primary(), MappingMode.Stretch) { TabletAspect = 1.6 };
        Console.WriteLine($"   压力上界：{injector.MaxPressure}  压感可用：{injector.PressureSupported}");

        var startX = 220;
        var startY = 420;
        var ok = true;
        ok &= Step("悬停", injector, startX, startY, PenAction.Hover, 0f);
        ok &= Step("按下", injector, startX, startY, PenAction.Down, 0.6f);

        for (var i = 1; i <= 60; i++)
        {
            // 画一条明显的波浪线
            var x = startX + i * 12;
            var y = startY + (int)(100 * Math.Sin(i / 6.0));
            ok &= Step($"移动{i}", injector, x, y, PenAction.Move, 0.6f, quiet: true);
            Thread.Sleep(8);
        }

        ok &= Step("抬起", injector, startX + 720, startY, PenAction.Up, 0f);
        Console.WriteLine($"   注入结果：{(ok ? "全部被接受" : "有步骤被拒绝")}");

        Thread.Sleep(800);

        var after = Path("paint-after.png");
        Capture(work, after);
        Console.WriteLine($"4) 注入后截图：{after}");

        injector.Dispose();

        // 比较两张图的差异像素比例。
        // 阈值取 0.05%：一条 3 像素宽的细线划过画布，差异大约只占检测区的 0.4%，
        // 阈值定得太高（比如 1%）会把「真的画出来了」误判成「没有变化」。
        var diff = Compare(before, after);
        const double threshold = 0.0005;

        Console.WriteLine();
        Console.WriteLine($"画布区域变化像素比例：{diff:P2}（判定阈值 {threshold:P2}）");
        Console.WriteLine(diff > threshold
            ? "结论：画布上出现了笔迹 —— 注入链路对真实绘图程序有效。"
            : "结论：画布没有变化 —— 注入没有落到画图上，需要继续排查。");

        Console.WriteLine();
        Console.WriteLine("（画图窗口保持打开，可自行查看；探针不会关闭它）");
        return diff > threshold ? 0 : 1;
    }

    private static string Path(string name) =>
        System.IO.Path.Combine(ShotDir, name);

    private static void Capture(Rectangle area, string file)
    {
        using var bitmap = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size);
        }
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// 比较两张截图，返回有差异的像素比例。
    ///
    /// 注意必须逐像素比较：笔迹线宽只有 1~3 像素，按 2 像素步长采样会大面积漏检
    /// （实测同一条清晰的曲线只测出 0.41%，误判成「没有变化」）。
    /// </summary>
    private static double Compare(string fileA, string fileB)
    {
        using var a = new Bitmap(fileA);
        using var b = new Bitmap(fileB);
        if (a.Width != b.Width || a.Height != b.Height) return 1.0;

        // 锁定像素数据，避免 GetPixel 的逐点开销
        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var dataA = a.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var dataB = b.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var stride = dataA.Stride;
            var bytesA = new byte[stride * a.Height];
            var bytesB = new byte[stride * b.Height];
            System.Runtime.InteropServices.Marshal.Copy(dataA.Scan0, bytesA, 0, bytesA.Length);
            System.Runtime.InteropServices.Marshal.Copy(dataB.Scan0, bytesB, 0, bytesB.Length);

            // 只看画图窗口所在的区域，避开任务栏时钟之类的干扰
            var y0 = 60;
            var y1 = Math.Min(760, a.Height);
            var x0 = 40;
            var x1 = Math.Min(1080, a.Width);

            long changed = 0;
            long total = 0;

            for (var y = y0; y < y1; y++)
            {
                var row = y * stride;
                for (var x = x0; x < x1; x++)
                {
                    var i = row + x * 4;
                    total++;
                    var diff = Math.Abs(bytesA[i] - bytesB[i])
                               + Math.Abs(bytesA[i + 1] - bytesB[i + 1])
                               + Math.Abs(bytesA[i + 2] - bytesB[i + 2]);
                    if (diff > 30) changed++;
                }
            }

            return total == 0 ? 0 : changed / (double)total;
        }
        finally
        {
            a.UnlockBits(dataA);
            b.UnlockBits(dataB);
        }
    }

    private static bool Step(string name, PenInjector injector, int x, int y,
        PenAction action, float pressure, bool quiet = false)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        var packet = new PenPacket
        {
            Action = action,
            X = (x - bounds.Left) / (float)bounds.Width,
            Y = (y - bounds.Top) / (float)bounds.Height,
            Pressure = pressure,
            TiltX = 0,
            TiltY = 0,
        };

        try
        {
            injector.Inject(packet);
            if (!quiet) Console.WriteLine($"   [成功] {name}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [失败] {name} → {ex.Message}");
            return false;
        }
    }
}

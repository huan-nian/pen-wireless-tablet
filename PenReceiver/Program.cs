using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PenReceiver;

/// <summary>
/// 接收端入口：创建注入器并打开主窗口。
///
/// 这个可执行文件声明了管理员权限（app.manifest 里的 requireAdministrator），
/// 因为 Windows 的笔输入注入接口只在提升后的进程里可用。
/// 不需要权限的自检请运行同目录下的 PenSelfTest.exe。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 单实例互斥锁。有它才能保证「任何时候只跑一个接收端」，否则多个实例会抢
    /// 同一个 UDP 端口（其中一部分静默拿不到数据），而且提权进程普通权限杀不掉，
    /// 会越积越多。
    /// </summary>
    private const string InstanceMutexName = @"Local\PenReceiver.SingleInstance";

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    /// <summary>
    /// 重复启动时用来「唤醒已有窗口」的广播消息 id。
    ///
    /// 为什么不用 MessageBox 提示：提权进程的窗口常被压在后台，用户看到
    /// 「已经在运行」却找不到窗口，会以为程序卡住了。直接把它唤到前台更符合直觉。
    /// </summary>
    private static readonly int RestoreMessage = RegisterWindowMessage("PenReceiver.RestoreWindow");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已经有实例在跑：广播一条消息让它自己跳到前面，本次直接退出
            PostMessage(HwndBroadcast, RestoreMessage, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }

        return RunApplication();
    }

    private static int RunApplication()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        PenInjector injector;
        try
        {
            injector = new PenInjector(DisplayTarget.Primary(), MappingMode.Stretch)
            {
                TabletAspect = 1.6,
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"启动失败：{ex.Message}\n\n" +
                "笔输入注入需要管理员权限。请用 launch-receiver.cmd 启动，" +
                "或右键 PenReceiver.exe 选择「以管理员身份运行」。",
                "无线手写板 · 接收端",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        using (injector)
        {
            Application.Run(new PenReceiverForm(injector, RestoreMessage));
        }
        return 0;
    }
}

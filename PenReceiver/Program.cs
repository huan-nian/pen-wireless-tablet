using System;
using System.Windows.Forms;

namespace PenReceiver;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Console.WriteLine("=== Windows 无线手写板接收端 ===");

        try
        {
            var injector = new PenInjector();
            var receiver = new UdpPenReceiver(injector);
            receiver.Run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            Console.WriteLine("请以管理员身份运行此程序。");
            Console.ReadLine();
        }
    }
}
using System;

namespace PenReceiver;

/// <summary>
/// 自检程序入口。刻意与接收端分成两个可执行文件：
/// 接收端声明了管理员权限，而自检不需要任何权限，混在一起会导致自检也要求提权。
/// </summary>
internal static class SelfTestProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        return SelfTest.Run(args);
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace PenInstaller;

/// <summary>程序入口。同一个 exe 兼作安装器与卸载器，靠命令行参数区分。</summary>
internal static class Program
{
    /// <summary>载荷在程序集里的资源名（见 csproj 的 LogicalName）。</summary>
    internal const string PayloadResource = "payload.zip";

    /// <summary>安装后主程序的文件名。</summary>
    internal const string AppExeName = "PenReceiver.exe";

    /// <summary>卸载注册表项名，也是“应用和功能”里显示的名字。</summary>
    internal const string AppRegistryName = "无线手写板";

    /// <summary>开始菜单/桌面快捷方式名。</summary>
    internal const string ShortcutName = "无线手写板";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // /uninstall 由“应用和功能”的卸载项调用（见 InstallerCore.WriteUninstallRegistry）
        var uninstall = args.Any(a =>
            a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));

        // /finish 是“删除阶段”：卸载器会先把自己复制到临时目录、再以这个参数重启，
        // 这样原安装目录里不再有正在运行的进程，目录才能被删掉。
        var finish = args.Any(a => a.Equals("/finish", StringComparison.OrdinalIgnoreCase));

        if (uninstall && finish)
        {
            var dir = args.FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith('-'))
                ?? InstallerCore.GetInstalledDir()
                ?? InstallerCore.DefaultInstallDir;
            UninstallForm.RunFinish(dir);
            return;
        }

        if (uninstall)
        {
            using var form = new UninstallForm();
            Application.Run(form);
            return;
        }

        using var installForm = new InstallForm();
        Application.Run(installForm);
    }

    /// <summary>载荷是否真的被嵌进来了（没跑打包脚本时就没有）。</summary>
    internal static bool HasPayload =>
        Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadResource) != null;

    /// <summary>
    /// 当前进程是否已提权。
    ///
    /// 为什么需要自己判断：清单用的是 highestAvailable，万一用户拒绝了 UAC，
    /// 程序仍会启动但写不了 Program Files 与 HKLM。这时必须明确告诉用户，
    /// 而不是等安装到一半抛一个看不懂的「拒绝访问」。
    /// </summary>
    internal static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>以管理员身份重新启动自己（会弹出 UAC）。</summary>
    internal static bool RelaunchElevated()
    {
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = self,
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch
        {
            // 用户在 UAC 上点了「否」
            return false;
        }
    }
}

/// <summary>
/// 安装/卸载的公共逻辑。
///
/// 单独抽出来是因为安装、卸载、以及“判断是否已安装”三处都要用同一套路径与注册表约定，
/// 各写一份迟早会不一致（比如卸载项写的是 A 路径、安装写的是 B 路径）。
/// </summary>
internal static class InstallerCore
{
    /// <summary>默认安装目录：Program Files\无线手写板。</summary>
    internal static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Program.AppRegistryName);

    /// <summary>注册表卸载项位置（HKLM，所有用户可见）。</summary>
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + Program.AppRegistryName;

    /// <summary>读出已安装版本号；未安装返回 null。</summary>
    internal static string? GetInstalledVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读出已安装目录；未安装返回 null。</summary>
    internal static string? GetInstalledDir()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把载荷解压到目标目录。
    ///
    /// 进度回调按“解压出的字节数”而不是“条目数”计算——载荷里是一个 178 MB 的大文件，
    /// 按条目数算进度会一直停在 0% 然后突然跳到 100%。
    /// </summary>
    internal static void ExtractPayload(string installDir, IProgress<(long done, long total)> progress,
        CancellationToken token)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Program.PayloadResource)
            ?? throw new InvalidOperationException(
                "安装包内没有找到程序载荷。请用完整的安装包，或先运行 tools\\build-installer 生成载荷。");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var total = archive.Entries.Sum(e => e.Length);
        long done = 0;

        Directory.CreateDirectory(installDir);

        var buffer = new byte[1024 * 1024];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            // 目录条目：名字以 / 结尾、且长度为 0
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(Path.Combine(installDir, entry.FullName));
                continue;
            }

            var target = Path.Combine(installDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var source = entry.Open())
            using (var dest = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    dest.Write(buffer, 0, read);
                    done += read;
                    progress.Report((done, total));
                }
            }
        }
    }

    /// <summary>创建或更新“应用和功能”里的卸载项。</summary>
    internal static void WriteUninstallRegistry(string installDir, string version, long estimatedSizeKb)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法写入卸载注册表项（需要管理员权限）。");

        var uninstaller = Path.Combine(installDir, "PenWirelessTablet-Setup.exe");

        key.SetValue("DisplayName", Program.AppRegistryName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Huan Nian");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, Program.AppExeName));
        // QuietUninstallString 让“应用和功能”静默卸载时也不会弹黑框
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /quiet");
        key.SetValue("EstimatedSize", (int)estimatedSizeKb, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
    }

    internal static void RemoveUninstallRegistry()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch
        {
            // 卸载时删不掉注册表项不值得让整个卸载失败
        }
    }

    /// <summary>
    /// 创建快捷方式。
    ///
    /// 用 WScript.Shell 的 COM 接口而不是 IShellLink：后者要手写 COM 互操作声明，
    /// 而 WScript.Shell 在 Windows 上一直都可用，代码量少很多。
    /// </summary>
    internal static void CreateShortcut(string linkPath, string targetPath, string workingDir, string description)
    {
        var dir = Path.GetDirectoryName(linkPath)!;
        Directory.CreateDirectory(dir);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统未提供 WScript.Shell，无法创建快捷方式。");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDir;
            link.Description = description;
            // 接收端需要管理员权限，快捷方式也标记一下，少一次兼容性提示
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    internal static void DeleteShortcuts()
    {
        foreach (var link in EnumerateShortcutPaths())
        {
            try
            {
                if (File.Exists(link)) File.Delete(link);
            }
            catch
            {
                // 快捷方式删不掉不影响卸载
            }
        }
    }

    private static IEnumerable<string> EnumerateShortcutPaths()
    {
        var startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Program.ShortcutName + ".lnk");
        yield return startMenu;

        var desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Program.ShortcutName + ".lnk");
        yield return desktop;
    }

    /// <summary>目标盘剩余空间是否够用（留 100 MB 余量）。</summary>
    internal static bool HasEnoughSpace(string installDir, long neededBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(installDir));
            if (string.IsNullOrEmpty(root)) return true;

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace > neededBytes + 100L * 1024 * 1024;
        }
        catch
        {
            // 判断不出来就不拦着用户
            return true;
        }
    }

    /// <summary>递归删除目录，忽略删不掉的文件（例如正被占用）。</summary>
    internal static void DeleteDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 目录里有正在运行的程序时删不掉，留给用户手动清理
        }
    }

    internal static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.1";

    /// <summary>把安装目录加入“应用和功能”的体积估算（KB）。</summary>
    internal static long DirectorySizeKb(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length) / 1024;
        }
        catch
        {
            return 0;
        }
    }

    internal static void OpenFolder(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch
        {
            // 打不开资源管理器不算错误
        }
    }
}

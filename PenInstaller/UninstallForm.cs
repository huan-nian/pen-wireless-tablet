using System.Diagnostics;

namespace PenInstaller;

/// <summary>
/// 卸载界面。由“应用和功能”里的卸载项以 <c>/uninstall</c> 参数启动。
///
/// 卸载要处理的一个麻烦点：卸载器自己就住在要删掉的目录里，
/// 正在运行的 exe 无法被删除。做法是先把自己复制到临时目录、再用副本重启自己，
/// 让原目录变成“没有进程占用”的状态，然后删目录、最后清理临时副本。
/// </summary>
internal sealed class UninstallForm : Form
{
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _uninstallButton = new();
    private readonly Button _cancelButton = new();
    private readonly string _installDir;

    internal UninstallForm()
    {
        // 安装目录以注册表为准；读不到就退回安装器自身所在目录
        _installDir = InstallerCore.GetInstalledDir()
            ?? Path.GetDirectoryName(Environment.ProcessPath)
            ?? InstallerCore.DefaultInstallDir;

        Text = "卸载 无线手写板";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 230);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(0xF7, 0xF5, 0xF0);

        BuildLayout();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "卸载 无线手写板",
            Font = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x2B, 0x2E, 0x33),
            AutoSize = true,
            Location = new Point(24, 20),
        };

        _statusLabel.Text = $"将从下面的位置删除程序：\n{_installDir}\n\n快捷方式与卸载项也会一并清理。";
        _statusLabel.Location = new Point(26, 62);
        _statusLabel.Size = new Size(448, 80);
        _statusLabel.ForeColor = Color.FromArgb(0x6B, 0x70, 0x79);

        _progress.Location = new Point(26, 148);
        _progress.Size = new Size(448, 18);
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;

        _uninstallButton.Text = "卸载";
        _uninstallButton.Location = new Point(308, 180);
        _uninstallButton.Size = new Size(80, 30);
        _uninstallButton.Click += (_, _) => ConfirmAndUninstall();

        _cancelButton.Text = "取消";
        _cancelButton.Location = new Point(394, 180);
        _cancelButton.Size = new Size(80, 30);
        _cancelButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, _statusLabel, _progress, _uninstallButton, _cancelButton });
    }

    private void ConfirmAndUninstall()
    {
        var confirm = MessageBox.Show(this,
            "确定要卸载「无线手写板」吗？\n\n已安装的程序文件与快捷方式会被删除。",
            "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (confirm != DialogResult.Yes) return;

        _uninstallButton.Enabled = false;
        _cancelButton.Enabled = false;
        _progress.Visible = true;

        // 先把自己挪到临时目录，否则删不掉安装目录里的这个 exe
        var self = Environment.ProcessPath;
        var tempCopy = Path.Combine(Path.GetTempPath(), "PenWirelessUninstall-" + Guid.NewGuid().ToString("N") + ".exe");

        try
        {
            if (!string.IsNullOrEmpty(self))
            {
                File.Copy(self, tempCopy, overwrite: true);

                // 副本带 /uninstall /finish 重启自己：这一轮只做“删除”，不再问用户
                Process.Start(new ProcessStartInfo
                {
                    FileName = tempCopy,
                    Arguments = $"/uninstall /finish \"{_installDir}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                });
                Application.Exit();
                return;
            }
        }
        catch
        {
            // 复制失败就退化为原地卸载：能删多少删多少
        }

        PerformCleanup();
    }

    /// <summary>“删除”阶段的入口，由 <see cref="Program"/> 在带 /finish 参数时调用。</summary>
    internal static void RunFinish(string installDir)
    {
        // 给上一个进程一点时间退出，否则它的 exe 文件还被占用
        Thread.Sleep(1200);

        InstallerCore.DeleteShortcuts();
        InstallerCore.RemoveUninstallRegistry();
        InstallerCore.DeleteDirectory(installDir);

        // 临时副本自己删不掉自己，交给系统在重启后清理
        var self = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(self) && self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // MoveFileEx 的 DELAY_UNTIL_REBOOT：进程退出后由系统删除
                MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
            catch
            {
                // 删不掉也不影响，临时目录会被系统清理
            }
        }

        MessageBox.Show(
            "卸载完成。\n\n如果程序文件未能全部删除（例如接收端当时正在运行），请重启后再手动删除安装目录。",
            "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PerformCleanup()
    {
        InstallerCore.DeleteShortcuts();
        InstallerCore.RemoveUninstallRegistry();
        InstallerCore.DeleteDirectory(_installDir);

        MessageBox.Show(this, "卸载完成。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }

    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}

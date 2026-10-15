using System.Diagnostics;

namespace PenInstaller;

/// <summary>
/// 安装向导界面。
///
/// 刻意做成单页 + 就地切换内容，而不是多步向导：需要用户决定的只有「装到哪」和
/// 「要不要快捷方式」，为这点选择做三页翻页反而更麻烦。
/// 安装过程就在同一页显示进度，完成/失败也在同一页给结果。
/// </summary>
internal sealed class InstallForm : Form
{
    private readonly TextBox _pathBox = new();
    private readonly CheckBox _desktopShortcut = new();
    private readonly CheckBox _startMenuShortcut = new();
    private readonly Label _hintLabel = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _primaryButton = new();
    private readonly Button _secondaryButton = new();
    private string? _installedDir;

    internal InstallForm()
    {
        Text = "无线手写板 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 330);
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(0xF7, 0xF5, 0xF0);

        BuildLayout();
        Shown += (_, _) => CheckExistingInstall();
    }

    private void BuildLayout()
    {
        var title = new Label
        {
            Text = "无线手写板",
            Font = new Font("Microsoft YaHei UI", 16f, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x2B, 0x2E, 0x33),
            AutoSize = true,
            Location = new Point(24, 20),
        };

        var subtitle = new Label
        {
            Text = "把 Android 平板当作电脑的无线手写板",
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(0x6B, 0x70, 0x79),
            AutoSize = true,
            Location = new Point(26, 56),
        };

        var version = new Label
        {
            Text = $"版本 {InstallerCore.CurrentVersion}",
            ForeColor = Color.FromArgb(0x8A, 0x90, 0x99),
            AutoSize = true,
            Location = new Point(26, 78),
        };

        var pathLabel = new Label
        {
            Text = "安装位置",
            AutoSize = true,
            Location = new Point(26, 116),
        };

        _pathBox.Location = new Point(26, 140);
        _pathBox.Size = new Size(430, 26);
        _pathBox.Text = InstallerCore.DefaultInstallDir;
        _pathBox.TextChanged += (_, _) => UpdateHint();

        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(464, 139),
            Size = new Size(70, 28),
        };
        browse.Click += (_, _) => BrowseForFolder();

        _startMenuShortcut.Text = "创建开始菜单快捷方式";
        _startMenuShortcut.Checked = true;
        _startMenuShortcut.AutoSize = true;
        _startMenuShortcut.Location = new Point(26, 180);

        _desktopShortcut.Text = "创建桌面快捷方式";
        _desktopShortcut.Checked = true;
        _desktopShortcut.AutoSize = true;
        _desktopShortcut.Location = new Point(26, 204);

        _hintLabel.Location = new Point(26, 232);
        _hintLabel.Size = new Size(508, 36);
        _hintLabel.ForeColor = Color.FromArgb(0x6B, 0x70, 0x79);

        _progress.Location = new Point(26, 232);
        _progress.Size = new Size(508, 20);
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Visible = false;

        _primaryButton.Text = "安装";
        _primaryButton.Location = new Point(368, 280);
        _primaryButton.Size = new Size(80, 30);
        _primaryButton.Click += (_, _) => OnPrimary();

        _secondaryButton.Text = "取消";
        _secondaryButton.Location = new Point(454, 280);
        _secondaryButton.Size = new Size(80, 30);
        _secondaryButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, subtitle, version, pathLabel, _pathBox, browse,
            _startMenuShortcut, _desktopShortcut, _hintLabel, _progress,
            _primaryButton, _secondaryButton,
        });
    }

    /// <summary>启动时检查是否已经装过，是则把按钮改成“更新”。</summary>
    private void CheckExistingInstall()
    {
        // 没提权就先说清楚，别等安装到一半才报「拒绝访问」
        if (!Program.IsElevated)
        {
            var choice = MessageBox.Show(this,
                "安装程序需要管理员权限才能写入 Program Files 并注册卸载项。\n\n" +
                "是否以管理员身份重新启动安装程序？",
                "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (choice == DialogResult.Yes && Program.RelaunchElevated())
            {
                Close();
                return;
            }

            SetHint("当前没有管理员权限，安装很可能失败。请右键安装包选「以管理员身份运行」。", Error);
            _primaryButton.Enabled = false;
            return;
        }

        var existingVersion = InstallerCore.GetInstalledVersion();
        if (existingVersion == null) return;

        var existingDir = InstallerCore.GetInstalledDir();
        if (!string.IsNullOrEmpty(existingDir) && Directory.Exists(existingDir))
        {
            _pathBox.Text = existingDir;
        }

        _primaryButton.Text = "更新";
        SetHint($"检测到已安装版本 {existingVersion}，将覆盖更新到 {InstallerCore.CurrentVersion}。",
            Warning);
    }

    private static readonly Color Normal = Color.FromArgb(0x6B, 0x70, 0x79);
    private static readonly Color Warning = Color.FromArgb(0xB0, 0x6A, 0x00);
    private static readonly Color Error = Color.FromArgb(0xC0, 0x39, 0x2B);

    private void SetHint(string text, Color color)
    {
        _hintLabel.Text = text;
        _hintLabel.ForeColor = color;
    }

    private void UpdateHint()
    {
        var dir = _pathBox.Text.Trim();
        if (dir.Length == 0)
        {
            SetHint("请填写安装位置。", Error);
            return;
        }

        SetHint("安装包已内置运行环境，无需联网，也不需要预先安装 .NET。", Normal);
    }

    private void BrowseForFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择安装位置",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_pathBox.Text) ? _pathBox.Text : InstallerCore.DefaultInstallDir,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnPrimary()
    {
        // 安装完成后按钮变成“完成”，只关窗口
        if (_installedDir != null)
        {
            Close();
            return;
        }

        var dir = _pathBox.Text.Trim();

        if (dir.Length == 0)
        {
            SetHint("请填写安装位置。", Error);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            SetHint($"无法创建该目录：{ex.Message}", Error);
            return;
        }

        // 需要约 190 MB（解压后 178 MB + 余量）
        if (!InstallerCore.HasEnoughSpace(dir, 190L * 1024 * 1024))
        {
            SetHint("目标磁盘剩余空间不足，请换一个位置或先清理磁盘。", Error);
            return;
        }

        if (!Program.HasPayload)
        {
            SetHint("这个安装包内没有程序载荷。请使用正式发布的安装包。", Error);
            return;
        }

        SetBusy(true);
        var progress = new Progress<(long done, long total)>(p =>
        {
            var percent = p.total <= 0 ? 0 : (int)(p.done * 100 / p.total);
            _progress.Value = Math.Clamp(percent, 0, 100);
            SetHint($"正在安装… {percent}%（{p.done / 1024 / 1024} / {p.total / 1024 / 1024} MB）", Normal);
        });

        // 解压放到后台线程，否则界面会卡住、进度条也不会动
        Task.Run(() =>
        {
            InstallerCore.ExtractPayload(dir, progress, CancellationToken.None);
            return dir;
        }).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                SetBusy(false);
                var message = task.Exception?.GetBaseException().Message ?? "未知错误";
                SetHint($"安装失败：{message}", Error);
                return;
            }

            FinishInstall(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>解压完成后的收尾：快捷方式、注册表、卸载器副本。</summary>
    private void FinishInstall(string dir)
    {
        try
        {
            var exePath = Path.Combine(dir, Program.AppExeName);

            // 把安装器自己复制进安装目录，卸载时用它（此时它已经是 /uninstall 模式）。
            // 不放进 Program Files 的话，用户删掉下载的安装包后就无法卸载了。
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
            {
                File.Copy(self, Path.Combine(dir, "PenWirelessTablet-Setup.exe"), overwrite: true);
            }

            if (_startMenuShortcut.Checked)
            {
                InstallerCore.CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                        Program.ShortcutName + ".lnk"),
                    exePath, dir, "无线手写板 接收端");
            }

            if (_desktopShortcut.Checked)
            {
                InstallerCore.CreateShortcut(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                        Program.ShortcutName + ".lnk"),
                    exePath, dir, "无线手写板 接收端");
            }

            InstallerCore.WriteUninstallRegistry(dir, InstallerCore.CurrentVersion, InstallerCore.DirectorySizeKb(dir));

            _installedDir = dir;
            _progress.Value = 100;
            SetBusy(false);
            _primaryButton.Text = "完成";
            _secondaryButton.Visible = false;
            _pathBox.Enabled = false;
            _desktopShortcut.Enabled = false;
            _startMenuShortcut.Enabled = false;

            SetHint("安装完成。接收端需要在管理员权限下运行，这是 Windows 笔输入注入接口的要求。", Normal);

            // 装完直接问要不要启动，省得用户再去开始菜单找
            var launch = MessageBox.Show(this,
                "安装完成。\n\n现在启动「无线手写板 接收端」吗？\n（需要管理员权限，会弹出 UAC 确认）",
                "安装完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information);

            if (launch == DialogResult.Yes)
            {
                StartReceiver(exePath, dir);
            }

            Close();
        }
        catch (Exception ex)
        {
            SetBusy(false);
            SetHint($"安装未完全成功：{ex.Message}", Error);
        }
    }

    private void StartReceiver(string exePath, string workingDir)
    {
        try
        {
            // runas 触发 UAC 提权：接收端必须在提升的进程里才能注入笔输入
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDir,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"启动失败：{ex.Message}\n\n可以从开始菜单或安装目录手动启动。",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetBusy(bool busy)
    {
        _progress.Visible = busy;
        _hintLabel.Visible = !busy;
        _primaryButton.Enabled = !busy;
        _secondaryButton.Enabled = !busy;
        _pathBox.Enabled = !busy;
        _desktopShortcut.Enabled = !busy;
        _startMenuShortcut.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}

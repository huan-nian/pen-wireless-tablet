using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PenReceiver;

/// <summary>
/// 主窗口：左边是设置与实时状态，右边是事件日志。
/// 关掉窗口不退出程序，只是缩到托盘；真正退出用「停止并退出」或托盘菜单。
/// </summary>
public sealed class PenReceiverForm : Form
{
    private const int LogCapacity = 500;

    private readonly PenInjector _injector;
    private readonly PenSession _session;

    private readonly NumericUpDown _portBox = new();
    private readonly Button _startButton = new();
    private readonly Button _injectToggle = new();
    private readonly ComboBox _displayBox = new();
    private readonly ComboBox _mappingBox = new();
    private readonly NumericUpDown _aspectBox = new();
    private readonly TextBox _allowBox = new();
    private readonly CheckBox _allowCheck = new();

    private readonly Label _stateValue = new();
    private readonly Label _addressValue = new();
    private readonly Label _rateValue = new();
    private readonly Label _qualityValue = new();
    private readonly Label _penValue = new();
    private readonly Label _adminValue = new();

    private readonly ListBox _log = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    /// <summary>重复启动时广播的唤醒消息 id，由 Program 注册后传入。</summary>
    private readonly int _restoreMessage;

    private bool _injectionEnabled = true;
    private int _logCount;

    public PenReceiverForm(PenInjector injector, int restoreMessage)
    {
        _injector = injector;
        _restoreMessage = restoreMessage;
        _session = new PenSession(injector);
        _session.StatusChanged += OnStatusChanged;

        Text = "无线手写板 · 接收端";
        MinimumSize = new Size(720, 460);
        Size = new Size(860, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9f);
        BackColor = Color.FromArgb(243, 244, 246);

        BuildLayout();
        BuildTray();
        LoadDisplays();

        _uiTimer.Interval = 400;
        _uiTimer.Tick += (_, _) => RefreshAddress();
        _uiTimer.Start();

        AppendLog($"本机地址：{PenProtocol.DescribeLocalAddresses()}");
        AppendLog("在平板上点「扫描电脑」即可自动发现本机；也可以手动输入上面的地址。");
    }

    // ------------------------------------------------------------------ 布局

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildSettingsPanel(), 0, 0);
        root.Controls.Add(BuildLogPanel(), 1, 0);
        Controls.Add(root);
    }

    private Control BuildSettingsPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
        };

        panel.Controls.Add(Group("连接", BuildConnectionGroup()));
        panel.Controls.Add(Group("映射", BuildMappingGroup()));
        panel.Controls.Add(Group("状态", BuildStatusGroup()));
        panel.Controls.Add(Group("安全", BuildSecurityGroup()));
        return panel;
    }

    private static GroupBox Group(string title, Control content)
    {
        content.Dock = DockStyle.Fill;
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(10),
            ForeColor = Color.FromArgb(27, 27, 31),
        };
        box.Controls.Add(content);
        return box;
    }

    private Control BuildConnectionGroup()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        layout.Controls.Add(Muted("监听端口"), 0, 0);

        _portBox.Minimum = 1;
        _portBox.Maximum = 65535;
        _portBox.Value = PenProtocol.DefaultPort;
        _portBox.Dock = DockStyle.Fill;
        _portBox.Margin = new Padding(0, 2, 8, 2);
        layout.Controls.Add(_portBox, 1, 0);

        _startButton.Text = "开始监听";
        _startButton.Dock = DockStyle.Fill;
        _startButton.Height = 30;
        _startButton.Click += (_, _) => ToggleListening();
        layout.Controls.Add(_startButton, 2, 0);

        _injectToggle.Text = "暂停注入";
        _injectToggle.Dock = DockStyle.Fill;
        _injectToggle.Height = 30;
        _injectToggle.Margin = new Padding(0, 6, 0, 0);
        _injectToggle.Click += (_, _) => ToggleInjection();
        layout.Controls.Add(_injectToggle, 2, 1);

        layout.Controls.Add(Muted("平板地址"), 0, 1);

        _allowBox.Dock = DockStyle.Fill;
        _allowBox.Margin = new Padding(0, 4, 8, 2);
        _allowBox.PlaceholderText = "例如 192.168.1.23";
        layout.Controls.Add(_allowBox, 1, 1);

        return layout;
    }

    private Control BuildMappingGroup()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(Muted("目标显示器"), 0, 0);
        _displayBox.Dock = DockStyle.Fill;
        _displayBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _displayBox.SelectedIndexChanged += (_, _) => ApplyMapping();
        layout.Controls.Add(_displayBox, 1, 0);

        layout.Controls.Add(Muted("映射方式"), 0, 1);
        _mappingBox.Dock = DockStyle.Fill;
        _mappingBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mappingBox.Items.AddRange(new object[] { "铺满整屏（推荐）", "等比缩放居中" });
        _mappingBox.SelectedIndex = 0;
        _mappingBox.SelectedIndexChanged += (_, _) => ApplyMapping();
        layout.Controls.Add(_mappingBox, 1, 1);

        layout.Controls.Add(Muted("平板比例"), 0, 2);
        var aspectRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _aspectBox.DecimalPlaces = 2;
        _aspectBox.Increment = 0.01M;
        _aspectBox.Minimum = 0.5M;
        _aspectBox.Maximum = 4M;
        _aspectBox.Value = 1.60M;
        _aspectBox.Width = 70;
        _aspectBox.ValueChanged += (_, _) => ApplyMapping();
        aspectRow.Controls.Add(_aspectBox);
        aspectRow.Controls.Add(new Label
        {
            Text = "宽 ÷ 高，仅在等比模式下生效",
            AutoSize = true,
            Margin = new Padding(8, 6, 0, 0),
            ForeColor = Color.FromArgb(95, 99, 104),
        });
        layout.Controls.Add(aspectRow, 1, 2);

        return layout;
    }

    private Control BuildStatusGroup()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddStatusRow(layout, 0, "运行状态", _stateValue);
        AddStatusRow(layout, 1, "本机地址", _addressValue);
        AddStatusRow(layout, 2, "数据速率", _rateValue);
        AddStatusRow(layout, 3, "链路质量", _qualityValue);
        AddStatusRow(layout, 4, "笔状态", _penValue);
        AddStatusRow(layout, 5, "注入权限", _adminValue);

        return layout;
    }

    private static void AddStatusRow(TableLayoutPanel layout, int row, string caption, Label value)
    {
        layout.Controls.Add(Muted(caption), 0, row);
        value.Dock = DockStyle.Fill;
        value.AutoSize = true;
        value.ForeColor = Color.FromArgb(27, 27, 31);
        layout.Controls.Add(value, 1, row);
    }

    private Control BuildSecurityGroup()
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };

        _allowCheck.Text = "只接受上面填写的平板地址（防止局域网内其它设备乱注入）";
        _allowCheck.AutoSize = true;
        _allowCheck.ForeColor = Color.FromArgb(27, 27, 31);
        _allowCheck.CheckedChanged += (_, _) =>
        {
            _session.AllowedSender = _allowCheck.Checked ? _allowBox.Text.Trim() : null;
            AppendLog(_allowCheck.Checked
                ? $"已启用来源限制：{_allowBox.Text.Trim()}"
                : "已关闭来源限制，接受局域网内任意设备");
        };
        layout.Controls.Add(_allowCheck);

        var applyButton = new Button
        {
            Text = "应用地址限制",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 0),
        };
        applyButton.Click += (_, _) =>
        {
            _session.AllowedSender = _allowCheck.Checked ? _allowBox.Text.Trim() : null;
            AppendLog($"来源限制已更新：{(_allowCheck.Checked ? _allowBox.Text.Trim() : "不限")}");
        };
        layout.Controls.Add(applyButton);

        return layout;
    }

    private Control BuildLogPanel()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        _log.Dock = DockStyle.Fill;
        _log.IntegralHeight = false;
        _log.Font = new Font("Consolas", 9f);
        _log.HorizontalScrollbar = true;
        layout.Controls.Add(_log, 0, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        var clearButton = new Button { Text = "清空日志", AutoSize = true };
        clearButton.Click += (_, _) =>
        {
            _log.Items.Clear();
            _logCount = 0;
        };
        buttons.Controls.Add(clearButton);

        var minimizeButton = new Button { Text = "缩到托盘", AutoSize = true };
        minimizeButton.Click += (_, _) => HideToTray();
        buttons.Controls.Add(minimizeButton);

        var exitButton = new Button { Text = "停止并退出", AutoSize = true };
        exitButton.Click += (_, _) => ShutdownAndExit();
        buttons.Controls.Add(exitButton);

        layout.Controls.Add(buttons, 0, 1);
        return layout;
    }

    private static Label Muted(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 6, 8, 0),
            ForeColor = Color.FromArgb(95, 99, 104),
        };
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("暂停/恢复注入", null, (_, _) => ToggleInjection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止并退出", null, (_, _) => ShutdownAndExit());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "无线手写板接收端";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
    }

    // ------------------------------------------------------------------ 行为

    private void LoadDisplays()
    {
        _displayBox.Items.Clear();
        var displays = DisplayTarget.Enumerate();
        if (displays.Length == 0)
        {
            _displayBox.Items.Add(DisplayTarget.Primary());
        }
        else
        {
            foreach (var display in displays) _displayBox.Items.Add(display);
        }

        if (_displayBox.Items.Count > 0) _displayBox.SelectedIndex = 0;
        ApplyMapping();
    }

    private void ApplyMapping()
    {
        if (_displayBox.SelectedItem is DisplayTarget display)
        {
            _injector.Display = display;
        }

        _injector.Mode = _mappingBox.SelectedIndex == 1 ? MappingMode.AspectFit : MappingMode.Stretch;
        _injector.TabletAspect = (double)_aspectBox.Value;
    }

    private void ToggleListening()
    {
        if (_session.IsRunning)
        {
            _session.Stop();
            _startButton.Text = "开始监听";
            _portBox.Enabled = true;
            AppendLog("已停止监听。");
            return;
        }

        var port = (int)_portBox.Value;
        try
        {
            ApplyMapping();
            _session.Start(port);
            _startButton.Text = "停止监听";
            _portBox.Enabled = false;
            AppendLog($"正在监听 UDP {port}，等待平板连接…");
        }
        catch (Exception ex)
        {
            AppendLog($"无法监听 UDP {port}：{ex.Message}");
            MessageBox.Show(
                this,
                $"无法在端口 {port} 上监听：\n{ex.Message}\n\n" +
                "常见原因：端口被别的程序占用，或者防火墙拦截。",
                "启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ToggleInjection()
    {
        _injectionEnabled = !_injectionEnabled;
        _session.InjectionEnabled = _injectionEnabled;
        _injectToggle.Text = _injectionEnabled ? "暂停注入" : "恢复注入";
        AppendLog(_injectionEnabled ? "已恢复笔输入注入。" : "已暂停注入（仍统计报文）。");
    }

    private void OnStatusChanged(PenSessionStatus status)
    {
        if (IsDisposed) return;

        void Update()
        {
            if (IsDisposed) return;

            _stateValue.Text = status.IsRunning
                ? $"监听中 · UDP {status.Port}"
                : "未监听";
            _stateValue.ForeColor = status.IsRunning
                ? Color.FromArgb(24, 128, 56)
                : Color.FromArgb(154, 160, 166);

            _rateValue.Text = status.IsRunning
                ? $"{status.Rate:F0} 报文/秒 · 累计 {status.Stats.Packets}"
                : "—";

            var dropped = status.Stats.DroppedPackets;
            _qualityValue.Text = status.Stats.Packets == 0
                ? "暂无数据"
                : $"丢包 {dropped} · 断序 {status.Stats.SequenceGaps} · 兜底抬笔 {status.ForcedUps}";
            _qualityValue.ForeColor = dropped == 0
                ? Color.FromArgb(24, 128, 56)
                : Color.FromArgb(178, 106, 0);

            _penValue.Text = status.PenDown ? "按下" : "抬起";
            _penValue.ForeColor = status.PenDown
                ? Color.FromArgb(178, 106, 0)
                : Color.FromArgb(27, 27, 31);

            _adminValue.Text = status.HasInjected
                ? "正常（已成功注入）"
                : "等待首次注入…";
            _adminValue.ForeColor = status.HasInjected
                ? Color.FromArgb(24, 128, 56)
                : Color.FromArgb(95, 99, 104);

            if (!string.IsNullOrEmpty(status.LastInjectionError))
            {
                _adminValue.Text = $"注入失败：{status.LastInjectionError}";
                _adminValue.ForeColor = Color.FromArgb(197, 34, 31);
            }

            if (status.LastSender != null)
            {
                _tray.Text = Truncate($"无线手写板 · {status.LastSender} · {status.Rate:F0}/s", 63);
            }
        }

        if (InvokeRequired) BeginInvoke(Update);
        else Update();
    }

    private void RefreshAddress()
    {
        _addressValue.Text = PenProtocol.DescribeLocalAddresses();
    }

    private void AppendLog(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message)));
            return;
        }

        _log.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (++_logCount > LogCapacity)
        {
            _log.Items.RemoveAt(0);
            _logCount--;
        }
        _log.TopIndex = Math.Max(0, _log.Items.Count - 1);
    }

    private static string Truncate(string text, int max)
    {
        return text.Length <= max ? text : text[..max];
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        AppendLog("已缩到托盘，双击托盘图标可以回来。");
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        BringToFront();
        Activate();
        // 提权进程的 Activate() 常常只是闪烁任务栏，不一定真的置顶。
        // 这里用 SetForegroundWindow 配合临时置顶再取消，保证窗口真的跳到前面。
        ForceForeground(Handle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private static void ForceForeground(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(handle, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetForegroundWindow(handle);
    }

    private void ShutdownAndExit()
    {
        _uiTimer.Stop();
        _tray.Visible = false;
        try
        {
            _session.Stop();
            _session.Dispose();
            _injector.Dispose();
        }
        catch
        {
            // 退出路径不抛异常
        }
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 点右上角关闭只缩到托盘：手写板场景下用户只是想把它挪开，不是想退出
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>
    /// 处理「重复启动」广播：把窗口从托盘/最小化状态唤到前台。
    /// 这样用户重复双击启动脚本时，看到的是已经打开的窗口，而不是一个需要
    /// 手动关掉的「已在运行」提示框。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (_restoreMessage != 0 && m.Msg == _restoreMessage)
        {
            RestoreFromTray();
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _uiTimer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

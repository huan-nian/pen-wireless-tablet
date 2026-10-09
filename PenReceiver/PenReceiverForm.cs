using System;
using System.Drawing;
using System.Windows.Forms;

namespace PenReceiver;

/// <summary>
/// 主窗口：左边是设置与实时状态，右边是事件日志。
///
/// 布局上的两个要点（都来自上一版的实际问题）：
///   1. 设置列不能太窄。上一版固定 430px，中文标签换行后把「开始监听」「暂停注入」
///      两个按钮挤到只显示一半；现在加宽到 470px，并给按钮预留固定列宽。
///   2. 按钮不设 AutoSize，按所在列拉伸，避免中文字号变化时又被挤扁。
///
/// 关掉窗口不退出程序，只是缩到托盘；真正退出用「停止并退出」或托盘菜单。
/// </summary>
public sealed class PenReceiverForm : Form
{
    private const int LogCapacity = 500;

    /// <summary>
    /// 设置列宽度。够放下「目标显示器」这类 5 字标签加一个下拉框，
    /// 同时给右侧日志留出足够宽度（日志被裁会看不清诊断信息）。
    /// </summary>
    private const int SettingsColumnWidth = 440;

    /// <summary>标签列宽度，按最长标签「目标显示器」定。</summary>
    private const int LabelColumnWidth = 88;

    private readonly PenInjector _injector;
    private readonly PenSession _session;
    private readonly ReceiverSettings _settings = ReceiverSettings.Load();

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

    // 配色：浅灰底 + 白卡片，弱化边框，接近 Windows 11 的观感
    private static readonly Color PageBack = Color.FromArgb(243, 244, 246);
    private static readonly Color TextPrimary = Color.FromArgb(27, 27, 31);
    private static readonly Color TextMuted = Color.FromArgb(95, 99, 104);
    private static readonly Color OkGreen = Color.FromArgb(24, 128, 56);
    private static readonly Color WarnOrange = Color.FromArgb(178, 106, 0);
    private static readonly Color ErrorRed = Color.FromArgb(197, 34, 31);

    public PenReceiverForm(PenInjector injector, int restoreMessage)
    {
        _injector = injector;
        _restoreMessage = restoreMessage;
        _session = new PenSession(injector);
        _session.StatusChanged += OnStatusChanged;

        Text = "无线手写板 · 接收端";
        MinimumSize = new Size(900, 560);
        Size = new Size(1000, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = PageBack;
        ForeColor = TextPrimary;
        // 刻意不用 AutoScaleMode.Dpi：它会把 Size 也按 96/当前DPI 缩放一次，
        // 而 PerMonitorV2 下系统已经处理过 DPI，于是窗口被缩小两轮
        // （实测 125% 缩放时设定 1000×660 实际得到 800×528）。
        // Font 模式会按字号自动调整控件布局，且不会动窗口尺寸。
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        BuildTray();
        LoadDisplays();
        RestoreSettings();

        _uiTimer.Interval = 500;
        _uiTimer.Tick += (_, _) => RefreshAddress();
        _uiTimer.Start();

        AppendLog($"本机地址：{PenProtocol.DescribeLocalAddresses()}");
        AppendLog("在平板上点「扫描电脑」即可自动发现本机；也可以手动输入上面的地址。");
        AppendLog($"压感能力：{(injector.PressureSupported ? $"支持（0~{injector.MaxPressure}）" : "不支持（只能表达有/无压力）")}");
    }

    /// <summary>把上次的设置套回界面。这样重开程序不用重新填一遍。</summary>
    private void RestoreSettings()
    {
        _portBox.Value = Math.Clamp(_settings.Port, (int)_portBox.Minimum, (int)_portBox.Maximum);
        _allowBox.Text = _settings.AllowedSender;
        _allowCheck.Checked = _settings.RestrictSender;
        if (_allowCheck.Checked)
        {
            _session.AllowedSender = _settings.AllowedSender;
        }

        if (string.Equals(_settings.MappingMode, nameof(MappingMode.AspectFit), StringComparison.OrdinalIgnoreCase))
        {
            _mappingBox.SelectedIndex = 1;
        }

        if (double.TryParse(_settings.TabletAspect, out var aspect) &&
            aspect >= (double)_aspectBox.Minimum && aspect <= (double)_aspectBox.Maximum)
        {
            _aspectBox.Value = (decimal)aspect;
        }

        if (_settings.DisplayIndex >= 0 && _settings.DisplayIndex < _displayBox.Items.Count)
        {
            _displayBox.SelectedIndex = _settings.DisplayIndex;
        }

        ApplyMapping();

        if (!string.IsNullOrWhiteSpace(_settings.AllowedSender) || _settings.RestrictSender)
        {
            AppendLog($"已载入上次设置：端口 {_settings.Port}" +
                      (string.IsNullOrWhiteSpace(_settings.AllowedSender)
                          ? string.Empty
                          : $"，平板地址 {_settings.AllowedSender}"));
        }
    }

    /// <summary>把界面上的当前值写回配置。</summary>
    private void PersistSettings()
    {
        _settings.Port = (int)_portBox.Value;
        _settings.AllowedSender = _allowBox.Text.Trim();
        _settings.RestrictSender = _allowCheck.Checked;
        _settings.MappingMode = _injector.Mode.ToString();
        _settings.DisplayIndex = Math.Max(0, _displayBox.SelectedIndex);
        _settings.TabletAspect = _aspectBox.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _settings.Save();
    }

    /// <summary>
    /// 句柄创建后再套一次尺寸。
    ///
    /// 只在构造函数里设 Size 并不可靠：此时窗口还没有句柄，PerMonitorV2 下
    /// 系统会按当前显示器 DPI 重新调整一次，实际尺寸可能比设定的更小
    /// （实测设定 1000×660 得到 800×528），导致底部的「安全」分组被裁掉。
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyComfortableSize();

        // 默认自动开始监听：否则每次开程序都要手点一下，
        // 而「平板连不上」最常见的原因恰恰就是忘了点。
        if (_settings.AutoStart && !_session.IsRunning)
        {
            ToggleListening();
        }
    }

    /// <summary>
    /// 把窗口调到舒适尺寸，但绝不超出屏幕工作区。
    ///
    /// 这里按工作区的比例取尺寸，而不是写死逻辑像素值：在非 100% 缩放的桌面上，
    /// Form.Size 与实际物理像素之间存在换算（实测 125% 下请求 1000 只得到 800 物理像素），
    /// 写死数值会让窗口在不同 DPI 的机器上明显偏小，把右侧日志和底部分组挤掉。
    /// </summary>
    private void ApplyComfortableSize()
    {
        var screen = Screen.FromControl(this);
        var work = screen.WorkingArea;

        // 取工作区的 62% 宽、80% 高，并限制上限，保证在小屏上也能完整放下
        var width = Math.Min((int)(work.Width * 0.62), 1250);
        var height = Math.Min((int)(work.Height * 0.80), 860);

        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        Size = new Size(width, height);
        Location = new Point(
            work.Left + (work.Width - width) / 2,
            work.Top + (work.Height - height) / 2);

        AppendLog($"窗口适配：屏幕 {work.Width}×{work.Height}，请求 {width}×{height}，" +
                  $"实际 {Width}×{Height}，DPI {DeviceDpi}");
    }

    // ------------------------------------------------------------------ 布局

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(14, 12, 14, 12),
            BackColor = PageBack,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsColumnWidth));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var settings = BuildSettingsPanel();
        settings.Margin = new Padding(0, 0, 14, 0);
        root.Controls.Add(settings, 0, 0);
        root.Controls.Add(BuildLogPanel(), 1, 0);
        Controls.Add(root);
    }

    private Control BuildSettingsPanel()
    {
        // Dock=Top + 固定高度的分组框纵向堆叠；外层可滚动，
        // 这样小窗口下也不会把底部的「安全」组裁掉。
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PageBack,
            AutoScroll = true,
        };

        var groups = new Control[]
        {
            Group("连接", BuildConnectionGroup()),
            Group("映射", BuildMappingGroup()),
            Group("状态", BuildStatusGroup()),
            Group("安全", BuildSecurityGroup()),
        };

        // Dock=Top 时，控件在 z-order 里越靠前显示在**越下面**，
        // 所以这里倒序添加就能得到「连接 → 映射 → 状态 → 安全」的自上而下顺序。
        // 注意不要再调 BringToFront：那会把顺序又翻回去（上一版就是栽在这里，
        // 结果最上面的分组变成了「安全」）。
        for (var i = groups.Length - 1; i >= 0; i--)
        {
            var group = groups[i];
            group.Dock = DockStyle.Top;
            panel.Controls.Add(group);
        }

        return panel;
    }

    private GroupBox Group(string title, Control content)
    {
        // 分组框高度按内容测量结果给，避免中文换行后内容被裁掉
        var contentHeight = content.PreferredSize.Height;
        var box = new GroupBox
        {
            Text = title,
            Height = contentHeight + 36,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 6, 12, 8),
            ForeColor = TextPrimary,
            BackColor = Color.White,
        };

        content.Dock = DockStyle.Fill;
        box.Controls.Add(content);
        return box;
    }

    private Control BuildConnectionGroup()
    {
        var layout = Grid(rows: 2);

        // ---- 第一行：端口 + 开始监听 ----
        layout.Controls.Add(Muted("监听端口"), 0, 0);
        _portBox.Minimum = 1;
        _portBox.Maximum = 65535;
        _portBox.Value = PenProtocol.DefaultPort;
        _portBox.Dock = DockStyle.Fill;
        _portBox.Margin = new Padding(0, 3, 8, 3);
        _portBox.Font = new Font("Consolas", 10f);
        layout.Controls.Add(_portBox, 1, 0);

        StyleButton(_startButton, "开始监听", primary: true);
        _startButton.Click += (_, _) => ToggleListening();
        layout.Controls.Add(_startButton, 2, 0);

        // ---- 第二行：平板地址 + 暂停注入 ----
        layout.Controls.Add(Muted("平板地址"), 0, 1);
        _allowBox.Dock = DockStyle.Fill;
        _allowBox.Margin = new Padding(0, 3, 8, 3);
        _allowBox.PlaceholderText = "例如 192.168.1.23";
        layout.Controls.Add(_allowBox, 1, 1);

        StyleButton(_injectToggle, "暂停注入", primary: false);
        _injectToggle.Click += (_, _) => ToggleInjection();
        layout.Controls.Add(_injectToggle, 2, 1);

        return layout;
    }

    private Control BuildMappingGroup()
    {
        var layout = Grid(rows: 3);

        layout.Controls.Add(Muted("目标显示器"), 0, 0);
        _displayBox.Dock = DockStyle.Fill;
        _displayBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _displayBox.Margin = new Padding(0, 3, 0, 3);
        _displayBox.SelectedIndexChanged += (_, _) => ApplyMapping();
        layout.Controls.Add(_displayBox, 1, 0);
        layout.SetColumnSpan(_displayBox, 2);

        layout.Controls.Add(Muted("映射方式"), 0, 1);
        _mappingBox.Dock = DockStyle.Fill;
        _mappingBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mappingBox.Margin = new Padding(0, 3, 0, 3);
        _mappingBox.Items.AddRange(new object[] { "铺满整屏（推荐）", "等比缩放居中" });
        _mappingBox.SelectedIndex = 0;
        _mappingBox.SelectedIndexChanged += (_, _) => ApplyMapping();
        layout.Controls.Add(_mappingBox, 1, 1);
        layout.SetColumnSpan(_mappingBox, 2);

        layout.Controls.Add(Muted("平板比例"), 0, 2);
        var aspectRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 3),
        };
        _aspectBox.DecimalPlaces = 2;
        _aspectBox.Increment = 0.01M;
        _aspectBox.Minimum = 0.5M;
        _aspectBox.Maximum = 4M;
        _aspectBox.Value = 1.60M;
        _aspectBox.Width = 76;
        _aspectBox.Font = new Font("Consolas", 10f);
        _aspectBox.ValueChanged += (_, _) => ApplyMapping();
        aspectRow.Controls.Add(_aspectBox);
        aspectRow.Controls.Add(new Label
        {
            Text = "宽 ÷ 高，仅在等比模式下生效",
            AutoSize = true,
            Margin = new Padding(10, 6, 0, 0),
            ForeColor = TextMuted,
        });
        layout.Controls.Add(aspectRow, 1, 2);
        layout.SetColumnSpan(aspectRow, 2);

        return layout;
    }

    private Control BuildStatusGroup()
    {
        var layout = Grid(rows: 6, rowHeight: 25);

        AddStatusRow(layout, 0, "运行状态", _stateValue);
        AddStatusRow(layout, 1, "本机地址", _addressValue);
        AddStatusRow(layout, 2, "数据速率", _rateValue);
        AddStatusRow(layout, 3, "链路质量", _qualityValue);
        AddStatusRow(layout, 4, "笔状态", _penValue);
        AddStatusRow(layout, 5, "注入权限", _adminValue);

        return layout;
    }

    private Control BuildSecurityGroup()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };

        _allowCheck.Text = "只接受上面填写的平板地址（防止局域网内其它设备乱注入）";
        _allowCheck.AutoSize = true;
        _allowCheck.MaximumSize = new Size(SettingsColumnWidth - 40, 0);
        _allowCheck.ForeColor = TextPrimary;
        _allowCheck.Margin = new Padding(0, 0, 0, 8);
        _allowCheck.CheckedChanged += (_, _) =>
        {
            _session.AllowedSender = _allowCheck.Checked ? _allowBox.Text.Trim() : null;
            AppendLog(_allowCheck.Checked
                ? $"已启用来源限制：{_allowBox.Text.Trim()}"
                : "已关闭来源限制，接受局域网内任意设备");
        };
        layout.Controls.Add(_allowCheck, 0, 0);

        var applyButton = new Button();
        StyleButton(applyButton, "应用地址限制", primary: false);
        applyButton.AutoSize = true;
        applyButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        applyButton.Dock = DockStyle.Left;
        applyButton.Padding = new Padding(10, 4, 10, 4);
        applyButton.Click += (_, _) =>
        {
            _session.AllowedSender = _allowCheck.Checked ? _allowBox.Text.Trim() : null;
            AppendLog($"来源限制已更新：{(_allowCheck.Checked ? _allowBox.Text.Trim() : "不限")}");
        };
        layout.Controls.Add(applyButton, 0, 1);

        return layout;
    }

    private Control BuildLogPanel()
    {
        // 日志区做成白色卡片，与左侧设置区形成层次
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(1),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.White,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        _log.Dock = DockStyle.Fill;
        _log.BorderStyle = BorderStyle.None;
        _log.IntegralHeight = false;
        _log.Font = new Font("Consolas", 9.5f);
        _log.HorizontalScrollbar = true;
        _log.BackColor = Color.White;
        _log.ForeColor = TextPrimary;
        _log.Margin = new Padding(10, 10, 10, 4);
        layout.Controls.Add(_log, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(10, 0, 10, 8),
            BackColor = Color.White,
        };

        foreach (var (text, action) in new (string, Action)[]
        {
            ("清空日志", () => { _log.Items.Clear(); _logCount = 0; }),
            ("缩到托盘", HideToTray),
            ("停止并退出", ShutdownAndExit),
        })
        {
            var button = new Button();
            StyleButton(button, text, primary: false);
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Dock = DockStyle.None;
            button.Padding = new Padding(10, 4, 10, 4);
            button.Click += (_, _) => action();
            buttons.Controls.Add(button);
        }

        layout.Controls.Add(buttons, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    // ------------------------------------------------------------------ 控件工厂

    /// <summary>三列网格：标签 | 输入（拉伸） | 按钮（固定宽）。</summary>
    private static TableLayoutPanel Grid(int rows, int rowHeight = 34)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = rows,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 按钮列固定宽度，保证「开始监听」四个字完整显示
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        for (var i = 0; i < rows; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        }
        return layout;
    }

    private static void AddStatusRow(TableLayoutPanel layout, int row, string caption, Label value)
    {
        layout.Controls.Add(Muted(caption), 0, row);
        value.Dock = DockStyle.Fill;
        value.AutoSize = false;
        value.ForeColor = TextPrimary;
        value.TextAlign = ContentAlignment.MiddleLeft;
        // 地址可能很长，超出时用省略号而不是把列撑开
        value.AutoEllipsis = true;
        layout.Controls.Add(value, 1, row);
        layout.SetColumnSpan(value, 2);
    }

    private static Label Muted(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 8, 8, 0),
            ForeColor = TextMuted,
        };
    }

    private static void StyleButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.AutoSize = false;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;

        if (primary)
        {
            button.BackColor = Color.FromArgb(88, 86, 214);
            button.ForeColor = Color.White;
            button.Dock = DockStyle.Fill;
        }
        else
        {
            button.BackColor = Color.FromArgb(240, 241, 244);
            button.ForeColor = TextPrimary;
            button.FlatAppearance.BorderColor = Color.FromArgb(210, 213, 218);
            button.Dock = DockStyle.Fill;
        }
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("暂停/恢复注入", null, (_, _) => ToggleInjection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("停止并退出", null, (_, _) => ShutdownAndExit());

        _tray.Icon = LoadAppIcon() ?? SystemIcons.Application;
        _tray.Text = "无线手写板接收端";
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.Visible = true;
    }

    /// <summary>用可执行文件里内嵌的图标做托盘图标；取不到就退回系统默认。</summary>
    private static Icon? LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return null;
            return Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
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

        // 这三个控件的任何一个变化都会走到这里，顺手把设置落盘，
        // 用户就不用手动「保存」了
        PersistSettings();
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
            _stateValue.ForeColor = status.IsRunning ? OkGreen : TextMuted;

            _rateValue.Text = status.IsRunning
                ? $"{status.Rate:F0} 报文/秒 · 累计 {status.Stats.Packets}"
                : "—";

            var dropped = status.Stats.DroppedPackets;
            _qualityValue.Text = status.Stats.Packets == 0
                ? "暂无数据"
                : $"丢包 {dropped} · 断序 {status.Stats.SequenceGaps} · 兜底抬笔 {status.ForcedUps}";
            _qualityValue.ForeColor = dropped == 0 ? OkGreen : WarnOrange;

            _penValue.Text = status.PenDown ? "按下" : "抬起";
            _penValue.ForeColor = status.PenDown ? WarnOrange : TextPrimary;

            _adminValue.Text = status.HasInjected ? "正常（已成功注入）" : "等待首次注入…";
            _adminValue.ForeColor = status.HasInjected ? OkGreen : TextMuted;

            if (!string.IsNullOrEmpty(status.LastInjectionError))
            {
                _adminValue.Text = $"注入失败：{status.LastInjectionError}";
                _adminValue.ForeColor = ErrorRed;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
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

using System;
using System.Collections.Generic;
using System.IO;

namespace PenReceiver;

/// <summary>
/// 接收端的本机配置。
///
/// 存在 %APPDATA%\PenReceiver\settings.ini，是纯文本，用户可以直接看和改。
/// 为什么不用注册表：出问题时用户能自己打开文件核对，也方便随仓库附一份示例。
///
/// 目的：把「每次开机都要点一遍开始监听、填一遍平板地址」这件事去掉。
/// </summary>
public sealed class ReceiverSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PenReceiver");
            return Path.Combine(dir, "settings.ini");
        }
    }

    /// <summary>启动后是否自动开始监听。默认开，否则每次都要手点一下。</summary>
    public bool AutoStart
    {
        get => GetBool(nameof(AutoStart), true);
        set => Set(nameof(AutoStart), value ? "1" : "0");
    }

    /// <summary>上次使用的监听端口。</summary>
    public int Port
    {
        get => GetInt(nameof(Port), PenProtocol.DefaultPort);
        set => Set(nameof(Port), value.ToString());
    }

    /// <summary>是否启用来源限制。</summary>
    public bool RestrictSender
    {
        get => GetBool(nameof(RestrictSender), false);
        set => Set(nameof(RestrictSender), value ? "1" : "0");
    }

    /// <summary>允许的平板地址。</summary>
    public string AllowedSender
    {
        get => Get(nameof(AllowedSender));
        set => Set(nameof(AllowedSender), value);
    }

    /// <summary>映射方式：Stretch 或 AspectFit。</summary>
    public string MappingMode
    {
        get => Get(nameof(MappingMode));
        set => Set(nameof(MappingMode), value);
    }

    /// <summary>目标显示器序号（0 表示列表里第一个，即主屏）。</summary>
    public int DisplayIndex
    {
        get => GetInt(nameof(DisplayIndex), 0);
        set => Set(nameof(DisplayIndex), value.ToString());
    }

    /// <summary>平板长宽比（宽 ÷ 高）。</summary>
    public string TabletAspect
    {
        get => Get(nameof(TabletAspect));
        set => Set(nameof(TabletAspect), value);
    }

    public static ReceiverSettings Load()
    {
        var settings = new ReceiverSettings();
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return settings;

            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;

                var index = trimmed.IndexOf('=');
                if (index <= 0) continue;

                var key = trimmed[..index].Trim();
                var value = trimmed[(index + 1)..].Trim();
                settings._values[key] = value;
            }
        }
        catch (Exception)
        {
            // 配置读不出来就用默认值，不能因为配置问题起不来
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                "# 无线手写板 · 接收端设置",
                "# 可以直接编辑；改完重启接收端生效。",
                string.Empty,
            };
            foreach (var pair in _values)
            {
                lines.Add($"{pair.Key} = {pair.Value}");
            }

            File.WriteAllLines(path, lines, System.Text.Encoding.UTF8);
        }
        catch (Exception)
        {
            // 保存失败不影响本次使用
        }
    }

    private string Get(string key) => _values.TryGetValue(key, out var value) ? value : string.Empty;

    private void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) _values.Remove(key);
        else _values[key] = value;
    }

    private bool GetBool(string key, bool fallback)
    {
        var raw = Get(key);
        if (string.IsNullOrEmpty(raw)) return fallback;
        return raw is "1" or "true" or "True" or "yes";
    }

    private int GetInt(string key, int fallback)
    {
        return int.TryParse(Get(key), out var value) ? value : fallback;
    }
}

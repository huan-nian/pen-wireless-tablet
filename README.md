# Pen 无线手写板

把 Android 平板 / 手机当作电脑的外接手写板：Android 端采集触控笔的坐标、压力、
倾斜等数据，通过 UDP 发到同一局域网内的 Windows 电脑，Windows 端用系统笔输入
注入接口在本机还原笔迹，可直接在 Photoshop、OneNote、白板等软件里书写。

## 仓库结构

| 目录 | 平台 | 技术栈 | 职责 |
| --- | --- | --- | --- |
| `PenClient/` | Android | Kotlin + Gradle（AGP 9.2.1，minSdk 26 / targetSdk 36） | `PenCaptureView` 采集触控笔事件并封装为 UDP 报文发送 |
| `PenReceiver/` | Windows | C# / .NET 8（`net8.0-windows10.0.19041.0`）+ WinForms | `UdpPenReceiver` 监听报文，`PenInjector` 通过 `InputInjector` 注入笔输入 |

两端属于同一个项目，公用一个 git 仓库，按平台分目录。

## 通信协议

UDP 单播，默认端口 **8888**，定长 **32 字节**，全部为**小端序（Little Endian）**。

| 偏移 | 类型 | 字段 | 说明 |
| --- | --- | --- | --- |
| 0 | uint32 | `magic` | 固定 `0x50454E31`（ASCII `PEN1`），不匹配直接丢弃 |
| 4 | int32 | `action` | `0` = 按下，`1` = 移动，`2` = 抬起 |
| 8 | float | `x` | 归一化坐标 `[0,1]`，相对采集控件宽度 |
| 12 | float | `y` | 归一化坐标 `[0,1]`，相对采集控件高度 |
| 16 | float | `pressure` | 压力 `[0,1]`，Windows 端映射到 `0~1023` |
| 20 | float | `tilt` | 倾斜角，当前接收端未使用（预留） |
| 24 | float | `orientation` | 方位角，当前接收端未使用（预留） |
| 28 | float | 保留 | 恒为 `0` |

坐标以采集控件为参照系，接收端再按主屏幕分辨率换算成像素位置并做边界钳制。

## 运行

### Windows 端（接收 + 注入）

`InputInjector` 要求管理员权限，否则启动即失败。

```powershell
cd PenReceiver
dotnet run
```

首次运行需在防火墙提示中允许 **UDP 8888 入站**；控制台会打印每条报文的来源与长度。

### Android 端（采集 + 发送）

1. 把 `PenClient/app/src/main/java/com/example/penclient/PenCaptureView.kt` 中的
   `serverIp` 改成运行接收端的电脑局域网 IP（当前硬编码为 `192.168.1.107`）。
2. 构建安装：

```powershell
cd PenClient
.\gradlew.bat :app:installDebug
```

或直接用 Android Studio 打开 `PenClient/` 运行。

## 注意事项

- Windows 端**必须**以管理员身份运行，否则无法创建输入注入器。
- 两端必须在同一局域网，且电脑防火墙放行 UDP 8888 入站。
- 只有触控笔（`TOOL_TYPE_STYLUS`）事件会被采集，手指触摸不会发送，避免误触。
- 代码中的服务端 IP、端口属于本机环境相关配置，换网络环境时需要重新设置。

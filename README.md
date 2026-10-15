# Pen 无线手写板

把 Android 平板当作电脑的**无线手写板**：平板用触控笔书写，笔迹以真正的
Windows 笔输入（Windows Ink）注入到电脑上，Photoshop、OneNote、画图、Krita
等任何支持压感的软件都能直接画，无需装驱动、无需数据线。

平板上另有一排快捷按钮，可以直接控制电脑（回桌面、多任务、保存、撤销、滚轮），
以及可调的书写底色与笔迹消退时间。

```
┌──────────────────────────┐        UDP 36 / 8 字节        ┌──────────────────────────┐
│  Android 平板             │  ──────────────────────▶     │  Windows 电脑             │
│  连接页：扫描 / 连接       │   笔事件 · 快捷命令            │  接收端：监听 · 注入       │
│  书写页：左侧按钮 + 画布   │  ◀──────────────────────     │  笔输入 / 快捷键 / 滚轮    │
│  整块画布 = 电脑主屏       │      探测 / 回复（自动发现）    │  整块主屏 = 画布           │
└──────────────────────────┘                              └──────────────────────────┘
```

## 目录结构

| 路径 | 平台 | 说明 |
| --- | --- | --- |
| `PenClient/` | Android | 平板端应用（Kotlin + Gradle，AGP 9.2.1，minSdk 26 / targetSdk 36） |
| `PenCore/` | Windows | 核心类库：协议、笔注入、快捷键注入、UDP 监听、卡笔兜底、设置持久化 |
| `PenReceiver/` | Windows | 接收端界面（WinForms，**需要管理员权限**） |
| `PenReceiver.SelfTest/` | Windows | 自检程序（控制台，**不需要管理员权限**） |
| `PenProbe/` | Windows | 排查工具：用同一份注入代码向画图注入一笔并截图对比 |
| `scripts/` | — | 启动脚本与提权脚本 |
| `tools/` | — | 生成图标等一次性脚本 |
| `assets/` | — | 应用图标源图与生成产物（`.ico` / `.png`） |

核心逻辑单独放在 `PenCore` 而不是塞进 `PenReceiver`，是因为接收端必须声明管理员权限，
而自检必须能在没有管理员权限的环境里跑——两者分成两个 exe 才能做到。

## 快速开始

### 1. 电脑端

双击 **`scripts\launch-receiver.cmd`**，在 UAC 窗口点「是」。

启动后会**自动开始监听**（无需再手点按钮），窗口里能看到本机地址、实时速率、
链路质量与压感能力。首次运行请在防火墙提示里允许**专用网络**的 UDP 入站。

> **为什么必须提权**：Windows 的笔输入注入接口
> （`Windows.UI.Input.Preview.Injection.InputInjector`）只在提升后的进程里可用。
> 这不是本项目的限制，是系统设计。另外这个接口在没有权限时**抛 COM 异常**而不是
> 返回失败，所以代码里两处都做了捕获（否则程序会直接崩）。

设置会自动保存在 `%APPDATA%\PenReceiver\settings.ini`（纯文本，可直接编辑）：

```ini
AutoStart = 1                    # 启动即开始监听
Port = 8888
AllowedSender = 192.168.1.23     # 来源限制打开的平板地址（示例）
RestrictSender = 0
MappingMode = Stretch
DisplayIndex = 0
TabletAspect = 1.60
```

### 2. 平板端

```powershell
cd PenClient
.\gradlew.bat :app:installDebug
```

或用 Android Studio 打开 `PenClient/` 运行。安装后：

1. 打开应用 → 点「**扫描电脑**」（无需记 IP，接收端会自动回复自己的地址与端口），
   或手动填电脑 IP；
2. 点「**连接**」；
3. 连上后点右下角「**开始书写**」进入书写页。

### 3. 书写页

左侧一列按钮，自上而下：

| 图标 | 功能 | 电脑端动作 |
| --- | --- | --- |
| home | 回桌面 | `Win + D`，最小化所有窗口 |
| 任务 | 多任务视图 | `Win + Tab`，各窗口与虚拟桌面 |
| 保存 | 快捷保存 | `Ctrl + S` |
| 撤销 | 撤销上一步 | `Ctrl + Z` |
| 取消撤销 | 重做 | `Ctrl + Y` |
| 滚轮上 | 页面上滚 | 鼠标滚轮 `+120` |
| 滚轮下 | 页面下滚 | 鼠标滚轮 `-120` |
| 详情 | 回到连接页 | 本地操作，不发往电脑 |
| 关闭 | 退出软件 | 本地操作，并断开连接 |

底部右侧三个控件：

- **手写笔 / 触摸**：输入模式。默认「手写笔」——只采集触控笔，手掌可以自然搁在
  屏幕上；切到「触摸」后手指也能书写，**没有笔也能用**，但此时手掌碰到屏幕会被
  当成笔画，请把手抬离屏幕（触摸屏无法区分手指与手掌，这是硬件层面的限制）；
- **背景**：书写区底色，含暖白、纯白、护眼米黄、浅灰、豆沙绿、浅蓝、淡紫、深板岩、浓墨；
- **笔迹永存 / 笔迹 5 秒 / 10 秒 / 1 分钟**：停笔后自动清空**平板上**的笔迹。
  电脑上的笔迹不受影响。

三个设置都会记住，下次进入书写页沿用。

**书写约定**：整块画布等比映射到电脑主屏，指哪写哪；
笔尖离屏约 1cm 内即上报位置，电脑上会提前出现笔光标；
**笔尖按住不动约 0.6 秒即为长按，松开后电脑上弹出右键菜单**（平板会轻震提示）。
手写笔模式下手指事件被完全忽略（防手掌误触）；触摸模式下手指与笔都能写。

## 通信协议

UDP 单播，全部**小端序**。四种报文用 `magic` 区分：

| 报文 | magic | 长度 | 用途 |
| --- | --- | --- | --- |
| `PEN` | `PNB4` | 36 字节 | 笔/手指事件 |
| `HELLO` | `PNB0` | 8 字节 | 平板广播探测接收端 |
| `REPLY` | `PNB1` | 12 字节 | 接收端回复监听端口 |
| `CMD` | `PNB3` | 8 字节 | 平板快捷按钮下发的系统命令 |

`PEN` 报文布局：

| 偏移 | 类型 | 字段 | 说明 |
| --- | --- | --- | --- |
| 0 | uint32 | `magic` | 固定 `0x504E4232` |
| 4 | uint32 | `seq` | 单调递增序号，接收端据此统计丢包 |
| 8 | int32 | `action` | `0` 按下 / `1` 移动 / `2` 抬起 / `3` 悬停 |
| 12 | float | `x` | 归一化 `[0,1]`，相对画布宽度 |
| 16 | float | `y` | 归一化 `[0,1]`，相对画布高度 |
| 20 | float | `pressure` | 压力 `[0,1]`，Windows 端按**实测上界**缩放 |
| 24 | float | `tiltX` | 相对屏幕 X 轴倾角，度 |
| 28 | float | `tiltY` | 相对屏幕 Y 轴倾角，度 |
| 32 | int32 | `flags` | 位 0 = 笔尾橡皮，位 1 = 长按（= 右键），位 2 = 手指输入 |

`CMD` 报文的命令编号（两端必须严格一致，发布后不可改）：

| 编号 | 命令 | 编号 | 命令 |
| --- | --- | --- | --- |
| 1 | 回桌面 | 5 | 取消撤销 |
| 2 | 多任务 | 6 | 向上滚动 |
| 3 | 保存 | 7 | 向下滚动 |
| 4 | 撤销 | | |

Android 只提供「笔身与屏幕法线夹角 + 倾斜方向」，Windows 要的是分别相对 X/Y 轴的
倾角，因此平板侧做了一次球面分解（`PenProtocol.kt` 的 `tiltToWindows`）。

## 实测得到的三条硬约束

下面三条都是在本机实测出来的，都推翻了「照文档写就行」的直觉。改代码前值得先看一眼：

**1. 压感的有效范围取决于当前生效的笔设备，不能写死 0..1023。**

实测：压力 `0` 和 `1` 被接受，`2` 及以上一律被 `InjectPenInput` 整包拒绝，抛
`ArgumentException: Value does not fall within the expected range`。

关键在于它拒绝的是**整个报文**而不是「忽略压力」——所以只要压力超出范围，
这一笔就完全不会出现，表现为「平板上写得出来、电脑上毫无反应」。
因此 `PenInjector` 构造时会实测一次上界并在界面上报告；压力换算成整数时，
上界为 1 的情况用**阈值**而不是四舍五入（否则 0.5 会被舍成 0，等于没有压感）。

**2. 注入的笔输入不会触发 Windows 的长按右键手势。**

实测：笔按下后原地静止 1.1 秒再抬起，目标窗口收到的右键次数是 **0**。
所以「长按 = 右键」不能指望系统识别，必须由平板端判定（静止 600ms 且位移不超阈值），
在抬起报文上打标记，由接收端显式注入鼠标右键；这一笔的笔迹要整体丢弃，
否则长按时会先留下一个墨点。

**3. 鼠标绝对坐标用 0..65535 归一化值，不是像素。**

`InjectedInputMouseOptions.Absolute` 传像素会让光标落在完全错误的位置
（实测传 `480` 结果落在 `14`）。归一化基准用虚拟屏。

## 排查手段

从「不碰笔」到「接近真实书写」，代价由低到高：

1. **`scripts\selftest.cmd`**（不需要管理员）：85 项断言，覆盖协议字节序、两种映射模式、
   压力缩放、长按状态机、快捷命令到快捷键的映射、UDP 收发与探测回复。
   全部通过时退出码为 0。
2. **接收端界面**：
   - 「暂停注入」——仍然统计报文但不注入，用来区分「网络不通」和「注入失败」；
   - 「注入权限」——显示「正常（已成功注入）」即说明提权成功；
   - 「数据速率 / 丢包 / 断序 / 兜底抬笔」——直接看出链路哪一段有问题；
   - 「压感能力」——启动时实测并打印上界。
3. **`scripts\probe.cmd`**（需要管理员，会短暂接管屏幕）：绕过平板与 UDP，直接用
   接收端同一份注入代码把一条曲线画进 Windows 画图，并截图对比前后差异。
   这条路径能把问题干净地切成两半：**能画出线 → 问题在平板或网络；画不出 → 问题在注入**。

## 运行环境

- Windows 10 1809 及以上（笔注入接口要求目标框架 `10.0.19041` 及以上）。
- 电脑端需要 .NET 8 SDK：`dotnet --version` 能输出版本号。
- 平板 Android 8.0（API 26）及以上，与电脑在同一局域网。
- Windows 防火墙需放行 UDP 8888 入站（端口可在接收端里改）。
- Android 端构建需要 JDK 17 以上。若命令行 `java -version` 还是 Java 8，
  把 `JAVA_HOME` 指向 Android Studio 自带的 JBR（即 `<Android Studio 安装目录>\jbr`）。

## 手工构建

```powershell
# Windows：类库 + 接收端 + 自检 + 探针
dotnet build PenCore\PenCore.csproj
dotnet build PenReceiver\PenReceiver.csproj
dotnet build PenReceiver.SelfTest\PenReceiver.SelfTest.csproj
dotnet build PenProbe\PenProbe.csproj

# Android
cd PenClient
.\gradlew.bat :app:assembleDebug      # 产物在 app\build\outputs\apk\debug\
.\gradlew.bat :app:installDebug       # 直接装到已连接的平板
```

## 正式签名

Android 的正式版必须用**你自己的密钥库**签名，这样后续版本才能覆盖升级
（签名不一致时系统会拒绝安装）。

> **密钥库就是发布者身份**：谁拿到它，谁就能以本项目的名义发版；
> 而它一旦丢失，已发布的版本就再也无法升级——只能换包名重发，等于换了个应用。
> 所以它**不进仓库**，请单独离线备份。

### 生成密钥库（只需一次）

```powershell
keytool -genkeypair `
  -keystore signing\pen-release.keystore `
  -alias pen-release `
  -keyalg RSA -keysize 2048 -validity 10000 `
  -storepass "你的口令" -keypass "你的口令" `
  -dname "CN=你的名字, O=pen-wireless-tablet, OU=Android, C=CN"
```

`-validity 10000` 约 27 年。Android 要求证书有效期覆盖到应用的最后一次更新，
别用默认的 90 天。

再写 `signing\keystore.properties`：

```properties
storeFile=../../signing/pen-release.keystore
storePassword=你的口令
keyAlias=pen-release
keyPassword=你的口令
```

> `storeFile` 是**相对 `PenClient/app/` 模块目录**解析的，所以 `../../signing/`
> 指向仓库根。`signing/.gitignore` 已排除这两类文件。
>
> 若曾误提交过密钥库，必须 `git rm --cached` 移出索引——只加忽略规则
> 对已被跟踪的文件无效。

### 构建与校验

```powershell
cd PenClient
.\gradlew.bat :app:assembleRelease     # 产物 app\build\outputs\apk\release\
```

```powershell
# 校验签名（build-tools 版本换成你本机的）
& "$env:ANDROID_HOME\build-tools\36.1.0\apksigner.bat" verify --print-certs --verbose `
  app\build\outputs\apk\release\app-release.apk
```

应看到 `Verifies`、`Number of signers: 1`，以及
`certificate DN: CN=..., O=pen-wireless-tablet`。

> 本项目 `minSdk = 26`，产出的是 **v2 方案签名**。AGP 9 在这个配置下不生成
> v1（JAR）签名，`enableV1Signing = true` 也不改变结果——实测连 debug 包同样没有 v1。
> Android 7.0 起就用 v2 校验，因此对 `minSdk 26` 覆盖的设备没有影响。

### CI 上的签名

密钥库不进仓库，CI 由 GitHub Secrets 注入。在仓库
**Settings → Secrets and variables → Actions** 添加：

| Secret | 内容 | 获取方式 |
| --- | --- | --- |
| `KEYSTORE_BASE64` | 密钥库文件的 base64 | `[Convert]::ToBase64String([IO.File]::ReadAllBytes("signing\pen-release.keystore"))` |
| `KEYSTORE_PASSWORD` | 密钥库口令 | 生成时设的 |
| `KEYSTORE_ALIAS` | 密钥别名 | 例如 `pen-release` |

工作流会从 base64 还原密钥库、写入 `keystore.properties`，再构建 release 包，
并用 `apksigner` 复核证书主题里确实有你的署名——**密钥库弄错会直接让构建失败**，
不会悄悄产出一个签错名的包。

## Windows 安装包

普通用户推荐用安装包，不用手动解压：

```powershell
scripts\build-installer.cmd 1.0.1
```

产出 `dist\PenWirelessTablet-Setup-<版本>.exe`（约 69 MB）。特点：

- **单个文件**，双击即装，会创建开始菜单与桌面快捷方式；
- **安装时不需要联网**，也**不需要预装 .NET**——载荷是 self-contained 发布产物
  （已内含 .NET 8）压缩后嵌在安装器里；
- 在「应用和功能」里注册卸载项，卸载时会一并清理快捷方式与注册表；
- 自动检测已安装版本，已装过时按钮变为「更新」，可覆盖安装。

> **为什么不做「检测到没有 .NET 就去下载安装」？**
> 原本考虑过：安装器先探测 .NET 8，缺了就调用官方安装程序。但那样会有两个代价：
> 一是用户必须联网，二是可能出现「运行时装了一半失败」的中间状态。
> 直接把自包含程序内置，用户永远不可能因为缺环境而装不上——多出来的体积（约 60 MB）
> 换掉了整类失败场景，更划算。

安装器会在未提权时明确提示并可一键重新提权（接收端必须在提升的进程里才能注入笔输入）。

## Windows 代码签名（Authenticode）

**注意：这跟 Android 的 APK 签名完全是两回事**，不要混为一谈：

| | Android APK | Windows EXE |
| --- | --- | --- |
| 签名机制 | APK 签名方案（v2） | Authenticode |
| 凭据 | `signing\pen-release.keystore` | `signing\codesign.pfx` |
| 作用 | 让系统认可「同一开发者的升级包」 | 让 Windows 显示发布者名称 |
| 能否免费 | 能，自建 keystore 即可 | 只能是自签（有代价，见下） |

### 生成自签证书（免费）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\make-codesign-cert.ps1
```

会生成 `signing\codesign.pfx`（含私钥，签名用）与 `signing\codesign.cer`（公钥，随安装包发布）。
两者都已在 `signing\.gitignore` 中排除。

### 签名

打包脚本会自动调用，也可单独运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\sign-windows.ps1
```

### 自签证书的两条硬限制（务必先了解）

1. **无法消除 SmartScreen 拦截。**
   用户首次运行仍会看到「Windows 已保护你的电脑」。
   那个拦截基于证书声誉（是否由受信任 CA 签发、下载量多少），自签证书没有声誉。
   只有购买 CA 签发的代码签名证书（DigiCert / Sectigo 等）才能真正消除。

2. **用户必须先导入 `codesign.cer` 才会显示发布者。**
   不导入的话，签名状态是「不受信任」，文件属性里看不到名字。
   导入方法：双击 `codesign.cer` → 安装证书 → 本地计算机 →
   放入「受信任的根证书颁发机构」。

> 本项目自己的签名状态就是如此：签名数据已写入（`Get-AuthenticodeSignature`
> 能读出 `CN=Huan Nian, O=pen-wireless-tablet`），但因为证书未进受信任存储，
> 状态显示为 `UnknownError`。这是自签证书的正常表现，**不是没签上**。

## 打包与发布

本地打一个可直接发布的包：

```powershell
scripts\package-release.cmd 1.0.1
```

产物在 `dist\`（已加入 .gitignore，不进仓库）：

| 文件 | 体积 | 说明 |
| --- | --- | --- |
| `PenWirelessTablet-Setup-<版本>.exe` | 约 69 MB | **推荐给普通用户**。单文件安装包，内置运行环境 |
| `PenReceiver-<版本>-win-x64.zip` | 约 69 MB | 免安装绿色版（自包含，无需 .NET） |
| `PenReceiver-<版本>-win-x64-lite.zip` | 约 6 MB | 框架依赖版，需预装 .NET 8 |
| `PenClient-<版本>.apk` | 约 4.7 MB | 平板端，**正式签名版** |
| `codesign.cer` | 1 KB | 自签证书公钥，**请随发布一起提供**，否则用户看不到发布者 |
| `SHA256SUMS.txt` | — | 校验值，防下载损坏 |

推到 GitHub 后，`.github/workflows/release.yml` 会在推送 `v*` 标签时自动构建并创建 Release：

```powershell
git tag v1.0.0
git push origin v1.0.0
```

CI 里会先跑一遍自检（85 项断言）、再校验 APK 签名，都通过才打包发布。

脚本会先检查签名材料是否齐备，缺了就报错中止，不会拿 debug 签名凑合——
那会产出一个「看起来是正式版、装上去却是调试签名」的包，以后无法用正式密钥升级。

### 调试版与正式版并存

debug 构建的包名是 `com.HuanNian.penclient.debug`，与正式版
`com.HuanNian.penclient` 不同，两者可同时安装在一台设备上，方便对比排查。

## 应用图标

图标源图是 `assets/app-icon-source.png`（512×512）。改图标后重新生成：

```powershell
python tools\generate-app-icons.py
```

它会产出 `assets/app.ico`（含 16~256 共 7 个尺寸，小尺寸单独重采样，避免直接缩放发糊）
和 `PenClient` 各密度 mipmap 下的方形/圆形 PNG。两侧都做了圆角处理，
以贴合 Windows 11 与 Android 的图标风格。工具栏那 11 个图标由
`PenClient/tools/generate-tool-icons.py` 生成。

## 已知限制

- **本机没有真实笔数字化器，因此拿不到压感**。接收端启动时会实测压力上界并在界面上
  报告（本机测得为 1，即只能表达「有/无压力」）。要获得真正的粗细变化，需要一台
  Windows 能识别为笔设备的数字化屏；此时上界会是 1023，代码会自动用上。
- **平板必须有真正的压感笔**。手指、普通电容笔都不行：本项目只采集
  `TOOL_TYPE_STYLUS` / `TOOL_TYPE_ERASER`。不少平板的笔也只在 0/1 两档上报压力。
- **只映射整块主屏**。没有做「映射到指定窗口 / 指定区域」，也没有做坐标校准
  （旋转、非等比拉伸、边缘偏移）。等比模式可以缓解长宽比不一致导致的笔迹变形。
- **平板锁定横屏**。竖屏书写没有做旋转映射。
- **长按 = 右键由平板端判定**：静止 600ms 且位移不超过阈值。所以「按住笔停顿一下」
  会弹右键菜单，而不是在电脑上画一个点——这是有意为之。
- **单指单笔**：只跟一支笔，不处理多笔同时书写。
- **局域网明文**：报文没有加密或鉴权，同网段设备可以伪造笔输入与快捷键。
  接收端提供「只接受指定平板地址」的来源限制开关，建议开启。
- **注入的是笔事件而不是鼠标事件**，不支持把平板当普通鼠标用。
- 笔身按键（barrel button）没有映射。

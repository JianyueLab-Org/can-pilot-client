# CAN Pilot Client

CAN 连飞网络的统一飞行员客户端，支持 Microsoft Flight Simulator（2020 / 2024）与 X-Plane（11.50+ / 12），C# / .NET 10 实现。
同一套代码发布 Windows、macOS、Linux 三个平台：Windows 版同时支持 MSFS 和 X-Plane；macOS（仅 Apple Silicon ARM64）和 Linux x64（通用 tar.gz）版只接 X-Plane（MSFS 依赖 Windows 上的 SimConnect），FSD、语音、PTT、消息和设置在三个平台上一致。
已整合 [can-xplane-client](https://github.com/JianyueLab-Org/can-xplane-client) 的模拟器接入和原生交通插件协议，两种模拟器共用同一个 EXE、FSD、C# 语音引擎、消息和设置，不需要 Python 语音进程。
界面基于 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui)（Avalonia，布局参考 vPilot），
他机模型匹配按 vPilot 的做法实现。

功能：SimConnect 连接、CAN FSD 联飞与文字消息、Mumble 语音（跟随 COM1 切换 `FREQ_xxxxxx` 频道、跟随机模 COM2 监听第二频率，
加密 UDP、不通时退回 TCP 隧道）、他机注入与模型匹配、观察员模式、键盘 / 鼠标侧键 / 摇杆 PTT。

## 环境

- Windows x64、macOS 12+（仅 Apple Silicon ARM64）或 Linux x64，[.NET 10 SDK](https://dotnet.microsoft.com/download)。
- 实际联飞需要启动 MSFS（仅 Windows）或 X-Plane，准备 CAN 账号，并能访问所选语音 / FSD 服务器。
- 可选：RNNoise 麦克风降噪。Windows 需要 CMake + Visual Studio 2022 Build Tools（C++ 桌面开发）；macOS / Linux 需要 CMake 和 C 编译器（Xcode 命令行工具 / gcc）。

## 运行与构建

```powershell
dotnet run --project src/CanClient          # 从源码运行；加 -- --debug 记录调试日志
dotnet test CanMfsClient.slnx               # 单元测试 + 界面渲染测试
pwsh -File build.ps1                        # Windows：构建 RNNoise、准备 X-Plane 插件、跑测试、发布到 dist/can-pilot
```

```bash
./build.sh                                  # macOS / Linux：构建 RNNoise、跑测试、发布；--skip-rnnoise / --skip-tests / --target osx-arm64
```

构建脚本采用 .NET 10 Windows x64 自包含单文件发布，无需安装 .NET；托管依赖和原生 DLL 打进 EXE，
原生库在运行时自动解压到 .NET 缓存。分发整个 `dist/can-pilot/`（EXE、MSFS 模型库、X-Plane 插件压缩包、许可证及来源说明），入口为 `can-pilot.exe`。版本系列 `3.0` 写在 `Directory.Build.props`；
也可在构建前设置 `$env:CAN_VERSION` 覆盖，程序标题和版本检查与发布包保持一致。

macOS / Linux 同样是自包含单文件发布。macOS 输出仅支持 Apple Silicon ARM64 的 `dist/CAN Pilot.app`，内含一份 ARM64 单文件程序；`tools/make-macos-app.sh` 生成 Info.plist、图标并做 ad-hoc 签名。Linux 输出 `dist/can-pilot/` 和通用 tar.gz，入口为 `can-pilot`。

### macOS / Linux（只支持 X-Plane）

- **模拟器**：只接 X-Plane，设置里不显示 MSFS 页面和接入模式选择；从 Windows 拷来的配置照常读取，接入模式不会被改写。X-Plane 安装位置按各平台安装器的记录自动查找（macOS `~/Library/Preferences`，Linux `~/.x-plane`）。
- **音频**：通过随包的 OpenAL Soft 访问声卡（Linux 下走 PipeWire / PulseAudio / ALSA，macOS 下走 CoreAudio），Opus 编解码用托管实现 Concentus，和 Windows 版的 `opus.dll` 码流互通。RNNoise 由 `native/rnnoise/build.sh` 构建成 `librnnoise.so` / `librnnoise.dylib`，缺了只是没有降噪。
- **PTT**：键盘、鼠标侧键、摇杆都支持，绑定仍按 Windows 虚拟键码存，配置跨平台通用。
  - macOS：键盘 / 鼠标侧键用 CoreGraphics 读取，需要在“系统设置 → 隐私与安全性 → 输入监控”允许 CAN Pilot（首次使用时会请求，授权后重启客户端）；摇杆走 IOKit HID，无需额外权限。
  - Linux：直接读 evdev（`/dev/input/event*`），X11 和 Wayland 下都全局有效。键盘和鼠标节点一般只有 `input` 组可读，需执行 `sudo usermod -aG input $USER` 后重新登录；读不到时日志里会提示。
- **文件位置**：Linux 与 Windows 一样，设置和 `logs/` 放在程序目录；程序目录不可写时两者都放 `$XDG_DATA_HOME/CAN Pilot Client`（默认 `~/.local/share/CAN Pilot Client`）。macOS 的 `.app` 包里不写文件，设置和日志在 `~/Library/Application Support/CAN Pilot Client`。
- **Linux 安装**：Release 提供通用的 `linux-x64.tar.gz`，适用于 glibc 发行版，解压后直接运行 `can-pilot/can-pilot`。`desktop/` 里的菜单文件和图标可拷到 `~/.local/share/applications`、`~/.local/share/icons/hicolor/64x64/apps`。运行依赖：ICU、fontconfig、X11 基础库（Wayland 下经 XWayland）、一种中文字体：
  - Debian / Ubuntu：`sudo apt install libicu-dev fontconfig libx11-6 libice6 libsm6 libxi6 libxcursor1 libxrandr2 fonts-noto-cjk`
  - Fedora：`sudo dnf install libicu fontconfig libX11 libICE libSM libXi libXcursor libXrandr google-noto-sans-cjk-fonts`
  - 不支持 musl 发行版（如 Alpine）。
- **macOS 首次打开**：发布包未经 Apple 公证，解压后执行一次 `xattr -dr com.apple.quarantine "/Applications/CAN Pilot.app"`；麦克风权限在第一次连语音时弹出。
- **X-Plane 交通插件**：固定的 `v0.0.1` 插件发布包只有 Windows 版 `64/win.xpl`。macOS / Linux 需要从 can-xplane-client 的 `native_plugin` 构建 `mac.xpl` / `lin.xpl` 后加入插件包（见下文“X-Plane 接入”）；在此之前这两个平台可以正常联飞、语音、收发消息，但看不到他机，安装插件时会提示插件包缺少本平台版本。

### 自动发布

`.github/workflows/release.yml` 在推到默认分支时在 Windows / macOS / Linux 上跑测试、编译各平台 RNNoise、
发布 `can-pilot-v3.0.x-win-x64.zip`、`-osx-arm64.zip`、`-linux-x64.tar.gz`，再打标签并建 GitHub Release（附 `SHA256SUMS`）。macOS / Linux 包发布前会在 CI 里真的启动一次（Linux 用 Xvfb，macOS 跑原生 ARM64），30 秒内退出就不发。PR 也跑同样的测试、打包和启动检查，只是不发版。
补丁号由已有的 `v3.0.*` 标签加一得出，第一次是 `v3.0.0`；想换到 3.1 就把
`Directory.Build.props` 和工作流里的 `SERIES` 一起改。手动触发或推到其他分支只在
Actions 里留产物，不发版、不占版本号。

X-Plane 插件包 `native/XPC_for_CAN-plugin.zip` 已提交在仓库里，CI 直接使用，不需要任何 secret。

设置保存在程序目录的 `msfs_settings.json`（与旧 Python 版格式兼容，老配置可直接读入），
日志始终保存在 EXE 所在文件夹下的 `logs/yyyy-MM-dd.log`，例如 `logs/2026-10-23.log`，与启动时的工作目录无关。请把软件解压到可写目录；配置目录不可写时使用 `%LOCALAPPDATA%\CAN Pilot Client`。
默认自动写入日志，记录启动、连接、双电台调频与 RX/TX 选择、PTT 和异常；每天一个文件，跨日自动切换，同日重启追加写入。
点击“⋯ → 打开日志位置”定位日志；需要协议细节时用 `can-pilot.exe --debug` 启动。

统一客户端继续读取原有 `msfs_settings.json`，可将旧 C# 客户端配置放在 `can-pilot.exe` 旁；日志也继续使用 `logs/`。更新服务包标识改为 `can-pilot`，需在 CAN API 配置对应包，避免将旧 Python 的 2.x 版本误报为新版。

客户端保留启动时和手动的新版本检查，发现新版本时仅提醒；下载与安装由独立更新器负责。

### 开发调试：手动服务器地址

默认网络页只显示服务器选择，不显示域名、端口输入框。需要调试自定义端点时，在 `can-pilot.exe` 旁手动创建独立文件 `can-pilot.debug.json`：

```json
{
  "show_server_endpoints": true
}
```

重新打开设置窗口后显示 FSD / 语音域名、端口输入框和调试自定义选项。移除文件或改为 `false` 后再次打开设置即隐藏。此文件不提交、不自动生成、不随 Release 分发，也不由 `--debug` 或普通日志设置自动启用。

## 首次连接

1. 启动 MSFS 或 X-Plane 进入飞行，再启动客户端。
2. 设置 → 网络：填写 CID、密码，选择 FSD 和语音服务器；音频、按键通话 (PTT) 按需设置。
3. 点击“连接”，填写呼号和机型代码。观察员模式只连语音，可以不开模拟器（用 `.com1 121.800` 设频率）。
4. 在模拟器里调 COM1，语音会跟着切频道；按住 PTT 发话。
5. 在机模音频面板开启 COM2 接收，即可同时监听 COM2 有效频率；调频、关闭电台或关闭接收时自动同步。PTT 发话跟随机模的 COM1/COM2 TX 选择，两个 TX 指示互斥；都未选择时禁止发话。双频同时有话音时混音播放，界面分别显示接收灯；COM1/COM2 同频时只接收一份。需要语音服务器支持 Mumble 频道监听。机模须同步标准 SimConnect COM 频率、接收和发射状态。
6. 打开“设置 → 信息”查看实时 FSD 和语音连接状态；语音显示实际使用的 TCP / UDP 及对应的平均往返延迟（RTT，毫秒），尚无测量时显示 `-- ms`，下方显示最近的连接状态说明。

## 距离音频范围

语音连上后每 5 秒读取 `https://data.ceruleanavi.net/v1/data.json`，按坐标（大圆距离）判断同频的人是否在范围内，超出范围的人在本地静音（解码前丢弃，不播放、不亮接收灯）。语义与 can-audio `for_hjdczy` 分支的 `range_audio_protocol.md` 一致：按 CID 生成完整的 allow/deny 快照，不在快照里的人（ATIS、只连语音的观察员）默认放行；本客户端用的是内置 C# 语音引擎，直接在进程内应用快照，不经过 Python 进程。

- 飞行员 ↔ 飞行员：200 nm
- 飞行员 ↔ 管制员：按管制席位，TWR 及以下 100 nm、APP 300 nm、CTR 600 nm、FSS 2000 nm（双向一致）
- 管制员 ↔ 管制员：不限距离（自己的 CID 正以管制席位在线时按管制员算）

自己的位置取模拟器实时坐标，没有模拟器时到 data.json 里按 CID 找；找不到自己、或 data.json 超过 1 分钟拉不到时，全部放行。可在“设置 → 音频”关闭。

## X-Plane 接入

1. 打开“设置 → 模拟器”，接入模式选 `Auto`（自动检测）或 `XPlane`，MSFS 用户可选 `MSFS`；X-Plane 目录和插件配置在相邻的“X-Plane”页。
2. 填写 X-Plane 根目录，或点击“自动查找”；点击“安装 / 更新交通插件”，再重启 X-Plane。
3. 配置 CSL 模型库目录，可复用 X-CSL、xPilot、LiveTraffic 或 IVAO 已安装的 CSL；“扫描 CSL 模型”用于核对数量。点“应用”保存。
4. 同一个客户端完成登录、双电台收发、文字、飞行计划、IDENT 和应答机操作。X-Plane 侧不再运行 Python 客户端或 voice worker。

自动模式保持已接入的模拟器；两者同时运行时优先已有连接，初次同时检测到时优先 MSFS。普通联飞连接期间锁定模拟器，掉线不会把同一呼号切换到另一台；切换前先断开网络。

**同时开启两个模拟器**：自动模式同时收到 MSFS 和 X-Plane 的有效数据时，主页才出现“选择本次模拟器”下拉框；连接联飞前需明确选择其中一个。只开一个时不显示该控件，自动接入。已连飞时保持原模拟器、选择框禁用；需要切换时先断开网络再选择。关闭其中一个后选择框自动隐藏；下次重新出现双模拟器时重新选择。手动指定 MSFS / XPlane 模式时不提示选择。

X-Plane 自带 UDP RREF/DREF 提供本机状态和控制；本机原生 `XPC_for_CAN` 插件使用 v3 UDP 49900 协议接收已积分的交通位姿，负责 CSL 机型/航司/涂装匹配、贴地和 TCAS（最多 63 架）。插件没有逐架生成回执，航空器列表据实显示“已发送至插件”，不伪报已生成。

客户端与 X-Plane 在同一台电脑运行。默认自动发现信标，失败时尝试 `127.0.0.1:49000`；也可配置本机 UDP 地址和端口。MSFS 继续使用 SimConnect、原有模型扫描和 VMR 匹配。

插件包固定复用 X-Plane 仓库 `v0.0.1` 的原生插件，校验完整发布包 SHA256 后只提取插件文件；来源、协议、源码及许可证见 `native/XPLANE-NOTICES.md`。打好的插件包已提交在 `native/` 下，构建不需要再下载；只有换插件版本时才需要重新打包（需要 Python，私有仓库下载需要 `gh` 登录），再提交新的压缩包：

```powershell
python tools/package-xplane-plugin.py --source "路径/xpc-for-can-0.0.1-win-x64.zip"
```

macOS / Linux 版插件从同一份 `native_plugin` 源码构建，再加入已提交的插件包（不重新下载）：

```bash
python tools/package-xplane-plugin.py --add-only --mac-xpl 路径/mac.xpl --lin-xpl 路径/lin.xpl
```

## 模拟器设置显示与版本区分

- 设置侧栏将 **MSFS 2020、MSFS 2024、X-Plane** 挨在一起排列。MSFS 两个版本分别切换各自的包目录、扫描结果和模型规则。
- 默认只显示检测到已安装的模拟器。MSFS 根据 `UserCfg.opt` 和所配置的包目录检测；X-Plane 根据安装记录、手动指定目录或正在运行的连接检测。
- 安装检测用于控制设置侧栏显示：MSFS 启动时检测并缓存结果，未检测到对应版本就隐藏该版本页面；不据此停止模拟器连接等待或掉线重连。未安装 MSFS 2020 时只隐藏 2020，不影响 2024 或 X-Plane。安装或迁移后重启客户端重新检测；也可开启“显示所有模拟器”手动配置。
- 便携版、迁移安装没有被识别时：打开 **设置 → 模拟器 → 显示所有模拟器（包括未检测到安装的）**，即可强制显示三种模拟器，再进入对应页面指定目录。点“应用”保存此选项。
- 选择 MSFS 2020/2024 配置页是编辑对应版本的模型设置，不会切换正在联飞的模拟器连接。页面切换前会保存当前未应用的设置，避免将目录和规则写入另一版本。

## 多 PTT 设置与低配置电脑

1. 打开 **设置 → 按键通话 (PTT)**，点击“添加 PTT 按键或按钮”，按下要绑定的键盘键、鼠标侧键或摇杆按钮。
2. 重复添加即可保留多个绑定；重复的同一个输入不会重复添加。支持不同摇杆和同一摇杆的多个按钮。
3. 选中列表项可“移除选中的 PTT”，或“清除全部 PTT”。点“应用”或“确定”保存。
4. 任意一个 PTT 按住即可发话；多个同时按住时，必须全部松开才结束。多个 PTT 共用机模选择的 TX 电台，不会变成 COM1/COM2 同时发射。

性能设计：所有 PTT 共用一条普通优先级线程，不会每加一个绑定就新建线程。未连接语音、没有绑定时采用事件休眠；连接后仅轮询配置的输入，正常轮询间隔约 10 ms。同一摇杆在一轮内只读一次按钮状态；完整设备扫描只用于录制并每 3 秒刷新，拔掉的设备低频重试，状态不变不刷新 UI。录制时临时停止 PTT 发射判定，避免录制新按键时误发话。这些措施减少空闲唤醒、设备枚举和分配开销；没有在低配实机上做 CPU 占用基准测试。

低配电脑还可在“性能”页降低他机数量和显示距离；不需要他机时关闭渲染。手动指定模拟器可避免不需要的探测连接。

输入 `.help` 查看命令：`.com1` `.com2` `.x` `.chat` `.msg` `.atis` `.metar` `.wallop` `.match` `.clear`。

飞行计划支持两种填写方式：在客户端“飞行计划”窗口内填写提交，或点击窗口顶部“网页版飞行计划 ↗”，自动用默认浏览器打开 https://ceruleanavi.net/pilots/flightplan ，通过 CAN 网站填写和管理。

### Note 本地随手记

主界面的固定 **Note** 标签可像聊天一样输入并回车追加记录，无需连接服务器。内容仅保存在本次运行的内存，不发送到 FSD 或语音服务器、不写入配置或日志；以点开头的文字也不会执行为命令。断开联飞不清空，退出并重新启动软件后自动清空。需要提前清空时，选中 Note 后使用“⋯ → 清空当前消息”。

## 他机模型匹配（同 vPilot）

- **扫描**：从 `UserCfg.opt` 读 `InstalledPackagesPath`（也可在设置里手动指定），读取 Official / Community /
  Community2024 下每个包的 `layout.json`，取 `SimObjects` 下的 `aircraft.cfg` / `sim.cfg`（支持
  `[VARIATION] base_container` 涂装包）。结果缓存在 `ModelScanResult-<模拟器>.xml`，飞机装了、删了、改了会自动重扫。
- **识别**：用 vPilot 的 `data/ModelMatchingData.bin`（一万多个已知模型、ICAO 机型 / 航司表、同族机型）；
  不在库里的按 `icao_type_designator`、`atc_parking_codes`（以及 `icao_airline`）推导机型和航司。
- **规则**：自定义规则集（`.vmr`）优先，其次是按包优先级自动生成的规则，最后是默认机内置规则。
  Community 包里自带的 `.vmr`（如 FSLTL 的 `FSLTL_Rules.vmr`）扫描时自动启用。
- **匹配**：四轮——航司 + 机型、航司 + 同族机型、机型、同族机型，都没有就用默认模型。
  模拟器建不出的模型会被拉黑并给该航空器重新匹配。
- **查看映射**：“⋯ → 航空器列表”显示每架他机的模型、匹配方式和规则来源；设置 → 模型匹配里有匹配测试，
  消息栏也可以用 `.match <呼号> <机型>`。高级页可按包 / 飞机 / 涂装排除并调整包的优先级。

## 目录

| 路径 | 内容 |
|---|---|
| `src/CanClient.Core/` | 业务逻辑：FSD（`Fsd/`）、Mumble 语音（`Voice/`）、SimConnect 与他机注入（`Sim/`）、运动模型（`Traffic/`）、模型匹配（`ModelMatching/`）、PTT、设置 |
| `src/CanClient/` | Avalonia 界面（主题令牌在 `Styles/Theme/Tokens.axaml`） |
| `tests/CanClient.Tests/` | 单元测试（协议、加密、高度、模型扫描与匹配；有 MSFS 时另扫一遍真实安装） |
| `tests/CanClient.UiTests/` | 无头渲染各窗口和设置页，截图在 `bin/.../screens` |
| `native/` | Windows 的 `SimConnect.dll`、`opus.dll`，以及 RNNoise 构建脚本（`build.ps1` / `build.sh`） |
| `data/ModelMatchingData.bin` | vPilot 的模型匹配数据 |

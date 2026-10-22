# MSFS for CAN

CAN 连飞网络的 Microsoft Flight Simulator（2020 / 2024）飞行员客户端，C# / .NET 10 实现。
界面基于 [can-client-ui](https://github.com/JianyueLab-Org/can-client-ui)（Avalonia，布局参考 vPilot），
他机模型匹配按 vPilot 的做法实现。

功能：SimConnect 连接、CAN FSD 联飞与文字消息、Mumble 语音（跟随 COM1 切换 `FREQ_xxxxxx` 频道、跟随机模 COM2 监听第二频率，
加密 UDP、不通时退回 TCP 隧道）、他机注入与模型匹配、观察员模式、键盘 / 鼠标侧键 / 摇杆 PTT。

## 环境

- Windows x64，[.NET 10 SDK](https://dotnet.microsoft.com/download)。
- 实际联飞需要启动 MSFS，准备 CAN 账号，并能访问所选语音 / FSD 服务器。
- 可选：CMake + Visual Studio 2022 Build Tools（C++ 桌面开发），用于构建 RNNoise 麦克风降噪。

## 运行与构建

```powershell
dotnet run --project src/CanClient          # 从源码运行；加 -- --debug 记录调试日志
dotnet test CanMfsClient.slnx               # 单元测试 + 界面渲染测试
pwsh -File build.ps1                        # 构建 RNNoise、跑测试、发布到 dist/msfs-for-can-singlefile
```

构建脚本采用 .NET 10 Windows x64 自包含单文件发布，无需安装 .NET；托管依赖和原生 DLL 打进 EXE，
原生库在运行时自动解压到 .NET 缓存。分发整个 `dist/msfs-for-can-singlefile/`（EXE、模型库、许可证），入口为 `msfs-for-can.exe`。正式版本号在构建前设置
`$env:CAN_VERSION = "0.0.1"`；不设置时按开发版显示（报下一个补丁号并标注“开发版”）。
C# 版本从 `0.0.1` 开始独立编号。GitHub 预发布也可使用相同的完整版本号，程序标题和版本检查与发布包保持一致。

设置保存在程序目录的 `msfs_settings.json`（与旧 Python 版格式兼容，老配置可直接读入），
日志单独保存在 `logs/yyyy-MM-dd.log`，例如 `logs/2026-10-23.log`。程序目录不可写时改用 `%LOCALAPPDATA%\MSFS for CAN`。
默认自动写入日志，记录启动、连接、双电台调频与 RX/TX 选择、PTT 和异常；每天一个文件，跨日自动切换，同日重启追加写入。
点击“⋯ → 打开日志位置”定位日志；需要协议细节时用 `msfs-for-can.exe --debug` 启动。

客户端保留启动时和手动的新版本检查，发现新版本时仅提醒；下载与安装由独立更新器负责。

## 首次连接

1. 启动 MSFS 进入飞行，再启动客户端。
2. 设置 → 网络：填写 CID、密码，选择 FSD 和语音服务器；音频、按键通话 (PTT) 按需设置。
3. 点击“连接”，填写呼号和机型代码。观察员模式只连语音，可以不开模拟器（用 `.com1 121.800` 设频率）。
4. 在模拟器里调 COM1，语音会跟着切频道；按住 PTT 发话。
5. 在机模音频面板开启 COM2 接收，即可同时监听 COM2 有效频率；调频、关闭电台或关闭接收时自动同步。PTT 发话跟随机模的 COM1/COM2 TX 选择，两个 TX 指示互斥；都未选择时禁止发话。双频同时有话音时混音播放，界面分别显示接收灯；COM1/COM2 同频时只接收一份。需要语音服务器支持 Mumble 频道监听。机模须同步标准 SimConnect COM 频率、接收和发射状态。
6. 主窗口底部实时显示 FSD 和语音的连接状态；语音显示实际使用的 TCP / UDP 及对应的平均往返延迟（RTT，毫秒），尚无测量时显示 `-- ms`。鼠标悬停可查看最近的连接状态说明。

输入 `.help` 查看命令：`.com1` `.com2` `.x` `.chat` `.msg` `.atis` `.metar` `.wallop` `.match` `.clear`。

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
| `native/` | `SimConnect.dll`、`opus.dll`，以及 RNNoise 构建脚本 |
| `data/ModelMatchingData.bin` | vPilot 的模型匹配数据 |

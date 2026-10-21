# CAN MFS Client · MSFS for CAN

[项目仓库](https://github.com/JianyueLab-Org/can-mfs-client)

从 [JianyueLab-Org/can-audio](https://github.com/JianyueLab-Org/can-audio) 的
`msfs/` 提取的独立 Windows 客户端（界面名称 **MSFS for CAN**）。
支持 Microsoft Flight Simulator 的 SimConnect 连接、CAN FSD 联飞和文字消息、
Mumble 语音、COM1 频率同步、他机模型匹配与注入、观察员模式，以及键盘/鼠标侧键/摇杆 PTT。

## 环境

- Windows x64、**Python 3.12 x64**、Git（安装固定提交的语音依赖时使用）。
- 实际联飞需要启动支持 SimConnect 的 MSFS，准备 CAN 账号，并能访问所选语音/FSD 服务。
- 无需原 `can-audio` 仓库，无需 X-Plane 插件，也无需在本机部署服务端。

## 安装与启动

打开 PowerShell，克隆本仓库并安装依赖：

```powershell
git clone https://github.com/JianyueLab-Org/can-mfs-client.git
cd can-mfs-client
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
.\.venv\Scripts\python.exe run.py
```

`run.py` 自动设置工作目录和 `opus.dll` 搜索路径，也可以用绝对路径从其他目录启动。
调试日志：`python run.py --debug`（使用已安装依赖的 Python）。
使用新的虚拟环境，避免旧版 `pymumble` 与 `pymumble-udp` 写入同一个模块目录。

## 首次连接

1. 启动 MSFS，进入飞行场景，再启动客户端。
2. 在设置中选择麦克风、扬声器，并配置键盘、鼠标侧键或摇杆 PTT。
3. 在「网络」页分别选择语音和 FSD 服务器，或填写自定义地址和端口。
4. 填写 CAN 账号、密码和呼号，连接网络。
5. 在模拟器中调整 COM1 频率，客户端会同步切换语音频道；按住 PTT 发话。

需要显示他机时，在设置中选择 MSFS 的飞机包目录（Community / Official 等）
供模型扫描。观察员模式只连语音，不创建 FSD 飞机；未连接模拟器时可手动设置频率。

设置保存到项目目录的 `msfs_settings.json`，日志为 `msfs-for-can.log*`。
更新检查保留原来的 CAN API 和 `msfs-for-can` 客户端标识。

## 麦克风降噪

`opus.dll` 已随源码提供。RNNoise 是可选的原生降噪库，未构建时仍可启动和使用语音，
但无法启用 RNNoise 降噪。完整功能需安装 **CMake** 和 **Visual Studio 2022
Build Tools（使用 C++ 的桌面开发，含 Windows SDK）**，然后执行：

```powershell
pwsh -File native/rnnoise/build.ps1 -Out .
```

脚本下载并校验固定的 RNNoise v0.2，生成 `rnnoise.dll` 和 `RNNOISE-COPYING`。

## 本地打包

```powershell
.\.venv\Scripts\python.exe -m pip install -r requirements-build.txt
pwsh -File build.ps1
```

`build.ps1` 先构建 RNNoise，再运行 PyInstaller。也可通过 `-Python <python.exe>`
指定已安装构建依赖的 Python。若已备好原生库，可直接运行
`.\.venv\Scripts\python.exe -m PyInstaller --clean --noconfirm gui.spec`。

分发整个 **`dist/msfs-for-can/`**，入口为 `msfs-for-can.exe`。
不要单独复制 EXE，也不要运行 `build/` 中的中间文件。
打包会包含 `opus.dll`、`SimConnect/SimConnect.dll`，存在时也会包含 RNNoise 及其许可证。
需要指定正式版本号时，打包前设置 `$env:CAN_VERSION = "2.2.0"`；
未设置时沿用 `version.py` 的开发版本显示。

## 验证

在根目录执行，使用同一虚拟环境：

```powershell
$env:PATH = "$PWD;$env:PATH"
.\.venv\Scripts\python.exe -m unittest discover -v
.\.venv\Scripts\python.exe smoke_gui.py
```

测试不需要真实模拟器、服务器或音频设备。原仓库的跨组件副本比较和可选的
`can-fsd` 源码对照在缺少对应仓库时会跳过；本地客户端行为测试照常运行。
离屏冒烟测试会创建主窗口及设置等对话框。

## 来源

- 源提交：`81abcbc9eb26d565ed648dcbab5686a5bf3d20db`
- 提取范围：`msfs/`、`native/rnnoise/`。
- 运行依赖从上游清单中按客户端需要整理；测试及打包文件已适配独立根目录。
- 此项目不包含 CI 工作流。源仓库未作修改。

## 目录说明

| 文件 | 用途 |
|---|---|
| `run.py` / `gui.py` | 源码启动入口 / 主界面 |
| `simlink.py` / `inject.py` | SimConnect 数据读取 / 他机注入 |
| `voice.py` / `fsdpilot.py` | Mumble 语音 / FSD 联飞协议 |
| `settings.py` / `i18n.py` | 配置持久化 / 中英文界面 |
| `requirements.txt` | 客户端运行依赖 |
| `build.ps1` / `gui.spec` | 本地完整构建 / PyInstaller 配置 |
| `native/rnnoise/` | 固定版本的 RNNoise 构建脚本 |
| `test_*.py` / `smoke_gui.py` | 单元测试 / 离屏界面验证 |

# 在本地构建自包含单文件 Windows x64 客户端（包括麦克风降噪，无需安装 .NET）。
#   pwsh -File build.ps1               输出到 dist/can-pilot
#   pwsh -File build.ps1 -SkipRnnoise  不构建 RNNoise（没装 CMake / MSVC 时）
# 版本号默认取 Directory.Build.props（当前 1.0.0）；需要时构建前设置 $env:CAN_VERSION 覆盖。
param([switch]$SkipRnnoise, [string]$Out = "$PSScriptRoot/dist/can-pilot")
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    if (-not $SkipRnnoise -and -not (Test-Path "$PSScriptRoot/rnnoise.dll")) {
        & "$PSScriptRoot/native/rnnoise/build.ps1" -Out $PSScriptRoot
    }
    if (-not (Test-Path "$PSScriptRoot/native/XPC_for_CAN-plugin.zip")) {
        python "$PSScriptRoot/tools/package-xplane-plugin.py"
        if ($LASTEXITCODE) { throw "X-Plane 原生插件打包失败" }
    }
    dotnet test CanMfsClient.slnx -c Release
    if ($LASTEXITCODE) { throw "测试失败" }
    # 不裁剪：Avalonia、JSON 设置和 protobuf 仍需要完整的运行时类型信息。
    # 原生库打进 EXE，.NET 启动时自动解压；模型库和许可证作为随包文件保留。
    dotnet publish src/CanClient/CanClient.csproj -c Release -r win-x64 --self-contained true -o $Out `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE) { throw "发布失败" }
    Write-Output "已输出统一客户端到 $Out，分发整个目录，入口为 can-pilot.exe（无需安装 .NET / Python）"
} finally {
    Pop-Location
}

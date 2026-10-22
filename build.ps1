# 在本地构建可分发的 Windows x64 客户端（包括麦克风降噪）。
#   pwsh -File build.ps1               输出到 dist/msfs-for-can
#   pwsh -File build.ps1 -SkipRnnoise  不构建 RNNoise（没装 CMake / MSVC 时）
# 正式版本号：构建前设置 $env:CAN_VERSION = "2.2.36"；不设就按开发版处理。
param([switch]$SkipRnnoise, [string]$Out = "$PSScriptRoot/dist/msfs-for-can")
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    if (-not $SkipRnnoise -and -not (Test-Path "$PSScriptRoot/rnnoise.dll")) {
        & "$PSScriptRoot/native/rnnoise/build.ps1" -Out $PSScriptRoot
    }
    dotnet test CanMfsClient.slnx -c Release
    if ($LASTEXITCODE) { throw "测试失败" }
    dotnet publish src/CanClient/CanClient.csproj -c Release -o $Out
    if ($LASTEXITCODE) { throw "发布失败" }
    Write-Output "已输出到 $Out，分发整个目录，入口为 msfs-for-can.exe"
} finally {
    Pop-Location
}

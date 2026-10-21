# 在本地构建完整的 Windows x64 客户端（包括麦克风降噪）。
param([string]$Python = "$PSScriptRoot/.venv/Scripts/python.exe")
$ErrorActionPreference = "Stop"

Push-Location $PSScriptRoot
try {
    & "$PSScriptRoot/native/rnnoise/build.ps1" -Out $PSScriptRoot
    & $Python -m PyInstaller --clean --noconfirm gui.spec
    if ($LASTEXITCODE) { throw "PyInstaller 构建失败" }
} finally {
    Pop-Location
}

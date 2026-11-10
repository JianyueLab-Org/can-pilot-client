#!/usr/bin/env bash
# 在 macOS / Linux 上构建自包含单文件客户端（只接 X-Plane，无需安装 .NET）。Windows 用 build.ps1。
#   ./build.sh                    按本机平台构建，输出到 dist/
#   ./build.sh --skip-rnnoise     不构建 RNNoise（没装 CMake / 编译器时，客户端照常运行，只是没有麦克风降噪）
#   ./build.sh --skip-tests       不跑测试
#   ./build.sh --target osx-arm64 指定目标：osx-arm64（macOS 默认）/ linux-x64 / linux-arm64
# macOS 输出 dist/CAN Pilot.app（仅 Apple Silicon ARM64）；
# Linux 输出 dist/can-pilot/ 目录和 dist/can-pilot-linux-*.tar.gz，入口为 can-pilot。
# 版本号默认取 Directory.Build.props；需要时构建前设置 CAN_VERSION 覆盖。
#
# 注意：echo 里变量紧挨中文时一律写成 ${var}——macOS 的 bash 会把全角标点的字节当成变量名的一部分。
set -euo pipefail

root=$(cd "$(dirname "$0")" && pwd)
cd "$root"

skip_rnnoise=0
skip_tests=0
target=""
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-rnnoise) skip_rnnoise=1 ;;
    --skip-tests) skip_tests=1 ;;
    --target|--rid) target=$2; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$target" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) target=osx-arm64 ;;
    Linux-x86_64) target=linux-x64 ;;
    Linux-aarch64) target=linux-arm64 ;;
    *) echo "unsupported platform $(uname -s) $(uname -m)" >&2; exit 2 ;;
  esac
fi

case "$target" in
  osx-arm64) rids=osx-arm64; lib=librnnoise.dylib; arch=arm64 ;;
  linux-x64|linux-arm64) rids=$target; lib=librnnoise.so; arch="" ;;
  *) echo "unsupported target ${target}" >&2; exit 2 ;;
esac

if [ "$skip_rnnoise" -eq 0 ]; then
  # 每个目标单独构建（macOS 仅构建 arm64 dylib），CanClient.Core.csproj 从仓库根目录取
  bash native/rnnoise/build.sh "${root}/native/rnnoise/out-${target}" "$arch"
  cp "native/rnnoise/out-${target}/${lib}" "${root}/${lib}"
  cp "native/rnnoise/out-${target}/RNNOISE-COPYING" "${root}/RNNOISE-COPYING"
fi

if [ "$skip_tests" -eq 0 ]; then
  dotnet test CanMfsClient.slnx -c Release
fi

for rid in $rids; do
  rm -rf "${root}/dist/publish-${rid}"
  # 不裁剪：Avalonia、JSON 设置和 protobuf 仍需要完整的运行时类型信息。
  dotnet publish src/CanClient/CanClient.csproj -c Release -r "$rid" --self-contained true -o "${root}/dist/publish-${rid}" \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
done

version=${CAN_VERSION:-$(sed -n "s/.*<Version Condition=\"'\$(CAN_VERSION)' == ''\">\([0-9.]*\)<\/Version>.*/\1/p" Directory.Build.props)}
case "$target" in
  osx-arm64)
    bash tools/make-macos-app.sh "${root}/dist/CAN Pilot.app" "$version" "${root}/dist/publish-${target}"
    echo "已输出 dist/CAN Pilot.app（${target}，版本 ${version}）"
    ;;
  *)
    bash tools/package-linux.sh "${root}/dist/publish-${target}" "${root}/dist/can-pilot-${target}.tar.gz"
    rm -rf "${root}/dist/can-pilot"
    mv "${root}/dist/publish-${target}" "${root}/dist/can-pilot"
    echo "已输出 dist/can-pilot/ 和 dist/can-pilot-${target}.tar.gz（版本 ${version}），入口为 can-pilot"
    ;;
esac

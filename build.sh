#!/usr/bin/env bash
# 在 macOS / Linux 上构建自包含单文件客户端（只接 X-Plane，无需安装 .NET）。Windows 用 build.ps1。
#   ./build.sh                    按本机平台构建，输出到 dist/
#   ./build.sh --skip-rnnoise     不构建 RNNoise（没装 CMake / 编译器时，客户端照常运行，只是没有麦克风降噪）
#   ./build.sh --skip-tests       不跑测试
#   ./build.sh --rid osx-x64      指定目标：osx-arm64 / osx-x64 / linux-x64 / linux-arm64
# macOS 输出 dist/CAN Pilot.app；Linux 输出 dist/can-pilot/ 目录，入口为 can-pilot。
# 版本号默认取 Directory.Build.props；需要时构建前设置 CAN_VERSION 覆盖。
set -euo pipefail

root=$(cd "$(dirname "$0")" && pwd)
cd "$root"

skip_rnnoise=0
skip_tests=0
rid=""
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-rnnoise) skip_rnnoise=1 ;;
    --skip-tests) skip_tests=1 ;;
    --rid) rid=$2; shift ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$rid" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) rid=osx-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64) rid=linux-arm64 ;;
    *) echo "unsupported platform $(uname -s) $(uname -m)" >&2; exit 2 ;;
  esac
fi

case "$rid" in
  osx-arm64) lib=librnnoise.dylib; arch=arm64 ;;
  osx-x64) lib=librnnoise.dylib; arch=x86_64 ;;
  linux-*) lib=librnnoise.so; arch="" ;;
  *) echo "unsupported target $rid" >&2; exit 2 ;;
esac

if [ "$skip_rnnoise" -eq 0 ]; then
  # 每个目标单独构建，避免把另一种架构的库打进去；CanClient.Core.csproj 从仓库根目录取
  bash native/rnnoise/build.sh "$root/native/rnnoise/out-$rid" "$arch"
  cp "native/rnnoise/out-$rid/$lib" "$root/$lib"
  cp "native/rnnoise/out-$rid/RNNOISE-COPYING" "$root/RNNOISE-COPYING"
fi

if [ "$skip_tests" -eq 0 ]; then
  dotnet test CanMfsClient.slnx -c Release
fi

publish="$root/dist/can-pilot-$rid"
rm -rf "$publish"
# 不裁剪：Avalonia、JSON 设置和 protobuf 仍需要完整的运行时类型信息。
dotnet publish src/CanClient/CanClient.csproj -c Release -r "$rid" --self-contained true -o "$publish" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false

version=${CAN_VERSION:-$(sed -n "s/.*<Version Condition=\"'\$(CAN_VERSION)' == ''\">\([0-9.]*\)<\/Version>.*/\1/p" Directory.Build.props)}
case "$rid" in
  osx-*)
    bash tools/make-macos-app.sh "$publish" "$root/dist/CAN Pilot.app" "$version"
    echo "已输出 dist/CAN Pilot.app（$rid，版本 $version）"
    ;;
  *)
    rm -rf "$root/dist/can-pilot"
    mv "$publish" "$root/dist/can-pilot"
    chmod +x "$root/dist/can-pilot/can-pilot"
    echo "已输出 dist/can-pilot/（$rid，版本 $version），分发整个目录，入口为 can-pilot"
    ;;
esac

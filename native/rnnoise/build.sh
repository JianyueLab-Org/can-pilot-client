#!/usr/bin/env bash
# 下载 xiph RNNoise v0.2 发布包、校验、构建 librnnoise.so（Linux）/ librnnoise.dylib（macOS），输出到 OUT 目录。
#   native/rnnoise/build.sh [OUT] [ARCH]
# ARCH 只对 macOS 有用：arm64 / x86_64 / "arm64;x86_64"（通用 dylib）；默认本机架构。
# Windows 用同目录的 build.ps1。
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
out=${1:-$here/out}
arch=${2:-}
url="https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz"
sha="90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37"
work="$here/work"
mkdir -p "$work" "$out"

tarball="$work/rnnoise-0.2.tar.gz"
curl -fsSL -o "$tarball" "$url"
if command -v sha256sum >/dev/null; then
  actual=$(sha256sum "$tarball" | cut -d' ' -f1)
else
  actual=$(shasum -a 256 "$tarball" | cut -d' ' -f1)
fi
if [ "$actual" != "$sha" ]; then
  echo "rnnoise-0.2.tar.gz SHA-256 mismatch: $actual" >&2
  exit 1
fi
tar -xzf "$tarball" -C "$work"

build="$work/build${arch:+-${arch//;/-}}"
args=(-S "$here" -B "$build" -DCMAKE_BUILD_TYPE=Release "-DRNNOISE_SRC=$work/rnnoise-0.2")
if [ "$(uname)" = Darwin ]; then
  lib=librnnoise.dylib
  args+=(-DCMAKE_OSX_DEPLOYMENT_TARGET=12.0)
  [ -n "$arch" ] && args+=("-DCMAKE_OSX_ARCHITECTURES=$arch")
else
  lib=librnnoise.so
fi
cmake "${args[@]}"
cmake --build "$build" --config Release

cp "$build/$lib" "$out/$lib"
cp "$work/rnnoise-0.2/COPYING" "$out/RNNOISE-COPYING"
echo "$out/$lib"

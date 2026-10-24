#!/usr/bin/env bash
# 把 Linux 的自包含单文件发布目录打成 tar.gz：解压出来是 can-pilot/ 目录，入口 can-pilot/can-pilot。
#   tools/package-linux.sh <发布目录> <输出的 .tar.gz>
#
# 目录里另带 desktop/can-pilot.desktop 和图标：Arch 包（packaging/arch/PKGBUILD）装到系统菜单，
# 其他发行版的用户也可以照 README 自己拷过去。
set -euo pipefail

publish=$1
output=$2
root=$(cd "$(dirname "$0")/.." && pwd)

chmod +x "$publish/can-pilot"
mkdir -p "$publish/desktop"
cp "$root/packaging/linux/can-pilot.desktop" "$publish/desktop/can-pilot.desktop"
cp "$root/src/CanClient/app-logo.png" "$publish/desktop/can-pilot.png"

staging=$(mktemp -d)
cp -R "$publish" "$staging/can-pilot"
tar -czf "$output" -C "$staging" can-pilot
rm -rf "$staging"
echo "$output"

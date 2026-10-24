#!/usr/bin/env bash
# CI 用：检查 macOS / Linux 发布目录里的随包文件。
#   tools/ci-check-package.sh <发布目录> <程序名> <本平台插件文件名（mac.xpl / lin.xpl）>
# 缺文件就失败；插件包里没有本平台的交通插件只报警告（他机显示不了，联飞、语音照常），不挡发布。
set -euo pipefail

root=$1
program=$2
plugin=$3

missing=""
for f in "$program" XPC_for_CAN-plugin.zip XPLANE-NOTICES.md RNNOISE-COPYING; do
  [ -e "${root}/${f}" ] || missing="${missing} ${f}"
done
if [ -n "$missing" ]; then
  echo "::error::${root} 里缺这些文件:${missing}"
  exit 1
fi
if ! unzip -l "${root}/XPC_for_CAN-plugin.zip" | grep -q "XPC_for_CAN/64/${plugin}"; then
  echo "::warning::XPC_for_CAN-plugin.zip 里没有 ${plugin}，这个平台暂时无法安装 X-Plane 交通插件"
fi

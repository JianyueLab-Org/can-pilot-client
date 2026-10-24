#!/usr/bin/env bash
# 把 macOS 的自包含单文件发布目录包成 CAN Pilot.app（只能在 macOS 上跑：要用 sips / iconutil / codesign）。
#   tools/make-macos-app.sh <发布目录> <输出的 .app 路径> <版本号>
#
# 包里：Contents/MacOS/can-pilot（单文件程序）、Contents/Resources/（模型库、X-Plane 插件包、许可证、图标）。
# 程序在 .app 里运行时设置和日志放 ~/Library/Application Support/CAN Pilot Client（见 AppPaths）。
# Info.plist 必须带 NSMicrophoneUsageDescription，否则系统不让用麦克风。
# 没有开发者证书，做 ad-hoc 签名：Apple Silicon 要求所有代码至少有 ad-hoc 签名，
# 同一个包的麦克风 / 输入监控授权也靠签名记住。
set -euo pipefail

publish=$1
app=$2
version=$3
root=$(cd "$(dirname "$0")/.." && pwd)

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

cp "$publish/can-pilot" "$app/Contents/MacOS/can-pilot"
chmod +x "$app/Contents/MacOS/can-pilot"
for f in "$publish"/*; do
  name=$(basename "$f")
  case "$name" in
    can-pilot) ;;
    # 没打进单文件的原生库只能放在程序旁边
    *.dylib) cp "$f" "$app/Contents/MacOS/" ;;
    *) cp -R "$f" "$app/Contents/Resources/" ;;
  esac
done

# 图标：从 app.ico 同源的 64 px PNG 放大生成（Dock 上略糊，但比没有强）
iconset="$(mktemp -d)/app.iconset"
mkdir -p "$iconset"
for size in 16 32 64 128 256 512; do
  sips -z "$size" "$size" "$root/src/CanClient/app-logo.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/src/CanClient/app-logo.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/app.icns"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>com.jianyuelab.can-pilot</string>
  <key>CFBundleName</key>
  <string>CAN Pilot</string>
  <key>CFBundleDisplayName</key>
  <string>CAN Pilot Client</string>
  <key>CFBundleExecutable</key>
  <string>can-pilot</string>
  <key>CFBundleIconFile</key>
  <string>app.icns</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundleVersion</key>
  <string>${version}</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.simulation-games</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSMicrophoneUsageDescription</key>
  <string>CAN Pilot 需要麦克风进行无线电语音通话。</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$app"
echo "$app"

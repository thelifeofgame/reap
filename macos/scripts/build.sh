#!/bin/bash
# 构建 build/Reap.app（Apple 芯片 + Intel 通用包，ad-hoc 签名）。
# 用法：scripts/build.sh            正式发布前设置 SIGN_IDENTITY 用开发者证书签名。
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0}"
APP=build/Reap.app

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/Reap"

if [ ! -f Resources/AppIcon.icns ]; then
  swift scripts/make-icon.swift Resources/AppIcon.icns
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Reap"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"

codesign --force --options runtime --sign "${SIGN_IDENTITY:--}" "$APP"
echo "已生成 ${APP}（${VERSION}）"

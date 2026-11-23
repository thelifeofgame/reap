#!/bin/bash
# 把 build/Reap.app 打成 build/Reap.dmg，打开后拖到“应用程序”即可安装。
set -euo pipefail
cd "$(dirname "$0")/.."

[ -d build/Reap.app ] || scripts/build.sh
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -R build/Reap.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f build/Reap.dmg
hdiutil create -volname Reap -srcfolder "$STAGE" -ov -format UDZO build/Reap.dmg >/dev/null
echo "已生成 build/Reap.dmg"

#!/bin/bash
# Puts freshly built native libraries over the installed app's own (for trying a change without building and
# installing the APK; the next install of the APK replaces them again). The game is stopped first.
# usage: host/push-lib.sh libwaylithaudio.so [...]     (files are taken from build/native/lib/)
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
"$ADB" shell am force-stop $PKG
DIR=$("$ADB" shell "su -c 'dirname \$(pm path $PKG | cut -d: -f2)'" | tr -d '\r')/lib/arm64
for f in "$@"; do
  "$ADB" push "$(cygpath -w "build/native/lib/$f")" /data/local/tmp/push-lib.tmp > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/push-lib.tmp $DIR/$f; chown system:system $DIR/$f; chmod 755 $DIR/$f; chcon u:object_r:apk_data_file:s0 $DIR/$f; md5sum $DIR/$f'" | tr -d '\r' | cut -c1-32
  md5sum "build/native/lib/$f" | cut -c1-32
done

#!/bin/bash
# Installs build/port17.apk on the phone as root (pm install), which does not go through the system's install
# screen, then starts the activity once so that its data folders exist. usage: host/install.sh
set -e
cd "$(dirname "$0")/.."
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
PKG=com.local.waylith.port
"$ADB" push build/port17.apk /data/local/tmp/port17.apk | tail -1 | cut -c1-80
"$ADB" shell "su -c 'pm install -r /data/local/tmp/port17.apk'" | tail -1
"$ADB" shell am start -n $PKG/android.app.NativeActivity | tail -1
sleep 3
"$ADB" shell am force-stop $PKG
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell "su -c 'mkdir -p /data/media/0/Android/data/$PKG/files; chown -R $UID_:1078 /data/media/0/Android/data/$PKG; ls -la /data/media/0/Android/data/$PKG/files'"

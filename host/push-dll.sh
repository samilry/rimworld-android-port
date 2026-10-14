#!/bin/bash
# Puts freshly built assemblies of the port into the game folder on the phone (the game must be restarted to load them).
# usage: host/push-dll.sh [PortMobile.dll PortHooks.dll ...]     (default: PortMobile.dll; files are taken from build/)
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
for f in "${@:-PortMobile.dll}"; do
  "$ADB" push "$(cygpath -w "build/$f")" /data/local/tmp/push-dll.tmp > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/push-dll.tmp $GAME/$f.new; chown $UID_:1078 $GAME/$f.new; chmod 666 $GAME/$f.new; mv -f $GAME/$f.new $GAME/$f; md5sum $GAME/$f'" | tr -d '\r' | cut -c1-32
  md5sum "build/$f" | cut -c1-32
done

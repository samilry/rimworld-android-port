#!/bin/bash
# Puts build/workshop (PortWorkshop.dll and the libraries it loads) into game/workshop on the phone.
# usage: host/push-workshop.sh [file.dll ...]      (default: all of build/workshop/*.dll)
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
G=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell "su -c 'mkdir -p $G/workshop; chown $UID_:1078 $G/workshop; chmod 770 $G/workshop'"
FILES=${*:-$(cd build/workshop && ls *.dll)}
for n in $FILES; do
  "$ADB" push "$(cygpath -w build/workshop/$n)" /data/local/tmp/ws.tmp > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/ws.tmp $G/workshop/$n; chown $UID_:1078 $G/workshop/$n; chmod 660 $G/workshop/$n'"
  echo "$n $(md5sum build/workshop/$n | cut -c1-8)"
done

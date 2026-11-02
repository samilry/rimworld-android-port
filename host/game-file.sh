#!/bin/bash
# Puts a local file into the game folder on the phone under a name, or removes that name there.
# usage: host/game-file.sh put <local file> <name in game/>      host/game-file.sh rm <name in game/> ...
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
GAME=/data/media/0/Android/data/$PKG/files/game
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
case "$1" in
  put)
    "$ADB" push "$(cygpath -w "$2")" /data/local/tmp/game-file.tmp > /dev/null
    "$ADB" shell "su -c 'cp /data/local/tmp/game-file.tmp $GAME/$3; chown $UID_:1078 $GAME/$3; chmod 660 $GAME/$3; ls -la $GAME/$3'" | tr -d '\r' ;;
  rm)
    shift
    for f in "$@"; do "$ADB" shell "su -c 'rm -f $GAME/$f'"; done
    "$ADB" shell "su -c 'cd $GAME; echo *.txt'" | tr -d '\r' ;;
  *) echo "usage: host/game-file.sh put <local file> <name> | rm <name> ..."; exit 1 ;;
esac

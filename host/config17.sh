#!/bin/bash
# The 1.7 game's Config folder on the phone is the user's (mod settings, the mod list, the game's preferences).
#   host/config17.sh backup <label>     copies it to build/backup17/config-1.7-<label>.tar
#   host/config17.sh diff <label>       what differs now from that backup
#   host/config17.sh restore <label>    puts the backup's files back (files the game added since stay)
# The game is not stopped or started here; stop it before a restore, or it writes its own state back on exit.
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
C="/data/media/0/Android/data/$PKG/files/home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
TAR="build/backup17/config-1.7-$2.tar"
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
case "$1" in
  backup)
    mkdir -p build/backup17
    "$ADB" shell "su -c 'cd \"$C\" && tar cf /data/local/tmp/config17.tar Config'"
    "$ADB" pull /data/local/tmp/config17.tar "$(cygpath -w "$TAR")" | tail -1 ;;
  diff|restore)
    [ -f "$TAR" ] || { echo "no backup $TAR"; exit 1; }
    "$ADB" push "$(cygpath -w "$TAR")" /data/local/tmp/config17.tar > /dev/null
    "$ADB" shell "su -c 'rm -rf /data/local/tmp/config17; mkdir /data/local/tmp/config17; cd /data/local/tmp/config17 && tar xf ../config17.tar'"
    if [ "$1" = diff ]; then
      "$ADB" shell "su -c 'cd /data/local/tmp/config17/Config; for f in *; do a=\$(md5sum \"\$f\" | cut -c1-32); b=\$(md5sum \"$C/Config/\$f\" 2>/dev/null | cut -c1-32); [ \"\$a\" = \"\$b\" ] || echo \"differs: \$f\"; done; echo compared'" | tr -d '\r'
    else
      "$ADB" shell "su -c 'cd /data/local/tmp/config17/Config; for f in *; do a=\$(md5sum \"\$f\" | cut -c1-32); b=\$(md5sum \"$C/Config/\$f\" 2>/dev/null | cut -c1-32); if [ \"\$a\" != \"\$b\" ]; then cp \"\$f\" \"$C/Config/\$f\"; chown $UID_:1078 \"$C/Config/\$f\"; chmod 660 \"$C/Config/\$f\"; echo \"restored: \$f\"; fi; done; echo done'" | tr -d '\r'
    fi
    "$ADB" shell "su -c 'rm -rf /data/local/tmp/config17 /data/local/tmp/config17.tar'" ;;
  *) echo "usage: host/config17.sh backup|diff|restore <label>"; exit 1 ;;
esac

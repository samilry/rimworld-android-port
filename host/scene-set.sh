#!/bin/bash
# Puts the 1.7 game on the phone into one of the states the measurements use, or back into the state the user plays
# in. The game is stopped first; nothing is started.
#   vanilla [speed]   official content only, the save vanilla100 (100 colonists), camera on cell 112,131 at size 24
#   mods [speed]      the user's 106 mods, the save port17-perf (the race mods' colonists), camera in the middle at 24
#   menu-vanilla      official content only, no scene (the game stays at the main menu)
#   menu-mods         the user's 106 mods, no scene
#   play              the user's 106 mods, no test saves in Saves, no switch files
# In the measuring states game/memwatch.txt (5) is there too: the runtime's and the engine's own accounts every 5 s.
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
B=/data/media/0/Android/data/$PKG/files
C="$B/home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
STATE=$1; SPEED=${2:-3}
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell am force-stop $PKG
put() { # local file, name in game/
  "$ADB" push "$(cygpath -w "$1")" /data/local/tmp/scene-set.tmp > /dev/null
  "$ADB" shell "su -c 'cp /data/local/tmp/scene-set.tmp $B/game/$2; chown $UID_:1078 $B/game/$2; chmod 660 $B/game/$2'"
}
sh() { "$ADB" shell "su -c '$1'" | tr -d '\r'; }
# Everything a measuring state may have left behind.
sh "rm -f $B/game/perftest.txt $B/game/perfcmd.txt $B/game/memwatch.txt $B/game/runtime.env $B/game/spawntest.txt; for f in \"$C\"/Saves/*.rws; do [ -f \"\$f\" ] && mv \"\$f\" $B/test-saves/; done; true"
case "$STATE" in
  vanilla|menu-vanilla)
    "$ADB" push "$(cygpath -w build/ModsConfig.vanilla17.xml)" /data/local/tmp/scene-set.tmp > /dev/null
    sh "cp /data/local/tmp/scene-set.tmp \"$C/Config/ModsConfig.xml\"; chown $UID_:1078 \"$C/Config/ModsConfig.xml\"" ;;
  mods|menu-mods|play)
    sh "cp $B/test-saves/ModsConfig.user-106mods.xml \"$C/Config/ModsConfig.xml\"; chown $UID_:1078 \"$C/Config/ModsConfig.xml\"" ;;
  *) echo "usage: host/scene-set.sh vanilla|mods|menu-vanilla|menu-mods|play [speed]"; exit 1 ;;
esac
case "$STATE" in
  vanilla) SAVE=vanilla100; printf 'load vanilla100\nspeed %s\ncamera 24 112 131\ninterval 5\n' "$SPEED" > build/perftest.txt ;;
  mods) SAVE=port17-perf; printf 'load port17-perf\nspeed %s\ncamera 24\ninterval 5\n' "$SPEED" > build/perftest.txt ;;
  *) SAVE= ;;
esac
if [ -n "$SAVE" ]; then
  sh "cp $B/test-saves/$SAVE.rws \"$C/Saves/$SAVE.rws\"; chown $UID_:1078 \"$C/Saves/$SAVE.rws\""
  put build/perftest.txt perftest.txt
fi
if [ "$STATE" != play ]; then
  echo 5 > build/memwatch.txt
  put build/memwatch.txt memwatch.txt
  # The Milira Imperium mod asks for a preset when its settings file is missing, and that dialog holds the scene up.
  sh "[ -f \"$C/Config/Mod_3588393755_Settings_MI_Main.xml\" ] || cp $B/test-saves/Mod_3588393755_Settings_MI_Main.xml \"$C/Config/\"; true"
fi
sh "md5sum \"$C/Config/ModsConfig.xml\" | cut -c1-32; ls \"$C/Saves\"; cd $B/game; echo *.txt"
echo "state: $STATE"

#!/bin/bash
# One UI smoke batch on the test package: its mod list becomes the minimal base (Harmony, the game and DLCs, HugsLib)
# plus the given mods, the game is started, host/ui-smoke.js walks the interface, and the logs are pulled.
#   host/ui-smoke-batch.sh NAME packageId...       (a driver serving on RW_DRIVER_PORT=47198 for the test package)
# Back up the test package's Config/ModsConfig.xml first and put it back afterwards; nothing is saved.
cd "$(dirname "$0")/.."
A="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
M=/data/media/0/Android/data/com.local.waylith.porttest/files
D="$M/home/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
name=$1; shift
mkdir -p logs/smoke
MSYS_NO_PATHCONV=1 timeout 20 "$A" exec-out "su -c 'cat \"$D/Config/ModsConfig.xml\"'" </dev/null > logs/smoke/ModsConfig-current.xml
python -I host/ui-smoke-config.py "$(cygpath -w logs/smoke/ModsConfig-current.xml)" "$(cygpath -w logs/smoke/MC-$name.xml)" "$@"
MSYS_NO_PATHCONV=1 timeout 20 "$A" shell "am force-stop com.local.waylith.porttest" </dev/null
MSYS_NO_PATHCONV=1 timeout 20 "$A" push "$(cygpath -w logs/smoke/MC-$name.xml)" /data/local/tmp/MC-smoke.xml >/dev/null </dev/null
MSYS_NO_PATHCONV=1 timeout 20 "$A" shell "su -c 'cat /data/local/tmp/MC-smoke.xml > \"$D/Config/ModsConfig.xml\"; am start -n com.local.waylith.porttest/com.local.waylith.port.MainActivity >/dev/null'" </dev/null
SMOKE_MODS="$*" RW_DRIVER_PORT=${RW_DRIVER_PORT:-47198} timeout 1500 node host/ui-smoke.js "$name"
MSYS_NO_PATHCONV=1 timeout 30 "$A" exec-out "su -c 'cat \"$D/Logs/Player.log\"'" </dev/null > logs/smoke/$name/Player.log
MSYS_NO_PATHCONV=1 timeout 30 "$A" exec-out "su -c 'cat \"$D/Logs/Port17.log\"'" </dev/null > logs/smoke/$name/Port17.log

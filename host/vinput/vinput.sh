#!/bin/bash
# A virtual USB mouse and keyboard on the phone, for testing the game's mouse and keyboard handling (mobile/InputMode.cs)
# without real ones: /system/bin/hid (Android's HID test command, as root) reads HID descriptors and reports as JSON
# from a pipe (hidctl.sh on the phone). Malformed JSON ends it (the devices go away with it).
#   host/vinput/vinput.sh start [kbd]     the mouse, and the keyboard too with "kbd"
#   host/vinput/vinput.sh move DX DY [N]  N reports of (DX, DY) (-127..127); prints the pointer in game pixels
#                                         (the display is turned: game x = event y, game y = 1272 - event x; a
#                                         report of 50 moves about 102 pixels on this phone)
#   host/vinput/vinput.sh click [1|2]     left or right button
#   host/vinput/vinput.sh key CODES [MOD] HID usage codes, comma separated (4 a .. 29 z, 40 Enter, 41 Esc, 44 Space)
#   host/vinput/vinput.sh stop            both devices gone, the files removed
cd "$(dirname "$0")"
. ../env.sh  # ADB
run() { timeout 60 env MSYS_NO_PATHCONV=1 "$ADB" shell "$1"; }
case "$1" in
start)
  for f in hidctl.sh reg-mouse.json reg-kbd.json; do MSYS_NO_PATHCONV=1 "$ADB" push $f /data/local/tmp/rw-$f >/dev/null; done
  run 'su -c "sh /data/local/tmp/rw-hidctl.sh start"; sleep 1; su -c "sh /data/local/tmp/rw-hidctl.sh file /data/local/tmp/rw-reg-mouse.json"'
  [ "$2" = kbd ] && run 'su -c "sh /data/local/tmp/rw-hidctl.sh file /data/local/tmp/rw-reg-kbd.json"'
  sleep 2; run 'dumpsys input 2>/dev/null | grep -E "^ +[0-9]+: Port17 Test" | sort -u'
  ;;
move)
  run "for i in \$(seq 1 ${4:-1}); do su -c 'sh /data/local/tmp/rw-hidctl.sh move $2 $3'; done; sleep 0.3; dumpsys input 2>/dev/null | grep 'source=MOUSE' | grep -o 'pointers=\[0: ([0-9.]*, [0-9.]*)' | tail -1" | sed -E 's/.*\(([0-9.]+), ([0-9.]+)\).*/\1 \2/' | awk '{printf "pointer at %d, %d\n", $2, 1272 - $1}'
  ;;
click)
  run "su -c 'sh /data/local/tmp/rw-hidctl.sh move 0 0 ${2:-1}'; sleep 0.08; su -c 'sh /data/local/tmp/rw-hidctl.sh move 0 0 0'"
  ;;
key)
  run "su -c 'sh /data/local/tmp/rw-hidctl.sh key $2 ${3:-0}'"
  ;;
stop)
  run 'su -c "sh /data/local/tmp/rw-hidctl.sh stop"; rm -f /data/local/tmp/rw-hidctl.sh /data/local/tmp/rw-reg-mouse.json /data/local/tmp/rw-reg-kbd.json; su -c "rm -f /data/local/tmp/rw-hid.log /data/local/tmp/rw-hid.fifo"'
  ;;
esac

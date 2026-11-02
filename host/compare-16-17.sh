#!/bin/bash
# One round of the comparison of the two phone ports on the same scene: the save "vanilla100" (made by the 1.6 port
# without mods: the quick test colony on a 250 map plus 97 colonists; the 1.7 game loads it as it is), camera on cell
# 112,131 at size 24, three states: paused, normal speed, triple speed. Each state starts from a fresh load of the
# save and is measured for 25 s from the moment the scene is 20 s old.
# Both games must be set up for it beforehand: official content only in their ModsConfig.xml, the save in their Saves
# folders, the 1.6 game in its test state (../tools/phone-handover.sh test), game/perftest.txt for the 1.7 game
# ("load vanilla100", "speed 0", "camera 24 112 131", "interval 5").
# usage: host/compare-16-17.sh 16|17 <label>
#   16: uses the 1.6 project's own tools (../tools/test-start.sh, diag-cmd.sh, perf-stat.sh); lines go to ../logs/perf-stat.txt
#   17: uses host/perf-start.sh and host/sched-exp.sh; lines go to logs/sched-exp.txt
# After the triple speed state the whole process is counted for 10 s (all threads: cycles and processor time), and the
# threads that used the most processor time are listed: both to logs/compare-16-17.txt.
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
WHICH=$1; LABEL=$2
P16=com.local.rimworld.rebuild
P17=com.local.waylith.port
pause() { W=0; until [ $W -ge $1 ]; do sleep 1; W=$((W + 1)); done; }
# All threads of a process for ten seconds, and its busiest threads.
whole() {
  echo "== $(date +%T) $1: the whole process at triple speed, 10 s" >> logs/compare-16-17.txt
  "$ADB" shell "su -c 'P=\$(pidof $2); simpleperf stat -p \$P --duration 10 -e cpu-cycles,instructions,task-clock 2>&1 | grep -E \"cpu-cycles|instructions|task-clock\"; top -H -b -n 2 -d 4 -p \$P -o TID,%CPU,CMD -s 2 -m 12 2>/dev/null | tail -12'" | tr -d '\r' >> logs/compare-16-17.txt
}
if [ "$WHICH" = 16 ]; then
  "$ADB" shell am force-stop $P17
  (cd .. && bash tools/test-start.sh | tail -1)
  DRAW=0
  for STEP in "paused 0" "1x 1" "3x 3"; do
    set -- $STEP
    "$ADB" logcat -c
    (cd .. && bash tools/diag-cmd.sh load vanilla100)
    W=0; until [ $W -gt 60 ] || "$ADB" logcat -d -s Unity 2>/dev/null | grep -a -q "PORTDIAG save loaded"; do sleep 2; W=$((W + 1)); done
    pause 3
    (cd .. && bash tools/diag-cmd.sh closewindows); pause 2
    (cd .. && bash tools/diag-cmd.sh camera 112 131 24); pause 2
    (cd .. && bash tools/diag-cmd.sh calm); pause 2
    (cd .. && bash tools/diag-cmd.sh speed $2)
    pause 20
    LINE=$(cd .. && bash tools/perf-stat.sh "v16-$LABEL-$1" 25 $DRAW)
    echo "$LINE"
    if [ "$1" = paused ]; then DRAW=$(echo "$LINE" | sed -n 's/.*per frame \([0-9.]*\) Mcycles.*/\1/p'); fi
  done
  whole "1.6 $LABEL" $P16
  # With PROFILE=1: where the time goes, by the 1.6 project's own sampler (../logs/v16-<label>-3x-profile.txt).
  if [ "$PROFILE" = 1 ]; then (cd .. && bash tools/profile.sh "v16-$LABEL-3x" 12 | tail -3); fi
else
  "$ADB" shell am force-stop $P16
  bash host/perf-start.sh | tail -1
  echo "== $(date +%T) vanilla100, round $LABEL" >> logs/sched-exp.txt
  for STEP in "paused 0" "1x 1" "3x 3"; do
    set -- $STEP
    WARM=20 bash host/sched-exp.sh "v17-$LABEL-$1" 25 speed=$2 | cut -c1-260
  done
  whole "1.7 $LABEL" $P17
fi

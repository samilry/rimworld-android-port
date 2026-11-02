#!/bin/bash
# The native heap by who allocated it, with the phone's own malloc debug (libc_malloc_debug.so): every live
# allocation of 16 000 bytes and more with the call stack that made it. Works on the installed app as it is (it need
# not be debuggable): the zygote starts a process through the command in the property wrap.<package>, and that
# command is a script in the app's own files that sets LIBC_DEBUG_MALLOC_OPTIONS (the property itself is too short
# to hold the options).
# usage: host/heap-debug.sh on            the next starts of the game run under malloc debug (slower)
#        host/heap-debug.sh dump <label>  the running game writes its heap; fetched to logs/heap/<label>.txt and summed
#                                         by host/heap-dump.js (libraries; pass its options after the label)
#        host/heap-debug.sh off           back to normal starts (do not leave it on: the game is slower under it)
. "$(dirname "$0")/env.sh"  # ADB
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."
PKG=com.local.waylith.port
FILES=/data/data/$PKG/files
case "$1" in
  on)
    "$ADB" shell am force-stop $PKG
    printf '#!/system/bin/sh\nexport LIBC_DEBUG_MALLOC_OPTIONS="backtrace=32 backtrace_min_size=16000 backtrace_dump_prefix=%s/heap"\nexec "$@"\n' "$FILES" > build/wrap-malloc.sh
    "$ADB" push "$(cygpath -w build/wrap-malloc.sh)" /data/local/tmp/w.sh > /dev/null
    UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
    "$ADB" shell "su -c 'cp /data/local/tmp/w.sh $FILES/w.sh; chown $UID_:$UID_ $FILES/w.sh; chmod 755 $FILES/w.sh; setprop wrap.$PKG \"sh $FILES/w.sh\"; getprop wrap.$PKG'"
    ;;
  off)
    "$ADB" shell am force-stop $PKG
    "$ADB" shell "su -c 'setprop wrap.$PKG \"\"; rm -f $FILES/w.sh $FILES/heap.*.txt'"
    echo "wrap.$PKG: '$("$ADB" shell getprop wrap.$PKG | tr -d '\r')'"
    ;;
  dump)
    LABEL=${2:-heap}; shift 2
    PID=$("$ADB" shell pidof $PKG | tr -d '\r')
    [ -z "$PID" ] && echo "$PKG is not running" && exit 1
    mkdir -p logs/heap
    # Signal 47 (SIGRTMAX - 17): malloc debug writes the heap at the next allocation.
    "$ADB" shell "su -c 'rm -f $FILES/heap.$PID.txt; kill -47 $PID'"
    W=0; until [ $W -gt 20 ] || [ -n "$("$ADB" shell "su -c 'grep -l ^END $FILES/heap.$PID.txt 2>/dev/null'" | tr -d '\r')" ]; do sleep 1; W=$((W + 1)); done
    "$ADB" shell "su -c 'cat $FILES/heap.$PID.txt'" | tr -d '\r' > "logs/heap/$LABEL.txt"
    node host/heap-dump.js "logs/heap/$LABEL.txt" "$@"
    ;;
  *) echo "usage: host/heap-debug.sh on | dump <label> [heap-dump.js options] | off"; exit 1 ;;
esac

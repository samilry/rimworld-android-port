#!/system/bin/sh
# Virtual HID devices for testing (run as root). hidctl.sh start | file <path> | move <dx> <dy> [buttons] | click [button] | stop
F=/data/local/tmp/rw-hid.fifo
kill_all() {
  for p in $(ps -A -o PID,ARGS | grep -e "$1" | grep -v grep | grep -v hidctl | awk '{print $1}'); do kill $p 2>/dev/null; done
}
byte() { if [ "$1" -lt 0 ]; then echo $((256 + $1)); else echo $1; fi; }
case "$1" in
start)
  kill_all 'commands.hid.Hid'
  kill_all 'sleep 99999'
  rm -f $F; mkfifo $F
  nohup hid $F > /data/local/tmp/rw-hid.log 2>&1 < /dev/null &
  nohup sleep 99999 > $F 2>/dev/null < /dev/null &
  ;;
file)
  cat "$2" > $F; echo >> $F
  ;;
move)
  echo "{\"id\":1,\"command\":\"report\",\"report\":[${4:-0},$(byte $2),$(byte $3),0]}" > $F
  ;;
key)
  for k in $(echo "$2" | tr ',' ' '); do
    echo "{\"id\":2,\"command\":\"report\",\"report\":[${3:-0},0,$k,0,0,0,0,0]}" > $F
    sleep 0.05
    echo "{\"id\":2,\"command\":\"report\",\"report\":[0,0,0,0,0,0,0,0]}" > $F
    sleep 0.1
  done
  ;;
stop)
  kill_all 'sleep 99999'
  sleep 1
  kill_all 'commands.hid.Hid'
  rm -f $F
  ;;
esac

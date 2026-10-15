#!/bin/bash
# Restarts the game on the phone with the PortMobile.dll in build/ and loads a save, for test runs through the MCP driver.
# Only for test games: whatever is not saved is lost. usage: host/mcp/restart-load.sh <save name>
# (the driver must be stopped first and is started again here in the background)
set -e
cd "$(dirname "$0")/../.."
SAVE="$1"
[ -n "$SAVE" ] || { echo "usage: $0 <save name>"; exit 1; }
bash host/push-dll.sh PortMobile.dll > /dev/null
node host/mcp/test-client.js rw_game '{"action":"stop","confirm":true}' rw_game '{"action":"start"}' 2>/dev/null | grep -v '^=='
(node host/mcp/driver.js serve > /dev/null 2>&1 &)
for i in $(seq 1 40); do
  sleep 3
  if node host/mcp/driver.js rw_state '{}' 2>/dev/null | grep -q '"program":"Entry"'; then break; fi
done
D="node host/mcp/driver.js"
$D rw_log '{}' rw_click '{"text":"载入"}' rw_wait '{"seconds":1.5}' > /dev/null
Y=$($D rw_screen "{\"window\":\"0\",\"contains\":\"$SAVE\"}" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{const j=JSON.parse(s.slice(s.indexOf('{')));const t=j.texts.find(x=>x.t===process.argv[1]);console.log(t?t.r[1]+32:'');})" "$SAVE")
[ -n "$Y" ] || { echo "save $SAVE not in the list"; exit 1; }
$D rw_tap "{\"x\":1731,\"y\":$Y}" > /dev/null
for i in $(seq 1 30); do
  sleep 3
  if $D rw_state '{}' 2>/dev/null | grep -q '"program":"Playing".*"loading":false'; then break; fi
done
sleep 3
$D rw_speed '{"speed":"paused"}' rw_log '{}' | grep -v '^\s*at '

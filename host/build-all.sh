#!/bin/bash
# Builds the tester's APK from a fresh clone, in the order the parts need each other (BUILDING.md): patch.dll; PortHooks,
# against the game's own assemblies as nothing is staged yet; the staged game, which patch.dll rewrites to call
# PortHooks; PortMobile and the Workshop downloader against it; PortSetup; the patched CoreLib; the native libraries
# (Slang's first build takes a quarter of an hour or more); the APK. Run host/fetch-deps.sh first.
# usage: host/build-all.sh        (PORT17_KS_PASS set; output build/port17-tester.apk)
set -e
cd "$(dirname "$0")/.."
# Asked first, not after the half hour the rest takes.
: "${PORT17_KS_PASS:?set PORT17_KS_PASS to the password of the signing keystore (port17.keystore, made on the first build)}"
# run <command...>: one step, and how long it took.
run() {
  local s=$SECONDS
  echo; echo "=== $*"
  "$@"
  echo "=== $*: $((SECONDS - s)) s"
}
run bash patch/build.sh
run bash hooks/build.sh
run bash host/stage.sh build
run bash mobile/build.sh
run bash workshop/build.sh
run bash setup/build.sh
run bash host/corelib.sh
run bash native/build.sh
PAYLOAD=port run bash host/build.sh
echo "build/port17-tester.apk: $(stat -c %s build/port17-tester.apk) bytes, $SECONDS s in all"

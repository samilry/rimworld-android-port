#!/bin/bash
# Puts the .NET runtime assemblies and the game's files on the phone, in the app's external files folder.
# The game files are taken from the owner's Steam copy; only the game's own assemblies go (the framework ones
# come from the runtime pack), plus Assets.zip, Data/, Rendering/.
# usage: host/stage.sh [runtime|game|all]     (default all; the APK must be installed and started once first)
set -e
cd "$(dirname "$0")/.."
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
PKG=com.local.waylith.port
D=/sdcard/Android/data/$PKG/files
GAME="/d/SteamLibrary/steamapps/common/RimWorld"
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
what=${1:-all}

if [ "$what" = runtime ] || [ "$what" = all ]; then
  "$ADB" shell "rm -rf $D/runtime"
  "$ADB" push "$RT" "$D/runtime" | tail -1 | cut -c1-100
fi

if [ "$what" = game ] || [ "$what" = all ]; then
  rm -rf build/game
  mkdir -p build/game
  ls "$RT"/*.dll | xargs -n1 basename | sort > build/framework-names.txt
  for f in "$GAME"/*.dll; do
    b=$(basename "$f")
    if grep -qx "$b" build/framework-names.txt; then continue; fi
    case "$b" in
      *.Native.*.dll|*.Native.dll|Microsoft.DiaSymReader.*) continue;;
    esac
    cp "$f" build/game/
  done
  cp "$GAME/Assets.zip" "$GAME/Version.txt" "$GAME/RimWorld.runtimeconfig.json" "$GAME/RimWorld.deps.json" build/game/
  cp -r "$GAME/Data" "$GAME/Rendering" build/game/
  node host/fix-arch.js build/game --prune-native
  [ -f build/PortHooks.dll ] && cp build/PortHooks.dll build/game/
  # Mono.Cecil: the hook prepares mod assemblies on the phone with it (hooks/ModPrepHost.cs).
  cp /d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0/Mono.Cecil.dll build/game/
  # Harmony's 1.7 build of 0Harmony.dll lives in the game folder (the loader hook skips the mods' own copies); patch.dll
  # applies the port's rewrites to it and to the game's assemblies (hook calls, MonoMod on bionic, font paths).
  HARMONY="/d/SteamLibrary/steamapps/workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll"
  [ -f "$HARMONY" ] && cp "$HARMONY" build/game/
  dotnet build/patch/patch.dll build/game "$RT"
  # The 1.6 Chinese language packs (this 1.7 test build ships English only; most 1.6 strings still apply).
  for p in Core Royalty Ideology Biotech Anomaly Odyssey; do
    t="/d/Harness/rimworld/Builds/AndroidData/Data/$p/Languages/ChineseSimplified (简体中文).tar"
    [ -f "$t" ] && mkdir -p "build/game/Data/$p/Languages" && cp "$t" "build/game/Data/$p/Languages/"
  done
  # Type-forwarding facades for the Unity assembly names mods were compiled against (all types -> UnityStubs).
  [ -d build/forwarders ] && cp build/forwarders/*.dll build/game/
  printf -- "-Headless\n-NoCrashDialog\n-Verbose\n" > build/game/args.txt
  echo "game staging: $(ls build/game/*.dll | wc -l) assemblies, $(du -sh build/game | cut -f1)"
  "$ADB" shell "rm -rf $D/game"
  "$ADB" push build/game "$D/game" | tail -1 | cut -c1-100
fi
# Files pushed over adb belong to the shell user; the app must own them to read them.
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell "su -c 'mkdir -p /data/media/0/Android/data/$PKG/files/home; chown -R $UID_:1078 /data/media/0/Android/data/$PKG/files; ls /data/media/0/Android/data/$PKG/files'"

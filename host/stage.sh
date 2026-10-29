#!/bin/bash
# Puts the .NET runtime assemblies and the game's files on the phone, in the app's external files folder.
# The game files are taken from the owner's Steam copy; only the game's own assemblies go (the framework ones
# come from the runtime pack), plus Assets.zip, Data/, Rendering/.
# usage: host/stage.sh [runtime|game|build|all]     (default all; the APK must be installed and started once first)
#   build   only makes build/game on the PC (nothing goes to the phone)
#   game    makes build/game and copies it over the phone's game folder. Nothing there is deleted: the folder also holds
#           the user's mods (Mods/), the phone settings (mobile.txt), the Workshop downloader (workshop/) and the launch
#           arguments (args.txt, not overwritten). Files the new build no longer has are listed, not removed.
# RW_PACKAGE=<package> puts it into another install, e.g. the side-by-side test APK (com.local.waylith.porttest).
set -e
cd "$(dirname "$0")/.."
ADB="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
export MSYS_NO_PATHCONV=1
PKG=${RW_PACKAGE:-com.local.waylith.port}
D=/sdcard/Android/data/$PKG/files
GAME="/d/SteamLibrary/steamapps/common/RimWorld"
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
what=${1:-all}

if [ "$what" = runtime ] || [ "$what" = all ]; then
  "$ADB" shell "rm -rf $D/runtime"
  "$ADB" push "$RT" "$D/runtime" | tail -1 | cut -c1-100
  # CoreLib with the runtime fork's string hash (host/corelib.sh builds it).
  if [ -f build/runtime/System.Private.CoreLib.dll ]; then
    "$ADB" push build/runtime/System.Private.CoreLib.dll "$D/runtime/" | tail -1 | cut -c1-100
  else
    echo "build/runtime/System.Private.CoreLib.dll missing (host/corelib.sh): the pack's CoreLib stays, string hashes change per launch"
  fi
  # BinaryFormatter's implementation, as the PC game ships it (host/build.sh fetches it).
  [ -f build/runtime/System.Runtime.Serialization.Formatters.dll ] && "$ADB" push build/runtime/System.Runtime.Serialization.Formatters.dll "$D/runtime/" | tail -1 | cut -c1-100
fi

if [ "$what" = game ] || [ "$what" = build ] || [ "$what" = all ]; then
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
  # The phone side of the game (mobile/build.sh; build it again after a new staging, it compiles against build/game).
  [ -f build/PortMobile.dll ] && cp build/PortMobile.dll build/game/
  # RimSort's community load-order rules for the mod page's auto-sort (mobile/ModSort.cs; node host/modrules/build.js refreshes the file).
  cp host/modrules/ModRules.txt build/game/
  # Mono.Cecil: the hook prepares mod assemblies on the phone with it (hooks/ModPrepHost.cs).
  cp /d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0/Mono.Cecil.dll build/game/
  # Harmony's 1.7 build of 0Harmony.dll lives in the game folder (the loader hook skips the mods' own copies); patch.dll
  # applies the port's rewrites to it and to the game's assemblies (hook calls, MonoMod on bionic, font paths).
  HARMONY="/d/SteamLibrary/steamapps/workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll"
  [ -f "$HARMONY" ] && cp "$HARMONY" build/game/
  dotnet build/patch/patch.dll build/game "$RT"
  # The 1.6 Chinese language packs, for a build that ships English only (most 1.6 strings still apply).
  for p in Core Royalty Ideology Biotech Anomaly Odyssey; do
    t="/d/Harness/rimworld/Builds/AndroidData/Data/$p/Languages/ChineseSimplified (简体中文).tar"
    # Only where the game has none of its own (the official 1.7 packs come with the game since 2026-10-13).
    o="build/game/Data/$p/Languages/ChineseSimplified (简体中文).tar"
    [ -f "$t" ] && [ ! -f "$o" ] && mkdir -p "build/game/Data/$p/Languages" && cp "$t" "build/game/Data/$p/Languages/"
  done
  # Type-forwarding facades for the Unity assembly names mods were compiled against (all types -> UnityStubs).
  [ -d build/forwarders ] && cp build/forwarders/*.dll build/game/
  printf -- "-Headless\n-NoCrashDialog\n-Verbose\n" > build/game/args.txt
  echo "game staging: $(ls build/game/*.dll | wc -l) assemblies, $(du -sh build/game | cut -f1)"
fi
if [ "$what" = build ]; then
  exit 0
fi
if [ "$what" = game ] || [ "$what" = all ]; then
  if [ -n "$("$ADB" shell pidof $PKG | tr -d '
')" ]; then
    echo "the game is running: stop it first (the asset pack and the assemblies are in use)"; exit 1
  fi
  "$ADB" shell "mkdir -p $D/game"
  for f in build/game/*; do
    b=$(basename "$f")
    [ "$b" = args.txt ] && continue
    "$ADB" push "$f" "$D/game/" | tail -1 | cut -c1-100
  done
  # What the phone has at the top of the game folder that this build does not (old engine files, or the user's).
  "$ADB" shell "ls $D/game" > build/phone-game-names.txt
  ls build/game > build/build-game-names.txt
  echo "on the phone only (left as they are): $(node host/names-only-in.js build/phone-game-names.txt build/build-game-names.txt)"
fi
# Files pushed over adb belong to the shell user; the app must own them to read them.
UID_=$("$ADB" shell "dumpsys package $PKG | grep -m1 -o 'appId=[0-9]*' | cut -d= -f2" | tr -d '\r')
"$ADB" shell "su -c 'mkdir -p /data/media/0/Android/data/$PKG/files/home; chown -R $UID_:1078 /data/media/0/Android/data/$PKG/files; ls /data/media/0/Android/data/$PKG/files'"

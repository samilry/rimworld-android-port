#!/bin/bash
# Whether the port as built now (build/game's PortMobile and PortHooks, compiled against the current game) also runs on
# the game copies kept from earlier updates (build/game-<version>, e.g. for testers who have not updated their PC):
# every type and member the two use directly (host/compat/RefCheck.cs) and every member they look up by name for Harmony
# or reflection (host/compat/NameCheck.cs) must be in each copy. PortHooks always comes with the APK, so the current one
# is taken before a copy's own. A by-name lookup that is meant to find nothing on older games (FrameRate's
# WaitForBacklogAtMost, 4985) is made on a run-time type and not listed.
# usage: host/compat-check.sh build/game-4982 build/game-4984 ...   (exit status 1 when a copy lacks something)
set -e
cd "$(dirname "$0")/.."
NET8=$(ls -d "/c/Program Files/dotnet/shared/Microsoft.NETCore.App/8.0."* | tail -1)
CECIL=/d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
OUT=build/compat
mkdir -p "$OUT/port"
for tool in RefCheck NameCheck; do
  {
    echo "-nologo -target:exe -optimize+ -langversion:latest -nostdlib+ -deterministic+ -out:\"$(cygpath -w "$OUT/$tool.dll")\""
    for f in System.Runtime System.Collections System.Console System.Linq System.IO.FileSystem System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$NET8/$f.dll")\""; done
    echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.dll")\""
    echo "\"$(cygpath -w host/compat/$tool.cs)\""
  } > "$OUT/$tool.rsp"
  dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll -noconfig @"$OUT/$tool.rsp"
  echo '{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }' > "$OUT/$tool.runtimeconfig.json"
done
cp "$CECIL/Mono.Cecil.dll" "$OUT/"
cp build/game/PortHooks.dll "$OUT/port/"
FIRST="$(cygpath -w "$OUT/port");$(cygpath -w "$RT")"
COPIES=()
for d in "$@"; do COPIES+=("$(cygpath -w "$d")"); done
STATUS=0
for a in build/game/PortMobile.dll build/game/PortHooks.dll; do
  dotnet "$OUT/RefCheck.dll" "$(cygpath -w "$a")" "$FIRST" "${COPIES[@]}" || STATUS=1
  dotnet "$OUT/NameCheck.dll" "$(cygpath -w "$a")" "$FIRST" "${COPIES[@]}" "$(cygpath -w build/game)" || STATUS=1
done
exit $STATUS

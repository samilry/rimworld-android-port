#!/bin/bash
# Builds the phone's System.Private.CoreLib.dll: the runtime pack's, with string.GetHashCode() as in Ludeon's runtime fork
# (WaylithClr.MonoStringHashCode, see PatchCoreLib in patch/Patch.cs), compiled again to ReadyToRun code with the crossgen2
# of the same runtime build (downloads/crossgen2: Microsoft.NETCore.App.Crossgen2.win-x64 10.0.12 from nuget.org, the
# same commit as the runtime pack). Output: build/runtime/System.Private.CoreLib.dll; host/stage.sh runtime pushes it
# over the pack's copy.   usage: host/corelib.sh
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
CG=downloads/crossgen2/pkg/tools/crossgen2.exe
[ -x "$CG" ] || { echo "crossgen2 missing: $CG"; exit 1; }
v1=$(sed -n 1p dotnet-android-coreclr/Microsoft.NETCore.App.versions.txt)
v2=$(sed -n 1p downloads/crossgen2/pkg/Microsoft.NETCore.App.Crossgen2.versions.txt)
[ "$v1" = "$v2" ] || { echo "crossgen2 ($v2) is not from the runtime pack's build ($v1)"; exit 1; }
mkdir -p build/runtime
rm -f build/runtime/System.Private.CoreLib.il.dll build/runtime/System.Private.CoreLib.dll
dotnet build/patch/patch.dll --corelib "$RT/System.Private.CoreLib.dll" build/runtime/System.Private.CoreLib.il.dll
"$CG" build/runtime/System.Private.CoreLib.il.dll -o build/runtime/System.Private.CoreLib.dll --targetos linux --targetarch arm64 -O
ls -la build/runtime/System.Private.CoreLib.dll | awk '{print "System.Private.CoreLib.dll", $5, "bytes"}'

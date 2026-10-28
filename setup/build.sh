#!/bin/bash
# Compiles PortSetup.dll (setup/Setup.cs with the PC patcher's rewrites, patch/Patch.cs and patch/ModPrep.cs): the tester's
# APK runs it on the phone to make the player's own copy of the game ready (SetupService). usage: setup/build.sh
# Output: build/setup/ (PortSetup.dll, Mono.Cecil.dll), which goes to setup/ in the APK's payload.
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
CECIL=/d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0
mkdir -p build/setup
{
  echo "-nologo -target:exe -main:PortSetup.Setup -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/setup/PortSetup.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Linq System.Threading System.Security.Cryptography System.Reflection.Primitives System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.dll")\""
  # Patch.cs uses Rocks (the CoreLib switches, 2026-10-27): without it the build failed and the APKs kept the PortSetup of
  # 10-22, without the rewrites made since (ModLister.RebuildModList through ReadableModFolders).
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.Rocks.dll")\""
  echo "setup/Setup.cs patch/Patch.cs patch/ModPrep.cs"
} > build/setup.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/setup.rsp
cp "$CECIL/Mono.Cecil.dll" "$CECIL/Mono.Cecil.Rocks.dll" build/setup/
ls -la build/setup/PortSetup.dll | awk '{print "PortSetup.dll", $5, "bytes"}'

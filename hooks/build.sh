#!/bin/bash
# Compiles the startup hook against the Android runtime pack's assemblies; the mod preparation (patch/ModPrep.cs, shared
# with the PC's patch.dll) is part of it and needs Mono.Cecil.dll next to the game. usage: hooks/build.sh   (output: build/PortHooks.dll)
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
mkdir -p build
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/PortHooks.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Linq System.Threading System.Threading.Thread System.Security.Cryptography System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "-r:\"$(cygpath -w /d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0/Mono.Cecil.dll)\""
  echo "hooks/StartupHook.cs hooks/PortDiag.cs hooks/WaylithClr.cs hooks/ModPrepHost.cs patch/ModPrep.cs"
} > build/hooks.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/hooks.rsp
ls -la build/PortHooks.dll | awk '{print "PortHooks.dll", $5, "bytes"}'

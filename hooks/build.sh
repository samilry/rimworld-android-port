#!/bin/bash
# Compiles the startup hook against the Android runtime pack's assemblies. usage: hooks/build.sh   (output: build/PortHooks.dll)
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
mkdir -p build
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/PortHooks.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Private.CoreLib; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "hooks/StartupHook.cs"
} > build/hooks.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/hooks.rsp
ls -la build/PortHooks.dll | awk '{print "PortHooks.dll", $5, "bytes"}'

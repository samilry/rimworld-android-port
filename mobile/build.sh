#!/bin/bash
# Compiles PortMobile.dll (the phone-side game patches) against the staged game assemblies and the Android runtime
# pack. usage: mobile/build.sh   (output: build/PortMobile.dll, also copied to build/game)
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
mkdir -p build
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+ -unsafe+"
  echo "-out:build/PortMobile.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Linq System.Threading System.Threading.Thread System.Private.CoreLib System.Runtime.InteropServices System.Numerics.Vectors; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  for f in Assembly-CSharp UnityStubs 0Harmony Waylith Waylith.UnityCompat; do echo "-r:\"$(cygpath -w "build/game/$f.dll")\""; done
  ls mobile/*.cs | tr '\n' ' '
} > build/mobile.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/mobile.rsp
cp build/PortMobile.dll build/game/
ls -la build/PortMobile.dll | awk '{print "PortMobile.dll", $5, "bytes"}'

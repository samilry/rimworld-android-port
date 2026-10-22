#!/bin/bash
# Compiles PortMobile.dll (the phone-side game patches) against the staged game assemblies and the Android runtime
# pack. usage: mobile/build.sh   (output: build/PortMobile.dll, also copied to build/game)
set -e
cd "$(dirname "$0")/.."
RT=dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0
mkdir -p build
# The game version this build is made for (the staged game's), for the reminder at the main menu (mobile/VersionNotice.cs).
# BUILT_FOR=1.7.xxxx mobile/build.sh pretends another, to see the reminder.
echo "namespace PortMobile { internal static class BuiltFor { public const string Version = \"${BUILT_FOR:-$(head -1 build/game/Version.txt | cut -d' ' -f1)}\"; } }" > build/BuiltFor.cs
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+ -unsafe+"
  echo "-out:build/PortMobile.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Linq System.Threading System.Threading.Thread System.Private.CoreLib System.Runtime.InteropServices System.Numerics.Vectors System.Private.Xml System.Text.RegularExpressions System.Reflection.Primitives System.Reflection.Emit.ILGeneration System.Net.Http System.Private.Uri System.Net.Sockets System.Net.Primitives System.Text.Json System.Collections.Concurrent System.Memory System.Text.Encodings.Web System.Threading.Tasks.Parallel System.Collections.NonGeneric; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  for f in Assembly-CSharp UnityStubs 0Harmony Waylith Waylith.UnityCompat Brutal.Vulkan Brutal.Vulkan.Abstractions Brutal.Vulkan.Vma Brutal.Core.Numerics System.IO.Hashing; do echo "-r:\"$(cygpath -w "build/game/$f.dll")\""; done
  # The startup hook assembly: the mod import asks it to prepare a mod's assemblies (hooks/ModPrepHost.cs).
  echo "-r:\"$(cygpath -w build/PortHooks.dll)\""
  ls mobile/*.cs | tr '\n' ' '
  echo "build/BuiltFor.cs"
} > build/mobile.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/mobile.rsp
cp build/PortMobile.dll build/game/
ls -la build/PortMobile.dll | awk '{print "PortMobile.dll", $5, "bytes"}'

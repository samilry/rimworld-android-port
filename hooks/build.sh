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
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Collections.Concurrent System.Runtime.InteropServices System.Linq System.Threading System.Threading.Thread System.Security.Cryptography System.Reflection.Primitives System.Reflection.Emit.ILGeneration System.Reflection.Emit.Lightweight System.Threading.Tasks.Parallel System.Memory System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "-r:\"$(cygpath -w /d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0/Mono.Cecil.dll)\""
  # Harmony's own types, for hooks/HarmonyCompat.cs (the Harmony mod's 1.7 build; at run time it is the game folder's copy).
  echo "-r:\"$(cygpath -w /d/SteamLibrary/steamapps/workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll)\""
  # The engine's texture service and Vulkan types, for hooks/Textures.cs and hooks/Astc.cs, and the hash Astc.cs keys its
  # cache with (System.IO.Hashing, which the game brings); the game's own copies at run time.
  for f in Waylith Brutal.Vulkan Brutal.Core.Common System.IO.Hashing; do echo "-r:\"$(cygpath -w "build/game/$f.dll")\""; done
  echo "hooks/StartupHook.cs hooks/PortDiag.cs hooks/WaylithClr.cs hooks/ModPrepHost.cs hooks/HarmonyCompat.cs hooks/Textures.cs hooks/Astc.cs hooks/JitProfile.cs hooks/PortLog.cs patch/ModPrep.cs"
} > build/hooks.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/hooks.rsp
# Into the game folder too, as mobile/build.sh does: the APKs take it from there (the tester APKs of 2026-10-20
# 14:45 to 16:45 carried the 12:13 build, without mod rules v11, because only build/PortHooks.dll was made).
cp build/PortHooks.dll build/game/
ls -la build/PortHooks.dll | awk '{print "PortHooks.dll", $5, "bytes"}'

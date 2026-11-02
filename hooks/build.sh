#!/bin/bash
# Compiles the startup hook against the Android runtime pack's assemblies; the mod preparation (patch/ModPrep.cs, shared
# with the PC's patch.dll) is part of it and needs Mono.Cecil.dll next to the game. usage: hooks/build.sh   (output: build/PortHooks.dll)
set -e
cd "$(dirname "$0")/.."
. host/env.sh
need "$CSC" "the C# compiler, Microsoft.Net.Compilers.Toolset 4.11.0" "host/fetch-deps.sh fetches it, or set ROSLYN_DIR"
need "$RT/System.Runtime.dll" "the .NET runtime pack for android-arm64, 10.0.12" "host/fetch-deps.sh fetches it, or set DOTNET_RUNTIME_PACK"
need "$HARMONY_DLL" "Harmony's 1.7 0Harmony.dll" "subscribe to the Harmony mod in the Workshop, or set HARMONY_DLL"
mkdir -p build
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/PortHooks.dll"
  for f in System.Runtime System.Runtime.Loader System.Console System.Collections System.Collections.Concurrent System.Runtime.InteropServices System.Linq System.Threading System.Threading.Thread System.Security.Cryptography System.Reflection.Primitives System.Reflection.Emit.ILGeneration System.Reflection.Emit.Lightweight System.Reflection.Emit System.Threading.Tasks.Parallel System.Memory System.Net.Primitives System.Net.Http System.Private.Uri System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.dll")\""
  # Harmony's own types, for hooks/HarmonyCompat.cs (the Harmony mod's 1.7 build; at run time it is the game folder's copy).
  echo "-r:\"$(cygpath -w "$HARMONY_DLL")\""
  # The engine's texture service and Vulkan types, for hooks/Textures.cs and hooks/Astc.cs, and the hash Astc.cs keys its
  # cache with (System.IO.Hashing, which the game brings); the game's own copies at run time.
  for f in Waylith Brutal.Vulkan Brutal.Core.Common System.IO.Hashing; do echo "-r:\"$(cygpath -w "build/game/$f.dll")\""; done
  echo "hooks/StartupHook.cs hooks/PortDiag.cs hooks/WaylithClr.cs hooks/ModPrepHost.cs hooks/HarmonyCompat.cs hooks/Textures.cs hooks/Astc.cs hooks/JitProfile.cs hooks/InlineWatch.cs hooks/UiDescriptors.cs hooks/PortLog.cs hooks/ModFolders.cs hooks/MonoModArch.cs hooks/EngineTypes.cs hooks/AndroidFonts.cs hooks/Watch.cs hooks/WindowsLibraries.cs hooks/MissingAssemblies.cs hooks/LoadRecovery.cs hooks/SelfContained.cs hooks/ShaderSessions.cs hooks/HardwareInfo.cs hooks/SystemProxy.cs patch/ModPrep.cs"
} > build/hooks.rsp
dotnet "$CSC" @build/hooks.rsp
# Into the game folder too, as mobile/build.sh does: the APKs take it from there (the tester APKs of 2026-10-20
# 14:45 to 16:45 carried the 12:13 build, without mod rules v11, because only build/PortHooks.dll was made).
cp build/PortHooks.dll build/game/
ls -la build/PortHooks.dll | awk '{print "PortHooks.dll", $5, "bytes"}'

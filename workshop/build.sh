#!/bin/bash
# Compiles PortWorkshop.dll (the Steam Workshop window, loaded by PortMobile's WorkshopLoader into a load context of
# its own) against the staged game assemblies, PortMobile.dll and the libraries in build/workshop (SteamKit2 and what
# it needs, QRCoder; taken from their NuGet packages in downloads/nuget). usage: workshop/build.sh
# Output: build/workshop/PortWorkshop.dll; the whole of build/workshop goes to game/workshop on the phone.
set -e
cd "$(dirname "$0")/.."
. host/env.sh
need "$CSC" "the C# compiler, Microsoft.Net.Compilers.Toolset 4.11.0" "host/fetch-deps.sh fetches it, or set ROSLYN_DIR"
{
  echo "-nologo -target:library -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/workshop/PortWorkshop.dll"
  for f in System.Runtime System.Console System.Collections System.Linq System.Threading System.Private.CoreLib System.Net.Http System.Net.Primitives System.IO.Compression System.IO.Compression.ZipFile System.Security.Cryptography System.Text.RegularExpressions System.Runtime.InteropServices System.Memory System.Private.Uri; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  for f in Assembly-CSharp UnityStubs; do echo "-r:\"$(cygpath -w "build/game/$f.dll")\""; done
  echo "-r:\"$(cygpath -w build/PortMobile.dll)\""
  for f in SteamKit2 protobuf-net protobuf-net.Core QRCoder; do echo "-r:\"$(cygpath -w "build/workshop/$f.dll")\""; done
  ls workshop/*.cs | tr '\n' ' '
} > build/workshop.rsp
dotnet "$CSC" @build/workshop.rsp
ls -la build/workshop/PortWorkshop.dll | awk '{print "PortWorkshop.dll", $5, "bytes"}'

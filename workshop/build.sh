#!/bin/bash
# Compiles PortWorkshop.dll (the Steam Workshop window, loaded by PortMobile's WorkshopLoader into a load context of
# its own) against the staged game assemblies, PortMobile.dll and the libraries in build/workshop (SteamKit2 and what
# it needs, QRCoder; unpacked from their NuGet packages in DEPS_DIR/nuget). usage: workshop/build.sh
# Output: build/workshop/PortWorkshop.dll; the whole of build/workshop goes to game/workshop on the phone.
set -e
cd "$(dirname "$0")/.."
. host/env.sh
need "$CSC" "the C# compiler, Microsoft.Net.Compilers.Toolset 4.11.0" "host/fetch-deps.sh fetches it, or set ROSLYN_DIR"
need build/PortMobile.dll "PortMobile" "run mobile/build.sh first"
# The libraries, unpacked from their packages once (host/fetch-deps.sh puts the packages in DEPS_DIR/nuget): the
# framework builds the downloader has shipped with since 2026-10-14. SteamKit2 3.4.0 asks for protobuf-net 3.2.56,
# ZstdSharp.Port 0.8.7 and System.IO.Hashing.
mkdir -p build/workshop
while read -r dll package framework; do
  [ -f "build/workshop/$dll" ] && continue
  need "$DEPS_DIR/nuget/$package.nupkg" "a library of the Workshop downloader" "host/fetch-deps.sh fetches it"
  unzip -p "$DEPS_DIR/nuget/$package.nupkg" "lib/$framework/$dll" > "build/workshop/$dll"
done <<'EOF'
SteamKit2.dll steamkit2.3.4.0 net10.0
protobuf-net.dll protobuf-net.3.2.56 net8.0
protobuf-net.Core.dll protobuf-net.core.3.2.56 net8.0
ZstdSharp.dll zstdsharp.port.0.8.7 net8.0
System.IO.Hashing.dll system.io.hashing.10.0.12 net10.0
QRCoder.dll qrcoder.1.8.1 net6.0
EOF
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

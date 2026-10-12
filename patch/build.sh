#!/bin/bash
# Compiles the assembly patcher (runs on the PC with the installed .NET 8). usage: patch/build.sh  (output: build/patch/patch.dll)
set -e
cd "$(dirname "$0")/.."
RT=$(ls -d "/c/Program Files/dotnet/shared/Microsoft.NETCore.App/8.0."* | tail -1)
CECIL=/d/Harness/rimworld/tools/patcher/cecilpkg/lib/netstandard2.0
mkdir -p build/patch
{
  echo "-nologo -target:exe -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/patch/patch.dll"
  for f in System.Runtime System.Collections System.Console System.Linq System.IO.FileSystem System.Reflection System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$RT/$f.dll")\""; done
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.dll")\""
  echo "patch/Patch.cs"
} > build/patch/patch.rsp
dotnet /d/Harness/rimworld/tools/roslyn/pkg/tasks/netcore/bincore/csc.dll @build/patch/patch.rsp
cp "$CECIL/Mono.Cecil.dll" build/patch/
cat > build/patch/patch.runtimeconfig.json <<'EOF'
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }
EOF
ls -la build/patch/patch.dll | awk '{print "patch.dll", $5, "bytes"}'

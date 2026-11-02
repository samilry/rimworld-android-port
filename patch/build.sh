#!/bin/bash
# Compiles the assembly patcher (runs on the PC with the installed .NET 8). usage: patch/build.sh  (output: build/patch/patch.dll)
set -e
cd "$(dirname "$0")/.."
. host/env.sh
need "$CSC" "the C# compiler, Microsoft.Net.Compilers.Toolset 4.11.0" "host/fetch-deps.sh fetches it, or set ROSLYN_DIR"
need "$CECIL/Mono.Cecil.dll" "Mono.Cecil 0.11.6" "host/fetch-deps.sh fetches it, or set CECIL_DIR"
need "$NET8/System.Runtime.dll" "the .NET 8 runtime" "install it (dotnet on the PATH), or set NET8"
mkdir -p build/patch
{
  echo "-nologo -target:exe -optimize+ -nullable:disable -langversion:latest -nostdlib+ -noconfig -deterministic+"
  echo "-out:build/patch/patch.dll"
  for f in System.Runtime System.Collections System.Console System.Linq System.IO.FileSystem System.Reflection System.Security.Cryptography System.Private.CoreLib netstandard; do echo "-r:\"$(cygpath -w "$NET8/$f.dll")\""; done
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.dll")\""
  echo "-r:\"$(cygpath -w "$CECIL/Mono.Cecil.Rocks.dll")\""
  echo "patch/Patch.cs patch/ModPrep.cs"
} > build/patch/patch.rsp
dotnet "$CSC" @build/patch/patch.rsp
cp "$CECIL/Mono.Cecil.dll" "$CECIL/Mono.Cecil.Rocks.dll" build/patch/
cat > build/patch/patch.runtimeconfig.json <<'EOF'
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "8.0.0" } } }
EOF
ls -la build/patch/patch.dll | awk '{print "patch.dll", $5, "bytes"}'

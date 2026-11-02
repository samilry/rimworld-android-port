# Where the build finds what this repository does not hold. Sourced by the scripts (". host/env.sh" after they have
# changed to the repository's root; the adb helpers source it from anywhere). Every place can be given in the
# environment. Unset, it is what host/fetch-deps.sh fetched into DEPS_DIR, if that is there, and otherwise where the
# development machine has it: the Android NDK, SDK and JDK of Unity 2022.3.62f3's Android support, the compiler packages
# in ../tools, the Steam library on D:. BUILDING.md describes the build from nothing.
#
# The versions are fixed (host/fetch-deps.sh has the sources and checksums); another NDK, JDK or compiler builds other
# files or fails:
#   ANDROID_NDK          Android NDK r23b (23.1.7779620), Unity 2022.3's, for everything but Slang
#   ANDROID_SDK          an SDK folder with build-tools/34.0.0, platforms/android-34 (r2) and, for adb, platform-tools
#   JDK                  Eclipse Temurin 11.0.14.1+1, Unity 2022.3's (javac, jar, keytool, and java for d8 and apksigner)
#   SLANG_NDK            Android NDK r27c (27.2.12479018): Slang 2026.19 needs C++20 (native/slang/build-official.cmd)
#   DOTNET_RUNTIME_PACK  Microsoft.NETCore.App.Runtime.android-arm64 10.0.12, unpacked (the phone's runtime)
#   CROSSGEN2_DIR        Microsoft.NETCore.App.Crossgen2.win-x64 10.0.12, unpacked (host/corelib.sh)
#   ROSLYN_DIR           Microsoft.Net.Compilers.Toolset 4.11.0, unpacked (csc.dll, run with the installed .NET 8)
#   CECIL_DIR            Mono.Cecil 0.11.6, unpacked
#   ILSPY_DIR            ilspycmd 9.1.0.7988, unpacked (native/build.sh: Brutal.ImGui's sizes for the ImGui wrapper)
#   NET8                 the installed .NET 8 runtime (shared/Microsoft.NETCore.App/8.0.x): patch.dll's references
#   RIMWORLD_DIR         the player's own RimWorld 1.7 (Steam's folder of it)
#   WORKSHOP_DIR         Steam's Workshop folder of RimWorld (steamapps/workshop/content/294100)
#   HARMONY_DLL          Harmony's 1.7 build of 0Harmony.dll (the Harmony mod, Workshop item 2009463077)
#   ADB                  adb, for the scripts that talk to a phone
# DEPS_DIR also holds the sources native/build.sh compiles (src/, ktx/), the NuGet packages (nuget/) and Noto Emoji.

# Paths are kept in the mixed form (D:/x/y), which MSYS programs and Windows programs read alike: host/stage.sh turns
# MSYS's path conversion off for adb, and dotnet, clang or curl given /d/x read it as \d\x on the current drive.
# A relative path is relative to the repository's root, where the scripts run.
PORT17_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
DEPS_DIR=$(cygpath -m "${DEPS_DIR:-downloads}")
# The first of the given places that exists, else the first (so that a missing tool's error names the fetched place).
pick() {
  local p
  for p in "$@"; do
    case "$p" in /*|?:*) [ -e "$p" ] && break;; *) [ -e "$PORT17_ROOT/$p" ] && break;; esac
    p=
  done
  cygpath -m "${p:-$1}"
}
# Stops a script when something it needs is not there: need <path> <what it is> <what to do about it>.
need() {
  [ -e "$1" ] || { echo "missing: $1 ($2; $3; see BUILDING.md)" >&2; exit 1; }
}
# Stops a script that would compile with another NDK than r23b: another clang builds other libraries, or fails.
check_ndk() {
  grep -q "Pkg.Revision = 23.1.7779620" "$ANDROID_NDK/source.properties" 2>/dev/null || { echo "not the Android NDK r23b (23.1.7779620) the port is built with: $ANDROID_NDK (host/fetch-deps.sh fetches it, or set ANDROID_NDK; see BUILDING.md)" >&2; exit 1; }
}

# The development machine's places.
UNITY_ANDROID="C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer"
DEV_TOOLS=D:/Harness/rimworld/tools
STEAM_LIBRARY=D:/SteamLibrary/steamapps

ANDROID_NDK=$(pick "${ANDROID_NDK:-$DEPS_DIR/android-ndk-r23b}" "${ANDROID_NDK:-$UNITY_ANDROID/NDK}")
ANDROID_SDK=$(pick "${ANDROID_SDK:-$DEPS_DIR/android-sdk}" "${ANDROID_SDK:-$UNITY_ANDROID/SDK}")
JDK=$(pick "${JDK:-$DEPS_DIR/jdk-11.0.14.1+1}" "${JDK:-$UNITY_ANDROID/OpenJDK}")
SLANG_NDK=$(cygpath -m "${SLANG_NDK:-$DEPS_DIR/android-ndk-r27c}")
DOTNET_RUNTIME_PACK=$(pick "${DOTNET_RUNTIME_PACK:-$DEPS_DIR/dotnet-android-coreclr}" "${DOTNET_RUNTIME_PACK:-dotnet-android-coreclr}")
CROSSGEN2_DIR=$(cygpath -m "${CROSSGEN2_DIR:-$DEPS_DIR/crossgen2/pkg}")
ROSLYN_DIR=$(pick "${ROSLYN_DIR:-$DEPS_DIR/roslyn/pkg}" "${ROSLYN_DIR:-$DEV_TOOLS/roslyn/pkg}")
CECIL_DIR=$(pick "${CECIL_DIR:-$DEPS_DIR/cecil/pkg}" "${CECIL_DIR:-$DEV_TOOLS/patcher/cecilpkg}")
ILSPY_DIR=$(pick "${ILSPY_DIR:-$DEPS_DIR/ilspy/pkg}" "${ILSPY_DIR:-$DEV_TOOLS/ilspy/pkg}")
if [ -z "$NET8" ]; then
  # The newest 8.0 runtime next to the dotnet on the PATH (any 8.0.x: the references name 8.0.0.0 alike).
  DOTNET_EXE=$(command -v dotnet || echo "/c/Program Files/dotnet/dotnet")
  NET8=$(ls -d "$(dirname "$DOTNET_EXE")/shared/Microsoft.NETCore.App/8.0."* 2>/dev/null | tail -1)
fi
[ -n "$NET8" ] && NET8=$(cygpath -m "$NET8")
RIMWORLD_DIR=$(cygpath -m "${RIMWORLD_DIR:-$STEAM_LIBRARY/common/RimWorld}")
WORKSHOP_DIR=$(cygpath -m "${WORKSHOP_DIR:-$STEAM_LIBRARY/workshop/content/294100}")
HARMONY_DLL=$(cygpath -m "${HARMONY_DLL:-$WORKSHOP_DIR/2009463077/1.7/Assemblies/0Harmony.dll}")
# Absolute: the adb helpers run from other folders too.
if [ -z "$ADB" ]; then
  case "$ANDROID_SDK" in /*|?:*) ADB=$ANDROID_SDK/platform-tools/adb.exe;; *) ADB=$PORT17_ROOT/$ANDROID_SDK/platform-tools/adb.exe;; esac
fi
ADB=$(cygpath -am "$ADB")
# The 1.6 Chinese language packs of an older build, used only where the game has none of its own (host/stage.sh).
LANG16_DIR=$(cygpath -m "${LANG16_DIR:-D:/Harness/rimworld/Builds/AndroidData/Data}")

# What the scripts use of them.
RT=$DOTNET_RUNTIME_PACK/runtimes/android-arm64/lib/net10.0
CSC=$ROSLYN_DIR/tasks/netcore/bincore/csc.dll
CECIL=$CECIL_DIR/lib/netstandard2.0

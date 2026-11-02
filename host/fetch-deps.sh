#!/bin/bash
# Fetches what the build needs and this repository does not hold, at the versions the port is built with, from their
# publishers (Google's Android repository, Eclipse Adoptium, nuget.org, the projects' GitHub tags), checks each download
# against the SHA-256 below and unpacks it into DEPS_DIR (default downloads/ in the repository) where host/env.sh looks.
# Slang is a git clone of its tag with the submodules (the commit is checked); native/build.sh builds it with NDK r27c and
# Visual Studio 2022 (native/slang/build-official.cmd). What is unpacked already is left alone (a mark in
# DEPS_DIR/.fetched says it is complete), so an interrupted run can simply be started again.
# usage: host/fetch-deps.sh             everything: about 2.5 GB of downloads, 9 GB in DEPS_DIR with the archives
#        host/fetch-deps.sh NAME...     only these: ndk slang-ndk sdk jdk dotnet roslyn cecil ilspy nuget sources ktx
#                                       slang emoji
# The player brings the game (RIMWORLD_DIR) and Harmony (HARMONY_DLL); Git for Windows, Node.js, the .NET 8 runtime,
# Visual Studio 2022 and Python 3 are installed (BUILDING.md). The archives stay in DEPS_DIR/archives and may be deleted
# afterwards.
set -e
cd "$(dirname "$0")/.."
. host/env.sh
A=$DEPS_DIR/archives
M=$DEPS_DIR/.fetched
mkdir -p "$A" "$M"
GOOGLE=https://dl.google.com/android/repository
NUGET=https://api.nuget.org/v3-flatcontainer
GITHUB=https://github.com
RAW=https://raw.githubusercontent.com

# fetch <url> <sha256> <file>: downloads unless the file is there with that checksum, and checks the download. A transfer
# that stalls (under 1 KB/s for a minute) counts as failed and is tried again, as an error is.
fetch() {
  if [ -f "$3" ] && [ "$(sha256sum < "$3" | cut -d' ' -f1)" = "$2" ]; then return; fi
  echo "fetching $1"
  mkdir -p "$(dirname "$3")"
  curl -fsSL --retry 3 --retry-delay 2 --speed-limit 1024 --speed-time 60 -o "$3.part" "$1"
  local got
  got=$(sha256sum < "$3.part" | cut -d' ' -f1)
  if [ "$got" != "$2" ]; then
    rm -f "$3.part"
    echo "$1: SHA-256 $got, expected $2 (not the file the port is built with)" >&2
    exit 1
  fi
  mv "$3.part" "$3"
}
# done_before <name>: whether <name> was unpacked completely before; begin <name> <folder...> clears what an
# interrupted run left; finish <name> marks it complete.
done_before() { [ -f "$M/$1" ]; }
begin() { local n=$1; shift; rm -f "$M/$n"; rm -rf "$@"; }
finish() { touch "$M/$1"; echo "  $1 ready"; }
# unzip_to <archive> <folder>: the archive's contents into the folder (created).
unzip_to() { mkdir -p "$2"; unzip -q -o "$1" -d "$2"; }
# untar <archive> <folder> [members...]: a .tar.gz into the folder. --force-local: tar takes "D:/x" for host D's file x.
untar() { local f=$1 d=$2; shift 2; mkdir -p "$d"; tar --force-local -xzf "$f" -C "$d" "$@"; }

# want <name>: whether to fetch it (all when no names were given).
WANT="$*"
want() { [ -z "$WANT" ] || [[ " $WANT " == *" $1 "* ]]; }

# Android NDK r23b (23.1.7779620): Unity 2022.3's, the same files; compiles everything but Slang.
if want ndk; then
  if ! done_before ndk; then
    fetch $GOOGLE/android-ndk-r23b-windows.zip 36270f8b2cfdd940f410bd8ffe6ce86ffaa8e87ff1c4fd4a8be5130268357778 "$A/android-ndk-r23b-windows.zip"
    begin ndk "$DEPS_DIR/android-ndk-r23b"
    unzip_to "$A/android-ndk-r23b-windows.zip" "$DEPS_DIR"
    finish ndk
  fi
fi

# Android NDK r27c (27.2.12479018): Slang 2026.19 needs C++20, which r23b's clang 12 lacks.
if want slang-ndk; then
  if ! done_before slang-ndk; then
    fetch $GOOGLE/android-ndk-r27c-windows.zip 27e49f11e0cee5800983d8af8f4acd5bf09987aa6f790d4439dda9f3643d2494 "$A/android-ndk-r27c-windows.zip"
    begin slang-ndk "$DEPS_DIR/android-ndk-r27c"
    unzip_to "$A/android-ndk-r27c-windows.zip" "$DEPS_DIR"
    finish slang-ndk
  fi
fi

# The SDK parts host/build.sh uses: build-tools 34.0.0 (aapt2, d8, zipalign, apksigner), platform android-34 revision 2
# (extension 7, the android.jar it compiles and links against) and platform-tools 37.0.1 (adb, only to talk to a phone).
if want sdk; then
  if ! done_before sdk; then
    fetch $GOOGLE/build-tools_r34-windows.zip 9be665ad74ef22bf0e489b37db465074484df79b95f26d759945d5c25a47b326 "$A/build-tools_r34-windows.zip"
    fetch $GOOGLE/platform-34-ext7_r02.zip 5323311cc3e4ad614f0b8053c72b651726f3422448cedd39e48f00737cda8ad0 "$A/platform-34-ext7_r02.zip"
    fetch $GOOGLE/platform-tools_r37.0.1-win.zip 45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99 "$A/platform-tools_r37.0.1-win.zip"
    S=$DEPS_DIR/android-sdk
    begin sdk "$S"
    # The archives hold android-14/ (build-tools), android-34/ and platform-tools/.
    unzip_to "$A/build-tools_r34-windows.zip" "$S/build-tools"
    mv "$S/build-tools/android-14" "$S/build-tools/34.0.0"
    unzip_to "$A/platform-34-ext7_r02.zip" "$S/platforms"
    unzip_to "$A/platform-tools_r37.0.1-win.zip" "$S"
    finish sdk
  fi
fi

# Eclipse Temurin 11.0.14.1+1, the JDK Unity 2022.3 ships (javac's class files feed d8: the same JDK, the same dex).
if want jdk; then
  if ! done_before jdk; then
    fetch "$GITHUB/adoptium/temurin11-binaries/releases/download/jdk-11.0.14.1%2B1/OpenJDK11U-jdk_x64_windows_hotspot_11.0.14.1_1.zip" 3e7da701aa92e441418299714f0ed6db10c3bb1e2db625c35a2c2cd9cc619731 "$A/OpenJDK11U-jdk_x64_windows_hotspot_11.0.14.1_1.zip"
    begin jdk "$DEPS_DIR/jdk-11.0.14.1+1"
    unzip_to "$A/OpenJDK11U-jdk_x64_windows_hotspot_11.0.14.1_1.zip" "$DEPS_DIR"
    finish jdk
  fi
fi

# .NET 10.0.12 (commit 95017c71 of dotnet/dotnet): the CoreCLR runtime pack for android-arm64 (the phone's runtime) and
# the crossgen2 of the same build (host/corelib.sh compiles the patched CoreLib to ReadyToRun with it).
if want dotnet; then
  if ! done_before dotnet; then
    fetch $NUGET/microsoft.netcore.app.runtime.android-arm64/10.0.12/microsoft.netcore.app.runtime.android-arm64.10.0.12.nupkg 7f67905e5e5324e0c2b03920b62a3b09c036737ba84f9e7cb61f7e058d285ad3 "$A/microsoft.netcore.app.runtime.android-arm64.10.0.12.nupkg"
    fetch $NUGET/microsoft.netcore.app.crossgen2.win-x64/10.0.12/microsoft.netcore.app.crossgen2.win-x64.10.0.12.nupkg de87383c9c59d3c96c19bc1ebd8712aa6eb579df2ba034162e3a783c28668157 "$A/microsoft.netcore.app.crossgen2.win-x64.10.0.12.nupkg"
    begin dotnet "$DEPS_DIR/dotnet-android-coreclr" "$DEPS_DIR/crossgen2/pkg"
    unzip_to "$A/microsoft.netcore.app.runtime.android-arm64.10.0.12.nupkg" "$DEPS_DIR/dotnet-android-coreclr"
    unzip_to "$A/microsoft.netcore.app.crossgen2.win-x64.10.0.12.nupkg" "$DEPS_DIR/crossgen2/pkg"
    finish dotnet
  fi
fi

# The C# compiler (Roslyn 4.11.0, commit 5e3a11e2; csc.dll runs on the installed .NET 8), Mono.Cecil 0.11.6 and ILSpy's
# command line 9.1.0.7988 (native/build.sh decompiles the game's Brutal.ImGui with it, for the sizes it checks).
if want roslyn; then
  if ! done_before roslyn; then
    fetch $NUGET/microsoft.net.compilers.toolset/4.11.0/microsoft.net.compilers.toolset.4.11.0.nupkg db165c75f0d0386d078d01448f96c93f8222320633e4fe828f5d1bf8c99b7d6f "$A/microsoft.net.compilers.toolset.4.11.0.nupkg"
    begin roslyn "$DEPS_DIR/roslyn/pkg"
    unzip_to "$A/microsoft.net.compilers.toolset.4.11.0.nupkg" "$DEPS_DIR/roslyn/pkg"
    finish roslyn
  fi
fi
if want cecil; then
  if ! done_before cecil; then
    fetch $NUGET/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg d2a23832aaa948ba9a01acc42b5726e34c5f995958f1b30d45c0e7c70b3a72d5 "$A/mono.cecil.0.11.6.nupkg"
    begin cecil "$DEPS_DIR/cecil/pkg"
    unzip_to "$A/mono.cecil.0.11.6.nupkg" "$DEPS_DIR/cecil/pkg"
    finish cecil
  fi
fi
if want ilspy; then
  if ! done_before ilspy; then
    fetch $NUGET/ilspycmd/9.1.0.7988/ilspycmd.9.1.0.7988.nupkg 2b5058f5ccc164c33b7aabf1a5eb0cf3d3a6af6c145aaf58efd3ed891443af7c "$A/ilspycmd.9.1.0.7988.nupkg"
    begin ilspy "$DEPS_DIR/ilspy/pkg"
    unzip_to "$A/ilspycmd.9.1.0.7988.nupkg" "$DEPS_DIR/ilspy/pkg"
    finish ilspy
  fi
fi

# The packages kept packed in DEPS_DIR/nuget: the Workshop downloader's libraries (workshop/build.sh unpacks them) and
# BinaryFormatter's implementation as the PC game ships it (host/build.sh).
if want nuget; then
  N=$DEPS_DIR/nuget
  fetch $NUGET/steamkit2/3.4.0/steamkit2.3.4.0.nupkg cd90b5ac507c4eba3ee3439b62b8a5dab17d687dc51dfc81202b69f6c8643153 "$N/steamkit2.3.4.0.nupkg"
  fetch $NUGET/protobuf-net/3.2.56/protobuf-net.3.2.56.nupkg 2a3c111f21b07e54038d56ae753f8d8d19df1a11dbe02a427e7f611d58b123e1 "$N/protobuf-net.3.2.56.nupkg"
  fetch $NUGET/protobuf-net.core/3.2.56/protobuf-net.core.3.2.56.nupkg 355bcbade0afbef9d2fecd2cc8be7f2f7991a577acc0fec4ef50ba58ff5fd837 "$N/protobuf-net.core.3.2.56.nupkg"
  fetch $NUGET/zstdsharp.port/0.8.7/zstdsharp.port.0.8.7.nupkg 5acb834d93afdd382213c355fd35db29130706c64a81607c88374a2abf4ebc0a "$N/zstdsharp.port.0.8.7.nupkg"
  fetch $NUGET/system.io.hashing/10.0.12/system.io.hashing.10.0.12.nupkg d918de044168a48e148a73e84c6db6f8fea570963848dc4c03631aad97958dd1 "$N/system.io.hashing.10.0.12.nupkg"
  fetch $NUGET/qrcoder/1.8.1/qrcoder.1.8.1.nupkg d3fe5c80c742b6a2ef8a31734c06600e2d674806a97f36f4af97b0d2871de309 "$N/qrcoder.1.8.1.nupkg"
  fetch $NUGET/system.runtime.serialization.formatters/10.0.0/system.runtime.serialization.formatters.10.0.0.nupkg 5a084d800e873cb48975e232a6b89d6bfaedd3bcbc3ef469ad0863149bc9d04d "$N/system.runtime.serialization.formatters.10.0.0.nupkg"
  echo "  nuget ready"
fi

# The upstream sources native/build.sh and host/build.sh compile, in DEPS_DIR/src. GitHub's archives of the tags (of a
# commit for gli, whose last release is older than the code the engine's gli.dll has); single files at fixed commits.
if want sources; then
  S=$DEPS_DIR/src
  # src_zip <name> <url> <sha256> <archive's top folder> <folder>
  src_zip() {
    done_before "$1" && return
    fetch "$2" "$3" "$A/$1.zip"
    begin "$1" "$S/$5" "$S/$4"
    unzip_to "$A/$1.zip" "$S"
    [ "$4" = "$5" ] || mv "$S/$4" "$S/$5"
    finish "$1"
  }
  src_zip vulkan-headers $GITHUB/KhronosGroup/Vulkan-Headers/archive/refs/tags/v1.3.296.zip 45a8c99f867a686b85bbb6fa460dad41840ecfa1ca111a29f044387f47308dce Vulkan-Headers-1.3.296 Vulkan-Headers-1.3.296
  src_zip vma $GITHUB/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/archive/refs/tags/v3.3.0.zip 81755d8fcb411b97292c6682e828501315db319374c7c34ba6e1226452c6c392 VulkanMemoryAllocator-3.3.0 VulkanMemoryAllocator-3.3.0
  src_zip gli $GITHUB/g-truc/gli/archive/3542f8830178061e0661f5df1e89d36ba7d7b0ab.zip 482302cff80aeed356a57ecf5b8ef04c0ef2334648e15d48aa7bdc43df88f38d gli-3542f8830178061e0661f5df1e89d36ba7d7b0ab gli-master
  src_zip glm $GITHUB/g-truc/glm/archive/refs/tags/1.0.1.zip 09c5716296787e1f7fcb87b1cbdbf26814ec1288ed6259ccd30d5d9795809fa5 glm-1.0.1 glm-1.0.1
  src_zip imgui $GITHUB/ocornut/imgui/archive/refs/tags/v1.92.2-docking.zip dbd869305d7cd40cc7fbc285f201ec921950a5e296bf4e8abf54d5b28b638254 imgui-1.92.2-docking imgui-1.92.2-docking
  if ! done_before freetype; then
    fetch $GITHUB/freetype/freetype/archive/refs/tags/VER-2-13-3.tar.gz bc5c898e4756d373e0d991bab053036c5eb2aa7c0d5c67e8662ddc6da40c4103 "$A/freetype-VER-2-13-3.tar.gz"
    begin freetype "$S/freetype-VER-2-13-3"
    untar "$A/freetype-VER-2-13-3.tar.gz" "$S"
    finish freetype
  fi
  # astc-encoder 5.7.0: the library's sources (astcenc*), not the command line tool, tests and third-party code.
  if ! done_before astc; then
    fetch $GITHUB/ARM-software/astc-encoder/archive/refs/tags/5.7.0.tar.gz 7c1b28ece59c9c2737e297123a9910c1c548676c7aed87a09d5734e2fa7bdaf0 "$A/astc-encoder-5.7.0.tar.gz"
    begin astc "$S/astc-encoder-5.7.0"
    untar "$A/astc-encoder-5.7.0.tar.gz" "$S" --wildcards 'astc-encoder-5.7.0/LICENSE.txt' 'astc-encoder-5.7.0/Source/astcenc.h' 'astc-encoder-5.7.0/Source/astcenc_*'
    finish astc
  fi
  # miniaudio 0.11.22 and the stb_vorbis it comes with; stb_image 2.30 and stb_image_write 1.16 (stb at 013ac3b).
  if ! done_before single-files; then
    fetch $RAW/mackron/miniaudio/0.11.22/miniaudio.h 9019743287e443c55e5737a7297f38e5e358561701d6db2d905afb114390c410 "$S/miniaudio/miniaudio.h"
    fetch $RAW/mackron/miniaudio/0.11.22/extras/stb_vorbis.c 4c7cb2ff1f7011e9d67950446b7eb9ca044f2e464d76bfbb0b84dd2e23e65636 "$S/miniaudio/stb_vorbis.c"
    fetch $RAW/nothings/stb/013ac3beddff3dbffafd5177e7972067cd2b5083/stb_image.h 594c2fe35d49488b4382dbfaec8f98366defca819d916ac95becf3e75f4200b3 "$S/stb_image.h"
    fetch $RAW/nothings/stb/013ac3beddff3dbffafd5177e7972067cd2b5083/stb_image_write.h cbd5f0ad7a9cf4468affb36354a1d2338034f2c12473cf1a8e32053cb6914a05 "$S/stb_image_write.h"
    finish single-files
  fi
fi

# KTX-Software 4.4.0 (the PC's Ktx.dll is the same version): the folders the read-only library is built from; the
# archive's tests and tools are most of its 200 MB.
if want ktx; then
  if ! done_before ktx; then
    fetch $GITHUB/KhronosGroup/KTX-Software/archive/refs/tags/v4.4.0.tar.gz 3585d76edcdcbe3a671479686f8c81c1c10339f419e4b02a9a6f19cc6e4e0612 "$A/KTX-Software-4.4.0.tar.gz"
    begin ktx "$DEPS_DIR/ktx/KTX-Software-4.4.0"
    untar "$A/KTX-Software-4.4.0.tar.gz" "$DEPS_DIR/ktx" KTX-Software-4.4.0/CMakeLists.txt KTX-Software-4.4.0/LICENSE.md KTX-Software-4.4.0/external KTX-Software-4.4.0/include KTX-Software-4.4.0/lib KTX-Software-4.4.0/other_include KTX-Software-4.4.0/utils
    finish ktx
  fi
fi

# Slang 2026.19, the PC game's shader compiler: the tag with its submodules (its version comes from git describe, so a
# clone, not an archive). Checked out with CRLF line ends as Git for Windows does by default: the build embeds Slang's
# own shader library text, and the port's libraries were built from such a checkout.
if want slang; then
  if ! done_before slang; then
    D=$DEPS_DIR/src/slang-2026.19
    begin slang "$D"
    git -c core.autocrlf=true -c advice.detachedHead=false clone -q --depth 1 --branch v2026.19 --recurse-submodules --shallow-submodules https://github.com/shader-slang/slang.git "$D"
    [ "$(git -C "$D" rev-parse HEAD)" = 0fb6b7587a94fa33d88141ec9930766eedc6f59a ] || { echo "slang: v2026.19 is not commit 0fb6b758 any more" >&2; exit 1; }
    finish slang
  fi
fi

# Google's Noto Emoji 3.002 (OFL) at the google/fonts commit host/stage.sh names (it fetches it there itself as well).
if want emoji; then
  fetch "$RAW/google/fonts/b979dba422e445492b0eb9951ac52ee0b4d648c3/ofl/notoemoji/NotoEmoji%5Bwght%5D.ttf" de6c18832938afc99caf132b39d6a30a19bac7f2e812e28db2535b4608d27551 "$DEPS_DIR/notoemoji/NotoEmoji[wght].ttf"
  echo "  emoji ready"
fi

# (Not du: over the NDKs' hundred thousand files it takes minutes.)
echo "dependencies in $DEPS_DIR"

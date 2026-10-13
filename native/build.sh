#!/bin/bash
# Builds the engine's native libraries for arm64 Android from upstream sources (downloads/src, see README):
#   libusage: native/build.sh [all|vma|stb|gli|imgui|freetype|slang|monomodc|waylithaudio]|vma|stb|gli|imgui|freetype|slang|monomodc].so            Vulkan Memory Allocator 3.3.0 (native/vma)
#   libstb.so            stb_image + stb_image_write (native/stb)
#   libgli.so            the gli C API (native/gli, gli + glm)
#   libimgui.so          Dear ImGui 1.92.2 docking + the generated flat C API (native/imgui)
#   libimgui_freetype.so ImGui's FreeType loader with FreeType 2.13.3 inside (native/imgui_freetype)
#   libslang.so          stand-in for the Slang compiler (native/slang)
#   libmonomodc.so       MonoMod's glibc P/Invokes for bionic (native/monomodc), used by the patched 0Harmony.dll
#   libwaylithaudio.so   the engine's audio library on miniaudio 0.11.22 + stb_vorbis (native/waylithaudio)
# usage: native/build.sh [all|vma|stb|gli|imgui|freetype|slang]   (output: build/native/lib/*.so)
set -e
cd "$(dirname "$0")/.."
NDK="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/NDK"
BIN="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
CC="$BIN/clang.exe"
CXX="$BIN/clang++.exe"
AR="$BIN/llvm-ar.exe"
TARGET="--target=aarch64-linux-android26"
SRC=downloads/src
IMGUI=$SRC/imgui-1.92.2-docking
FREETYPE=$SRC/freetype-VER-2-13-3
OUT=build/native
LIB=$OUT/lib
OBJ=$OUT/obj
mkdir -p "$LIB" "$OBJ"
WHAT=${1:-all}
CXXFLAGS="-std=c++17 -O2 -fPIC -fvisibility=default -static-libstdc++ -Wno-unused-parameter"
IMGUI_DEFS="-DIMGUI_DISABLE_OBSOLETE_FUNCTIONS -DIMGUI_USE_WCHAR32 -mms-bitfields"

want() { [ "$WHAT" = all ] || [ "$WHAT" = "$1" ]; }

if want vma; then
  echo "== vma"
  "$CXX" $TARGET $CXXFLAGS -shared -I "$SRC/Vulkan-Headers-1.3.296/include" -I "$SRC/VulkanMemoryAllocator-3.3.0/include" -Wno-nullability-completeness -o "$LIB/libvma.so" native/vma/vma.cpp -ldl
fi

if want stb; then
  echo "== stb"
  "$CC" $TARGET -O2 -fPIC -shared -I "$SRC" -o "$LIB/libstb.so" native/stb/stb.c -lm
fi

if want gli; then
  echo "== gli"
  "$CXX" $TARGET $CXXFLAGS -shared -I "$SRC/gli-master" -I "$SRC/glm-1.0.1" -DGLM_ENABLE_EXPERIMENTAL -DGLM_FORCE_SILENT_WARNINGS -o "$LIB/libgli.so" native/gli/gli_c.cpp
fi

if want monomodc; then
  echo "== monomodc (MonoMod's libc/libdl P/Invokes on bionic)"
  "$CC" $TARGET -O2 -fPIC -shared -fvisibility=hidden -Wall -o "$LIB/libmonomodc.so" native/monomodc/monomodc.c -llog -ldl
fi

if want waylithaudio; then
  echo "== waylithaudio (miniaudio + stb_vorbis)"
  "$CC" $TARGET -O2 -fPIC -shared -fvisibility=hidden -Wall -Wno-unused-function -Wno-unused-variable -I "$SRC/miniaudio" -o "$LIB/libwaylithaudio.so" native/waylithaudio/waylithaudio.c -llog -ldl -lm
fi

if want slang; then
  echo "== slang stub"
  "$CC" $TARGET -O2 -fPIC -shared -o "$LIB/libslang.so" native/slang/slang_stub.c
fi

if want imgui; then
  echo "== imgui"
  node native/imgui/gen-imgui-c.js build/scan/imgui-ast.json ref/tmp_Brutal.ImGui/Brutal.ImGui.decompiled.cs build/scan/imgui-plain.txt "$OUT/imgui_c.cpp"
  "$CXX" $TARGET $CXXFLAGS $IMGUI_DEFS -shared -I "$IMGUI" -o "$LIB/libimgui.so" \
    "$IMGUI/imgui.cpp" "$IMGUI/imgui_draw.cpp" "$IMGUI/imgui_tables.cpp" "$IMGUI/imgui_widgets.cpp" "$IMGUI/imgui_demo.cpp" "$OUT/imgui_c.cpp" -lm -llog
fi

if want freetype; then
  echo "== freetype (static) + imgui_freetype"
  FT_SOURCES="base/ftsystem.c base/ftinit.c base/ftdebug.c base/ftbase.c base/ftbbox.c base/ftglyph.c base/ftbdf.c base/ftbitmap.c base/ftcid.c base/ftfstype.c base/ftgasp.c base/ftgxval.c base/ftmm.c base/ftotval.c base/ftpatent.c base/ftpfr.c base/ftstroke.c base/ftsynth.c base/fttype1.c base/ftwinfnt.c truetype/truetype.c cff/cff.c sfnt/sfnt.c psaux/psaux.c psnames/psnames.c pshinter/pshinter.c autofit/autofit.c smooth/smooth.c raster/raster.c sdf/sdf.c type1/type1.c cid/type1cid.c type42/type42.c pfr/pfr.c winfonts/winfnt.c pcf/pcf.c bdf/bdf.c gzip/ftgzip.c lzw/ftlzw.c svg/svg.c"
  rm -f "$OBJ"/ft_*.o
  for f in $FT_SOURCES; do
    o="$OBJ/ft_$(basename "$f" .c).o"
    "$CC" $TARGET -O2 -fPIC -DFT2_BUILD_LIBRARY -I "$FREETYPE/include" -w -c "$FREETYPE/src/$f" -o "$o"
  done
  rm -f "$OBJ/libfreetype.a"
  "$AR" rcs "$OBJ/libfreetype.a" "$OBJ"/ft_*.o
  "$CXX" $TARGET $CXXFLAGS $IMGUI_DEFS -shared -I "$IMGUI" -I "$FREETYPE/include" -o "$LIB/libimgui_freetype.so" \
    "$IMGUI/misc/freetype/imgui_freetype.cpp" native/imgui_freetype/imgui_freetype_c.cpp "$OBJ/libfreetype.a" -L "$LIB" -limgui -lm
fi

ls -la "$LIB" | awk 'NR>3 {print $5"\t"$9}'

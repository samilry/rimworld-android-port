#!/bin/bash
# Builds the engine's native libraries for arm64 Android from upstream sources (downloads/src, see README):
#   libvma.so            Vulkan Memory Allocator 3.3.0 (native/vma)
#   libstb.so            stb_image + stb_image_write (native/stb)
#   libgli.so            the gli C API (native/gli, gli + glm)
#   libimgui.so          Dear ImGui 1.92.2 docking + the generated flat C API (native/imgui)
#   libimgui_freetype.so ImGui's FreeType loader with FreeType 2.13.3 inside (native/imgui_freetype)
#   libslang.so          the part of the Slang compiler's API the engine uses, on glslang 11.2 from the NDK's
#                        sources (native/slang): compiles the GLSL shaders mods bring as source
#   libmonomodc.so       MonoMod's glibc P/Invokes for bionic (native/monomodc), used by the patched 0Harmony.dll
#   libwaylithaudio.so   the engine's audio library on miniaudio 0.11.22 + stb_vorbis (native/waylithaudio)
#   libmm_exhelper.so    MonoMod's native exception helper rebuilt with a static libunwind (native/exhelper)
#   libportastc.so       ASTC 4x4 encoding with ARM's astcenc 5.7.0 (native/astc), for GPUs without BC textures
# usage: native/build.sh [all|vma|stb|gli|imgui|freetype|slang|monomodc|waylithaudio|exhelper|astc]   (output: build/native/lib/*.so)
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
# NDEBUG: ImGui's IM_ASSERTs left out, as in the PC's imgui.dll and imgui_freetype.dll (none of their texts are in them;
# the PC's vma.dll and waylithaudio.dll keep theirs, and so do ours). With them in, an assert the PC never runs ended
# the game (a tester, 2026-10-27, the mod list of 326 mods: the font atlas's rectangle generation check, see below).
IMGUI_DEFS="-DIMGUI_DISABLE_OBSOLETE_FUNCTIONS -DIMGUI_USE_WCHAR32 -mms-bitfields -DNDEBUG"

want() { [ "$WHAT" = all ] || [ "$WHAT" = "$1" ]; }

# ImGui's source with the port's fixes (native/imgui/*.patch) in a copy; downloads/src stays as downloaded.
#   rect-generation.patch: upstream's fix of 1.92.3 (a309d2d, #8906). A font atlas rectangle slot reused 512 times read
#   its 10-bit signed Generation back negative: from then on the check failed (the assert above) and, without asserts
#   as on the PC, the slot's lookups (ImFontAtlasPackGetRectSafe) found nothing. A long list of mod names in several
#   scripts fills the atlas and re-bakes glyphs often enough to get there in minutes.
#   thread-lock.patch: the functions that load a glyph or change the font atlas take one lock. The engine measures text on
#   any thread and loads unseen glyphs there; two threads rendering into the same FreeType glyph slot crashed a tester's
#   vivo (2026-10-27, a NULL bitmap in ImGui_ImplFreeType_FontBakedLoadGlyph). Applied after rect-generation.patch.
if want imgui || want freetype; then
  rm -rf "$OBJ/imgui-src"
  cp -r "$IMGUI" "$OBJ/imgui-src"
  for p in native/imgui/*.patch; do patch -s -p1 -d "$OBJ/imgui-src" < "$p"; done
  IMGUI=$OBJ/imgui-src
fi

if want vma; then
  echo "== vma"
  "$CXX" $TARGET $CXXFLAGS -shared -I "$SRC/Vulkan-Headers-1.3.296/include" -I "$SRC/VulkanMemoryAllocator-3.3.0/include" -Wno-nullability-completeness -o "$LIB/libvma.so" native/vma/vma.cpp -ldl -llog
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

if want exhelper; then
  echo "== exhelper (MonoMod's exception helper with a static libunwind)"
  "$CC" $TARGET -O2 -fPIC -shared -fvisibility=hidden -Wall -o "$LIB/libmm_exhelper.so" native/exhelper/exhelper.c native/exhelper/exhelper_arm64.S -lunwind -Wl,--exclude-libs,libunwind.a
fi

if want waylithaudio; then
  echo "== waylithaudio (miniaudio + stb_vorbis)"
  "$CC" $TARGET -O2 -fPIC -shared -fvisibility=hidden -Wall -Wno-unused-function -Wno-unused-variable -I "$SRC/miniaudio" -o "$LIB/libwaylithaudio.so" native/waylithaudio/waylithaudio.c -llog -ldl -lm
fi

if want slang; then
  echo "== slang (the engine's shader compiler API on the NDK's glslang)"
  GLSLANG="$NDK/sources/third_party/shaderc/third_party/glslang"
  SHADERC_UTIL="$NDK/sources/third_party/shaderc/libshaderc_util"
  GOBJ="$OBJ/glslang"
  mkdir -p "$GOBJ/include/glslang"
  # glslang/build_info.h is generated from a template by a Python script in the NDK's build; it is four numbers.
  sed -e 's/@major@/11/' -e 's/@minor@/2/' -e 's/@patch@/0/' -e 's/@flavor@//' "$GLSLANG/build_info.h.tmpl" > "$GOBJ/include/glslang/build_info.h"
  GFLAGS="-std=c++11 -O2 -fPIC -fno-exceptions -fno-rtti -fvisibility=hidden -w -DAMD_EXTENSIONS -DNV_EXTENSIONS -DENABLE_HLSL -DGLSLANG_OSINCLUDE_UNIX"
  # The source lists of the NDK's Android.mk for glslang (OSDependent, OGLCompiler, glslang with HLSL, SPIRV).
  for dir in glslang/GenericCodeGen glslang/HLSL glslang/MachineIndependent glslang/MachineIndependent/preprocessor glslang/OSDependent/Unix OGLCompilersDLL SPIRV; do
    for f in "$GLSLANG/$dir"/*.cpp; do
      o="$GOBJ/$(echo "$dir" | tr '/' '_')_$(basename "$f" .cpp).o"
      # Compiled once: the sources are the NDK's and do not change.
      [ -f "$o" ] || "$CXX" $TARGET $GFLAGS -I "$GLSLANG" -I "$GLSLANG/glslang/MachineIndependent" -I "$GOBJ/include" -c "$f" -o "$o"
    done
  done
  # The limits glslang wants to be told (shaderc's defaults).
  [ -f "$GOBJ/shaderc_resources.o" ] || "$CXX" $TARGET $GFLAGS -I "$GLSLANG" -I "$SHADERC_UTIL/include" -c "$SHADERC_UTIL/src/resources.cc" -o "$GOBJ/shaderc_resources.o"
  rm -f "$OBJ/libglslang.a"
  "$AR" rcs "$OBJ/libglslang.a" "$GOBJ"/*.o
  "$CXX" $TARGET -std=c++17 -O2 -fPIC -shared -s -fvisibility=hidden -fno-exceptions -fno-rtti -static-libstdc++ -Wl,--exclude-libs,ALL -Wall -Wno-unused-parameter \
    -I "$GLSLANG" -I "$SHADERC_UTIL/include" -I "$GOBJ/include" -o "$LIB/libslang.so" native/slang/slang_glslang.cpp "$OBJ/libglslang.a" -llog
fi

if want imgui; then
  echo "== imgui"
  node native/imgui/gen-imgui-c.js build/scan/imgui-ast.json ref/tmp_Brutal.ImGui/Brutal.ImGui.decompiled.cs build/scan/imgui-plain.txt "$OUT/imgui_c.cpp"
  # imgui_draw.cpp loads font files through PortFontFileLoad (native/imgui/port_fonts.cpp: mapped, not read into the heap).
  "$CXX" $TARGET $CXXFLAGS $IMGUI_DEFS -DImFileLoadToMemory=PortFontFileLoad -I "$IMGUI" -c -o "$OBJ/imgui_draw.o" "$IMGUI/imgui_draw.cpp"
  "$CXX" $TARGET $CXXFLAGS $IMGUI_DEFS -shared -I "$IMGUI" -o "$LIB/libimgui.so" \
    "$IMGUI/imgui.cpp" "$OBJ/imgui_draw.o" "$IMGUI/imgui_tables.cpp" "$IMGUI/imgui_widgets.cpp" "$IMGUI/imgui_demo.cpp" native/imgui/port_fonts.cpp "$OUT/imgui_c.cpp" -lm -llog
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

if want ktx; then
  echo "== ktx (read and transcode)"
  # Khronos' libktx 4.4.0 (Apache-2.0, the PC's Ktx.dll is the same version), the read-only library of its CMake
  # (KTX_MAIN_SRC + KTX 1, no writers, encoders, GL or Vulkan upload): the engine's Brutal.Ktx loads .ktx/.ktx2
  # textures through it (Brutal.TextureApi.Ktx.Loader); without it a mod's KTX texture failed on the phone only.
  # Bound as "Ktx": libKtx.so. The encoding entry points Brutal.Ktx also declares are not there (looked up on first
  # use, and the engine's loader uses none).
  KTX=downloads/ktx/KTX-Software-4.4.0
  KDEFS="-DLIBKTX -DKTX_FEATURE_KTX1 -DKTX_FEATURE_KTX2 -DKTX_OMIT_VULKAN=1 -DSUPPORT_SOFTWARE_ETC_UNPACK=1 -DBASISD_SUPPORT_FXT1=0"
  KINC="-I $KTX/include -I $KTX/lib -I $KTX/utils -I $KTX/other_include -I $KTX/external -I $KTX/external/basisu/transcoder -I $KTX/external/basisu/zstd -I $KTX/external/dfdutils"
  KOBJ="$OBJ/ktx"
  rm -rf "$KOBJ"
  mkdir -p "$KOBJ"
  for f in lib/checkheader.c lib/filestream.c lib/hashlist.c lib/info.c lib/memstream.c lib/strings.c lib/swap.c lib/texture.c lib/texture1.c lib/texture2.c lib/vkformat_check.c lib/vkformat_check_variant.c lib/vkformat_str.c lib/vkformat_typesize.c \
           external/dfdutils/createdfd.c external/dfdutils/colourspaces.c external/dfdutils/interpretdfd.c external/dfdutils/printdfd.c external/dfdutils/queries.c external/dfdutils/vk2dfd.c external/basisu/zstd/zstd.c; do
    "$CC" $TARGET -std=c11 -O2 -fPIC -w $KDEFS $KINC -c "$KTX/$f" -o "$KOBJ/$(echo "$f" | tr '/' '_').o"
  done
  for f in lib/basis_transcode.cpp lib/miniz_wrapper.cpp external/basisu/transcoder/basisu_transcoder.cpp lib/etcunpack.cxx external/etcdec/etcdec.cxx; do
    "$CXX" $TARGET -std=c++17 -O2 -fPIC -w $KDEFS $KINC -c "$KTX/$f" -o "$KOBJ/$(echo "$f" | tr '/' '_').o"
  done
  # The engine binds the entry points of Brutal's Ktx.dll ("<function>1"), not libktx's own: native/ktx/ktx_c.c.
  "$CC" $TARGET -std=c11 -O2 -fPIC -w $KDEFS $KINC -c native/ktx/ktx_c.c -o "$KOBJ/ktx_c.o"
  "$CXX" $TARGET -shared -fPIC -static-libstdc++ -Wl,--no-undefined -o "$LIB/libKtx.so" "$KOBJ"/*.o -lm -llog
fi

if want astc; then
  echo "== astc"
  # ARM's astcenc 5.7.0 (Apache-2.0), NEON only (every arm64 core), for the GPUs without BC (hooks/Textures.cs).
  ASTC=$SRC/astc-encoder-5.7.0/Source
  "$CXX" $TARGET -std=c++17 -O3 -fPIC -s -fvisibility=hidden -static-libstdc++ -DASTCENC_NEON=1 -DASTCENC_SSE=0 -DASTCENC_AVX=0 -DASTCENC_SVE=0 -DASTCENC_POPCNT=0 -DASTCENC_F16C=0 -DASTCENC_NO_INVARIANCE=1 -shared -I "$ASTC" -o "$LIB/libportastc.so" native/astc/astc_shim.cpp "$ASTC"/astcenc_*.cpp -lm
fi

ls -la "$LIB" | awk 'NR>3 {print $5"\t"$9}'

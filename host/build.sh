#!/bin/bash
# Builds the host APK: our NativeActivity host, the stubs, and the CoreCLR native libraries, signed with a local key.
# usage: host/build.sh            (output: build/port17.apk)
set -e
cd "$(dirname "$0")/.."
ANDROID="/c/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer"
NDK="$ANDROID/NDK"
SDK="$ANDROID/SDK"
JDK="$ANDROID/OpenJDK"
BT="$SDK/build-tools/34.0.0"
export JAVA_HOME="C:/Program Files/Unity/Hub/Editor/2022.3.62f3/Editor/Data/PlaybackEngines/AndroidPlayer/OpenJDK"
export PATH="$JDK/bin:$PATH"
CLANG="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/clang.exe"
TARGET="--target=aarch64-linux-android26"
OUT=build
LIB=$OUT/apk/lib/arm64-v8a
rm -rf "$OUT/apk" "$OUT/unsigned.apk" "$OUT/aligned.apk"
mkdir -p "$LIB"

"$CLANG" $TARGET -shared -fPIC -O2 -Wall -Wno-unused-function -I host -o "$LIB/libporthost.so" host/host.c host/android_native_app_glue.c -llog -landroid -ldl -Wl,-u,ANativeActivity_onCreate
"$CLANG" $TARGET -shared -fPIC -O2 -o "$LIB/libsteam_api64.so" host/stubs/steam_api64.c
# The Android GLFW (window, input, Vulkan surface), bound by the engine as libglfw.so.
"$CLANG" $TARGET -shared -fPIC -O2 -Wall -Wno-unused-function -I host -o "$LIB/libglfw.so" host/glfw/glfw_android.c -landroid -llog -ldl
# The Vulkan interposer (identity surface transform, swapchain and present logging), found before the system loader.
"$CLANG" $TARGET -shared -fPIC -O2 -Wall -I downloads/src/Vulkan-Headers-1.3.296/include -o "$LIB/libvulkan.so" host/vulkan/vulkan_interpose.c -llog -ldl
# The engine's native libraries built from upstream sources (native/build.sh).
for f in build/native/lib/*.so; do cp "$f" "$LIB/"; done
# The Khronos validation layer only when asked (VVL=1 host/build.sh); the engine enables it with -EnableVulkanValidation.
if [ "$VVL" = 1 ]; then cp build/native/vvl/libVkLayer_khronos_validation.so "$LIB/"; fi
for f in libcoreclr.so libclrjit.so libSystem.Native.so libSystem.Globalization.Native.so libSystem.IO.Compression.Native.so libSystem.Security.Cryptography.Native.Android.so; do
  cp "dotnet-android-coreclr/runtimes/android-arm64/native/$f" "$LIB/"
done
ls -la "$LIB" | awk 'NR>3 {print $5"\t"$9}'

# The Java side: our Boot class plus the runtime's crypto helper classes, in one classes.dex.
rm -rf "$OUT/classes" "$OUT/dex"
mkdir -p "$OUT/classes" "$OUT/dex"
"$JDK/bin/javac.exe" -source 8 -target 8 -nowarn -bootclasspath "$SDK/platforms/android-34/android.jar" -d "$OUT/classes" host/java/com/local/waylith/port/Boot.java host/java/com/local/waylith/port/MainActivity.java host/java/com/local/waylith/port/TextInputView.java
# (d8.bat breaks on the space in the JDK path; the jar is run directly.)
"$JDK/bin/java.exe" -cp "$BT/lib/d8.jar" com.android.tools.r8.D8 --release --min-api 26 --lib "$SDK/platforms/android-34/android.jar" --output "$OUT/dex" "$OUT"/classes/com/local/waylith/port/*.class dotnet-android-coreclr/runtimes/android-arm64/native/libSystem.Security.Cryptography.Native.Android.dex
cp "$OUT/dex/classes.dex" "$OUT/apk/classes.dex"

"$BT/aapt2.exe" link -o "$OUT/unsigned.apk" --manifest host/AndroidManifest.xml -I "$SDK/platforms/android-34/android.jar" --min-sdk-version 26 --target-sdk-version 34
(cd "$OUT/apk" && "$JDK/bin/jar.exe" uf ../unsigned.apk lib classes.dex)
"$BT/zipalign.exe" -f -p 4 "$OUT/unsigned.apk" "$OUT/aligned.apk"
if [ ! -f port17.keystore ]; then
  "$JDK/bin/keytool.exe" -genkeypair -keystore port17.keystore -alias port -keyalg RSA -keysize 2048 -validity 10000 -storepass ***REMOVED*** -keypass ***REMOVED*** -dname "CN=port17"
fi
"$BT/apksigner.bat" sign --ks port17.keystore --ks-pass pass:***REMOVED*** --key-pass pass:***REMOVED*** --out "$OUT/port17.apk" "$OUT/aligned.apk"
ls -la "$OUT/port17.apk" | awk '{print "port17.apk", $5, "bytes"}'

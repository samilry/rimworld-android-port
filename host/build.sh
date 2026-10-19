#!/bin/bash
# Builds the host APK: our NativeActivity host, the stubs, and the CoreCLR native libraries, signed with a local key.
# usage: host/build.sh            (output: build/port17.apk)
#   PAYLOAD=port host/build.sh     the tester's APK (build/port17-tester.apk): the port without any of the game
#                                  (runtime, PortHooks, PortMobile, Harmony, the Workshop downloader, setup/PortSetup.dll
#                                  from setup/build.sh); the player imports their own copy of the game on the first start
#                                  (MainActivity.prepareGame, SetupService). This one may go to other people.
#   PAYLOAD=1 host/build.sh        the game's files go in as well (assets/payload.zip: the .NET framework, build/game,
#                                  build/workshop, an empty Mods folder), unpacked on the first start (Payload.java):
#                                  an APK for a phone they are not copied to by hand. Output build/port17-full.apk.
#                                  Only for the owner's own devices: it holds the whole game.
#   TEST_PACKAGE=name host/build.sh  another package name and the label "Port17 测试", to try an APK beside the
#                                  installed game (its own data folder; uninstalling it touches nothing else)
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
APK_NAME=port17.apk
[ "$PAYLOAD" = 1 ] && APK_NAME=port17-full.apk
[ "$PAYLOAD" = port ] && APK_NAME=port17-tester.apk

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
"$JDK/bin/javac.exe" -encoding UTF-8 -source 8 -target 8 -nowarn -bootclasspath "$SDK/platforms/android-34/android.jar" -d "$OUT/classes" host/java/com/local/waylith/port/Boot.java host/java/com/local/waylith/port/MainActivity.java host/java/com/local/waylith/port/TextInputView.java host/java/com/local/waylith/port/ZipNames.java host/java/com/local/waylith/port/Payload.java host/java/com/local/waylith/port/SetupService.java
# (d8.bat breaks on the space in the JDK path; the jar is run directly.)
"$JDK/bin/java.exe" -cp "$BT/lib/d8.jar" com.android.tools.r8.D8 --release --min-api 26 --lib "$SDK/platforms/android-34/android.jar" --output "$OUT/dex" "$OUT"/classes/com/local/waylith/port/*.class dotnet-android-coreclr/runtimes/android-arm64/native/libSystem.Security.Cryptography.Native.Android.dex
cp "$OUT/dex/classes.dex" "$OUT/apk/classes.dex"

MANIFEST=host/AndroidManifest.xml
if [ -n "$TEST_PACKAGE" ]; then
  sed -e "s/package=\"com.local.waylith.port\"/package=\"$TEST_PACKAGE\"/" -e 's/android:label="Port17"/android:label="Port17 测试"/g' host/AndroidManifest.xml > "$OUT/AndroidManifest.test.xml"
  MANIFEST="$OUT/AndroidManifest.test.xml"
  APK_NAME="${APK_NAME%.apk}-test.apk"
fi
"$BT/aapt2.exe" link -o "$OUT/unsigned.apk" --manifest "$MANIFEST" -I "$SDK/platforms/android-34/android.jar" --min-sdk-version 26 --target-sdk-version 34
(cd "$OUT/apk" && "$JDK/bin/jar.exe" uf ../unsigned.apk lib classes.dex)
# The notices, in every APK: host/licenses/NOTICE.txt (the notice RimWorld's EULA asks of mods, the open source parts and
# their licenses) and the license texts; the tester's import dialog shows them (MainActivity.showNotices).
mkdir -p "$OUT/apk/assets/licenses"
cp host/licenses/* "$OUT/apk/assets/licenses/"
(cd "$OUT/apk" && "$JDK/bin/jar.exe" uf ../unsigned.apk assets/licenses)

if [ "$PAYLOAD" = 1 ]; then
  # What host/stage.sh puts on the phone, in the layout of the app's external files folder.
  P=$OUT/payload
  rm -rf "$P"
  mkdir -p "$P/runtime" "$P/game/Mods" "$P/game/workshop" "$OUT/apk/assets"
  cp dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0/* "$P/runtime/"
  [ -f build/runtime/System.Private.CoreLib.dll ] && cp build/runtime/System.Private.CoreLib.dll "$P/runtime/"
  cp -r build/game/. "$P/game/"
  cp -r build/workshop/. "$P/game/workshop/"
  # The launch arguments of the phone (build/game's are for headless test runs).
  printf -- "-NoCrashDialog\n-Verbose\n" > "$P/game/args.txt"
  BYTES=$(du -sb "$P" | cut -f1)
  printf "%s\n%s\n" "$(head -1 build/game/Version.txt | tr -d '\r') $(date +%Y%m%d-%H%M%S)" "$BYTES" > "$OUT/apk/assets/payload.txt"
  (cd "$P" && "$JDK/bin/jar.exe" cfM ../apk/assets/payload.zip runtime game)
  ls -la "$OUT/apk/assets/payload.zip" | awk -v b="$BYTES" '{print "payload.zip", $5, "bytes (" b " unpacked)"}'
  cat "$OUT/apk/assets/payload.txt"
  # Stored, not compressed again: the APK entry is read as a stream on the first start.
  (cd "$OUT/apk" && "$JDK/bin/jar.exe" u0f ../unsigned.apk assets)
fi
if [ "$PAYLOAD" = port ]; then
  # The port only: nothing of the game. setup/version.txt: the rules (PortSetup and the PortHooks it imports from) and
  # the game version the port was made for; a new APK with other rules prepares the player's game again.
  P=$OUT/payload
  rm -rf "$P"
  mkdir -p "$P/runtime" "$P/game/Mods" "$P/game/workshop" "$P/setup" "$OUT/apk/assets"
  cp dotnet-android-coreclr/runtimes/android-arm64/lib/net10.0/* "$P/runtime/"
  [ -f build/runtime/System.Private.CoreLib.dll ] && cp build/runtime/System.Private.CoreLib.dll "$P/runtime/"
  for f in PortHooks.dll PortMobile.dll 0Harmony.dll Mono.Cecil.dll ModRules.txt; do cp "build/game/$f" "$P/game/"; done
  cp -r build/workshop/. "$P/game/workshop/"
  printf -- "-NoCrashDialog\n-Verbose\n" > "$P/game/args.txt"
  cp build/setup/PortSetup.dll build/setup/Mono.Cecil.dll "$P/setup/"
  ls build/forwarders | sed 's/\.dll$//' > "$P/setup/forwarders.txt"
  printf "%s\n%s\n" "$(cat build/setup/PortSetup.dll build/game/PortHooks.dll | md5sum | cut -c1-16)" "$(head -1 build/game/Version.txt | cut -d' ' -f1)" > "$P/setup/version.txt"
  BYTES=$(du -sb "$P" | cut -f1)
  printf "%s\n%s\n" "port $(date +%Y%m%d-%H%M%S)" "$BYTES" > "$OUT/apk/assets/payload.txt"
  (cd "$P" && "$JDK/bin/jar.exe" cfM ../apk/assets/payload.zip runtime game setup)
  ls -la "$OUT/apk/assets/payload.zip" | awk -v b="$BYTES" '{print "payload.zip", $5, "bytes (" b " unpacked)"}'
  cat "$P/setup/version.txt"
  (cd "$OUT/apk" && "$JDK/bin/jar.exe" u0f ../unsigned.apk assets)
fi
"$BT/zipalign.exe" -f -p 4 "$OUT/unsigned.apk" "$OUT/aligned.apk"
if [ ! -f port17.keystore ]; then
  "$JDK/bin/keytool.exe" -genkeypair -keystore port17.keystore -alias port -keyalg RSA -keysize 2048 -validity 10000 -storepass ***REMOVED*** -keypass ***REMOVED*** -dname "CN=port17"
fi
"$BT/apksigner.bat" sign --ks port17.keystore --ks-pass pass:***REMOVED*** --key-pass pass:***REMOVED*** --out "$OUT/$APK_NAME" "$OUT/aligned.apk"
ls -la "$OUT/$APK_NAME" | awk -v n="$APK_NAME" '{print n, $5, "bytes"}'

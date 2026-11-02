# Changelog

[中文](CHANGELOG.md) · **English**

This file lists what changed in Port17 each day, newest first. It is a shorter English version of the [Chinese changelog](CHANGELOG.md), which was put together from the development log and the notes sent to testers at the time. Testers' personal details are left out, and so are answers meant for a single tester (such as "this error comes from the mod itself").

- Each day has its own section. "Commits" links to all of that day's commits on GitHub. The code as it stood at the end of each day is tagged `vYYYY.MM.DD`, and each day has an entry on the [Releases](https://github.com/samilry/rimworld-android-port/releases) page.
- The **tester APK** has no game files in it; players import their own copy of the game. This repository does not ship APKs: build one yourself with [BUILDING.md](BUILDING.md).
- **Game versions** are build numbers of RimWorld 1.7 on Steam's unstable branch, for example 1.7.4995.
- There were no commits on 10-17, 10-18, 10-31 and 11-01.

## 2026-11-02: RimWorld 1.7.4995, public source, reproducible build

Game 1.7.4995 (1.7.4990 also works) · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.30...v2026.11.02) · [Code at the end of the day](https://github.com/samilry/rimworld-android-port/tree/v2026.11.02)

**Added**
- Left and right mouse buttons are now two separate on-screen buttons, taken out of the key wheel.
  - Each can be dragged anywhere, and its place is remembered.
  - A tap on "right" applies to the next touch only; a half-second hold locks it.
  - They hide while a mouse is connected.
- Mod update notice on the main menu: once a day, the installed Workshop mods are checked in the background. A click opens the Workshop window with the outdated mods ticked; nothing downloads by itself.
- The game's folders (saves and settings, mods) appear in the phone's Files app through a DocumentsProvider. Mods' "open folder" buttons open them there.

**Fixed and improved**
- Text input:
  - A tap outside a text field is handled before the keyboard closes.
  - Naming fields in mods no longer open the keyboard by themselves.
  - Mods can no longer reopen the keyboard after it was closed.
  - "Done" sends in the Phinix and RimTalk dialogs.
- DragSelect selects the starting cell after a hold, as on the PC.
- Map sounds are panned left and right the way the PC does.
- The inline guard rebuilds only the methods that can actually be affected (startup step about 4 s → 1.8 s on the owner's phone).
- Reopening the Workshop window right after closing it no longer hangs at "connecting".

**RimWorld 1.7.4995**
- The new game build quit at startup on the phone. Three stand-ins fix it:
  - field injection, handled as the PC behaves with that feature switched off (VEF uses its own fallback path);
  - the audio device watchdog, which also brings sound back after Bluetooth headphones disconnect or switch;
  - the profiler's new clock.
- A game assembly that uses another assembly's non-public types skips access checks, as on the PC. This fixes Melee Animation's patches on 4995.

**Source and build**
- The source is published on GitHub under the MIT license, with READMEs in Chinese and English and a technical write-up. Three things are left out of the public history: the private development log, the tester guide (it has game screenshots), and the signing password.
- Reproducible build: `BUILDING.md`, `host/fetch-deps.sh` (pinned versions, SHA-256 checked), `host/env.sh`, `host/build-all.sh` and `.gitattributes`.
  - A fresh clone built from nothing gave a tester APK whose 193 payload files are byte-identical to the development build.
  - On a phone it reached the main menu 27 s after importing the game.
- Unity type-forwarding facades are made again after a game update.
- This changelog.

## 2026-10-30: mod compatibility checked against the PC, item by item

Game 1.7.4990 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.29...v2026.10.30) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.30)

- Drag and tap tools with a confirm button (mine, deconstruct, cut, zones, walls) now hand each tap or drag to the game separately, in order, as the PC does on each release. Some mods fail when several are merged into one.
- A Desktop folder is created inside the game's folder for mods that reset Harmony's log there.
- Releasing a hold no longer presses DragSelect's buttons.
- Mod lists drawn with the engine's own scroll views scroll by finger, with momentum.
- Smart Speed's buttons are finger-sized.
- With cutout avoidance on, Vanilla Gravship Expanded's panel moves with the architect menu.
- The inline guard covers everything the runtime really inlines, so a mod's patch to a small method always takes effect.
- Patch shapes that work on the PC are handled the same way on the phone: a return inside a try block, leaving a try block, extra values under the return value.
- The port's own fixes survive when a mod unpatches everything.
- Stack traces show file and line, as on the PC (when the mod ships a PDB).
- `.rimunity` files are prepared once.
- x86-flagged assemblies loaded from memory are fixed like files.
- MonoMod flushes the instruction cache.
- Reflection rules now follow the PC's runtime: writing static readonly fields, finding framework private fields by their Mono names.
- Android asks whether to keep data (saves, settings, mods) on uninstall.
- Stand-in libraries are found for mods loaded in special ways.
- User folders (Documents, Pictures, `.config`) exist inside the game's folder.
- Mods' web requests use the phone's proxy.
- "Missing dependency" links and Workshop buttons open the in-game Workshop window.
- Two different mods with the same packageId are both kept on import.
- Mono interface sounds follow the PC's level and panning.
- Emoji draw as monochrome outlines (Noto Emoji) instead of boxes.

## 2026-10-29: the PC's shader compiler, third round of PC parity

Game 1.7.4990 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.28...v2026.10.29) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.29)

- Shaders that mods bring as source are now compiled on the phone by Slang 2026.19 itself, the compiler the PC game uses, built for Android from source.
  - Of 372 shaders, the 371 the PC compiles come out as byte-identical SPIR-V.
  - The APK grows by about 15 MB. The compiler uses about 250 MB while it works and is unloaded 10 s later.
- Mods' Windows-style backslash paths are rewritten to `/` when mods are prepared. Files that were created with backslashes in their names are moved to the right places.
- Culture rules come from the phone's system ICU, as on the PC: sorting, comparison, normalization, date and number formats.
- "Export save…" in the load window: one save, or all saves as a zip, to Download/RimWorld.
- Links and folders that mods hand to the system: web addresses open in the browser, and folders show where they are.
- The full BinaryFormatter from Microsoft's compatibility package.
- The real chip name in `SystemInfo.processorType`.
- F10 screenshots are also saved to the gallery.
- A hardware keyboard stays with the game after a text field loses focus.
- Importing 100+ mods no longer climbs to 4.5 GB.
- Loading an assembly name twice returns the one already loaded, as on the PC.
- PurePatcher gets the framework assemblies it expects in the game folder.
- Cutout avoidance also applies to the main menu.

## 2026-10-28: font crash, thread stacks, stand-in assemblies

Game 1.7.4990 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.27...v2026.10.28) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.28)

- An occasional crash in `libimgui_freetype.so`: two threads were making glyphs for the same font at once. Glyph making now takes turns.
- After a GPU device loss while compressing textures, older Adreno GPUs compress on the CPU for 7 days.
- A stand-in assembly made for a mod that loads early could block the real assembly from loading later. The real one now loads, as on the PC.
- Threads that the game and mods start get 16 MB stacks, the same as the main thread. They had 1 MB; the PC gives 1.5 MB.
- Crash reports pick up stack-overflow locations from the system log.
- The gear tab on short screens no longer jumps back to the top when tapped.

## 2026-10-27: RimWorld 1.7.4990, compatibility mode, better reports

Game 1.7.4990 (1.7.4985 also works) · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.26...v2026.10.27) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.27)

- Support for 1.7.4990: an adapter for engine types that moved, and the new font fallback. 4985 and 4990 both work.
- New compatibility mode: it turns off every layout change the port makes, to tell whether an interface problem comes from the port.
- Errors that show both the port and a mod on the stack are marked as possible conflicts.
- Crash reports now include:
  - `summary.txt`, the log's errors grouped by message;
  - `mods.txt`, each mod's packageId, supported versions and load order;
  - `watch.txt`, memory and stalls while loading;
  - for native crashes, the memory region of the faulting address.
- GWP-ASan is enabled.
- New: MP4 playback through MediaCodec, KTX and KTX2 textures, a clipboard shared with the phone, links that open in the browser.
- A warning appears when the imported game is newer than the APK supports.
- Fixed:
  - Readable Numbers made the interface disappear.
  - The Mali ASTC cache stopped growing just below its limit.
  - The options page lost the port's rows when a mod patch threw.
  - The speed buttons went back to PC size.
  - An ImGui assert crash while scrolling long mod lists.
  - Decimal formatting now matches the PC.
  - The main thread's stack went from 1 MB to 16 MB.
  - Fields that moved to a base class (mod preparation rules v13).
  - Code that refers to missing mods fails as it does on the PC.
  - Newer library references resolve as on the PC.
  - Shader variable names such as `common` now compile.
- The log counts loaded and missing textures and sounds per mod.
- A swipe-away is no longer reported as an abnormal exit.
- An automated smoke test of 34 popular interface mods found no port errors.

## 2026-10-26: the cause of missing textures, input mode detection, confirm buttons for dev tools

Game 1.7.4985 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.25...v2026.10.26) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.26)

- The atlas cache is checked page by page at startup, and rebuilt when a tile is missing.
- The cause of the missing tiles: Adreno 600/700 GPUs read atlas pages that were not finished yet. Atlas pages are now compressed on the CPU on Adreno GPUs below the 800 series.
- Mod assemblies flagged 32-bit x86 only (EPOE) load as on the PC.
- The command grid wraps before the time buttons.
- Cutout fixes:
  - Better Architect Menu's panel;
  - research tabs;
  - table pages sized to the screen;
  - RimHUD at large UI scales.
- A double tap counts only on the same thing.
- Keyboards switch the input mode only once they are typed on. Bluetooth sleep no longer flips modes.
- The confirm bar sits beside the game's shape and rotate arrows. Drags released over a button end properly.
- Number fields are selected whole on a tap.
- Dev-mode map tools and one-tap placement tools in mods (Universal Blueprints) get confirm and cancel buttons.
- Import errors are explained for files that are still downloading or in the trash.

## 2026-10-25: list momentum, render resolution, save import

Game 1.7.4985 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.24...v2026.10.25) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.25)

- Render resolution option: native, 1080p or 720p. The UI scale is never changed automatically; a hint shows which scale keeps the same size.
- Coasting lists no longer leave a blank band, and fling distance no longer depends on the frame rate.
- The gear tab keeps one size.
- Importing a single `.rws` save works.
- The save dialog no longer opens the keyboard by itself (control ids repeat when a window reopens).
- Dev-mode debug windows scroll by finger.
- The research window no longer widens itself again with cutout avoidance on.
- HugsLib finds its log file.

## 2026-10-24: save import, IME, Kirin and older Mali crashes

Game 1.7.4985 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.23...v2026.10.24) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.24)

- "Import save…" in the load window.
- Chinese input no longer gets swallowed after the IME process restarts.
- Older Mali-G57 drivers: a depth format they lack is swapped for one they have.
- Kirin phones: MonoMod's architecture detection is fixed, so the port's patches install.
- Key wheel keys can be renamed.
- Cutout fixes for the key wheel and the inspect tabs (with RimHUD too).
- The Log tab responds again.
- The containment platform's transfer and capture choices work (Anomaly).
- Area tools explain, as on the PC, when nothing in the box can be designated.
- The God Hand mod can grab and drag by finger, and its hold-to-charge strike works.
- Dev window lists scroll by finger.
- Monolyn Race's placement tools were checked one by one.

## 2026-10-23: two-step landing, camera cutout, mouse and keyboard mode, command icon size

Game 1.7.4985 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.22...v2026.10.23) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.23)

- Shuttle and pod landing takes two steps (pick, then confirm), with rotate buttons.
- Camera cutout avoidance: the picture stays full screen, and only the interface that overlaps the hole moves.
- A connected mouse or keyboard switches to PC-style controls automatically (USB or Bluetooth). The mode can also be fixed in the options.
- Command icon size (100/85/70 %) and automatic shrinking when the commands take more than two or three rows.
- Fixed:
  - partly drawn textures on Adreno 6xx/7xx (missing atlas mip levels);
  - crashes on drivers that lack debug functions or extensions;
  - file chooser crashes on some Xiaomi phones (games can now be imported from a fixed folder);
  - "Don't keep activities" is detected;
  - rotated icons in shrunk grids;
  - lists that would not scroll with many mods;
  - a long stall after loading when memory is swapped;
  - transient GPU memory from mods that process hundreds of textures at the main menu (800 MB → 120 MB);
  - memory access violations in mods are caught as on the PC instead of crashing;
  - a crash from a freed texture after long play.
- A faster warm-up after loading was tried, measured slower on a late-game save, and reverted the same evening.

## 2026-10-22: touch fixes, RimWorld 1.7.4985, Linux copies of the game

Game 1.7.4985 (4984 and 4982 also work) · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.21...v2026.10.22) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.22)

- Two touch bugs:
  - a hold joined by a second finger swallowed every later tap;
  - quick repeated swipes counted as double taps.
- Support for 1.7.4985 (frame pacing, texture copies, text layout). The owner's phone with 147 mods runs about 360 TPS and 114 FPS at triple speed.
- A long press dismisses letters, like a right click.
- An input bar shows the text being typed when the keyboard covers the field. "Done" only ends input.
- Manual saves no longer open the keyboard. Edge scrolling is off for fingers.
- Mod folders the game cannot read are skipped, with an explanation, instead of a black screen.
- No more false crash reports. Adreno 7xx driver hints are filtered out of the log.
- A game version reminder appears at the main menu.
- Allow Tool's drag tools act when the finger lifts and have a cancel button.
- Architect grids that do not fit are scaled down.
- Linux and Mac copies of the game import correctly: the Steam stub is shipped under both library names.

## 2026-10-21: RimWorld 1.7.4984, ASTC textures on Mali, sturdier import

Game 1.7.4984 (4982 also works) · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.20...v2026.10.21) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.21)

- Support for 1.7.4984. Vanilla Gravship Expanded loads again after its update that day.
- Mali GPUs get ASTC textures:
  - atlases are compressed on the first start, other textures in the background on idle cores;
  - a self-test runs at each start;
  - a cache of up to 3 GB keeps the results for 30 days.
- Item textures no longer vanish when zoomed in.
- Profiler mods no longer crash, and more of the PC runtime's compatibility APIs for old mods are in place.
- Key bindings can be set without a keyboard, and Dubs Mint Menus' wheel works by touch.
- The game is less likely to be killed in the background.
- Import hardening:
  - one import at a time;
  - a half-finished import is not taken as done;
  - the import survives the file provider being killed;
  - no recreation on system font or language changes.
- A Mali crash from the UI descriptor pool is fixed.
- A mod that patches a method and then a small method it calls now gets both patches.
- A 1.7 test build of the Multiplayer mod (separate, not in this repository) ran a phone and a PC in the same game for 16 minutes at triple speed without desync.

## 2026-10-20: black screen after Home, UI scale, key wheel, double tap

Game 1.7.4982 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.19...v2026.10.20) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.20)

- A black screen after coming back from the background is fixed (the surface was not rebuilt).
- UI scale from 0.5x to 4x, with keep or revert after a change.
- 1.6-era mods that failed with `DefaultInterpolatedStringHandler` load: Prepatcher, PurePatcher, Kingfisher.
- World map targets are confirmed by tapping the same place twice.
- Double taps are easier: 0.5 s and about 6 mm.
- Drafted controls are back to normal.
- Key wheel:
  - Shift moved into it;
  - keys are shown with their labels;
  - left, middle and right mouse toggles;
  - any keyboard key can be added.
- "Re-import game" in the launcher icon's menu.
- Mods that check for Linux find their resources (Nice Health Tab).
- Imports on low-memory phones no longer fail.
- No more false crash reports.
- Non-zip files are explained.
- Setup gets all cores.
- Mali icon workarounds.
- Landscape follows the phone's rotation.

## 2026-10-19: the tester APK without the game, RimWorld 1.7.4982, crash reports

Game 1.7.4982 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.16...v2026.10.19) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.19)

- The tester APK carries the port without the game. Players import their own copy, which PortSetup prepares on the phone.
- Ludeon's EULA notice and third-party licenses are in the tester APK.
- Support for 1.7.4982.
- Mali GPUs, which cannot sample BC textures, get them decoded at load.
- Crash reports and log export for testers, with API keys masked.
- Stand-ins in the Vulkan interposer for debug functions that older drivers lack (Snapdragon 865).

## 2026-10-16: 120 fps, RimWorld 1.7.4980

Game 1.7.4980 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.15...v2026.10.16) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.16)

- A 120 fps frame rate limit.
- A profile of a late colony at triple speed, and an A/B measuring command.
- Support for 1.7.4980.
- Tap-to-order controls for drafted pawns (reverted on 10-20).

## 2026-10-15: play-testing through MCP, RimWorld 1.7.4979

Game 1.7.4979 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.14...v2026.10.15) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.15)

- An MCP server and an in-game agent let Claude see, tap and read errors on the phone like a player (`host/mcp`).
- Five rounds of play-testing, with fixes:
  - covered: caravans, travel, drafting in combat, prisoners, weddings, raids, transport pods, the gravship;
  - fixes: a general inline guard, taps eaten by windows that follow the pointer, tool controls with no category open, caravan routes by hold.
- Support for 1.7.4979.
- Mod preparation rules v9.
- Pawn names no longer change on every launch: Mono's string hash is emulated in CoreLib, as in Ludeon's runtime fork.
- The research tree scrolls where it does not fit.
- A "Show TPS and FPS" option.

## 2026-10-14: scheduling, memory, first phone UI round, in-game Workshop

Game 1.7.4976 · [Commits](https://github.com/samilry/rimworld-android-port/compare/v2026.10.13...v2026.10.14) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.14)

- Scheduling and frame rate options:
  - a 32 MiB young generation;
  - finer job batches;
  - a frame rate limit (30/60/90) with a choice of what gives way.
- Efficiency review: the frame limiter sleeps, and the start no longer walks every mod file.
- Against the earlier 1.6 port, on the same vanilla 100-colonist save at triple speed: 31–36 FPS / 339–353 TPS became 57 FPS / 359 TPS.
- Memory in the same scene: about 5.4 GB → 2.3 GB (process plus GPU).
- First phone UI round:
  - tap assist for small targets;
  - an on-screen Shift key;
  - a main bar clear of the rounded corners;
  - search boxes that take focus only from a finger;
  - no window jump on pinch.
- An in-game Steam Workshop downloader (QR code or password sign-in).
- Mod preparation rules v7 and v8.
- A clean quit to desktop. World map names in non-Latin scripts.

## 2026-10-13: from nothing to the main menu, then to 92 mods

Game 1.7.4976 · [Commits](https://github.com/samilry/rimworld-android-port/commits/v2026.10.13) · [Code](https://github.com/samilry/rimworld-android-port/tree/v2026.10.13)

- On the PC, the game ran fully on Microsoft's stock .NET 10 runtime.
- The Android host:
  - a NativeActivity that starts CoreCLR from the `linux-bionic-arm64` runtime pack;
  - a startup hook that maps Unity assembly names to UnityStubs.
- A headless boot on the phone, then a picture:
  - an Android GLFW shim;
  - a Vulkan interposer;
  - arm64 native libraries (VMA, stb, gli, ImGui with 1146 generated C exports, FreeType).
- Harmony runs on the phone (MonoMod on bionic).
- Touch layer v1:
  - gestures, finger scrolling and tooltips on hold;
  - two-step map tools with confirm buttons;
  - fixed UI scale 1.85 and windows fitted to the screen.
- Audio rebuilt on miniaudio and stb_vorbis (AAudio), with Freeverb reverb.
- Mods:
  - type-forwarding facades for 76 Unity assembly names;
  - mod assemblies prepared for the phone's runtime, embedded ones too;
  - in-game mod import and RimSort-style sorting.
- Chinese interface and Chinese input.
- Stand-ins for four leniencies of Mono and the runtime fork. The owner's 92 mods then started with zero errors.

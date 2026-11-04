# Port17: technical overview

> An unofficial fan project, not affiliated with or endorsed by Ludeon Studios. Game class and method names appear only to say where the port hooks in; the repository contains no game code, assets or binaries.

This is a two-page summary of [TECHNICAL.md](TECHNICAL.md) (Chinese, with the details, measurements and code excerpts). Project page: [README](../README.md).

## The problem

RimWorld 1.7 left Unity for Ludeon's own engine, Waylith: a plain .NET 10 application with Vulkan 1.3 and VMA, GLFW, Slang shaders compiled at run time, ImGui and FreeType for the interface, miniaudio behind a native `waylithaudio` library, and BC-compressed textures. It has Windows, Linux and macOS code paths only. On the PC it runs on Ludeon's own CoreCLR fork, whose runtimeconfig turns on 20 `WaylithClr.*` switches that keep Mono-era mods working.

The goal: play 1.7 on Android with **PC mods working unchanged**, added and removed at will. The constraints: no game files are distributed (players import their own copy); mod files are never edited (the phone prepares its own copy); the phone runs Microsoft's stock .NET 10 CoreCLR for Android, which has none of Ludeon's switches; the interface scale stays fixed, and only what does not fit shrinks; where the phone differs, it copies the PC's exact behaviour; every change can be switched off.

## Architecture

- **APK host**: `MainActivity` (a `NativeActivity`) and `host.c` start CoreCLR on a 16 MB-stack thread, with `PortHooks.dll` as the startup hook, then run the game's launcher. Java covers the soft keyboard, file pickers, foreground services, crash reports and a DocumentsProvider.
- **PortHooks** (startup hook, before the game's `Main`): assembly binding, a faked process path, the GPU memory budget, mod preparation, repair of Harmony's emitted code, BC decoding and ASTC, fonts, stand-ins for missing assemblies, a start-up watchdog.
- **PortMobile** (loaded when the main menu appears, all Harmony patches): touch, interface fitting, key wheel and mouse buttons, camera-cutout avoidance, frame pacing, memory trimming, a run record, a test agent.
- **patch.dll** (Cecil, at build time) and **PortSetup** (the same code on the phone) rewrite the game's platform checks and adapt the Harmony mod's `0Harmony.dll` (MonoMod on bionic).
- **ModPrep**: one source compiled into both patch.dll and PortHooks; prepares each mod before its first assembly loads.
- **Native libraries**: an Android GLFW shim, a Vulkan interposer, `waylithaudio` rebuilt on miniaudio, Slang 2026.19 built for Android, VMA, ImGui/FreeType, KTX, astcenc, and MonoMod helpers for bionic.
- **Workshop downloader** on SteamKit2, in a load context of its own, with the player's own Steam login.

## Hard problems

**Emulating Ludeon's runtime on the stock one.** Early mod failures (Humanoid Alien Races, MinifyEverything) first looked like "not updated for 1.7"; they were the missing switches. Managed switches (Mono string hash, Mono float formatting, dynamic-method leniencies, Mono private field names, by-name resolution of isolated loads, static readonly writes) went into a patched `System.Private.CoreLib`, recompiled with the matching crossgen2. Two binary patches in the stripped `libcoreclr.so` let a refused `sched_setaffinity` pass, as newer upstream builds do (on one tester's phone the runtime never started), and remove a native readonly check. Mod-level switches became ModPrep rules: `IgnoresAccessChecksTo`, retargeted type references, pops after framework calls that gained a return value, inherited field references. HarmonyCompat rebalances stacks that Mono-era transpilers leave unbalanced before Harmony emits them. InlineGuard reproduces "never inline game methods into Harmony replacements": when a method is first patched, replacements built earlier that may hold an inlined copy are rebuilt, using an inlining model measured on .NET 10 (20 levels, 128/1024 IL bytes, try/finally inlined, catch not). Measured on the PC with the owner's 130 mods' patch targets, it leaves 0 stale copies (the earlier, narrower version left 22), at 1.8 s of start-up on the phone. Missing assemblies get empty stand-ins in a separate load context, and access violations are made catchable.

**PC mods unchanged.** ModPrep (rules version 14) also fixes x64- and x86-only PE marks, removes attributes of missing types (one such attribute stopped every mod from loading), adapts calls to game methods whose signatures changed in 1.7, bridges overrides by an old return type, rewrites mods' own OS checks to "Linux", converts backslash-only path literals (11 in 732 mod assemblies), keeps PDBs, and handles embedded and byte-loaded assemblies. A stamp lists each prepared file, so the start-up check went from 753 ms to 130 ms. PC parity extends to same-name `LoadFrom`, PurePatcher's self-contained layout, system ICU, BinaryFormatter, clipboard, links, MP4 through MediaCodec, KTX, emoji, and asset bundles of mod folders not named after their Workshop id.

**Touch and interface without global rescaling.** The GLFW shim hands raw fingers to `Touch.cs`, which injects mouse events and classifies a press by the game's own reaction (selection box, tool, interface). Tap assist sends a press that lands just beside a small control (under 5.2 mm, extended by up to 1.8 mm a side) to that control; measured on screen, none of 106 controls reached 5 mm. Lists scroll with inertia and click on lift; tools confirm in two steps; a key wheel sends real key events; two movable buttons give right clicks. Windows that do not fit are drawn smaller by scaling the GUI matrix around them; command grids follow an exact model of the game's layout; only interface overlapping the camera hole is moved. A text view feeds the IME into the game. The frame rate is the same with all of it on or off (48–53 FPS on, 48–54 off, at 3×).

**GPU differences.** The Vulkan interposer reports an identity transform, hides non-coherent memory, splits descriptor pools (Adreno backs each set with about 23 KiB of GPU memory: 5421 → 3366 MiB for one map), stubs missing debug extensions, fixes a stale swapchain table (black screen after Home: 11/12 → 0/13), swaps D32S8 for D24S8 on Mali and drops commands on null images. Mali cannot sample BC textures: they are decoded, then re-encoded to ASTC 4×4 on the phone and cached (atlases 821 MB instead of ≈3.3 GB RGBA). Broken atlas levels on Adreno 7xx are detected and rebuilt.

**Shaders.** A glslang stand-in compiled 7 of 36 GLSL forms the PC's Slang accepts, and Slang chooses some layouts itself, so Slang 2026.19 was built for Android (NDK r27c, a small patch, a code-less `libslang.so` proxy). All 371 shaders the PC compiles from a 372-shader corpus give byte-identical SPIR-V; sessions (215 MB each) are limited to one and released when idle.

**Audio.** `waylithaudio` has 59 exports on miniaudio. Decoding all ~6,000 sounds at load cost 1.7 GB; on-demand decoding with a 64 MiB cache took the native heap from 1475 to 366 MiB. The mix matrix and 3D panning follow the PC library (59 of 62 test scenes identical; the rest differ in a pan mode the game does not use); 1.7.4995's device watchdog was added.

**Performance.** Measured in CPU cycles, not FPS: vendor clock caps and a closed-loop 30-FPS hold let the same scene run at 28 or 42 FPS from one run to the next. Wins: the GC's gen0 budget (the runtime fell back to 256 KiB, 80 collections a second; pauses 9.7 % → 0.4 %), finer parallel batches (26 FPS / 310 TPS → 43 / 357 with 57 modded colonists at 3×), a nanosleep frame limiter (−13.5 % main-thread cycles at 60 FPS), and memory work (≈5.4 → 2.3 GB vanilla, ≈7.4 → 4.4 GB with 106 mods). Measured and dropped: core pinning, Android hint sessions, triple buffering, multi-core JIT, ReadyToRun for game code (it would defeat Harmony patches), and TieredPGO off (late-game ticks about 9 % slower).

**No game in the APK.** The ≈55 MB tester APK carries the runtime and the port only. The player picks their game folder or zip; PortSetup prepares it in a separate process (about 8 s) and the game starts: 44 s from picking the zip to the main menu. Real imports on testers' phones and the development phone drove the hardening: foreground services, an 8-thread copy (13.5 min → 97 s for 17,498 files), GBK file names in zips, vendor file pickers, unreadable mod folders. Reports carry a run record (with the moments a tester marked and described), a start-up watchdog, decoded tombstones (naming the methods of compiled code from the runtime's perf map when the crash comes within three days of an earlier native crash), the runtime's last words and an error summary.

**Game updates.** Eight updates (1.7.4976 → 4998) were followed with one recipe: stage the new version beside the old, compare assemblies and the runtime, rebuild, check the build against kept older versions (`host/compat-check.sh`), verify on a side-by-side test package. Examples: 4990 moved engine texture types (bound by name for both versions); 4995 added runtime field injection and 4998 a new runtime interface for method-body replacement, used by the profiler and by free patches (stand-ins behave as the PC with these features off); 4998 also made the engine's own frame wait precise, so the port no longer replaces it on that build.

## Testing

PC harnesses on the stock runtime and on the PC's own runtime files (run without the game); byte-for-byte comparisons of PortSetup output and shader SPIR-V; a side-by-side test package for clean installs; self-test and fault-simulation switches; an MCP server with 38 tools that lets Claude drive and read the game on a real phone with real touches; scripted measurements (cycles per tick, memory by source, controls in millimetres); an automated smoke test of 34 popular interface mods.

## Results

| | |
|---|---|
| Scale | 470 commits, 2026-10-13 to 11-04 (300+ of them in this repository's history, the rest only updated the private development log and tester guides; daily changes in [CHANGELOG.en.md](../CHANGELOG.en.md)); about 36k lines of C#, 7.5k C/C++, 6.7k Java |
| Start-up | vanilla 16.7 s to the main menu; 130+ mods about 46–48 s |
| 3× speed | vanilla 100 colonists 57 FPS / 359 TPS (60 FPS cap); a modded mid-game colony 357–360 TPS, 110–119 FPS (120 cap) |
| vs. the 1.6 port | 31–36 → 57 FPS; main-thread cycles per tick halved |
| Memory | ≈5.4 → 2.3 GB vanilla, ≈7.4 → 4.4 GB with 106 mods |
| Shaders | 371 of 371 byte-identical to the PC |
| Workshop | a 146-mod collection (2.5 GB) in about 17 minutes, no failures |

## Lessons

Measure cycles, not frame rates. When a mod fails only on the phone, check the PC runtime's switches before blaming the mod. Inlining is Harmony's enemy (JIT tiers, ReadyToRun CoreLib), so test patches at start-up too. The engine dispatches input to windows before `OnGUI`. A stand-in for an engine library must match what the game does with it at start-up. The PC's native libraries are release builds. Compare a failing log's first errors with a good log before concluding. Build every change as a switch with a way to reproduce its fault. Matching the PC beats improving on it.

## Developers and how it was built

- **samilry**, project owner: set the goals, made the decisions, tested on real phones, relayed testers' reports and publishes the project.
- **Claude (Anthropic)**, working through Claude Code: did most of the engineering under the owner's direction, including analysis, design, implementation, measurement, the testing tools (among them the MCP tools that drive the phone) and this documentation.

Commits carry a `Co-Authored-By: Claude` trailer.

# 从源码构建 Port17 / Building Port17 from source

[中文](#中文) · [English](#english)

---

## 中文

这份说明从一台干净的 Windows 电脑开始，构建测试者安装包 `build/port17-tester.apk`。安装包里没有任何游戏文件：玩家第一次启动时导入自己的 RimWorld 1.7。

构建用到的工具和第三方源码都固定在开发时的版本。`host/fetch-deps.sh` 从各自的官方来源（Google 的 Android 仓库、Eclipse Adoptium、nuget.org、各项目在 GitHub 上的标签）下载，逐个核对 SHA-256。用同一份游戏和同一份 Harmony，构建出的 PortHooks、PortMobile、PortWorkshop、PortSetup、patch.dll、改过的 CoreLib 和原生库与开发机上的逐字节相同；例外见第 6 节。

### 1. 需要先装好的

| | 说明 |
|---|---|
| Windows 10 或 11（x64） | 脚本都在 Git Bash 里运行。 |
| [Git for Windows](https://git-scm.com/download/win) | 带 Git Bash、curl、unzip、tar、patch、sha256sum、cygpath。仓库的 `.gitattributes` 规定了换行符，`core.autocrlf` 不用改（测试用的是 2.55）。 |
| [Node.js](https://nodejs.org/) 18 或更新 | 几个小工具脚本（测试用的是 24.21）。 |
| [.NET 8 运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（x64） | `dotnet` 要在 PATH 上；装 .NET 8 SDK 也可以。C# 编译器、patch.dll、ILSpy 都在它上面运行，patch.dll 编译时引用它的程序集（引用只记程序集名和版本 8.0.0.0，所以别的 8.0.x 应当得到同样的文件；测试过的是 8.0.19）。 |
| [Visual Studio 2022](https://visualstudio.microsoft.com/vs/)（Community、Professional、Enterprise 或 Build Tools） | 只用来编 Slang 的代码生成器。要装“使用 C++ 的桌面开发”工作负载里的 MSVC v143、Windows 10/11 SDK 和“适用于 Windows 的 C++ CMake 工具”（CMake 和 Ninja）。装在别处时用 `VS_DIR` 指过去。测试用的是 17.14（MSVC 19.44，CMake 3.31）。 |
| [Python 3](https://www.python.org/downloads/windows/) | 只有 Slang 的构建要：它带的 SPIRV-Tools 用 Python 生成语法表（CMake 自己找，测试用的是 3.14）。用 python.org 的安装包，不要用 Microsoft Store 的占位程序。 |

不需要 Android Studio 和 Unity。

- **磁盘**：`DEPS_DIR` 构建完约 13 GB，其中 2.3 GB 是下载的压缩包（`DEPS_DIR/archives`，可以删）、约 4 GB 是 Slang 的构建目录；构建输出约 1.5 GB（`build/game` 是游戏的一份拷贝）。`DEPS_DIR` 用短路径，不要带空格（Slang 的源码树很深）。
- **网络**：下载约 2.5 GB。构建时还会联网两次：`host/stage.sh` 下载 RimSort 社区排序规则库（`host/modrules/build.js`），Slang 的 CMake 下载几个 Windows 上测试用的预编译包（不进安装包）。
- **时间**：开发机上（24 个逻辑处理器，下载约 50 MB/s），取依赖约 6 分钟，构建约 9 分钟，大部分是 Slang；处理器少的电脑上 Slang 要半小时以上。以后再构建只要几分钟（Slang 和 ImGui 的扫描结果会留着）。

### 2. 自己提供的两样东西

- **自己的 RimWorld 1.7**（Steam 正版；1.7 现在是 Steam 上的 unstable 测试分支）。构建对着游戏的程序集编译，并从中准备 `build/game`；安装包里不放任何游戏文件。参考构建用的是 1.7.4995 rev309。PortMobile 记下构建时的游戏版本，玩家的版本不同时在主菜单提醒。
- **Harmony mod 的 1.7 版 `0Harmony.dll`**：在创意工坊订阅 Harmony（2009463077），文件在 `steamapps/workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll`。

在 Git Bash 里用环境变量指向它们（Git Bash 的 `/c/...` 写法或 `C:/...` 都可以）：

```bash
export RIMWORLD_DIR="/c/Program Files (x86)/Steam/steamapps/common/RimWorld"
export WORKSHOP_DIR="/c/Program Files (x86)/Steam/steamapps/workshop/content/294100"   # Harmony 在它下面
# 或者直接指定文件：export HARMONY_DLL=".../2009463077/1.7/Assemblies/0Harmony.dll"
```

### 3. 下载依赖

```bash
git clone https://github.com/samilry/rimworld-android-port.git port17
cd port17
export DEPS_DIR=/d/port17-deps     # 不设就放在仓库里的 downloads/
host/fetch-deps.sh
```

`host/fetch-deps.sh` 可以重复运行：已经完整解压的会跳过（`DEPS_DIR/.fetched` 里有记号），中断后再运行一次即可。也可以只取一部分，比如 `host/fetch-deps.sh sdk jdk`。它取的是：

| 名称 | 版本 | 来源 | 用途 | 下载大小 |
|---|---|---|---|---|
| `ndk` | Android NDK r23b（23.1.7779620） | dl.google.com | 除 Slang 外所有原生代码（与 Unity 2022.3 带的 NDK 文件相同） | 789 MB |
| `slang-ndk` | Android NDK r27c（27.2.12479018） | dl.google.com | Slang（要 C++20） | 782 MB |
| `sdk` | build-tools 34.0.0、platform android-34 r2、platform-tools 37.0.1 | dl.google.com | aapt2、d8、zipalign、apksigner、android.jar；adb | 58 + 63 + 8 MB |
| `jdk` | Eclipse Temurin 11.0.14.1+1 | GitHub（adoptium） | javac、jar、keytool；d8 和 apksigner 用的 java（与 Unity 2022.3 带的 JDK 相同） | 197 MB |
| `dotnet` | Microsoft.NETCore.App.Runtime.android-arm64 10.0.12；Microsoft.NETCore.App.Crossgen2.win-x64 10.0.12 | nuget.org | 手机上的 CoreCLR 运行时；把改过的 CoreLib 重新预编译 | 190 + 12 MB |
| `roslyn` | Microsoft.Net.Compilers.Toolset 4.11.0 | nuget.org | C# 编译器 csc.dll | 22 MB |
| `cecil` | Mono.Cecil 0.11.6 | nuget.org | patch.dll、PortSetup、PortHooks 改写程序集 | 0.7 MB |
| `ilspy` | ilspycmd 9.1.0.7988 | nuget.org | 反编译游戏的 Brutal.ImGui，取 ImGui 结构体大小做检查 | 4 MB |
| `nuget` | SteamKit2 3.4.0、protobuf-net 3.2.56、protobuf-net.Core 3.2.56、ZstdSharp.Port 0.8.7、System.IO.Hashing 10.0.12、QRCoder 1.8.1、System.Runtime.Serialization.Formatters 10.0.0 | nuget.org | 创意工坊下载器的库；电脑版游戏带的 BinaryFormatter 实现 | 10 MB |
| `sources` | Vulkan-Headers 1.3.296、VulkanMemoryAllocator 3.3.0、gli（提交 3542f88）、glm 1.0.1、Dear ImGui 1.92.2-docking、FreeType 2.13.3、astc-encoder 5.7.0、miniaudio 0.11.22（含 stb_vorbis）、stb_image 2.30 / stb_image_write 1.16（stb 提交 013ac3b） | GitHub | 引擎原生库的上游源码 | 66 MB |
| `ktx` | KTX-Software 4.4.0 | GitHub | libKtx（只解出编库用的目录） | 213 MB |
| `slang` | Slang 2026.19（提交 0fb6b75，含子模块） | GitHub（git clone） | 电脑版游戏的着色器编译器 | 克隆后约 400 MB |
| `emoji` | Noto Emoji 3.002（google/fonts 提交 b979dba） | GitHub | 手机上的轮廓 emoji | 2 MB |

### 4. 构建

```bash
read -rsp "签名密钥库的密码: " PORT17_KS_PASS; echo; export PORT17_KS_PASS
host/build-all.sh
```

结果是 `build/port17-tester.apk`（约 56 MB）。第一次构建会在仓库根目录生成 `port17.keystore`（自签名的 RSA 2048 密钥，CN=port17），密码就是 `PORT17_KS_PASS`。以后的版本要用同一个密钥库和同一个密码签名，手机上才能覆盖安装：把这两样保存好，不要提交到仓库，也不要发给别人。

`host/build-all.sh` 按依赖顺序运行这些脚本，也可以一个个手动运行：

1. `patch/build.sh`：patch.dll，电脑上改写程序集的工具。
2. `hooks/build.sh`：PortHooks.dll。这时还没有准备好的 `build/game`，就对着游戏文件夹里的原始程序集编译（只看程序集名和公开类型，结果相同）。
3. `host/stage.sh build`：从 `RIMWORLD_DIR` 准备 `build/game`，用 patch.dll 改写游戏的程序集和 Harmony；第一次时还会生成 Unity 程序集名的类型转发门面（名单在 `host/forwarders.txt`），并下载转换社区排序规则（`host/modrules/ModRules.txt`）。
4. `mobile/build.sh`：PortMobile.dll（对着改写过的 `build/game` 编译）。
5. `workshop/build.sh`：从 NuGet 包解出创意工坊下载器的库，编 PortWorkshop.dll。
6. `setup/build.sh`：PortSetup.dll，手机上处理玩家导入的游戏。
7. `host/corelib.sh`：给运行时包的 System.Private.CoreLib 加上 Ludeon 运行时分支的兼容行为，再用同一版本的 crossgen2 编成 ReadyToRun。
8. `native/build.sh`：引擎的原生库。第一次时先从游戏的 imgui.dll 和 Brutal.ImGui.dll 取 ImGui 扁平接口要的名单和结构体大小（`build/scan`、`ref/`），并调用 `native/slang/build-official.cmd` 编 Slang（Visual Studio 编代码生成器，NDK r27c 编安卓库）。
9. `PAYLOAD=port host/build.sh`：宿主、桩库、Java 层，加上运行时、上面这些和许可文本，打包签名。

只改了一部分代码时，重新运行对应的那一步和最后一步即可。

### 5. 装到手机上、导入游戏

手机要求：64 位 ARM（arm64-v8a）、Android 8.0 及以上、支持 Vulkan 的 GPU，导入时约 3 GB 空闲空间。

1. 安装：把 `build/port17-tester.apk` 拷到手机上点开安装（允许安装未知来源的应用）；或者打开手机的 USB 调试，用 `"$DEPS_DIR/android-sdk/platform-tools/adb.exe" install -r build/port17-tester.apk`。别人的密钥签名的 Port17 已经装着时，先卸载它（卸载会删除它的数据，先备份存档）。
2. 把电脑上的游戏文件夹（或者打成的 zip）拷到手机上，比如“下载”文件夹。
3. 第一次启动时选中这个 zip 或文件夹：应用把它复制进自己的目录，在手机上改写（PortSetup），然后进入主菜单。自己拥有的 DLC 一起导入。
4. mod：在 Mod 页用“导入Mod…”导入电脑上的 mod 文件夹或 zip，或者在游戏内的创意工坊窗口用自己的 Steam 账号下载。

### 6. 可复现性：哪些文件相同，哪些不同

用同一版本的游戏（核对时是 1.7.4995）和同一份 Harmony，下面这些与开发机上的构建逐字节相同：PortHooks.dll、PortMobile.dll、PortWorkshop.dll、PortSetup.dll、patch.dll、改过的 CoreLib（IL 版和 ReadyToRun 版）、创意工坊下载器的库、classes.dex，以及除 Slang 以外的全部原生库（libvma、libstb、libgli、libimgui、libimgui_freetype、libmonomodc、libmm_exhelper、libwaylithaudio、libportastc、libKtx、libslang 代理、libporthost、libglfw、libvulkan、libportvideo、libportuser32、libsteam_api，以及改过的 libcoreclr）。安装包里 `payload.zip` 解开后的每个文件都相同，规则行（`setup/version.txt`，PortSetup 和 PortHooks 的散列）也就相同。

不同的有：

- **Slang 的三个库**：Slang 构建时把核心模块序列化后嵌进 libslang-compiler 和 libslang-glsl-module，里面记着构建目录的绝对路径；换个文件夹构建（哪怕路径一样长），压缩后的模块数据和排在它后面的内容就都不同。libslang-glslang 只差 12 个字节：build ID 和 `.gnu_debuglink` 的校验和，它们是从记着构建路径的调试信息算出来的。源码、补丁和 NDK r27c 都相同。
- **签名**：每个人用自己的密钥。
- **`ModRules.txt`**：构建时下载的社区排序规则是当时的最新版本（写这份说明时规则库从 2026-09-12 起没变过，文件也相同）。
- **安装包里的时间戳**：`assets/payload.txt` 记着打包时间，zip 条目带着文件时间。
- **许可文本**：开发机的工作副本里有些许可文本是 CRLF 换行，克隆出来的是 LF。
- **游戏版本不同时**：PortMobile 里记着构建时的游戏版本，类型转发门面按当时的 UnityStubs 生成。
- **`build/forwarders` 里的类型转发门面**：只放进开发用脚本推到手机的 `build/game`，不进安装包（测试者的手机导入时自己生成）。每次生成，PE 时间戳和 MVID 都不同。

### 7. 环境变量

都在 `host/env.sh` 里解析。没设的，先找 `DEPS_DIR` 里 `host/fetch-deps.sh` 取来的，再找开发机上的位置。

| 变量 | 默认 | 内容 |
|---|---|---|
| `DEPS_DIR` | `downloads`（仓库里） | `host/fetch-deps.sh` 放依赖的地方 |
| `RIMWORLD_DIR` | `D:/SteamLibrary/steamapps/common/RimWorld` | 自己的游戏 |
| `WORKSHOP_DIR` | `D:/SteamLibrary/steamapps/workshop/content/294100` | 创意工坊文件夹 |
| `HARMONY_DLL` | `$WORKSHOP_DIR/2009463077/1.7/Assemblies/0Harmony.dll` | Harmony |
| `PORT17_KS_PASS` | （无） | 签名密钥库的密码 |
| `ANDROID_NDK` | `$DEPS_DIR/android-ndk-r23b` | NDK r23b |
| `SLANG_NDK` | `$DEPS_DIR/android-ndk-r27c` | NDK r27c |
| `ANDROID_SDK` | `$DEPS_DIR/android-sdk` | build-tools 34.0.0、platforms/android-34、platform-tools |
| `JDK` | `$DEPS_DIR/jdk-11.0.14.1+1` | JDK（不看 `JAVA_HOME`） |
| `DOTNET_RUNTIME_PACK` | `$DEPS_DIR/dotnet-android-coreclr` | 解开的安卓运行时包 |
| `CROSSGEN2_DIR` | `$DEPS_DIR/crossgen2/pkg` | 解开的 crossgen2 包 |
| `ROSLYN_DIR`、`CECIL_DIR`、`ILSPY_DIR` | `$DEPS_DIR/roslyn/pkg` 等 | 解开的 NuGet 包 |
| `NET8` | PATH 上 dotnet 旁边最新的 8.0.x | patch.dll 编译时引用的 .NET 8 程序集 |
| `VS_DIR` | Community 的默认位置，否则用 vswhere 找 | Visual Studio 2022 |
| `ADB` | `$ANDROID_SDK/platform-tools/adb.exe` | 推送、调试脚本用的 adb |

### 8. 常见问题

- **`missing: ...`**：脚本缺的东西和该做的事都写在这一行里：运行 `host/fetch-deps.sh`、设对应的变量，或者先运行前面的步骤。
- **`SHA-256 ..., expected ...`**：下载的文件和构建用的不是同一个。GitHub 自动生成的标签压缩包在极少数情况下会变；这时请开 issue，不要直接改校验值。
- **`not the Android NDK r23b`**：环境里的 `ANDROID_NDK` 指向别的 NDK。取消它（`unset ANDROID_NDK`）或指向 r23b。
- **`set PORT17_KS_PASS ...`**：签名需要密码，见第 4 节。忘了密码的话，只能删掉 `port17.keystore` 重新生成，手机上要先卸载旧版本才能装新的。
- **Slang 构建失败**：看 `native/slang/build-official.cmd` 的输出。常见原因是 Visual Studio 缺 C++ 工具、Windows SDK 或 CMake 组件，没装 Python 3，或者 `DEPS_DIR` 路径太长。修好后再运行 `native/build.sh slang`；要从头来，删掉 `$DEPS_DIR/src/slang-2026.19` 里的 `build`、`build-android-arm64-v8a` 和 `generators` 文件夹。
- **社区排序规则下载失败**（没网或 GitHub 不可达）：`host/stage.sh` 停在 `node host/modrules/build.js`。联网后重试；或者把别处取得的 `communityRules.json` 放到 `host/modrules/` 下，运行 `node host/modrules/build.js --offline`。
- **杀毒软件**：解压 NDK 时实时扫描会让它慢很多；可以把 `DEPS_DIR` 和仓库加入排除项。
- **只想构建开发用的宿主安装包或推到自己的手机**：`host/build.sh`（不带 `PAYLOAD`）和 `host/stage.sh` 见脚本开头的说明；它们需要 adb 和一台装好 Port17 的手机。

---

## English

These steps start from a clean Windows PC and build the tester's APK, `build/port17-tester.apk`. The APK contains no game files: on the first start the player imports their own copy of RimWorld 1.7.

Every tool and third-party source the build uses is fixed at the version the port is developed with. `host/fetch-deps.sh` downloads each from its original publisher (Google's Android repository, Eclipse Adoptium, nuget.org, the projects' GitHub tags) and checks its SHA-256. With the same game and the same Harmony, the build produces PortHooks, PortMobile, PortWorkshop, PortSetup, patch.dll, the patched CoreLib and the native libraries byte for byte as on the development machine; the exceptions are listed in section 6.

### 1. Install first

| | |
|---|---|
| Windows 10 or 11 (x64) | All scripts run in Git Bash. |
| [Git for Windows](https://git-scm.com/download/win) | Brings Git Bash, curl, unzip, tar, patch, sha256sum and cygpath. The repository's `.gitattributes` fixes the line endings; leave `core.autocrlf` as it is (tested with 2.55). |
| [Node.js](https://nodejs.org/) 18 or later | A few small tool scripts (tested with 24.21). |
| [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) | `dotnet` on the PATH; the .NET 8 SDK works as well. The C# compiler, patch.dll and ILSpy run on it, and patch.dll is compiled against its assemblies (the references record only the assembly names and version 8.0.0.0, so another 8.0.x should give the same files; tested with 8.0.19). |
| [Visual Studio 2022](https://visualstudio.microsoft.com/vs/) (Community, Professional, Enterprise or Build Tools) | Only for Slang's code generators. From the "Desktop development with C++" workload: MSVC v143, a Windows 10/11 SDK and "C++ CMake tools for Windows" (CMake and Ninja). Point `VS_DIR` at it when it is installed elsewhere. Tested with 17.14 (MSVC 19.44, CMake 3.31). |
| [Python 3](https://www.python.org/downloads/windows/) | Only for Slang's build: the SPIRV-Tools it bundles generates its grammar tables with Python (CMake finds it; tested with 3.14). Use the python.org installer, not the Microsoft Store stub. |

Android Studio and Unity are not needed.

- **Disk**: `DEPS_DIR` holds about 13 GB after the build: 2.3 GB of downloaded archives (`DEPS_DIR/archives`, which may be deleted) and about 4 GB of Slang's build folders among them; the build output is about 1.5 GB (`build/game` is a copy of the game). Give `DEPS_DIR` a short path without spaces (Slang's source tree is deep).
- **Network**: about 2.5 GB of downloads. The build itself goes online twice: `host/stage.sh` downloads RimSort's community load-order rules (`host/modrules/build.js`), and Slang's CMake downloads a few prebuilt packages for its Windows tests (none of them goes into the APK).
- **Time**: on the development machine (24 logical processors, downloads at about 50 MB/s) about 6 minutes to fetch and about 9 minutes to build, most of it Slang; with fewer processors Slang alone takes half an hour or more. Later builds take a few minutes (Slang's build and the ImGui scan are kept).

### 2. The two things you bring

- **Your own RimWorld 1.7** (bought on Steam; 1.7 is currently Steam's "unstable" beta branch). The build compiles against the game's assemblies and stages `build/game` from it; no game file goes into the APK. The reference build used 1.7.4995 rev309. PortMobile records the game version it was built for and reminds the player at the main menu when theirs differs.
- **The Harmony mod's 1.7 `0Harmony.dll`**: subscribe to Harmony (2009463077) in the Workshop; the file is `steamapps/workshop/content/294100/2009463077/1.7/Assemblies/0Harmony.dll`.

Point the build at them in Git Bash (`/c/...` and `C:/...` both work):

```bash
export RIMWORLD_DIR="/c/Program Files (x86)/Steam/steamapps/common/RimWorld"
export WORKSHOP_DIR="/c/Program Files (x86)/Steam/steamapps/workshop/content/294100"   # Harmony is below it
# or name the file itself: export HARMONY_DLL=".../2009463077/1.7/Assemblies/0Harmony.dll"
```

### 3. Fetch the dependencies

```bash
git clone https://github.com/samilry/rimworld-android-port.git port17
cd port17
export DEPS_DIR=/d/port17-deps     # unset: downloads/ inside the repository
host/fetch-deps.sh
```

`host/fetch-deps.sh` can be run again at any time: what is completely unpacked is skipped (marks in `DEPS_DIR/.fetched`), so after an interruption just start it again. It also takes names, e.g. `host/fetch-deps.sh sdk jdk`. It fetches:

| Name | Version | Source | Used for | Download |
|---|---|---|---|---|
| `ndk` | Android NDK r23b (23.1.7779620) | dl.google.com | all native code but Slang (the same files as Unity 2022.3's NDK) | 789 MB |
| `slang-ndk` | Android NDK r27c (27.2.12479018) | dl.google.com | Slang (needs C++20) | 782 MB |
| `sdk` | build-tools 34.0.0, platform android-34 r2, platform-tools 37.0.1 | dl.google.com | aapt2, d8, zipalign, apksigner, android.jar; adb | 58 + 63 + 8 MB |
| `jdk` | Eclipse Temurin 11.0.14.1+1 | GitHub (adoptium) | javac, jar, keytool, and java for d8 and apksigner (the same JDK as Unity 2022.3's) | 197 MB |
| `dotnet` | Microsoft.NETCore.App.Runtime.android-arm64 10.0.12; Microsoft.NETCore.App.Crossgen2.win-x64 10.0.12 | nuget.org | the phone's CoreCLR runtime; compiling the patched CoreLib to ReadyToRun | 190 + 12 MB |
| `roslyn` | Microsoft.Net.Compilers.Toolset 4.11.0 | nuget.org | the C# compiler, csc.dll | 22 MB |
| `cecil` | Mono.Cecil 0.11.6 | nuget.org | assembly rewriting in patch.dll, PortSetup and PortHooks | 0.7 MB |
| `ilspy` | ilspycmd 9.1.0.7988 | nuget.org | decompiles the game's Brutal.ImGui for the ImGui struct sizes to check | 4 MB |
| `nuget` | SteamKit2 3.4.0, protobuf-net 3.2.56, protobuf-net.Core 3.2.56, ZstdSharp.Port 0.8.7, System.IO.Hashing 10.0.12, QRCoder 1.8.1, System.Runtime.Serialization.Formatters 10.0.0 | nuget.org | the Workshop downloader's libraries; the BinaryFormatter implementation the PC game ships | 10 MB |
| `sources` | Vulkan-Headers 1.3.296, VulkanMemoryAllocator 3.3.0, gli (commit 3542f88), glm 1.0.1, Dear ImGui 1.92.2-docking, FreeType 2.13.3, astc-encoder 5.7.0, miniaudio 0.11.22 (with stb_vorbis), stb_image 2.30 / stb_image_write 1.16 (stb commit 013ac3b) | GitHub | the upstream sources of the engine's native libraries | 66 MB |
| `ktx` | KTX-Software 4.4.0 | GitHub | libKtx (only the folders the library is built from are unpacked) | 213 MB |
| `slang` | Slang 2026.19 (commit 0fb6b75, with submodules) | GitHub (git clone) | the PC game's shader compiler | about 400 MB cloned |
| `emoji` | Noto Emoji 3.002 (google/fonts commit b979dba) | GitHub | outline emoji on the phone | 2 MB |

### 4. Build

```bash
read -rsp "Signing keystore password: " PORT17_KS_PASS; echo; export PORT17_KS_PASS
host/build-all.sh
```

The result is `build/port17-tester.apk` (about 56 MB). The first build creates `port17.keystore` in the repository's root (a self-signed RSA 2048 key, CN=port17) with `PORT17_KS_PASS` as its password. Later versions must be signed with the same keystore and password to install over the earlier ones: keep both safe, never commit them and never hand them out.

`host/build-all.sh` runs these scripts in the order they depend on each other; they can also be run one by one:

1. `patch/build.sh`: patch.dll, the PC-side assembly rewriter.
2. `hooks/build.sh`: PortHooks.dll. With nothing staged yet it compiles against the game folder's original assemblies (only their names and public types matter, so the result is the same).
3. `host/stage.sh build`: stages `build/game` from `RIMWORLD_DIR` and rewrites the game's assemblies and Harmony with patch.dll; the first time it also writes the type-forwarding facades for the Unity assembly names (listed in `host/forwarders.txt`) and downloads and converts the community load-order rules (`host/modrules/ModRules.txt`).
4. `mobile/build.sh`: PortMobile.dll (compiled against the rewritten `build/game`).
5. `workshop/build.sh`: unpacks the Workshop downloader's libraries from their NuGet packages and compiles PortWorkshop.dll.
6. `setup/build.sh`: PortSetup.dll, which prepares the player's imported game on the phone.
7. `host/corelib.sh`: gives the runtime pack's System.Private.CoreLib the behaviour of Ludeon's runtime fork and compiles it to ReadyToRun with the crossgen2 of the same build.
8. `native/build.sh`: the engine's native libraries. The first time it takes the names and struct sizes of the ImGui flat API from the game's imgui.dll and Brutal.ImGui.dll (`build/scan`, `ref/`) and builds Slang with `native/slang/build-official.cmd` (Visual Studio for the code generators, NDK r27c for the Android libraries).
9. `PAYLOAD=port host/build.sh`: the host, the stubs and the Java side, together with the runtime, all of the above and the license texts, packed and signed.

After changing part of the code, run that step again and then the last one.

### 5. Install on a phone and import the game

The phone needs 64-bit ARM (arm64-v8a), Android 8.0 or later, a Vulkan GPU, and about 3 GB of free storage for the import.

1. Install: copy `build/port17-tester.apk` to the phone and open it (allow installing apps from unknown sources), or with USB debugging on: `"$DEPS_DIR/android-sdk/platform-tools/adb.exe" install -r build/port17-tester.apk`. If a Port17 signed with someone else's key is installed, uninstall it first (that deletes its data: back up the saves).
2. Copy your game folder (or a zip of it) from the PC to the phone, e.g. into Download.
3. On the first start, pick that zip or folder: the app copies it into its own storage, rewrites it on the phone (PortSetup) and opens the main menu. The DLCs you own come along.
4. Mods: import PC mod folders or zips with "Import mods..." on the mod page, or download them in the in-game Workshop window with your own Steam account.

### 6. Reproducibility: what is identical, what differs

With the same game version (checked with 1.7.4995) and the same Harmony, these are byte for byte the development machine's: PortHooks.dll, PortMobile.dll, PortWorkshop.dll, PortSetup.dll, patch.dll, the patched CoreLib (IL and ReadyToRun), the Workshop downloader's libraries, classes.dex, and every native library but Slang's (libvma, libstb, libgli, libimgui, libimgui_freetype, libmonomodc, libmm_exhelper, libwaylithaudio, libportastc, libKtx, the libslang proxy, libporthost, libglfw, libvulkan, libportvideo, libportuser32, libsteam_api, and the patched libcoreclr). Every file in the APK's `payload.zip` is the same, so the rules line (`setup/version.txt`, a hash of PortSetup and PortHooks) is the same as well.

What differs:

- **Slang's three libraries**: Slang's build serializes its core module, with the absolute path of the build folder in it, and embeds it in libslang-compiler and libslang-glsl-module; built in another folder (even one whose path is as long), the compressed module data and everything laid out after it differ. libslang-glslang differs in 12 bytes only: its build ID and the `.gnu_debuglink` checksum, computed from debug information that records the build paths. The sources, the patch and NDK r27c are the same.
- **The signature**: everyone signs with their own key.
- **`ModRules.txt`**: the community load-order rules are the newest at build time (when this was written, the database had not changed since 2026-09-12 and the file was the same).
- **Time stamps in the APK**: `assets/payload.txt` holds the packing time, and the zip entries carry file times.
- **License texts**: some of them have CRLF line ends in the development machine's working copy; a clone has LF.
- **With another game version**: PortMobile records the game version it was built for, and the type-forwarding facades follow that version's UnityStubs.
- **The type-forwarding facades in `build/forwarders`**: they go only into the `build/game` the development scripts push to a phone, not into the APK (a tester's phone makes its own at the import). Every making gives them a new PE time stamp and MVID.

### 7. Environment variables

All of them are resolved in `host/env.sh`. Unset, a place is what `host/fetch-deps.sh` fetched into `DEPS_DIR`, else where the development machine keeps it.

| Variable | Default | What |
|---|---|---|
| `DEPS_DIR` | `downloads` (in the repository) | where `host/fetch-deps.sh` puts the dependencies |
| `RIMWORLD_DIR` | `D:/SteamLibrary/steamapps/common/RimWorld` | your game |
| `WORKSHOP_DIR` | `D:/SteamLibrary/steamapps/workshop/content/294100` | Steam's Workshop folder of RimWorld |
| `HARMONY_DLL` | `$WORKSHOP_DIR/2009463077/1.7/Assemblies/0Harmony.dll` | Harmony |
| `PORT17_KS_PASS` | (none) | the signing keystore's password |
| `ANDROID_NDK` | `$DEPS_DIR/android-ndk-r23b` | NDK r23b |
| `SLANG_NDK` | `$DEPS_DIR/android-ndk-r27c` | NDK r27c |
| `ANDROID_SDK` | `$DEPS_DIR/android-sdk` | build-tools 34.0.0, platforms/android-34, platform-tools |
| `JDK` | `$DEPS_DIR/jdk-11.0.14.1+1` | the JDK (`JAVA_HOME` is not read) |
| `DOTNET_RUNTIME_PACK` | `$DEPS_DIR/dotnet-android-coreclr` | the unpacked Android runtime pack |
| `CROSSGEN2_DIR` | `$DEPS_DIR/crossgen2/pkg` | the unpacked crossgen2 package |
| `ROSLYN_DIR`, `CECIL_DIR`, `ILSPY_DIR` | `$DEPS_DIR/roslyn/pkg` and so on | the unpacked NuGet packages |
| `NET8` | the newest 8.0.x next to the `dotnet` on the PATH | the .NET 8 assemblies patch.dll is compiled against |
| `VS_DIR` | the Community edition's default place, else what vswhere finds | Visual Studio 2022 |
| `ADB` | `$ANDROID_SDK/platform-tools/adb.exe` | adb for the staging and debugging scripts |

### 8. Troubleshooting

- **`missing: ...`**: the line names what a script lacks and what to do: run `host/fetch-deps.sh`, set the variable, or run an earlier step first.
- **`SHA-256 ..., expected ...`**: the download is not the file the port is built with. GitHub's generated tag archives change in rare cases; please open an issue rather than editing the checksum.
- **`not the Android NDK r23b`**: an `ANDROID_NDK` in your environment points at another NDK. Unset it (`unset ANDROID_NDK`) or point it at r23b.
- **`set PORT17_KS_PASS ...`**: signing needs the password, see section 4. A forgotten password means deleting `port17.keystore` and making a new one; the phone then needs the old version uninstalled before it takes the new one.
- **Slang fails to build**: read the output of `native/slang/build-official.cmd`. Usual causes are Visual Studio without the C++ tools, the Windows SDK or the CMake component, no Python 3, or a too long `DEPS_DIR`. Once fixed, run `native/build.sh slang`; to start over, delete the `build`, `build-android-arm64-v8a` and `generators` folders in `$DEPS_DIR/src/slang-2026.19`.
- **The community load-order rules cannot be downloaded** (offline, GitHub unreachable): `host/stage.sh` stops at `node host/modrules/build.js`. Retry when online, or put a `communityRules.json` obtained elsewhere into `host/modrules/` and run `node host/modrules/build.js --offline`.
- **Antivirus**: real-time scanning makes unpacking the NDKs much slower; consider excluding `DEPS_DIR` and the repository.
- **The development host APK and staging to your own phone**: `host/build.sh` without `PAYLOAD` and `host/stage.sh` are described at the top of the scripts; they need adb and a phone with Port17 installed.

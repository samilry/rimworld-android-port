# Port17 技术文档：RimWorld 1.7 安卓移植

> 非官方玩家项目，与 Ludeon Studios 无关，也未经其认可。文中出现的游戏类名和方法名，只用来说明移植层挂在哪里；仓库里没有任何游戏代码、素材或二进制文件。

英文概要见 [OVERVIEW.en.md](OVERVIEW.en.md)，项目说明见 [README](../README.md)。

**目录**

1. 背景、目标与约束
2. 总体架构
3. 难点一：在标准运行时上模拟 WaylithClr
4. 难点二：电脑版 mod 原样可用
5. 难点三：不改全局缩放的触控与界面适配
6. 难点四：GPU 差异
7. 难点五：着色器编译器
8. 难点六：音频对等
9. 难点七：性能
10. 难点八：安装包不含游戏——导入、处理与诊断
11. 跟进游戏更新
12. 测试方法
13. 成果数字
14. 经验
15. 开发者与开发方式

---

## 1. 背景、目标与约束

### 1.1 RimWorld 1.7 是什么样的程序

1.7 把游戏从 Unity 搬到了 Ludeon 自研的 Waylith 引擎。它是一个纯 .NET 10 程序：

- `RimWorld.dll` 是启动器，`Assembly-CSharp.dll` 是游戏逻辑，`Waylith.dll` 是引擎，`Brutal.*` 是一组原生库的封装；
- 渲染用 Vulkan 1.3 + VMA，开窗和输入用 GLFW，着色器在运行时用 Slang 编译，界面后端是 ImGui + FreeType，音频是 miniaudio 外面一层 `waylithaudio` 原生库；
- 贴图是 BC 压缩格式：资源包里是 BC7/BC4 的 DDS，其余贴图在加载时编成 BC7；
- 给 mod 留了一个 Unity 兼容层 `UnityStubs.dll`，mod 仍然是 Mono 时代按 Unity 程序集名编出来的，用 Harmony 打补丁；
- 只有 Windows、Linux、macOS 三套平台代码。

电脑上，它跑在 Ludeon 自己修改的 CoreCLR（"WaylithClr"，版本 10.0.13-waylithclr）上。游戏的 `RimWorld.runtimeconfig.json` 开着一组兼容开关（1.7.4990 是 20 个），专门放过 Mono 时代 mod 的各种写法。

### 1.2 目标

在安卓手机上玩 1.7，并且**从电脑拿来的 mod 原样能用，随时可以增减**。任何把性能或兼容性绑死在某一份 mod 列表上、或者 mod 一变就要在电脑上重新编译的方案，都不在考虑之内。

### 1.3 约束

1. **不分发游戏**：给玩家的安装包里没有任何游戏文件，玩家导入自己购买的正版。
2. **mod 文件不改**：电脑上的文件原样拿来；需要改的地方，手机上另存一份准备好的副本。
3. **标准运行时**：手机上只有微软发布的 .NET 10 CoreCLR for Android（runtime pack 10.0.12），没有 Ludeon 的开关。差异要么补进 CoreLib 和 `libcoreclr.so`，要么在程序集层面仿出来。
4. **界面不乱动**：不做全局自动缩放，倍率固定（默认 1.85），只缩小放不下的那个窗口；尽量不碰 mod 自己画的布局。
5. **和电脑一致**：遇到差异先查电脑上的确切行为，照着做，而不是发明手机独有的行为。每一项改动都能在 `game/mobile.txt` 里单独关掉。

### 1.4 为什么是这条路线

此前的 1.6 移植版（同一位负责人早先的项目，第 9.5 节拿它做对比）面对的是 Unity + Mono：安卓上只能走 IL2CPP 预编译 + 解释器执行 mod，再自己重新实现 Harmony。1.7 本身就是一个 .NET 程序，工作于是变成四件事：一个安卓宿主，一层平台代码，引擎原生库的安卓版，以及弥补电脑运行时和标准运行时之间的差异。

第一步是在电脑上验证这条路：把游戏目录复制一份，把运行时文件换成微软的 .NET 10（185 个文件），游戏能完整启动进主循环。2026-10-13 一天之内，手机上依次做到了无头启动、主菜单出画面、触控和 Harmony 跑通，晚上负责人当时的 92 个 mod 列表能零报错开新档。之后三周是兼容性、界面、GPU、性能、测试者反馈和游戏更新，到 11-03 共 460 次提交（本仓库的历史里是其中 300 多次，其余只改了不公开的开发记录和测试指南）。

---

## 2. 总体架构

```
APK（不含任何游戏文件）
├─ Java 层：MainActivity（NativeActivity 子类）、TextInputView（输入法）、
│          ImportService / SetupService / GameService（前台服务）、
│          Diagnostics / ReportSummary（崩溃报告）、FilesProvider（DocumentsProvider）
├─ libporthost.so（host.c）
│    └─ dlopen libcoreclr.so → coreclr_initialize（TPA、STARTUP_HOOKS=PortHooks.dll）
│                             → coreclr_execute_assembly(RimWorld.dll)
└─ 原生库
     ├─ 平台：libglfw.so（GLFW 垫片）、libvulkan.so（Vulkan 拦截层）、
     │        libsteam_api64.so / libsteam_api.so（桩）、libportuser32.so、libportvideo.so
     ├─ 引擎库的安卓版：libwaylithaudio.so、libslang*.so、libvma.so、libimgui*.so、
     │        libKtx.so、libgli.so、libstb.so
     └─ 辅助：libportastc.so（astcenc）、libmonomodc.so、libmm_exhelper.so

外部存储/runtime   .NET 类库 + 补过的 System.Private.CoreLib.dll
外部存储/game      玩家的游戏（改写过的程序集、Assets.zip、Data/）
                   + PortHooks.dll、PortMobile.dll、改写过的 0Harmony.dll
                   + 76 个 Unity 程序集名的类型转发门面 + Mods/ + workshop/
```

启动顺序：

1. `MainActivity` 启动，`host.c` 在运行时线程上检查游戏是否准备好；没有就经 JNI 调 `prepareGame` 让玩家导入，再在 `:setup` 进程里跑 PortSetup（第 10 节）。
2. `host.c` 设好运行时的环境变量（第 3、9 节），`coreclr_initialize`，`STARTUP_HOOKS` 指向 PortHooks。
3. `PortHooks.Initialize` 在游戏的 `Main` 之前运行：程序集解析、显存预算、贴图/字体/代理/替身等补丁。
4. 游戏启动，引擎经 GLFW 垫片拿到窗口和输入，经 Vulkan 拦截层拿到设备。
5. 读 mod：每个 mod 的第一个程序集加载之前，ModPrep 检查戳记，必要时现场准备。
6. 主菜单出现（`UIRoot_Entry.Init`）时 PortMobile 启动：触控、界面适配、内联保护等。

### 2.1 APK 宿主

- **`MainActivity`** 是 `NativeActivity` 的子类：沉浸式全屏，铺进摄像头挖孔区，保持亮屏；文件选择器、软键盘、输入设备列表、剪贴板、挖孔位置、重启等 Java 侧能力经 JNI 暴露给 GLFW 垫片。
- **`host.c`**：`android_native_app_glue` 的线程只处理活动的事件和输入，另起一个 16 MB 栈的线程作为游戏的主线程，在上面 `dlopen` 运行时，信任程序集列表（TPA）由运行时和游戏目录的程序集组成，然后执行 `RimWorld.dll`。`game/runtime.env` 和 `game/runtime.properties` 可以加环境变量和运行时属性，方便做实验。
- **运行时包选对**：NuGet 上的 `Microsoft.NETCore.App.Runtime.linux-bionic-arm64` 其实是 Mono（`libcoreclr.so` 只是 Mono 的宿主外壳）；真正的 CoreCLR 在 `android-arm64` 包里。
- **加密库**：安卓版 .NET 的加密库通过 JNI 调 Java，必须由 Java 的 `System.load` 加载（`Boot.java`）。处理进程里一度漏了这一步，Cecil 一写程序集就段错误。
- **黑屏的根因**：安卓的堆指针带 0xb4 标签，Adreno 的 `VkDescriptorSet` 句柄当成 int64 是负数，被 ImGui 渲染器当成"材质绘制索引"，所有界面绘制都被跳过。清单里设 `allowNativeHeapPointerTagging="false"`。
- **重启与退出**：mod 页"保存并应用"要重启进程，用 `Intent.makeRestartActivityTask` 加 `killProcess`；退出用 `finishAndRemoveTask`，再注册一个最先执行的 `_exit(0)`，绕开静态析构阶段的崩溃。

### 2.2 PortHooks：启动钩子

CoreCLR 的 `STARTUP_HOOKS` 在游戏 `Main` 之前运行。`hooks/StartupHook.cs` 的入口（节选）：

```csharp
public static void Initialize()
{
    // ...
    AssemblyLoadContext.Default.Resolving += Resolve;
    PretendProcessPath();
    LimitGraphicsMemory();
    DropAtlasCacheIfAsked();
    MoveBackslashNames();
    MakeHomeFolders();
    // Before the first patch: the order in which Harmony's replacements are put in, for PortMobile's InlineGuard.
    PortHooks.ReplacementCalls.Watch(new HarmonyLib.Harmony("port17.replacements"));
    // A mod that takes every owner's patches off leaves the port's, which are engine code on the PC.
    PortHooks.PortPatches.Keep(new HarmonyLib.Harmony("port17.keeppatches"));
    PortHooks.Textures.Start();
    PortHooks.UiDescriptors.Start();
    PortHooks.AndroidFonts.Start();
    PortHooks.WindowsLibraries.Start();
    // The phone's proxy for web requests, as the PC's runtime follows Windows' proxy settings.
    PortHooks.SystemProxy.Start();
    PortHooks.MissingAssemblies.Start(new HarmonyLib.Harmony("port17.missingassemblies"));
    // ...
}
```

职责：程序集解析（Unity 程序集名、同名和新版本的绑定）、伪造进程路径（安卓上 `Environment.ProcessPath` 是 `/system/bin/app_process64`，引擎靠它找游戏目录）、显存预算、mod 准备的入口（`ModPrepHost`）、Harmony 发射前的栈修补（`HarmonyCompat`）、贴图（BC 解码、ASTC）、字体、系统代理、缺失程序集替身、启动监视线程、加载失败的记录。

原则：**要在游戏加载过程中起作用的，必须放在 PortHooks**。PortMobile 到主菜单才启动，那时 def 和贴图早就查完了。

### 2.3 PortMobile：手机层

由构建时插入 `UIRoot_Entry.Init` 开头的一个调用加载，只启动一次（回主菜单时再被调到，只安排一次内存整理）。全部用 Harmony 补丁实现：触控手势、点按辅助、列表滑动、窗口缩小、按键盘和鼠标键、挖孔避让、帧率与调度、内存整理、冷贴图回收、运行记录、测试代理，以及 mod 导入、排序、存档导入导出、mod 更新提示等。每一项在 `mobile.txt` 里有开关。

### 2.4 patch.dll：构建期改写

电脑上跑的 Cecil 改写器；手机上的 PortSetup 编进同一份代码。

- **平台检查**：`OperatingSystem.IsLinux()` 和 `RuntimeInformation.IsOSPlatform` 改调 `PortPlatform`（安卓当 Linux，引擎没有安卓分支，否则抛 "Unsupported platform"）；POSIX 信号注册和 `Console.CancelKeyPress` 变成空操作。
- **大小写不敏感的路径解析**：引擎逐级列出祖先目录来解析路径，而安卓应用列不出 `Android/data` 这类目录，结果整个贴图文件夹找不到。只在这一个方法里，把列目录的调用换成能用已知路径编出列表的版本；修后贴图数和电脑一致。
- **其他游戏程序集**：字体路径；主菜单不弹"Steam 初始化失败"；ModLister 跳过游戏无权读的 mod 文件夹（文件管理器拷进来的文件属于文件管理器）。
- **0Harmony.dll**：MonoMod 见到 `/system/build.prop` 就当成"没有实现的 Android"，改写这个字面量后按 Linux 走；对 glibc 的 P/Invoke 改到 `libmonomodc.so`；原生异常辅助库改为加载随包的 `libmm_exhelper.so`；写完代码刷新指令缓存；Harmony 的按名查字段、Emitter 的操作码、发射前的栈修补（第 3 节）。
- **其他模式**：`--corelib`（CoreLib 补丁）、`--forwarders`（类型转发门面）、`--mods` / `--mods-import`（mod 准备）、`--check`（列出 mod 引用了、而游戏、运行时和其他 mod 都没有的成员）。

### 2.5 ModPrep：手机上的 mod 准备

`patch/ModPrep.cs` 一份源码编进两处：电脑上的 patch.dll 和手机上的 PortHooks。手机在两个时机准备 mod：导入时，以及游戏加载某个 mod 的第一个程序集之前（兜底）。所以 mod 不管是导入的、文件管理器拷进来的还是 adb 推的，都能用。规则见第 4 节。

### 2.6 PortSetup：玩家导入自己的游戏

测试者安装包带着运行时和移植层，没有游戏。PortSetup 在手机上做电脑上 `host/stage.sh` 做的事：检查是不是 1.7，把数据移进游戏目录，原始程序集留一份，改写程序集，生成类型转发门面。详见第 10 节。

### 2.7 创意工坊下载器

`workshop/`（PortWorkshop.dll）基于 SteamKit2，由 PortMobile 在第一次打开时装进**单独的加载上下文**，mod 自带的同名库（比如别的版本的 protobuf-net）不受影响。

- 登录：用另一台设备上的 Steam App 扫二维码，或者玩家自己输入账号密码（只用于这一次）；只保存 Steam 发的"记住登录"令牌，放在应用私有目录。
- 下载：和 Steam 客户端一样从内容服务器取清单和文件块，并行下载、解密、解压，然后交给现有的导入流程。国内的合作 CDN 要按主机名申请 CDN 授权令牌。
- 合集展开成里面的 mod，必需的前置 mod 一起列出；mod 文件夹里记下物品号和清单号，用来检查更新。主菜单每天最多检查一次已装 mod 的更新（"N 个 mod 有更新"），不会自己下载。
- 游戏内的 Steam 链接和创意工坊网页链接会打开这个窗口。

### 2.8 原生库

| 库 | 来源 | 说明 |
|---|---|---|
| `libglfw.so` | 自写 | GLFW 3.4 中 Vulkan 程序用到的部分，建在 NativeActivity 上；回调都在轮询线程上调用，和真 GLFW 一样。原始触摸交给 PortMobile，由它决定注入什么鼠标事件 |
| `libvulkan.so` | 自写 | Vulkan 拦截层，见第 6 节 |
| `libwaylithaudio.so` | 自写，基于 miniaudio 0.11.22 + stb_vorbis | 引擎音频库的 59 个 `wa_*` 导出，见第 8 节 |
| `libslang-compiler.so` 等 | Slang 2026.19 源码，NDK r27c | 见第 7 节；`libslang.so` 是没有代码的代理 |
| `libimgui.so`、`libimgui_freetype.so` | ImGui 1.92.2 docking、FreeType 2.13.3 | 扁平 C 接口由脚本按 clang 的 AST 生成（一千多个导出），要 `-mms-bitfields` 才和 C# 的结构布局一致；和电脑一样关掉断言；两处补丁 |
| `libvma.so`、`libgli.so`、`libstb.so`、`libKtx.so` | VMA 3.3.0、gli/glm、stb、KTX-Software 4.4.0 | 引擎按名字绑定的库；KTX 只编只读部分，外加同名导出的包装 |
| `libportastc.so` | Arm astcenc 5.7.0（只开 NEON） | Mali 上的 ASTC 编码 |
| `libmonomodc.so`、`libmm_exhelper.so` | 自写 | MonoMod 在 bionic 上要的 libc/libdl 函数（errno、sysconf 编号、没有 /tmp）；按原版行为重写的原生异常辅助库（静态链接 LLVM libunwind） |
| `libsteam_api64.so` / `libsteam_api.so` | 自写桩 | 手机上没有 Steam 客户端；两个名字分别对应 Windows 版和 Linux/Mac 版游戏里的 Steamworks.NET |
| `libportuser32.so`、`libportvideo.so` | 自写 | mod 直接调用的 Windows 剪贴板和 MessageBox；MP4 用 MediaCodec 解码（电脑上是 Media Foundation） |

---

## 3. 难点一：在标准运行时上模拟 WaylithClr

### 3.1 问题

第一天在电脑上用标准 .NET 跑游戏，mod 的失败只有两类：Unity 程序集名找不到，以及 mod 自带的旧版 Harmony。看起来标准运行时就够了。这是错觉：之后 Humanoid Alien Races 和 MinifyEverything 在手机上的失败一度被当成"mod 没适配 1.7"，直到发现游戏 runtimeconfig 里的那组开关（同一轮里 Toddlers、[NL] Custom Portraits 的问题也是这一类）。光仿出其中的 `LaxPrivateFieldNames`，92 个 mod 的报错就从 62 类降到 3 类。

**教训：手机上看到只有 1.7 才有的 mod 错误，先对照这张开关表，再下结论。**

### 3.2 怎么知道电脑上的确切行为

- **托管部分**：对照电脑版 `System.Private.CoreLib` 里这些开关的行为，在手机的 CoreLib 里照样做。
- **原生部分**（`coreclr.dll`、`clrjit.dll`）：不启动游戏。把电脑游戏目录里的运行时文件复制到临时目录，`RimWorld.exe` 本身就是一个 dotnet 宿主，用它运行自己写的测试程序集，在 runtimeconfig 里逐个开关对比。`clrjit.dll` 里还有 runtimeconfig 没列出来的 `LaxILStack` 和 `LaxRetInTry`。
- **手机上核对**：`mobile.txt` 的 `corelibTest=1`、`parityTest=1` 等自检开关逐项跑一遍，和电脑的结果比。

### 3.3 开关对照表

| 开关 | 电脑上的作用 | 手机上的做法 |
|---|---|---|
| `AssemblyNameRedirects` | `UnityEngine.*` 等程序集名指到 UnityStubs | 76 个类型转发门面程序集（标准运行时不接受 `Resolving` 返回另一个名字的程序集，类型转发是标准的说法）；Prepatcher 那几个名字不重定向（见字段注入） |
| `LaxAssemblyBinding` | 引用的版本和已加载的不同也能绑定 | `Resolving` 按简单名返回已加载的，没加载的按名字加载 |
| `LaxAssemblyArchitecture` | 放过标成 x64 或 x86 专用的纯 IL 程序集 | 改 PE 机器类型、清掉 32BITREQUIRED（游戏、mod、资源里内嵌的、从字节加载的都改） |
| `SkipAccessChecks` | 不检查访问权限 | ModPrep 注入 `[IgnoresAccessChecksTo]`；1.7.4995 起游戏程序集之间也按需注入 |
| `AllowInitOnlyStaticWrites` | 反射可以写 `static readonly` 字段 | CoreLib 里的检查 + `libcoreclr.so` 的二进制补丁；游戏和 mod 的这类字段也去掉了 initonly |
| `LaxCallvirt` | 对静态方法的 callvirt 当 call | 在 Harmony 的 Emitter 入口改写操作码 |
| `LaxTypeRefResolution` | 写错程序集的框架类型引用按名字找到 | ModPrep 规则 9：指向真正定义或转发它的程序集 |
| `LaxMemberSignatures` | Mono 上返回 void、.NET 上有返回值的框架方法 | ModPrep 规则 9：调用后补 pop |
| `LaxPrivateFieldNames` | 按 Mono 的私有字段名（`name`）找到 .NET 的（`_name`、`m_name`） | IL 里的引用：ModPrep；反射：CoreLib 的 `GetField` |
| `LaxInheritedFieldRefs` | 字段挪到父类后，旧的字段引用仍有效 | ModPrep 规则 13 |
| `LaxInterfaceDispatch` | 接口分发上的宽松处理 | 未模拟：在电脑上实测，缺接口或抽象方法实现时同样抛 TypeLoadException，还没遇到需要它的 mod |
| `LaxDynamicMethodAttributes` | `DynamicMethod` 接受任意特性和调用约定 | CoreLib |
| `DynamicMethodOwnerIsDeclaringType` | 动态方法的 `DeclaringType` 是它所属的类型 | CoreLib |
| `IsolatedLoadsResolveByName` | `Load(byte[])` / `LoadFile` 加载的程序集能被按名字引用 | CoreLib |
| `MissingAssemblyTypeLoadException` | 缺程序集时抛 TypeLoadException，而不是 FileNotFoundException | 同名同版本的空替身程序集，放在单独的加载上下文里 |
| `MonoStringHashCode` | `string.GetHashCode()` 用 Mono 的固定算法 | CoreLib（没有昵称的小人显示名字还是姓氏，就由字符串哈希决定；标准运行时的哈希每次启动随机，显示名会跟着变） |
| `MonoFloatFormatPrecision` | 浮点数默认格式按 Mono 的有效位数（float 7 位、double 15 位） | CoreLib |
| `CatchableAccessViolations` | 托管代码里的访问违例是普通异常 | `DOTNET_legacyCorruptedStateExceptionsPolicy=1`（标准运行时仍然认） |
| `JitNoInlinePatchableIntoDynamicMethods` + `JitPatchableAssemblies` | 不把 Assembly-CSharp 的方法内联进 Harmony 的替换方法 | InlineGuard（3.7） |
| `FieldInjection`（4995 起，另有一项预留参数） | 运行时给已加载的类加实例字段，供 Prepatcher 层使用 | 占位类型，行为和电脑上关掉这个功能时一样 |
| JIT 的 `LaxILStack`、`LaxRetInTry` | 汇合点栈深不一致、try 里的 ret | HarmonyCompat（3.6），只作用于 Harmony 的替换方法 |

### 3.4 CoreLib 补丁

`host/corelib.sh`：先用 `patch.dll --corelib` 改运行时包里的 `System.Private.CoreLib.dll`（Cecil），再用**同一个运行时版本**的 crossgen2 重新预编译成 ReadyToRun（版本不一致脚本会拒绝），约 5 秒。启动时间没有变化。内容：

- 上表里属于 CoreLib 的开关：Mono 字符串哈希、浮点格式、`DynamicMethod` 的两项、按名字解析隔离加载、私有字段名、只读静态字段写入。第一批四个开关补完后，`corelibTest=1` 的九项检查全部和电脑一致（之前有六项不同）。
- Ludeon 的 CoreLib 多出来的公开成员：`X86Base.ReadTimeStampCounter`（引擎的帧分析器把它内联了，缺了它，打开帧分析器每帧抛异常直到游戏退出）、1.7.4995 起分析器时钟用的 `ArmBase.Arm64.ReadVirtualCounter`（这里返回 `Stopwatch.GetTimestamp`）、`AppDomain.DefineDynamicAssembly`、`AssemblyBuilder.DefineDynamicModule(name, fileName)`、Mono 的 `JitHelpers.UnsafeEnumCast`。
- 每次游戏或运行时更新后，比较两份 CoreLib 的公开成员，找新加的东西。

### 3.5 libcoreclr.so 的两处二进制补丁

运行时用的是微软发布的预编译库（去符号），不自己重新编译运行时，只在 APK 打包时改几个字节。`host/patch-coreclr.js`（节选）：

```js
const patches = [
  {
    name: 'affinity patch',
    at: 0x56a3bc,
    original: Buffer.from([0xc0, 0xf9, 0xff, 0x34]), // cbz w0, 0x56a2f4
    patched: Buffer.from([0xce, 0xff, 0xff, 0x17]),  // b 0x56a2f4
    done: 'a refused sched_setaffinity no longer fails a thread\'s start',
  },
  {
    name: 'initonly patch',
    at: 0x455254,
    original: Buffer.from([0x08, 0x2d, 0x28, 0x37]), // tbnz w8, #5, 0x4557f4
    patched: Buffer.from([0x1f, 0x20, 0x03, 0xd5]),  // nop
    done: 'reflection writes static readonly fields as on the PC (AllowInitOnlyStaticWrites)',
  },
];
```

- **线程亲和性**：一位测试者的手机上运行时每次都启动失败（0x8007054F）。顺着导出的 `coreclr_initialize` 在去符号的库里一路跟到线程入口：这个版本的 PAL 在每个新线程上把亲和性设成进程的，设置失败就判线程启动失败（上游新版本会放过 EPERM/EACCES）。推断是那台手机的系统把应用主线程绑在部分核心上，这一步被拒绝。补丁让失败时照常继续，和上游新版本一样；宿主在启动运行时之前还会用一个探测线程照做一遍，把结果写进日志，留作证据。
- **只读静态字段**：反射写字段的第一次和结构体类型字段的每一次都经过 `InvokeUtil::SetValidField` 里的检查，CoreLib 里改不到，在这里把那条跳转改成 nop。
- 补丁前先核对原字节；换了运行时版本，脚本报错停下，需要重新找位置。

### 3.6 HarmonyCompat：转译器留下的不平衡栈

Mono 的 JIT 不比较控制流汇合处的栈深度，为 Mono 写的转译器常常在跳转处多留值，多年来照样能用：MinifyEverything 把条件跳转改成无条件跳转，栈上留下两个值；HAR 给一个调用包循环，留下了返回值。CoreCLR 判整个方法"非法程序"，如果补丁是在 mod 的构造函数里打的，整个 mod 都起不来。

做法（`hooks/HarmonyCompat.cs`）：Harmony 发射替换方法之前（构建时在它的 `EmitCodes` 开头插入调用），沿所有路径跟踪栈深度：

- 无条件跳转或落入跳转目标时，比该处最少的深度多出来的值，前面补 pop；try 块开始前、void 方法返回前同样处理；
- 返回值下面压着的多余值：先把返回值存进局部变量，弹掉其余，再放回去；
- 从 try 块或 catch 里跳出去的 `br`（Harmony 把转译器的 `ret` 变成这种跳转）改成 `leave`；
- 本来就平衡的指令表原样不动；跟踪不了的（calli、变参）或修不了的（条件跳转带多余值）也不动，补完再复核一遍，不平衡就放弃。

"带着值跳出 try" 这种形状，在电脑上用同一个 Harmony 实测了 21 种写法：11 种能跑，10 种被拒，取决于代码布局而不是一条可以照抄的规则，所以不仿。当时 92 个 mod 里只有两个方法触发了修补，没有误伤。

### 3.7 InlineGuard：JIT 内联绕过补丁

**问题**：电脑上的运行时从不把 Assembly-CSharp 的方法内联进 Harmony 的替换方法；标准 JIT 会。于是先编好的替换方法里内联了一个小方法，之后再给这个小方法打的补丁，在那里就被跳过。实际遇到的两例：

- 一个 mod（Universal Turret Tweaker）启动时给 `MapInterfaceUpdate` 挂了后缀，替换方法内联了 `DesignatorManagerUpdate`；移植层在主菜单才给后者打补丁，结果拖动工具标记的格子不画；
- Geological Landforms 启动时编的替换方法里内联了 `Rand.PopState`，Map Preview 生成预览时才给 `Rand` 打补丁，结果是 "Stack empty"。

**做法**（`mobile/InlineGuard.cs`）：挂在 Harmony 的 `PatchFunctions.UpdateWrapper` 上（所有补丁都经过它）。一个方法**第一次**被打补丁时，找出在它之前编好的、可能内联了它的替换方法，让 Harmony 按原样重新生成；新编的代码里被补丁的方法就不会再被内联。"可能内联了什么"的模型是用 .NET 10.0.12 的内联事件实测出来的：

- 看替换方法最终的指令表（原方法转译后的、前缀、后缀、finalizer 都会被内联进去），递归最多 20 层；
- 一般方法 128 字节 IL 以内；替换方法是一段直线代码、调用很少时放宽到 1024 字节；标了 AggressiveInlining 的不限；
- 带 catch 的、NoInlining 的、同步的方法不会被内联；**带 try/finally 的会**（.NET 10 的 JIT）。第一版把所有带异常子句的方法都当成不内联，漏掉了 `GUI.ProcessPendingWindows`，列表滑动因此失灵，复核时改正。

还有两点：引擎每帧调用的方法在加载画面期间就以全优化编好了，早于移植层，所以移植层打完自己的补丁后，引擎里调用这些小方法的方法也重建一次；PortHooks 记录每个替换方法的生成顺序，只重建"在目标第一次被补丁之前生成"的那些。

**结果**：在电脑的标准 .NET 10.0.12 上带游戏和负责人 130 个 mod 实际补丁的 1049 个方法测量，旧做法漏掉 22 处内联副本，新做法 0 处；全部重建要 835 次、2.6–2.9 s，按先后只重建 381 次、1.3–1.5 s。手机上这份列表的启动代价是 1.8 s。

相关的测量工具 `hooks/InlineWatch.cs` 用 `EventListener` 收运行时的内联事件，到主菜单时统计"打补丁之前已经被内联进普通编译代码"的地方：默认 36–37 处；如果去掉分层编译的调用计数延迟，会变成 89 处。这个延迟让启动阶段停在不内联的第 0 层，正好覆盖 mod 打补丁的时段，所以保留默认值。

### 3.8 访问检查

mod 普遍用"publicize"过的游戏程序集编译，IL 里直接访问私有成员，Mono 和 Ludeon 的运行时都放过，标准运行时抛 FieldAccessException。ModPrep 给 mod 加上 `[assembly: IgnoresAccessChecksTo("...")]`：

- 对游戏程序集和 mod 引用的每个程序集；特性类型按运行时的要求定义在 mod 自己的程序集里；
- 名字要写**定义**那个成员的程序集，例如读 `List<T>._items` 要写 `System.Private.CoreLib`，而不是 mod 引用的门面 `System.Runtime`。

1.7.4995 的游戏程序集自己也需要它：新加的 Prepatcher 兼容层实现了 Harmony 内部打包的 Cecil 里的一个 internal 接口，电脑靠 `SkipAccessChecks` 放过。现在 patch.dll 和 PortSetup 给"用到别的程序集非公开类型或成员"的游戏程序集也加这个特性，是通用规则，4995 里只命中这一处。

### 3.9 其余几项

- **缺失程序集替身**（`hooks/MissingAssemblies.cs`）：别的办法都解析不了、又不是显式加载时，造一个同名同版本的空程序集，类型加载失败的信息和电脑一字不差。替身放在单独的加载上下文里：第一版放在默认上下文，后来真正的同名程序集就加载不进来了。替身从已加载程序集列表里藏起来；`AppDomain.GetAssemblies` 也要单独打补丁，因为 CoreLib 是 ReadyToRun 预编译的，它内联了那个小包装方法，补丁要到分层编译重编之后才生效。
- **可捕获的访问违例**：一个 1.6 版 mod 在设置窗口里用错误的引用调用 `List.Add`，电脑上窗口记一条错误，手机上进程直接结束。标准运行时仍然认 `legacyCorruptedStateExceptionsPolicy`。
- **线程栈**：游戏和 mod 不指定大小新建的线程，在安卓上只有 bionic 的 1 MB（电脑上是 RimWorld.exe 的 1.5 MB，Linux 上 8 MB），一位测试者读档时加载线程栈溢出。变量名必须是 `Thread_DefaultStackSize`，只写 `DefaultStackSize` 运行时不读。主线程另设为 16 MB。

```c
/* host/host.c（节选，省略了各行之间的注释） */
setenv("DOTNET_GCgen0size", "0x2000000", 1);
setenv("DOTNET_legacyCorruptedStateExceptionsPolicy", "1", 1);
setenv("DOTNET_Thread_DefaultStackSize", "0x1000000", 1);
```

（第一行见第 9 节。）

- **字段注入**（1.7.4995）：启动器每次启动都会用到运行时新加的字段注入。手机上提供占位类型，报告"不支持"，`Commit` 抛出和电脑关掉此功能时相同的异常。负责人的 139 个 mod 里只有 VEF 用到真字段注入，而它自带备用写法。真正实现要改 `libcoreclr`，量级是几个月，等真有不带备用写法的 mod 再说。

### 3.10 结果

- 92 个 mod 的列表从"开不了新档"到启动零报错；此后的报错按"电脑上会不会一样"逐条分类，移植造成的修，mod 自身的如实告诉用户。
- 1.7.4990 的 20 个开关里，19 个有了对应，`LaxInterfaceDispatch` 实测没有找到差别、未模拟；4995 的字段注入按"功能关闭"处理。
- 同一套自检在电脑和手机上逐项结果一致。

---

## 4. 难点二：电脑版 mod 原样可用

### 4.1 ModPrep 的规则

ModPrep 对 mod 文件夹下的每个程序集（`.dll` 和 RimUnity 的 `.rimunity`）做一组改写，写一个戳记文件 `.port17-prepared`，记下规则版本和每个程序集的路径、大小。规则版本一变，下次启动所有 mod 重新准备一遍（几秒到一分钟）；戳记里直接列文件，启动时的检查从遍历 5 万个文件的 753 ms 降到 130 ms。`patch/ModPrep.cs` 里每个程序集的处理顺序：

```csharp
int edits = AddAccessCheckExemptions(assembly);
// First: the rules below look types up, and a type pointed at its assembly resolves for them.
edits += RetargetMissingFrameworkTypes(module, resolver, label, log);
edits += AdaptFrameworkReturnTypes(module, label, log);
edits += MatchFrameworkFieldNames(module, label, log);
edits += MatchInheritedFields(module, label, log);
// ... (a loop over all types: static fields lose initonly, Mono's Array.UnsafeLoad/UnsafeStore become ldelem/stelem)
edits += DropAttributesOfMissingTypes(module, label, log);
edits += TreatAndroidAsLinux(module, label, log);
edits += ReadBackslashesAsSeparators(module, label, log);
edits += AdaptChangedSignatures(module, label, log);
edits += BridgeChangedOverrides(module, label, log);
return edits + PrepareEmbeddedAssemblies(module, resolver, label, result, log);
```

| 规则版本 | 加了什么 | 起因 |
|---|---|---|
| 1–5 | PE 机器类型、访问检查豁免、去掉 initonly、Mono 的 `Array.UnsafeLoad/UnsafeStore` 改成 `ldelem/stelem`、资源里内嵌的程序集同样处理、Lunar Framework 的文件校验值重算 | 第一天的 92 个 mod |
| 6 | 删掉类型不存在的特性 | Mono 跳过这种特性；标准运行时在每次特性查询时抛异常，游戏扫描 `[DefOf]` 时也抛，结果所有 mod 的加载到此为止 |
| 7 | 按旧签名调用游戏方法的地方，按"重新编译会得到的样子"改成新签名（补默认值、转换返回值） | 1.7 给一些方法加了可选参数、改了返回类型；14 个种族 mod 里 324 处 |
| 8 | 按旧返回类型重写游戏虚方法的类，补一个按新返回类型的重写 | 否则 mod 的重写永远不被调用 |
| 9 | `LaxTypeRefResolution`、`LaxMemberSignatures`、`LaxPrivateFieldNames` 的 IL 部分 | 一个存储 mod 的箱子每帧抛异常，后面的东西都不画 |
| 10 | 为 1.6 编的 mod 引用"在 0Harmony 里"的框架类型时改指 | 测试者的报错 |
| 11 | mod 自己的平台检查当成 Linux（4.2） | 测试者的报错 |
| 12 | Mono 的 `JitHelpers` 指向手机 CoreLib 里补上的同名类型 | 一个存储 mod |
| 13 | 字段挪到父类后的旧引用（`LaxInheritedFieldRefs`） | 电脑上实测确认的开关行为 |
| 14 | 反斜杠路径（4.3） | PC 对照审计 |

此外：

- **符号**：读写时带上 PDB（同目录的和内嵌的），手机上 mod 的堆栈也有"文件:行号"。
- **校验和**：每个程序集记下准备前后的 CRC-32；联机 mod 按 CRC 比对双方的 mod 文件，手机对仍是准备后样子的文件回答原文件的校验和，手机和电脑才能用同一套 mod 联机。
- **内存**：一位测试者导入 107 个 mod 时占了 4.5 GB（解析器把所有读过的程序集一直留着）。每个 mod 准备完就释放：85 个 mod 的批量准备从 4.5 GB、56 s 降到几乎不占内存、39 s，输出逐字节相同。

### 4.2 Linux 分支

游戏的平台检查改写成"安卓当 Linux"之后，引擎的 Linux 分支就在手机上跑。由此带来的一串问题：

- **mod 自己的平台检查**：一个 mod 按 `IsOSPlatform` 选资源包的平台文件夹，在手机上什么都没选到，它的着色器和后面依赖它的东西全部失败。规则 11 把 mod 里的 `IsOSPlatform(p)` 改成 `p == OSPlatform.Linux`，不引用移植层。
- **只有 glibc 有的函数**：引擎的内存报告调 `mallinfo2`，换成 bionic 的 `mallinfo`；芯片名从系统属性取（arm64 的 `/proc/cpuinfo` 没有型号）。
- **Steam 库名**：Linux/Mac 版游戏里的 Steamworks.NET 找的是 `steam_api`，Windows 版找 `steam_api64`。三位测试者导入的是 Linux/Mac 版的游戏，每次启动两到五秒就自己退出（找不到库时游戏调用 `Application.Quit`）。桩库现在两个名字都带。
- **路径**：HugsLib 在 Linux 上去 `/tmp` 找日志；Harmony 的 `FileLog.Reset` 删桌面上的日志文件，手机上没有桌面目录就抛异常，一个 mod 的静态构造因此失败。启动时在 HOME 里建齐 Linux 桌面的用户文件夹。

### 4.3 反斜杠路径

Windows 上 `"\Sounds\Songs"` 这样的路径能用，Linux 和安卓上反斜杠是文件名的一部分。规则 14：在调用 `System.IO` 的 mod 方法里，只由反斜杠连接的名字组成的字符串字面量改成正斜杠。对 732 个 mod 程序集扫描，命中 11 处，全是真实路径（一个点唱机 mod 找不到歌曲、界面 mod 的预设目录、一个导出功能写出的文件名带反斜杠）。已经用反斜杠名字建出来的文件和文件夹，启动时挪进对应的嵌套目录。

### 4.4 程序集的各种形态

- 标成 AMD64 的纯 IL 程序集，arm64 运行时拒绝加载；标成 x86 专用（PE32 + 32BITREQUIRED）的同样拒绝，比如一个义体 mod 的 DLL。改机器类型、清标志；从字节和流加载的也一样处理。
- 一个 mod 自带的加载器从资源里取出真正的程序集再 `Assembly.Load(byte[])`，没经过文件准备，每帧抛 FieldAccessException，mod 页详情区一片空白。ModPrep 递归处理资源里内嵌的托管程序集。
- RimUnity 的 `.rimunity` 文件像 DLL 一样准备，并写进戳记。

### 4.5 同名程序集与"自包含"布局

- 电脑运行时上，`Assembly.LoadFrom` 一个文件，如果同名程序集已经加载（哪怕文件名、版本都不同），返回已加载的那个；标准运行时抛异常，游戏随即跳过那个 mod 的其余程序集。一个 mod 的 `Assemblies/backup/` 里放着旧版本的副本，就是这样出的事。按电脑的行为处理。
- PurePatcher（1.7 版的 Prepatcher）要求电脑那种"自包含"的游戏目录：它列出游戏目录里的全部 DLL 交给 Cecil，接管加载 mod 的 `LoadFrom` 调用，并通过游戏的重启机制重新启动。移植层把运行时的 172 个程序集复制进游戏目录，对 mod 程序集的处理改成 `Assembly.LoadFrom` 的前缀（不再改写游戏的调用），重启请求转成活动重启。

### 4.6 其余的电脑对等项

- **全球化**：一开始用不变文化模式（排序按码点、不能规范化、所有文化都是不变文化的数据）。改用系统自带的 ICU，启动多 1.6 s。剩下的差别：安卓的 ICU 数据不带定制规则字符串，.NET 的"忽略大小写"排序会丢掉语言定制（土耳其语、拼音等）。
- **BinaryFormatter**：电脑版带着微软的兼容包（真正的实现），运行时包里的是抛异常的桩。照电脑带上同一个 NuGet 包。
- **剪贴板、链接、文件夹、截图**：手机剪贴板和游戏互通；`Application.OpenURL` 和 `Process.Start` 的 shell 打开交给安卓的应用；mod 直接调 Windows 剪贴板 API 的，由 `libportuser32.so` 接住；F10 截图复制到相册并通知媒体库。
- **其他**：MP4 视频用 MediaCodec 解码；KTX 贴图；Noto Emoji 作为表情后备字体；mod 的网络请求走手机的系统代理；存档导入和导出；系统"文件"应用里能直接看到游戏的数据文件夹和 Mods（DocumentsProvider）；卸载时可以选择保留数据。

### 4.7 结果

- 负责人自己用 130 多个 mod 的列表在手机上游玩。
- 热门界面 mod 冒烟测试（`host/ui-smoke.js` 自动走完主菜单、设置、读档、主标签、建筑菜单、小人信息页、工作台、交易，每站截图和计错）：34 个 mod 分组跑完，除一个 mod 自己缺贴图外没有报错，界面没有错位。
- 一次 PC 对照审计列出 6 个方向 94 条可能的差距，逐条复核后修了能确定的 43 处，需要取舍的交给负责人决定。

---

## 5. 难点三：不改全局缩放的触控与界面适配

### 5.1 原则

- 界面倍率固定，默认 1.85（正好放下建筑菜单）；玩家可以在 0.5 到 4 之间任选，游戏自己的"保留/恢复"确认是安全网。游玩中不自动改倍率。
- 放不下的窗口**自己**缩小，屏幕上别的东西不动。
- 绝大多数改动不改布局，只改"手指落在哪里、算什么操作"，所以对 mod 画的界面同样生效，也不会和 mod 的布局冲突。
- 每项都能在 `mobile.txt` 里关；另有"兼容模式"，只保留触控、去掉所有界面适配，给和界面 mod 冲突的玩家用。

### 5.2 手指到鼠标

GLFW 垫片记录每根手指的原始事件，不再自己合成鼠标；`mobile/Touch.cs` 每帧识别手势，再通过垫片注入光标、按键、滚轮事件，引擎下一帧轮询到。

分类靠**看游戏的反应**：按下立刻交给游戏，两帧后看游戏拿它做了什么：出现选框就是地图，有工具在用就是工具，否则是界面。

| 位置 | 手势 | 含义 |
|---|---|---|
| 地图 | 点 | 左键 |
| 地图 | 拖 | 移动镜头 |
| 地图 | 长按 | 有选中：右键（下令）；没有：开始框选 |
| 地图 | 双指 | 围绕两指中心缩放、平移 |
| 界面 | 点、拖 | 左键 |
| 列表 | 点 | 抬手时才算点击 |
| 列表 | 顺着列表拖 | 只滚动，带惯性，不按任何东西 |
| 列表 | 横着拖、停一下再拖 | 鼠标拖动（滑条、涂一行格子、拖动排序） |
| 窗口 | 双指 | 滚轮和中键拖动（mod 自己的地图和图表靠它缩放平移） |
| 界面 | 长按 | 立刻显示提示框，抬手后保留，不点击 |

### 5.3 小目标

开发机上 1 mm 约等于 9.6 个界面单位（倍率 1.85）。带 106 个 mod 的游戏画面里有 106 个可点控件，短边没有一个到 5 mm：窗口关闭叉 1.9 mm，图标按钮 2.5 mm，筛选树的复选框 2.0 mm。

- **点偏了算最近的控件**（`mobile/TapAssist.cs`）：重绘时把每个可按控件的屏幕矩形记下来（引擎的按钮、开关、文本框、滑条，游戏的可拖按钮，工作格子，殖民者栏；mod 的窗口用同一批调用，一样被记到）。手指没按在任何控件上时，给不到 5.2 mm 的小控件每边补最多 1.8 mm，落在补出的范围里就按到最近的那个；按在大控件（列表的一行）上、离躺在它上面的小控件不到 0.9 mm 时按小的；不会隔着窗口按到别的窗口里。
- **按下的位置抬起**：手指移动不到约 2 mm 之前，鼠标一直停在按下的位置，指尖滚动不会让小按钮的点击落空。
- **三处原版控件变大**（都是按内容自己排版的地方，不挤动别的东西）：浮动菜单的行距用游戏自己给 Steam Deck 的值（一行 3.1 → 4.4 mm）；物品筛选树的行间距；时间控制按钮 32×24 → 44×33 单位。有 mod 替换了原版时间控件时让开（判断时忽略移植层自己的补丁）。

### 5.4 列表滑动

照搬游戏自带的 Steam Deck 触屏滚动逻辑（速度和惯性），去掉"只在 Steam Deck 上"的判断。踩过的坑：

- 被别的控件盖住的列表抢走了点击（资源列表的滚动区延伸到检视面板的标签页下面）；修法是列表只在触摸从它里面开始时才接管。随后又发现 Mod 页在列表之后画了一个覆盖整页的隐形按钮，"盖住"的判断要排除包含整个列表的控件。
- 有的列表在开始滚动视图之前就按滚动位置裁掉看不见的行，惯性滚动如果发生在重绘里，露出的那条就是空白；惯性改为每帧在第一遍里走一次，并按帧时间缩放（否则 120 帧时滚得快一倍）。
- 直接用 `GUI.BeginScrollView` 或开发者窗口自己的滚动视图画的列表，要单独接上。
- 有的列表只在重绘时才计算内容高度，滚动夹紧要用上一次重绘的尺寸。

### 5.5 右键、按键盘和确认按钮

- **右键**：地图上有选中时长按是右键。屏幕上还有两个可以拖动的圆钮"左键""右键"：点一下右键，下一次触摸就是右键；长按右键锁定，直到再点一下左键或右键。这两个键也可以加到按键盘上，和圆钮共用一个状态；在按键盘里切换成右键后，那一圈按键会收起，把地图空出来。
- **按键盘**：屏幕边缘的圆钮展开一圈按键：游戏的快捷键绑定、任意键盘按键或带 Ctrl/Shift/Alt 的组合（按住期间引擎看到的就是真实的按键事件，游戏和 mod 照电脑的方式处理）；Ctrl、Shift、Alt 和鼠标中键是开关；"记录问题"键把当时的截图和状态存进报告，再弹出输入框，让测试者写几句说明。没有键盘的手机上，快捷键绑定对话框换成屏幕上的按键网格。
- **两步确认**：放置建筑、拖动标记、选降落点、开发者模式的单格工具，都改成"先标记，再按确定"。和电脑一致的细节：每次松手分别调用一次指派（一次性交给游戏会让某些 mod 报"键已存在"）。自己读鼠标按键的 mod 工具（扫描它的更新方法里有没有直接读鼠标按键）照常得到按下、拖动、松开，不加确认。
- **双击**：放宽到 0.5 s、36 dp，而且两次都点在同一个东西上才算（否则点地面后快速点旁边的小人会选中所有同类）。

### 5.6 放不下的窗口自己缩小

- **WindowFit**：在 `Window.WindowOnGUI` 前后，给 GUI 矩阵乘一个系数、把窗口矩形换算成缩小后的单位，画完还原；游戏看到的窗口始终是原来的尺寸。引擎在缩小的窗口里换算屏幕坐标时偏移量没有缩放，需要修正；窗口阴影也要跟着缩小后的窗口画。
- **命令栏（GizmoFit）**：按游戏的精确排版规则预测行数（稳定排序、分组、可收缩的命令），选能放下的最大系数（不小于 0.5）；另有"命令图标大小"选项和"行多时自动缩小"。只在完全复刻游戏的排版时预测才准，简化模型猜 2 行时游戏画了 3 行。
- **其他**：装备页在矮屏幕上会随装备多少变尺寸，固定上限；工作表按窗口宽度而不是屏幕宽度定；研究树的滚动区至少和整棵树一样高，变成双向可拖的列表；旋转画的图标在缩放区域里用矩阵的缩放而不是界面倍率。

### 5.7 摄像头挖孔

负责人要的是"画面铺满，但挖孔不挡信息"，并做成可选。被否决的方案：让窗口避开挖孔（留一条黑边，"太难看"）；整个界面平移（游戏和 mod 有十几处用世界坐标换算放地图上的标签，会整体偏开）。

做法：窗口一直铺进挖孔区，Java 侧从系统的 `WindowInsets` 取挖孔的矩形，`mobile/Cutout.cs` 只移动和挖孔重叠的东西：窗口（移开，没地方就收窄）、左上的资源列表、右下那一列（时间、日期、信件、警报）、窗口画在外面的附属部分（建筑菜单的命令网格和说明框、检视面板的标签页）、主菜单的按钮区。窗口在按下事件分发时就已经要在新位置，所以 `WindowStackOnGUI` 之外还要在窗口自己的绘制前缀里再移一次。每帧代价约 8 µs。

### 5.8 文字输入

- `NativeActivity` 没有 InputConnection，输入法提交的文字到不了游戏。`TextInputView` 是一个 1×1 的视图，只在游戏里有文本框获得焦点时才可聚焦（否则安卓会自己弹键盘且收不掉），输入法提交的文字经 JNI 变成字符事件。
- 输入法进程被系统杀掉重启后，同一个视图重新获得焦点时安卓不会重新建立输入连接，中文输入"被吞"；接上实体键盘后，点过文本框，字母就一直被输入法拿走。两处都在适当时机调用 `restartInput` 解决。
- 搜索框只接受手指给的焦点（有的窗口一打开就把焦点给搜索框，键盘随之弹出）；键盘挡住低处的文本框时，屏幕顶部显示正在输入的内容；数字框被手指点中时全选。

### 5.9 键鼠模式

接上鼠标或键盘时，自动换回电脑的操作方式（提示框跟随指针、放置不需要确认、菜单行距恢复、不强制弹软键盘），拔掉后回到触控。外接鼠标立即生效；内置或经投屏工具模拟的设备在第一次使用后生效；键盘在第一次打字母后生效（有的蓝牙外设会报一个假键盘）；设备休眠时模式保持，直到手指触摸屏幕。

### 5.10 引擎层面的几个事实

- 引擎先把鼠标事件交给鼠标下的窗口，然后才运行游戏的 `Root.OnGUI`；只在 `OnGUI` 里拦截，窗口里的控件照样会触发。引擎按钮自己记着"按下"状态，清 hotControl 取消不了它。
- `GUI.Window` 只是带着当时的矩阵把窗口排进队列，到帧末才画；任何改 GUI 矩阵的地方，都要连带处理在它里面请求的即时窗口。
- 控件 id 每一遍从固定值开始数，重新打开的窗口里字段 id 相同；不能跨焦点变化保存控件 id。
- `Window.WindowOnGUI` 会把窗口矩形取整，挪窗口时要挪整数个单位。
- 引擎画贴图时把起点和尺寸分别取整；在实际倍率（横 1.8505、纵 1.8488）下，九宫格底图相邻两块会错开一个像素，按钮里出现一条黑线。只在画九宫格底图时改成两个角各自取整。

### 5.11 结果

触控层的这些改动全部打开和全部关闭，三倍速帧率相同（关 48–54 帧，开 48–53 帧，358 TPS 不变）。阈值（5.2 / 1.8 / 0.9 mm、0.45 s、2 mm）是按尺寸定的初值，之后按负责人和测试者的手感逐轮调整。

---

## 6. 难点四：GPU 差异

### 6.1 Vulkan 拦截层

`host/vulkan/vulkan_interpose.c` 编成 `libvulkan.so` 放在 APK 的库目录里，引擎和移植层自己的库拿到的都是它；它通过 `vkGetInstanceProcAddr` 把调用转给系统的加载器，只在少数地方插手：

| 问题 | 处理 |
|---|---|
| 引擎按表面的当前旋转建交换链、却用窗口的宽高，安卓上画面错；安卓对每次获取和呈现都回答"次优"，引擎就每帧重建交换链 | 旋转报成 identity（由合成器旋转），"次优"当成功 |
| 非一致的主机可见内存需要手动刷新，引擎（按桌面 GPU 写的）从不刷新 | 隐藏这类内存类型 |
| 描述符池在 Adreno 上建池时就按每套约 23 KiB 占显存 | 大池按十六分之一建，满了再加（6.2） |
| 老驱动没有 `VK_EXT_debug_utils`，引擎却无条件调用调试标签函数，在渲染线程上跳到地址 0 | 缺的调试扩展从实例创建里去掉，函数换成什么都不做的替身；Adreno 7xx 的性能建议消息（每次运行约 2000 条）过滤掉 |
| 切到后台再回来黑屏：交换链到表面的表没有删掉销毁的项，句柄被复用 | 在销毁交换链时更新表。用 adb 连续 12 次切后台再回来、按截图平均亮度判断：修前 11/12 黑屏，修后 0/13 |
| 引擎重建表面时先建新的再销毁旧的，同一个窗口被占用（NATIVE_WINDOW_IN_USE） | 先释放旧表面再建 |
| 部分 Mali 驱动做不了 D32_SFLOAT_S8_UINT 深度 | 换成 D24_UNORM_S8_UINT |
| 引擎把贴图的图像句柄直接放进复制和缩放命令，贴图已被放掉时是空句柄（Adreno 驱动里崩溃） | 跳过这类命令和屏障，记下前 20 条 |
| 渲染分辨率选项（原生 / 1080p / 720p） | GLFW 垫片报小一些的窗口尺寸，拦截层报对应的 currentExtent，由安卓缩放显示 |
| 有的手机不让应用读 logcat | 关键行和驱动缺少的函数同时写到 stderr，进崩溃报告 |

还有一个坑：VMA 原来按库名打开 `libvulkan.so`，拿到的是系统加载器，绕过了拦截层；现在按路径打开旁边那个。

### 6.2 显存

开发机厂商的低内存杀手有一条规则：进程的 GPU 内存超过 6 GiB 就当作"内存泄漏"杀掉。手机的 CPU 和 GPU 共用内存，桌面上的很多假设都不成立：

| 问题 | 处理 | 效果 |
|---|---|---|
| 引擎按 Vulkan 堆定显存预算（手机上是大半个内存），回收水位永远到不了 | 设引擎自己的 `WAYLITH_VRAM_BUDGET_MB`：内存的 3/8，最多 4096 | 贴图常驻约 1–1.4 GiB，其余用到时从缓存读 |
| 描述符池按每套约 23 KiB 占显存（5 个材质池各 360 MiB），引擎的统计里看不到；靠 `/proc/<pid>/maps` 里 GPU 映射的大小分布找到 | 拦截层分片建池 | 同一张地图 5421 → 3366 MiB |
| 显存分配器的块要等最后一张贴图走了才还 | 128 KiB 以上的图像独立分配 | 106 个 mod：分配器 3.65 → 2.28 GiB |
| 每张"相机渲染到贴图"都配一张深度缓冲并一直留着，实际只是草稿空间 | 同尺寸共用一张 | 渲染目标 987 → 794 MiB（106 个 mod） |
| 引擎在手机上只放掉从没画过的贴图，画过一次就永远留着（主菜单 170 MiB 的背景图带进游戏） | 10 分钟没画的用引擎自己的办法放掉，再画时从缓存取回（平均 0.7 ms） | 原版场景游戏贴图 739 → 141 MiB |
| 一个 mod 在主菜单一帧内借还 645 张临时渲染贴图（1.3 GB），引擎到下一帧才释放，手机被迫把游戏换出到交换区 | 每帧超过 32 张归还的就提前释放 | 同样的测试，渲染目标多出 802 → 123 MiB |

### 6.3 Mali

- **BC 贴图**：Mali 不能采样 BC 压缩贴图，而 1.7 的贴图全是 BC。第一份 Mali 测试者的日志里，第一张界面图标就分配失败，接着是几百条连锁错误（那次闪退的直接原因是 6.1 里表面重建的问题）。`hooks/Textures.cs` 在引擎配置贴图压缩之后检查设备特性：不支持就关掉加载时的 BC7 编码，并在建贴图时用引擎自带的块解码器把每一级解成同通道的普通格式。在 Adreno 上强制走这条路测试的代价：显存约翻倍（2.0 → 4.15 GB），帧率 120 → 85。
- **ASTC**（`hooks/Astc.cs` + astcenc）：ASTC 4×4 和 BC7 一样是每像素 1 字节。静态图集画好后读回，所有核心并行编码（最快档），按引擎自己的图集缓存键存起来，下次启动直接采用，不再画图集；其他贴图先按 RGBA 上传一次，由一个低优先级的后台线程编码，按数据内容的哈希缓存。启用前先检查格式支持并跑一次编码再解码的自检。缓存按剩余空间定上限，到 90 % 开始清理。在开发机上模拟：第一次启动编 23 页图集（8.6 亿像素）多 11.6 s，之后 55 s 到主菜单，图集 821 MB（RGBA 要约 3.3 GB）。
- **界面描述符池**：引擎每帧给界面的材质绘制只留 512 套描述符，满了就抛异常；Mali 严格执行池的大小，测试者的手机上崩溃。池扩大 8 倍，万一再满就跳过这次绘制。
- **材质绘制的界面图标**：Mali 上画不出来或画成一道，改用普通图标绘制。

### 6.4 Adreno 6xx/7xx

- **图集的某一级丢了**：几台 Adreno 720/725/730 的测试者报告物品在最近的缩放级别消失、树只画了一半。"记录问题"会把选中物所在图集页的每一级读回存图，由此看到某一级是空的或截断的，而开发机做出的同一页是好的。`mobile/AtlasCheck.cs`：启动后把每页读回一次，在 128×64 个点上逐级比较，坏掉的一级在 CPU 上从上一级重建、重新采用、重写缓存；在 Adreno 8xx 以下的 GPU 上，还逐块和缓存文件比对。推断根因是引擎的 GPU BC7 编码器读到了没画完的页，所以在这些 GPU 上改用引擎的 CPU 编码（完整烘焙多约 2 s）。
- **设备丢失**：一台 Adreno 730 在 4990 的 GPU 贴图编码中丢失设备；记下来，7 天内改用 CPU 编码。

### 6.5 结果

开发机是一台骁龙 8 系手机（Adreno）；上面这些问题几乎都来自测试者的 Adreno 6xx–8xx 和多种 Mali 手机的报告。开发机上复现不了的，就做一个开关来模拟那种故障或那条代码路径（`bcDecode=1`、`depth24=1`、`uiPoolTest`、`atlasCheckTest`、`debugutils=0` 等），验证修复之后再发给测试者确认。Mali 上贴图的显存和带宽回到与 BC 相当的水平。

---

## 7. 难点五：着色器编译器

### 7.1 第一版：glslang 替身

游戏自己的着色器都有预编译缓存，只有 mod 带的着色器源码要在运行时编译，而当时 Slang 没有安卓版。第一版是 NDK 自带的 glslang 外面包一层引擎用到的那部分 Slang 接口。关键的一处：Slang 按引擎的要求把块里的矩阵标成行主序，glslang 默认列主序，不处理的话所有变换都是转置的（地图画成一个大红三角）。

### 7.2 为什么换成 Slang 本身

做了一个 372 个着色器的语料（游戏的和几个 mod 的），再写了 36 种写法的对照：电脑的 Slang 能编 34 种，替身只能编 7 种（HLSL 的内置函数和类型、C 风格强转、隐式转换、先调用后定义、参数默认值、typedef、static、没有 `#version`……）。其中"uniform 不写 binding""块外的 uniform"两种，连布局都是 Slang 自己定的，用 glslang 不可能对齐。结论：把 Slang 本身移植过来。

### 7.3 构建

- 源码取 `v2026.19` 标签，构建标签必须是 "2026.19"：引擎的着色器缓存键里有它，对上了游戏自带的预编译缓存才能命中。
- Slang 要 C++20，用 NDK r27c；交叉编译前先在 Windows 上用 Visual Studio 2022 编出它的代码生成器。
- 只编 `slang`、`slang-glslang`、`slang-glsl-module` 三个目标；一个小补丁让它在 API 28 以下不启动子进程（`posix_spawn`）。
- Slang 的 `libslang.so` 本来是指向 `libslang-compiler.so` 的符号链接，APK 里放不了；做一个没有代码、只声明依赖它的 `libslang.so`，引擎对 "slang" 的 P/Invoke 经依赖树落到真正的库里。

### 7.4 内存和缓存

- 一个 Slang 全局会话创建时就占 215 MB 常驻内存，编了几百个后约 256 MB；引擎每个编译槽留一个会话（核心数的一半），而且一直不放。手机上调用引擎自己的接口把并发会话数设成 1，空闲 10 s 后释放会话。
- 替身编出的缓存条目和 Slang 的键相同，第一次启动时清掉一次。

### 7.5 结果

- 语料里电脑能编的 371 个，手机输出的 SPIR-V 与电脑**逐字节相同**；36 种写法的结果相同，其中 34 个 SPIR-V 逐字节相同。
- 运行性能和替身没有差别（主线程和 GPU 每帧的周期数在噪声内）；热启动根本不调用编译器（预热时全部命中缓存）。
- 代价：安装包大了约 15 MB（测试者包 39.2 → 54.6 MB）。

---

## 8. 难点六：音频对等

`native/waylithaudio/waylithaudio.c` 用 miniaudio 0.11.22 和 stb_vorbis 重新实现了引擎的音频库，接口按引擎的 P/Invoke 声明，目前 59 个 `wa_*` 导出。安卓上走 AAudio（OpenSL ES 兜底），48 kHz。

- **结构**：声音分为 PCM、内存或文件里的压缩流、引擎回调的用户流；每个 voice 是一个 `ma_sound`，可挂低通、高通、回声节点；group；3D 的听者和衰减；按引擎时间定时启动；句柄带代数，引擎在 voice 自动释放后还持有句柄也无害。
- **按需解码**：游戏启动时会加载它的全部声音（约六千个，五个半小时），引擎对小于 2 MiB 的要整段 PCM。第一版在加载时全部解码，吃掉 1.7 GB 内存——这是移植层自己的问题，内存调查时用系统的 malloc debug 按调用者统计才找到。现在加载时只读长度（Ogg 的长度直接从首尾页读），播放时短的整段解码进一个 64 MiB 的缓存，长的边播边解。解码一个音效平均 1.3–1.7 ms；原生堆 1475 → 366 MiB。
- **混响**：引擎传的是 Freeverb 风格的参数，自写一个 Freeverb 节点。
- **混音矩阵与 3D**：`wa_voice_set_mix_matrix` 原来是空函数，结果单声道音效比电脑大约 3 dB 而且不分左右。照电脑的库实现：混音矩阵定位声像，3D 声音的方向衰减设为 0、由矩阵按 cos/sin 定位，最小、最大距离按电脑的方式处理。在手机上跑和电脑的库同一套测试，62 个场景里 59 个结果相同，剩下 3 个只差一个游戏不用的声像模式。
- **1.7.4995**：引擎每帧调用新的 `wa_device_recoveries` 和 `wa_device_lost`。照电脑的库加上设备看门狗：设备停了约 1 秒就重启，重启不了就重建；重建不用空后端，失败就每秒重试。

---

## 9. 难点七：性能

### 9.1 怎么量

- **量周期，不量帧率**。开发机发热时帧率会差一成；厂商的稳帧服务有时把目标定在 30 帧并一起降 CPU 和内存总线的频率，同一个场景在不同轮次之间能差出 28 到 42 帧。比较两个版本时，看主线程每帧、每 tick 的处理器周期（simpleperf），交替测量。
- **每一步从同一个存档点开始**。三倍速下三分钟过一天，小人睡着后帧率翻倍，不重读存档的话，后面的步骤比的是另一个场景。`host/sched-exp.sh` 每步让游戏重读存档、等场景就绪后再量。
- **托管代码的调用栈**：simpleperf 自己的报告会丢掉所有托管帧。`host/perf-profile.sh` 让运行时写出方法地址表（perf map），从 `simpleperf dump` 的原始地址自己解析成"程序集!方法"，按代码种类统计。
- **内存**：`dumpsys meminfo` 几乎不算 Vulkan 程序的显存（1.9 GiB 里只算了 136 MB），不能用它比较。`host/mem-report.sh` 按映射种类汇总 smaps，加上 GPU 驱动对这个进程的账。

### 9.2 有效的改动

- **回收器第 0 代预算**：运行时按处理器最大一级缓存定第 0 代的最小预算，在这类手机上找不到缓存大小，退到 256 KiB；游戏每秒分配约 20 MB，于是每秒回收 80 次，十分之一的时间停着（电脑上 16 MB 的三级缓存对应 12 MB 预算，没有这个问题）。设成 32 MiB：暂停占比 9.7 % → 0.4 %，单这一项 27 → 40 帧。
- **并行任务切细**：游戏把每帧的并行预绘制按"线程数 + 1"切块，主线程做完自己那块就等最慢的工作线程。切成每线程 8 小块后主线程可以帮忙，忙碌度 87 % → 94 %。两项合起来：57 个种族殖民者、106 个 mod、三倍速，26 帧 / 310 TPS → 43 帧 / 357 TPS。
- **帧率限制器**：引擎（为 Windows 的睡眠精度）每帧睡整毫秒数，然后空转读时钟 1–2 ms。改成 nanosleep 睡到离帧末 300 µs：稳定 60 帧时主线程周期少 13.5 %，省下的是电和热。
- **内存**（9.1 的工具量出来的构成）：音频按需解码；加载后的整理（压缩大对象堆、清数组池、放掉资源包映射的页、清掉游戏翻译键系统里拽着全部 def XML 的两个列表、放掉没画过的贴图），读档后常驻 2257 → 1074 MiB；字体文件改为映射共享；显存的几项见 6.2。合计：原版百人场景（PSS + 显存）约 5.4 → 2.3 GB，106 个 mod 的场景约 7.4 → 4.4 GB。
- **启动**：mod 戳记检查 753 → 130 ms；InlineGuard 按先后只重建必要的（负责人的列表 4.0 → 1.8 s）。

### 9.3 量过、没有用的

| 尝试 | 结果 |
|---|---|
| 主线程绑到最快的核、工作线程避开小核 | 厂商的游戏服务已经把主线程和渲染线程绑在大核上，无差别 |
| 安卓的性能提示会话（Android 13 的和 Android 16 的新接口）、`ANativeWindow_setFrameRate`、强制 120 Hz、三缓冲 | 稳帧服务照样介入，无差别 |
| 关分层编译 / 关 TieredPGO | 关 PGO 启动快几秒、读档后的热身短，但后期存档每 tick 多约 9 % 周期（1.82–1.93 M 对 2.05–2.09 M）；负责人选了后期速度，保持默认 |
| 多核预编译（`ProfileOptimization`） | 4 次对 5 次启动，差别在波动内（主线程上还在编的多是 Harmony 的替换方法，清单装不了） |
| 游戏程序集预编译成 ReadyToRun | 不做：预编译代码里的内联会让 mod 的补丁悄悄失效；电脑上的游戏程序集也是 IL |
| 更大的第 0 代预算（64–256 MiB）、调第 1 代预算、关 W^X | 帧率和启动时间都没有改善（256 MiB 时每次回收停 35 ms） |
| 帧末按截止时间排帧、植物摇摆参数每帧只写一次 | 无可测效果，撤回 |
| 照电脑设线程优先级 | .NET 在 Linux/安卓上不应用线程优先级；实测 8 核上没有争抢，不会有收益 |

### 9.4 厂商调频是真正的上限

开发机厂商的游戏服务在"均衡"档把最快的核（3.2 GHz）压在 1.67–2.4 GHz；它的稳帧器是一个闭环，有时目标定在 30 帧，把 CPU、DDR、L3、LLCC 一起降到刚好 30 帧（帧时间中位数正好 33 ms，主线程仍然 97 % 忙），加载后先压住、一两分钟后放开，冷启动之间随机。应用侧能做的都量过、无效；剩下的只有游戏助手里的性能档，那是玩家自己的系统设置。

在测过的重场景里，GPU 负载 45–55 %，时钟在最低两档；主线程 89 % 的时间在托管代码里，瓶颈完全在单线程 CPU。一个后期原版存档在超快速下，主线程上 tick 占 35 %、地图绘制 30 %、界面 11.5 %，都是游戏本身的开销；移植层自己约占 0.1–1 %。

### 9.5 和 1.6 移植版对比

同一个原版存档（快速测试殖民地加到 100 个殖民者，1.7 直接读 1.6 的存档），同一个镜头位置，两个版本交替各跑两轮：

| | 1.6 移植版（Unity + IL2CPP + OpenGL ES） | 1.7 移植版（Waylith + CoreCLR + Vulkan） |
|---|---|---|
| 三倍速 | 31–36 FPS，339–353 TPS（主线程 99 % 忙，顶在 2.4 GHz） | 57 FPS，359 TPS（主线程 81 % 忙，1.74 GHz） |
| 主线程每 tick | 5.5–5.8 M 周期 | 约 2.8 M 周期 |
| 主线程每帧（暂停） | 13.4–13.7 M | 7.1–7.2 M |
| 其中界面 | 约 7.1 M | 约 1.8 M |
| 渲染线程每帧 | 约 31 M | 4–5 M |
| 原版启动到主菜单 | 20 s | 16.7 s |
| 读这个存档 | 约 11 s | 约 5 s |

差别主要来自引擎和运行时，不是移植层：渲染后端（GLES 的逐网格绘制调用对比 Vulkan 的排序合批和缓存）、界面后端（IMGUI 每帧多遍），以及代码生成（IL2CPP 的共享泛型和支撑代码不能内联，三倍速时主线程 26 % 在类库和共享泛型里、11 % 在 IL2CPP 自己的支撑代码里；CoreCLR 的分层编译和 PGO 把这些小方法大多内联掉了）。

---

## 10. 难点八：安装包不含游戏——导入、处理与诊断

### 10.1 测试者安装包的流程

`PAYLOAD=port host/build.sh` 打出的安装包（约 55 MB）里有 .NET 运行库（含补过的 CoreLib）、PortHooks、PortMobile、改写过的 Harmony、Mono.Cecil、创意工坊下载器和 PortSetup，没有任何游戏文件。

1. 第一次启动，宿主发现游戏没准备好，经 JNI 调 `prepareGame`：说明对话框（附 RimWorld 用户协议要求 mod 附带的声明和开源许可）→ 系统文件选择器选 zip 或文件夹 → 复制进应用目录（先查 3 GB 空闲空间）。
2. 在复制好的文件里找含 `Version.txt` 和 `Assembly-CSharp.dll` 的目录，启动 `:setup` 进程跑 PortSetup（一个进程里运行时只能启动一次）：检查是不是 1.7，数据移进游戏目录（同一存储内改名，不复制），原始程序集留在 `setup/original`，标记修正、平台改写、生成类型转发门面。开发机上约 8 秒。
3. 处理完宿主接着启动游戏，不需要重启应用。开发机上从选中压缩包到主菜单 44 秒。
4. 以后的安装包规则变了（PortSetup 和 PortHooks 的哈希），用留下的原始程序集自动重做（约十秒），不用重新导入；游戏更新后可以用桌面快捷方式"重新导入游戏"；游戏版本和移植对应的版本不同，主菜单会提醒。

处理过程在电脑上用标准 .NET 10 跑过，66 个游戏程序集和原来电脑流程的输出逐字节相同。

### 10.2 导入时碰到的问题（测试者的手机和开发机）

| 现象 | 原因 | 处理 |
|---|---|---|
| 导入还没完成应用就没了 | 全屏文件选择器和长时间复制让应用进入后台，被系统在一两分钟内杀掉 | 导入和处理期间各开一个前台服务；游戏运行时也有一个 |
| 文件夹导入 12 个 mod 要 13.5 分钟 | 系统的文件接口每个文件来回一次 | 目录单线程走、文件交给 8 个线程复制：17 498 个文件 97 秒 |
| zip 里的中文文件夹名成了乱码 | Windows 中文系统的压缩写 GBK、不带 UTF-8 标志；安卓不报错而是换成替换字符 | 不解码地读名字，逐条判断 UTF-8 还是 GBK |
| 选择器一打开导入就丢了 | 开发者选项"不保留活动"；或者某些厂商的选择器结束了启动它的任务 | 检测前者并说明；后者提供"用 USB 把游戏拷进应用目录"的路 |
| 复制途中外部存储提供者进程死掉，连带杀掉应用 | 普通的提供者客户端会随之结束 | 改用不稳定提供者客户端 |
| 装了 Harmony 后黑屏 | 文件管理器拷进去的文件属于文件管理器，游戏读不了；一个读不了的 `About.xml` 让整个 mod 列表初始化失败 | 跳过读不了的 mod 文件夹，主菜单给出提示 |
| 一启动就退出、没有错误 | Linux/Mac 版游戏的 Steam 库名 | 4.2 |

### 10.3 诊断

测试者大多远程，报告必须自带全部信息。

- **崩溃报告**：下次启动时根据系统的 `ApplicationExitInfo` 生成；原生崩溃附带墓碑，在手机上解码成文本，标出故障地址落在哪个映射、哪些是 JIT 帧、各库的 build id；清单里开着 GWP-ASan。`host/symbolize.sh` 在电脑上按 build id 加函数名和行号。
- **运行时的遗言**：安卓上 .NET 的控制台输出直接进 logcat，栈溢出报告也只写 logcat，而有的手机不让应用读自己的 logcat。宿主用 liblog 的 logger 钩子把运行时的错误抄进 `stderr.txt`，并保留上一次运行的一份。
- **运行记录**（`mobile/Recorder.cs`）：每 10 秒一行快照（状态、帧率和 99 % 帧时间、TPS、各线程 CPU、核心频率和上限、GC、内存、显存、温度），加上事件（加载耗时、存读档、窗口、信件、每种错误的前三条、慢帧、每一次触摸）。"记录问题"键额外存截图和引擎的完整内存报告；之后在输入框里写的说明也存进这次记录；导出日志时，摘要（summary.txt）开头列出手机上保留的最近六次记录的时间和说明，压缩包里带最近三次的截图。
- **启动监视**（`hooks/Watch.cs`）：主菜单之前每 5 秒记内存和游戏日志的最后一行（看崩溃的加载走到了哪里）；之后只在主线程 10 秒没出画面时记一行，并按主线程的 CPU 时间区分"在忙"还是"在等"。
- **报告摘要**：错误按消息去掉数字归类、附第一条非日志帧；一位测试者日志里的 10 670 条错误归成 125 种，第一眼就看到占 6641 条的那一种。另有 mod 清单（加载顺序、是否标了 1.7、DLL 数量）。
- **假崩溃**：游戏自己的重启、被划掉后的自杀、系统清理数据，都会被系统记成异常退出；按导入轨迹和退出列表识别，不再弹崩溃对话框。
- **冲突标记**：一条错误的堆栈同时经过移植层和某个 mod 时，在记录里标出双方。

两个典型案例：一个 mod 列表打开时的闪退是 ImGui 的断言（字体图集槽位的 10 位代数在复用 512 次后读成负数）——电脑上的 ImGui 是不带断言的 release 构建，手机上也改成这样，并打上游的修复补丁；另一个是引擎在多个线程上测量文字，两个线程同时往同一个 FreeType 字形槽里渲染，给字形加载加了一把锁。

---

## 11. 跟进游戏更新

### 11.1 流程

1. 保留旧版本处理好的游戏目录，用 `host/stage.sh build` 处理新版本；
2. 比较两版的程序集（类型、成员、方法体）和运行时，检查移植层按名字反射用到的游戏和引擎成员、GLFW 和 Vulkan 的入口、新加的原生导出；
3. 对着新版本重新编 PortHooks 和 PortMobile；`host/compat-check.sh` 拿新构建去检查保留的旧版本游戏（直接引用，以及按名字的 Harmony 和反射查找），知道哪些旧版本还能用；
4. 先在并排测试包上验证，再上主安装（之前备份存档）。

### 11.2 各版本改了什么

| 版本 | 游戏和引擎的变化 | 移植层的跟进 |
|---|---|---|
| 1.7.4979 | 后备字体改按文件名在系统字体目录里找；`GUI.ProcessPendingWindows` 变成 try/finally 里的两个调用，被引擎的调用者内联 | 字体路径改写；InlineGuard 也重建引擎里的调用者 |
| 1.7.4980 | 界面倍率加了上限；音频库新导出 `wa_sound_resident_bytes` | 去掉上限（手机的显示缩放是 1）；音频库补导出 |
| 1.7.4982 | 官方运行时换到 10.0.13-waylithclr；贴图按需上传；VMA 内存池；新的带重试的分配入口 | 共用深度缓冲多认一个分配入口（否则悄悄失效，显存多几百 MB）；类型转发门面按新 UnityStubs 重新生成 |
| 1.7.4984 | 帧率上限的计算移进帧等待函数 | 移植层整个替换了这个函数，不用改 |
| 1.7.4985 | 帧末先等渲染线程的积压不超过 1 帧 | 在替换的等待函数里照做 |
| 1.7.4990 | 贴图相关的引擎类型换了命名空间；后备字体改按字体族名查找；GPU 崩溃诊断 | `EngineTypes` 按名字绑定，两个版本都支持；`AndroidFonts` 把手机缺的族名映射到 `/system/fonts` 的文件（厂商会改字体的族名，只能按文件映射） |
| 1.7.4995 | 字段注入；音频设备看门狗；分析器时钟读 ARM 计数器；Prepatcher 兼容层用到 Harmony 的内部类型 | 占位类型；音频库补看门狗；CoreLib 加方法；游戏程序集也加访问检查豁免（3.8）。第一次启动重建图集多约 55 s，之后 57.8 s 到主菜单 |

测试者导入的游戏版本不一，所以移植层尽量同时支持相邻的几个版本：不同版本只在第一次导入时提示，从不拒绝；主菜单的版本提醒说明移植是按哪个版本做的；安装包比导入的游戏旧时，提示先更新安装包。

---

## 12. 测试方法

### 12.1 电脑上的对照环境

- 第一步就是在电脑上把游戏的运行时换成标准 .NET 10，看哪些地方依赖 Ludeon 的分支。
- 不启动游戏，用电脑游戏目录里的运行时文件运行自己写的测试程序集（3.2），逐项量 WaylithClr 开关、HarmonyCompat 的各种跳转形状、同名 `LoadFrom`、缺失引擎方法在 JIT 时的行为。
- 着色器语料：电脑侧调用引擎自己的运行时编译接口，手机侧是同样设置的命令行程序，比 SPIR-V。
- PortSetup 在电脑上用标准 .NET 10 跑，和原流程的输出逐字节比较。
- `patch.dll --check` 静态扫描 mod 引用了而不存在的类型和成员（找 Mono 独有的 API、1.7 删掉或改签名的方法）。

### 12.2 手机上的测试安装

- **并排测试包**：`TEST_PACKAGE=<包名> host/build.sh` 换一个包名，和正式安装并排装在同一台手机上，用来测全新安装、导入流程、测试者的 mod 列表，不碰正式安装的存档和设置。
- **复现测试者的环境**：在测试包里放进测试者的 mod、改界面倍率模拟矮屏幕、`cutoutMirror=1` 把挖孔镜像到另一边、`bcDecode=1` 在 Adreno 上走 Mali 的贴图路径、改游戏程序集里的 Steam 库名模拟 Linux 版游戏、在开发者选项里打开"不保留活动"。
- **自检开关**（`mobile.txt`）：`corelibTest`、`parityTest`、`avTest`、`stackTest`、`standInTest`、`clipboardTest`、`videoTest`、`ktxTest`、`inlineGuardTest`、`atlasCheckTest`、`uiPoolTest`、`tempTest`、`stallTest` 等，每个在主菜单或启动时触发一种已知的故障或差异，看修复是否生效。
- **没有硬件也能测键鼠**：用系统的 `hid` 命令造虚拟的 USB 鼠标和键盘。

### 12.3 让 AI 代理在真机上玩

`host/mcp/rimworld-mcp.js` 是一个无依赖的 MCP 服务器，提供 38 个工具：看（截图、引擎知道的窗口、控件和文字及其像素位置、游戏状态、地图上的东西、日志的新行）、动（经 adb 发真实的触摸，测的就是移植的触控层；按文字点击、长按、多指手势、按键、镜头、选择、速度）、以及只给测试存档用的造场景命令（生成物品和小人、研究、事件、关系）。游戏里的另一半是 `mobile/Agent.cs`：只有游戏目录里有开关文件时才在本机回环地址上监听，每个请求要带令牌，命令在主线程的两帧之间执行。Claude 用它像玩家一样走完了俘虏、婚礼、商队、袭击、运输舱、飞船等流程，找出了好几个只在真实操作里才出现的问题。

### 12.4 测量脚本

`host/sched-exp.sh`（重读存档、数周期）、`host/perf-profile.sh`（解析托管帧的采样）、`host/mem-report.sh`（内存按来源拆分）、`host/heap-debug.sh`（借系统的 malloc debug 按调用者统计原生堆，应用不需要可调试）、`host/controls-dump.sh`（当前界面每个可点控件的毫米尺寸）、`host/ui-smoke.js`（界面 mod 冒烟测试）。

---

## 13. 成果数字

| 项目 | 数字 |
|---|---|
| 规模 | 2026-10-13 至 11-03，460 次提交（本仓库的历史里是其中 300 多次，其余只改了不公开的开发记录和测试指南；每天的改动见 [CHANGELOG.md](../CHANGELOG.md)）；约 3.6 万行 C#、7.5 千行 C/C++、6.4 千行 Java、4 千行脚本 |
| 游戏版本 | 跟进 7 次更新（1.7.4976 → 4995） |
| 启动到主菜单 | 原版 16.7 s（1.6 移植版 20 s）；130 多个 mod 约 46–48 s |
| 三倍速 | 原版百人 57 FPS / 359 TPS（帧率上限 60；1.6 移植版 31–36 / 339–353）；负责人带 mod 的中期殖民地 357–360 TPS、110–119 FPS（上限 120）；8.7 年的后期原版存档 360 TPS、约 58 FPS（上限 60） |
| 每 tick 主线程周期 | 1.6 移植版 5.5–5.8 M → 约 2.8 M |
| GC 暂停占比 | 9.7 % → 0.4 % |
| 帧等待 | 稳定 60 帧时主线程周期 −13.5 % |
| 内存（PSS + 显存） | 原版百人约 5.4 → 2.3 GB；106 个 mod 约 7.4 → 4.4 GB |
| 描述符池 | 同一张地图显存 5421 → 3366 MiB |
| 着色器 | 371 个着色器与电脑逐字节相同；36 种写法 34 种 SPIR-V 相同 |
| Mali / ASTC | 图集 821 MB（RGBA 约 3.3 GB） |
| 内联保护 | 漏掉的内联副本 22 → 0；手机启动代价 1.8 s |
| 移植层自身开销 | 主线程每帧的 0.1–1 % |
| 导入 | 从选中游戏压缩包到主菜单 44 s；文件夹复制 13.5 min → 97 s |
| 创意工坊 | 146 个 mod 的合集（2.5 GB）约 17 min，零失败 |
| 界面 mod 冒烟测试 | 34 个热门 mod，没有移植层报错 |
| 切后台黑屏 | 11/12 → 0/13 |

---

## 14. 经验

1. **先量再改，量周期不量帧率。** 温度和厂商调频能让同一个场景的帧率差出一成到五成。直觉也常常不准：植物摇摆参数每个 tick 写一遍，看起来改成每帧写一次就能省下主线程的 4 %，实测几乎不变（4.15 % → 3.96 %），贵的是每帧第一次写入时的缓存未命中，不是写的次数。每一项"有效"和"无效"都留下了数字，免得以后绕回去。
2. **只在 1.7 手机上出现的 mod 错误，先查电脑运行时的开关。** 有两次先把移植层的问题判断成了"mod 没适配 1.7"（Humanoid Alien Races、Map Preview），查清后才更正。
3. **内联是 Harmony 的天敌。** JIT 的内联、分层编译的时机、预编译的 CoreLib，都会让补丁在某些调用点悄悄失效。"补丁没生效"时先想内联，并且在启动时测，而不只在主菜单测。
4. **引擎的事件顺序决定了能在哪里拦截。** 输入先分发给窗口、窗口在帧末才画、控件 id 会重复：很多"只在大对话框里才偏"的问题都在这里。
5. **给引擎的原生库写替身，先看游戏启动时怎么用它。** 音频库一开始把六千个音效全部解码。
6. **电脑上的原生库是 release 构建。** 断言、调试扩展、日志级别这类差异，会变成只有手机才有的崩溃。
7. **下结论前，对照失败日志和正常日志的第一条错误。** "游戏版本太旧"的猜测从没被复现过，真正的原因是 Linux/Mac 版游戏的 Steam 库名。
8. **测试者的手机千差万别，诊断要自带。** 厂商系统会杀后台、绑核、改变文件选择器的行为、不给应用读 logcat；运行记录、启动监视和墓碑解码让远程报告可以直接定位。
9. **每项改动都做成开关，并配一个复现或自检开关。** 测试者的问题先在并排测试包里复现，修完再上主安装。
10. **和电脑一致比"更好"重要。** 例如确定按钮按每次松手分别调用、同名程序集的加载、浮点格式、字符串哈希：照电脑做，mod 才会和在电脑上一样工作。

---

## 15. 开发者与开发方式

- **samilry**：项目负责人。定目标和优先级，在交互方式和各种取舍上做决定，在真实手机上试玩验收，转来测试者的报告，发布项目。
- **Claude（Anthropic）**，通过 Claude Code 工作：在负责人的指导下承担了大部分工程工作，包括分析、设计、实现、测量、测试工具（包括驱动真机的 MCP 工具）和这份文档。

仓库的提交带有 `Co-Authored-By: Claude` 标注。

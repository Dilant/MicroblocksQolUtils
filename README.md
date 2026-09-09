# microblock's QoL Utils

[English](README.en.md)

面向 Celeste + Everest 的 QoL 工具模组。项目以 Windows 为主要目标平台，
但字体、图标和界面绘制所需的 native rasterizer 也可以构建到 Linux/macOS。
MiaoNet、CollabUtils2 和 SpeedrunTool 都是运行时可选集成，不是硬依赖。

## 功能

### 界面与系统

- Material You 风格的 HUD 信息卡、设置页、章节选择页和 Everest 模组设置页。
  HUD 背景、亚克力模糊和模组设置页替换都可以单独开关。
- 自绘的 QOL 设置页：按分类浏览设置、搜索设置、鼠标/键盘/手柄操作，
  并保留 Everest 原生的绑定配置和自定义设置项。
- 可选替换原版选关页：支持最近游玩、地图集、搜索、作者/描述/标签预览，
  键盘、手柄、鼠标和滚轮操作，并通过原生 OuiChapterPanel 流程进入章节。
  会遵守官方地图解锁限制。
- CollabUtils2 集成是反射式的：默认隐藏 Collab 地图和 Gym；打开高级选项后
  可以显示它们。多大厅 Collab 会按 Lobby 分组，并可以折叠整个 Lobby。
- 内置 Material Symbols 图标和跨平台字体渲染。文本按实际像素栅格化并做缓存，
  默认字体是 Microsoft YaHei UI，可以从已安装字体中选择，并支持 80%–160% 字体缩放。
- Windows HiDPI 支持、输入法自动切换（文本输入使用中文布局，正常游戏使用英文布局）。
- 可选移除房间过渡和死亡动画；碰撞箱支持隐藏、叠加显示、仅显示碰撞箱三种模式。
- 暂停菜单中提供“Microblock 的 QOL 工具”入口。

### HUD 与小地图

- 滚动 FPS、CPU 帧耗时，以及在 Motion Smoothing 可用时分别显示物理帧率和渲染帧率。
- 可选帧卡顿提示和帧分析 HUD。
- 基于当前固体网格绘制的圆形/方形小地图：
  - 可调整尺寸、缩放和键盘/手柄缩放按键；
  - 房间边界、房间背景、背景透明度和自适应地图颜色；
  - 房间之间到地图终点的缓存最短路线与剩余房间数；
  - 草莓、金草莓、月莓、心、磁带、钥匙和宝石标记；
  - 根据存档显示收集状态；可选在边缘显示附近房间的草莓。
- 可选显示 MiaoNet 同地图玩家、头像、越界玩家和玩家名称；玩家名称可以设置为
  不显示、仅显示关注玩家或显示所有人。
- 可隐藏 MiaoNet 原生的越界名字标签。

### MiaoNet 关注与通知

MiaoNet 存在时，模组会通过反射读取同地图玩家的位置、房间、名称和头像，
MiaoNet 不存在时不会阻止模组加载。

Everest 控制台命令：

~~~text
qol_watch <玩家名>
qol_unwatch <玩家名>
qol_watch_list
~~~

MiaoNet 聊天框还注册了 /qol（别名 /mu）：

~~~text
/qol watch <玩家名>
/qol unwatch <玩家名>
/qol list
~~~

Windows 下，关注的玩家换房间且 Celeste 不在前台时，会发送系统通知。

### 录制与死亡回放

录制按实际 renderer 选择 native 取帧：Windows 支持 D3D11 与 OpenGL；Linux/macOS
保留 OpenGL 3.2+ 路径。不再依赖桌面录屏 API、权限选择器或屏幕位置。
**不要为录制强制改成 OpenGL**。安装器会备份并撤销上一版自动添加的 OpenGL 参数，
保留用户自行配置的参数。Metal/Vulkan/SDL_GPU 尚未实现；Linux/macOS/Android 未真机验证。

在 `SDL_GL_SwapWindow` **之前**提交每帧 PBO 异步读回；后续帧零等待检查 GPU fence，
后台线程转成 BGRA 并分发，不在游戏线程编码或调用消费者。尺寸变化会重建 PBO；
没有订阅时停止读回。D3D11 对应 native DXGI Present shim、staging texture 与非阻塞 query/map。
读回与编码解耦，消费者可以分别注册像素、FMOD PCM、音乐事件 callback。
内置录制器使用借用式像素池，避免逐帧大数组分配；`Subscribe` 保持 owned 语义，
低分配 `SubscribeBorrowed` 需要在回调内消费或调用 `Snapshot()` 保留像素。
编码器冷启动采用有明确内存/帧数上限的无损 burst 缓冲，不再只保留最后三帧。
详见 [采集架构及测试](docs/capture-architecture.md)。

- 自动录制：关闭／完整流程／金草莓挑战。金莓挑战可选择结算时保存，或继续录到整关完成；
  死亡后可选择丢弃、继续录制（保留失败过程，直到通关）或保存失败录像。结束点与死亡策略在开始录制时确定。
- 停止后不会自动重开或转成手动录制；金莓模式等待下一次拿起，完整流程等待下一关或重新开关自动录制。
  开始键只开启手动录制，停止键保存当前手动或自动录像。
- 录制页使用自动录制、死亡回放、画质声音、存储清理、快捷键、录像库六个 Tab，金莓选项仅在金莓模式显示。
- 控制台和设置页都支持手动录制、保存和丢弃；开始录制和结束保存可以分别绑定
  可选的键盘或手柄按键，默认均不绑定。
- 完整录像和死亡回放使用独立的编码/剪辑会话，共享唯一的像素和 FMOD 采集源；死亡回放默认保留最近 30 秒，
  可设置为 10–60 秒，并在死亡后自动保存、复活后继续录制。
- 连续的 H.264 死亡回放会直接复用采集时已编码的视频，不再二次编码；普通房间/BGM 元数据切分不影响快速保存。
  非关键帧开头通过 MP4 edit list 隐藏解码预滚，保留音效、BGM 重构和敏感房间策略。
  暂停造成的剪切、开启“剪辑冻结帧”、不兼容的码流会自动走原精确剪辑流程。
  快速保存仍需排空采集队列、编码音频及文件收尾，不保证所有机器/负载下零等待。
- 默认连续录制只保留成功片段（金莓死亡选择“不停止录制”时保留失败过程）。死亡、房间切换、暂停、SpeedrunTool 加载和自定义
  respawn 点会改变最终剪辑时间线，不会让失败过程进入最终视频。
- “录制时切面自动保存（保证视频连续性）”默认开启：完整录像录制中（含手动录制）切面结束后，
  自动存入独立的 SpeedrunTool 内部槽，并一起保存录像时间线；仅死亡回放不触发。
  本房间普通死亡会自动读内部槽，不依赖 SpeedrunTool 的死亡自动读档开关；金草莓重开章节、
  自定义死亡动作和跨房间恢复不接管。录制开始、复活点变化和手动读档后也会更新内部恢复点。
  不覆盖/切走用户槽位，用户多次 SL 后清当前槽或清全部用户槽，仍可正常恢复。
  内部存读档不新增计时器/金草莓标记，不播放提示或停顿，不清除已有标记，也不回退正常死亡计数和时间。
  用户手动存读档保留 SpeedrunTool 原有行为。停止录制、关闭功能或退出关卡后释放内部槽。
  未安装/未启用 SpeedrunTool、TAS 运行中或当前为 TAS 存档时不执行；接口不兼容时安全跳过。
- 房间完成后，native finalizer 只读取保留的片段，生成无间隙的 MP4；
  支持进度显示、手动/自动/死亡回放三个分类、打开文件夹、播放和删除。
- 输出默认位于 %USERPROFILE%\Videos\Celeste\microblocks-qol-recordings，
  也可以在设置中指定目录。手动、自动、死亡回放分别放在 full/<区域>、auto/<区域> 和 deaths/<区域>，各自设置保留数量；旧录像保留原位；
  每个完成的 MP4 旁边会有 .timeline.json 时间线文件。
- 视频优先使用平台 H.264 编码器（Windows Media Foundation、macOS
  VideoToolbox、Linux NVENC/QSV）；Linux 在没有可直接使用的 H.264 编码器时回退到
  MP4 中的 MPEG-4 Part 2。音频使用 AAC。可以选择录制 UI 音效、帧率、码率和编码器，
  并设置完整录像/死亡回放的保留数量或立即清理旧录像。
- 音频通过唯一一套 FMOD DSP tap 采集 gameplay_sfx、music 和 ui_sfx（各消费者独立筛选 UI 音效）。
  SFX/UI 写入 `.sfxchunks`，BGM 单独写入 `.bgmchunks`；`.music.jsonl` 独立保存切歌、
  重启、停止、暂停、seek、参数和播放位置等信息。同曲重启不当作旧曲连续播放。
  最终化时分别处理 SFX 剪辑和 BGM 后期时间线。因此 `.working` 里的 MKV 是无音轨的中间文件；
  应播放 `full` 或 `deaths` 目录下完成最终化的 MP4。
- BGM 可以直接使用捕获的游戏混音，也可以使用 SfxOnlyWithPostMix：
  普通地图在该模式下只按视频时间线剪辑 SFX/UI，music 总线作为独立后期音源，
  跨死亡和暂停剪辑点连续铺设；如果配置了干净 BGM 映射，则用映射文件替换对应
  事件片段。检测到 cassette block、rhythm 或 music-sync 类实体的节奏地图会自动
  保留现场混音，避免破坏音画同步。

可选的 BgmEventMapFile 是一个 JSON 对象，路径相对 JSON 文件所在目录解析：

~~~json
{
  "event:/music/lvl1/main": "D:/Celeste-BGM/first_steps.flac"
}
~~~

没有映射时仍会使用单独捕获的 music 总线作为后期 BGM 来源。

录制设置中的“剪辑冻结帧”默认关闭；开启后会在最终化时检测捕获时间线中的冻结间隔，
将这些间隔从视频、音频和 BGM 时间线中一并移除。开启状态会在设置页使用高亮边框突出显示。

### Profiler

设置页中的 Profiler 可以启动一次 10 秒的进程内 EventPipe 栈采样：

- 按 Update 和 Render 分开统计；
- 显示独占 CPU 时间、占比、所属模组程序集和 MonoMod hook 目标；
- 支持“简单·仅 Mod”和“专业·全部”两种列表；
- 生成 CSV 和 .nettrace，保存到
  %LOCALAPPDATA%\MicroblocksQolUtils\profiles；
- 轻量级的帧耗时 HUD 不依赖完整采样。

## 控制台命令

除上面的关注命令外，录制和 native capture 还提供：

~~~text
qol_capture_probe_start
qol_capture_probe_stats
qol_capture_probe_stop

qol_record_start
qol_record_save
qol_record_discard
qol_record_status
~~~

前三个命令用于开发时检查共享 SDL 捕获、队列深度、丢帧和媒体时长，
不会自动开启正常录制。

## 构建与安装

默认 Celeste 路径是：

~~~text
C:\SteamLibrary\steamapps\common\Celeste
~~~

需要 Node.js、.NET 8 SDK、Rust，以及对应平台的 C/C++ 工具链。
Windows 下如果要构建完整录制后端，还需要：

- Visual Studio C++ Build Tools；
- LLVM/Clang（包括 libclang.dll 和 clang-cl.exe）；
- MSYS2、GNU make、Perl 和 NASM；
- tar。

Linux 完整构建还需要 Clang/libclang、GNU make、pkg-config
开发包；macOS 完整构建需要 Xcode Command Line Tools。各平台都会从已校验的
FFmpeg 8.1 源码构建并随包附带最小 LGPL shared runtime。

仓库的入口脚本会构建 managed mod、native rasterizer、当前平台的
capture/recording backend，并生成 Build 和 MicroblocksQolUtils.zip：

~~~powershell
node scripts/build-qol-mod.mjs
~~~

构建并安装到默认 Celeste 安装目录：

~~~powershell
node scripts/build-qol-mod.mjs --install
~~~

如果游戏不在默认路径，设置 CELESTE_ROOT。安装前请关闭 Celeste，脚本会拒绝
替换正在被游戏加载的 DLL：

~~~powershell
$env:CELESTE_ROOT = "D:\Games\Celeste"
node scripts/build-qol-mod.mjs --install
~~~

可以显式选择 CI 使用的平台：

~~~powershell
node scripts/build-qol-mod.mjs --target x86_64-pc-windows-msvc
node scripts/build-qol-mod.mjs --target x86_64-unknown-linux-gnu
node scripts/build-qol-mod.mjs --target x86_64-apple-darwin
~~~

Windows、Linux 和 macOS 构建都包含录制后端及其 FFmpeg shared runtime；合并
后的通用包按 Everest 约定分别放入 `Code/lib-win-x64`、`Code/lib-linux` 和
`Code/lib-osx`，由 Everest 只加载当前平台的版本。只有 Windows 系统通知仍是
平台专用功能。运行时不会打包或调用 ffmpeg 可执行文件。

GitHub Actions 会运行 Rust 格式检查和测试，并构建 Windows x64、Linux x64
和 macOS x64 版本，再将三个版本合并为一个 `MicroblocksQolUtils.zip` 通用包。
master 的每个提交会更新 nightly 预发布，v* 标签会发布相同的通用包。

## 依赖说明

- MiaoNet、CollabUtils2、SpeedrunTool：运行时可选，使用反射或桥接接口。
- Material Symbols：仓库内嵌图标资源。
- Source/CaptureSource.cs / CaptureSubscription.cs：共享采集和可取消、隔离的回调。
- Source/SdlFrameSource.cs / Native/src/sdl_readback.rs：SDL native hook 与 PBO 异步读回。
- Tests/Capture / Capture.Integration：回调/时钟测试及真实 SDL、FMOD、编码集成测试。

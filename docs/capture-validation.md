# Capture validation — 2026-09-09 follow-up

## 第二轮：吞吐/丢块修复（同日，codex/capture-throughput）

下面较早记录中的“高负载丢帧”不是可接受的正常结论。加上长时间与分阶段统计后，发现：

1. 旧版 QSV 创建耗时约 1.4–1.5 秒，第二个 sink 前面的 NVENC 失败探测另外等待约 0.6–0.8 秒。
   3 帧队列把这段启动视频丢了，而后续编码通常能跟上。
2. native registry/audio queue、managed subscription 仍有旧的 try-lock 丢 PCM；这些调用已经是 worker，不应因瞬时锁竞争丢数据。
3. 向下取整 FPS bucket 会把同频来源的细小时间抖动误判成重复帧。
4. 每帧分配几 MiB owned 托管数组会使全量模组场景频繁停顿。即使 native 视频队列零丢弃，
   上游帧率与订阅 PCM 仍可能损失；恢复后的 PCM 突发又超过小缓冲。

已修复：有界空间差分/Zstd 冷启动缓冲；worker 锁不再抢锁失败就丢音频；mixer 三个
预分配 ring；公平派发；最近 tick；借用式引用计数像素池（旧 owned API 保持兼容）；
扩大但仍有界的 PCM 突发缓冲；AVFrame 重用及异步 codec 写前 make-writable。

### 实测对照

同机 1280×720、两个真实编码器（30 fps + 60 fps）：

| 测试 | 第一 sink 接收/编码/丢视频 | 第二 sink 接收/编码/丢视频 | native 音频丢块 |
|---|---|---|---|
| 修复前、隔离环境约 21 秒 | 616 / 575 / 41 | 1141 / 1026 / 115 | 9 / 13 |
| 最终池化路径、162 模组约 31 秒 | 926 / 926 / 0 | 1862 / 1862 / 0 | 0 / 0 |

最终测试还要求两个 sink 的订阅层像素/PCM/音乐事件/异常全部为 0，source PCM drops=0，
健康 observer 的帧/PCM drops=0，目标帧率达到时间线预期的 98% 以上；全部通过，
不是把丢帧计数隐藏在前一级。故意 200ms 的慢观察器仍独立丢帧，未拖住录制器。
本次 31 秒 Gen2 GC=0，累计 GC pause 20.5ms（进程报告值）。
burst 峰值：第一 sink 48 帧 / 63,817,229 bytes；第二 sink 136 帧 / 166,997,144 bytes，
均在 192 MiB 上限内，初始化之后队列排空。没有为了通过测试无限增加 RAM。

最终安装包又进行了同样的 31 秒全量复验（`installed-verify.*`）：再次 926/926、1862/1862，
所有上述丢弃统计为 0；健康观察器收到 3824 帧，呈现 sequence 缺口 **0**，source pool drops **0**。
Gen2 GC=0，pause=18.1ms；这次启动更慢，第二 encoder 探测/创建共约 2.59 秒，
burst 峰值 155 帧 / 189,262,934 bytes，仍在上限内并完整排空。
安装文件为 `C:\SteamLibrary\steamapps\common\Celeste\Mods\MicroblocksQolUtils.zip`，SHA256：
`7FBABE4B294B0D78086B7E6E4629902E4878CE7F9DD355C7A83AB91A4D8DF844`。

失败的中间测试也保留：仅修 native 锁/视频 burst、未池化的全量运行仍有订阅音频丢弃 259/378 块，
native 音频丢弃 25/26 块，未通过严格检查；这促成了最后的托管池化/突发缓冲修复。

最终 MP4 已解码目视确认正常画面：H.264 1280×720 / AAC stereo 48kHz，
视频 30.833333s、音频 30.826000s。原始 MKV、独立 SFX/BGM PCM、music journal 均保留。
本次是全量模组的菜单动态场景，不等同于所有复杂地图、所有 GPU/磁盘/分辨率或无限时长验证。

证据在 `G:\MicroblocksQolUtils\.work\capture-throughput\.work\`：
`baseline.mkv.passed`、`full-final.mkv.failed`、`full-pooled.mkv.passed`、
`full-pooled-console.txt`、`full-pooled-decoded.png`、`final-rust-tests.txt`。

额外回归：Rust + FFmpeg/D3D11 44 tests 通过；C# 增加 PCM ring 并发、队列公平性、
借用池上限/取消/正在执行的 callback 生命周期/Snapshot 保真；真实 SDL+FMOD 集成
238 帧、419 个非静音 PCM callback，像素、BGM 事件与剪辑连续性、resize/reload 均通过。

长时间严格 smoke（只对该次游戏进程设置，不写入 Steam/系统环境）：
```powershell
$env:MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT = '<absolute .work path>.mkv'
$env:MICROBLOCKS_QOL_CAPTURE_SMOKE_SECONDS = '30'
$env:MICROBLOCKS_QOL_CAPTURE_SMOKE_REQUIRE_LOSSLESS = '1'
```

平台默认值已经按本机 FNA3D 二进制追溯到确切源码 revision；来源及结论见
`capture-architecture.md`。Linux/macOS 默认 OpenGL 不等于已经在这两端运行测试。

## 第一轮历史记录（5bc4f08，以下丢帧结论已由上面的定位修正）

## 实测结论

测试机器：Windows / Intel Graphics，Celeste 1.4.0.0 + Everest 6487-ultra，原生默认 D3D11。

已用独立游戏目录 `G:\MicroblocksQolUtils\.work\Celeste-test` 对照：
- OpenGL + 模组 + 录制：纯色画面。
- OpenGL + 模组，不录制：仍纯色。
- OpenGL，禁用本模组：仍纯色。
- D3D11 + 模组，不录制：正常标题画面。
- D3D11 + 新版延迟 native Present shim + 双录制消费者：正常画面、音频、最终 MP4。

因此不能通过强制 OpenGL 解决这台机器的兼容性。早期 prototype 的 DXGI prologue hook
以及在 mod load 阶段安装的 hook 都在真实 Steam 游戏中失败，最终方案使用延迟 vtable shim。
这些失败不计为通过。旧 smoke 仅检查文件/帧数会把纯色视频判成功，现已要求至少十帧有可见像素差异。
UI smoke 现在有自己的 `MICROBLOCKS_QOL_MATERIAL_UI_SMOKE` 开关，不再与录制 smoke 共用环境变量。

## 已通过

- C# Release 构建：零警告、零错误。
- C# 单元测试：有界队列、慢/异常 callback 隔离、取消与 drain、自注销、FMOD sample clock；
  新增音乐事件溢出检测、完整排空、per-sink 原点/初始快照/日志完整性测试。
- Rust FFmpeg 测试：40 项，通过；包含真实 D3D11 staging/query/pixels/resize/release，
  DXGI shim 原函数调用及卸载，GL PBO 状态恢复、混音/编码、音乐日志与样本级连续性测试。
- 真实 SDL/OpenGL + FMOD 银行集成：238 帧、417 个非静音 PCM callback，像素方向/通道错误 0；
  实际触发游戏 8 声道音效和 stereo 音乐，两个录制器、慢消费者、resize、卸载重载、finalize。
  验证 BGM 独立文件及 pause/resume/seek/同实例 stop/start 日志，并导出连续 BGM 剪辑。
- 真实 Celeste/D3D11 smoke：232 像素 callback、548 PCM callback，慢消费者独立丢弃 209 帧；
  第一录制器关闭后第二个继续。无音频丢块。输出已用 FFmpeg 解码和检查画面。
  MP4：H.264 1280x720，AAC stereo 48kHz；视频 2.800s / 音频 2.773s（小于一帧尾长差）。
  这次机器的自动编码器明显跟不上输入，第一录制器 84 帧接收/49 帧编码队列丢弃；
  测试证明时间线/隔离，并不宣称所有编码器、分辨率都能满帧。

另外通过用户现有 Mods 的全量启动（162 modules，存档使用独立副本）：429 像素 callback / 506 PCM callback，MP4 解码画面正常。该高负载双编码测试中第一 sink 接收 84 帧、编码队列丢 57 帧、音频丢 3 块；不隐瞒负载下的数据丢弃，也不把它视为无损/满帧验证。

数据和日志在 `.work/capture-followup/.work/`，测试游戏不改用户存档。

## 复现测试

### 2026-09-09：BGM 例外缩小到对应 room（ABI 7）

用户导出的第一章视频 sidecar 中 `reconstructBgm=false`，设置却为 `SfxOnlyWithPostMix`；
日志显示整张地图被判为 rhythm-sensitive。原因是地图内任意磁带房会关闭全章重构，
不是这次导出缺少 PCM 块。现在模式仍按用户设置，例外保存为每个片段的 `BgmFollowsVideo` / `RoomName`。

本轮验证：
- Rust **47/47**：包含真实 D3D11 staging / DXGI hook 测试、FFmpeg 编码/解码及新增房间策略测试。
  7 段音频采样测试覆盖普通→敏感→普通、敏感房内多次剪切、离开后的连续 metadata 切分；
  经生产 `build_audio_track` 同时验证 SFX 裁切、BGM 游标、显式现场混音模式，逐样本误差 < `1e-6`。
  敏感段不被静态外部音乐替换；JSON/去冻结帧切分保留策略。
- `Tests/Recording.Policy` 使用实际编译的 AutoRecorder 和 Celeste 类型（不是复制策略）：
  同地图普通房不受 cassette/音乐同步 trigger 房影响，两条 sink 分支切分、恢复快照、极短房间、
  死亡回放裁尾及旧 timeline 默认值均通过。时钟由无 native handle 的测试对象固定，不改用户存档。
- 原有 managed Capture 回归通过。
- 实际 SDL OpenGL + FMOD 集成：**238** 像素 callback、**419** 非静音 PCM callback，像素/顺序错误 **0**。
  两个 sink 编码队列分别消耗 **63/178** 帧、丢弃均 **0**；慢订阅者是故意制造的负向测试。
  新 `room-bgm.mp4` 与普通混音/连续 BGM 输出均可被 FFmpeg 完整解码，房间策略视频 0.900s、AAC 0.917s。
  集成测试末尾的 unsupported-renderer 日志是非 GL 窗口拒绝测试的预期结果。

证据在 `.work/bgm-continuity/.work/`：`rust-tests.log`、`room-policy.log`、`managed-tests.log`、
`integration.log`、`integration/room-bgm.mp4`。未把此轮自动化验证称为用户实际通关后的听感确认。
旧用户 MP4 对应原始 MKV/独立音轨已被成功导出清理；本修复不会凭空修复已混合的旧文件，需重新录制。

设置本机 `CELESTE_ROOT`、`FFMPEG_DIR`、`LIBCLANG_PATH`、Rust/.NET PATH，TEMP/TMP 指到 `.work`：

```powershell
$env:MQOL_TEST_FFMPEG = '1'
$env:MQOL_TEST_D3D11 = '1'
cargo test -p microblocks-qol-native --features ffmpeg --lib -- --test-threads=1
dotnet run --project Tests/Capture/Capture.csproj -c Release
dotnet run --project Tests/Recording.Policy/Recording.Policy.csproj -c Release
# 集成测试另外设置 MQOL_NATIVE_PATH 到 release DLL，MQOL_TEST_OUTPUT 到 .work 目录
# 并确保 SDL/FMOD/FFmpeg DLL 可加载。
dotnet run --project Tests/Capture.Integration/Capture.Integration.csproj -c Release
```

真实游戏 smoke：仅为该次启动设置 `MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT` 到 `.work/*.mkv`。
等 Overworld/Level 加载后开始，写 `.passed` 或 `.failed`；不要把这个环境变量永久写入 Steam/系统。

## 补充验证

### 2026-09-09：死亡回放复用已编码画面

- Rust **49/49**，包含真实 D3D11 测试及 FFmpeg 集成。新增连续范围判定、非关键帧裁切、
  跨 GOP seek、0 秒起点、20ms 短片、room metadata 边界和无音频输出；
  对输出的可见 H.264 包逐字节比较原始 MKV，并解码验证画面、帧数与音画时间（误差不超过一帧）。
  原不连续范围/crossfade 测试改为请求快速路径，验证其安全回退到精确转码。
- managed Capture / Recording.Policy 回归通过；实际 AutoRecorder 死亡任务工厂会传递快速路径标记，
  同时保留用户的冻结帧编辑设置。Release 构建零 C# 警告、零错误。
- 实际 SDL OpenGL + FMOD 双录制集成：238 像素 / 421 音频 callback、像素错误 0、
  两个 sink 视频队列均丢弃 0。额外通过 managed/native bridge 保存非关键帧起点、
  普通→敏感房 metadata 的 1.5 秒快速回放，用时 **56.2ms**（小尺寸测试画面，非 720p 性能数据）。
  生成的全部 MP4 均通过 FFmpeg 完整解码。
- 同机 Release synthetic benchmark：12 秒 1280×720@60 H.264 源，保留从 1.25s 开始的 10s，
  8 Mbps、stereo 48kHz 音频，两条路径同用 libopenh264/AAC：

  | 最终化路径 | 耗时 | 视频 / 音频时长 |
  | --- | ---: | --- |
  | 原完整转码 | 3203.6ms | 10.000s / 10.005s |
  | 复用视频包 | 603.5ms | 9.999s / 10.005s |

  两种输出的音视频起点均为 0，完整解码无错误。这是一次合成素材对比，
  **不包含死亡时尚未排空的实时编码队列**，不代表用户全部模组/地图下的点击到播放耗时。
  长回放音频编码、磁盘或录制积压仍可能增加等待，复杂剪辑/冻结帧编辑仍需转码。

证据：`.work/instant-death-replay/.work/` 下 `rust-tests.log`、`policy-tests.log`、
`managed-tests.log`、`integration.log`、`benchmark.log`、`benchmark.ps1` 和各输出 MP4。

## 平台与功能限制

第一轮 ABI 6 在添加本轮 Zstd 之前通过 Linux x64、macOS x64、Android arm64 的无 FFmpeg cargo check；不等于真实窗口、驱动、音频、FFmpeg 打包测试。本轮仅在 Windows 构建运行，未重跑三端交叉检查；Zstd C 库由 Cargo 构建，三平台 CI 构建矩阵保留。
Metal/Vulkan/SDL_GPU 没有实现。D3D11 HDR/MSAA swapchain、其他 GPU/overlay 组合没有普遍兼容性保证。
游戏之外直接 native FMOD 命令的一帧内中间状态可能不能被 managed observer 看见。
静态 BGM 映射不能重放动态 FMOD 音乐；新独立 PCM/事件路径应作为保真来源。

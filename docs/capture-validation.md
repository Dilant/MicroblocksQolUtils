# Capture validation — 2026-09-09 follow-up

## ABI 8：2560×1506 实际帧率修复（codex/frame-cadence）

### 用户文件证据

`20260909-215504-Celeste_1-ForsakenCity.mp4`：2560×1506，106.316667 秒，4082 帧。
`r_frame_rate=60/1` 但 `avg_frame_rate=244920/6379`，实际约 **38.4 FPS**。
前 10 秒约 60，此后多为 35–39；60-Hz tick 间隔计数为 1:1871、2:2123、3:87。
画面 HUD 显示 physics60/render120–124；不是只因为游戏自身跑到 38 FPS。
原始 MKV 已在旧导出后清理，不能从现有 MP4 追溯每一级准确丢弃数，也不能恢复未采到的画面。

### 定位与最终方案

- 同分辨率 paced native producer 复现旧压缩缓存：本应 20 秒提交 1200 帧，实际耗时约 50.53 秒，1144 编码 / 56 队列丢弃。
- QSV async4、SIMD predictor、快 Zstd、NV12 缓存等中间方案改善吞吐，但完整 FNA 测试仍有启动丢帧。
  temporal delta 在静止图片上可通过；**双路＋整幅滚动仍失败**，没有把该结果当最终通过。
- 最终移除 Zstd/差分压缩，采用有界 NV12 临时文件启动缓冲；采样提前到 GPU 提交及订阅入队前；
  录制 callback 5 槽吸收短突发，共享 128-MiB pool/native 192-MiB RAM cap 不扩大。
  每段文件限制 2 GiB，队列仍限制 `capacity+fps*5`；IO 在 worker，不在 render/FMOD。
  在录制和导出的 AVFrame 复用点处理异步编码器引用。

### 实际 FNA/D3D11 完整链路长测

同机 Intel Graphics / h264_qsv，真实 FNA Game / SDL / DXGI，**2560×1506，120-Hz 呈现，两路各 60-FPS 编码，持续 120 秒**。
使用用户视频截图整幅横向滚动＋每个呈现帧的二进制可见编号，不是纯色、静止画面或只测 native push。
走生产 managed source、subscription worker、native encoder 和 FMOD music bus；不启动 Steam、不操作用户存档/图形设置。

| 输出 | 实际视频帧数 | 最后视频 PTS | 实测帧率 | 编号重复/倒序 |
|---|---:|---:|---:|---:|
| 第一原始 MKV | 7186 | 119.750s | 60.000 | 0 / 0 |
| 第二原始 MKV | 7189 | 119.800s | 60.000 | 0 / 0 |
| 第一轨经过生产 finalizer 的 MP4 | 7186 | 119.750s | 60.000 | 0 / 0 |

两路 native/订阅视频丢弃、PCM 丢块、音乐事件丢失、callback exception 均为 **0**。
所有相邻视频 PTS 都相差一个 60-Hz tick；导出完整解码，并检查 top 512×32 区域的画面编号，未用重复帧补齐。
两条轨尾部相差 3 帧来自顺序停止时第二条仍运行，不能把 120 秒墙钟直接当作每条视频的首帧原点。
启动后缓存排空；两路峰值分别 56/111 帧，RAM 排队各 15,421,504 bytes，spool 排队 318,067,200 / 636,134,400 bytes。
正常结束后测试目录无 `mqol-frames-*.tmp` 残留。
主线整合前的构建又做了 20 秒双路滚动复验：1185/1188 帧，各级视频/音频丢失、source pool/PCM 丢失及 callback 错误均为 0。
构建包与安装到 `C:\SteamLibrary\steamapps\common\Celeste\Mods\MicroblocksQolUtils.zip` 的文件 SHA256 一致：
`BF7B5F80A7CC48A7D9EA7F08BFFD755FE19126BE143554532BF73E7E55DF27D5`。
这是完整采集管线测试，不冒充用户真实游戏重新通关的测试；其他 GPU、慢磁盘、长期过载仍需实际测量。

证据：`.work/frame-cadence/.work/fna-final.log`、`fna-final/report.txt`、两个 MKV 的 `.cadence.json`、
`export-final/cadence-final.mp4.cadence.json`、`export-frame.png`。
早期失败的 native、GL 吞吐 prototype 及压缩实验日志保留在同目录；GL prototype 渲染自身达不到 120 FPS，未用来宣称游戏 OpenGL 后端吞吐失败。

### 与期间新增主线提交整合

保留并合并主线 `9f44e03` 中的自动录制策略、独立恢复存档、快速死亡回放和界面变更，未以旧工作树覆盖这些功能。
合并后的 Rust FFmpeg 回归 **52 passed / 2 hardware ignored**；Capture、Recording.Policy 均通过，Recorder **94** 项、Recording recovery **89** 项通过；真实已安装 SpeedrunTool 的私有 slot/静默 hooks 互操作验证通过，未生成游戏存档。
重新跑 SDL/GL＋FMOD＋快速回放集成和 2560×1506 双路滚动 20 秒（1186/1188 帧，全部丢弃/异常计数 0）；
最新 finalizer 对两分钟原始录像再次输出 **7186 帧 / 60 FPS / 编号重复与倒序 0**。
另在实际录像所在 **C 盘** 的 `C:\Users\17153\Videos\Celeste\.work\mqol-cadence-verification-20260909` 重跑双录制，source pool/PCM、native/订阅的视频和音频丢失均为 0。
最终安装包与构建包 SHA256 一致：`F0EDB1626CB9997A9F323727125A61740D79A48E6FA2FC206868EF0A2BE18194`。
合并回归证据在 `.work/frame-cadence/.work/merged-*.log`、`merged-export/`、`c-volume-verify.log`。

### 回归与复现

- Rust FFmpeg：50 passed，2 个硬件吞吐用例默认 ignored；含 NV12 奇偶/非对齐尺寸与原 swscale 逐平面精确对照、
  spool 顺序/拒绝/resize/空间上限/最后 reader 清理、真实 D3D11 staging/resize/shim、音频与房间级 BGM 样本测试。
- 两个默认 ignored 的 release 吞吐用例也单独运行通过：native producer 1200/1200（19.988 秒提交）；D3D11 分离 poll/sink worker 的读回链路 1200/1200，均无队列丢弃。早期串行 poll+push prototype 曾失败，现测试按实际双 worker 拓扑修正，不降低帧率断言。
- 无 FFmpeg Rust：25 passed；managed Capture / Recording.Policy 均通过。
- 当前源码通过 Linux x64、macOS x64、Android arm64 的 **无 FFmpeg cargo check**；这不是三平台 FFmpeg 打包、GPU 或音频真机测试。
- SDL OpenGL + FMOD 原有集成：238 像素 / 419 非静音 PCM callback，方向/通道/顺序错误 0；两个 sink 62/178 帧、native 丢弃 0。
  验证慢消费者隔离、resize、重载、独立 BGM/事件、普通/连续/房间策略导出；末尾不支持 renderer 的错误是预期负向测试。

高分辨率 FNA 测试在原 `Capture.Integration` 环境之外设置：

```powershell
$env:MQOL_TEST_D3D_GAME='1'
$env:MQOL_TEST_BGRA='<2560x1506 BGRA fixture under .work>'
$env:MQOL_TEST_ENCODER='auto'
$env:MQOL_TEST_DUAL='1'
$env:MQOL_TEST_SCROLL='1'
$env:MQOL_TEST_SECONDS='120'
# MQOL_TEST_OUTPUT 必须指向 .work 内的输出目录。
dotnet run --project Tests/Capture.Integration/Capture.Integration.csproj -c Release
```

下面均为历史阶段的结果，尤其旧 720p/Zstd 验证不能替代上面的高 DPI 长测。

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

### 2026-09-10：内部读档恢复首帧冻结

- 用户 `20260910-013227-513` 完整录像逐帧检查：约 3.83s、26.33s 的存档恢复接缝有角色/镜头跳变；
  timeline 已全部标记 seamless，所以继续关闭 crossfade 不能修复缺失的游戏状态。
- 原内部 load 在 Engine.Update 末尾同步完成，但下一次玩家物理更新先于 QolHud 重新开启录制分支。
  现在 load 前进入冻结门，load 后保持冻结，直到两路 sink 接受各自恢复关键帧；首个物理步不使用读档积累的 wall time。
  保存增加无 UI 的 boundary presentation，边界姿态留到恢复分支包含，指示器和后台克隆期间均不更新游戏。
- `Tests/Recording` **122** assertions：三次恢复中每次模拟 120 个长 elapsed 补步和延迟采集，
  检查位置、镜头、场景帧号不变，收到确认后才恢复；同时保留独立槽、清槽、无标记、死亡统计、失败回退和场景切换测试。
- `Tests/Recorder` **105** checks：生产 AutoRecorder 双路确认、独立起点、重复恢复、只保留成功分支、
  硬切和死亡回放 prefer-video-copy 标记。Recording.Policy、Capture、真实原始/Cache SRT hook 兼容测试通过。
- Rust **53 passed / 2 ignored**（含真实 FFmpeg/D3D11）；实际 SDL/FMOD 双录制集成 **238** 视频 / **439** 音频 callback。
  保存 gap 的 GOP 硬切复制约 **29.8ms**，1.5s 普通死亡回放约 **54.7ms**；均为小尺寸测试，不是用户 2560×1506 的性能保证。
  生成 MP4 完整解码通过，Release 构建零警告/错误。证据在 `.work/recovery-frame-seams/.work/`。
- 这些验证不能替代用户模组组合下重新录制的实机验收；旧 MP4 的缺失帧不会凭空恢复。

### 2026-09-10：MotionSmoothing 高清背景恢复首帧

- 用户 `20260910-014544-654` 视频约 4.25s 接缝：恢复帧背景镜头偏移，随后回到正常位置。
  本机 MotionSmoothing 1.6.5 使用 Hires 镜头、高清背景/前景、动态渲染。
- 检查真实 relinked DLL：SRT load 后 SmoothAllObjects 注册相机状态，但其 SmoothedRealPosition
  在 UpdateHistory 之前仍是零；Hires 渲染直接读该值。现在内部恢复后提前初始化插值历史并禁止旧时间戳外推，
  不执行任何额外的 Scene/Engine/Player 更新。另给 UpdateAtDraw 加暂停门，防止背景、雪和粒子在保存期间继续运行。
- 新 `Tests/Recording.MotionSmoothing` 直接加载实际 Cache DLL（不启动游戏、不改设置）：**20 checks**。
  三次模拟恢复，验证初始零坐标确实可重现、初始化后的镜头/实体显示坐标等于恢复位置且不移动实体；
  180 次绘制更新等待期间背景/粒子零更新，正常绘制、恢复、重复注册、卸载和不兼容程序集回退通过。
- Recording **122**、Recorder **105**、Recording.Policy、Capture、真实 Cache SRT hooks 回归通过；
  Release 零警告/错误。证据在 `.work/background-recovery-seam/.work/`。
  本轮不修改 native 编码/剪辑路径，硬切与死亡回放快速路径保持原样。仍需重新录制做用户组合下的画面验收。

## 平台与功能限制

ABI 8 的 Linux x64、macOS x64、Android arm64 无 FFmpeg 编译检查通过；实际 GPU/音频/FFmpeg 运行仍仅在 Windows 验证。三平台 CI 打包矩阵保留，不能把 cargo check 当作真机通过。
Metal/Vulkan/SDL_GPU 没有实现。D3D11 HDR/MSAA swapchain、其他 GPU/overlay 组合没有普遍兼容性保证。
游戏之外直接 native FMOD 命令的一帧内中间状态可能不能被 managed observer 看见。
静态 BGM 映射不能重放动态 FMOD 音乐；新独立 PCM/事件路径应作为保真来源。

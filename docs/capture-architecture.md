# 共享采集架构（native ABI 11）

## 数据路径

```text
SDL/FNA 呈现：按真实后端路由，不更改游戏 renderer
  OpenGL: SDL_GL_SwapWindow native hook → 3 个 PBO + GLsync
  Windows D3D11: DXGI Present vtable shim → 3 个 staging texture + event query
        ↓ 采集层按每种请求 FPS 选一次，记录接收者位图及配置版本
        ↓ 后续帧非阻塞检查 GPU 完成；保持原始提交时间戳与接收者信息
        ↓ 有界 CPU 队列（3 帧）；worker 转为 top-down BGRA8

FMOD: gameplay_sfx / ui_sfx / music 记录 EventInstance 命令；录制阶段不启用 PCM tap
        ↓ SFX 事件路径、实例、时间、参数、位置和生命周期 → .sfxevents
        ↓ music event state、切换、参数、seek、pause 和位置 → .music.jsonl

MusicCapture: 主/alt 音乐状态快照 + managed command hooks
        ↓ switch / start / stop / pause / resume / seek / parameter / seek-or-loop
        ↓ 统一时钟、instance ID、事件路径、播放位置、参数快照

                        CaptureSource（单一来源）
                                  ↓
            CaptureSubscription（各自队列、各自串行 callback worker）
                   /              |              \
             全程录制          死亡回放          第三方消费者
          NativeCaptureSession：独立编码、原点、PCM 文件及事件日志
                                  ↓
            视频/SFX 剪辑时间线 + music 输出时间线 → FFmpeg 输出
```

PR #2 仅引入音频首视频帧原点/reset 的同步处理；未合并其录制、授权及 UI 实现。
scap 及 vendored capture 依赖已移除。`NativeCaptureSession` 不再安装 DSP 或获取画面。

## Callback API

```csharp
CaptureSubscription registration = CaptureSource.Subscribe(
    pixels: frame => {
        // ReadOnlyMemory<byte> Pixels; Width/Height/Stride;
        // TimestampNanos、Sequence；top-down、紧密排列的 BGRA8。
    },
    fmod: audio => {
        // ReadOnlyMemory<float> Samples；SampleRate、Channels、BusId、BusPath；
        // DspClock、TimestampNanos。录制内置消费者不注册 PCM callback；
        // gameplay/UI SFX 和 music 都写入事件日志。
    },
    music: change => {
        // Track(main/alt)、Kind、Event、InstanceId、TimelineMilliseconds；
        // Paused、PlaybackState、Parameters、TimestampNanos、Sequence。
    });

registration.Dispose();        // 取消：丢弃待处理队列
await registration.Completion; // 在 callback 外等待正在执行的 callback
// 或 registration.Complete(); await registration.Completion;
// Complete 注销后排空已接受的队列；录制消费者使用此路径，保留尾部音乐事件。
```

三个 callback 可以独立省略，但不能全空。最多 16 个注册。每个注册独立排队：
普通订阅 3 帧，内置录制订阅 5 帧；256 PCM 块、256 音乐事件。callback 内不能直接操纵 game/GL/D3D/FMOD 对象，
也不能同步等待自身 Completion。慢消费者、抛异常的消费者不影响其他 callback。
数组在发布后不复用，可保留；只读契约不允许通过 unsafe/MemoryMarshal 修改共享数据。

性能敏感的逐帧处理可改用 `CaptureSource.SubscribeBorrowed(...)`。
参数及取消方式相同，但像素仅在同步 callback 执行期间有效；要保留/交给异步代码必须先
`CaptureFrame owned = frame.Snapshot()`。PCM/音乐事件仍为 owned，不受此限制。
借用路径采用引用计数共享缓冲：入队持有，丢弃/取消/回调结束归还，绝不在正在执行 callback 时复用。
单一 source pool 最多 32 个 buffer、总 payload 128 MiB，含正在借出的 buffer；池满计入 `CaptureSource.DroppedFrames`。
原 `Subscribe` 的 owned 契约不变，仅在存在 owned 像素消费者时每帧额外复制一次并共享给它们；
高帧率大图的 owned 模式仍有相应分配/GC 成本，低频截图宜借用后按需 Snapshot。

`DroppedFrames`、`DroppedAudioChunks`、`DroppedMusicEvents`、`CallbackErrors` 是消费者统计；
`CaptureSource.DroppedAudioChunks` 是源端 PCM 溢出/非法块/同 bus 异常重入统计。
源端每个 bus 预分配 65 个 16,384-float slot（可用 64 个），共约 12.2 MiB；
worker 完成 owned copy 后才归还 slot。普通读写竞争不再丢音频。
订阅者入队只持短 bookkeeping 锁，callback 在锁外执行；视频、音乐事件、PCM 轮询公平派发。
不能在同一个串行订阅里放任意耗时 callback 又要求它无损；可把慢视频分析单独注册。
`VideoBackend`、`VideoError`、`MusicError` 提供真实后端和失败信息。
音乐事件溢出/异常会使日志标记 incomplete，重构 BGM 时拒绝把残缺日志当成功。

## 每个录制会话的文件

- `run.mkv`：连续视频。
- `run.mkv.sfxevents`：游戏音效及 UI 音效的 FMOD 事件命令；录制期间不保存 SFX PCM。
- `run.mkv.music.jsonl`：music 事件和主/alt 状态；录制阶段没有 BGM PCM，最终化时临时生成 `.bgmchunks`。
- `run.mkv.capture.json`：排空后的目标/实际输入 FPS、native/订阅/source pool 丢失统计；导出后保存在 MP4 的 `.timeline.json` 的 `captureReports` 中。

最终化期间的临时 PCM 文件保持 `MQOLAUD1` 格式及原始声道信息；只在导出混音时将 FMOD 标准
1/2/4/5/6/8 声道折叠为双声道。中心/环绕分配到左右，LFE 不加入 stereo。
不同 bus 的声道数可以不同；同一录制中采样率改变目前明确报错，不做隐式重采样。
最终化时加载 Celeste 安装目录的 FMOD banks，在独立的同步 NRT Studio system 中重放 SFX 和 music 事件；
SFX 按源视频片段裁切，music 按输出时间线连续重放，再交给 AAC 混音。临时的 `.sfxchunks` 和 `.bgmchunks` 只在最终化期间存在。

音乐观察器只有一份，不覆盖 FMOD 已有的 event callback。主/alt 切换在 Celeste 音乐命令边界观察；
现有音乐实例的 start/stop/seek/pause/setParameterValue 另外 hook FMOD managed wrapper。
按帧状态观察补充自然结束、循环、参数/播放位置变化。重复设置相同参数不切断音乐段。
直接绕过 managed wrapper 的 native 命令仅能通过后续状态快照观察，不能保证捕获一帧内所有中间状态。
命令时间戳表示游戏发出命令的时刻，不声称等于 FMOD 异步执行后第一个可听样本。

每个新消费者收到当前音乐快照。自己的首个视频提交时间是零点；后来的消费者不会重置
任何其他消费者的 PCM/DSP 时钟或音乐日志。事件写盘不发生在 FMOD mixer/render 线程。

## 剪辑时如何保持 BGM

开启 BGM 重构时：
1. 视频及 SFX 按保留片段裁切/交叉淡化。
2. BGM 使用独立日志划分音乐段；**不只比较歌曲名称**，同一首歌重新 start/seek 也是新段。
3. 普通房间中，同一音乐段跨视频剪切点时，BGM 读取游标按输出时长连续前进，不跳到下个画面的源时间。
4. 保留画面内的切歌、停止、参数变化会单独切分音乐时间线，不必强迫视频生成新的片段。
5. 跨过被删区间内的重启/切歌等变化时，使用新的音乐段，不误接旧实例。
6. 节奏敏感性只检查当前 `Session.Level` 对应 room 的 entities/triggers；磁带房不会禁用整章的重构。
   切换房间时，两条录制分支均按各自 media clock 无淡化切分，片段保存 `RoomName`、`BgmFollowsVideo`。
   死亡回放裁尾、复活锚点、SpeedrunTool 快照及去冻结帧切分均保留此标记。
7. 敏感房间片段的 BGM 读取真实源时间，保留音乐/画面节奏；普通房间恢复后从前段结束处连续读起。
   进入敏感房间（或房内剪切）可能需要重新对齐音乐，这是该房间的同步例外，而不是整章都跟着剪。
   静态外部音乐映射不替换敏感片段的现场 FMOD 音乐。ABI 7 防止旧 DLL 静默忽略片段策略。

关闭重构时仍保留独立源文件，只在导出阶段让 BGM 跟随视频裁切。
静态外部 BGM 映射是可选替换功能，不能等价重放 FMOD 动态参数、alt 叠加及 DSP 效果；
需要保留游戏实际动态音乐时使用采集到的 BGM，不配置静态替换映射。
剪辑不能凭空生成未采到的音乐，PCM 丢块仍按时间戳表现为缺口。

## 录制启动、帧率与过载（ABI 11）

- **只有采集层筛选帧**：`SubscribeRecording(fps, ...)` 注册目标速率，GL/D3D11 source 在 GPU 提交前，
  每种速率只做一次选择，同速率消费者共享同一张图像。只有任一接收者需要画面才提交读回；
  不同速率按各自所需呈现的并集采集。普通 `Subscribe` / `SubscribeBorrowed` 仍每次呈现接收。
- 选定的接收者位图随 PBO/staging、native CPU 帧传到 source worker；worker 按位图分发，
  `CaptureSubscription` 只负责有界队列与背压，`NativeCaptureSession` 不再按 FPS 二次筛帧。
  录制器只拒绝重复/倒序递送，编码器发现违反递增 PTS 契约时明确报错，不静默吞帧。
- 每帧也携带路由配置版本，复用订阅槽不能接收到旧接收者的在途 GPU 帧；其他槽变动不丢弃已有消费者的帧。
  每次 Present/Swap 仍轮询未完成 GPU 任务；游戏 renderer、渲染帧率不被修改。
- 每个 sink 的硬件编码器仍在首帧确定分辨率后独立创建，选择/初始化耗时写日志。
  QSV 的 `async_depth` 从 1 改为 4；录制及导出重用 AVFrame 前均 make-writable，不能覆盖编码器/交叉淡化仍持有的像素。
- 移除旧的 BGRA 空间差分 + Zstd 启动缓存。高 DPI 下压缩本身超过一帧预算，会让短暂启动积压变成持续丢帧；
  只换成更快压缩参数仍不足以覆盖双编码、全画面滚动，因此最终方案不依赖画面可压缩性。
- 稳态直接送 BGRA 给 encoder；积压时 callback worker 先做编码本来需要的 NV12 4:2:0 转换，
  写入录制目录中的 `mqol-frames-*.tmp`，队列只保留尺寸、原始时间戳、偏移、长度和文件引用。
  encoder worker FIFO 读出 NV12 后直接编码；不做 NV12→RGB→NV12 的有损往返，不补重复帧冒充 60 FPS。
  SwsContext 按 sink 缓存且由 mutex 独占；直接在有 padding 的 owned buffer 间转换，不额外复制两份全分辨率 AVFrame。
- 上限仍明确：共享 lease pool **128 MiB**；native 排队 RAM payload **192 MiB/sink**；
  排队帧数最多 `queue_capacity + 5 * fps`；临时文件**每段 2 GiB**。达到帧数上限先拒绝，避免继续写盘。
  队列赶上后切换/释放文件段，不会截断正在读取的旧文件；切换瞬间最多一个旧读任务段加一个新段。
  正常 Stop/Dispose 排空并删除缓存。异常杀进程可能留下具名 `.tmp`，不能保证 crash 后也自动删除。
  上述不是进程 RSS 上限：不含正在处理的帧、转换 scratch、OS 文件缓存、codec/GPU 内存。
- 录制订阅队列为 5 帧（60 FPS 约 83ms），吸收启动/IO 的短突发；普通消费者仍是 3 帧。
  没有扩大共享 pool 或 native RAM cap。慢 callback、慢磁盘、持续低于实时速度的编码器仍可能耗尽有限缓冲。
  临时 IO 不在 render/FMOD 线程；这不是保证任何磁盘/任意分辨率都能满帧。
- 录制 sink 只写视频和事件日志；SFX/BGM 临时 PCM 由最终化阶段的独立 FMOD NRT renderer 产生，
  不阻塞游戏 mixer，也不把音频副本留在 `.working` 目录。
- 编码 PTS/剪辑边界使用与采集匹配的 tick 映射，这是时间换算，不是额外筛选。
  原始 GPU 提交时间、首帧音频原点不改写。
  `NativeCaptureSession.DeliveryStatistics` 报告订阅损失，`Statistics` 报告 native 损失；排空后写 capture report。
  `EncoderInputFps` 是送入编码器的帧率，不是声称最终 MP4 的独立画面帧率；后者必须实际解码核对。
- 当前仍是单一采集、多 sink 独立编码，不声称已共享两条压缩码流。

### 自动存读档的画面边界

- 切面协程晚于 QolHud.Update 结束时，在同一次 Engine.Update 末尾完成录像切面状态并安排保存，
  不再为等待下一次 HUD 更新额外放行一个玩家物理步。
- 保存按 `Boundary → Indicator → Save/Cloning → Clean → ResumeFrame` 推进，整个过程跳过游戏更新。
  Boundary 是保存姿态第一次干净呈现的 end-exclusive 边界；不把保存 UI 的首帧当作姿态边界。
- 内部死亡恢复同样冻结更新；恢复实体后不等待 QolHud.Update 重新开启分支，而是在冻结状态下
  请求两路 sink 的恢复关键帧，按各自实际接受的源帧时间戳开启分支，再放行物理更新。
  捕获尚未结束的死亡音效在新分支之前停止。没有有效存档的死亡仍使用普通复活流程。
- 队列延迟不会选择旧帧；恢复首个物理步不使用保存/读档耗时，首次恢复绘制前最多放行一个物理步，
  避免 fixed-step catch-up 连跑多步。普通绘制也可解除此补步门，停止采集不会造成更新死锁。
  独立恢复槽不改用户槽与计时器标记，
  普通死亡的时间和死亡次数继续累计，不把统计回滚来伪装连续。
- MotionSmoothing 高清镜头会在 SRT 回调后创建未初始化的插值状态。内部 load 完成后只初始化
  Value/PushSprite 历史，不运行物理更新；恢复首帧不从旧尝试的时间戳外推镜头。
  自动存读档暂停期间也阻止其 UpdateAtDraw 更新背景/粒子，绘制和保存指示器仍正常运行。
  开始保存和首次跳过恢复补步时，把显示坐标固定在最新物理样本，不改变实体、速度历史或用户插值设置，
  避免保留旧的显示姿态或跨保存耗时外推。
  适配器不切换用户的 MotionSmoothing 设置，不影响普通手动 SL；缺失/不兼容时日志提示并安全跳过。

### 手动 SpeedrunTool 死亡优先与多版本录像分支

- 手动档只影响死亡恢复选择；录制开始、切面、复活点变化始终按正常条件排队自动保存。
  当前选中非 TAS 用户槽已保存且 SRT 开启死亡自动读档时，死亡流程完全交给 SRT，不加速 End、不改变其设置。
  否则在匹配当前 Level/章节/房间/复活点/录制源时使用当前分支的内部版本；未选中的手动槽不全局禁用自动恢复。
- 每次自动保存分配新命名槽；当前上下文、各复活点最新版本、手动槽引用共同保留历史版本。
  手动槽绑定 `RecordingTimelineSnapshot.Copy()` 的只读前缀、复活前缀、源身份和 `RecoveryVersionId`。
  相同复活点不同走法不覆盖仍被手动档引用的历史游戏快照；读取旧槽同时恢复旧视频前缀与其自动档上下文。
  覆盖/清槽只更新可达引用，不改变当前分支、不发起保存/加载；失去全部引用的旧内部版本在安全空闲时清理。
- beforeSave/beforeLoad 关闭两路录像分支，操作回调只暂存数据。实际 `SaveStateImpl/LoadStateImpl` 成功返回才提交，
  内部操作还必须等 pre-clone 成功。死亡先标记待切分支，正常复活时才使用复活前缀；成功 SL 则恢复精确存档前缀。
  手动 load 不排队自动保存，也不应用内部游戏冻结门。
- 完整录像源持续追加写入磁盘 `.working/<区域>`，所有分支复用同一份 MKV 和事件日志；不因删槽提前清源文件。
  内存主要保存编码/采集缓冲、前缀引用和 SRT 游戏快照，而不是整段视频。完整录制结束后沿用导出完成后清理策略。
  恢复拒绝不匹配的源文件（包括空前缀和复活前缀）；无可用录像历史的旧会话存档从新前缀开始，不伪造无缝连接。
- SRT 操作结束且玩家可录制后的第一次 source presentation 请求关键帧并重新打开录像分支，
  还需等待 `level.Wipe` 和暂停菜单消失；附加进度 UI 的 Present 不算游戏 presentation。
  排除 SL 等待期间的片段，停止死亡音效尾音；有效存档恢复优先硬切，无有效录像前缀的 load 不冒充无缝恢复。
  此手动路径不等待 GPU/sink 接收确认，不为录像干预 SRT 游戏时序；与内部存读档的冻结确认门不同。

### SRT 阻塞进度界面隔离

- 可选适配器 hook `BusyIndicator.TryPresent`，在第一次绘制之前进入可嵌套、异常安全的线程本地排除作用域。
  首次提示早于 SRT 的 Saving/Loading 状态与 beforeSave/beforeLoad 回调，不能仅靠这些状态过滤。
- SDL GL swap 与 DXGI Present 在采集入口跳过辅助 UI：不提交 GPU readback、不占用限帧筛选、不推进存读档恢复门。
  已提交的正常帧仍按原始时间戳投递；不重设共享音视频时钟，不做下游二次筛帧。
- 清档、GC、pre-clone 等无存读档回调的提示也封口录像分支，正常游戏画面恢复后续接，避免只是丢弃 UI 却留下长冻结帧。
  嵌套提示不会重新标记已挂起的 load 为无缝；只有有效保存前缀才允许 saved-state 硬切。
- `BusyIndicator.Begin/Wait` 可用时，保存边界排除后在一个阻塞更新中复用 SRT 原生保存/pre-clone 提示，
  不再绘制自己的保存 HUD；不可用时保留原提示/异步等待门。辅助 Present 始终不能推进干净恢复帧计数。
  只有过时内部版本的后台清理提示静默；用户手动操作的提示和游戏时序保持原样。

### SFX 暂停、叠加与剪辑边界

- FMOD 时间戳使用持续运行的 master mixer DSP clock，而不是随 gameplay bus 暂停的局部时钟。
  后者会让恢复后的 SFX 时间戳提前，形成百毫秒级错位；不能靠 native 连续 PCM 计数补偿掉这个误差。
- 内部保存到达视频 Boundary 时才暂停音效；保存/读档后的 Clean 等待不提前恢复音效，
  请求第一张保留画面时才恢复。Studio pause/resume 命令会 flush，取消和失败路径仍解除暂停。
- 离线 FMOD 输出与 BGM 使用浮点余量，所有贡献相加后在送入 AAC 时限幅，避免逐次限幅破坏叠加/相消。
  有效硬切处若存在异常波形阶跃，仅在两侧各最多 1ms 做 SFX 去爆音；不借用被删死亡区间的样本，
  不改视频时长或加入画面 crossfade。正常连续波形、纯 metadata 分段不动，独立 BGM 在此步骤后混入。
  旧的混合 BGM sidecar 无法分离时不做这项 SFX 去爆音，避免误伤音乐。

### 死亡回放快速最终化

- 只有死亡回放任务设置 `prefer_video_copy`；旧 JSON 默认为 false，完整录像仍走原剪辑器。
    managed bridge 保留旧公开方法签名。ABI 9 新增按源时间戳请求关键帧的内部入口。
- 连续、无冻结帧编辑的 H.264 范围可直接复制视频包；相邻 room/music metadata 片段只对音频保留分段。
  通过 MKV 索引向前一个关键帧 seek，最多缓存 32 MiB 预滚；MP4 edit list 隐藏负时间戳的解码依赖，
  最终音视频 remux 保留 edit list。可见帧不会回退到关键帧，时间量化误差不超过一帧。
- 保存暂停造成的同源顺序多范围，在每个内部起点都有关键帧且要求硬切时仍可复制。
  需要 crossfade、重排帧、无时间戳、内部起点无关键帧、索引异常、其他编码格式或复制失败自动回退；
  不为快速保存修改用户的冻结帧/BGM 设置，不在游戏线程上做最终化。
- 快速路径复用的是**同一个死亡录制 sink 已编码的数据**，不是与完整录像共享编码器。
  仍需等待 capture stop/drain、既有音频重构/AAC 编码与落盘；这不是即时内存播放器或零延迟承诺。

## GPU / 平台边界

- **不是三端都必然使用 OpenGL**。SDL 管窗口，不统一各 renderer 的呈现/读回 API。
- Windows：实现 OpenGL 和 D3D11。D3D11 是 SDL/FNA 路径实际调用的 DXGI Present，
  不能用 SDL_GL_SwapWindow 冒充支持。只接受 SDR RGBA8/BGRA8 swapchain。
- Linux/macOS：保留 SDL OpenGL 3.2+ PBO/fence 路径；这次没有 Linux/macOS 真机运行验证。
- Vulkan、Metal、SDL_GPU 尚未实现。要支持实际使用这些 renderer 的版本/配置，才需要增加对应 GPU readback 后端；不能标为已支持。
- Android：代码路径预留 GLES3，但 SDL/JNI 生命周期、ARM hook、打包及真机均未验证。

GL 在 swap **之前**提交 readpixels，后续帧用 timeout=0 的 fence 检查；保存/恢复所有修改的 GL pack/read 状态。
D3D11 在 Present 前 CopyResource/End(query)，后续帧 GetData(DONOTFLUSH) 仅 S_OK 才 Map(DO_NOT_WAIT)。
不调用阻塞式 ReadBackbuffer，不在 worker 上调用 immediate context，不保留 swapchain backbuffer 引用跨 resize。
CPU 拷贝发生在已完成的映射上；像素转换、分发、编码在 worker。每帧最多 64 MiB。

DXGI hook 延迟到首个像素消费者出现后的 game update，避免 mod load 的探测设备干扰 Steam overlay 初始化。
使用 vtable native shim，不重写 DXGI Present 的函数 prologue；原函数直接调用。
卸载先关闭 managed callback、排空在途 callback，再释放回调资源；不会覆盖后来安装的第三方 vtable hook。
如果第三方保留了 shim 链，则拒绝无重启重装，避免形成递归链。

安装器不选择 renderer；只迁移删除旧安装器加的那段带专用注释的 `--graphics OpenGL`，
原配置先备份到 `.work`，用户自行写的参数不删除。

### 本机 Everest 对应的默认后端（2026-09-09 核查）

本机 `FNA3D.dll` 的 Git blob SHA 为 `46c82820493b98ce5e5354f4ddb06fd51bc4bf60`，
匹配 Everest 6487 使用的 Everest-libs `591f7c12fcb4e8fda9ef5ef1b331b5ed40d3fb1f` 中的 Windows x64 文件。
同一制品树包含 Linux/macOS 的 FNA3D。追溯二进制提交到 libs 源码 `9136b4e0545853f30ff8c80a6272abafcf96df6f`，
FNA `a5920865ab28dcd9d27fca22e03f2658e804b07b`，最终 FNA3D `2a6f8586c8d032da18f707eb340bfbef26d1ff8b`。

该 revision 编译 OpenGL、Vulkan；D3D11 仅 Windows/显式 DXVK-native 构建启用。
默认探测顺序 **D3D11 → OpenGL → Vulkan**，`FNA3D_FORCE_DRIVER` 可以覆盖。
因此这版默认 Windows D3D11，Linux/macOS OpenGL；macOS 的可选 Vulkan 经包里的 MoltenVK 使用 Metal，
不是本版默认 native Metal renderer。这里是制品/源码核验，Linux/macOS 尚未真机验证。
不同 FNA 更新、启动器覆盖、vanilla/Android 移植版本必须重新检测，不能按操作系统硬编码。

可复核的一手来源：
- [Everest 6487 对应源码树](https://github.com/EverestAPI/Everest/tree/d72e94f4b9e62b91cbdea674587ed39d53de9550)
- [Everest-libs 二进制制品](https://github.com/EverestAPI/Everest-libs/tree/591f7c12fcb4e8fda9ef5ef1b331b5ed40d3fb1f)
- [三平台构建参数及 MoltenVK 打包](https://github.com/EverestAPI/Everest-libs/blob/9136b4e0545853f30ff8c80a6272abafcf96df6f/.github/workflows/build-libs.yml)
- [FNA3D 实际 revision 的后端选择](https://github.com/FNA-XNA/FNA3D/blob/2a6f8586c8d032da18f707eb340bfbef26d1ff8b/src/FNA3D.c)
- [该 revision 的编译开关](https://github.com/FNA-XNA/FNA3D/blob/2a6f8586c8d032da18f707eb340bfbef26d1ff8b/CMakeLists.txt)

# 共享采集架构（native ABI 7）

## 数据路径

```text
SDL/FNA 呈现：按真实后端路由，不更改游戏 renderer
  OpenGL: SDL_GL_SwapWindow native hook → 3 个 PBO + GLsync
  Windows D3D11: DXGI Present vtable shim → 3 个 staging texture + event query
        ↓ 后续帧非阻塞检查 GPU 完成；保持原始提交时间戳
        ↓ 有界 CPU 队列（3 帧）；worker 转为 top-down BGRA8

FMOD: gameplay_sfx / ui_sfx / music 各一个 pass-through DSP
        ↓ 原始 interleaved float PCM、声道/采样率、bus、DSP clock
        ↓ 每 bus 一个预分配 SPSC ring（64 块）；mixer 不拿 worker 的锁
        ↓ 唯一 source worker

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
             视频/SFX 剪辑时间线 + 独立 BGM 时间线 → FFmpeg 输出
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
        // DspClock、TimestampNanos。1=游戏音效，2=UI音效，3=BGM。
        // 原始多声道数据保留，不在 callback 前混成 stereo。
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
3 帧、256 PCM 块、256 音乐事件。callback 内不能直接操纵 game/GL/D3D/FMOD 对象，
也不能同步等待自身 Completion。慢消费者、抛异常的消费者不影响其他 callback。
数组在发布后不复用，可保留；只读契约不允许通过 unsafe/MemoryMarshal 修改共享数据。

性能敏感的逐帧处理（内置录制器也使用它）可改用 `CaptureSource.SubscribeBorrowed(...)`。
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
- `run.mkv.sfxchunks`：游戏音效及可选 UI 音效，**不含 BGM**。
- `run.mkv.bgmchunks`：独立 music bus 原始 PCM，包含 FMOD 实际混出的主/alt 音乐及过渡。
- `run.mkv.music.jsonl`：版本头、初始主/alt 状态、时间戳事件、完成标记。

PCM 文件保持 `MQOLAUD1` 格式及原始声道信息；只在导出混音时将 FMOD 标准
1/2/4/5/6/8 声道折叠为双声道。中心/环绕分配到左右，LFE 不加入 stereo。
不同 bus 的声道数可以不同；同一录制中采样率改变目前明确报错，不做隐式重采样。
旧版本只有 `.sfxchunks` 的文件仍支持按 bus id 分离并使用旧 clip 元数据。

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

## 录制启动和过载

- 每个 sink 的硬件编码器在首帧确定分辨率后创建。初始化选择/耗时写入日志。
- 不再用 3 帧覆盖队列丢掉冷启动期间的画面。正常时保留少量 raw BGRA；队列积压时，
  sink worker 做可逆空间差分 + Zstd level 1 无损压缩，encoder worker 按 FIFO 解压消费。
- 每 sink 排队 payload 最多 **192 MiB**，数量最多 `queue_capacity + 5 * fps`；任一达到就拒绝新帧并计数。
  不是无限 RAM，也不是承诺能缓存任意内容的 5 秒。上限不含正在处理的帧、压缩 scratch、codec/GPU 内存。
  用完即释放；稳态不做这次压缩。日志报告峰值帧数/字节与 overflow。
- 音频 sink 入队发生在 callback worker，不是 mixer，已移除两层旧的 try-lock 丢块逻辑。
  音频磁盘写入在独立线程、锁外进行；native 和订阅缓冲各容纳 256 块，覆盖 source 三个
  64-slot ring 恢复后的突发批次。native 每 sink 最多 16 MiB PCM buffer 容量，不是无限增长。
- FPS 统一使用最近 tick 量化，避免 60-Hz 来源的微小抖动被向下取整误判成重复帧。
  最大量化误差半个目标帧；原始采集时间戳、音频原点和 PCM 时钟不改写。
- encoder 重用输入 AVFrame，并在覆盖 converted frame 前 make-writable，防止异步 codec 仍引用旧像素。
- `NativeCaptureSession.DeliveryStatistics` 报告进入 native 之前的订阅丢帧/PCM/音乐事件/异常；
  `Statistics` 报告 native 队列损失。不能仅看后者就宣称端到端无损。
- 持续编码/磁盘速度低于输入速度时，任何有限缓冲最终都会满。后续可用相同配置共享编码、
  更低分辨率/帧率或不同 encoder 减负；当前仍是单一采集、多 sink 独立编码，不伪称已经共享码流。

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

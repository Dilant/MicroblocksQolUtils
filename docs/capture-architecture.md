# 共享采集架构（native ABI 6）

## 数据路径

```text
SDL/FNA 呈现：按真实后端路由，不更改游戏 renderer
  OpenGL: SDL_GL_SwapWindow native hook → 3 个 PBO + GLsync
  Windows D3D11: DXGI Present vtable shim → 3 个 staging texture + event query
        ↓ 后续帧非阻塞检查 GPU 完成；保持原始提交时间戳
        ↓ 有界 CPU 队列（3 帧）；worker 转为 top-down BGRA8

FMOD: gameplay_sfx / ui_sfx / music 各一个 pass-through DSP
        ↓ 原始 interleaved float PCM、声道/采样率、bus、DSP clock
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
3 帧、32 PCM 块、256 音乐事件。callback 内不能直接操纵 game/GL/D3D/FMOD 对象，
也不能同步等待自身 Completion。慢消费者、抛异常的消费者不影响其他 callback。
数组在发布后不复用，可保留；只读契约不允许通过 unsafe/MemoryMarshal 修改共享数据。

`DroppedFrames`、`DroppedAudioChunks`、`DroppedMusicEvents`、`CallbackErrors` 是消费者统计；
`CaptureSource.DroppedAudioChunks` 是源端 PCM 溢出/争用统计。
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
3. 同一音乐段跨视频剪切点时，BGM 读取游标按输出时长连续前进，不跳到下个画面的源时间。
4. 保留画面内的切歌、停止、参数变化会单独切分音乐时间线，不必强迫视频生成新的片段。
5. 跨过被删区间内的重启/切歌等变化时，使用新的音乐段，不误接旧实例。

关闭重构时仍保留独立源文件，只在导出阶段让 BGM 跟随视频裁切。
静态外部 BGM 映射是可选替换功能，不能等价重放 FMOD 动态参数、alt 叠加及 DSP 效果；
需要保留游戏实际动态音乐时使用采集到的 BGM，不配置静态替换映射。
剪辑不能凭空生成未采到的音乐，PCM 丢块仍按时间戳表现为缺口。

## GPU / 平台边界

- **不是三端都必然使用 OpenGL**。SDL 管窗口，不统一各 renderer 的呈现/读回 API。
- Windows：实现 OpenGL 和 D3D11。D3D11 是 SDL/FNA 路径实际调用的 DXGI Present，
  不能用 SDL_GL_SwapWindow 冒充支持。只接受 SDR RGBA8/BGRA8 swapchain。
- Linux/macOS：保留 SDL OpenGL 3.2+ PBO/fence 路径；这次没有 Linux/macOS 真机运行验证。
- Vulkan、Metal、SDL_GPU 尚未实现，必须增加各自 GPU readback 后端；不能标为已支持。
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

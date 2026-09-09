# Capture validation — 2026-09-09 follow-up

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

设置本机 `CELESTE_ROOT`、`FFMPEG_DIR`、`LIBCLANG_PATH`、Rust/.NET PATH，TEMP/TMP 指到 `.work`：

```powershell
$env:MQOL_TEST_FFMPEG = '1'
$env:MQOL_TEST_D3D11 = '1'
cargo test -p microblocks-qol-native --features ffmpeg --lib -- --test-threads=1
dotnet run --project Tests/Capture/Capture.csproj -c Release
# 集成测试另外设置 MQOL_NATIVE_PATH 到 release DLL，MQOL_TEST_OUTPUT 到 .work 目录
# 并确保 SDL/FMOD/FFmpeg DLL 可加载。
dotnet run --project Tests/Capture.Integration/Capture.Integration.csproj -c Release
```

真实游戏 smoke：仅为该次启动设置 `MICROBLOCKS_QOL_CAPTURE_SMOKE_OUTPUT` 到 `.work/*.mkv`。
等 Overworld/Level 加载后开始，写 `.passed` 或 `.failed`；不要把这个环境变量永久写入 Steam/系统。

## 未证明的部分

最新版 native 已通过 Linux x64、macOS x64、Android arm64 的无 FFmpeg cargo check；不等于真实窗口、驱动、音频、FFmpeg 打包测试。
Metal/Vulkan/SDL_GPU 没有实现。D3D11 HDR/MSAA swapchain、其他 GPU/overlay 组合没有普遍兼容性保证。
游戏之外直接 native FMOD 命令的一帧内中间状态可能不能被 managed observer 看见。
静态 BGM 映射不能重放动态 FMOD 音乐；新独立 PCM/事件路径应作为保真来源。

# 录制内部恢复槽

录制设置中的“录制时切面自动保存（保证视频连续性）”默认开启，也能在 Everest 原生模组设置中关闭。
仅完整录像实际录制中触发，包括手动录制；仅开启死亡回放、未开始录制或录制启动失败时不触发。

## 独立槽与时间线

- 使用独立命名的内部 SpeedrunTool 槽，不占用十个用户 UI 槽，不覆盖用户存档。
  每次操作后恢复用户原来选中的槽；必须先等待内部异步 pre-clone 完成，避免后台线程读错槽。
- 内部槽保留在 SRT 槽字典，参与场景和保存回调重初始化。清全部用户槽时暂时摘下内部槽，
  然后重新放回；清当前用户槽自然不会碰到它。
- 切面、录制开始、复活点变化时保存。本房间有手动档时优先交由 SRT 恢复；
  其他房间的手动档不阻止新面自动保存，也不会被覆盖。手动读档本身不排队自动保存。
  清档不立即补存、不恢复过时内部档，仍等下一次正常触发；清档等待从视频中排除。
- 深克隆绝不在 `QolHud.Update`、死亡 hook 或切面协程内部执行；统一在场景更新完成后处理。
  暂停、过场、冻结、复活动画及 SRT 忙碌时等待；死亡/场景变化会取消未完成的存档请求。
  停止录制、关闭功能或退出关卡后清理内部存档和其 FMOD 资源。

## 保存时真正暂停游戏

- 稳定帧排队后，在 `Engine.Update` 入口拦住游戏、实体、物理与输入更新，不只是停录像或事后删掉卡顿。
  先呈现“正在保存…”提示，再在主线程执行场景存档；仅 SRT 自带的 pre-clone 工作继续在其后台线程执行。
- 等 pre-clone 的期间继续渲染提示，不运行游戏逻辑，也不把内部槽提前切回用户槽。
  完成后呈现两帧无提示画面，在请求第一张保留帧时恢复音频；两路录制确认接收后放行更新，首步不接受保存积累的大 delta。
- 两路录像都在保存姿态的 Boundary 帧封口，在干净画面的呈现时刻无渐变续接。排除的仅为**游戏已暂停的等待**，
  不用剪辑来掩盖游戏从 A 到 B 的漏帧。存档回调仍能保存已封口的时间线。
- SRT 进度界面额外调用的 Present 不参与采集或推进暂停阶段。内部槽跳过重复的 SRT 提示，
  使用已有的保存动画，避免重复 UI 和同步 backbuffer 读回；手动操作的提示仍正常显示。
- 剪辑边界使用与视频/PCM 同源的时钟，而不是滞后的 GPU/编码投递统计。
  暂停 BGM 使用可写入音乐日志的 instance pause/resume，避免后期连续混音重新读入保存期间的静音。
- 新一轮游玩开始/恢复录像分支时，只立即结束上一轮的普通死亡和 predeath one-shot 并刷新 FMOD 命令。
  不停止整个音效总线、不静音新一轮的动作音效；金草莓失败继续录制时也不提前切断声音。

## 死亡接管细节

- `PlayerDeadBody.End` 的高优先级 hook 先于 SRT 自动读档运行，只拦截普通同房间复活。
  验证相同 Level、章节、房间、复活点和录制文件；没有有效内部槽时走原流程。
- 金草莓重开章节、自定义 `DeathAction`、PlayerSeeker、切面中、跨房间或旧录制的状态不接管。
  `DeathAction == null` 或显式的 `level.Reload` 属于正常复活。
- hook 只取消死亡协程并排队；帧末先结束死亡回放，再读内部槽并恢复录像时间线。
  失败时只回退一次原死亡流程，不把玩家留在死锁状态；离开场景后不会调用旧场景的回调。
- 内部恢复不依赖用户的 `AutoLoadStateAfterDeath` 设置。正常死亡的会话/总计/章节时间和死亡数
  在恢复后保留，即使用户手动 SL 设置启用了 `SaveTimeAndDeaths`。

## 无标记存读档

内部操作使用普通 `SaveStateImpl(false, ...)` / `LoadStateImpl(false, ...)`，不假冒 TAS，
也不修改 SpeedrunTool 全局设置。仅在本次内部操作中跳过新增计时器/金草莓标记及存读档动画/冻结，
同步完成恢复，避免切回用户槽后旧 wipe 回调读取错误的 `StateManager.Instance`。
已有手动 SL 标记不清除；用户手动存读档的原有标记和行为不变。

缺失/禁用 SRT、TAS 运行中或选中 TAS 存档时跳过。反射签名不匹配则撤销 hook；IL 模式不匹配
则记录具体方法并禁用整套内部恢复，已安装的补丁保持原有 SRT 行为，不回退到覆盖用户槽或有标记的自动保存。
同时匹配原始 DLL 的 `call` 和 Everest 重链接后的 `callvirt`。在 Everest Ultra 延迟/并行安装 hook 时，
必须等标记、保存、加载三个 IL 补丁全部应用成功后才允许内部操作；不兼容分支不会向启动事务抛出异常。

## 自动化验证

在 worktree 执行，TEMP/TMP 和探针输出都指定到 `.work`。需要 .NET 8 和本机 Celeste/Everest 引用：

```powershell
dotnet build Source/MicroblocksQolUtils.csproj -c Release
dotnet run --project Tests/Recording -c Release
dotnet run --project Tests/Recording.Interop -c Release -- C:/SteamLibrary/steamapps/common/Celeste .work/interop
dotnet run --project Tests/Recording.Interop -c Release -- C:/SteamLibrary/steamapps/common/Celeste .work/interop --cached
dotnet run --project Tests/Capture -c Release
```

`Recording` 使用假游戏/SRT 对象，但实际执行 MonoMod 运行时 hook 和 ModInterop 回调：
覆盖专用槽隔离、异步 pre-clone 选槽一致性、多次 SL 后清槽、死亡接管、统计保留、时间线恢复、
历史标记、各种延迟/失效条件、失败回退、SRT 后安装的死亡 hook 优先级、卸载与部分 hook 失败回滚。

`Recording.Interop` 从本机 `SpeedrunTool.zip` 解出 DLL 到临时目录，检查生产设置默认值，
并对**真实安装的 DLL** 安装/卸载内部槽及无标记存读档 hook，不启动游戏、不操作玩家存档。
`--cached` 改用游戏实际加载的 `Mods/Cache/SpeedrunTool.SpeedrunTool.dll`（需先启动过游戏生成缓存），
避免只验证原始程序集、漏掉重链接指令变化。两种 DLL 都测试立即安装、Ultra 延迟并行提交、
存/读分支不匹配时安全禁用、失败后重新加载及取消尚未应用的 hook。
已验证本机 SpeedrunTool 3.27.21 与 Everest 6487 Ultra 的原始和重链接 IL 兼容。

自动化测试不等于实际游戏内完整切面→死亡→手动 SL→清槽→死亡→导出视频验收。
游戏内还应检查开启/关闭开关、金草莓重开章节不受影响，并试听导出视频的衔接。

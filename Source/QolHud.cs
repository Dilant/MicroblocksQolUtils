using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

[Tracked]
public sealed class QolHud : Entity {
    public QolHud() {
        Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate | Tags.TransitionUpdate;
        Depth = -1_000_000;
    }

    public override void Update() {
        base.Update();
        if (!MicroblocksQolUtilsModule.Settings.Enabled) return;
        MiaoNetBridge.Update(Scene as Level);
        if (Scene is Level level) {
            UpdateMiniMapZoom();
            UpdateRecordingBindings(level);
            AutoRecorder.Update(level);
        }
    }

    public override void Render() {
        base.Render();
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.Enabled) return;

        float miniMapBottom = Scene is Level level ? MiniMapRenderer.Render(level) : 0f;
        RecordingHudRenderer.Render(miniMapBottom);
        RenderProfilerStatus();

        if (settings.ShowFps) {
            bool dualFps = settings.ShowPhysicalAndRenderFps && MotionSmoothingBridge.Enabled;
            string text = dualFps
                ? $"物理 {FrameRateCounter.PhysicsFps,3:0} FPS  ·  渲染 {FrameRateCounter.RenderFps,3:0} FPS"
                : $"{FrameRateCounter.RenderFps,3:0} FPS";
            if (settings.ShowFrameTime)
                text += $"  ·  {FrameProfiler.LastFrameMilliseconds,5:0.0} ms CPU";
            Vector2 position = new(18f, 16f);
            MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
            Vector2 measured = SystemTtfFont.Measure(text, 0.43f);
            if (settings.HudMaterialSurfaces) {
                MaterialUi.AcrylicSurface(
                    position.X - 10f,
                    position.Y - 7f,
                    measured.X + 20f,
                    measured.Y + 14f,
                    16f,
                    palette.SurfaceHigh * 0.90f,
                    palette.Outline
                );
            }
            SystemTtfFont.Draw(
                text,
                position,
                Vector2.Zero,
                0.43f,
                palette.OnSurface,
                settings.HudMaterialSurfaces ? 0f : 1f,
                Color.Black
            );
            if (settings.EnableFrameProfiler) FrameProfiler.RenderHud(new Vector2(18f, 48f));
        }
    }

    private static void RenderProfilerStatus() {
        ManagedSamplingStage stage = ManagedCpuSampler.Stage;
        ManagedProfileReport? report = ManagedCpuSampler.LatestReport;
        if (stage is ManagedSamplingStage.Idle or ManagedSamplingStage.Failed) return;
        if (stage == ManagedSamplingStage.Complete
            && (report is null || (DateTime.Now - report.CapturedAt).TotalSeconds > 8d)) return;

        string text = stage switch {
            ManagedSamplingStage.WarmingUp => "Profiler 即将开始",
            ManagedSamplingStage.Sampling => $"Profiler 采样中  {ManagedCpuSampler.RemainingSeconds:0.0}s",
            ManagedSamplingStage.Analyzing => "Profiler 正在生成报告",
            ManagedSamplingStage.Complete => "Profiler 报告已生成",
            _ => "Profiler"
        };
        float progress = ManagedCpuSampler.Progress;
        Vector2 measured = SystemTtfFont.Measure(text, 0.38f);
        float width = Math.Max(330f, measured.X + 36f);
        float x = (1920f - width) / 2f;
        float y = 24f;
        MaterialPalette palette = MaterialPalette.FromSeed(new Color(126, 99, 184));
        MaterialUi.AcrylicSurface(x, y, width, 62f, 20f,
            palette.SurfaceHigh * 0.94f, palette.Outline);
        SystemTtfFont.Draw(text, new Vector2(x + 18f, y + 12f), Vector2.Zero,
            0.38f, palette.OnSurface, 0f);
        if (stage is ManagedSamplingStage.WarmingUp or ManagedSamplingStage.Sampling) {
            MaterialUi.RoundedRect(x + 18f, y + 48f, width - 36f, 4f, 2f,
                palette.Outline * 0.36f);
            MaterialUi.RoundedRect(x + 18f, y + 48f, (width - 36f) * progress, 4f, 2f,
                palette.Primary);
        }
    }

    private static void UpdateMiniMapZoom() {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (settings.MiniMapZoomInBinding.Pressed)
            settings.MiniMapZoom = Math.Min(12, settings.MiniMapZoom + 1);
        if (settings.MiniMapZoomOutBinding.Pressed)
            settings.MiniMapZoom = Math.Max(0, settings.MiniMapZoom - 1);
    }

    private static void UpdateRecordingBindings(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (AutoRecorder.IsRecording || AutoRecorder.ManualMode) {
            if (settings.StopRecordingBinding.Pressed)
                AutoRecorder.StopManual(level, save: true);
        } else if (settings.StartRecordingBinding.Pressed) {
            AutoRecorder.StartManual();
        }
    }
}

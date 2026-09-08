using Microsoft.Xna.Framework;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class RecordingHudRenderer {
    private const float ScreenWidth = 1920f;
    private const float Margin = 22f;
    private const float Top = 16f;
    private const float Height = 38f;
    private const float TextScale = 0.44f;

    public static void Render(float miniMapBottom) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        float top = miniMapBottom > 0f ? miniMapBottom + 12f : Top;
        bool showRecording = AutoRecorder.IsRecording
            && (settings.ShowRecordingIndicator || settings.ShowRecordingDuration);
        if (showRecording) {
            RenderRecordingBadge(settings, top);
        }
        if (AutoRecorder.IsFinalizing) {
            RenderFinalizationProgress(settings, showRecording ? top + Height + 8f : top);
        }
    }

    private static void RenderRecordingBadge(QolSettings settings, float top) {
        string duration = settings.ShowRecordingDuration
            ? FormatDuration(AutoRecorder.DisplaySeconds)
            : "";
        Vector2 measured = duration.Length > 0
            ? SystemTtfFont.Measure(duration, TextScale, UiFontWeight.Bold)
            : Vector2.Zero;
        float width = measured.X + (settings.ShowRecordingIndicator ? 52f : 24f);
        float right = ScreenWidth - Margin;
        float left = right - width;

        if (settings.HudMaterialSurfaces) {
            MaterialUi.RoundedRect(left, top, width, Height, Height / 2f, Color.Black * 0.62f);
            MaterialUi.RoundedOutline(left, top, width, Height, Height / 2f, 1.5f, Color.White * 0.18f);
        }

        if (settings.ShowRecordingIndicator) {
            Vector2 center = new(left + 19f, top + Height / 2f);
            bool bright = AutoRecorder.CurrentSeconds % 1.0 < 0.58;
            Color red = new Color(255, 52, 64) * (bright ? 1f : 0.25f);
            MaterialUi.Circle(center, 9f, new Color(255, 40, 52) * (bright ? 0.22f : 0.06f));
            MaterialUi.Circle(center, 6f, red);
        }

        if (duration.Length > 0) {
            SystemTtfFont.Draw(
                duration,
                new Vector2(right - 12f, top + 7f),
                new Vector2(1f, 0f),
                TextScale,
                Color.White,
                1f,
                Color.Black * 0.85f,
                UiFontWeight.Bold
            );
        }
    }

    private static void RenderFinalizationProgress(QolSettings settings, float top) {
        float progress = (float)Math.Clamp(AutoRecorder.FinalizationProgress, 0d, 1d);
        int pending = AutoRecorder.PendingFinalizationCount;
        string text = pending > 1
            ? $"生成{AutoRecorder.FinalizationDescription}  {progress:P0}  ·  还剩 {pending - 1} 个"
            : $"生成{AutoRecorder.FinalizationDescription}  {progress:P0}";
        const float scale = 0.34f;
        Vector2 measured = SystemTtfFont.Measure(text, scale, UiFontWeight.Bold);
        float width = Math.Max(230f, measured.X + 28f);
        float right = ScreenWidth - Margin;
        float left = right - width;
        if (settings.HudMaterialSurfaces) {
            MaterialUi.RoundedRect(left, top, width, Height, Height / 2f, Color.Black * 0.68f);
            MaterialUi.RoundedOutline(left, top, width, Height, Height / 2f, 1.5f, Color.White * 0.18f);
        }
        SystemTtfFont.Draw(
            text,
            new Vector2(left + 14f, top + 6f),
            Vector2.Zero,
            scale,
            Color.White,
            1f,
            Color.Black * 0.85f,
            UiFontWeight.Bold
        );
        float trackWidth = width - 28f;
        MaterialUi.RoundedRect(left + 14f, top + Height - 8f, trackWidth, 4f, 2f,
            Color.White * 0.18f);
        MaterialUi.RoundedRect(left + 14f, top + Height - 8f, trackWidth * progress, 4f, 2f,
            new Color(178, 143, 255));
    }

    private static string FormatDuration(double seconds) {
        long totalSeconds = Math.Max(0L, (long)Math.Floor(seconds));
        long hours = totalSeconds / 3_600L;
        long minutes = totalSeconds / 60L % 60L;
        long remainingSeconds = totalSeconds % 60L;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{remainingSeconds:00}"
            : $"{minutes:00}:{remainingSeconds:00}";
    }
}

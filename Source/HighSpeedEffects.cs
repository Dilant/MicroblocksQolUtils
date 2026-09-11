using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>Gives very fast custom maps readable motion feedback without changing physics.</summary>
public static class HighSpeedEffects {
    private sealed class Sample {
        public Vector2 Position;
        public float Strength;
    }

    private static readonly Dictionary<Player, Queue<Sample>> History = new();

    public static void Load() {
        On.Celeste.Player.Update += PlayerUpdate;
        On.Celeste.Player.Render += PlayerRender;
    }

    public static void Unload() {
        On.Celeste.Player.Update -= PlayerUpdate;
        On.Celeste.Player.Render -= PlayerRender;
        History.Clear();
    }

    private static void PlayerUpdate(On.Celeste.Player.orig_Update orig, Player self) {
        orig(self);
        if (!MicroblocksQolUtilsModule.Settings.HighSpeedEffects || self.Scene is not Level)
            return;

        float speed = self.Speed.Length();
        float threshold = MicroblocksQolUtilsModule.Settings.HighSpeedThreshold;
        float strength = MathHelper.Clamp((speed - threshold) / Math.Max(1f, threshold), 0f, 1f);
        if (!History.TryGetValue(self, out Queue<Sample>? samples))
            History[self] = samples = new Queue<Sample>();
        samples.Enqueue(new Sample { Position = self.Position, Strength = strength });
        while (samples.Count > 12) samples.Dequeue();
    }

    private static void PlayerRender(On.Celeste.Player.orig_Render orig, Player self) {
        if (MicroblocksQolUtilsModule.Settings.HighSpeedEffects && History.TryGetValue(self, out Queue<Sample>? samples))
            RenderTrail(self, samples);
        orig(self);
    }

    private static void RenderTrail(Player player, Queue<Sample> samples) {
        if (samples.Count < 2) return;
        Sample[] points = samples.ToArray();
        float intensity = MicroblocksQolUtilsModule.Settings.HighSpeedEffectIntensity / 100f;
        Vector2 velocity = player.Speed;
        float speed = velocity.Length();
        if (speed < MicroblocksQolUtilsModule.Settings.HighSpeedThreshold) return;

        Vector2 direction = velocity / speed;
        Color tint = Color.Lerp(Color.Cyan, Color.White, 0.35f);
        for (int i = 0; i < points.Length - 1; i++) {
            float age = (i + 1f) / points.Length;
            float alpha = (1f - age) * 0.42f * intensity * MathHelper.Clamp(speed / 1400f, 0.35f, 1f);
            if (alpha <= 0f) continue;
            float radius = 3f + (1f - age) * 5f;
            Draw.Circle(points[i].Position, radius, tint * alpha, 8);
        }

        // A few lines radiating from the player make extreme velocity legible even
        // when the sprite itself is moving too quickly to leave a useful trail.
        float lineLength = MathHelper.Clamp((speed - 500f) * 0.055f, 8f, 72f) * intensity;
        Vector2 start = player.Position - direction * 6f;
        for (int i = 0; i < 3; i++) {
            float spread = (i - 1) * 0.16f;
            Vector2 side = new Vector2(-direction.Y, direction.X) * spread;
            Vector2 end = start - direction * (lineLength * (0.72f + i * 0.14f)) + side * lineLength;
            Draw.Line(start + side * 8f, end, tint * (0.24f * intensity), 1.25f);
        }
    }
}

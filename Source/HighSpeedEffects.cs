using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds (1000+ px/frame) tech and gimmick maps
/// reach, where vanilla effects read as teleportation: an additive motion ribbon
/// in world space, a screen warp driven through vanilla Distort.Anxiety, a
/// shader-free chromatic-aberration + radial-ghost pass on the level buffer, and
/// comic-style speed lines over gameplay. Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const float MaxWarp = 0.30f;

    private sealed class PlayerFx {
        public readonly Vector2[] Positions = new Vector2[TrailSamples];
        public int Count;
        public int Head;
        public float Heat;
        public Vector2 LastDirection = new(1f, 0f);
    }

    // Trail history keyed weakly so respawned players never leak.
    private static readonly ConditionalWeakTable<Player, PlayerFx> Fx = new();

    // Deterministic RNG owned by the render hooks; Calc.Random must stay untouched
    // here or replay/TAS gameplay rolls would depend on the draw rate.
    private static readonly Random Random = new();

    // Distort.Anxiety is shared with vanilla cutscenes (Gondola darkness, seekers).
    // Only steer it while our speed owns a non-zero value; fade it out from the
    // render hook once the player stops updating with a warp-worthy speed.
    private static float ownedAnxiety;
    private static ulong anxietyOwnerFrame = ulong.MaxValue;

    // Debug override driven by the qol_speedfx command: fakes the given speed on
    // the local player so every layer of the effect can be eyeballed in any map.
    internal static float? DebugSpeed;
    internal static float DebugSpeedTimer;

    public static void Load() {
        On.Celeste.Player.Update += PlayerUpdate;
        On.Celeste.Player.Render += PlayerRender;
        On.Celeste.Glitch.Apply += GlitchApply;
        On.Celeste.HudRenderer.RenderContent += HudRenderContent;
    }

    public static void Unload() {
        On.Celeste.Player.Update -= PlayerUpdate;
        On.Celeste.Player.Render -= PlayerRender;
        On.Celeste.Glitch.Apply -= GlitchApply;
        On.Celeste.HudRenderer.RenderContent -= HudRenderContent;
        Fx.Clear();
        DebugSpeed = null;
        if (ownedAnxiety > 0f) Distort.Anxiety = 0f;
        ownedAnxiety = 0f;
    }

    private static QolSettings Settings => MicroblocksQolUtilsModule.Settings;

    private static bool Active => Settings.Enabled && Settings.HighSpeedEffects;

    private static float Intensity => Settings.HighSpeedEffectIntensity / 100f;

    private static void PlayerUpdate(On.Celeste.Player.orig_Update orig, Player self) {
        orig(self);
        PlayerFx fx = Fx.GetOrCreateValue(self);
        if (DebugSpeed is float debugSpeed) {
            DebugSpeedTimer -= Engine.RawDeltaTime;
            if (DebugSpeedTimer <= 0f) DebugSpeed = null;
            fx.Heat = Calc.Approach(fx.Heat, 1f, 8f * Engine.DeltaTime);
            UpdateMotion(fx, self, debugSpeed);
        } else {
            float speed = self.Speed.Length();
            float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
            float target = Active && self.Scene is Level
                ? MathHelper.Clamp((speed - threshold) / threshold, 0f, 1f)
                : 0f;
            fx.Heat = Calc.Approach(fx.Heat, target, (target > fx.Heat ? 10f : 3.5f) * Engine.DeltaTime);
            UpdateMotion(fx, self, speed);
        }

        if (fx.Heat > 0f && Active && Settings.HighSpeedWarp
            && self.Scene is Level level && !level.FrozenOrPaused) {
            Camera camera = level.Camera;
            Distort.AnxietyOrigin = new Vector2(
                MathHelper.Clamp((self.Center.X - camera.X) / 320f, 0f, 1f),
                MathHelper.Clamp((self.Center.Y - camera.Y) / 180f, 0f, 1f));
            ownedAnxiety = MaxWarp * fx.Heat * Intensity;
            Distort.Anxiety = ownedAnxiety;
            anxietyOwnerFrame = Engine.FrameCounter;
        }
    }

    private static void UpdateMotion(PlayerFx fx, Player player, float speed) {
        fx.Positions[fx.Head] = player.Position;
        fx.Head = (fx.Head + 1) % TrailSamples;
        if (fx.Count < TrailSamples) fx.Count++;
        if (player.Speed.LengthSquared() > 1f) fx.LastDirection = Vector2.Normalize(player.Speed);
    }

    private static void PlayerRender(On.Celeste.Player.orig_Render orig, Player self) {
        PlayerFx fx = Fx.GetOrCreateValue(self);
        if (Active && Settings.HighSpeedTrail && fx.Heat > 0f && fx.Count >= 2)
            RenderRibbon(self, fx);
        orig(self);
    }

    private static void RenderRibbon(Player player, PlayerFx fx) {
        float heat = fx.Heat;
        float intensity = Intensity;
        Color edge = Color.Lerp(Color.Cyan, Color.White, 0.3f);
        int newest = (fx.Head - 1 + TrailSamples) % TrailSamples;

        Draw.SpriteBatch.End();
        Level? level = player.SceneAs<Level>();
        if (level is null) {
            GameplayRenderer.Begin();
            return;
        }
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, level.Camera.Matrix);
        Vector2 previous = fx.Positions[newest];
        for (int i = 1; i < fx.Count; i++) {
            Vector2 point = fx.Positions[(newest - i + TrailSamples) % TrailSamples];
            float freshness = 1f - i / (float)fx.Count;
            float alpha = freshness * freshness * 0.45f * heat * intensity;
            if (alpha > 0.003f) {
                float width = 1f + freshness * 5f * (0.5f + 0.5f * heat);
                Draw.Line(point, previous, edge * alpha, width);
                if (freshness > 0.75f)
                    Draw.Line(point, previous, Color.White * (alpha * 0.5f), width * 0.35f);
            }
            previous = point;
        }

        float speed = MotionSpeed(player, fx);
        if (speed > 1f) {
            // Streaks shooting off the sprite keep the direction legible even when
            // the ribbon itself has already left the visible area.
            Vector2 direction = MotionDirection(player, fx, false);
            float streak = MathHelper.Clamp((speed - 400f) * 0.05f, 10f, 90f) * heat * intensity;
            for (int i = 0; i < 3; i++) {
                Vector2 side = new Vector2(-direction.Y, direction.X) * ((i - 1) * 7f);
                Vector2 start = player.Center + side - direction * 8f;
                Draw.Line(start, start - direction * streak * (0.7f + 0.15f * i), edge * (0.20f * heat * intensity), 1.5f);
            }
        }
        Draw.SpriteBatch.End();
        GameplayRenderer.Begin();
    }

    // Runs right after the level buffer is finished (bloom, glitch, foreground):
    // the level render target is still bound, which is exactly where a fullscreen
    // pass can rebuild the image with speed-driven aberration and ghosting.
    private static void GlitchApply(On.Celeste.Glitch.orig_Apply orig, VirtualRenderTarget source,
        float timer, float seed, float amplitude) {
        orig(source, timer, seed, amplitude);
        ReleaseStaleAnxiety();
        if (source != GameplayBuffers.Level || !Active || !Settings.HighSpeedAberration)
            return;
        Level? level = Engine.Scene as Level;
        if (level is null || level.FrozenOrPaused) return;
        Player? player = FindFocusPlayer(level);
        if (player is null) return;
        PlayerFx fx = Fx.GetOrCreateValue(player);
        float heat = fx.Heat;
        float speed = MotionSpeed(player, fx);
        if (heat <= 0f || speed < 1f) return;

        float intensity = Intensity;
        Vector2 direction = MotionDirection(player, fx, true);
        Vector2 focus = player.Center - level.Camera.Position;
        if (SaveData.Instance.Assists.MirrorMode) focus.X = 320f - focus.X;
        float offset = MathF.Max(0.75f, (0.5f + 3f * heat) * intensity);

        GraphicsDevice device = Engine.Instance.GraphicsDevice;
        RenderTarget2D levelBuffer = (RenderTarget2D)GameplayBuffers.Level;
        RenderTarget2D temp = (RenderTarget2D)GameplayBuffers.TempA;
        device.SetRenderTarget(temp);
        device.Clear(Color.Transparent);
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        Draw.SpriteBatch.Draw(levelBuffer, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        device.SetRenderTarget(levelBuffer);
        device.Clear(Color.Transparent);
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        // The channel tints zero out two channels per copy, so three shifted
        // additive copies reassemble the image as true chromatic aberration
        // without needing a shader.
        Draw.SpriteBatch.Draw(temp, direction * offset, new Color(255, 0, 0, 255));
        Draw.SpriteBatch.Draw(temp, Vector2.Zero, new Color(0, 255, 0, 255));
        Draw.SpriteBatch.Draw(temp, -direction * offset, new Color(0, 0, 255, 255));
        // Two enlarged copies centred on the player smear the world outward from
        // the focal point, selling extreme velocity without a blur pass.
        float smear = heat * intensity;
        Draw.SpriteBatch.Draw(temp, focus, null, Color.White * (0.10f * smear), 0f, focus,
            1f + 0.015f * smear, SpriteEffects.None, 0f);
        Draw.SpriteBatch.Draw(temp, focus, null, Color.White * (0.06f * smear), 0f, focus,
            1f + 0.035f * smear, SpriteEffects.None, 0f);
        Draw.SpriteBatch.End();
    }

    private static void ReleaseStaleAnxiety() {
        if (ownedAnxiety <= 0f || Engine.FrameCounter == anxietyOwnerFrame) return;
        // The player no longer updates with warp-worthy speed (death, cutscene,
        // pause); fade our value out so vanilla sequences can reclaim the shader.
        ownedAnxiety = Calc.Approach(ownedAnxiety, 0f, 6f * Engine.RawDeltaTime);
        Distort.Anxiety = ownedAnxiety;
    }

    // Vanilla RenderContent opens and closes its own sprite batch, so drawing in
    // a prefix lands the lines over the gameplay but under every HUD element, on
    // both the buffer and direct-render paths.
    private static void HudRenderContent(On.Celeste.HudRenderer.orig_RenderContent orig, HudRenderer self, Scene scene) {
        DrawSpeedLines(scene as Level);
        orig(self, scene);
    }

    private static void DrawSpeedLines(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedLines || level.FrozenOrPaused) return;
        Player? player = FindFocusPlayer(level);
        if (player is null) return;
        PlayerFx fx = Fx.GetOrCreateValue(player);
        if (fx.Heat <= 0f) return;

        float heat = fx.Heat;
        float intensity = Intensity;
        Vector2 screenDirection = MotionDirection(player, fx, true);
        float baseAngle = MathF.Atan2(screenDirection.Y, screenDirection.X);
        Vector2 center = new Vector2(960f, 540f) - screenDirection * (90f * heat);
        Color tint = Color.Lerp(Color.White, Color.Cyan, 0.25f);
        float strength = heat * heat * intensity;

        HiresRenderer.BeginRender(BlendState.Additive);
        for (int i = 0; i < 24; i++) {
            float angle = i % 10 < 7
                ? baseAngle + MathF.PI + Triangular() * 0.9f
                : (float)(Random.NextDouble() * MathF.PI * 2f);
            float distance = 200f + 340f * NextFloat();
            float length = (160f + 520f * NextFloat()) * (0.6f + 0.4f * heat);
            float alpha = (0.05f + 0.22f * NextFloat()) * strength;
            Vector2 radial = Calc.AngleToVector(angle, 1f);
            Draw.Line(center + radial * distance, center + radial * (distance + length), tint * alpha, 1f + 2f * NextFloat());
        }
        HiresRenderer.EndRender();
    }

    private static Player? FindFocusPlayer(Level? level) {
        if (level is null) return null;
        Player? best = null;
        float bestHeat = 0f;
        foreach (Player player in level.Tracker.GetEntities<Player>()) {
            float heat = Fx.GetOrCreateValue(player).Heat;
            if (heat > bestHeat) {
                bestHeat = heat;
                best = player;
            }
        }
        return best;
    }

    private static float MotionSpeed(Player player, PlayerFx fx)
        => DebugSpeed is float debugSpeed ? debugSpeed : player.Speed.Length();

    private static Vector2 MotionDirection(Player player, PlayerFx fx, bool screenSpace) {
        Vector2 direction = player.Speed.LengthSquared() > 1f ? Vector2.Normalize(player.Speed) : fx.LastDirection;
        if (screenSpace && SaveData.Instance?.Assists.MirrorMode == true) direction.X = -direction.X;
        return direction;
    }

    private static float NextFloat() => (float)Random.NextDouble();

    private static float Triangular() => NextFloat() + NextFloat() - 1f;
}

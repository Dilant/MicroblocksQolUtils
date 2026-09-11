using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds tech and gimmick maps reach, where
/// vanilla effects read as teleportation: an additive motion ribbon in world
/// space, restrained chromatic aberration on the level buffer, and a water-wake
/// space crush — the world along the player's recent path is pushed aside
/// through vanilla's displacement map and ripples outward like a boat's wake.
/// Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const int CrushTextureWidth = 128;
    private const int CrushTextureHeight = 64;

    private sealed class PlayerFx {
        public readonly Vector2[] Positions = new Vector2[TrailSamples];
        public readonly float[] Speeds = new float[TrailSamples];
        public int Count;
        public int Head;
        public float Heat;
        public Vector2 LastDirection = new(1f, 0f);
    }

    // Trail history keyed weakly so respawned players never leak.
    private static readonly ConditionalWeakTable<Player, PlayerFx> Fx = new();

    private static Texture2D? crushTexture;
    private static Texture2D? waveTexture;

    // Debug override driven by the qol_speedfx command: feeds the given speed to
    // every layer while the player moves for real, so the wake follows the
    // player's actual path.
    internal static float? DebugSpeed;
    internal static float DebugSpeedTimer;

    public static void Load() {
        On.Celeste.Player.Update += PlayerUpdate;
        On.Celeste.Player.Render += PlayerRender;
        On.Celeste.Glitch.Apply += GlitchApply;
        Everest.Events.Level.OnLoadLevel += OnLoadLevel;
    }

    public static void Unload() {
        On.Celeste.Player.Update -= PlayerUpdate;
        On.Celeste.Player.Render -= PlayerRender;
        On.Celeste.Glitch.Apply -= GlitchApply;
        Everest.Events.Level.OnLoadLevel -= OnLoadLevel;
        Fx.Clear();
        DebugSpeed = null;
        crushTexture?.Dispose();
        crushTexture = null;
        waveTexture?.Dispose();
        waveTexture = null;
    }

    private static QolSettings Settings => MicroblocksQolUtilsModule.Settings;

    private static bool Active => Settings.Enabled && Settings.HighSpeedEffects;

    private static float Intensity => Settings.HighSpeedEffectIntensity / 100f;

    // Effects begin just below the threshold and saturate at 3x, so the wake is
    // already faintly visible at the threshold itself.
    private static float HeatCurve(float speed, float threshold)
        => MathHelper.Clamp((speed - 0.7f * threshold) / (2.3f * threshold), 0f, 1f);

    private static void OnLoadLevel(Level level, Player.IntroTypes intro, bool fromLoader) {
        if (level.Tracker.GetEntity<SpaceCrushHook>() is null) level.Add(new SpaceCrushHook());
    }

    // Carries the displacement hook; vanilla's DisplacementRenderer collects one
    // callback per hook component while filling the displacement buffer.
    [Tracked] // Tracker.GetEntity<> throws for types that are not registered as tracked.
    private sealed class SpaceCrushHook : Entity {
        public SpaceCrushHook() => Add(new DisplacementRenderHook(RenderCrush));

        private void RenderCrush() => RenderSpaceCrush(Engine.Scene as Level);
    }

    private static void PlayerUpdate(On.Celeste.Player.orig_Update orig, Player self) {
        orig(self);
        PlayerFx fx = Fx.GetOrCreateValue(self);
        if (self.Speed.LengthSquared() > 1f) fx.LastDirection = Vector2.Normalize(self.Speed);
        float speed = DebugSpeed is float debugSpeed ? debugSpeed : self.Speed.Length();
        if (DebugSpeed is float) {
            DebugSpeedTimer -= Engine.RawDeltaTime;
            if (DebugSpeedTimer <= 0f) DebugSpeed = null;
        }
        float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
        float target = Active && self.Scene is Level ? HeatCurve(speed, threshold) : 0f;
        fx.Heat = Calc.Approach(fx.Heat, target, (target > fx.Heat ? 10f : 3.5f) * Engine.DeltaTime);
        PushSample(fx, self.Position, speed);
    }

    private static void PushSample(PlayerFx fx, Vector2 position, float speed) {
        fx.Positions[fx.Head] = position;
        fx.Speeds[fx.Head] = speed;
        fx.Head = (fx.Head + 1) % TrailSamples;
        if (fx.Count < TrailSamples) fx.Count++;
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
            Vector2 direction = player.Speed.LengthSquared() > 1f ? Vector2.Normalize(player.Speed) : fx.LastDirection;
            float streak = MathHelper.Clamp((speed - 300f) * 0.05f, 8f, 80f) * heat * intensity;
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
        Vector2 direction = ScreenDirection(player, fx);
        Vector2 focus = player.Center - level.Camera.Position;
        if (SaveData.Instance.Assists.MirrorMode) focus.X = 320f - focus.X;
        // Deliberately restrained: aberration is seasoning, not the dish.
        float offset = MathF.Max(0.5f, (0.2f + 1.1f * heat) * intensity);

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
        Draw.SpriteBatch.Draw(temp, focus, null, Color.White * (0.05f * smear), 0f, focus,
            1f + 0.010f * smear, SpriteEffects.None, 0f);
        Draw.SpriteBatch.Draw(temp, focus, null, Color.White * (0.03f * smear), 0f, focus,
            1f + 0.022f * smear, SpriteEffects.None, 0f);
        Draw.SpriteBatch.End();
    }

    // The wake: two layers per path segment. The fresh crush strip squeezes the
    // world aside right behind the player and closes again quickly; the wave lobe
    // starts narrow near the path and spreads outward as the segment ages, like
    // ripples propagating away from a boat's track.
    private static void RenderSpaceCrush(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedWarp || level.FrozenOrPaused) return;
        float intensity = Intensity;
        float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
        Texture2D crush = GetCrushTexture();
        Texture2D wave = GetWaveTexture();

        foreach (Player player in level.Tracker.GetEntities<Player>()) {
            PlayerFx fx = Fx.GetOrCreateValue(player);
            if (fx.Heat <= 0f || fx.Count < 2) continue;
            for (int i = 0; i < fx.Count - 1; i++) {
                Vector2 newer = fx.Positions[(fx.Head - 1 - i + TrailSamples * 2) % TrailSamples];
                Vector2 older = fx.Positions[(fx.Head - 2 - i + TrailSamples * 2) % TrailSamples];
                Vector2 delta = newer - older;
                float length = delta.Length();
                if (length < 2f) continue;
                Vector2 direction = delta / length;

                float speed = fx.Speeds[(fx.Head - 1 - i + TrailSamples * 2) % TrailSamples];
                float speedFactor = MathHelper.Clamp(speed / threshold, 0f, 2.5f);
                if (speedFactor <= 0.4f) continue;
                float age = i / (float)(fx.Count - 1);
                float speedStrength = MathHelper.Clamp(speedFactor, 0f, 1.6f);
                float halfWidth = 4f + 9f * speedFactor;
                Vector2 centre = (newer + older) * 0.5f;
                float rotation = MathF.Atan2(direction.Y, direction.X);
                Vector2 origin = new Vector2(CrushTextureWidth, CrushTextureHeight) * 0.5f;
                Vector2 stretch = new Vector2(length, 1f) / CrushTextureWidth * 1.12f;

                // Squeeze layer: strong when fresh, gone by mid-history.
                float crushAlpha = fx.Heat * MathF.Pow(1f - age, 2.4f) * speedStrength * 0.5f * intensity;
                if (crushAlpha > 0.01f) {
                    Draw.SpriteBatch.Draw(crush, centre, null, Color.White * crushAlpha, rotation, origin,
                        new Vector2(stretch.X, halfWidth * 2f / CrushTextureHeight), SpriteEffects.None, 0f);
                }

                // Wave layer: one outward lobe per side, widening as it ages out.
                float waveAlpha = fx.Heat * MathF.Pow(1f - age, 0.8f) * speedStrength * 0.35f * intensity;
                if (waveAlpha > 0.01f) {
                    float spread = (0.45f + 1.6f * age) * halfWidth;
                    Draw.SpriteBatch.Draw(wave, centre, null, Color.White * waveAlpha, rotation, origin,
                        new Vector2(stretch.X, spread * 2f / CrushTextureHeight), SpriteEffects.None, 0f);
                }
            }
        }
    }

    // A sideways-push displacement strip: neutral on the centre line, pushing
    // outward on both sides (peak at mid-radius, gone at the rim), with a slight
    // along-path drag that stretches the wake. The vanilla distort shader reads
    // this as a UV offset of (channel - 0.5).
    private static Texture2D GetCrushTexture() {
        if (crushTexture is { } texture) return texture;
        Color[] pixels = new Color[CrushTextureWidth * CrushTextureHeight];
        for (int y = 0; y < CrushTextureHeight; y++) {
            float across = (y - (CrushTextureHeight - 1) * 0.5f) / ((CrushTextureHeight - 1) * 0.5f);
            float acrossAbs = MathF.Abs(across);
            float push = acrossAbs * (1f - acrossAbs) * 4f;
            for (int x = 0; x < CrushTextureWidth; x++) {
                float along = x / (float)(CrushTextureWidth - 1);
                float cap = MathF.Pow(MathF.Sin(MathF.PI * along), 0.35f);
                float sideways = MathF.Sign(across) * push * cap;
                float drag = -0.28f * (1f - acrossAbs) * cap;
                pixels[y * CrushTextureWidth + x] = new Color(new Vector4(
                    0.5f + 0.5f * drag, 0.5f + 0.5f * sideways, 0f, 1f));
            }
        }
        crushTexture = new Texture2D(Engine.Instance.GraphicsDevice, CrushTextureWidth, CrushTextureHeight);
        crushTexture.SetData(pixels);
        return crushTexture;
    }

    // A propagating ripple: displacement is zero near the centre line, rises to a
    // single outward lobe and falls back to zero — drawing it with a growing
    // across-scale makes the lobe travel away from the path like a water ripple.
    private static Texture2D GetWaveTexture() {
        if (waveTexture is { } texture) return texture;
        Color[] pixels = new Color[CrushTextureWidth * CrushTextureHeight];
        for (int y = 0; y < CrushTextureHeight; y++) {
            float across = (y - (CrushTextureHeight - 1) * 0.5f) / ((CrushTextureHeight - 1) * 0.5f);
            float acrossAbs = MathF.Abs(across);
            float lobe = acrossAbs <= 0.15f ? 0f
                : MathF.Sin(MathF.PI * (acrossAbs - 0.15f) / 0.85f);
            for (int x = 0; x < CrushTextureWidth; x++) {
                float along = x / (float)(CrushTextureWidth - 1);
                float cap = MathF.Pow(MathF.Sin(MathF.PI * along), 0.35f);
                float sideways = MathF.Sign(across) * lobe * cap;
                pixels[y * CrushTextureWidth + x] = new Color(new Vector4(
                    0.5f, 0.5f + 0.5f * sideways, 0f, 1f));
            }
        }
        waveTexture = new Texture2D(Engine.Instance.GraphicsDevice, CrushTextureWidth, CrushTextureHeight);
        waveTexture.SetData(pixels);
        return waveTexture;
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

    private static Vector2 ScreenDirection(Player player, PlayerFx fx) {
        Vector2 direction = player.Speed.LengthSquared() > 1f ? Vector2.Normalize(player.Speed) : fx.LastDirection;
        if (SaveData.Instance?.Assists.MirrorMode == true) direction.X = -direction.X;
        return direction;
    }
}

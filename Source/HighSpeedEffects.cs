using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds (1000+ px/frame) tech and gimmick maps
/// reach, where vanilla effects read as teleportation: an additive motion ribbon
/// in world space, chromatic aberration with radial ghosting on the level buffer,
/// and a space-crush wake — the world along the player's recent path is pushed
/// aside through vanilla's displacement map, like field lines bending around a
/// wire. Purely visual; physics are untouched.
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
        // Anchor for the qol_speedfx preview: a virtual head that actually travels
        // so a stationary player still ploughs a corridor through the world.
        public Vector2 VirtualPosition;
    }

    // Trail history keyed weakly so respawned players never leak.
    private static readonly ConditionalWeakTable<Player, PlayerFx> Fx = new();

    private static Texture2D? crushTexture;

    // Debug override driven by the qol_speedfx command: fakes the given speed on
    // the local player so every layer of the effect can be eyeballed in any map.
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
    }

    private static QolSettings Settings => MicroblocksQolUtilsModule.Settings;

    private static bool Active => Settings.Enabled && Settings.HighSpeedEffects;

    private static float Intensity => Settings.HighSpeedEffectIntensity / 100f;

    private static void OnLoadLevel(Level level, Player.IntroTypes intro, bool fromLoader) {
        if (level.Tracker.GetEntity<SpaceCrushHook>() is null) level.Add(new SpaceCrushHook());
    }

    // Carries the displacement hook; vanilla's DisplacementRenderer collects one
    // callback per hook component while filling the displacement buffer.
    private sealed class SpaceCrushHook : Entity {
        public SpaceCrushHook() => Add(new DisplacementRenderHook(RenderCrush));

        private void RenderCrush() => RenderSpaceCrush(Engine.Scene as Level);
    }

    private static void PlayerUpdate(On.Celeste.Player.orig_Update orig, Player self) {
        orig(self);
        PlayerFx fx = Fx.GetOrCreateValue(self);
        if (fx.Count == 0) fx.VirtualPosition = self.Center;
        if (self.Speed.LengthSquared() > 1f) fx.LastDirection = Vector2.Normalize(self.Speed);
        if (DebugSpeed is float debugSpeed) {
            DebugSpeedTimer -= Engine.RawDeltaTime;
            if (DebugSpeedTimer <= 0f) DebugSpeed = null;
            fx.Heat = Calc.Approach(fx.Heat, 1f, 8f * Engine.DeltaTime);
            fx.VirtualPosition += fx.LastDirection * debugSpeed * Engine.DeltaTime;
            PushSample(fx, fx.VirtualPosition, debugSpeed);
        } else {
            float speed = self.Speed.Length();
            float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
            float target = Active && self.Scene is Level
                ? MathHelper.Clamp((speed - threshold) / threshold, 0f, 1f)
                : 0f;
            fx.Heat = Calc.Approach(fx.Heat, target, (target > fx.Heat ? 10f : 3.5f) * Engine.DeltaTime);
            PushSample(fx, self.Position, speed);
        }
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

    // The space-crush wake: for every segment of the player's recent path, push
    // the world sideways through the displacement map — perpendicular to the
    // motion at that point, scaled by the speed at that point, decaying as the
    // segment ages. Space "bends around" the traversed path and closes back up.
    private static void RenderSpaceCrush(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedWarp || level.FrozenOrPaused) return;
        float intensity = Intensity;
        float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
        Texture2D texture = GetCrushTexture();

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
                if (speedFactor <= 0.3f) continue;
                float age = i / (float)(fx.Count - 1);
                float halfWidth = 4f + 9f * speedFactor;
                float alpha = fx.Heat * MathF.Pow(1f - age, 1.3f)
                    * MathHelper.Clamp(speedFactor, 0f, 1.6f) * 0.5f * intensity;
                if (alpha <= 0.01f) continue;

                Vector2 scale = new(length / CrushTextureWidth * 1.12f, halfWidth * 2f / CrushTextureHeight);
                Draw.SpriteBatch.Draw(texture, (newer + older) * 0.5f, null, Color.White * alpha,
                    MathF.Atan2(direction.Y, direction.X),
                    new Vector2(CrushTextureWidth, CrushTextureHeight) * 0.5f, scale, SpriteEffects.None, 0f);
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

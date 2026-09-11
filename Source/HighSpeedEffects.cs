using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds tech and gimmick maps reach, where
/// vanilla effects read as teleportation: an additive motion ribbon in world
/// space plus a wake computed analytically from the trail — capsule-shaped wave
/// bands around each trail segment that thicken and spread as they age, with
/// the forward half cut away so the wake opens backward like a boat's V and can
/// never overtake the player. The finished frame is then post-processed at
/// render resolution by a single pixel shader driven by the field texture
/// (displacement vectors + strength): warp, a graded blur whose radius follows
/// the wake strength through a downsample pyramid, wake-local chromatic
/// aberration and a brightness lift. Scattering sparks and wall-impact
/// shockwaves round it off. Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const int ParticleCapacity = 160;

    // The wake field resolution matches the level buffer 1:1.
    private const int FieldWidth = 320;
    private const int FieldHeight = 180;

    // Wake band shaping: a fresh segment wears a thin tight band hugging the
    // path; as it ages the band travels outward (radius grows), widens and
    // fades. Everything is a direct function of segment age, so the field is
    // born behind the player and shrinks away — nothing propagates forward.
    private const float WakeRadiusBase = 5f;
    private const float WakeRadiusGrowth = 58f;
    private const float WakeBandBase = 4.5f;
    private const float WakeBandGrow = 6f;
    private const float WakePushScale = 0.6f;
    private const float WakeForwardCut = 0.45f;

    private sealed class PlayerFx {
        public readonly Vector2[] Positions = new Vector2[TrailSamples];
        public readonly float[] Speeds = new float[TrailSamples];
        public int Count;
        public int Head;
        public float Heat;
        public Vector2 LastDirection = new(1f, 0f);
        public float PreviousSpeed;
    }

    private struct Particle {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public float MaxLife;
        public float Length;
        public bool Streak;
        public Color Tint;
    }

    // Trail history keyed weakly so respawned players never leak.
    private static readonly ConditionalWeakTable<Player, PlayerFx> Fx = new();
    private static readonly Particle[] Particles = new Particle[ParticleCapacity];
    private static int particleCursor;

    // Deterministic RNG owned by the effects layer; Calc.Random must stay
    // untouched here or gameplay rolls would depend on this mod's activity.
    private static readonly Random Random = new();

    // The field texture: R/G carry the displacement vector around the neutral
    // 0.5, B carries the (blurred, peak-normalized) wake strength that drives
    // the shader's graded blur, aberration and brightness.
    private static readonly float[] WakeVecX = new float[FieldWidth * FieldHeight];
    private static readonly float[] WakeVecY = new float[FieldWidth * FieldHeight];
    private static readonly float[] WakeStrength = new float[FieldWidth * FieldHeight];
    private static readonly float[] ScratchField = new float[FieldWidth * FieldHeight];
    private static readonly Color[] FieldPixels = new Color[FieldWidth * FieldHeight];
    private static Texture2D? fieldTexture;

    // Render-resolution scratch: the shader output plus the three-level blur
    // pyramid (the pyramid is field-sized; the shader resolves it at output
    // resolution through linear sampling).
    private static RenderTarget2D? screenWork;
    private static RenderTarget2D? screenHalf;
    private static RenderTarget2D? screenQuarter;
    private static RenderTarget2D? screenEighth;
    private static int screenWidth;
    private static int screenHeight;

    private static Effect? wakeEffect;

    // Peak wake strength from the last field build; gates the post pass so it
    // follows the wake's own fade-out instead of the player's speed.
    private static float fieldActivity;

    // Debug override driven by the qol_speedfx command: feeds the given speed to
    // every layer while the player moves for real, so the wake follows the
    // player's actual path.
    internal static float? DebugSpeed;
    internal static float DebugSpeedTimer;

    public static void Load() {
        On.Celeste.Player.Update += PlayerUpdate;
        On.Celeste.Player.Render += PlayerRender;
        On.Celeste.Glitch.Apply += GlitchApply;
    }

    public static void Unload() {
        On.Celeste.Player.Update -= PlayerUpdate;
        On.Celeste.Player.Render -= PlayerRender;
        On.Celeste.Glitch.Apply -= GlitchApply;
        Fx.Clear();
        DebugSpeed = null;
        Array.Clear(Particles);
        fieldTexture?.Dispose();
        fieldTexture = null;
        screenWork?.Dispose();
        screenWork = null;
        screenHalf?.Dispose();
        screenHalf = null;
        screenQuarter?.Dispose();
        screenQuarter = null;
        screenEighth?.Dispose();
        screenEighth = null;
        wakeEffect?.Dispose();
        wakeEffect = null;
        diagnosedWake = false;
        screenWidth = 0;
        screenHeight = 0;
    }

    private static QolSettings Settings => MicroblocksQolUtilsModule.Settings;

    private static bool Active => Settings.Enabled && Settings.HighSpeedEffects;

    private static float Intensity => Settings.HighSpeedEffectIntensity / 100f;

    // Effects begin at half the threshold and saturate at 2x, so light dashes
    // already stir a faint ripple and the wake reads clearly from the threshold.
    private static float HeatCurve(float speed, float threshold)
        => MathHelper.Clamp((speed - 0.5f * threshold) / (1.5f * threshold), 0f, 1f);

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
        PushSample(fx, self.Center, speed);

        if (Active && self.Scene is Level level && !level.FrozenOrPaused) {
            // A sudden stop (wall impact, ground slam) releases a vanilla-style
            // displacement burst plus sparks.
            float drop = fx.PreviousSpeed - speed;
            if (drop > 400f && fx.PreviousSpeed > threshold * 1.1f && Settings.HighSpeedWarp) {
                float power = MathHelper.Clamp(drop / 900f, 0.25f, 1.25f) * Intensity;
                level.Displacement.AddBurst(self.Center, 0.55f, 4f, 40f + 48f * power,
                    0.45f * power, Ease.QuadOut, Ease.QuadOut);
                SpawnImpactSparks(self.Center, fx.LastDirection, power);
            }
            if (Settings.HighSpeedParticles)
                SpawnTrailParticles(self, fx, speed, threshold);
        }
        fx.PreviousSpeed = speed;
        UpdateParticles(Engine.DeltaTime);
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
        if (Settings.HighSpeedParticles && player == FindFocusPlayer(level))
            RenderParticles(edge);
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
            float streak = MathHelper.Clamp((speed - 250f) * 0.05f, 8f, 80f) * heat * intensity;
            for (int i = 0; i < 3; i++) {
                Vector2 side = new Vector2(-direction.Y, direction.X) * ((i - 1) * 7f);
                Vector2 start = player.Center + side - direction * 8f;
                Draw.Line(start, start - direction * streak * (0.7f + 0.15f * i), edge * (0.20f * heat * intensity), 1.5f);
            }
        }
        Draw.SpriteBatch.End();
        GameplayRenderer.Begin();
    }

    // The wake, computed analytically from the trail every frame. Each segment
    // contributes a capsule band: the set of pixels at distance ~radius(age)
    // from the segment, pushed radially outward. Bands from neighbouring
    // segments overlap into a continuous wave front; the forward half of every
    // band is cut away so the wake opens backward and can never overtake the
    // player, and when the player stops the trail ages out and the whole wake
    // shrinks away — no propagation, no damping, no absorbing borders.
    private static void BuildWakeField(Level? level) {
        if (level is null) return;
        fieldTexture ??= new Texture2D(Engine.Instance.GraphicsDevice, FieldWidth, FieldHeight);

        Array.Clear(WakeVecX);
        Array.Clear(WakeVecY);
        Array.Clear(WakeStrength);

        Vector2 camera = level.Camera.Position;
        float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
        float intensity = Intensity;
        List<Entity> players = level.Tracker.GetEntities<Player>();
        float peakStrength = 0f;

        foreach (Entity entity in players) {
            Player player = (Player)entity;
            PlayerFx fx = Fx.GetOrCreateValue(player);
            // Speed is only the *generation* condition: segments that were fast
            // enough keep rendering while they age out, so the wake fades with
            // its own curve instead of being gated by the player's current heat.
            if (fx.Count < 2) continue;
            for (int i = 0; i < fx.Count - 1; i++) {
                Vector2 newer = fx.Positions[(fx.Head - 1 - i + TrailSamples * 2) % TrailSamples];
                Vector2 older = fx.Positions[(fx.Head - 2 - i + TrailSamples * 2) % TrailSamples];
                Vector2 segment = newer - older;
                float segmentLength = segment.Length();
                if (segmentLength < 2f) continue;
                Vector2 direction = segment / segmentLength;

                float speed = fx.Speeds[(fx.Head - 1 - i + TrailSamples * 2) % TrailSamples];
                float speedFactor = MathHelper.Clamp(speed / threshold - 0.5f, 0f, 2f);
                if (speedFactor <= 0.03f) continue;
                float age = i / (float)(fx.Count - 1);
                float radius = WakeRadiusBase + WakeRadiusGrowth * age;
                float band = WakeBandBase + WakeBandGrow * (1f - age);
                float reach = radius + band;
                float fade = MathF.Pow(1f - age, 1.6f) * speedFactor * WakePushScale * intensity;

                float minX = MathF.Min(newer.X, older.X) - reach;
                float maxX = MathF.Max(newer.X, older.X) + reach;
                float minY = MathF.Min(newer.Y, older.Y) - reach;
                float maxY = MathF.Max(newer.Y, older.Y) + reach;
                int x0 = Math.Max(0, (int)(minX - camera.X));
                int x1 = Math.Min(FieldWidth - 1, (int)(maxX - camera.X));
                int y0 = Math.Max(0, (int)(minY - camera.Y));
                int y1 = Math.Min(FieldHeight - 1, (int)(maxY - camera.Y));
                if (x0 > x1 || y0 > y1) continue;
                float segmentLengthSquared = segmentLength * segmentLength;

                for (int y = y0; y <= y1; y++) {
                    for (int x = x0; x <= x1; x++) {
                        Vector2 pixel = new(x + camera.X, y + camera.Y);
                        // Closest point on the segment, then the capsule band test.
                        float t = MathHelper.Clamp(Vector2.Dot(pixel - older, segment) / segmentLengthSquared, 0f, 1f);
                        Vector2 closest = older + segment * t;
                        Vector2 offset = pixel - closest;
                        float distance = offset.Length();
                        float fromBand = MathF.Abs(distance - radius);
                        if (fromBand > band) continue;
                        float bandShape = 1f - fromBand / band;
                        if (bandShape <= 0f || distance < 0.01f) continue;
                        // Rounded band profile so the wave edges taper smoothly.
                        bandShape = bandShape * bandShape * (3f - 2f * bandShape);
                        Vector2 radial = offset / distance;
                        // Cut away the forward half: the wake only opens backward.
                        float forwardness = radial.X * direction.X + radial.Y * direction.Y;
                        if (forwardness >= WakeForwardCut) continue;
                        float cut = forwardness <= 0.1f
                            ? 1f
                            : 1f - (forwardness - 0.1f) / (WakeForwardCut - 0.1f);
                        float amplitude = fade * bandShape * cut;
                        if (amplitude <= 0.004f) continue;
                        int index = y * FieldWidth + x;
                        WakeVecX[index] += radial.X * amplitude;
                        WakeVecY[index] += radial.Y * amplitude;
                        float strength = MathHelper.Clamp(amplitude / (WakePushScale * intensity), 0f, 1f);
                        if (strength > WakeStrength[index]) {
                            WakeStrength[index] = strength;
                            if (strength > peakStrength) peakStrength = strength;
                        }
                    }
                }
            }
        }

        // Every player is punched back out (even from other players' wakes) so
        // sprites stay crisp.
        for (int p = 0; p < players.Count; p++) {
            Player player = (Player)players[p];
            Vector2 local = player.Center - camera;
            int x0 = Math.Max(0, (int)(local.X - 17f));
            int x1 = Math.Min(FieldWidth - 1, (int)(local.X + 17f));
            int y0 = Math.Max(0, (int)(local.Y - 17f));
            int y1 = Math.Min(FieldHeight - 1, (int)(local.Y + 17f));
            for (int y = y0; y <= y1; y++) {
                for (int x = x0; x <= x1; x++) {
                    float distance = MathF.Sqrt((x - local.X) * (x - local.X) + (y - local.Y) * (y - local.Y));
                    float falloff = MathHelper.Clamp(distance / 16f, 0f, 1f);
                    if (falloff >= 1f) continue;
                    falloff = falloff * falloff * (3f - 2f * falloff);
                    int index = y * FieldWidth + x;
                    WakeVecX[index] *= falloff;
                    WakeVecY[index] *= falloff;
                    WakeStrength[index] *= falloff;
                }
            }
        }

        // Blur the strength so the shader's graded blur and fringes ease in over
        // a wide slope; normalize against the surviving peak because the blur
        // dilutes it, otherwise the heavy tier never reaches its threshold.
        BlurStrength(4);
        float blurredPeak = 0f;
        for (int index = 0; index < WakeStrength.Length; index++)
            if (WakeStrength[index] > blurredPeak) blurredPeak = WakeStrength[index];
        float normalize = blurredPeak > 0.01f ? 1f / blurredPeak : 0f;
        for (int index = 0; index < FieldPixels.Length; index++) {
            FieldPixels[index] = new Color(new Vector4(
                0.5f + WakeVecX[index],
                0.5f + WakeVecY[index],
                MathHelper.Clamp(WakeStrength[index] * normalize, 0f, 1f),
                1f));
        }
        fieldActivity = peakStrength;
        fieldTexture.SetData(FieldPixels);
    }

    // Separable box blur over the wake strength, in place.
    private static void BlurStrength(int radius) {
        int width = FieldWidth, height = FieldHeight;
        Span<float> scratch = ScratchField;
        int window = radius * 2 + 1;
        int stride = width;
        for (int y = 0; y < height; y++) {
            int row = y * width;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
                sum += WakeStrength[row + Math.Clamp(k, 0, width - 1)];
            for (int x = 0; x < width; x++) {
                scratch[row + x] = sum / window;
                sum -= WakeStrength[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += WakeStrength[row + Math.Clamp(x + radius + 1, 0, width - 1)];
            }
        }
        for (int x = 0; x < width; x++) {
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
                sum += scratch[Math.Clamp(k, 0, height - 1) * stride + x];
            for (int y = 0; y < height; y++) {
                WakeStrength[y * width + x] = sum / window;
                sum -= scratch[Math.Clamp(y - radius, 0, height - 1) * stride + x];
                sum += scratch[Math.Clamp(y + radius + 1, 0, height - 1) * stride + x];
            }
        }
    }

    // The level buffer is finished here (bloom, glitch, foreground) and still
    // bound — the wake pass runs inside the pipeline, reading and writing the
    // level buffer itself. With MotionSmoothing's Fancy mode that buffer is a
    // 1920x1080 surface, so the effect automatically runs at high resolution
    // there and falls back to 320x180 otherwise.
    private static void GlitchApply(On.Celeste.Glitch.orig_Apply orig, VirtualRenderTarget source,
        float timer, float seed, float amplitude) {
        orig(source, timer, seed, amplitude);
        if (source == GameplayBuffers.Level) {
            BuildWakeField(Engine.Scene as Level);
            RenderWakeInPipeline(Engine.Scene as Level);
        }
    }

    private static void RenderWakeInPipeline(Level? level) {
        if (level is null || !Active || fieldActivity <= 0.01f) return;
        if (!Settings.HighSpeedWarp && !Settings.HighSpeedBlur && !Settings.HighSpeedAberration) return;
        GraphicsDevice device = Engine.Instance.GraphicsDevice;
        RenderTarget2D levelBuffer = (RenderTarget2D)GameplayBuffers.Level;
        int width = levelBuffer.Width;
        int height = levelBuffer.Height;
        if (width < 16 || height < 16) return;
        EnsureScreenTargets(device, width, height);

        List<Entity> players = level.Tracker.GetEntities<Player>();
        Player? player = FindFocusPlayer(level)
            ?? (players.Count > 0 ? (Player)players[0] : null);
        if (player is null) return;
        PlayerFx fx = Fx.GetOrCreateValue(player);

        // Blur pyramid from the level buffer (half/quarter/eighth of its real
        // size — high resolution under MotionSmoothing, field-sized otherwise).
        device.SetRenderTarget(screenHalf);
        BeginSprite(BlendState.Opaque, SamplerState.LinearClamp);
        Draw.SpriteBatch.Draw(levelBuffer, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        Draw.SpriteBatch.End();
        device.SetRenderTarget(screenQuarter);
        BeginSprite(BlendState.Opaque, SamplerState.LinearClamp);
        Draw.SpriteBatch.Draw(screenHalf, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        Draw.SpriteBatch.End();
        device.SetRenderTarget(screenEighth);
        BeginSprite(BlendState.Opaque, SamplerState.LinearClamp);
        Draw.SpriteBatch.Draw(screenQuarter, Vector2.Zero, null, Color.White, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 0f);
        Draw.SpriteBatch.End();

        // One shader pass: warp + graded blur + aberration + brightness lift,
        // self-composited over a nearest-neighbour copy of the frame.
        float activity = MathHelper.Clamp(fieldActivity * 1.5f, 0f, 1f);
        float intensity = Intensity;
        Effect effect = GetWakeEffect();
        effect.Parameters["ScreenTex"].SetValue(levelBuffer);
        effect.Parameters["FieldTex"].SetValue(fieldTexture);
        effect.Parameters["BlurHalfTex"].SetValue(screenHalf);
        effect.Parameters["BlurQuarterTex"].SetValue(screenQuarter);
        effect.Parameters["BlurEighthTex"].SetValue(screenEighth);
        effect.Parameters["ScreenTexel"].SetValue(new Vector2(1f / width, 1f / height));
        effect.Parameters["WarpScale"].SetValue(Settings.HighSpeedWarp ? 0.06f : 0f);
        effect.Parameters["BlurAmount"].SetValue(Settings.HighSpeedBlur ? 1f : 0f);
        Vector2 direction = ScreenDirection(player, fx);
        float offsetPixels = MathF.Max(1f, (0.2f + 1.1f * activity) * intensity);
        effect.Parameters["CaShift"].SetValue(Settings.HighSpeedAberration
            ? direction * offsetPixels / new Vector2(width, height)
            : Vector2.Zero);
        effect.Parameters["BrightnessLift"].SetValue(Settings.HighSpeedAberration ? 0.10f * activity : 0f);
        // Keep displaced sampling away from the player's sprite.
        Vector2 playerLocal = player.Center - level.Camera.Position;
        if (SaveData.Instance.Assists.MirrorMode) playerLocal.X = 320f - playerLocal.X;
        effect.Parameters["PlayerUV"].SetValue(playerLocal / new Vector2(FieldWidth, FieldHeight));
        effect.Parameters["PlayerRadiusUV"].SetValue(20f / FieldWidth);
        bool diagnose = !diagnosedWake;
        diagnosedWake = true;
        device.SetRenderTarget(screenWork);
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone, effect);
        Draw.SpriteBatch.Draw(levelBuffer, new Rectangle(0, 0, width, height), Color.White);
        Draw.SpriteBatch.End();
        Color[]? work = diagnose ? ReadTargetCentre(screenWork!) : null;

        // Write the processed frame back into the level buffer; the vanilla
        // composite (or MotionSmoothing's) then carries it to the screen.
        device.SetRenderTarget(levelBuffer);
        BeginSprite(BlendState.Opaque, SamplerState.LinearClamp);
        Draw.SpriteBatch.Draw(screenWork, new Rectangle(0, 0, width, height), Color.White);
        Draw.SpriteBatch.End();
        if (diagnose) {
            Color[] after = ReadTargetCentre(levelBuffer);
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils",
                $"wakediag: level={width}x{height} motionSmoothing={MotionSmoothingBridge.Enabled} "
                + $"shaderOut={work![0]} after={after[0]}");
        }
    }

    // One-shot diagnostics when the wake first renders: samples the shader
    // output and the level buffer after the write-back.
    private static bool diagnosedWake;

    private static Color[] ReadTargetCentre(RenderTarget2D target) {
        Color[] centre = new Color[1];
        target.GetData(0, new Rectangle(target.Width / 2, target.Height / 2, 1, 1), centre, 0, 1);
        return centre;
    }

    private static void EnsureScreenTargets(GraphicsDevice device, int width, int height) {
        if (screenWidth == width && screenHeight == height) return;
        screenWork?.Dispose();
        screenHalf?.Dispose();
        screenQuarter?.Dispose();
        screenEighth?.Dispose();
        screenWidth = width;
        screenHeight = height;
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils", $"waketargets: {width}x{height}");
        screenWork = new RenderTarget2D(device, width, height, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
        screenHalf = new RenderTarget2D(device, FieldWidth / 2, FieldHeight / 2, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
        screenQuarter = new RenderTarget2D(device, FieldWidth / 4, FieldHeight / 4, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
        screenEighth = new RenderTarget2D(device, FieldWidth / 8, FieldHeight / 8, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.DiscardContents);
    }

    private static Effect GetWakeEffect() {
        if (wakeEffect is { } effect) return effect;
        using Stream stream = typeof(HighSpeedEffects).Assembly
            .GetManifestResourceStream("Celeste.Mod.MicroblocksQolUtils.Effects.HighSpeedWake.fxb")
            ?? throw new InvalidOperationException("Embedded HighSpeedWake.fxb is missing");
        using MemoryStream memory = new();
        stream.CopyTo(memory);
        wakeEffect = new Effect(Engine.Instance.GraphicsDevice, memory.ToArray());
        return wakeEffect;
    }

    private static void BeginSprite(BlendState blend, SamplerState sampler)
        => Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, blend, sampler,
            DepthStencilState.None, RasterizerState.CullNone);

    private static void SpawnTrailParticles(Player player, PlayerFx fx, float speed, float threshold) {
        float excess = MathHelper.Clamp(speed / threshold - 0.5f, 0f, 2f);
        int count = 1 + (int)(excess * 2f);
        for (int i = 0; i < count && i < 5; i++) {
            // Scatter: sparks fly off against the motion with a random radial
            // kick, then drag against the air as they fade.
            Vector2 radial = Calc.AngleToVector(NextFloat() * MathF.PI * 2f, 1f)
                * (40f + 130f * NextFloat());
            Vector2 velocity = -fx.LastDirection * speed * 0.08f + radial;
            bool streak = NextFloat() < 0.3f;
            SpawnParticle(new Particle {
                Position = player.Center + Range(-Vector2.One * 4f, Vector2.One * 4f),
                Velocity = velocity,
                Life = 0.45f + NextFloat() * 0.4f,
                MaxLife = 0.85f,
                Length = streak ? 9f + NextFloat() * 14f : 1.6f + NextFloat(),
                Streak = streak,
                Tint = Color.Lerp(Color.Cyan, Color.White, NextFloat() * 0.7f),
            });
        }
    }

    private static void SpawnImpactSparks(Vector2 centre, Vector2 direction, float power) {
        int count = (int)(18 + 18 * power);
        for (int i = 0; i < count; i++) {
            float angle = NextFloat() * MathF.PI * 2f;
            Vector2 radial = Calc.AngleToVector(angle, 40f + 150f * power * NextFloat());
            SpawnParticle(new Particle {
                Position = centre,
                Velocity = radial - direction * (NextFloat() * 60f),
                Life = 0.3f + NextFloat() * 0.4f,
                MaxLife = 0.7f,
                Length = 1f + NextFloat() * 2f,
                Streak = false,
                Tint = Color.Lerp(Color.Cyan, Color.White, 0.4f + NextFloat() * 0.5f),
            });
        }
    }

    private static void SpawnParticle(Particle particle) {
        Particles[particleCursor] = particle;
        particleCursor = (particleCursor + 1) % ParticleCapacity;
    }

    private static void UpdateParticles(float delta) {
        float drag = MathF.Pow(0.25f, delta);
        for (int i = 0; i < ParticleCapacity; i++) {
            if (Particles[i].Life > 0f) {
                Particles[i].Life -= delta;
                Particles[i].Position += Particles[i].Velocity * delta;
                Particles[i].Velocity *= drag;
            }
        }
    }

    private static void RenderParticles(Color tint) {
        for (int i = 0; i < ParticleCapacity; i++) {
            Particle particle = Particles[i];
            if (particle.Life <= 0f) continue;
            float alpha = MathHelper.Clamp(particle.Life / particle.MaxLife, 0f, 1f);
            alpha *= alpha;
            if (particle.Streak) {
                Vector2 direction = particle.Velocity.LengthSquared() > 1f
                    ? Vector2.Normalize(particle.Velocity) : Vector2.UnitX;
                Draw.Line(particle.Position, particle.Position - direction * particle.Length,
                    particle.Tint * (0.4f * alpha), 1f);
            } else {
                float size = particle.Length * (0.5f + 0.5f * alpha);
                Draw.Rect(particle.Position - Vector2.One * size * 0.5f, size, size, particle.Tint * (0.6f * alpha));
            }
        }
    }

    // Diagnostics for the qol_wakestat command: dumps the wake pipeline state to
    // the log so scroll-related issues can be located from log.txt alone.
    internal static void DumpWakeStats() {
        Level? level = Engine.Scene as Level;
        if (level is null) {
            Logger.Log(LogLevel.Info, "MicroblocksQolUtils", "wakestat: no level");
            return;
        }
        Player? player = level.Tracker.GetEntity<Player>();
        PlayerFx? fx = player is null ? null : Fx.GetOrCreateValue(player);
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils",
            $"wakestat: camera={level.Camera.Position} player={(player is null ? "none" : player.Center.ToString())} "
            + $"heat={(fx is null ? 0f : fx.Heat):0.00} speed={(player is null ? 0f : player.Speed.Length()):0} "
            + $"segments={fx?.Count ?? 0}/{TrailSamples} activity={fieldActivity:0.00}");
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

    private static float NextFloat() => (float)Random.NextDouble();

    private static Vector2 Range(Vector2 min, Vector2 max)
        => new(min.X + NextFloat() * (max.X - min.X), min.Y + NextFloat() * (max.Y - min.Y));
}

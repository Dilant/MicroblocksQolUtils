using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds tech and gimmick maps reach, where
/// vanilla effects read as teleportation: an additive motion ribbon in world
/// space, a wake computed analytically from the trail — capsule-shaped wave
/// bands around each trail segment that thicken and spread as they age, with
/// the forward half cut away so the wake opens backward like a boat's V and can
/// never overtake the player — feeding vanilla's displacement map, plus
/// wake-local chromatic aberration, motion blur and a brightness lift driven
/// by the same field, scattering sparks and wall-impact shockwaves.
/// Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const int ParticleCapacity = 160;

    // The wake field resolution matches the displacement buffer 1:1.
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

    // Per-pixel wake accumulation: displacement vector and normalized strength
    // (the strength doubles as the aberration/blur mask, giving those layers a
    // continuous fade that tracks the wake exactly).
    private static readonly float[] WakeVecX = new float[FieldWidth * FieldHeight];
    private static readonly float[] WakeVecY = new float[FieldWidth * FieldHeight];
    private static readonly float[] WakeStrength = new float[FieldWidth * FieldHeight];
    private static readonly float[] ScratchField = new float[FieldWidth * FieldHeight];
    private static readonly Color[] FieldPixels = new Color[FieldWidth * FieldHeight];
    private static readonly Color[] CaMaskPixels = new Color[FieldWidth * FieldHeight];
    private static Texture2D? fieldTexture;
    private static Texture2D? caMaskTexture;
    // Peak wake strength from the last field build; gates the aberration pass so
    // it follows the wake's own fade-out instead of the player's speed.
    private static float fieldActivity;

    // Debug override driven by the qol_speedfx command: feeds the given speed to
    // every layer while the player moves for real, so the wake follows the
    // player's actual path.
    internal static float? DebugSpeed;
    internal static float DebugSpeedTimer;

    // Multiplies colour copy into the wake mask (src.rgb * dst.rgb).
    private static readonly BlendState MultiplyBlend = new() {
        ColorBlendFunction = BlendFunction.Add,
        ColorSourceBlend = Blend.DestinationColor,
        ColorDestinationBlend = Blend.Zero,
        AlphaBlendFunction = BlendFunction.Add,
        AlphaSourceBlend = Blend.One,
        AlphaDestinationBlend = Blend.Zero,
    };

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
        Array.Clear(Particles);
        fieldTexture?.Dispose();
        fieldTexture = null;
        caMaskTexture?.Dispose();
        caMaskTexture = null;
    }

    private static QolSettings Settings => MicroblocksQolUtilsModule.Settings;

    private static bool Active => Settings.Enabled && Settings.HighSpeedEffects;

    private static float Intensity => Settings.HighSpeedEffectIntensity / 100f;

    // Effects begin at half the threshold and saturate at 2x, so light dashes
    // already stir a faint ripple and the wake reads clearly from the threshold.
    private static float HeatCurve(float speed, float threshold)
        => MathHelper.Clamp((speed - 0.5f * threshold) / (1.5f * threshold), 0f, 1f);

    private static void OnLoadLevel(Level level, Player.IntroTypes intro, bool fromLoader) {
        if (level.Tracker.GetEntity<SpaceCrushHook>() is null) level.Add(new SpaceCrushHook());
    }

    // Carries the displacement hook; vanilla's DisplacementRenderer collects one
    // callback per hook component while filling the displacement buffer.
    [Tracked] // Tracker.GetEntity<> throws for types that are not registered as tracked.
    private sealed class SpaceCrushHook : Entity {
        public SpaceCrushHook() => Add(new DisplacementRenderHook(RenderCrush));

        private void RenderCrush() => RenderTrailWake(Engine.Scene as Level);
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
    private static void RenderTrailWake(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedWarp || level.FrozenOrPaused) return;
        Texture2D texture = fieldTexture ??= new Texture2D(Engine.Instance.GraphicsDevice, FieldWidth, FieldHeight);
        caMaskTexture ??= new Texture2D(Engine.Instance.GraphicsDevice, FieldWidth, FieldHeight);

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

        // Bake the accumulated field into the displacement map; every player is
        // punched back out (even from other players' wakes) so sprites stay
        // crisp.
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

        // Blur the strength so the aberration mask is wider than the displacement
        // band itself — the fringes then ease in over a 10px+ slope instead of
        // hugging the wave's hard edge.
        BlurStrength(4);
        for (int index = 0; index < FieldPixels.Length; index++) {
            float strength = WakeStrength[index];
            FieldPixels[index] = new Color(new Vector4(
                0.5f + WakeVecX[index],
                0.5f + WakeVecY[index],
                0f, 1f));
            float ramp = MathHelper.Clamp((strength - 0.12f) / 0.58f, 0f, 1f);
            float mask = ramp * ramp * (3f - 2f * ramp);
            CaMaskPixels[index] = new Color(new Vector4(mask, mask, mask, 1f));
        }
        fieldActivity = peakStrength;
        texture.SetData(FieldPixels);
        caMaskTexture.SetData(CaMaskPixels);

        // The hook's sprite batch is already begun with the camera transform and
        // alpha blending; draw the field over the camera's view of the world.
        Draw.SpriteBatch.Draw(texture, camera, Color.White);
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
        List<Entity> players = level.Tracker.GetEntities<Player>();
        // Prefer the hottest player for direction; fall back to any player so the
        // fringes keep their heading while the wake fades out after a stop.
        Player? player = FindFocusPlayer(level)
            ?? (players.Count > 0 ? (Player)players[0] : null);
        if (player is null) return;
        PlayerFx fx = Fx.GetOrCreateValue(player);
        // Follow the wake, not the current speed: the fringes stay alive (and
        // fade out with the field) after the player has already stopped.
        if (fieldActivity <= 0.01f) return;

        float intensity = Intensity;
        Vector2 direction = ScreenDirection(player, fx);
        float offset = MathF.Max(0.5f, (0.2f + 1.1f * MathHelper.Clamp(fieldActivity * 1.5f, 0f, 1f)) * intensity);
        // The aberration region is the wake field itself: the strength mask
        // generated alongside the displacement map fades in and out with the
        // waves, so the fringes appear and vanish smoothly.
        if (caMaskTexture is null) return;
        // The field is regenerated in camera space each frame, so it lines up
        // with the level buffer one to one.
        Vector2 maskPosition = Vector2.Zero;

        GraphicsDevice device = Engine.Instance.GraphicsDevice;
        RenderTarget2D levelBuffer = (RenderTarget2D)GameplayBuffers.Level;
        RenderTarget2D tempA = (RenderTarget2D)GameplayBuffers.TempA;
        RenderTarget2D tempB = (RenderTarget2D)GameplayBuffers.TempB;

        device.SetRenderTarget(tempA);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.AlphaBlend, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(levelBuffer, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        device.SetRenderTarget(levelBuffer);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.Additive, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(tempA, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        DrawMaskedLayer(device, tempB, tempA, direction * offset, maskPosition, new Color(170, 0, 0, 255));
        DrawMaskedLayer(device, tempB, tempA, -direction * offset, maskPosition, new Color(0, 0, 170, 255));
        // A faint uniform white copy inside the wake acts as a brightness lift.
        DrawMaskedLayer(device, tempB, tempA, Vector2.Zero, maskPosition, new Color(85, 85, 85, 255));
        // Two slight along-motion copies soften the wake with directional blur.
        DrawMaskedLayer(device, tempB, tempA, direction * (offset * 0.35f), maskPosition, new Color(38, 38, 38, 255));
        DrawMaskedLayer(device, tempB, tempA, -direction * (offset * 0.35f), maskPosition, new Color(38, 38, 38, 255));
    }

    private static void BeginSprite(BlendState blend, SamplerState sampler)
        => Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, blend, sampler,
            DepthStencilState.None, RasterizerState.CullNone);

    private static void DrawMaskedLayer(GraphicsDevice device, RenderTarget2D work, RenderTarget2D source,
        Vector2 shift, Vector2 maskPosition, Color tint) {
        device.SetRenderTarget(work);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.AlphaBlend, SamplerState.LinearClamp);
        Draw.SpriteBatch.Draw(caMaskTexture, maskPosition, Color.White);
        Draw.SpriteBatch.End();
        BeginSprite(MultiplyBlend, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(source, shift, tint);
        Draw.SpriteBatch.End();

        device.SetRenderTarget((RenderTarget2D)GameplayBuffers.Level);
        BeginSprite(BlendState.Additive, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(work, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();
    }

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

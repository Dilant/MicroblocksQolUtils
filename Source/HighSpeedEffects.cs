using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds tech and gimmick maps reach, where
/// vanilla effects read as teleportation: an additive motion ribbon in world
/// space, a CPU-simulated water surface (classic two-buffer wave propagation —
/// the player's path stirs the field and the ripples spread, oscillate and
/// decay on their own) feeding vanilla's displacement map, wake-local
/// chromatic aberration with a saturation boost, scattering sparks and
/// wall-impact shockwaves. Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const int ParticleCapacity = 160;

    // Water surface simulation resolution matches the displacement buffer 1:1.
    private const int FieldWidth = 320;
    private const int FieldHeight = 180;
    private const float FieldDamping = 0.987f;
    private const float FieldGradientScale = 9f;
    private const int FieldStepsPerFrame = 2;

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

    // Two-buffer wave height field: current and previous step. The next buffer
    // is scratch for the propagation pass (the three rotate every step).
    private static float[] WaveCurrent = new float[FieldWidth * FieldHeight];
    private static float[] WavePrevious = new float[FieldWidth * FieldHeight];
    private static float[] WaveScratch = new float[FieldWidth * FieldHeight];
    private static readonly Color[] FieldPixels = new Color[FieldWidth * FieldHeight];
    private static Texture2D? fieldTexture;
    private static Vector2 fieldCamera;

    private static Texture2D? wakeMaskTexture;

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
        Array.Clear(WaveCurrent);
        Array.Clear(WavePrevious);
        fieldTexture?.Dispose();
        fieldTexture = null;
        wakeMaskTexture?.Dispose();
        wakeMaskTexture = null;
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

        private void RenderCrush() => RenderWaterField(Engine.Scene as Level);
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
            if (Settings.HighSpeedWarp) {
                // Stir the water surface along the path; the amplitude scales with
                // speed so slow movement only dimples the surface.
                float excess = MathHelper.Clamp(speed / threshold - 0.5f, 0f, 2f);
                if (excess > 0f)
                    InjectWave(level, self.Center, -(1.5f + 8f * excess * excess) * Intensity,
                        2.5f + 3.5f * excess);
                // A sudden stop (wall impact, ground slam) dumps the built-up
                // momentum into the surface plus a vanilla-style burst.
                float drop = fx.PreviousSpeed - speed;
                if (drop > 400f && fx.PreviousSpeed > threshold * 1.1f) {
                    float power = MathHelper.Clamp(drop / 900f, 0.25f, 1.25f) * Intensity;
                    InjectWave(level, self.Center, -14f * power, 7f);
                    level.Displacement.AddBurst(self.Center, 0.55f, 4f, 40f + 48f * power,
                        0.45f * power, Ease.QuadOut, Ease.QuadOut);
                    SpawnImpactSparks(self.Center, fx.LastDirection, power);
                }
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
        if (SaveData.Instance.Assists.MirrorMode) {
            focus.X = 320f - focus.X;
            direction.X = -direction.X;
        }
        float offset = MathF.Max(0.5f, (0.2f + 1.1f * heat) * intensity);

        GraphicsDevice device = Engine.Instance.GraphicsDevice;
        RenderTarget2D levelBuffer = (RenderTarget2D)GameplayBuffers.Level;
        RenderTarget2D tempA = (RenderTarget2D)GameplayBuffers.TempA;
        RenderTarget2D tempB = (RenderTarget2D)GameplayBuffers.TempB;

        device.SetRenderTarget(tempA);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.AlphaBlend, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(levelBuffer, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        // Rebuild the level as the clean image plus wake-masked copies. Each
        // masked layer is composed in tempB as: clear, draw the mask (black
        // outside the fan), multiply the shifted copy — so nothing leaks outside
        // the wake region, then it is accumulated additively into the level.
        device.SetRenderTarget(levelBuffer);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.Additive, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(tempA, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        DrawMaskedLayer(device, tempB, tempA, direction * offset, focus, direction, heat, new Color(255, 0, 0, 255));
        DrawMaskedLayer(device, tempB, tempA, -direction * offset, focus, direction, heat, new Color(0, 0, 255, 255));
        // A faint self-overlap copy inside the wake acts as a saturation boost.
        DrawMaskedLayer(device, tempB, tempA, Vector2.Zero, focus, direction, heat, new Color(52, 52, 52, 255));
    }

    private static void BeginSprite(BlendState blend, SamplerState sampler)
        => Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, blend, sampler,
            DepthStencilState.None, RasterizerState.CullNone);

    private static void DrawMaskedLayer(GraphicsDevice device, RenderTarget2D work, RenderTarget2D source,
        Vector2 shift, Vector2 focus, Vector2 direction, float heat, Color tint) {
        device.SetRenderTarget(work);
        device.Clear(Color.Transparent);
        BeginSprite(BlendState.AlphaBlend, SamplerState.LinearClamp);
        DrawWakeMask(focus, direction, heat);
        Draw.SpriteBatch.End();
        BeginSprite(MultiplyBlend, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(source, shift, tint);
        Draw.SpriteBatch.End();

        device.SetRenderTarget((RenderTarget2D)GameplayBuffers.Level);
        BeginSprite(BlendState.Additive, SamplerState.PointClamp);
        Draw.SpriteBatch.Draw(work, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();
    }

    // A fan-shaped mask: bright at the apex (right edge midpoint), fading toward
    // the left opening — used to clip the aberration layer to the wake region.
    private static void DrawWakeMask(Vector2 focus, Vector2 direction, float heat) {
        Texture2D mask = GetWakeMaskTexture();
        // The fan apex sits at the right edge of the texture and opens leftward;
        // aligning the +x axis with the motion points the fan back over the wake.
        float length = MathHelper.Clamp(56f + 130f * heat, 64f, 190f);
        float spread = MathHelper.Clamp(10f + 26f * heat, 12f, 40f);
        Draw.SpriteBatch.Draw(mask, focus, null, Color.White,
            MathF.Atan2(direction.Y, direction.X),
            new Vector2(mask.Width, mask.Height * 0.5f),
            new Vector2(length / mask.Width, spread / (mask.Height * 0.5f)),
            SpriteEffects.None, 0f);
    }

    // The water surface: propagate the classic two-buffer wave simulation, then
    // encode the height gradient as the displacement map. The field is anchored
    // to the world (shifted with the camera) so ripples stay where they were
    // stirred and spread outward on their own. The player is punched back out of
    // the field so the sprite is not smeared by its own wake.
    private static void RenderWaterField(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedWarp || level.FrozenOrPaused) return;
        Texture2D texture = fieldTexture ??= new Texture2D(Engine.Instance.GraphicsDevice, FieldWidth, FieldHeight);

        ShiftField(level.Camera.Position);
        for (int step = 0; step < FieldStepsPerFrame; step++)
            PropagateWave();

        List<Entity> players = level.Tracker.GetEntities<Player>();

        float gradientScale = FieldGradientScale * Intensity;
        for (int y = 0; y < FieldHeight; y++) {
            int row = y * FieldWidth;
            for (int x = 0; x < FieldWidth; x++) {
                int index = row + x;
                float wave = WaveCurrent[index];
                float gradientX = 0f, gradientY = 0f;
                if (x > 0 && x < FieldWidth - 1 && y > 0 && y < FieldHeight - 1 && wave != 0f) {
                    gradientX = WaveCurrent[index - 1] - WaveCurrent[index + 1];
                    gradientY = WaveCurrent[index - FieldWidth] - WaveCurrent[index + FieldWidth];
                }
                float shield = 1f;
                for (int p = 0; p < players.Count; p++) {
                    Player player = (Player)players[p];
                    Vector2 local = player.Center - fieldCamera;
                    float distanceX = x - local.X, distanceY = y - local.Y;
                    float distanceSquared = distanceX * distanceX + distanceY * distanceY;
                    if (distanceSquared < 121f) {
                        float falloff = MathF.Sqrt(distanceSquared) / 11f;
                        shield *= MathHelper.Clamp(falloff, 0f, 1f);
                    }
                }
                float weight = gradientScale * shield;
                FieldPixels[index] = new Color(new Vector4(
                    0.5f + gradientX * weight,
                    0.5f + gradientY * weight,
                    0f, 1f));
            }
        }
        texture.SetData(FieldPixels);

        // The hook's sprite batch is already begun with the camera transform and
        // alpha blending; draw the full field at its world anchor.
        Draw.SpriteBatch.Draw(texture, fieldCamera, Color.White);
    }

    // Keeps the simulation anchored to the world when the camera scrolls.
    private static void ShiftField(Vector2 camera) {
        int deltaX = (int)MathF.Floor(camera.X - fieldCamera.X);
        int deltaY = (int)MathF.Floor(camera.Y - fieldCamera.Y);
        if (deltaX == 0 && deltaY == 0) return;
        ShiftBuffer(WaveCurrent, deltaX, deltaY);
        ShiftBuffer(WavePrevious, deltaX, deltaY);
        fieldCamera += new Vector2(deltaX, deltaY);
    }

    private static void ShiftBuffer(float[] buffer, int deltaX, int deltaY) {
        Array.Clear(WaveScratch);
        for (int y = 0; y < FieldHeight; y++) {
            int sourceY = y - deltaY;
            if (sourceY < 0 || sourceY >= FieldHeight) continue;
            for (int x = 0; x < FieldWidth; x++) {
                int sourceX = x - deltaX;
                if (sourceX < 0 || sourceX >= FieldWidth) continue;
                WaveScratch[y * FieldWidth + x] = buffer[sourceY * FieldWidth + sourceX];
            }
        }
        Array.Copy(WaveScratch, buffer, buffer.Length);
    }

    // Classic two-buffer wave propagation: the next height is the neighbourhood
    // average minus the previous step, damped over time. Ripples spread,
    // oscillate through zero and fade out entirely on their own.
    private static void PropagateWave() {
        for (int y = 1; y < FieldHeight - 1; y++) {
            int row = y * FieldWidth;
            for (int x = 1; x < FieldWidth - 1; x++) {
                int index = row + x;
                float value = (WaveCurrent[index - 1] + WaveCurrent[index + 1]
                    + WaveCurrent[index - FieldWidth] + WaveCurrent[index + FieldWidth]) * 0.5f
                    - WavePrevious[index];
                WaveScratch[index] = value * FieldDamping;
            }
        }
        float[] swap = WavePrevious;
        WavePrevious = WaveCurrent;
        WaveCurrent = WaveScratch;
        WaveScratch = swap;
        // Edges stay at zero; the loop above never writes them.
    }

    private static void InjectWave(Level level, Vector2 world, float strength, float radius) {
        ShiftField(level.Camera.Position);
        Vector2 local = world - fieldCamera;
        int x0 = Math.Max(1, (int)(local.X - radius));
        int x1 = Math.Min(FieldWidth - 2, (int)(local.X + radius));
        int y0 = Math.Max(1, (int)(local.Y - radius));
        int y1 = Math.Min(FieldHeight - 2, (int)(local.Y + radius));
        for (int y = y0; y <= y1; y++) {
            for (int x = x0; x <= x1; x++) {
                float distance = MathF.Sqrt((x - local.X) * (x - local.X) + (y - local.Y) * (y - local.Y));
                float falloff = MathHelper.Clamp(1f - distance / radius, 0f, 1f);
                if (falloff > 0f)
                    WaveCurrent[y * FieldWidth + x] += strength * falloff;
            }
        }
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

    // A fan-shaped mask: bright at the apex (right edge midpoint), fading toward
    // the left opening, black everywhere else.
    private static Texture2D GetWakeMaskTexture() {
        if (wakeMaskTexture is { } texture) return texture;
        const int size = 64;
        Color[] pixels = new Color[size * size];
        Vector2 apex = new(size - 1f, (size - 1f) * 0.5f);
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                Vector2 offset = new(x - apex.X, y - apex.Y);
                float distance = offset.Length() / (size - 1f);
                float angle = MathF.Atan2(MathF.Abs(offset.Y), -offset.X);
                float wedge = MathHelper.Clamp(1f - (angle - MathF.PI / 8f) / (MathF.PI / 8f), 0f, 1f);
                float brightness = MathHelper.Clamp(1f - distance, 0f, 1f);
                brightness = MathF.Pow(brightness, 0.8f) * wedge;
                pixels[y * size + x] = new Color(new Vector4(brightness, brightness, brightness, 1f));
            }
        }
        wakeMaskTexture = new Texture2D(Engine.Instance.GraphicsDevice, size, size);
        wakeMaskTexture.SetData(pixels);
        return wakeMaskTexture;
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

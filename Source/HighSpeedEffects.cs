using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

/// <summary>
/// Motion feedback for the extreme speeds tech and gimmick maps reach, where
/// vanilla effects read as teleportation: an additive motion ribbon in world
/// space, a water-wake space crush driven through vanilla's displacement map
/// (with the player itself punched out of the field), wake-local chromatic
/// aberration with a saturation boost, sparks and wall-impact shockwaves.
/// Purely visual; physics are untouched.
/// </summary>
public static class HighSpeedEffects {
    private const int TrailSamples = 24;
    private const int CrushTextureWidth = 128;
    private const int CrushTextureHeight = 64;
    private const int DirectionSteps = 16;
    private const int ParticleCapacity = 128;

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

    // Displacement textures must be pre-rotated per direction: the vanilla
    // distort shader interprets the R/G channels as screen-space X/Y, so a
    // rotated sprite would rotate the pattern but not the push vectors.
    private static Texture2D[]? crushDirections;
    private static Texture2D[]? waveDirections;
    private static Texture2D? holeTexture;
    private static Texture2D? aberrationMaskTexture;

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
        DisposeTextures();
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
        PushSample(fx, self.Center, speed);

        if (Active && self.Scene is Level level && !level.FrozenOrPaused) {
            if (Settings.HighSpeedParticles)
                SpawnTrailParticles(self, fx, speed, threshold);
            // A sudden stop at high speed (wall impact, ground slam) releases the
            // built-up wake as a vanilla-style displacement burst plus sparks.
            float drop = fx.PreviousSpeed - speed;
            if (drop > 400f && fx.PreviousSpeed > threshold * 1.1f) {
                float power = MathHelper.Clamp(drop / 900f, 0.25f, 1.25f) * Intensity;
                level.Displacement.AddBurst(self.Center, 0.55f, 4f, 40f + 48f * power,
                    0.45f * power, Ease.QuadOut, Ease.QuadOut);
                SpawnImpactSparks(self.Center, fx.LastDirection, power);
            }
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
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        Draw.SpriteBatch.Draw(levelBuffer, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();

        // Build the wake-local layer: shifted red/blue fringes plus a copy of the
        // image itself (a saturation boost where it overlaps), then clip all of
        // it to a wake-shaped mask behind the player so the rest of the screen is
        // left untouched.
        device.SetRenderTarget(tempB);
        device.Clear(Color.Transparent);
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        Draw.SpriteBatch.Draw(tempA, direction * offset, new Color(255, 0, 0, 255));
        Draw.SpriteBatch.Draw(tempA, -direction * offset, new Color(0, 0, 255, 255));
        Draw.SpriteBatch.Draw(tempA, Vector2.Zero, new Color(52, 52, 52, 255)); // ~20% self-overlap
        Draw.SpriteBatch.End();
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, MultiplyBlend, SamplerState.LinearClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        DrawWakeMask(focus, direction, heat);
        Draw.SpriteBatch.End();

        device.SetRenderTarget(levelBuffer);
        device.Clear(Color.Transparent);
        Draw.SpriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Additive, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        Draw.SpriteBatch.Draw(tempA, Vector2.Zero, Color.White);
        Draw.SpriteBatch.Draw(tempB, Vector2.Zero, Color.White);
        Draw.SpriteBatch.End();
    }

    // A wake-shaped fan behind the player: narrow at the player, widening and
    // fading with distance, matching the space-crush corridor's extent.
    private static void DrawWakeMask(Vector2 focus, Vector2 direction, float heat) {
        Texture2D mask = GetAberrationMaskTexture();
        // The fan apex sits at the right edge of the texture and opens leftward;
        // aligning the +x axis with the motion points the fan back over the wake.
        float length = MathHelper.Clamp(40f + 120f * heat, 48f, 176f);
        float spread = MathHelper.Clamp(8f + 22f * heat, 10f, 34f);
        Draw.SpriteBatch.Draw(mask, focus, null, Color.White,
            MathF.Atan2(direction.Y, direction.X),
            new Vector2(mask.Width, mask.Height * 0.5f),
            new Vector2(length / mask.Width, spread / (mask.Height * 0.5f)),
            SpriteEffects.None, 0f);
    }

    // The wake: two layers per path segment. The fresh crush strip squeezes the
    // world aside right behind the player and closes again quickly; the wave lobe
    // starts narrow near the path and spreads outward as the segment ages, like
    // ripples propagating away from a boat's track. The player itself is punched
    // back out of the displacement field so its sprite stays crisp.
    private static void RenderSpaceCrush(Level? level) {
        if (level is null || !Active || !Settings.HighSpeedWarp || level.FrozenOrPaused) return;
        float intensity = Intensity;
        float threshold = MathF.Max(1f, Settings.HighSpeedThreshold);
        Texture2D[] crush = GetCrushTextures();
        Texture2D[] wave = GetWaveTextures();

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
                int angleIndex = (int)MathF.Round(MathF.Atan2(direction.Y, direction.X)
                    / (MathF.PI * 2f / DirectionSteps) + DirectionSteps) % DirectionSteps;

                float speed = fx.Speeds[(fx.Head - 1 - i + TrailSamples * 2) % TrailSamples];
                float speedFactor = MathHelper.Clamp(speed / threshold, 0f, 2.5f);
                if (speedFactor <= 0.4f) continue;
                float age = i / (float)(fx.Count - 1);
                float speedStrength = MathHelper.Clamp(speedFactor, 0f, 1.6f);
                // Kept at or below player height at the fresh end so the wake
                // collects into (nearly) a point at the player and only widens
                // once the ripple has travelled away.
                float halfWidth = 2.5f + 3.5f * MathHelper.Clamp(speedFactor, 0f, 1.3f);
                Vector2 centre = (newer + older) * 0.5f;
                Vector2 stretch = new Vector2(length, 1f) / CrushTextureWidth * 1.12f;

                // Squeeze layer: strong when fresh, gone by mid-history.
                float crushAlpha = fx.Heat * MathF.Pow(1f - age, 2.4f) * speedStrength * 0.5f * intensity;
                if (crushAlpha > 0.01f) {
                    Draw.SpriteBatch.Draw(crush[angleIndex], centre, null, Color.White * crushAlpha, 0f,
                        new Vector2(CrushTextureWidth, CrushTextureHeight) * 0.5f,
                        new Vector2(stretch.X, halfWidth * 2f / CrushTextureHeight), SpriteEffects.None, 0f);
                }

                // Wave layer: one outward lobe per side, widening as it ages out.
                float waveAlpha = fx.Heat * MathF.Pow(1f - age, 0.8f) * speedStrength * 0.35f * intensity;
                if (waveAlpha > 0.01f) {
                    float spread = (0.5f + 1.8f * age) * halfWidth * 1.6f;
                    Draw.SpriteBatch.Draw(wave[angleIndex], centre, null, Color.White * waveAlpha, 0f,
                        new Vector2(CrushTextureWidth, CrushTextureHeight) * 0.5f,
                        new Vector2(stretch.X, spread * 2f / CrushTextureHeight), SpriteEffects.None, 0f);
                }
            }
        }

        // Punch the player back out of the displacement field so the sprite is
        // not smeared by its own wake.
        Player? focusPlayer = FindFocusPlayer(level);
        if (focusPlayer != null) {
            float heat = Fx.GetOrCreateValue(focusPlayer).Heat;
            if (heat > 0f) {
                Texture2D hole = GetHoleTexture();
                float radius = 9f + 5f * heat;
                Draw.SpriteBatch.Draw(hole, focusPlayer.Center, null, Color.White, 0f,
                    new Vector2(hole.Width, hole.Height) * 0.5f,
                    radius * 2f / hole.Width, SpriteEffects.None, 0f);
            }
        }
    }

    private static void SpawnTrailParticles(Player player, PlayerFx fx, float speed, float threshold) {
        float spawnRate = fx.Heat * MathHelper.Clamp(speed / threshold, 0f, 2.5f);
        int count = (int)spawnRate / 2 + (NextFloat() < spawnRate % 2f ? 1 : 0);
        for (int i = 0; i < count && i < 4; i++) {
            bool streak = NextFloat() < 0.25f;
            Vector2 velocity = -fx.LastDirection * speed * 0.03f;
            if (streak) {
                SpawnParticle(new Particle {
                    Position = player.Center + Range(-Vector2.One * 6f, Vector2.One * 6f),
                    Velocity = velocity * 0.7f,
                    Life = 0.55f, MaxLife = 0.55f,
                    Length = 8f + NextFloat() * 14f,
                    Streak = true,
                    Tint = Color.Lerp(Color.Cyan, Color.White, NextFloat() * 0.6f),
                });
            } else {
                SpawnParticle(new Particle {
                    Position = player.Center + Range(-Vector2.One * 5f, Vector2.One * 5f),
                    Velocity = velocity + Range(-Vector2.One * 40f, Vector2.One * 40f),
                    Life = 0.35f + NextFloat() * 0.35f,
                    MaxLife = 0.7f,
                    Length = 1f + NextFloat(),
                    Streak = false,
                    Tint = Color.Lerp(Color.Cyan, Color.White, NextFloat() * 0.7f),
                });
            }
        }
    }

    private static void SpawnImpactSparks(Vector2 centre, Vector2 direction, float power) {
        int count = (int)(18 + 18 * power);
        for (int i = 0; i < count; i++) {
            float angle = NextFloat() * MathF.PI * 2f;
            Vector2 radial = Calc.AngleToVector(angle, 40f + 130f * power * NextFloat());
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

    private static float NextFloat() => (float)Random.NextDouble();

    private static Vector2 Range(Vector2 min, Vector2 max)
        => new(min.X + NextFloat() * (max.X - min.X), min.Y + NextFloat() * (max.Y - min.Y));

    private static void SpawnParticle(Particle particle) {
        Particles[particleCursor] = particle;
        particleCursor = (particleCursor + 1) % ParticleCapacity;
    }

    private static void UpdateParticles(float delta) {
        for (int i = 0; i < ParticleCapacity; i++) {
            if (Particles[i].Life > 0f) {
                Particles[i].Life -= delta;
                Particles[i].Position += Particles[i].Velocity * delta;
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
                    particle.Tint * (0.35f * alpha), 1f);
            } else {
                float size = particle.Length * alpha;
                Draw.Rect(particle.Position - Vector2.One * size * 0.5f, size, size, particle.Tint * (0.55f * alpha));
            }
        }
    }

    // Displacement textures are generated per direction: the pixel pattern is the
    // rotated base strip and the R/G channels already carry world-space vectors,
    // because the vanilla distort shader reads them as screen X/Y offsets.
    private static Texture2D[] CreateDirectionalTextures(
        Func<float, float, float, Vector2> field) {
        Texture2D[] textures = new Texture2D[DirectionSteps];
        Vector2 centre = new(CrushTextureWidth * 0.5f, CrushTextureHeight * 0.5f);
        Vector2 half = new(CrushTextureWidth * 0.5f - 1f, CrushTextureHeight * 0.5f - 1f);
        for (int k = 0; k < DirectionSteps; k++) {
            float angle = k * MathF.PI * 2f / DirectionSteps;
            Vector2 path = Calc.AngleToVector(angle, 1f);
            Vector2 normal = new(-path.Y, path.X);
            Color[] pixels = new Color[CrushTextureWidth * CrushTextureHeight];
            for (int y = 0; y < CrushTextureHeight; y++) {
                for (int x = 0; x < CrushTextureWidth; x++) {
                    // Rotate the pixel back into the strip's own frame.
                    Vector2 local = new((x - centre.X) / half.X, (y - centre.Y) / half.Y);
                    Vector2 rotated = new(
                        local.X * MathF.Cos(-angle) - local.Y * MathF.Sin(-angle),
                        local.X * MathF.Sin(-angle) + local.Y * MathF.Cos(-angle));
                    float along = MathHelper.Clamp(rotated.X * 0.5f + 0.5f, 0f, 1f);
                    float across = MathHelper.Clamp(rotated.Y, -1f, 1f);
                    Vector2 push = field(MathF.Abs(across), MathF.Sign(across), along);
                    pixels[y * CrushTextureWidth + x] = new Color(new Vector4(
                        0.5f + 0.5f * (push.X * path.X + push.Y * normal.X),
                        0.5f + 0.5f * (push.X * path.Y + push.Y * normal.Y),
                        0f, 1f));
                }
            }
            textures[k] = new Texture2D(Engine.Instance.GraphicsDevice, CrushTextureWidth, CrushTextureHeight);
            textures[k].SetData(pixels);
        }
        return textures;
    }

    private static Texture2D[] GetCrushTextures()
        => crushDirections ??= CreateDirectionalTextures((acrossAbs, acrossSign, along) => {
            float cap = MathF.Pow(MathF.Sin(MathF.PI * along), 0.35f);
            float push = acrossAbs * (1f - acrossAbs) * 4f;
            return new Vector2(-0.28f * (1f - acrossAbs), acrossSign * push) * cap;
        });

    private static Texture2D[] GetWaveTextures()
        => waveDirections ??= CreateDirectionalTextures((acrossAbs, acrossSign, along) => {
            float cap = MathF.Pow(MathF.Sin(MathF.PI * along), 0.35f);
            float lobe = acrossAbs <= 0.15f ? 0f : MathF.Sin(MathF.PI * (acrossAbs - 0.15f) / 0.85f);
            return new Vector2(0f, acrossSign * lobe * cap);
        });

    // A soft round hole of neutral displacement (0.5, 0.5) with premultiplied
    // alpha, stamped over the player to cancel the wake around the sprite.
    private static Texture2D GetHoleTexture() {
        if (holeTexture is { } texture) return texture;
        const int size = 32;
        Color[] pixels = new Color[size * size];
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                Vector2 offset = new(x - (size - 1) * 0.5f, y - (size - 1) * 0.5f);
                float distance = offset.Length() / ((size - 1) * 0.5f);
                float alpha = MathHelper.Clamp(1f - distance * distance, 0f, 1f);
                pixels[y * size + x] = new Color(new Vector4(0.5f * alpha, 0.5f * alpha, 0f, alpha));
            }
        }
        holeTexture = new Texture2D(Engine.Instance.GraphicsDevice, size, size);
        holeTexture.SetData(pixels);
        return holeTexture;
    }

    // A fan-shaped mask: bright at the apex (right edge midpoint), fading toward
    // the left opening — used to clip the aberration layer to the wake region.
    private static Texture2D GetAberrationMaskTexture() {
        if (aberrationMaskTexture is { } texture) return texture;
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
        aberrationMaskTexture = new Texture2D(Engine.Instance.GraphicsDevice, size, size);
        aberrationMaskTexture.SetData(pixels);
        return aberrationMaskTexture;
    }

    private static void DisposeTextures() {
        if (crushDirections is { } crush) foreach (Texture2D texture in crush) texture.Dispose();
        if (waveDirections is { } wave) foreach (Texture2D texture in wave) texture.Dispose();
        crushDirections = null;
        waveDirections = null;
        holeTexture?.Dispose();
        holeTexture = null;
        aberrationMaskTexture?.Dispose();
        aberrationMaskTexture = null;
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

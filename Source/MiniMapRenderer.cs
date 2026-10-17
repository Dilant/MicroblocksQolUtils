using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using System.Runtime.CompilerServices;

namespace Celeste.Mod.MicroblocksQolUtils;

public static class MiniMapRenderer {
    private const float ScreenWidth = 1920f;
    private const float Margin = 22f;
    private static readonly ConditionalWeakTable<SolidTiles, SolidRunCache> SolidRuns = new();
    private static readonly ConditionalWeakTable<Level, MapFocusState> MapFocusStates = new();
    private static Texture2D? solidPixel;

    public static void Dispose() {
        solidPixel?.Dispose();
        solidPixel = null;
    }

    public static float Render(Level level) {
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (!settings.MiniMapEnabled) return 0f;
        Player? player = level.Tracker.GetEntity<Player>();
        SolidTiles? solids = level.Tracker.GetEntity<SolidTiles>();
        if (solids is null || !TryResolvePlayerCenter(level, player, out Vector2 playerCenter)) return 0f;

        float size = settings.MiniMapSize;
        float radius = size / 2f;
        Vector2 center = new(ScreenWidth - Margin - radius, Margin + radius);
        float pixelsPerWorld = ResolveScale(level, size, settings.MiniMapZoom);
        Color levelBackground = level.BackgroundColor;
        MaterialPalette palette = MaterialPalette.FromSeed(
            settings.MiniMapAdaptiveColors
                ? levelBackground
                : AreaData.Get(level.Session.Area)?.TitleBaseColor ?? new Color(126, 99, 184)
        );
        Color background = palette.SurfaceHigh * (settings.MiniMapBackgroundOpacity / 10f);
        if (settings.MiniMapBackground) {
            if (settings.MiniMapShape == MiniMapShape.Circle) MaterialUi.Circle(center, radius, background);
            else MaterialUi.RoundedRect(center.X - radius, center.Y - radius, size, size, 24f, background);
        }

        Color mapBackdrop = settings.MiniMapBackground
            ? CompositeOver(background, levelBackground)
            : levelBackground;
        Color terrainColor = settings.MiniMapAdaptiveColors
            ? AdaptiveForeground(mapBackdrop) * 0.9f
            : Color.SlateGray * 0.9f;
        if (settings.MiniMapRoomBackgrounds && settings.MiniMapRoomBackgroundOpacity > 0)
            DrawRoomBackgrounds(level, playerCenter, center, radius, pixelsPerWorld, settings.MiniMapShape,
                settings.MiniMapRoomBackgroundOpacity / 10f);
        if (settings.MiniMapRoomBounds) {
            IReadOnlySet<string>? route = settings.MiniMapHighlightRoute
                ? RoomRouteCache.RouteToGoal(level)
                : null;
            DrawRoomBounds(level, playerCenter, center, radius, pixelsPerWorld, settings.MiniMapShape,
                terrainColor * 0.52f, palette.Primary * 0.9f, new Color(255, 190, 64) * 0.95f, route);
        }
        DrawSolids(solids, playerCenter, center, radius, pixelsPerWorld, settings.MiniMapShape, terrainColor);
        if (settings.MiniMapCollectibles && level.Session.MapData is MapData map)
            DrawCollectibles(map, level, playerCenter, center, radius, pixelsPerWorld, settings);
        foreach (RemotePlayer remote in MiaoNetBridge.Players) {
            if (!settings.ShowMiaoNetPlayers) break;
            DrawRemote(remote, playerCenter, center, radius, pixelsPerWorld, settings);
        }
        DrawLocalPlayer(center, settings.MiniMapAvatarShape);

        Color border = settings.MiniMapAdaptiveColors
            ? AdaptiveForeground(mapBackdrop) * 0.8f
            : palette.Outline;
        if (settings.MiniMapBorder) {
            if (settings.MiniMapShape == MiniMapShape.Circle) MaterialUi.CircleOutline(center, radius, 2f, border);
            else MaterialUi.RoundedOutline(center.X - radius, center.Y - radius, size, size, 24f, 2f, border);
        }

        List<string> data = [];
        if (settings.ShowRoomsRemaining) {
            int? rooms = RoomRouteCache.RoomsToGoal(level);
            data.Add(rooms is int count ? $"还剩 {count} 面" : "还剩 ? 面");
        }
        if (settings.ShowMapPlayerCount) data.Add($"{MiaoNetBridge.PlayersInMap} 人");
        if (settings.ShowClock) data.Add(DateTime.Now.ToString("HH:mm:ss"));
        float reservedBottom = Margin + size;
        if (data.Count > 0) {
            string text = string.Join("  ·  ", data);
            Vector2 textPosition = center + new Vector2(0f, radius + 10f);
            const float preferredScale = 0.42f;
            float availableWidth = Math.Max(1f, size - 24f);
            Vector2 preferredSize = SystemTtfFont.Measure(text, preferredScale);
            float scale = preferredSize.X <= availableWidth
                ? preferredScale
                : preferredScale * availableWidth / preferredSize.X;
            Vector2 measured = SystemTtfFont.Measure(text, scale);
            if (settings.HudMaterialSurfaces) {
                MaterialUi.AcrylicSurface(
                    textPosition.X - measured.X / 2f - 12f,
                    textPosition.Y - 5f,
                    measured.X + 24f,
                    measured.Y + 10f,
                    14f,
                    palette.SurfaceHigh * 0.92f,
                    palette.Outline
                );
            }
            SystemTtfFont.Draw(
                text,
                textPosition,
                new Vector2(0.5f, 0f),
                scale,
                palette.OnSurface,
                settings.HudMaterialSurfaces ? 0f : 1f,
                Color.Black
            );
            reservedBottom = textPosition.Y + measured.Y + 5f;
        }
        return reservedBottom;
    }

    private static bool TryResolvePlayerCenter(Level level, Player? player, out Vector2 center) {
        MapFocusState state = MapFocusStates.GetValue(level, static _ => new MapFocusState());
        if (player is not null) {
            center = player.Center;
            state.LastPlayerCenter = center;
            state.HasPlayerCenter = true;
            return true;
        }

        PlayerDeadBody? deadBody = PlayerLifecycle.Latest.DeadBody;
        if (deadBody is null) {
            center = default;
            return false;
        }

        // Player.Die removes the Player immediately and replaces it with a body for the
        // animation. Keep using the exact collider center captured on the preceding frame;
        // Position is only a fallback for a death before this HUD has rendered once.
        center = state.HasPlayerCenter ? state.LastPlayerCenter : deadBody.Position;
        return true;
    }

    private static float ResolveScale(Level level, float size, int zoom) {
        if (zoom > 0) return 0.24f * zoom;
        Rectangle room = level.Bounds;
        float largestDimension = Math.Max(1f, Math.Max(room.Width, room.Height));
        return Math.Max(0.02f, (size - 28f) / largestDimension);
    }

    private static void DrawRoomBounds(
        Level level,
        Vector2 player,
        Vector2 center,
        float radius,
        float scale,
        MiniMapShape shape,
        Color color,
        Color currentColor,
        Color routeColor,
        IReadOnlySet<string>? route
    ) {
        MapData? map = level.Session.MapData;
        if (map is null) return;
        foreach (LevelData room in map.Levels) {
            if (room.Dummy) continue;
            Rectangle bounds = room.Bounds;
            Vector2 topLeft = center + (new Vector2(bounds.Left, bounds.Top) - player) * scale;
            Vector2 topRight = center + (new Vector2(bounds.Right, bounds.Top) - player) * scale;
            Vector2 bottomRight = center + (new Vector2(bounds.Right, bounds.Bottom) - player) * scale;
            Vector2 bottomLeft = center + (new Vector2(bounds.Left, bounds.Bottom) - player) * scale;
            bool current = string.Equals(room.Name, level.Session.Level, StringComparison.Ordinal);
            bool onRoute = route?.Contains(room.Name) == true;
            Color lineColor = onRoute
                ? current ? Color.Lerp(routeColor, Color.White, 0.35f) : routeColor
                : current ? currentColor : color;
            float thickness = onRoute ? 3f : current ? 2.25f : 1.5f;
            DrawClippedLine(topLeft, topRight, center, radius - 2f, shape, lineColor, thickness);
            DrawClippedLine(topRight, bottomRight, center, radius - 2f, shape, lineColor, thickness);
            DrawClippedLine(bottomRight, bottomLeft, center, radius - 2f, shape, lineColor, thickness);
            DrawClippedLine(bottomLeft, topLeft, center, radius - 2f, shape, lineColor, thickness);
        }
    }

    private static void DrawRoomBackgrounds(
        Level level,
        Vector2 player,
        Vector2 center,
        float radius,
        float scale,
        MiniMapShape shape,
        float opacity
    ) {
        MapData? map = level.Session.MapData;
        if (map is null) return;
        int roomIndex = 0;
        foreach (LevelData room in map.Levels) {
            if (room.Dummy) continue;
            Rectangle bounds = room.Bounds;
            float left = center.X + (bounds.Left - player.X) * scale;
            float top = center.Y + (bounds.Top - player.Y) * scale;
            float right = center.X + (bounds.Right - player.X) * scale;
            float bottom = center.Y + (bounds.Bottom - player.Y) * scale;
            Color roomColor = RoomColor(roomIndex++) * opacity;
            DrawClippedRoomBackground(left, top, right, bottom, center, radius - 2f, shape, roomColor);
        }
    }

    private static void DrawClippedRoomBackground(
        float left,
        float top,
        float right,
        float bottom,
        Vector2 center,
        float radius,
        MiniMapShape shape,
        Color color
    ) {
        float clipLeft = Math.Max(left, center.X - radius);
        float clipTop = Math.Max(top, center.Y - radius);
        float clipRight = Math.Min(right, center.X + radius);
        float clipBottom = Math.Min(bottom, center.Y + radius);
        if (clipRight <= clipLeft || clipBottom <= clipTop) return;

        if (shape == MiniMapShape.Square) {
            Draw.Rect(clipLeft, clipTop, clipRight - clipLeft, clipBottom - clipTop, color);
            return;
        }

        int firstRow = (int)MathF.Floor(clipTop);
        int lastRow = (int)MathF.Ceiling(clipBottom);
        for (int row = firstRow; row < lastRow; row++) {
            float rowTop = Math.Max(clipTop, row);
            float rowBottom = Math.Min(clipBottom, row + 1f);
            if (rowBottom <= rowTop) continue;
            float sampleY = Math.Clamp((rowTop + rowBottom) / 2f, center.Y - radius, center.Y + radius);
            float deltaY = sampleY - center.Y;
            float halfWidth = MathF.Sqrt(Math.Max(0f, radius * radius - deltaY * deltaY));
            float rowLeft = Math.Max(clipLeft, center.X - halfWidth);
            float rowRight = Math.Min(clipRight, center.X + halfWidth);
            if (rowRight > rowLeft)
                Draw.Rect(rowLeft, rowTop, rowRight - rowLeft, rowBottom - rowTop, color);
        }
    }

    private static Color RoomColor(int index) {
        float hue = index * 0.61803398875f % 1f;
        return Hsv(hue, 0.58f, 0.92f);
    }

    private static Color Hsv(float hue, float saturation, float value) {
        float scaled = hue * 6f;
        int sector = (int)MathF.Floor(scaled);
        float fraction = scaled - sector;
        float low = value * (1f - saturation);
        float falling = value * (1f - saturation * fraction);
        float rising = value * (1f - saturation * (1f - fraction));
        (float red, float green, float blue) = (sector % 6) switch {
            0 => (value, rising, low),
            1 => (falling, value, low),
            2 => (low, value, rising),
            3 => (low, falling, value),
            4 => (rising, low, value),
            _ => (value, low, falling)
        };
        return new Color(red, green, blue);
    }

    private static void DrawSolids(
        SolidTiles solids,
        Vector2 player,
        Vector2 center,
        float radius,
        float scale,
        MiniMapShape shape,
        Color color
    ) {
        float tileSize = 8f * scale;
        float safeRadius = radius - Math.Max(1f, tileSize);
        if (safeRadius <= 0f) return;

        float originX = center.X + (solids.Position.X - player.X) * scale;
        float originY = center.Y + (solids.Position.Y - player.Y) * scale;
        Texture2D pixel = GetSolidPixel();
        foreach (SolidRun run in SolidRuns.GetValue(solids, static value => new SolidRunCache(value)).Runs) {
            float rowCenter = originY + (run.Y + 0.5f) * tileSize;
            float deltaY = rowCenter - center.Y;
            float halfWidth;
            if (shape == MiniMapShape.Circle) {
                float remaining = safeRadius * safeRadius - deltaY * deltaY;
                if (remaining < 0f) continue;
                halfWidth = MathF.Sqrt(remaining);
            } else {
                if (Math.Abs(deltaY) > safeRadius) continue;
                halfWidth = safeRadius;
            }

            float minimumCenterX = center.X - halfWidth;
            float maximumCenterX = center.X + halfWidth;
            int first = Math.Max(run.Start,
                (int)MathF.Ceiling((minimumCenterX - originX) / tileSize - 0.5f));
            int end = Math.Min(run.End,
                (int)MathF.Floor((maximumCenterX - originX) / tileSize - 0.5f) + 1);
            if (end <= first) continue;

            // Draw.Rect truncates every float independently and samples a one-pixel region from
            // an atlas. At non-integer zooms (or with a linear HUD sampler), doing that per tile
            // exposes the atlas edge and creates the conspicuous grid. A dedicated 1x1 texture,
            // one quad per continuous run, and shared integer boundaries keep adjacent rows flush.
            int left = (int)MathF.Floor(originX + first * tileSize);
            int right = (int)MathF.Floor(originX + end * tileSize);
            int top = (int)MathF.Floor(originY + run.Y * tileSize);
            int bottom = (int)MathF.Floor(originY + (run.Y + 1) * tileSize);
            if (right <= left || bottom <= top) continue;
            Draw.SpriteBatch.Draw(pixel, new Rectangle(left, top, right - left, bottom - top), color);
        }
    }

    private static Texture2D GetSolidPixel() {
        GraphicsDevice graphics = Engine.Graphics.GraphicsDevice;
        if (solidPixel is not null
            && !solidPixel.IsDisposed
            && ReferenceEquals(solidPixel.GraphicsDevice, graphics)) return solidPixel;

        solidPixel?.Dispose();
        solidPixel = new Texture2D(graphics, 1, 1);
        solidPixel.SetData(new[] { Color.White });
        return solidPixel;
    }

    private static void DrawCollectibles(
        MapData map,
        Level level,
        Vector2 player,
        Vector2 center,
        float radius,
        float scale,
        QolSettings settings
    ) {
        IReadOnlySet<string>? nearbyRooms = settings.MiniMapShowNearbyRoomStrawberries
            ? RoomRouteCache.NearbyRooms(level)
            : null;
        foreach (MiniMapCollectible collectible in MapCollectibleCache.Get(map)) {
            Vector2 point = center + (collectible.Position - player) * scale;
            bool offscreen = !Inside(point, center, radius - 14f, settings.MiniMapShape);
            if (offscreen) {
                if (!IsStrawberry(collectible.Kind) || nearbyRooms?.Contains(collectible.Room) != true) continue;
                point = ClampToEdge(point, center, radius - 14f, settings.MiniMapShape);
            }
            DrawCollectible(point, collectible.Kind, collectible.IsCollected(level.Session));
        }
    }

    private static void DrawCollectible(Vector2 point, MiniMapCollectibleKind kind, bool collected) {
        float alpha = collected ? 0.42f : 1f;
        Color shadow = Color.Black * (0.82f * alpha);
        MaterialUi.Circle(point, 7f, shadow);
        switch (kind) {
            case MiniMapCollectibleKind.Strawberry:
                MaterialUi.Circle(point + new Vector2(0f, 1f), 4.5f, new Color(244, 67, 83) * alpha);
                MaterialUi.Line(point + new Vector2(-3f, -3f), point + new Vector2(0f, -5f), 2f,
                    new Color(105, 220, 120) * alpha);
                MaterialUi.Line(point + new Vector2(3f, -3f), point + new Vector2(0f, -5f), 2f,
                    new Color(105, 220, 120) * alpha);
                break;
            case MiniMapCollectibleKind.GoldenBerry:
                MaterialUi.Circle(point, 5f, new Color(255, 193, 7) * alpha);
                MaterialUi.CircleOutline(point, 5f, 1.25f, new Color(255, 245, 185) * alpha);
                break;
            case MiniMapCollectibleKind.MoonBerry:
                MaterialUi.Circle(point, 5f, new Color(156, 115, 255) * alpha);
                MaterialUi.CircleOutline(point, 5f, 1.25f, new Color(112, 230, 255) * alpha);
                break;
            case MiniMapCollectibleKind.Heart:
                Color heart = new Color(255, 96, 180) * alpha;
                MaterialUi.Circle(point + new Vector2(-2.2f, -1.7f), 3.2f, heart);
                MaterialUi.Circle(point + new Vector2(2.2f, -1.7f), 3.2f, heart);
                MaterialUi.Line(point + new Vector2(-4f, 0f), point + new Vector2(0f, 5f), 3.5f, heart);
                MaterialUi.Line(point + new Vector2(4f, 0f), point + new Vector2(0f, 5f), 3.5f, heart);
                break;
            case MiniMapCollectibleKind.Cassette:
                Color cassette = new Color(78, 178, 255) * alpha;
                Draw.Rect(point.X - 5f, point.Y - 4f, 10f, 8f, cassette);
                MaterialUi.Circle(point + new Vector2(-2.5f, 0f), 1.3f, Color.White * alpha);
                MaterialUi.Circle(point + new Vector2(2.5f, 0f), 1.3f, Color.White * alpha);
                break;
            case MiniMapCollectibleKind.Key:
                Color key = new Color(255, 224, 92) * alpha;
                MaterialUi.CircleOutline(point + new Vector2(-2f, -1.5f), 3f, 1.8f, key);
                MaterialUi.Line(point, point + new Vector2(5f, 4.5f), 2f, key);
                MaterialUi.Line(point + new Vector2(3f, 2.5f), point + new Vector2(5f, 0.5f), 1.5f, key);
                break;
            case MiniMapCollectibleKind.Gem:
                Color gem = new Color(80, 224, 230) * alpha;
                DrawDiamond(point, 5.5f, gem, 2f);
                break;
        }

        if (collected && IsStrawberry(kind)) DrawCollectedBadge(point + new Vector2(5f, 5f));
    }

    private static bool IsStrawberry(MiniMapCollectibleKind kind) =>
        kind is MiniMapCollectibleKind.Strawberry
            or MiniMapCollectibleKind.GoldenBerry
            or MiniMapCollectibleKind.MoonBerry;

    private static void DrawCollectedBadge(Vector2 center) {
        MaterialUi.Circle(center, 4.25f, Color.Black * 0.9f);
        MaterialUi.Circle(center, 3.25f, new Color(76, 210, 120));
        MaterialUi.Line(center + new Vector2(-1.8f, 0f), center + new Vector2(-0.4f, 1.5f), 1.25f, Color.White);
        MaterialUi.Line(center + new Vector2(-0.4f, 1.5f), center + new Vector2(2f, -1.7f), 1.25f, Color.White);
    }

    private static void DrawDiamond(Vector2 center, float radius, Color color, float thickness) {
        Vector2 top = center + new Vector2(0f, -radius);
        Vector2 right = center + new Vector2(radius, 0f);
        Vector2 bottom = center + new Vector2(0f, radius);
        Vector2 left = center + new Vector2(-radius, 0f);
        MaterialUi.Line(top, right, thickness, color);
        MaterialUi.Line(right, bottom, thickness, color);
        MaterialUi.Line(bottom, left, thickness, color);
        MaterialUi.Line(left, top, thickness, color);
    }

    private static void DrawRemote(
        RemotePlayer remote,
        Vector2 player,
        Vector2 center,
        float radius,
        float scale,
        QolSettings settings
    ) {
        Vector2 point = center + (remote.Position - player) * scale;
        bool offscreen = !Inside(point, center, radius - 12f, settings.MiniMapShape);
        if (offscreen) {
            if (!settings.MiniMapShowOffscreenPlayers) return;
            point = ClampToEdge(point, center, radius - 12f, settings.MiniMapShape);
        }
        DrawAvatarBackdrop(point, settings.MiniMapAvatarShape, Color.Black * 0.85f);
        if (!MiaoNetBridge.TryDrawAvatar(remote.Id, point, 20f, Color.White, settings.MiniMapAvatarShape)) {
            MaterialUi.Circle(point, 8f, remote.Color);
            string initial = remote.Name.Length == 0 ? "?" : remote.Name[..1];
            SystemTtfFont.Draw(initial, point + new Vector2(0f, -1f), new Vector2(0.5f), 0.27f, Color.White, 1f);
        }

        bool showName = settings.MiniMapNames == MiniMapNameMode.Everyone
            || settings.MiniMapNames == MiniMapNameMode.WatchedOnly && WatchList.Contains(remote.Name);
        if (showName) {
            Vector2 labelOffset = offscreen
                ? -Vector2.Normalize(point - center) * 17f
                : new Vector2(0f, 13f);
            Vector2 justify = offscreen ? new Vector2(0.5f) : new Vector2(0.5f, 0f);
            SystemTtfFont.Draw(remote.Name, point + labelOffset, justify, 0.25f, Color.White, 1f);
        }
    }

    private static void DrawLocalPlayer(Vector2 center, MiniMapAvatarShape avatarShape) {
        if (MiaoNetBridge.LoggedIn && MiaoNetBridge.LocalPlayer is RemotePlayer local) {
            DrawAvatarBackdrop(center, avatarShape, Color.Black * 0.9f);
            if (MiaoNetBridge.TryDrawAvatar(local.Id, center, 20f, Color.White, avatarShape)) return;
            MaterialUi.Circle(center, 8f, local.Color);
            return;
        }
        MaterialUi.Circle(center, 6f, Color.Cyan);
        MaterialUi.CircleOutline(center, 7.5f, 1.5f, Color.White * 0.8f);
    }

    private static void DrawAvatarBackdrop(Vector2 center, MiniMapAvatarShape shape, Color color) {
        if (shape == MiniMapAvatarShape.Circle) MaterialUi.Circle(center, 11f, color);
        else MaterialUi.RoundedRect(center.X - 11f, center.Y - 11f, 22f, 22f, 2f, color);
    }

    private static Vector2 ClampToEdge(Vector2 point, Vector2 center, float radius, MiniMapShape shape) {
        Vector2 delta = point - center;
        if (delta.LengthSquared() < 0.0001f) return center;
        if (shape == MiniMapShape.Circle) return center + Vector2.Normalize(delta) * radius;
        float factor = radius / Math.Max(Math.Abs(delta.X), Math.Abs(delta.Y));
        return center + delta * factor;
    }

    private static bool Inside(Vector2 point, Vector2 center, float radius, MiniMapShape shape) {
        Vector2 delta = point - center;
        return shape == MiniMapShape.Circle
            ? delta.LengthSquared() <= radius * radius
            : Math.Abs(delta.X) <= radius && Math.Abs(delta.Y) <= radius;
    }

    private static void DrawClippedLine(
        Vector2 from,
        Vector2 to,
        Vector2 center,
        float radius,
        MiniMapShape shape,
        Color color,
        float thickness
    ) {
        if (!TryClipLine(ref from, ref to, center, radius, shape)) return;
        MaterialUi.Line(from, to, thickness, color);
    }

    private static bool TryClipLine(
        ref Vector2 from,
        ref Vector2 to,
        Vector2 center,
        float radius,
        MiniMapShape shape
    ) {
        if (shape == MiniMapShape.Square)
            return ClipToSquare(ref from, ref to, center, radius);

        Vector2 localFrom = from - center;
        Vector2 delta = to - from;
        float a = Vector2.Dot(delta, delta);
        if (a < 0.0001f) return localFrom.LengthSquared() <= radius * radius;
        float b = 2f * Vector2.Dot(localFrom, delta);
        float c = Vector2.Dot(localFrom, localFrom) - radius * radius;
        float discriminant = b * b - 4f * a * c;
        bool fromInside = c <= 0f;
        bool toInside = (to - center).LengthSquared() <= radius * radius;
        if (fromInside && toInside) return true;
        if (discriminant < 0f) return false;
        float root = MathF.Sqrt(discriminant);
        float first = (-b - root) / (2f * a);
        float second = (-b + root) / (2f * a);
        float start = Math.Clamp(Math.Min(first, second), 0f, 1f);
        float end = Math.Clamp(Math.Max(first, second), 0f, 1f);
        if (end <= start) return false;
        Vector2 original = from;
        from = original + delta * start;
        to = original + delta * end;
        return true;
    }

    private static bool ClipToSquare(ref Vector2 from, ref Vector2 to, Vector2 center, float radius) {
        Vector2 delta = to - from;
        float t0 = 0f;
        float t1 = 1f;
        float left = center.X - radius;
        float right = center.X + radius;
        float top = center.Y - radius;
        float bottom = center.Y + radius;
        if (!ClipTest(-delta.X, from.X - left, ref t0, ref t1)
            || !ClipTest(delta.X, right - from.X, ref t0, ref t1)
            || !ClipTest(-delta.Y, from.Y - top, ref t0, ref t1)
            || !ClipTest(delta.Y, bottom - from.Y, ref t0, ref t1)) return false;
        Vector2 original = from;
        from = original + delta * t0;
        to = original + delta * t1;
        return true;
    }

    private static bool ClipTest(float p, float q, ref float t0, ref float t1) {
        if (Math.Abs(p) < 0.0001f) return q >= 0f;
        float ratio = q / p;
        if (p < 0f) {
            if (ratio > t1) return false;
            if (ratio > t0) t0 = ratio;
        } else {
            if (ratio < t0) return false;
            if (ratio < t1) t1 = ratio;
        }
        return true;
    }

    private static Color CompositeOver(Color source, Color destination) {
        float inverseAlpha = 1f - source.A / 255f;
        return new Color(
            (byte)Math.Clamp(source.R + destination.R * inverseAlpha, 0f, 255f),
            (byte)Math.Clamp(source.G + destination.G * inverseAlpha, 0f, 255f),
            (byte)Math.Clamp(source.B + destination.B * inverseAlpha, 0f, 255f),
            255
        );
    }

    private static Color AdaptiveForeground(Color background) {
        float luminance = RelativeLuminance(background);
        float whiteContrast = 1.05f / (luminance + 0.05f);
        float darkContrast = (luminance + 0.05f) / 0.05f;
        return whiteContrast >= darkContrast
            ? new Color(238, 244, 248)
            : new Color(28, 34, 39);
    }

    private static float RelativeLuminance(Color color) =>
        0.2126f * LinearChannel(color.R)
        + 0.7152f * LinearChannel(color.G)
        + 0.0722f * LinearChannel(color.B);

    private static float LinearChannel(byte channel) {
        float value = channel / 255f;
        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private readonly record struct SolidRun(int Y, int Start, int End);

    private sealed class SolidRunCache {
        public List<SolidRun> Runs { get; } = [];

        public SolidRunCache(SolidTiles solids) {
            for (int y = 0; y < solids.Grid.CellsY; y++) {
                int x = 0;
                while (x < solids.Grid.CellsX) {
                    while (x < solids.Grid.CellsX && !solids.Grid[x, y]) x++;
                    int start = x;
                    while (x < solids.Grid.CellsX && solids.Grid[x, y]) x++;
                    if (x > start) Runs.Add(new SolidRun(y, start, x));
                }
            }
        }
    }

    private sealed class MapFocusState {
        public Vector2 LastPlayerCenter;
        public bool HasPlayerCenter;
    }
}

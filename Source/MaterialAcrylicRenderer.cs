using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MaterialAcrylicRenderer {
    private static VirtualRenderTarget? sceneTarget;
    private static VirtualRenderTarget? blurTemporary;
    private static VirtualRenderTarget? blurredTarget;
    private static IMaterialAcrylicPage? activePage;
    private static bool rendering;
    private static bool failed;
    private static int successfulFrames;

    internal static bool Failed => failed;
    internal static int SuccessfulFrames => successfulFrames;

    internal static bool CapturedSceneLooksVisible() {
        if (sceneTarget is null || sceneTarget.IsDisposed) return false;
        RenderTarget2D target = sceneTarget;
        Color[] pixels = new Color[target.Width * target.Height];
        target.GetData(pixels);
        int stride = Math.Max(1, pixels.Length / 4096);
        int samples = 0;
        int visibleSamples = 0;
        for (int index = 0; index < pixels.Length; index += stride) {
            Color color = pixels[index];
            samples++;
            if (color.A > 32 && Math.Max(color.R, Math.Max(color.G, color.B)) > 12) visibleSamples++;
        }
        return visibleSamples >= Math.Max(1, samples / 100);
    }

    public static void Load() {
        successfulFrames = 0;
        On.Monocle.Engine.RenderCore += RenderCore;
        On.Monocle.Scene.Render += RenderScene;
    }

    public static void Unload() {
        On.Monocle.Scene.Render -= RenderScene;
        On.Monocle.Engine.RenderCore -= RenderCore;
        activePage = null;
        DisposeTargets();
        failed = false;
    }

    private static void RenderCore(On.Monocle.Engine.orig_RenderCore orig, Engine self) {
        // Scene.Render is the final composition point for Overworld, but Level overrides
        // Render and never calls the base implementation. Trying to acrylic-composite the
        // in-level settings overlay therefore suppresses its normal render without ever
        // reaching RenderScene, leaving only the gameplay visible. Keep that overlay on its
        // regular translucent rendering path; acrylic remains available for pages rendered
        // through the Overworld composition path.
        IMaterialAcrylicPage? page = MaterialChapterSelect.ActivePage;
        page ??= MaterialModOptions.ActivePage;
        QolSettings settings = MicroblocksQolUtilsModule.Settings;
        if (rendering
            || failed
            || page is null
            || !settings.MaterialAcrylicBackground
            || Engine.Scene is not Scene) {
            orig(self);
            return;
        }

        rendering = true;
        activePage = page;
        IMaterialAcrylicOverlay? overlay = page as IMaterialAcrylicOverlay;
        page.SuppressNormalRender = true;
        try {
            overlay?.HideOverlaysForAcrylicCapture();
            // Keep the normal Engine render lifecycle intact. In particular, the
            // Overworld must run BeforeRender exactly once so its mountain and HUD
            // buffers are ready before RenderScene redirects the final composition.
            orig(self);
        } finally {
            overlay?.RestoreOverlaysAfterAcrylicCapture();
            page.SuppressNormalRender = false;
            activePage = null;
            rendering = false;
        }
    }

    private static void RenderScene(On.Monocle.Scene.orig_Render orig, Scene self) {
        IMaterialAcrylicPage? page = activePage;
        if (page is null || failed || !ReferenceEquals(self, Engine.Scene)) {
            orig(self);
            return;
        }
        IMaterialAcrylicOverlay? overlay = page as IMaterialAcrylicOverlay;

        GraphicsDevice graphics = Engine.Graphics.GraphicsDevice;
        RenderTargetBinding[] originalTargets = graphics.GetRenderTargets();
        Viewport originalViewport = graphics.Viewport;
        bool sceneRendered = false;
        try {
            EnsureTargets(Math.Max(1, originalViewport.Width), Math.Max(1, originalViewport.Height));
            if (sceneTarget is null || blurTemporary is null || blurredTarget is null) {
                overlay?.RestoreOverlaysAfterAcrylicCapture();
                RestoreOutput(graphics, originalTargets, originalViewport);
                page.SuppressNormalRender = false;
                orig(self);
                return;
            }

            graphics.SetRenderTarget(sceneTarget);
            graphics.Viewport = new Viewport(0, 0, sceneTarget.Width, sceneTarget.Height);
            graphics.Clear(Engine.ClearColor);
            HiresSnow? snow = (self as Overworld)?.Snow;
            bool renderSnow = snow is { Visible: true };
            if (renderSnow) snow!.Visible = false;
            try {
                orig(self);
            } finally {
                overlay?.RestoreOverlaysAfterAcrylicCapture();
                if (renderSnow) snow!.Visible = true;
            }
            sceneRendered = true;

            float sampleScale = MathHelper.Lerp(0.8f, 3.1f,
                Math.Clamp(MicroblocksQolUtilsModule.Settings.MaterialAcrylicBlurStrength, 1, 12) / 12f);
            Texture2D blurred = GaussianBlur.Blur(
                sceneTarget,
                blurTemporary,
                blurredTarget,
                samples: GaussianBlur.Samples.Nine,
                sampleScale: sampleScale
            );

            RestoreOutput(graphics, originalTargets, originalViewport);
            DrawTexture(blurred, originalViewport.Width, originalViewport.Height, BlendState.Opaque);
            DrawTexture(sceneTarget, originalViewport.Width, originalViewport.Height, BlendState.AlphaBlend,
                Color.White * 0.22f);

            page.SuppressNormalRender = false;
            DrawMaterialPage(page);
            DrawOverlays(overlay);
            if (renderSnow) snow!.Render(self);
            successfulFrames++;
        } catch (Exception exception) {
            page.SuppressNormalRender = false;
            overlay?.RestoreOverlaysAfterAcrylicCapture();
            RestoreOutput(graphics, originalTargets, originalViewport);
            failed = true;
            Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialAcrylic");

            // If the scene itself failed, preserve its original exception instead
            // of invoking every renderer a second time. Acrylic-only failures can
            // safely fall back to the ordinary unblurred scene for this frame.
            if (!sceneRendered) throw;
            graphics.Clear(Engine.ClearColor);
            orig(self);
        }
    }

    private static void DrawTexture(
        Texture2D texture,
        int width,
        int height,
        BlendState blend,
        Color? color = null
    ) {
        bool begun = false;
        try {
            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                blend,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                RasterizerState.CullNone
            );
            begun = true;
            Draw.SpriteBatch.Draw(texture, new Rectangle(0, 0, width, height), color ?? Color.White);
        } finally {
            if (begun) Draw.SpriteBatch.End();
        }
    }

    private static void DrawMaterialPage(IMaterialAcrylicPage page) {
        DrawScreenBatch(() => page.RenderMaterialContent(acrylicActive: true));
    }

    private static void DrawOverlays(IMaterialAcrylicOverlay? overlay) {
        if (overlay is null || !overlay.HasOverlaysForAcrylic) return;
        DrawScreenBatch(() => overlay.RenderOverlaysAboveAcrylic());
    }

    private static void DrawScreenBatch(Action render) {
        bool begun = false;
        try {
            Draw.SpriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.LinearClamp,
                DepthStencilState.None,
                RasterizerState.CullNone,
                null,
                Engine.ScreenMatrix
            );
            begun = true;
            render();
        } finally {
            if (begun) {
                try {
                    Draw.SpriteBatch.End();
                } catch (Exception exception) {
                    Logger.LogDetailed(exception, "MicroblocksQolUtils/MaterialAcrylic");
                }
            }
        }
    }

    private static void RestoreOutput(
        GraphicsDevice graphics,
        RenderTargetBinding[] targets,
        Viewport viewport
    ) {
        if (targets.Length == 0) graphics.SetRenderTarget(null);
        else graphics.SetRenderTargets(targets);
        graphics.Viewport = viewport;
    }

    private static void EnsureTargets(int width, int height) {
        if (sceneTarget?.Width == width && sceneTarget.Height == height) return;
        DisposeTargets();
        sceneTarget = VirtualContent.CreateRenderTarget("mqol-material-scene", width, height);
        blurTemporary = VirtualContent.CreateRenderTarget("mqol-material-blur-a", width, height);
        blurredTarget = VirtualContent.CreateRenderTarget("mqol-material-blur-b", width, height);
    }

    private static void DisposeTargets() {
        sceneTarget?.Dispose();
        blurTemporary?.Dispose();
        blurredTarget?.Dispose();
        sceneTarget = null;
        blurTemporary = null;
        blurredTarget = null;
    }
}

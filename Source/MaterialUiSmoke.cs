using Monocle;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MaterialUiSmoke {
    private const string CaptureSmokeVariable = "MICROBLOCKS_QOL_MATERIAL_UI_SMOKE";
    private static bool armed;
    private static bool started;
    private static bool passed;
    private static int visibleFrames;

    public static void Load() {
        armed = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CaptureSmokeVariable));
        started = false;
        passed = false;
        visibleFrames = 0;
    }

    public static void Unload() {
        armed = false;
    }

    public static void Update() {
        if (!armed || passed || Engine.Scene is not Overworld overworld) return;
        MaterialChapterSelect? page = overworld.GetUI<MaterialChapterSelect>();
        if (page is null) {
            passed = true;
            throw new InvalidOperationException("MaterialChapterSelect was not registered in Overworld.UIs");
        }
        if (!started && overworld.Current is not null) {
            QolSettings settings = MicroblocksQolUtilsModule.Settings;
            settings.MaterialAcrylicBackground = true;
            settings.ReplaceChapterSelect = true;
            overworld.Goto<MaterialChapterSelect>();
            started = true;
            return;
        }
        if (overworld.Current == page && page.Visible) {
            if (MaterialAcrylicRenderer.Failed) {
                passed = true;
                throw new InvalidOperationException("Material acrylic rendering failed; see the Everest log");
            }
            visibleFrames++;
            if (visibleFrames >= 60) {
                if (MaterialAcrylicRenderer.SuccessfulFrames == 0) {
                    passed = true;
                    throw new InvalidOperationException("Material acrylic renderer did not composite any frames");
                }
                if (!MaterialAcrylicRenderer.CapturedSceneLooksVisible()) {
                    passed = true;
                    throw new InvalidOperationException("Material acrylic renderer captured an empty or black scene");
                }
                if (ModuleInstalled("CollabUtils2") && !CollabUtils2Bridge.Available) {
                    passed = true;
                    throw new InvalidOperationException("CollabUtils2 is installed but its LobbyHelper interop is unavailable");
                }
                if (ModuleInstalled("SpeedrunTool") && !SpeedrunToolBridge.Available) {
                    passed = true;
                    throw new InvalidOperationException("SpeedrunTool is installed but recording timeline interop is unavailable");
                }
                if (ModuleInstalled("MiaoNet") && !MiaoNetBridge.SmokeValidate()) {
                    passed = true;
                    throw new InvalidOperationException("MiaoNet is installed but the /qol chat command could not be registered");
                }
                passed = true;
                Logger.Log(
                    LogLevel.Info,
                    "MicroblocksQolUtils/MaterialUI",
                    $"QOL_MATERIAL_UI_SMOKE_PASSED collab={CollabUtils2Bridge.Available} "
                        + $"miaonet={MiaoNetBridge.Available} speedrun={SpeedrunToolBridge.Available}"
                );
            }
        }
    }

    private static bool ModuleInstalled(string name) => Everest.Modules.Any(module =>
        string.Equals(module.Metadata.Name, name, StringComparison.OrdinalIgnoreCase)
    );
}

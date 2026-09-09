using System.Reflection;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class MotionSmoothingBridge {
    private static EverestModule? module;
    private static PropertyInfo? settingsProperty;
    private static PropertyInfo? enabledProperty;
    private static PropertyInfo? decoupledProperty;
    private static bool resolved;

    public static bool Available => Resolve();

    public static bool Enabled {
        get {
            if (!Resolve() || module is null) return false;
            try {
                object? settings = settingsProperty?.GetValue(null)
                    ?? module.GetType().GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(module);
                bool enabled = settings is not null && enabledProperty?.GetValue(settings) as bool? == true;
                bool decoupled = decoupledProperty?.GetValue(null) as bool? ?? true;
                return enabled && decoupled;
            } catch {
                return false;
            }
        }
    }

    public static void Unload() {
        RecordingMotionSmoothing.Unload();
        module = null;
        settingsProperty = null;
        enabledProperty = null;
        decoupledProperty = null;
        resolved = false;
    }

    private static bool Resolve() {
        if (resolved) return module is not null;
        resolved = true;
        module = Everest.Modules.FirstOrDefault(candidate =>
            string.Equals(candidate.Metadata.Name, "MotionSmoothing", StringComparison.OrdinalIgnoreCase)
        );
        if (module is null) return false;
        Type moduleType = module.GetType();
        settingsProperty = moduleType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static);
        object? settings = settingsProperty?.GetValue(null)
            ?? moduleType.GetProperty("Settings", BindingFlags.Public | BindingFlags.Instance)?.GetValue(module);
        enabledProperty = settings?.GetType().GetProperty("Enabled", BindingFlags.Public | BindingFlags.Instance);
        decoupledProperty = moduleType.GetProperty("UseDecoupledGameTick", BindingFlags.Public | BindingFlags.Static);
        Logger.Log(LogLevel.Info, "MicroblocksQolUtils",
            $"MotionSmoothing FPS integration enabled for {module.Metadata.VersionString}");
        return true;
    }
}

using System.Reflection;
using System.Collections;
using Microsoft.Xna.Framework;
using Monocle;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.MicroblocksQolUtils;

// Optional adapter: do not toggle MotionSmoothing settings/renderers or advance
// Scene.AfterUpdate just to initialize presentation state after an internal SL.
internal static class RecordingMotionSmoothing {
    private static Assembly? boundAssembly;
    private static ILHook? drawUpdateHook;
    private static PropertyInfo? handlerInstance, valueSmoother, pushSmoother;
    private static MethodInfo? updateValueHistory, updatePushHistory;
    private static FieldInfo? positionsUpdated;
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Prepare() {
        Assembly? assembly = Everest.Modules.FirstOrDefault(module => module.Metadata.Name == "MotionSmoothing")?.GetType().Assembly;
        if (assembly is not null) Initialize(assembly);
    }

    internal static void Initialize(Assembly assembly) {
        if (ReferenceEquals(assembly, boundAssembly)) return;
        Unload();
        boundAssembly = assembly;
        try {
            Type handler = assembly.GetType("Celeste.Mod.MotionSmoothing.Smoothing.MotionSmoothingHandler", true)!;
            handlerInstance = handler.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                ?? throw new MissingMemberException(handler.FullName, "Instance");
            valueSmoother = handler.GetProperty("ValueSmoother", Members)!;
            pushSmoother = handler.GetProperty("PushSpriteSmoother", Members)!;
            updateValueHistory = valueSmoother.PropertyType.GetMethod("UpdatePositions", Members, [])!;
            updatePushHistory = pushSmoother.PropertyType.GetMethod("UpdatePositions", Members, [])!;
            positionsUpdated = handler.GetField("_positionsWereUpdated", Members)!;
            if (updateValueHistory is null || updatePushHistory is null || positionsUpdated?.FieldType != typeof(bool))
                throw new MissingMemberException("MotionSmoothing history initialization contract changed");

            Type atDraw = assembly.GetType("Celeste.Mod.MotionSmoothing.Utilities.UpdateAtDraw", true)!;
            MethodInfo update = atDraw.GetMethod("Update", Members, [typeof(Scene)])
                ?? throw new MissingMethodException(atDraw.FullName, "Update(Scene)");
            if (update.ReturnType != typeof(void)) throw new MissingMethodException("Unexpected draw update signature");
            drawUpdateHook = new ILHook(update, il => {
                ILCursor cursor = new(il);
                ILLabel original = cursor.DefineLabel();
                cursor.EmitDelegate(() => RecordingSavePause.Active);
                cursor.Emit(OpCodes.Brfalse, original);
                cursor.Emit(OpCodes.Ret);
                cursor.MarkLabel(original);
            });
        } catch (Exception exception) {
            ClearBindings();
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", $"MotionSmoothing recording pause adapter unavailable: {exception.GetBaseException().Message}");
        }
    }

    internal static void PrimeRestoredState() {
        try {
            if (handlerInstance?.GetValue(null) is not { } handler) return;
            // SRT's after-load callback registers NEW smoothing states but does
            // not initialize their histories. Hires rendering reads the camera's
            // SmoothedRealPosition even before its first physics update.
            updateValueHistory!.Invoke(valueSmoother!.GetValue(handler), null);
            updatePushHistory!.Invoke(pushSmoother!.GetValue(handler), null);
            // Do not extrapolate using the abandoned attempt's update timestamp
            // while we deliberately wait for clean-frame delivery.
            positionsUpdated!.SetValue(handler, false);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", $"Cannot initialize restored smoothing history: {exception.GetBaseException().Message}");
        }
    }

    internal static void FreezePresentation() {
        try {
            if (handlerInstance?.GetValue(null) is not { } handler) return;
            Freeze(valueSmoother!.GetValue(handler)!);
            Freeze(pushSmoother!.GetValue(handler)!);
            positionsUpdated!.SetValue(handler, false);
        } catch (Exception exception) {
            Logger.Log(LogLevel.Warn, "MicroblocksQolUtils/Recorder", $"Cannot freeze smoothing presentation: {exception.GetBaseException().Message}");
        }

        static void Freeze(object strategy) {
            // Keep velocity history intact for the next real update. Only pin
            // display coordinates to the latest physical sample: neither an old
            // rendered sample nor extrapolation across the save's wall time.
            var states = strategy.GetType().GetMethod("States", Members, [])!.Invoke(strategy, null) as IEnumerable;
            foreach (object pair in states!) {
                object state = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
                Type type = state.GetType();
                PropertyInfo? original = type.GetProperty("OriginalRealPosition", Members);
                PropertyInfo? smooth = type.GetProperty("SmoothedRealPosition", Members);
                if (original?.PropertyType == typeof(Vector2) && smooth?.GetSetMethod(true) is { } set)
                    set.Invoke(state, [original.GetValue(state)]);
            }
        }
    }

    private static void ClearBindings() {
        drawUpdateHook?.Dispose(); drawUpdateHook = null;
        handlerInstance = valueSmoother = pushSmoother = null;
        updateValueHistory = updatePushHistory = null;
        positionsUpdated = null;
    }

    internal static void Unload() {
        ClearBindings();
        boundAssembly = null;
    }
}

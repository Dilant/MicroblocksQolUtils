using System.Reflection;
using System.Runtime.CompilerServices;
using Celeste.Mod.MicroblocksQolUtils;
using MonoMod.RuntimeDetour;

internal static class ProgressCompatibility {
    internal static void Verify(Assembly srt) {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        Type? indicator = srt.GetType("Celeste.Mod.SpeedrunTool.Progress.BusyIndicator");
        if (indicator is null) { Console.WriteLine("PASS: legacy SRT has no auxiliary presents"); return; }
        Assembly mod = typeof(AutoRecorder).Assembly;
        Type adapter = mod.GetType("Celeste.Mod.MicroblocksQolUtils.SpeedrunToolProgress", true)!;
        Type gate = mod.GetType("Celeste.Mod.MicroblocksQolUtils.CapturePresentationGate", true)!;
        Type silent = mod.GetType("Celeste.Mod.MicroblocksQolUtils.SpeedrunToolAutoSave", true)!;
        bool Accept() => (bool)gate.GetProperty("AcceptGameplay", flags)!.GetValue(null)!;
        MethodInfo present = indicator.GetMethod("TryPresent", flags)!;
        object progress = RuntimeHelpers.GetUninitializedObject(present.GetParameters()[0].ParameterType);
        int presented = 0;
        bool fail = false;
        // Replace only graphics in this test: production adapter and installed
        // SRT's real (relinked) method signature/hook chain remain under test.
        using Hook renderer = new(present, (Action<Action<object, bool>, object, bool>)((orig, value, force) => {
            if (Accept()) throw new Exception("Actual SRT progress escaped capture isolation");
            presented++;
            if (fail) throw new InvalidOperationException("expected graphics failure");
        }));
        try {
            adapter.GetMethod("Load", flags)!.Invoke(null, [srt]);
            if (adapter.GetProperty("Available", flags)!.GetValue(null) is not true)
                throw new Exception("Actual SRT progress hook did not install");
            present.Invoke(null, [progress, true]);
            if (presented != 1 || !Accept()) throw new Exception("Progress scope did not unwind");
            var saving = silent.GetField("<SavingSilently>k__BackingField", flags)!;
            saving.SetValue(null, true);
            try { present.Invoke(null, [progress, true]); }
            finally { saving.SetValue(null, false); }
            if (presented != 1) throw new Exception("Private save rendered duplicate progress");
            fail = true;
            try { present.Invoke(null, [progress, true]); }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException) { }
            if (!Accept()) throw new Exception("Graphics exception poisoned capture");
            Console.WriteLine("PASS: actual SRT progress method, source exclusion, private suppression, exception cleanup");
        } finally { adapter.GetMethod("Unload", flags)!.Invoke(null, null); }
    }
}

using System.Reflection;
using Celeste.Mod.MicroblocksQolUtils;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;

internal static class HookCompatibility {
    internal static void Verify(Assembly srt) {
        // Reflection keeps this test runnable with upstream MonoMod as well as
        // Ultra's transaction extension. No game scene or player save is needed.
        Type? transactionType = typeof(ILHook).Assembly.GetType("MonoMod.RuntimeDetour.ILHookTransaction");
        foreach (bool deferred in new[] { false, true }) {
            if (deferred && transactionType is null) {
                Console.WriteLine("SKIP: this MonoMod has no deferred IL hook transactions");
                continue;
            }

            VerifySupported();
            foreach (string method in new[] { "SaveStateImpl", "LoadStateImpl" }) VerifyUnsupported(method);
            // Reload must reset the failure latch from an incompatible generation.
            VerifySupported();
            if (deferred) {
                using IDisposable? transaction = Begin();
                SpeedrunToolAutoSave.Load(srt);
                Require(!SpeedrunToolAutoSave.Available, "Pending hooks reported ready");
                SpeedrunToolAutoSave.Unload();
                Flush(transaction);
                Require(!SpeedrunToolAutoSave.Available, "Cancelled hooks reactivated recovery");
                Console.WriteLine("PASS: unloading pending hooks cancels activation");
            }

            IDisposable? Begin() => deferred
                ? (IDisposable)transactionType!.GetMethod("Begin", Type.EmptyTypes)!.Invoke(null, null)!
                : null;
            void Flush(IDisposable? transaction) {
                if (transaction is not null)
                    transactionType!.GetMethod("FlushParallel")!.Invoke(transaction, [4]);
            }

            void VerifySupported() {
                using IDisposable? transaction = Begin();
                try {
                    SpeedrunToolAutoSave.Load(srt);
                    if (deferred) {
                        Require(!SpeedrunToolAutoSave.Available, "Queued hooks reported ready before application");
                        Require(SpeedrunToolAutoSave.TrySave() == RecoveryResult.Unavailable,
                            "Recovery attempted a save before hooks were applied");
                    }
                    Flush(transaction);
                    Require(SpeedrunToolAutoSave.Available, "Compatible hooks did not activate");
                    Console.WriteLine($"PASS: compatible hooks activate ({Mode()})");
                } finally {
                    SpeedrunToolAutoSave.Unload();
                }
            }

            void VerifyUnsupported(string methodName) {
                using IDisposable? transaction = Begin();
                Type manager = srt.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.StateManager", true)!;
                MethodInfo method = manager.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!;
                // Mimic an unknown implementation/another mod changing the branch.
                // dup/pop preserves the method's semantics, but the branch is no
                // longer immediately after ldarg.1. Our patch must leave it alone.
                using ILHook otherMod = new(method, il => {
                    ILCursor cursor = new(il);
                    Require(cursor.TryGotoNext(MoveType.After,
                        instruction => instruction.MatchCallOrCallvirt(manager, "PreCloneSavedEntities"),
                        instruction => instruction.MatchLdarg(1)), "Missing test mutation anchor");
                    cursor.Emit(OpCodes.Dup);
                    cursor.Emit(OpCodes.Pop);
                });
                try {
                    SpeedrunToolAutoSave.Load(srt);
                    Flush(transaction); // Must not throw, including parallel startup flush.
                    Require(!SpeedrunToolAutoSave.Available, "Partially patched recovery reported ready");
                    Require(SpeedrunToolAutoSave.TrySave() == RecoveryResult.Unavailable,
                        "Incompatible recovery fell back to ordinary saves");
                    otherMod.Dispose(); // Rebuild the remaining chain without the mutation.
                    Require(!SpeedrunToolAutoSave.Available, "Failed generation reactivated without reload");
                    Console.WriteLine($"PASS: unsupported {methodName} safely disables recovery ({Mode()})");
                } finally {
                    SpeedrunToolAutoSave.Unload();
                }
            }

            string Mode() => deferred ? "deferred/parallel" : "immediate";
        }
    }

    private static void Require(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }
}

using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Celeste.Mod.MicroblocksQolUtils;

// Checks the real installed assembly, without starting Celeste or touching saves.
// Run from the worktree with an explicit .work scratch directory.
if (args.Length != 2) throw new ArgumentException("Usage: Recording.Interop <CelesteRoot> <scratch-directory>");
string root = Path.GetFullPath(args[0]);
string scratch = Path.GetFullPath(args[1]);
Directory.CreateDirectory(scratch);
AssemblyLoadContext.Default.Resolving += (_, name) => {
    foreach (string directory in new[] { root, scratch }) {
        string path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
using (ZipArchive zip = ZipFile.OpenRead(Path.Combine(root, "Mods", "SpeedrunTool.zip"))) {
    foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        entry.ExtractToFile(Path.Combine(scratch, entry.Name), overwrite: true);
}
Assembly srt = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(scratch, "SpeedrunTool.dll"));
VerifyDefaultSetting();
SpeedrunToolAutoSave.Load(srt);
try {
    if (!SpeedrunToolAutoSave.Available)
        throw new Exception("Installed SpeedrunTool is incompatible with silent saves; see log");
    Console.WriteLine("PASS: private slot, silent save/load and marking hooks installed on actual SpeedrunTool.dll");
    const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    Type slots = srt.GetType("Celeste.Mod.SpeedrunTool.SaveLoad.SaveSlotsManager", true)!;
    slots.GetMethod("SwitchSlot", flags, [typeof(string)])!.Invoke(null, ["interop-user"]);
    object previous = slots.GetField("Slot", flags)!.GetValue(null)!;
    RecoveryResult result = SpeedrunToolRecoverySlot.Run(_ => RecoveryResult.Success, create: true);
    if (result != RecoveryResult.Success || !ReferenceEquals(previous, slots.GetField("Slot", flags)!.GetValue(null))
        || slots.GetProperty("SlotName", flags)!.GetValue(null) as string != "interop-user")
        throw new Exception("Actual SRT slot selection was not restored");
    if (!SpeedrunToolRecoverySlot.Release()) throw new Exception("Actual SRT private slot was not released");
    Console.WriteLine("PASS: actual SRT slot creation, selection restoration and cleanup (no game save created)");
} finally {
    SpeedrunToolAutoSave.Unload();
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static void VerifyDefaultSetting() {
    var property = typeof(QolSettings).GetProperty(nameof(QolSettings.RecordingAutoSaveOnTransition))!;
    var attribute = property.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>();
    if (!new QolSettings().RecordingAutoSaveOnTransition || attribute?.Value is not true)
        throw new Exception("New and existing configurations must default transition saves to enabled");
    Console.WriteLine("PASS: production setting initializer and serialization default are enabled");
}

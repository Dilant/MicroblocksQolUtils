using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Celeste.Mod.MicroblocksQolUtils;

internal static class AndroidNativeLibrary {
    // Some embedded CoreCLR builds report Linux rather than Android.
    internal static bool IsAndroid => OperatingSystem.IsAndroid()
        || (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RALCORE_NATIVEDIR"))
            && Directory.Exists("/system/fonts"));
    private static nint backend;

    internal static void Load(EverestModuleMetadata metadata) {
        if (!IsAndroid || backend != 0) return;
        const string file = "libmicroblocks_qol_native.so";
        const string entry = "Code/lib-linux/" + file;
        byte[] bytes;
        if (!string.IsNullOrEmpty(metadata.PathArchive)) {
            using ZipArchive zip = ZipFile.OpenRead(metadata.PathArchive);
            using Stream input = (zip.GetEntry(entry)
                ?? throw new FileNotFoundException("Install the Android ARM64 mod package: " + entry)).Open();
            using MemoryStream output = new();
            input.CopyTo(output);
            bytes = output.ToArray();
        } else {
            bytes = File.ReadAllBytes(Path.Combine(metadata.PathDirectory, entry));
        }
        // External Android storage is noexec. CeleMod's DOTNET_ROOT is in its
        // private files directory, alongside the extracted runtime libraries.
        string runtime = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Android host did not provide DOTNET_ROOT");
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string cache = Path.Combine(runtime, "mod-native-cache", "MicroblocksQolUtils", hash);
        Directory.CreateDirectory(cache);
        string path = Path.Combine(cache, file);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        backend = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(AndroidNativeLibrary).Assembly, Resolve);
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? flags) {
        if (name == "microblocks_qol_native") return backend;
        if (name == "SDL2") {
            string? directory = Environment.GetEnvironmentVariable("RALCORE_NATIVEDIR");
            if (!string.IsNullOrEmpty(directory)) return NativeLibrary.Load(Path.Combine(directory, "libSDL2.so"));
        }
        return 0;
    }
}

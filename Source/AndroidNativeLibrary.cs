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
    // FFmpeg dependencies must be available before dlopen reaches the backend.
    // Keep their handles alive for the lifetime of the process, like the backend.
    private static readonly List<nint> dependencies = [];
    private static readonly string[] dependencyNames = [
        "libavutil.so", "libswresample.so", "libswscale.so", "libavcodec.so", "libavformat.so"
    ];

    internal static void Load(EverestModuleMetadata metadata) {
        if (!IsAndroid || backend != 0) return;
        const string file = "libmicroblocks_qol_native.so";
        Dictionary<string, byte[]> libraries = [];
        if (!string.IsNullOrEmpty(metadata.PathArchive)) {
            using ZipArchive zip = ZipFile.OpenRead(metadata.PathArchive);
            foreach (string name in dependencyNames.Append(file)) {
                ZipArchiveEntry? entry = zip.GetEntry("Code/lib-linux/" + name);
                if (entry is null) continue;
                using Stream input = entry.Open();
                using MemoryStream output = new();
                input.CopyTo(output);
                libraries.Add(name, output.ToArray());
            }
        } else {
            foreach (string name in dependencyNames.Append(file)) {
                string source = Path.Combine(metadata.PathDirectory, "Code", "lib-linux", name);
                if (File.Exists(source)) libraries.Add(name, File.ReadAllBytes(source));
            }
        }
        if (!libraries.ContainsKey(file))
            throw new FileNotFoundException("Install the Android ARM64 mod package: " + file);
        if (dependencyNames.Any(libraries.ContainsKey) && !dependencyNames.All(libraries.ContainsKey))
            throw new FileNotFoundException("The Android mod package is missing FFmpeg libraries; reinstall it.");
        // External Android storage is noexec. CeleMod's DOTNET_ROOT is in its
        // private files directory, alongside the extracted runtime libraries.
        string runtime = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? throw new InvalidOperationException("Android host did not provide DOTNET_ROOT");
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] bytes in libraries.Values) digest.AppendData(bytes);
        string hash = Convert.ToHexString(digest.GetHashAndReset());
        string cache = Path.Combine(runtime, "mod-native-cache", "MicroblocksQolUtils", hash);
        Directory.CreateDirectory(cache);
        foreach (var (name, bytes) in libraries) {
            string path = Path.Combine(cache, name);
            // Rewrite incomplete/corrupt cache entries after an interrupted extraction.
            if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes)))
                File.WriteAllBytes(path, bytes);
        }
        foreach (string name in dependencyNames)
            if (libraries.ContainsKey(name)) dependencies.Add(NativeLibrary.Load(Path.Combine(cache, name)));
        backend = NativeLibrary.Load(Path.Combine(cache, file));
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

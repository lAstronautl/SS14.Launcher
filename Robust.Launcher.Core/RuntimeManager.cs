using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Robust.Launcher.Core;

/// <summary>Which .NET runtime an engine build needs, read from its runtimeconfig.json.</summary>
public sealed record EngineRuntimeInfo(int Major, string Version, Dictionary<string, string> ConfigProperties)
{
    public static EngineRuntimeInfo Read(string engineDir)
    {
        var path = Path.Combine(engineDir, "Robust.Client.runtimeconfig.json");
        var props = new Dictionary<string, string>();
        if (!File.Exists(path))
            return new EngineRuntimeInfo(10, "10.0.0", props);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var opts = doc.RootElement.GetProperty("runtimeOptions");
        var version = "10.0.0";
        if (opts.TryGetProperty("framework", out var fw))
            version = fw.GetProperty("version").GetString() ?? version;
        else if (opts.TryGetProperty("frameworks", out var fws))
        {
            foreach (var f in fws.EnumerateArray())
            {
                if (f.GetProperty("name").GetString() == "Microsoft.NETCore.App")
                    version = f.GetProperty("version").GetString() ?? version;
            }
        }

        if (opts.TryGetProperty("configProperties", out var cp))
        {
            foreach (var p in cp.EnumerateObject())
                props[p.Name] = p.Value.ToString();
        }

        return new EngineRuntimeInfo(int.Parse(version.Split('.')[0]), version, props);
    }
}

/// <summary>
/// Downloads the Mono runtime for Android of a given .NET major version (the official
/// Microsoft.NETCore.App.Runtime.Mono.android-* NuGet package), so each engine runs on the runtime it was built for.
/// </summary>
/// <remarks>
/// Layout of an installed runtime:
/// <c>lib/</c> — framework assemblies incl. System.Private.CoreLib.dll (also used by the sandbox verifier),
/// <c>native/</c> — libmonosgen-2.0.so, libSystem.*.so, crypto .dex.
/// </remarks>
public sealed class RuntimeManager
{
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    private readonly HttpClient _http;
    private readonly string _root;

    public RuntimeManager(HttpClient http, string cacheRoot)
    {
        _http = http;
        _root = Path.Combine(cacheRoot, "runtimes");
        Directory.CreateDirectory(_root);
    }

    public static string AndroidRid => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "android-arm64",
        Architecture.X64 => "android-x64",
        Architecture.Arm => "android-arm",
        Architecture.X86 => "android-x86",
        _ => throw new PlatformNotSupportedException(RuntimeInformation.ProcessArchitecture.ToString()),
    };

    public string GetInstallDir(int major) => Path.Combine(_root, $"net{major}-{AndroidRid}");

    /// <summary>
    /// Runtime version used per .NET major. Pinned because precompiled (AOT) code only loads into the exact
    /// runtime version it was built for; keep in sync with Robust.Client.Android/aot/runtime-versions.txt (CI).
    /// Majors not listed use the newest stable patch from NuGet, without AOT.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> PinnedVersions = new Dictionary<int, string>
    {
        [9] = "9.0.23",
        [10] = "10.0.15",
    };

    /// <summary>Installed runtime version of a major, or null.</summary>
    public string? GetInstalledVersion(int major)
    {
        var dir = GetInstallDir(major);
        var file = Path.Combine(dir, "version.txt");
        return File.Exists(Path.Combine(dir, ".installed")) && File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>
    /// Native libraries are mapped executable by the game process. Android warns that loading a *writable*
    /// library will be refused in future versions, so they are made read-only (r-x).
    /// </summary>
    private static void MakeNativesReadOnly(string nativeDir)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(nativeDir))
            return;

        foreach (var so in Directory.EnumerateFiles(nativeDir, "*.so"))
        {
            try
            {
                File.SetUnixFileMode(so, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            catch (IOException)
            {
                // Not critical.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public async Task<string> EnsureRuntimeAsync(int major, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetInstallDir(major);
        PinnedVersions.TryGetValue(major, out var pinned);
        if (File.Exists(Path.Combine(dir, ".installed")) && (pinned == null || GetInstalledVersion(major) == pinned))
        {
            MakeNativesReadOnly(Path.Combine(dir, "native"));
            return dir;
        }

        var id = $"microsoft.netcore.app.runtime.mono.{AndroidRid}";
        progress?.Report(new DownloadProgress($"Поиск среды .NET {major}", 0, 0));
        var version = pinned;
        if (version == null)
        {
            using var index = JsonDocument.Parse(await _http.GetStringAsync($"{FlatContainer}{id}/index.json", ct));
            version = index.RootElement.GetProperty("versions").EnumerateArray()
                .Select(v => v.GetString()!)
                .Where(v => !v.Contains('-') && v.StartsWith($"{major}."))
                .OrderBy(v => Version.Parse(v))
                .LastOrDefault() ?? throw new InvalidOperationException($"Среда .NET {major} для Android не найдена.");
        }

        var tmp = dir + ".nupkg.part";
        using (var resp = await _http.GetAsync($"{FlatContainer}{id}/{version}/{id}.{version}.nupkg",
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                progress?.Report(new DownloadProgress($"Загрузка среды .NET {major}", done, total));
            }
        }

        progress?.Report(new DownloadProgress($"Распаковка среды .NET {major}", 0, 0));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        var lib = Directory.CreateDirectory(Path.Combine(dir, "lib")).FullName;
        var native = Directory.CreateDirectory(Path.Combine(dir, "native")).FullName;

        using (var zip = ZipFile.OpenRead(tmp))
        {
            var prefix = $"runtimes/{AndroidRid}/";
            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                    continue;
                var rel = entry.FullName[prefix.Length..];
                var name = Path.GetFileName(rel);

                if (rel.StartsWith("lib/", StringComparison.Ordinal) && name.EndsWith(".dll"))
                    entry.ExtractToFile(Path.Combine(lib, name), overwrite: true);
                else if (rel.StartsWith("native/", StringComparison.Ordinal) && !rel.Contains("/include/")
                         && (name.EndsWith(".so") || name.EndsWith(".dex") || name.EndsWith(".jar")))
                    entry.ExtractToFile(Path.Combine(native, name), overwrite: true);
                else if (name == "System.Private.CoreLib.dll")
                    entry.ExtractToFile(Path.Combine(lib, name), overwrite: true);
            }
        }

        File.Delete(tmp);

        MakeNativesReadOnly(native);

        if (!File.Exists(Path.Combine(native, "libmonosgen-2.0.so")) || !File.Exists(Path.Combine(lib, "System.Private.CoreLib.dll")))
            throw new InvalidDataException($"Пакет среды .NET {major} неполный.");

        File.WriteAllText(Path.Combine(dir, "version.txt"), version);
        File.WriteAllText(Path.Combine(dir, ".installed"), DateTime.UtcNow.ToString("O"));
        return dir;
    }
}

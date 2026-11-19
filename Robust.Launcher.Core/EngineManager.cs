using System.IO.Compression;
using System.Security.Cryptography;

namespace Robust.Launcher.Core;

public sealed record ResolvedEngine(string Dir, EngineSource Source, string Version);

public enum EngineAvailability
{
    Patched,
    Official,
    None,
}

/// <summary>
/// Gets an engine build for the <c>engine_version</c> a server reports:
/// our Android build if one is published, otherwise the official build of that version.
/// </summary>
/// <remarks>
/// Builds are published by CI as GitHub release assets:
/// <c>{base}/android-engine-v{version}/Robust.Client_android.zip</c> and a <c>.sha256</c> file next to it.
/// </remarks>
public sealed class EngineManager
{
    public const string DefaultReleaseBase = "https://github.com/lAstronautl/RobustToolbox/releases/download/";
    public const string PlatformName = "android";
    public const string OfficialManifestUrl = "https://robust-builds.cdn.spacestation14.com/manifest.json";

    /// <summary>
    /// Official build whose managed assemblies we use. They are plain IL, but Robust.Client.dll is stamped
    /// with the build's CPU architecture, so it has to match the device (arm64 phones, x64 emulators/PCs).
    /// </summary>
    private static string[] OfficialPlatforms =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => new[] { "linux-arm64" },
            System.Runtime.InteropServices.Architecture.X64 => new[] { "linux-x64" },
            _ => Array.Empty<string>(),
        };

    private static readonly SemaphoreSlim ManifestLock = new(1, 1);
    private static Dictionary<string, OfficialVersion>? _manifest;
    private static DateTime _manifestTime;

    private readonly HttpClient _http;
    private readonly string _root;
    private readonly string _releaseBase;

    public EngineManager(HttpClient http, string cacheRoot, string? releaseBase = null)
    {
        _http = http;
        _root = Path.Combine(cacheRoot, "engines");
        _releaseBase = releaseBase ?? DefaultReleaseBase;
        if (!_releaseBase.EndsWith('/'))
            _releaseBase += "/";
        Directory.CreateDirectory(_root);
    }

    public static string NormalizeVersion(string v)
    {
        var plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        if (Version.TryParse(v, out var parsed))
            return $"{parsed.Major}.{parsed.Minor}.{Math.Max(parsed.Build, 0)}";
        return v.Trim();
    }

    public Uri GetZipUrl(string version) =>
        new($"{_releaseBase}{PlatformName}-engine-v{NormalizeVersion(version)}/Robust.Client_{PlatformName}.zip");

    public string GetInstallDir(string version) => Path.Combine(_root, NormalizeVersion(version));

    public string GetOfficialInstallDir(string version) => Path.Combine(_root, "official-" + NormalizeVersion(version));

    /// <summary>
    /// Picks and installs the best engine for a server: our build if published, else the official one.
    /// </summary>
    public async Task<ResolvedEngine> ResolveEngineAsync(string version, IProgress<DownloadProgress>? progress = null,
        bool forceOfficial = false, CancellationToken ct = default)
    {
        version = NormalizeVersion(version);
        if (!forceOfficial && await IsAvailableAsync(version, ct))
            return new ResolvedEngine(await EnsureEngineAsync(version, progress, ct), EngineSource.Patched, version);

        return new ResolvedEngine(await EnsureOfficialAsync(version, progress, ct), EngineSource.Official, version);
    }

    /// <summary>Cheap check for UI: which kind of engine would be used for this version.</summary>
    public async Task<EngineAvailability> CheckAvailabilityAsync(string version, CancellationToken ct = default)
    {
        if (await IsAvailableAsync(version, ct))
            return EngineAvailability.Patched;

        if (File.Exists(Path.Combine(GetOfficialInstallDir(version), ".installed")))
            return EngineAvailability.Official;

        try
        {
            var manifest = await GetOfficialManifestAsync(ct);
            return manifest.TryGetValue(NormalizeVersion(version), out var v) && PickOfficialPlatform(v) != null
                ? EngineAvailability.Official
                : EngineAvailability.None;
        }
        catch (HttpRequestException)
        {
            return EngineAvailability.None;
        }
    }

    private async Task<Dictionary<string, OfficialVersion>> GetOfficialManifestAsync(CancellationToken ct)
    {
        await ManifestLock.WaitAsync(ct);
        try
        {
            if (_manifest != null && DateTime.UtcNow - _manifestTime < TimeSpan.FromHours(1))
                return _manifest;

            var json = await _http.GetStringAsync(OfficialManifestUrl, ct);
            _manifest = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, OfficialVersion>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            _manifestTime = DateTime.UtcNow;
            return _manifest;
        }
        finally
        {
            ManifestLock.Release();
        }
    }

    private static OfficialPlatform? PickOfficialPlatform(OfficialVersion v)
    {
        foreach (var p in OfficialPlatforms)
        {
            if (v.Platforms != null && v.Platforms.TryGetValue(p, out var plat))
                return plat;
        }

        return null;
    }

    /// <summary>
    /// Downloads the official build of a version and keeps only what is platform-independent:
    /// managed assemblies and Resources/. Natives come from the APK.
    /// </summary>
    public async Task<string> EnsureOfficialAsync(string version, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetOfficialInstallDir(version);
        if (File.Exists(Path.Combine(dir, ".installed")))
            return dir;

        var manifest = await GetOfficialManifestAsync(ct);
        if (!manifest.TryGetValue(NormalizeVersion(version), out var ver) || PickOfficialPlatform(ver) is not { } plat)
            throw new EngineNotAvailableException(version, 404);

        var tmpZip = dir + ".zip.part";
        await DownloadAsync(new Uri(plat.Url), tmpZip, "Загрузка движка", progress, ct);

        await using (var f = File.OpenRead(tmpZip))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(plat.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmpZip);
                throw new InvalidDataException("Хэш официальной сборки движка не совпадает.");
            }
        }

        progress?.Report(new DownloadProgress("Распаковка движка", 0, 0));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        ZipFile.ExtractToDirectory(tmpZip, dir);
        File.Delete(tmpZip);

        StripNonManaged(dir);

        // Android needs SDL3 windowing; older engines only had GLFW/SDL2, which cannot run on Android.
        if (!File.Exists(Path.Combine(dir, "SpaceWizards.Sdl.dll")))
        {
            Directory.Delete(dir, recursive: true);
            throw new EngineTooOldException(version);
        }

        File.WriteAllText(Path.Combine(dir, ".installed"), DateTime.UtcNow.ToString("O"));
        return dir;
    }

    private static void StripNonManaged(string dir)
    {
        var runtimes = Path.Combine(dir, "runtimes");
        if (Directory.Exists(runtimes))
            Directory.Delete(runtimes, recursive: true);

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            var keep = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            if (!keep)
                File.Delete(file);
        }
    }

    private async Task DownloadAsync(Uri url, string dest, string stage, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buf = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            progress?.Report(new DownloadProgress(stage, done, total));
        }
    }

    private sealed class OfficialVersion
    {
        public Dictionary<string, OfficialPlatform>? Platforms { get; set; }
    }

    private sealed class OfficialPlatform
    {
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public bool IsInstalled(string version) =>
        File.Exists(Path.Combine(GetInstallDir(version), ".installed"));

    /// <summary>
    /// Checks whether an engine build exists for the version, without downloading it.
    /// </summary>
    public async Task<bool> IsAvailableAsync(string version, CancellationToken ct = default)
    {
        if (IsInstalled(version))
            return true;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, GetZipUrl(version));
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes sure the engine is downloaded and extracted. Returns the install directory.
    /// </summary>
    public async Task<string> EnsureEngineAsync(string version, IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var dir = GetInstallDir(version);
        if (IsInstalled(version))
            return dir;

        var zipUrl = GetZipUrl(version);
        string? expectedSha = null;
        try
        {
            var shaText = await _http.GetStringAsync(new Uri(zipUrl.AbsoluteUri + ".sha256"), ct);
            expectedSha = shaText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch (HttpRequestException)
        {
            // Checksum is optional; HTTPS already protects the download.
        }

        var tmpZip = Path.Combine(_root, NormalizeVersion(version) + ".zip.part");
        using (var resp = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new EngineNotAvailableException(version, (int)resp.StatusCode);

            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmpZip);
            var buf = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                progress?.Report(new DownloadProgress("Загрузка движка", done, total));
            }
        }

        if (expectedSha != null)
        {
            await using var f = File.OpenRead(tmpZip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
            if (!actual.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmpZip);
                throw new InvalidDataException("Хэш архива движка не совпадает.");
            }
        }

        progress?.Report(new DownloadProgress("Распаковка движка", 0, 0));
        InstallFromZip(tmpZip, dir);
        File.Delete(tmpZip);
        return dir;
    }

    /// <summary>Installs an engine zip that is already on disk (e.g. for testing or side-loading).</summary>
    public static void InstallFromZip(string zipPath, string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, dir);
        File.WriteAllText(Path.Combine(dir, ".installed"), DateTime.UtcNow.ToString("O"));
    }
}

public sealed class EngineNotAvailableException(string version, int status)
    : Exception($"Движок версии {version} не найден ни в Android-сборках, ни в официальных (HTTP {status}).")
{
    public string Version { get; } = version;
}

public sealed class EngineTooOldException(string version)
    : Exception($"Движок версии {version} слишком старый для Android: в нём нет окна на SDL3.")
{
    public string Version { get; } = version;
}

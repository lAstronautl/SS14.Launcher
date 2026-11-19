using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Robust.Launcher.Core;

/// <summary>
/// Downloads precompiled (AOT) native code for managed assemblies, so Mono on the phone doesn't have to
/// JIT-compile the .NET framework and the engine at every start. Packs are zips of "&lt;Assembly&gt;.dll.so"
/// built by CI (Robust.Client.Android/aot/aot-compile.sh) and published as GitHub release assets:
/// <list type="bullet">
/// <item><c>android-runtime-aot-v{runtime}/runtime-aot-android-arm64.zip</c>: .NET framework of that runtime version</item>
/// <item><c>android-official-aot-v{engine}/official-aot-android-arm64.zip</c>: official engine build of that version</item>
/// </list>
/// Each image is extracted next to its assembly; Mono picks it up when it loads the assembly from that directory.
/// Everything here is optional: without a pack the game simply JIT-compiles as before.
/// </summary>
public sealed class AotManager
{
    /// <summary>Releases of the launcher repository, where CI publishes the runtime and official-engine packs.</summary>
    public const string DefaultReleaseBase = "https://github.com/lAstronautl/SS14.Launcher/releases/download/";

    private readonly HttpClient _http;
    private readonly string _releaseBase;
    private readonly string _engineReleaseBase;

    /// <param name="engineReleaseBase">Where our engine builds (and their packs) are published.</param>
    public AotManager(HttpClient http, string engineReleaseBase, string releaseBase = DefaultReleaseBase)
    {
        _http = http;
        _releaseBase = releaseBase.EndsWith('/') ? releaseBase : releaseBase + "/";
        _engineReleaseBase = engineReleaseBase.EndsWith('/') ? engineReleaseBase : engineReleaseBase + "/";
    }

    /// <summary>Only arm64 phones get AOT packs (x86_64 emulators JIT).</summary>
    public static bool Supported => RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    public Uri RuntimePackUrl(string runtimeVersion) =>
        new($"{_releaseBase}android-runtime-aot-v{runtimeVersion}/runtime-aot-android-arm64.zip");

    public Uri OfficialEnginePackUrl(string engineVersion) =>
        new($"{_releaseBase}android-official-aot-v{EngineManager.NormalizeVersion(engineVersion)}/official-aot-android-arm64.zip");

    public Uri PatchedEnginePackUrl(string engineVersion) =>
        new($"{_engineReleaseBase}android-engine-v{EngineManager.NormalizeVersion(engineVersion)}/Robust.Client_android-aot-arm64.zip");

    /// <summary>Whether this exact pack is already installed in <paramref name="targetDir"/>.</summary>
    public static bool IsInstalled(Uri url, string targetDir)
    {
        var marker = Path.Combine(targetDir, ".aot-installed");
        return File.Exists(marker) && File.ReadAllText(marker).Trim() == url.AbsoluteUri;
    }

    /// <summary>Download size of a pack, or null when it is not published.</summary>
    public async Task<long?> GetSizeAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            return resp.Content.Headers.ContentLength ?? 0;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Installs the pack into <paramref name="targetDir"/> once (marker file keyed by URL).
    /// Returns the number of images installed, 0 if there is no pack.
    /// </summary>
    public async Task<int> EnsureAsync(Uri url, string targetDir, string what, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        if (!Supported || !Directory.Exists(targetDir))
            return 0;

        var marker = Path.Combine(targetDir, ".aot-installed");
        if (IsInstalled(url, targetDir))
            return Directory.GetFiles(targetDir, "*.dll.so").Length;

        var tmp = Path.Combine(targetDir, ".aot.zip.part");
        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return 0; // No pack published for this version: JIT only.

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
                    progress?.Report(new DownloadProgress($"Загрузка ускорения: {what}", done, total));
                }
            }

            var count = 0;
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var entry in zip.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (!name.EndsWith(".dll.so", StringComparison.Ordinal))
                        continue;
                    // Only images for assemblies that actually exist here.
                    if (!File.Exists(Path.Combine(targetDir, name[..^3])))
                        continue;
                    var dest = Path.Combine(targetDir, name);
                    if (File.Exists(dest))
                        File.Delete(dest);
                    entry.ExtractToFile(dest);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    count++;
                }
            }

            File.WriteAllText(marker, url.AbsoluteUri);
            return count;
        }
        catch (HttpRequestException)
        {
            return 0;
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}

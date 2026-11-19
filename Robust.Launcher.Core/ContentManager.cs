using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Robust.Launcher.Core;

public readonly record struct DownloadProgress(string Stage, long Done, long Total);

/// <summary>
/// Downloads and caches a server's client content (Content.Client.dll, prototypes, textures...).
/// Supports both manifest downloads (ACZ / manifest_url) and legacy zip downloads.
/// Files are stored content-addressed by hash, so different servers and versions share blobs.
/// </summary>
public sealed class ContentManager
{
    private const string ManifestHeader = "Robust Content Manifest 1";

    private readonly HttpClient _http;
    private readonly string _root;

    public ContentManager(HttpClient http, string cacheRoot)
    {
        _http = http;
        _root = cacheRoot;
        Directory.CreateDirectory(BlobDir);
        Directory.CreateDirectory(ManifestDir);
        Directory.CreateDirectory(ZipDir);
    }

    private string BlobDir => Path.Combine(_root, "blobs");
    private string ManifestDir => Path.Combine(_root, "manifests");
    private string ZipDir => Path.Combine(_root, "zips");

    public async Task<ContentBundle> EnsureContentAsync(ServerAddress address, ServerInfo info,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var build = info.Build ?? throw new InvalidDataException("Сервер не сообщил информацию о сборке (/info build).");

        if (build.Acz || !string.IsNullOrEmpty(build.ManifestUrl))
        {
            Uri manifestUrl, downloadUrl;
            if (build.Acz)
            {
                manifestUrl = new Uri(address.StatusUri, "manifest.txt");
                downloadUrl = new Uri(address.StatusUri, "download");
            }
            else
            {
                manifestUrl = new Uri(build.ManifestUrl!);
                downloadUrl = new Uri(build.ManifestDownloadUrl!);
            }

            return await EnsureManifestContentAsync(manifestUrl, downloadUrl, build.ManifestHash, progress, ct);
        }

        if (!string.IsNullOrEmpty(build.DownloadUrl))
            return await EnsureZipContentAsync(new Uri(build.DownloadUrl), build.Hash, progress, ct);

        throw new InvalidDataException("Сервер не предоставляет клиентский контент.");
    }

    // ---- Manifest (ACZ) content ----

    private async Task<ContentBundle> EnsureManifestContentAsync(Uri manifestUrl, Uri downloadUrl,
        string? expectedHash, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress("Загрузка манифеста", 0, 0));

        byte[] manifestData;
        var cachedPath = expectedHash != null ? Path.Combine(ManifestDir, expectedHash.ToUpperInvariant() + ".txt") : null;
        if (cachedPath != null && File.Exists(cachedPath))
        {
            manifestData = await File.ReadAllBytesAsync(cachedPath, ct);
        }
        else
        {
            manifestData = await _http.GetByteArrayAsync(manifestUrl, ct);
            if (expectedHash != null)
            {
                var actual = Convert.ToHexString(Native.Blake2B256(manifestData));
                if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Хэш манифеста не совпадает с заявленным сервером.");
                await File.WriteAllBytesAsync(cachedPath!, manifestData, ct);
            }
        }

        var entries = ParseManifest(manifestData);

        // Figure out what we don't have yet.
        var missing = new List<int>();
        var seen = new HashSet<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var hash = entries[i].Hash;
            if (!seen.Add(hash))
                continue;
            if (!File.Exists(BlobPath(hash)))
                missing.Add(i);
        }

        if (missing.Count > 0)
            await DownloadBlobsAsync(downloadUrl, entries, missing, progress, ct);

        progress?.Report(new DownloadProgress("Готово", entries.Count, entries.Count));

        var files = new Dictionary<string, string>(entries.Count);
        foreach (var (hash, path) in entries)
            files[path] = BlobPath(hash);

        return new ContentBundle(files, null);
    }

    internal static List<(string Hash, string Path)> ParseManifest(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != ManifestHeader)
            throw new InvalidDataException("Неизвестный формат манифеста.");

        var result = new List<(string, string)>(lines.Length);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                throw new InvalidDataException($"Повреждённая строка манифеста {i}.");
            result.Add((line[..space].ToUpperInvariant(), line[(space + 1)..]));
        }

        return result;
    }

    private async Task DownloadBlobsAsync(Uri downloadUrl, List<(string Hash, string Path)> entries,
        List<int> indices, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var body = new byte[indices.Count * 4];
        for (var i = 0; i < indices.Count; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(i * 4), indices[i]);

        using var req = new HttpRequestMessage(HttpMethod.Post, downloadUrl);
        req.Content = new ByteArrayContent(body);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Headers.Add("X-Robust-Download-Protocol", "1");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);

        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var flags = BinaryPrimitives.ReadInt32LittleEndian(header);
        var preCompressed = (flags & 1) != 0;

        var fileHeader = new byte[preCompressed ? 8 : 4];
        var tmpDir = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(tmpDir);

        for (var i = 0; i < indices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (hash, path) = entries[indices[i]];
            progress?.Report(new DownloadProgress("Загрузка контента", i, indices.Count));

            await stream.ReadExactlyAsync(fileHeader, ct);
            var blobLength = BinaryPrimitives.ReadInt32LittleEndian(fileHeader);
            var dataLength = preCompressed ? BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(4)) : 0;

            byte[] blob;
            if (dataLength == 0)
            {
                blob = new byte[blobLength];
                await stream.ReadExactlyAsync(blob, ct);
            }
            else
            {
                var compressed = new byte[dataLength];
                await stream.ReadExactlyAsync(compressed, ct);
                blob = new byte[blobLength];
                Native.ZstdDecompress(compressed, blob);
            }

            var actual = Convert.ToHexString(Native.Blake2B256(blob));
            if (actual != hash)
                throw new InvalidDataException($"Хэш файла {path} не совпадает.");

            var tmp = Path.Combine(tmpDir, hash);
            await File.WriteAllBytesAsync(tmp, blob, ct);
            var dest = BlobPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(tmp, dest, overwrite: true);
        }
    }

    private string BlobPath(string hash) => Path.Combine(BlobDir, hash[..2], hash);

    // ---- Zip content ----

    private async Task<ContentBundle> EnsureZipContentAsync(Uri url, string? expectedSha256,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var name = expectedSha256 != null
            ? expectedSha256.ToUpperInvariant()
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)));
        var zipPath = Path.Combine(ZipDir, name + ".zip");

        if (!File.Exists(zipPath) || expectedSha256 == null)
        {
            var tmp = zipPath + ".part";
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
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
                    progress?.Report(new DownloadProgress("Загрузка контента", done, total));
                }
            }

            if (expectedSha256 != null)
            {
                await using var f = File.OpenRead(tmp);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(f, ct));
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException("Хэш архива клиента не совпадает.");
                }
            }

            File.Move(tmp, zipPath, overwrite: true);
        }

        return new ContentBundle(null, zipPath);
    }

}

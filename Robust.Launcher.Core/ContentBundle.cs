using System.IO.Compression;
using System.Text.Json;

// NOTE: also compiled into Robust.Android.GameHost (net6.0).

namespace Robust.Launcher.Core;

/// <summary>
/// Downloaded content: either a path → blob file map (manifest) or a single zip.
/// Saved by the launcher and reopened by the game process.
/// </summary>
public sealed class ContentBundle
{
    public ContentBundle(Dictionary<string, string>? files, string? zipPath)
    {
        Files = files;
        ZipPath = zipPath;
    }

    public Dictionary<string, string>? Files { get; set; }
    public string? ZipPath { get; set; }

    public Robust.LoaderApi.IFileApi OpenFileApi()
    {
        if (Files != null)
            return new BlobFileApi(Files);
        return new ZipFileApi(ZipFile.OpenRead(ZipPath!));
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    public static ContentBundle Load(string path) =>
        JsonSerializer.Deserialize<ContentBundle>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Bad content bundle file");
}

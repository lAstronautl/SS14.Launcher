using System.Text.Json;
using Robust.Launcher.Core;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Persistent launcher state, stored as JSON in the app-private files dir.
/// </summary>
public sealed class LauncherSettings
{
    public LoginInfo? Login { get; set; }
    public string GuestName { get; set; } = "";
    public List<FavoriteServer> Favorites { get; set; } = new();
    /// <summary>Servers the user allowed to run in compatibility mode (official engine build, no sandbox).</summary>
    public HashSet<string> TrustedServers { get; set; } = new();
    public string HubUrl { get; set; } = ServerApi.DefaultHubUrl;
    public string EngineReleaseBase { get; set; } = EngineManager.DefaultReleaseBase;
    /// <summary>Launcher language ("" = system), like the desktop launcher's Language CVar.</summary>
    public string Language { get; set; } = "";
    /// <summary>Server list filters (same categories as the desktop launcher).</summary>
    public ServerFilterSet Filters { get; set; } = new();
    public string DiscordUrl { get; set; } = "https://discord.ss14.io/";
    public string WebsiteUrl { get; set; } = "https://spacestation14.com";
    public string NewsFeedUrl { get; set; } = "https://spacestation14.com/post/index.xml";
    public bool TouchControls { get; set; } = true;
    public int TouchOpacity { get; set; } = 60;
    public float TouchScale { get; set; } = 1f;
    /// <summary>Precompilation (AOT code download per engine): ask on first use of an engine, always, or never.</summary>
    public AotMode AotMode { get; set; } = AotMode.Ask;
    /// <summary>Per-engine answers to the precompilation question ("Да"/"Нет"), keyed by <see cref="AotKey"/>.</summary>
    public Dictionary<string, bool> AotDecisions { get; set; } = new();

    public static string AotKey(EngineSource source, string version) =>
        (source == EngineSource.Official ? "official-" : "") + EngineManager.NormalizeVersion(version);
    /// <summary>In-game settings chosen in the launcher (only those changed from the game's defaults).</summary>
    public Dictionary<string, string> GameCVars { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FilePath(string filesDir) => Path.Combine(filesDir, "launcher-settings.json");

    public static LauncherSettings Load(string filesDir)
    {
        try
        {
            var path = FilePath(filesDir);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path), Options) ?? new();
        }
        catch (Exception e)
        {
            global::Android.Util.Log.Warn("Launcher", $"Failed to load settings: {e}");
        }

        return new LauncherSettings();
    }

    public void Save(string filesDir)
    {
        var path = FilePath(filesDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class FavoriteServer
{
    public string Address { get; set; } = "";
    public string Name { get; set; } = "";
}

public enum AotMode
{
    Ask,
    Always,
    Never,
}

/// <summary>Answer to the precompilation question.</summary>
public enum AotChoice
{
    Yes,
    No,
    AlwaysYes,
    AlwaysNo,
}

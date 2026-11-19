using System.Text.Json.Serialization;

namespace Robust.Launcher.Core;

/// <summary>Response of a server's <c>/status</c> endpoint (also embedded in hub entries).</summary>
public sealed class ServerStatus
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("players")] public int Players { get; set; }
    [JsonPropertyName("soft_max_players")] public int SoftMaxPlayers { get; set; }
    [JsonPropertyName("map")] public string? Map { get; set; }
    [JsonPropertyName("preset")] public string? Preset { get; set; }
    [JsonPropertyName("run_level")] public int? RunLevel { get; set; }
    [JsonPropertyName("round_start_time")] public string? RoundStartTime { get; set; }
    [JsonPropertyName("panic_bunker")] public bool? PanicBunker { get; set; }
    [JsonPropertyName("tags")] public string[]? Tags { get; set; }
}

public sealed class HubServerEntry
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("statusData")] public ServerStatus? StatusData { get; set; }
    [JsonPropertyName("inferredTags")] public string[]? InferredTags { get; set; }
}

/// <summary>Response of a server's <c>/info</c> endpoint.</summary>
public sealed class ServerInfo
{
    [JsonPropertyName("connect_address")] public string? ConnectAddress { get; set; }
    [JsonPropertyName("auth")] public ServerAuthInfo? Auth { get; set; }
    [JsonPropertyName("build")] public ServerBuildInfo? Build { get; set; }
    [JsonPropertyName("desc")] public string? Description { get; set; }
    [JsonPropertyName("links")] public ServerInfoLink[]? Links { get; set; }
}

/// <summary>Link shown under a server's description (discord, wiki, web, github, forum, telegram icons).</summary>
public sealed record ServerInfoLink(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("url")] string Url);

public sealed class ServerAuthInfo
{
    /// <summary>"Optional", "Required" or "Disabled".</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "Optional";
    [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
}

public sealed class ServerBuildInfo
{
    [JsonPropertyName("engine_version")] public string? EngineVersion { get; set; }
    [JsonPropertyName("fork_id")] public string? ForkId { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
    [JsonPropertyName("acz")] public bool Acz { get; set; }
    [JsonPropertyName("manifest_download_url")] public string? ManifestDownloadUrl { get; set; }
    [JsonPropertyName("manifest_url")] public string? ManifestUrl { get; set; }
    [JsonPropertyName("manifest_hash")] public string? ManifestHash { get; set; }
}

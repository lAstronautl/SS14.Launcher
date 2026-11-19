using System.Net.Http.Json;

namespace Robust.Launcher.Core;

/// <summary>
/// Talks to the server hub and to individual game servers' status hosts.
/// </summary>
public sealed class ServerApi
{
    public const string DefaultHubUrl = "https://hub.spacestation14.com/";

    private readonly HttpClient _http;

    public ServerApi(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<HubServerEntry>> GetHubServersAsync(string hubUrl = DefaultHubUrl,
        CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<HubServerEntry>>(new Uri(new Uri(hubUrl), "api/servers"), ct);
        return list ?? new List<HubServerEntry>();
    }

    public async Task<ServerStatus> GetStatusAsync(ServerAddress address, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<ServerStatus>(new Uri(address.StatusUri, "status"), ct)
               ?? throw new InvalidDataException("Empty /status response");
    }

    public async Task<ServerInfo> GetInfoAsync(ServerAddress address, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<ServerInfo>(new Uri(address.StatusUri, "info"), ct)
               ?? throw new InvalidDataException("Empty /info response");
    }
}


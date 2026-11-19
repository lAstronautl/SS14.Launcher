using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Robust.Launcher.Core;

/// <summary>
/// Space Wizards account API (same API the official launcher uses).
/// </summary>
public sealed class AuthApi
{
    public const string DefaultAuthServer = "https://auth.spacestation14.com/";

    private readonly HttpClient _http;
    private readonly Uri _authServer;

    public AuthApi(HttpClient http, string authServer = DefaultAuthServer)
    {
        _http = http;
        _authServer = new Uri(authServer);
    }

    public string AuthServer => _authServer.AbsoluteUri;

    public async Task<LoginResult> LoginAsync(string username, string password, string? tfaCode = null,
        CancellationToken ct = default)
    {
        var request = new AuthenticateRequest(username, password, tfaCode);
        using var resp = await _http.PostAsJsonAsync(new Uri(_authServer, "api/auth/authenticate"), request, ct);

        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<AuthenticateResponse>(ct)
                       ?? throw new InvalidDataException("Empty auth response");
            return LoginResult.Ok(new LoginInfo(body.UserId, body.Username, body.Token, body.ExpireTime));
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var err = await TryRead<AuthenticateDenyResponse>(resp, ct);
            var code = err?.Code ?? AuthDenyCode.InvalidCredentials;
            return LoginResult.Fail(code, err?.Errors ?? new[] { "Неверный логин или пароль" });
        }

        return LoginResult.Fail(AuthDenyCode.UnknownError, new[] { $"Сервер авторизации ответил {(int)resp.StatusCode}" });
    }

    /// <summary>Gets a fresh token. Returns null if the token is no longer valid.</summary>
    public async Task<LoginToken?> RefreshAsync(LoginToken token, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(new Uri(_authServer, "api/auth/refresh"),
            new RefreshRequest(token.Token), ct);

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<RefreshResponse>(ct)
                   ?? throw new InvalidDataException("Empty refresh response");
        return new LoginToken(body.NewToken, body.ExpireTime);
    }

    /// <summary>Checks if a token is still accepted.</summary>
    public async Task<bool> PingAsync(LoginToken token, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_authServer, "api/auth/ping"));
        req.Headers.Authorization = new AuthenticationHeaderValue("SS14Auth", token.Token);
        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task LogoutAsync(LoginToken token, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(new Uri(_authServer, "api/auth/logout"),
            new LogoutRequest(token.Token), ct);
    }

    private static async Task<T?> TryRead<T>(HttpResponseMessage resp, CancellationToken ct) where T : class
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<T>(ct);
        }
        catch
        {
            return null;
        }
    }

    private sealed record AuthenticateRequest(string Username, string Password, string? TfaCode);

    private sealed record AuthenticateResponse(string Token, string Username, Guid UserId, DateTimeOffset ExpireTime);

    private sealed record AuthenticateDenyResponse(string[] Errors, AuthDenyCode Code);

    private sealed record RefreshRequest(string Token);

    private sealed record RefreshResponse(string NewToken, DateTimeOffset ExpireTime);

    private sealed record LogoutRequest(string Token);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthDenyCode
{
    None = 0,
    InvalidCredentials = 1,
    AccountUnconfirmed = 2,
    TfaRequired = 3,
    TfaInvalid = 4,
    AccountLocked = 5,
    UnknownError = -1,
}

public sealed record LoginToken(string Token, DateTimeOffset ExpireTime)
{
    public bool ShouldRefresh => ExpireTime - DateTimeOffset.UtcNow < TimeSpan.FromDays(15);
    public bool IsExpired => ExpireTime <= DateTimeOffset.UtcNow;
}

public sealed record LoginInfo(Guid UserId, string Username, string Token, DateTimeOffset ExpireTime)
{
    public LoginToken LoginToken => new(Token, ExpireTime);
}

public sealed record LoginResult(LoginInfo? Info, AuthDenyCode Code, string[] Errors)
{
    public bool Success => Info != null;
    public static LoginResult Ok(LoginInfo info) => new(info, AuthDenyCode.None, Array.Empty<string>());
    public static LoginResult Fail(AuthDenyCode code, string[] errors) => new(null, code, errors);
}

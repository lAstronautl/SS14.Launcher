using System.Text.Json;
using Robust.Launcher.Core;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Everything that happens between "tap Connect" and starting the game process:
/// server info, account token refresh, engine + content download, launch spec.
/// </summary>
public sealed class Connector
{
    private readonly HttpClient _http;
    private readonly LauncherSettings _settings;
    private readonly string _filesDir;
    private readonly string _cacheDir;

    public Connector(HttpClient http, LauncherSettings settings, string filesDir, string cacheDir)
    {
        _http = http;
        _settings = settings;
        _filesDir = filesDir;
        _cacheDir = cacheDir;

        // Engines used to live in external storage (cacheDir); they are in filesDir now. Free the old copies.
        var oldEngines = Path.Combine(cacheDir, "engines");
        if (Directory.Exists(oldEngines))
        {
            Task.Run(() =>
            {
                try { Directory.Delete(oldEngines, true); }
                catch { /* not critical */ }
            });
        }
    }

    /// <summary>Prepares everything and returns what the game process needs to start.</summary>
    /// <param name="confirmCompatMode">
    /// Asked when only the official engine build is available (runs without the content sandbox).
    /// Gets the engine version, returns whether the user agreed.
    /// </param>
    public async Task<GameLaunch> PrepareAsync(ServerAddress address, float defaultUiScale,
        Func<string, Task<bool>> confirmCompatMode, Func<string, long, Task<AotChoice>> askAot,
        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        progress.Report(new DownloadProgress("Получение информации о сервере", 0, 0));
        var info = await new ServerApi(_http).GetInfoAsync(address, ct);

        var engineVersion = info.Build?.EngineVersion;
        if (string.IsNullOrEmpty(engineVersion))
            throw new UserFacingException("Сервер не сообщил версию движка.");

        // Account.
        var login = _settings.Login;
        var authMode = info.Auth?.Mode ?? "Optional";
        if (login != null && authMode != "Disabled")
        {
            var auth = new AuthApi(_http);
            if (login.LoginToken.IsExpired)
            {
                login = null;
            }
            else if (login.LoginToken.ShouldRefresh)
            {
                progress.Report(new DownloadProgress("Обновление входа", 0, 0));
                var refreshed = await auth.RefreshAsync(login.LoginToken, ct);
                login = refreshed == null ? null : login with { Token = refreshed.Token, ExpireTime = refreshed.ExpireTime };
            }

            _settings.Login = login;
            _settings.Save(_filesDir);
        }

        if (login == null && authMode == "Required")
            throw new UserFacingException("Этот сервер требует вход в аккаунт. Войдите на вкладке «Аккаунт».");

        // Engine: our Android build of that version if published, otherwise the official build adapted at runtime.
        // Internal storage: precompiled .so images next to the engine DLLs must be loadable (external storage is noexec).
        var engines = new EngineManager(_http, _filesDir, _settings.EngineReleaseBase);
        var availability = await engines.CheckAvailabilityAsync(engineVersion, ct);
        if (availability == EngineAvailability.None)
            throw new UserFacingException($"Движок версии {engineVersion} не найден ни в Android-сборках, ни в официальных.");

        if (availability == EngineAvailability.Official && !_settings.TrustedServers.Contains(address.Original))
        {
            if (!await confirmCompatMode(engineVersion))
                throw new System.OperationCanceledException();
            _settings.TrustedServers.Add(address.Original);
            _settings.Save(_filesDir);
        }

        ResolvedEngine engine;
        try
        {
            engine = await engines.ResolveEngineAsync(engineVersion, progress, ct: ct);
        }
        catch (EngineTooOldException e)
        {
            throw new UserFacingException(e.Message);
        }
        catch (EngineNotAvailableException e)
        {
            throw new UserFacingException(e.Message);
        }

        // Content.
        var content = new ContentManager(_http, Path.Combine(_cacheDir, "content"));
        var bundle = await content.EnsureContentAsync(address, info, progress, ct);

        // .NET runtime: the Mono runtime of the .NET version this engine build targets.
        var runtimeInfo = EngineRuntimeInfo.Read(engine.Dir);
        // Must be internal storage: external storage is mounted noexec, so native code there cannot be loaded.
        var runtimes = new RuntimeManager(_http, _filesDir);
        var runtimeDir = await runtimes.EnsureRuntimeAsync(runtimeInfo.Major, progress, ct);

        // Precompiled native code for the runtime and the engine (published by CI): much faster startup than
        // JIT-compiling everything on the phone. Asked once per engine unless set to always/never. Never fatal.
        var useAot = false;
        if (AotManager.Supported && _settings.AotMode != AotMode.Never)
        {
            try
            {
                useAot = await PrepareAotAsync(runtimes, runtimeInfo.Major, runtimeDir, engine, engineVersion, askAot,
                    progress, ct);
            }
            catch (Exception e) when (e is not System.OperationCanceledException)
            {
                global::Android.Util.Log.Warn("Launcher", $"AOT packs: {e}");
            }
        }

        var launchDir = Path.Combine(_filesDir, "launch");
        Directory.CreateDirectory(launchDir);
        var bundlePath = Path.Combine(launchDir, "content.json");
        bundle.Save(bundlePath);

        var username = login?.Username ?? (string.IsNullOrWhiteSpace(_settings.GuestName) ? null : _settings.GuestName);
        var uiScale = _settings.UiScale > 0 ? _settings.UiScale : defaultUiScale;
        var cvars = new List<string>(CompatShims.AndroidCVars)
        {
            $"display.uiScale={uiScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        };
        // Game settings from the launcher's "Игра" tab; last wins, so they override the Android defaults.
        cvars.AddRange(GameSettings.ToCVars(_settings));

        var spec = new GameLaunchSpec
        {
            EngineDir = engine.Dir,
            EngineSource = engine.Source,
            ContentBundlePath = bundlePath,
            EngineArgs = LaunchArgs.BuildEngineArgs(address, info, username, cvars),
            Environment = LaunchArgs.BuildAuthEnvironment(login, info),
            // Lets the sandbox verifier of our engine builds find framework assemblies.
            FrameworkDir = Path.Combine(runtimeDir, "lib"),
        };
        // Official builds keep user data under $HOME (their Linux code path).
        spec.Environment["HOME"] = _filesDir;

        // The spec may contain the auth token: it lives in app-private storage and the game deletes it after reading.
        var specPath = Path.Combine(launchDir, "spec.json");
        await File.WriteAllTextAsync(specPath, JsonSerializer.Serialize(spec), ct);

        var config = string.Join("\n", runtimeInfo.ConfigProperties.Select(kv => $"{kv.Key}={kv.Value}"));
        return new GameLaunch(specPath, runtimeDir, config, runtimeInfo.Major, useAot);
    }

    /// <returns>Whether the game should run with precompiled code.</returns>
    private async Task<bool> PrepareAotAsync(RuntimeManager runtimes, int major, string runtimeDir, ResolvedEngine engine,
        string engineVersion, Func<string, long, Task<AotChoice>> askAot, IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        var aot = new AotManager(_http, _settings.EngineReleaseBase);
        var engineUrl = engine.Source == EngineSource.Official
            ? aot.OfficialEnginePackUrl(engineVersion)
            : aot.PatchedEnginePackUrl(engineVersion);
        var runtimeUrl = runtimes.GetInstalledVersion(major) is { } rtVer ? aot.RuntimePackUrl(rtVer) : null;
        var runtimeLib = Path.Combine(runtimeDir, "lib");

        // Already precompiled earlier: just use it.
        if (AotManager.IsInstalled(engineUrl, engine.Dir))
        {
            if (runtimeUrl != null)
                await aot.EnsureAsync(runtimeUrl, runtimeLib, "среда .NET", progress, ct);
            return true;
        }

        var key = LauncherSettings.AotKey(engine.Source, engineVersion);
        if (_settings.AotMode == AotMode.Ask && _settings.AotDecisions.TryGetValue(key, out var decided) && !decided)
            return false;

        progress.Report(new DownloadProgress("Проверка предкомпиляции", 0, 0));
        var engineSize = await aot.GetSizeAsync(engineUrl, ct);
        if (engineSize == null)
            return false; // Nothing published for this engine: nothing to offer.

        if (_settings.AotMode == AotMode.Ask && !_settings.AotDecisions.ContainsKey(key))
        {
            long total = engineSize.Value;
            if (runtimeUrl != null && !AotManager.IsInstalled(runtimeUrl, runtimeLib))
                total += await aot.GetSizeAsync(runtimeUrl, ct) ?? 0;

            var label = (engine.Source == EngineSource.Official ? "официальном движке " : "движке ")
                        + EngineManager.NormalizeVersion(engineVersion);
            var choice = await askAot(label, total);
            switch (choice)
            {
                case AotChoice.Yes:
                    _settings.AotDecisions[key] = true;
                    break;
                case AotChoice.No:
                    _settings.AotDecisions[key] = false;
                    break;
                case AotChoice.AlwaysYes:
                    _settings.AotMode = AotMode.Always;
                    break;
                case AotChoice.AlwaysNo:
                    _settings.AotMode = AotMode.Never;
                    break;
            }

            _settings.Save(_filesDir);
            if (choice is AotChoice.No or AotChoice.AlwaysNo)
                return false;
        }

        if (runtimeUrl != null)
            await aot.EnsureAsync(runtimeUrl, runtimeLib, "среда .NET", progress, ct);
        return await aot.EnsureAsync(engineUrl, engine.Dir, "предкомпиляция движка", progress, ct) > 0;
    }
}

/// <param name="RuntimeConfig">Engine runtimeconfig.json properties as "key=value" lines for the native host.</param>
public sealed record GameLaunch(string SpecPath, string RuntimeDir, string RuntimeConfig, int DotnetMajor, bool UseAot);

public sealed class UserFacingException(string message) : Exception(message);

using System.Diagnostics;
using System.Text.Json;
using Robust.Launcher.Core;

// Desktop harness doing what the Android app does, minus Android:
//   hub | info <addr>
//   play <addr> [--engine-zip <zip>] [--official] [--cache <dir>] [--headless] [--username <name>]
//       Prepares engine + content, then runs Robust.Android.GameHost in a child process on the .NET runtime
//       version the engine was built for (like the Android game process does with a downloaded Mono runtime).
//   runtime <major>   download the Android Mono runtime of that .NET version (tests RuntimeManager)
var cache = Path.GetFullPath(GetOpt("--cache") ?? Path.Combine(Path.GetTempPath(), "robust-launcher-cli"));
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("RobustLauncherCli/1.0");
var api = new ServerApi(http);
var progress = new Progress<DownloadProgress>(p => Console.Write($"\r{p.Stage}: {p.Done}/{p.Total}      "));

switch (args.ElementAtOrDefault(0))
{
    case "hub":
    {
        var servers = await api.GetHubServersAsync();
        Console.WriteLine($"{servers.Count} servers");
        foreach (var s in servers.Take(15))
            Console.WriteLine($"  {s.StatusData?.Players,3}/{s.StatusData?.SoftMaxPlayers,-3} {s.StatusData?.Name} [{s.Address}]");
        return 0;
    }
    case "info":
    {
        var addr = ServerAddress.Parse(args[1]);
        var info = await api.GetInfoAsync(addr);
        var status = await api.GetStatusAsync(addr);
        Console.WriteLine($"{status.Name}: {status.Players}/{status.SoftMaxPlayers}");
        Console.WriteLine($"engine {info.Build?.EngineVersion} fork {info.Build?.ForkId} acz {info.Build?.Acz} auth {info.Auth?.Mode}");
        return 0;
    }
    case "runtime":
    {
        var dir = await new RuntimeManager(http, cache).EnsureRuntimeAsync(int.Parse(args[1]), progress);
        Console.WriteLine($"\n{dir}: {Directory.GetFiles(Path.Combine(dir, "lib")).Length} assemblies, " +
                          $"{string.Join(", ", Directory.GetFiles(Path.Combine(dir, "native")).Select(Path.GetFileName))}");
        return 0;
    }
    case "play":
    {
        var addr = ServerAddress.Parse(args[1]);
        var info = await api.GetInfoAsync(addr);
        var engineVersion = info.Build?.EngineVersion ?? throw new Exception("no engine version");
        Console.WriteLine($"Server engine: {engineVersion}");

        var engines = new EngineManager(http, cache);
        if (GetOpt("--engine-zip") is { } zip)
            EngineManager.InstallFromZip(zip, engines.GetInstallDir(engineVersion));
        var engine = await engines.ResolveEngineAsync(engineVersion, progress, forceOfficial: args.Contains("--official"));
        var runtime = EngineRuntimeInfo.Read(engine.Dir);
        Console.WriteLine($"\nEngine: {engine.Dir} ({engine.Source}), needs .NET {runtime.Version}");

        var content = new ContentManager(http, Path.Combine(cache, "content"));
        var bundle = await content.EnsureContentAsync(addr, info, progress);
        var bundlePath = Path.Combine(cache, "bundle.json");
        bundle.Save(bundlePath);
        Console.WriteLine($"\nContent files: {bundle.Files?.Count ?? -1}");

        // --android: same CVars the Android launcher passes (GLES compat mode, no texture preload, ...).
        var engineArgs = LaunchArgs.BuildEngineArgs(addr, info, GetOpt("--username") ?? "AndroidTest",
            args.Contains("--android") ? CompatShims.AndroidCVars : null).ToList();
        if (args.Contains("--headless"))
            engineArgs.Add("--headless");

        var specPath = Path.Combine(cache, "spec.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(new GameLaunchSpec
        {
            EngineDir = engine.Dir,
            EngineSource = engine.Source,
            ContentBundlePath = bundlePath,
            EngineArgs = engineArgs.ToArray(),
        }));

        // Run the game host on the engine's .NET major version.
        // Run from its own directory so nothing of ours (e.g. a net10 Robust.LoaderApi.dll) is on its probing path.
        var hostDir = Directory.CreateDirectory(Path.Combine(cache, "gamehost")).FullName;
        foreach (var f in Directory.GetFiles(AppContext.BaseDirectory, "Robust.Android.GameHost.*"))
            File.Copy(f, Path.Combine(hostDir, Path.GetFileName(f)), overwrite: true);
        var hostDll = Path.Combine(hostDir, "Robust.Android.GameHost.dll");
        var rcPath = Path.Combine(cache, "gamehost.runtimeconfig.json");
        File.WriteAllText(rcPath, JsonSerializer.Serialize(new
        {
            runtimeOptions = new
            {
                framework = new { name = "Microsoft.NETCore.App", version = $"{runtime.Major}.0.0" },
                rollForward = "LatestPatch",
                configProperties = runtime.ConfigProperties,
            }
        }));

        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        foreach (var a in new[] { "exec", "--runtimeconfig", rcPath, hostDll, specPath, Path.Combine(cache, "last-crash.txt") })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
    default:
        Console.WriteLine("commands: hub | info <addr> | runtime <major> | play <addr> [--engine-zip z] [--official] [--headless] [--android]");
        return 1;
}

string? GetOpt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

using System.Text.Json;
using Robust.Launcher.Core;

namespace Robust.Android.GameHost;

/// <summary>
/// Game process entry point.
/// Usage: Robust.Android.GameHost &lt;spec.json&gt; [crash-file]
/// On Android this is started by the native host (libmain.so) through the Mono embedding API.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var crashFile = args.Length > 1 ? args[1] : null;
        try
        {
            if (args.Length < 1)
                throw new ArgumentException("Usage: Robust.Android.GameHost <spec.json> [crash-file]");

            var spec = JsonSerializer.Deserialize<GameLaunchSpec>(File.ReadAllText(args[0]))
                       ?? throw new InvalidDataException("Bad launch spec");
            // May contain the auth token; no reason to keep it on disk.
            File.Delete(args[0]);

            Console.WriteLine($"[GameHost] .NET {Environment.Version}, engine {spec.EngineDir} ({spec.EngineSource})");
            StartMemoryLogger();
            EngineLoader.Run(spec);
            Console.WriteLine("[GameHost] Engine exited");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[GameHost] Fatal: {e}");
            if (crashFile != null)
            {
                try
                {
                    File.WriteAllText(crashFile, $".NET {Environment.Version}\n{e}");
                }
                catch
                {
                    // ignored
                }
            }

            return 1;
        }
    }

    /// <summary>
    /// Logs memory use every few seconds, so a process killed for lack of memory shows the growth before it died.
    /// </summary>
    private static void StartMemoryLogger()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(5000);
                try
                {
                    Console.Error.WriteLine(
                        $"[mem] managed={GC.GetTotalMemory(false) / 1048576} MB, rss={ReadRssMb()} MB, gc0/1/2={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                }
                catch
                {
                    // Never let diagnostics kill the game.
                }
            }
        })
        {
            IsBackground = true,
            Name = "GameHost memory logger",
        };
        thread.Start();
    }

    private static string ReadRssMb()
    {
        const string path = "/proc/self/status";
        if (!File.Exists(path))
            return "?";
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("VmRSS:"))
                continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                return (kb / 1024).ToString();
        }

        return "?";
    }
}

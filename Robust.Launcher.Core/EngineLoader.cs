using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Robust.LoaderApi;

// NOTE: this file is also compiled into Robust.Android.GameHost (net6.0), which runs inside whatever
// .NET runtime version the engine was built for. Keep it free of newer-than-net6 APIs.

namespace Robust.Launcher.Core;

public enum EngineSource
{
    /// <summary>Our Android build of the engine (Android support compiled in).</summary>
    Patched,

    /// <summary>
    /// Official Space Wizards build of that version, adapted to Android from the outside (see <see cref="CompatShims"/>).
    /// Runs with the content sandbox disabled.
    /// </summary>
    Official,
}

/// <summary>
/// Everything the game process needs to start the engine. Serialized by the launcher, read by the game process.
/// </summary>
public sealed class GameLaunchSpec
{
    public string EngineDir { get; set; } = "";
    public EngineSource EngineSource { get; set; } = EngineSource.Patched;
    public string ContentBundlePath { get; set; } = "";
    public string[] EngineArgs { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>
    /// Directory with the .NET framework assemblies of the runtime the game runs on.
    /// Used by the content sandbox verifier where the runtime's own assemblies have no file path (Android).
    /// </summary>
    public string? FrameworkDir { get; set; }
}

/// <summary>
/// Loads a downloaded engine build and runs it through its <see cref="ILoaderEntryPoint"/>,
/// the same way the official launcher's loader does.
/// </summary>
public static class EngineLoader
{
    /// <summary>
    /// Runs the engine. Blocks until the game exits. Must be called on the thread that should own the window.
    /// </summary>
    public static void Run(GameLaunchSpec spec)
    {
        foreach (var kv in spec.Environment)
            System.Environment.SetEnvironmentVariable(kv.Key, kv.Value);

        var engineDir = Path.GetFullPath(spec.EngineDir);

        if (spec.EngineSource == EngineSource.Official)
            CompatShims.ApplyForOfficialBuild(engineDir);

        // Framework assemblies for the content sandbox verifier (see AssemblyTypeChecker).
        if (spec.FrameworkDir != null && Directory.Exists(spec.FrameworkDir))
            System.Environment.SetEnvironmentVariable("ROBUST_FRAMEWORK_DIR", spec.FrameworkDir);

        // Every managed dependency of the engine, including Robust.LoaderApi, comes from the engine's directory,
        // so the loader API types we hand over are exactly the ones the engine was built against.
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            if (name.Name == null)
                return null;
            var path = Path.Combine(engineDir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };

        RunCore(spec, engineDir);
    }

    // Separate method: it touches Robust.LoaderApi types, which must only be resolved after the hook above exists.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunCore(GameLaunchSpec spec, string engineDir)
    {
        var clientAsm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(engineDir, "Robust.Client.dll"));
        var attr = clientAsm.GetCustomAttribute<LoaderEntryPointAttribute>()
                   ?? throw new InvalidOperationException("Robust.Client has no LoaderEntryPoint.");
        var entry = (ILoaderEntryPoint)Activator.CreateInstance(attr.LoaderEntryPointType)!;

        var engineFiles = new DirectoryFileApi(engineDir);
        var content = ContentBundle.Load(spec.ContentBundlePath).OpenFileApi();

        entry.Main(new MainArgs(spec.EngineArgs, engineFiles, new[] { new ApiMount(content, "/") }));
    }

    private sealed class MainArgs : IMainArgs
    {
        public MainArgs(string[] args, IFileApi fileApi, IEnumerable<ApiMount> mounts)
        {
            Args = args;
            FileApi = fileApi;
            ApiMounts = mounts;
        }

        public string[] Args { get; }
        public IFileApi FileApi { get; }
        public IRedialApi? RedialApi => null;
        public IEnumerable<ApiMount>? ApiMounts { get; }
    }
}

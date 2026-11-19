using System.Reflection;
using System.Runtime.Loader;

namespace Robust.Launcher.Core;

/// <summary>
/// Makes an unmodified official engine build run on Android, from the outside:
/// no engine DLL is changed, everything is done through the loader, environment variables and CVars.
/// </summary>
public static class CompatShims
{
    /// <summary>
    /// CVars that reproduce what our Android engine patch does at runtime. Harmless for our own builds too,
    /// and unknown CVars are simply stored by older engines.
    /// </summary>
    public static readonly string[] AndroidCVars =
    {
        "display.compat=true", // OpenGL ES 3 / 2 only
        "display.thread_window_api=false", // SDL's Android backend owns the thread
        "display.windowing_api=sdl3",
        "discord.enabled=false",
        "font.system=false", // no fontconfig on Android
        "sys.gc_collect_start=false", // sets GCSettings.LargeObjectHeapCompactionMode: CoreCLR only, throws on Mono
        // Memory: phones have 3-6 GB shared with the GPU. Our engine builds skip RSI atlas preloading with this;
        // older/official builds at least skip plain texture preloading. Smaller atlases waste less on padding.
        "res.texturepreloadingenabled=false",
        "res.rsi_atlas_size=4096",
        // A slow phone can fall behind while it processes the first full game state.
        "net.connection_timeout=120",
        "net.max_buffer_size=2048",
        // OpenAL Soft's HRTF mixing path crashed on a phone (SIGSEGV writing past a buffer in alsoft-mixer),
        // and it is also the most CPU-heavy part of audio. Plain stereo panning instead.
        "audio.hrtf=false",
    };

    /// <summary>
    /// Call before loading Robust.Client from <paramref name="engineDir"/>.
    /// </summary>
    public static void ApplyForOfficialBuild(string engineDir)
    {
        // Without our patch the sandbox verifier cannot find framework assemblies on Android
        // (they have no file path inside the APK), so official builds run unsandboxed.
        // The launcher asks the user before connecting in this mode.
        Environment.SetEnvironmentVariable("ROBUST_DISABLE_SANDBOX", "1");

        if (!OperatingSystem.IsAndroid())
            return;

        // OpenTK picks "libopenal.so.1" on Linux-like systems; Android libraries are always lib*.so.
        // OpenTK registers its own DllImport resolver, but exposes an override for the file name.
        var openAlPath = Path.Combine(engineDir, "OpenTK.Audio.OpenAL.dll");
        if (File.Exists(openAlPath))
        {
            var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(openAlPath);
            var container = asm.GetType("OpenTK.Audio.OpenAL.OpenALLibraryNameContainer");
            var prop = container?.GetProperty("OverridePath", BindingFlags.Public | BindingFlags.Static);
            prop?.SetValue(null, "libopenal.so");
        }

        // Everything else (SDL3, zstd, libsodium, freetype6) resolves through the runtime's standard
        // "lib<name>.so" probing against the libraries shipped in the APK.
    }
}

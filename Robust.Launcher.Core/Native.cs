using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Robust.Launcher.Core;

/// <summary>
/// Minimal bindings to zstd and libsodium, which ship with the engine anyway.
/// </summary>
internal static unsafe partial class Native
{
    private const string Zstd = "zstd";
    private const string Sodium = "libsodium";

#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Already set.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            Zstd => OperatingSystem.IsWindows()
                ? new[] { "libzstd.dll", "zstd.dll" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "libzstd.1.dylib", "libzstd.dylib" }
                    : new[] { "libzstd.so", "zstd.so", "libzstd.so.1" },
            Sodium => OperatingSystem.IsWindows()
                ? new[] { "libsodium.dll" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "libsodium.dylib", "libsodium.23.dylib" }
                    : new[] { "libsodium.so", "libsodium.so.23", "libsodium.so.26" },
            _ => Array.Empty<string>()
        };

        foreach (var c in candidates)
        {
            if (NativeLibrary.TryLoad(c, assembly, path, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [DllImport(Zstd)]
    private static extern nuint ZSTD_decompress(void* dst, nuint dstCapacity, void* src, nuint compressedSize);

    [DllImport(Zstd)]
    private static extern uint ZSTD_isError(nuint code);

    [DllImport(Sodium)]
    private static extern int sodium_init();

    [DllImport(Sodium)]
    private static extern int crypto_generichash(byte* @out, nuint outlen, byte* @in, ulong inlen, byte* key,
        nuint keylen);

    private static int _sodiumInit;

    public static void ZstdDecompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        fixed (byte* s = src)
        fixed (byte* d = dst)
        {
            var res = ZSTD_decompress(d, (nuint)dst.Length, s, (nuint)src.Length);
            if (ZSTD_isError(res) != 0)
                throw new InvalidDataException("zstd decompression failed");
            if ((int)res != dst.Length)
                throw new InvalidDataException("zstd decompressed size mismatch");
        }
    }

    /// <summary>BLAKE2b-256, the hash used by Robust content manifests.</summary>
    public static byte[] Blake2B256(ReadOnlySpan<byte> data)
    {
        if (Interlocked.Exchange(ref _sodiumInit, 1) == 0 && sodium_init() < 0)
            throw new InvalidOperationException("sodium_init failed");

        var result = new byte[32];
        fixed (byte* o = result)
        fixed (byte* i = data)
        {
            if (crypto_generichash(o, 32, i, (ulong)data.Length, null, 0) != 0)
                throw new InvalidOperationException("crypto_generichash failed");
        }

        return result;
    }
}

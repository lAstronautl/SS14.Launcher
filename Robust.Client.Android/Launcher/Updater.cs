using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Launcher self-update: looks for the newest "android-launcher-v&lt;version&gt;" release of the launcher repository,
/// downloads its APK and hands it to the system installer (PackageInstaller session; Android asks the player to
/// confirm). An update only installs over the current app when both are signed with the same key, so releases must
/// come from CI with the project's release keystore.
/// </summary>
internal static class Updater
{
    public const string ReleasesApi = "https://api.github.com/repos/lAstronautl/SS14.Launcher/releases?per_page=30";
    private const string TagPrefix = "android-launcher-v";

    public sealed record Update(Version Version, string Tag, string ApkUrl, long Size);

    public static Version Current(Context ctx) =>
        Parse(ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0)!.VersionName) ?? new Version(0, 0);

    private static Version? Parse(string? v)
    {
        if (string.IsNullOrEmpty(v))
            return null;
        var dash = v.IndexOf('-');
        if (dash >= 0)
            v = v[..dash];
        return Version.TryParse(v, out var ver) ? ver : null;
    }

    /// <returns>The newest release newer than the installed version, or null.</returns>
    public static async Task<Update?> CheckAsync(HttpClient http, Context ctx, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        req.Headers.UserAgent.ParseAdd("PDAStation14-Android-Launcher");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        Update? best = null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean())
                continue;
            if (rel.TryGetProperty("prerelease", out var p) && p.GetBoolean())
                continue;
            var tag = rel.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || Parse(tag[TagPrefix.Length..]) is not { } ver)
                continue;

            foreach (var asset in rel.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (best == null || ver > best.Version)
                    best = new Update(ver, tag, asset.GetProperty("browser_download_url").GetString()!,
                        asset.GetProperty("size").GetInt64());
                break;
            }
        }

        return best != null && best.Version > Current(ctx) ? best : null;
    }

    /// <summary>Downloads the APK straight into an installer session and commits it.</summary>
    public static async Task InstallAsync(HttpClient http, Context ctx, Update update, IProgress<(long Done, long Total)> progress,
        CancellationToken ct = default)
    {
        var installer = ctx.PackageManager!.PackageInstaller;
        var sp = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        sp.SetAppPackageName(ctx.PackageName);
        var id = installer.CreateSession(sp);
        using var session = installer.OpenSession(id);
        try
        {
            using (var resp = await http.GetAsync(update.ApkUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? update.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var dst = session.OpenWrite("update.apk", 0, total > 0 ? total : -1);
                var buf = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    progress.Report((done, total));
                }

                session.Fsync(dst);
            }

            var intent = new Intent(ctx, typeof(UpdateStatusReceiver));
            var flags = PendingIntentFlags.UpdateCurrent;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                flags |= PendingIntentFlags.Mutable; // The installer fills in the status extras.
            var pending = PendingIntent.GetBroadcast(ctx, id, intent, flags)!;
            session.Commit(pending.IntentSender!);
        }
        catch
        {
            session.Abandon();
            throw;
        }
    }
}

/// <summary>Installer callbacks: shows the system confirmation and reports failures.</summary>
[BroadcastReceiver(Exported = false)]
public sealed class UpdateStatusReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null)
            return;

        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, -999);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
#pragma warning disable CA1422
                if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
#pragma warning restore CA1422
                {
                    confirm.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(confirm);
                }

                break;
            case PackageInstallStatus.Success:
                break;
            default:
                var msg = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? status.ToString();
                global::Android.Widget.Toast.MakeText(context, Loc.Get("android-update-failed", ("error", msg)),
                    global::Android.Widget.ToastLength.Long)!.Show();
                break;
        }
    }
}

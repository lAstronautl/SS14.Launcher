using System.Text;
using System.Text.RegularExpressions;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Collects what happened in the last game run without adb: our own log file plus logcat.
/// Apps may read logcat lines of their own UID, and the ":game" process shares the app's UID.
/// The engine's console log goes straight to logcat on Android (Console.OpenStandardOutput is a logcat stream),
/// and native crashes are reported there too ("F libc: Fatal signal ...").
/// </summary>
internal static class GameLogCollector
{
    public static string LogFile(string filesDir) => Path.Combine(filesDir, "last-game.log");
    public static string LogcatSnapshot(string filesDir) => Path.Combine(filesDir, "last-game-logcat.txt");

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m|\[\d{1,2}m", RegexOptions.Compiled);
    private static readonly Regex PidInLog = new(@"\[host\] PID: (\d+)", RegexOptions.Compiled);
    private static readonly Regex PidInLogcat = new(@"RobustHost\(\s*(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex Capability = new(@"clyde\.ogl:\s+\w+: (True|False|true|false)\s*$", RegexOptions.Compiled);

    private static readonly string[] SystemCrashMarkers =
        { "Fatal signal", "lowmemorykiller", "lmkd", "Process com.pdastation14.launcher:game", "has died", "Killing" };

    /// <summary>Whole logcat dump of this app (only our UID is visible to us).</summary>
    public static string ReadRawLogcat()
    {
        try
        {
            var proc = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-d", "-v", "time", "-t", "8000" })!;
            using var reader = new StreamReader(proc.InputStream!);
            var all = reader.ReadToEnd();
            proc.WaitFor();
            return all;
        }
        catch (Exception e)
        {
            return $"(не удалось прочитать logcat: {e.Message})";
        }
    }

    /// <summary>Keeps only the last game run's lines (and system crash lines about it), without noise.</summary>
    public static List<string> FilterRun(string rawLogcat, string? pid)
    {
        var lines = rawLogcat.Split('\n').Select(l => Ansi.Replace(l.TrimEnd('\r'), "")).ToList();

        if (pid == null)
        {
            // Fall back to the newest native host pid seen in logcat.
            foreach (var l in lines)
            {
                var m = PidInLogcat.Match(l);
                if (m.Success)
                    pid = m.Groups[1].Value;
            }
        }

        var pidTag = pid != null ? $"({pid.PadLeft(5)})" : null;
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0)
                continue;

            var ours = pidTag != null && (line.Contains(pidTag) || line.Contains($"({pid})"));
            var crash = SystemCrashMarkers.Any(line.Contains) && (pid == null || line.Contains(pid) || line.Contains(":game"));
            if (!ours && !crash)
                continue;
            if (IsNoise(line))
                continue;

            result.Add(line.Length > 400 ? line[..400] + "…" : line);
        }

        return CollapseRepeats(result);
    }

    private static readonly Regex LogcatPrefix = new(@"^\d\d-\d\d \d\d:\d\d:\d\d\.\d+ ", RegexOptions.Compiled);

    /// <summary>Consecutive lines that differ only by timestamp become one line with a "(×N)" count.</summary>
    internal static List<string> CollapseRepeats(List<string> lines)
    {
        var result = new List<string>();
        string? lastKey = null;
        var count = 0;
        foreach (var line in lines)
        {
            var key = LogcatPrefix.Replace(line, "");
            if (key == lastKey)
            {
                count++;
                continue;
            }

            if (count > 1)
                result[^1] += $"  (×{count})";
            result.Add(line);
            lastKey = key;
            count = 1;
        }

        if (count > 1)
            result[^1] += $"  (×{count})";
        return result;
    }

    private static bool IsNoise(string line)
    {
        if (line.Contains("[ALSOFT] (II)") || line.Contains("/openal") && !line.Contains("(EE)"))
            return true;
        if (line.Contains("OpenGL Extensions:") || Capability.IsMatch(line))
            return true;
        // RobustGame duplicates what the host already logged to logcat.
        if (line.Contains("/RobustGame(") && line.Contains("[ALSOFT]"))
            return true;
        if (line.Contains("V/SDL") && !(line.Contains("onTrimMemory") || line.Contains("onLowMemory")
                                        || line.Contains("Finished main") || line.Contains("thread ends")
                                        || line.Contains("onDestroy") || line.Contains("surfaceDestroyed")))
            return true;
        return false;
    }

    public static void SnapshotLogcat(string filesDir)
    {
        try
        {
            File.WriteAllText(LogcatSnapshot(filesDir), ReadRawLogcat());
        }
        catch
        {
            // ignored
        }
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Summary first (so a cut-off message still has what matters), then the compact log of the last run.
    /// </summary>
    public static string Collect(string filesDir)
    {
        var hostLog = ReadShared(LogFile(filesDir));
        var pidMatch = PidInLog.Matches(hostLog).LastOrDefault();
        var pid = pidMatch?.Groups[1].Value;

        var snap = LogcatSnapshot(filesDir);
        var raw = File.Exists(snap) ? File.ReadAllText(snap) : ReadRawLogcat();
        var run = FilterRun(raw, pid);

        var important = run.Where(l => l.Contains("Fatal") || l.Contains("Exception") || l.Contains("[FATL]")
                                       || l.Contains("[ERRO]") || l.Contains("lowmemorykiller") || l.Contains("lmkd")
                                       || l.Contains("onLowMemory") || l.Contains("Disconnect")
                                       || l.Contains("Status changed") || l.Contains("timed out")
                                       || l.Contains("Runlevel changed") || l.Contains("Exceeded maximum state")
                                       || l.Contains("Cannot keep up")).ToList();
        var mem = run.Where(l => l.Contains("[mem]")).TakeLast(5).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"=== ИТОГ (pid {pid ?? "?"}) ===");
        sb.AppendLine("--- ошибки ---");
        foreach (var l in important.TakeLast(30)) sb.AppendLine(l);
        sb.AppendLine("--- память ---");
        foreach (var l in mem) sb.AppendLine(l);
        sb.AppendLine("--- последние строки ---");
        foreach (var l in run.TakeLast(40)) sb.AppendLine(l);
        sb.AppendLine();
        sb.AppendLine("=== host log ===");
        sb.AppendLine(Ansi.Replace(hostLog, "").TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"=== logcat запуска ({run.Count} строк) ===");
        foreach (var l in run.TakeLast(1500)) sb.AppendLine(l);
        return sb.ToString();
    }

    /// <summary>Writes the text where robust.android.LogProvider serves it and returns its content URI.</summary>
    public static global::Android.Net.Uri ExportForSharing(global::Android.Content.Context ctx, string text)
    {
        File.WriteAllText(Path.Combine(ctx.CacheDir!.AbsolutePath, "game-log.txt"), text);
        return global::Android.Net.Uri.Parse("content://com.pdastation14.launcher.logs/game-log.txt")!;
    }
}

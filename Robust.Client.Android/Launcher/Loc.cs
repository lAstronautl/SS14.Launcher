using System.Globalization;
using System.Text;
using Android.Content;
using Linguini.Bundle;
using Linguini.Bundle.Builder;
using Linguini.Shared.Types.Bundle;
using Linguini.Syntax.Parser;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// The desktop launcher's localization (SS14.Launcher/Localization/LocalizationManager.cs) on Android:
/// same Fluent files (SS14.Launcher/Assets/Locale, packaged as assets), same Linguini setup, same languages,
/// en-US loaded first as the fallback.
/// </summary>
internal static class Loc
{
    private const string FallbackCulture = "en";
    private const string FallbackCultureSub = "en-US";

    /// <summary>Same list and order as LocalizationManager.AvailableLanguages.</summary>
    public static readonly string[] AvailableLanguages =
    {
        FallbackCulture, "nl", "el", "de", "ru", "pt-BR", "es", "uk", "fr", "tr", "sv", "fi", "zh-Hans", "et", "pl",
        "ja", "it", "nb-NO", "hu", "eo", "ro", "ga",
    };

    private static FluentBundle? _bundle;

    public static CultureInfo SystemCulture { get; private set; } = CultureInfo.InvariantCulture;
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    /// <param name="language">Saved language ("" = system), like the launcher's Language CVar.</param>
    public static void Initialize(Context ctx, string language)
    {
        SystemCulture = Match(CultureInfo.CurrentUICulture) ?? new CultureInfo(FallbackCulture);
        Load(ctx, string.IsNullOrEmpty(language) ? SystemCulture : new CultureInfo(language));
    }

    public static void Load(Context ctx, CultureInfo culture)
    {
        var bundle = LinguiniBuilder.Builder()
            .CultureInfo(culture)
            .SkipResources()
            .SetUseIsolating(false)
            .UseConcurrent()
            .UncheckedBuild();

        AddLanguageFiles(ctx, bundle, new CultureInfo(FallbackCultureSub));
        AddLanguageFiles(ctx, bundle, culture);
        _bundle = bundle;
        Culture = culture;
    }

    public static string Get(string key) => _bundle?.GetMessage(key) ?? key;

    public static string Get(string key, params (string, object?)[] args)
    {
        var dict = new Dictionary<string, IFluentType>(args.Length);
        foreach (var (k, v) in args)
            dict[k] = ToFluent(v);
        return _bundle?.GetMessage(key, args: dict) ?? key;
    }

    /// <summary>Display name of a language in itself, as the launcher's language drop-down shows it.</summary>
    public static string NativeName(string name)
    {
        var c = new CultureInfo(name);
        var n = c.NativeName;
        return n.Length > 0 ? char.ToUpper(n[0], c) + n[1..] : name;
    }

    private static IFluentType ToFluent(object? o) => o switch
    {
        string s => new FluentString(s),
        float f => (FluentNumber)f,
        double d => (FluentNumber)d,
        int i => (FluentNumber)i,
        long l => (FluentNumber)l,
        null => FluentNone.None,
        _ => new FluentString(o.ToString()),
    };

    private static void AddLanguageFiles(Context ctx, FluentBundle bundle, CultureInfo culture)
    {
        if (!culture.Parent.Equals(CultureInfo.InvariantCulture))
            AddLanguageFiles(ctx, bundle, culture.Parent);

        var dirs = new List<string> { culture.Name };
        // Weblate stores secondary language codes (like zh-Hans) with an underscore.
        if (culture.Name.Contains('-'))
            dirs.Add(culture.Name.Replace('-', '_'));

        foreach (var dir in dirs)
        {
            string[] files;
            try
            {
                files = ctx.Assets!.List("Locale/" + dir) ?? Array.Empty<string>();
            }
            catch
            {
                continue;
            }

            foreach (var file in files.Where(f => f.EndsWith(".ftl")))
            {
                using var stream = ctx.Assets!.Open($"Locale/{dir}/{file}");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var resource = LinguiniParser.FromTextReader(reader, file).Parse();
                bundle.AddResourceOverriding(resource);
            }
        }
    }

    private static CultureInfo? Match(CultureInfo culture)
    {
        while (!culture.Equals(CultureInfo.InvariantCulture))
        {
            if (AvailableLanguages.Contains(culture.Name))
                return culture;
            culture = culture.Parent;
        }

        return null;
    }
}

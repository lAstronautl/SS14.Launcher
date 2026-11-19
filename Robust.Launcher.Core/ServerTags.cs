using System.Text.RegularExpressions;

namespace Robust.Launcher.Core;

/// <summary>
/// Hub server tags, as in the desktop launcher: <c>ServerApi.Tags</c> and <c>ServerTagInfer</c>
/// (SS14.Launcher/Api/ServerApi.cs, SS14.Launcher/Utility/ServerTagInfer.cs).
/// </summary>
public static partial class ServerTags
{
    // @formatter:off
    public const string TagEighteenPlus = "18+";
    public const string TagRegion       = "region:";
    public const string TagLanguage     = "lang:";
    public const string TagRolePlay     = "rp:";
    public const string TagNoTagInfer   = "no_tag_infer";
    public const string TagWhitelist    = "whitelist";

    public const string RolePlayNone   = "none";
    public const string RolePlayLow    = "low";
    public const string RolePlayMedium = "med";
    public const string RolePlayHigh   = "high";
    // @formatter:on

    /// <summary>Region tag value → localization key (ServerListFiltersViewModel.Tables.cs).</summary>
    public static readonly IReadOnlyDictionary<string, string> RegionNames = new Dictionary<string, string>
    {
        ["af_c"] = "region-africa-central", ["af_n"] = "region-africa-north", ["af_s"] = "region-africa-south",
        ["ata"] = "region-antarctica", ["as_e"] = "region-asia-east", ["as_n"] = "region-asia-north",
        ["as_se"] = "region-asia-south-east", ["am_c"] = "region-central-america", ["eu_e"] = "region-europe-east",
        ["eu_w"] = "region-europe-west", ["grl"] = "region-greenland", ["ind"] = "region-india",
        ["me"] = "region-middle-east", ["luna"] = "region-the-moon", ["am_n_c"] = "region-north-america-central",
        ["am_n_e"] = "region-north-america-east", ["am_n_w"] = "region-north-america-west", ["oce"] = "region-oceania",
        ["am_s_e"] = "region-south-america-east", ["am_s_s"] = "region-south-america-south",
        ["am_s_w"] = "region-south-america-west",
    };

    public static readonly string[] RolePlayOrder = { RolePlayNone, RolePlayLow, RolePlayMedium, RolePlayHigh };

    public static readonly IReadOnlyDictionary<string, string> RolePlayNames = new Dictionary<string, string>
    {
        [RolePlayNone] = "filters-rp-none", [RolePlayLow] = "filters-rp-low",
        [RolePlayMedium] = "filters-rp-medium", [RolePlayHigh] = "filters-rp-high",
    };

    public static bool TryTagPrefix(string tag, string prefix, out string value)
    {
        if (!tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = "";
            return false;
        }

        value = tag[prefix.Length..];
        return true;
    }

    public static string PrimaryLanguageTag(string fullTag)
    {
        var idx = fullTag.IndexOf('-');
        return idx == -1 ? fullTag : fullTag[..idx];
    }

    /// <summary>Server tags plus the ones inferred from "[TAGS]" in its name.</summary>
    public static string[] AllTags(ServerStatus status, string[]? hubInferred = null)
    {
        var tags = new List<string>(status.Tags ?? Array.Empty<string>());
        if (hubInferred != null)
            tags.AddRange(hubInferred);
        tags.AddRange(InferTags(status));
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IEnumerable<string> InferTags(ServerStatus status)
    {
        var currentTags = status.Tags ?? Array.Empty<string>();
        var currentName = status.Name ?? "";
        var addedTags = new List<string>();

        if (currentTags.Contains(TagNoTagInfer))
            return addedTags;

        var tagLikes = TagLikeRegex()
            .Matches(currentName)
            .Select(x => x.Groups[1].Captures[0].Value)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        if (!currentTags.Any(t => t.StartsWith(TagLanguage, StringComparison.OrdinalIgnoreCase)))
        {
            if (tagLikes.Contains("en"))
                addedTags.Add(TagLanguage + "en");
            else if (tagLikes.Contains("ru") || tagLikes.Contains("rus"))
                addedTags.Add(TagLanguage + "ru");
        }

        if (!currentTags.Contains(TagEighteenPlus))
        {
            if (tagLikes.Contains("18+") || tagLikes.Contains("+18") || tagLikes.Contains("18") || tagLikes.Contains("ERP"))
                addedTags.Add(TagEighteenPlus);
        }

        if (!currentTags.Contains(TagWhitelist))
        {
            if (tagLikes.Contains("wl") || tagLikes.Contains("whitelist") || tagLikes.Contains("whitelisted"))
                addedTags.Add(TagWhitelist);
        }

        if (!currentTags.Any(t => t.StartsWith(TagRolePlay, StringComparison.OrdinalIgnoreCase)))
        {
            if (tagLikes.Contains("nrp"))
                addedTags.Add(TagRolePlay + RolePlayNone);
            else if (tagLikes.Contains("lrp"))
                addedTags.Add(TagRolePlay + RolePlayLow);
            else if (tagLikes.Contains("mrp"))
                addedTags.Add(TagRolePlay + RolePlayMedium);
            else if (tagLikes.Contains("hrp"))
                addedTags.Add(TagRolePlay + RolePlayHigh);
        }

        return addedTags;
    }

    [GeneratedRegex("\\[(.*?)\\]")]
    private static partial Regex TagLikeRegex();
}

/// <summary>Filter categories, as ServerFilterCategory in the desktop launcher.</summary>
public enum ServerFilterCategory : byte
{
    Language = 1,
    Region = 2,
    RolePlay = 3,
    EighteenPlus = 4,
    PlayerCount = 5,
    Hub = 6,
    Whitelist = 7,
}

/// <summary>
/// The desktop launcher's server filters (ServerListFiltersViewModel.ApplyFilters): within a category any checked
/// value matches ("unspecified" matches servers without that tag); yes/no categories need exactly one answer.
/// </summary>
public sealed class ServerFilterSet
{
    public const string DataUnspecified = "unspecified";
    public const string DataTrue = "true";
    public const string DataFalse = "false";
    public const string HideEmpty = "hide_empty";
    public const string HideFull = "hide_full";
    public const string Min = "min";
    public const string Max = "max";

    /// <summary>Enabled filters as "category:data" (persisted).</summary>
    public HashSet<string> Enabled { get; set; } = new();
    public int MinPlayers { get; set; }
    public int MaxPlayers { get; set; } = 100;

    public static string Key(ServerFilterCategory c, string data) => $"{(int)c}:{data}";

    public bool Get(ServerFilterCategory c, string data) => Enabled.Contains(Key(c, data));

    public void Set(ServerFilterCategory c, string data, bool on)
    {
        if (on)
            Enabled.Add(Key(c, data));
        else
            Enabled.Remove(Key(c, data));
    }

    /// <param name="visible">Values present in the current list per category (stale saved filters are ignored).</param>
    public bool Matches(string[] tags, int players, int softMax,
        IReadOnlyDictionary<ServerFilterCategory, IReadOnlyCollection<string>> visible)
    {
        if (YesNo(ServerFilterCategory.EighteenPlus) is { } adult && adult != tags.Contains(ServerTags.TagEighteenPlus))
            return false;
        if (YesNo(ServerFilterCategory.Whitelist) is { } wl && wl != tags.Contains(ServerTags.TagWhitelist))
            return false;

        if (!Category(ServerFilterCategory.Language, ServerTags.TagLanguage, ServerTags.PrimaryLanguageTag))
            return false;
        if (!Category(ServerFilterCategory.Region, ServerTags.TagRegion, null))
            return false;
        if (!Category(ServerFilterCategory.RolePlay, ServerTags.TagRolePlay, null))
            return false;

        if (Get(ServerFilterCategory.PlayerCount, HideEmpty) && players == 0)
            return false;
        if (Get(ServerFilterCategory.PlayerCount, HideFull) && softMax != 0 && players >= softMax)
            return false;
        if (Get(ServerFilterCategory.PlayerCount, Min) && players < MinPlayers)
            return false;
        if (Get(ServerFilterCategory.PlayerCount, Max) && players > MaxPlayers)
            return false;
        return true;

        bool? YesNo(ServerFilterCategory c)
        {
            var yes = Get(c, DataTrue);
            var no = Get(c, DataFalse);
            return yes == no ? null : yes;
        }

        bool Category(ServerFilterCategory c, string prefix, Func<string, string>? transform)
        {
            if (!visible.TryGetValue(c, out var values))
                return true;
            var set = values.Where(v => Get(c, v)).ToHashSet();
            if (set.Count == 0)
                return true;

            var unspecified = true;
            foreach (var tag in tags)
            {
                if (!ServerTags.TryTagPrefix(tag, prefix, out var value))
                    continue;
                if (transform != null)
                    value = transform(value);
                unspecified = false;
                if (set.Contains(value))
                    return true;
            }

            return unspecified && set.Contains(DataUnspecified);
        }
    }
}

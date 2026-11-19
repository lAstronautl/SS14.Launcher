using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Toolkit.Mvvm.ComponentModel;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Utility;

namespace SS14.Launcher.ViewModels.MainWindowTabs;

/// <summary>
/// AstroLauncher: every launcher setting that the original launcher only stores in settings.db or hardcodes.
/// </summary>
public sealed partial class DevelopmentTabViewModel
{
    // Ids from MainWindowViewModel.Warnings.cs, with short names for the toggles.
    private static readonly (string Id, string TitleKey)[] KnownWarnings =
    [
        ("EarlyAccess", "tab-development-astro-warning-early-access"),
        ("Epilepsy", "tab-development-astro-warning-epilepsy"),
        ("IntelDegradation", "tab-development-astro-warning-intel"),
        ("Rosetta", "tab-development-astro-warning-rosetta"),
        ("BackgroundFetch", "tab-development-astro-warning-background-fetch"),
    ];

    private IReadOnlyList<AstroWarningToggleViewModel>? _warnings;

    // ---- Storage ----

    public int MaxVersionsToKeep
    {
        get => _cfg.GetCVar(CVars.MaxVersionsToKeep);
        set => SetInt(CVars.MaxVersionsToKeep, value, 1, 500);
    }

    public int MaxForkVersionsToKeep
    {
        get => _cfg.GetCVar(CVars.MaxForkVersionsToKeep);
        set => SetInt(CVars.MaxForkVersionsToKeep, value, 1, 100);
    }

    public int InterruptibleDownloadKeepHours
    {
        get => _cfg.GetCVar(CVars.InterruptibleDownloadKeepHours);
        set => SetInt(CVars.InterruptibleDownloadKeepHours, value, 0, 24 * 365);
    }

    // ---- Network ----

    public int ServerStatusTimeoutSeconds
    {
        get => ConfigConstants.AstroCVar(CVars.ServerStatusTimeoutSeconds, ConfigConstants.ServerStatusTimeoutRange);
        set => SetInt(CVars.ServerStatusTimeoutSeconds, value, ConfigConstants.ServerStatusTimeoutRange);
    }

    public int RobustManifestCacheMinutes
    {
        get => ConfigConstants.AstroCVar(CVars.RobustManifestCacheMinutes, ConfigConstants.RobustManifestCacheRange);
        set => SetInt(CVars.RobustManifestCacheMinutes, value, ConfigConstants.RobustManifestCacheRange);
    }

    public bool CheckLauncherUpdates
    {
        get => _cfg.GetCVar(CVars.CheckLauncherUpdates);
        set => SetBool(CVars.CheckLauncherUpdates, value);
    }

    // ---- Accounts ----

    public int TokenRefreshThresholdDays
    {
        get => ConfigConstants.AstroCVar(CVars.TokenRefreshThresholdDays, ConfigConstants.TokenRefreshThresholdRange);
        set => SetInt(CVars.TokenRefreshThresholdDays, value, ConfigConstants.TokenRefreshThresholdRange);
    }

    public int TokenRefreshIntervalDays
    {
        get => ConfigConstants.AstroCVar(CVars.TokenRefreshIntervalDays, ConfigConstants.TokenRefreshIntervalRange);
        set => SetInt(CVars.TokenRefreshIntervalDays, value, ConfigConstants.TokenRefreshIntervalRange);
    }

    // ---- Game performance ----

    public bool ForceServerGC
    {
        get => _cfg.GetCVar(CVars.ForceServerGC);
        set => SetBool(CVars.ForceServerGC, value);
    }

    public int LoaderContentPoolSize
    {
        get => ConfigConstants.AstroCVar(CVars.LoaderContentPoolSize, ConfigConstants.LoaderContentPoolRange);
        set => SetInt(CVars.LoaderContentPoolSize, value, ConfigConstants.LoaderContentPoolRange);
    }

    // ---- Warnings ----

    public IReadOnlyList<AstroWarningToggleViewModel> Warnings => _warnings ??= KnownWarnings
        .Select(w => new AstroWarningToggleViewModel(this, w.Id, _loc.GetString(w.TitleKey)))
        .ToList();

    public bool WineWarningShown
    {
        get => _cfg.GetCVar(CVars.WineWarningShown);
        set => SetBool(CVars.WineWarningShown, value);
    }

    internal bool IsWarningHidden(string id)
        => GetDismissed().Contains(id);

    internal void SetWarningHidden(string id, bool hidden)
    {
        var dismissed = GetDismissed();
        if (hidden)
            dismissed.Add(id);
        else
            dismissed.Remove(id);

        _cfg.SetCVar(CVars.DismissedWarnings, string.Join(',', dismissed));
        _cfg.CommitConfig();
    }

    private List<string> GetDismissed()
        => _cfg.GetCVar(CVars.DismissedWarnings)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Distinct()
            .ToList();

    public void HideAllWarnings() => SetAllWarnings(true);

    public void ShowAllWarnings() => SetAllWarnings(false);

    private void SetAllWarnings(bool hidden)
    {
        foreach (var warning in Warnings)
        {
            warning.Hidden = hidden;
        }

        WineWarningShown = hidden;
        OnPropertyChanged(nameof(WineWarningShown));
    }

    // ---- Data folder ----

    public string DataDirectory => LauncherPaths.DirUserData;

    public string ProfileName => System.IO.Path.GetFileName(LauncherPaths.DirUserData.TrimEnd('/', '\\'));

    public void OpenDataDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirUserData
        });
    }

    public void ResetAll()
    {
        _cfg.SetCVar(CVars.MaxVersionsToKeep, CVars.MaxVersionsToKeep.DefaultValue);
        _cfg.SetCVar(CVars.MaxForkVersionsToKeep, CVars.MaxForkVersionsToKeep.DefaultValue);
        _cfg.SetCVar(CVars.InterruptibleDownloadKeepHours, CVars.InterruptibleDownloadKeepHours.DefaultValue);
        _cfg.SetCVar(CVars.ServerStatusTimeoutSeconds, CVars.ServerStatusTimeoutSeconds.DefaultValue);
        _cfg.SetCVar(CVars.RobustManifestCacheMinutes, CVars.RobustManifestCacheMinutes.DefaultValue);
        _cfg.SetCVar(CVars.CheckLauncherUpdates, CVars.CheckLauncherUpdates.DefaultValue);
        _cfg.SetCVar(CVars.TokenRefreshThresholdDays, CVars.TokenRefreshThresholdDays.DefaultValue);
        _cfg.SetCVar(CVars.TokenRefreshIntervalDays, CVars.TokenRefreshIntervalDays.DefaultValue);
        _cfg.SetCVar(CVars.ForceServerGC, CVars.ForceServerGC.DefaultValue);
        _cfg.SetCVar(CVars.LoaderContentPoolSize, CVars.LoaderContentPoolSize.DefaultValue);
        _cfg.CommitConfig();

        // Refresh every binding on the tab.
        OnPropertyChanged(string.Empty);
    }

    // ---- Helpers ----

    private void SetInt(CVarDef<int> def, int value, (int Min, int Max) range)
        => SetInt(def, value, range.Min, range.Max);

    private void SetInt(CVarDef<int> def, int value, int min, int max)
    {
        _cfg.SetCVar(def, Math.Clamp(value, min, max));
        _cfg.CommitConfig();
        OnPropertyChanged(def.Name);
    }

    private void SetBool(CVarDef<bool> def, bool value)
    {
        _cfg.SetCVar(def, value);
        _cfg.CommitConfig();
    }
}

public sealed class AstroWarningToggleViewModel(DevelopmentTabViewModel parent, string id, string title) : ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; } = title;

    public bool Hidden
    {
        get => parent.IsWarningHidden(Id);
        set
        {
            if (Hidden == value)
                return;

            parent.SetWarningHidden(Id, value);
            OnPropertyChanged();
        }
    }
}

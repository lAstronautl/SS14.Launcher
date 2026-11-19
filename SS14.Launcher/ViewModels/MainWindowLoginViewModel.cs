using System;
using System.Diagnostics;
using SS14.Launcher.Localization;
using Splat;
using SS14.Launcher.Api;
using SS14.Launcher.Models.Data;
using SS14.Launcher.Models.Logins;
using SS14.Launcher.Utility;
using SS14.Launcher.ViewModels.Login;

namespace SS14.Launcher.ViewModels;

public class MainWindowLoginViewModel : ViewModelBase
{
    private readonly DataManager _cfg;
    private readonly AuthApi _authApi;
    private readonly LoginManager _loginMgr;
    private BaseLoginViewModel _screen;

    public LanguageSelectorViewModel LanguageSelector { get; } = new();

    public BaseLoginViewModel Screen
    {
        get => _screen;
        set
        {
            if (_screen == value)
                return;

            OnPropertyChanging();
            _screen = value;
            OnPropertyChanged();

            value.Activated();
        }
    }

    public MainWindowLoginViewModel()
    {
        _cfg = Locator.Current.GetRequiredService<DataManager>();
        _authApi = Locator.Current.GetRequiredService<AuthApi>();
        _loginMgr = Locator.Current.GetRequiredService<LoginManager>();

        _screen = default!;
        SwitchToLogin();

        _loginMgr.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LoginManager.ActiveAccount))
            {
                OnPropertyChanged(nameof(ReturnAccount));
                OnPropertyChanged(nameof(CanReturnToAccount));
                OnPropertyChanged(nameof(ReturnToAccountText));
            }
        };
    }

    /// <summary>
    /// Set by <see cref="MainWindowViewModel"/>, performs a full account switch (including status checks).
    /// </summary>
    public Action<LoggedInAccount>? SwitchAccountCallback { get; set; }

    /// <summary>
    /// The account to go back to when the user opened "Add account" and changed their mind.
    /// </summary>
    public LoggedInAccount? ReturnAccount => _loginMgr.ActiveAccount == null ? _loginMgr.PreviousAccount : null;

    public bool CanReturnToAccount => ReturnAccount != null;

    public string ReturnToAccountText => LocalizationManager.Instance.GetString(
        "login-return-to-account", ("name", ReturnAccount?.Username ?? ""));

    public void ReturnToAccount()
    {
        if (ReturnAccount is { } account)
            SwitchAccountCallback?.Invoke(account);
    }

    public void SwitchToLogin()
    {
        Screen = new LoginViewModel(this, _authApi, _loginMgr, _cfg);
    }

    public void SwitchToExpiredLogin(LoggedInAccount account)
    {
        Screen = new ExpiredLoginViewModel(this, _cfg, _authApi, _loginMgr, account);
    }

    public void SwitchToAuthTfa(AuthApi.AuthenticateRequest request)
    {
        Screen = new AuthTfaViewModel(this, request, _loginMgr, _authApi, _cfg);
    }

    public void OpenLogDirectory()
    {
        Process.Start(new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = LauncherPaths.DirLogs
        });
    }
}

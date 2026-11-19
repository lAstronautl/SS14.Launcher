using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Robust.Launcher.Core;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Launcher screen. Layout and look follow the official SS14 launcher: stripeback header with the title and
/// account button, angled tabs (Главная / Серверы / Игра / Настройки), server list with alternating rows.
/// </summary>
[Activity(Label = "@string/app_name", MainLauncher = true, Exported = true, Theme = "@style/LauncherTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : Activity
{
    private enum Tab { Home, Servers, News, Settings, GameOptions }

    private const string AccountSiteUrl = "https://account.spacestation14.com/";

    private static readonly HttpClient Http = CreateHttp();

    private LauncherSettings _settings = null!;
    private FrameLayout _content = null!;
    private TextView _accountButton = null!;
    private string _gameOptionsTab = GameSettings.TabGraphics;
    private readonly Dictionary<string, string> _serverDetails = new();
    private List<(string Title, string Link)>? _news;
    private readonly Dictionary<Tab, TextView> _tabButtons = new();
    private Tab _tab = Tab.Home;

    private List<HubServerEntry> _hubServers = new();
    private string _filter = "";
    private bool _hideEmpty;
    private CancellationTokenSource? _connectCts;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RobustAndroidLauncher/0.2");
        return http;
    }

    private string FilesPath => FilesDir!.AbsolutePath;
    private string CachePath => (GetExternalFilesDir(null) ?? FilesDir!).AbsolutePath;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = LauncherSettings.Load(FilesPath);
        CleanupOldRuntimeLocation();
        Window?.SetStatusBarColor(Ui.ListRowA);
        Window?.SetNavigationBarColor(Ui.Bg);

        var root = Ui.Column(this);
        root.SetBackgroundColor(Ui.Bg);

        // --- Header, as in the official launcher: logo on the left; Discord / website buttons and the account
        // drop-down on the right; a thin gold line below.
        var header = Ui.Row(this);
        header.SetBackgroundColor(Ui.Bg);
        header.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 6), Ui.Dp(this, 8), Ui.Dp(this, 6));
        header.AddView(Logo(), Ui.Fill(1));
        var right = Ui.Column(this);
        var links = Ui.Row(this);
        links.AddView(SmallButton("Discord", () => OpenUrl(_settings.DiscordUrl)), Ui.Wrap());
        links.AddView(SmallButton("Веб-сайт", () => OpenUrl(_settings.WebsiteUrl)), Ui.Wrap(4, this));
        right.AddView(links);
        _accountButton = Ui.Label(this, "", 14);
        _accountButton.Background = Ui.AngleStates(this, Ui.ControlMid, Ui.Hovered, 8);
        _accountButton.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 4), Ui.Dp(this, 12), Ui.Dp(this, 4));
        _accountButton.Clickable = true;
        _accountButton.Click += (_, _) => ShowAccountMenu();
        right.AddView(_accountButton, Ui.Fill(0, 3, this));
        header.AddView(right, Ui.Wrap(8, this));
        root.AddView(header);
        var gold = new View(this);
        gold.SetBackgroundColor(Ui.NanoGold);
        root.AddView(gold, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 2)));
        UpdateAccountButton();

        _content = new FrameLayout(this);
        root.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        // --- Bottom tab bar on the stripeback, selected tab green, version on the right.
        var tabs = Ui.Row(this);
        tabs.Background = new Ui.StripeDrawable(Ui.Dp(this, 22));
        tabs.SetPadding(0, Ui.Dp(this, 3), Ui.Dp(this, 10), Ui.Dp(this, 3));
        foreach (var (tab, text) in new[]
                 {
                     (Tab.Home, "Главная"), (Tab.Servers, "Серверы"), (Tab.News, "Новости"), (Tab.Settings, "Настройки"),
                 })
        {
            var t = Ui.Label(this, text, 15);
            t.Gravity = GravityFlags.Center;
            t.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 9), Ui.Dp(this, 10), Ui.Dp(this, 9));
            t.Clickable = true;
            t.Click += (_, _) => ShowTab(tab);
            tabs.AddView(t, Ui.Wrap());
            _tabButtons[tab] = t;
        }

        tabs.AddView(new View(this), Ui.Fill(1));
        tabs.AddView(Ui.Label(this, "v" + PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName, 13, Ui.Muted),
            Ui.Wrap());
        root.AddView(tabs);
        SetContentView(root);

        ShowTab(_settings.Favorites.Count == 0 ? Tab.Servers : Tab.Home);
        ShowLastCrashIfAny();
    }

    protected override void OnResume()
    {
        base.OnResume();
        CheckUnexpectedGameExit();
        ShowLastCrashIfAny();
    }

    /// <summary>0.1.0 stored .NET runtimes on external storage, where native code cannot be loaded.</summary>
    private void CleanupOldRuntimeLocation()
    {
        try
        {
            var old = System.IO.Path.Combine(CachePath, "runtimes");
            if (CachePath != FilesPath && Directory.Exists(old))
                Directory.Delete(old, true);
        }
        catch
        {
            // Not important.
        }
    }

    private void UpdateAccountButton()
    {
        _accountButton.Text = (_settings.Login is { } login ? login.Username : "Гость") + "  ▾";
    }

    private void ShowTab(Tab tab)
    {
        _tab = tab;
        var shown = tab == Tab.GameOptions ? Tab.Settings : tab;
        foreach (var (t, view) in _tabButtons)
        {
            if (t == shown)
                view.SetBackgroundColor(Ui.ControlHigh);
            else
                view.Background = null;
            view.SetTextColor(Ui.Text);
        }

        _content.RemoveAllViews();
        _content.AddView(tab switch
        {
            Tab.Home => BuildHomeTab(),
            Tab.Servers => BuildServersTab(),
            Tab.News => BuildNewsTab(),
            Tab.GameOptions => BuildGameOptionsPage(),
            _ => BuildSettingsTab(),
        });
    }

    private ServerAdapter NewServerAdapter() => new(this, item =>
    {
        if (ServerAddress.TryParse(item.Address, out var addr))
            Connect(addr, item.Name);
    }, ServerDetails);

    private ListView MakeServerList(ServerAdapter adapter)
    {
        var list = new ListView(this) { DividerHeight = 0 };
        list.SetBackgroundColor(Ui.ListRowA);
        list.Adapter = adapter;
        list.ItemClick += (_, e) => adapter.Toggle(e.Position);
        return list;
    }

    /// <summary>Column titles above the server list, like the launcher's grid header.</summary>
    private View ServerListHeader()
    {
        var row = Ui.Row(this);
        row.SetBackgroundColor(Ui.Bg);
        row.SetPadding(Ui.Dp(this, 22), Ui.Dp(this, 4), Ui.Dp(this, 6), Ui.Dp(this, 4));
        row.AddView(Ui.Label(this, "Название сервера", 14, Ui.Muted), Ui.Fill(1));
        var time = Ui.Label(this, "Время", 14, Ui.Muted);
        time.Gravity = GravityFlags.End;
        row.AddView(time, ServerAdapter.Column(this, ServerAdapter.TimeWidth(this)));
        var players = Ui.Label(this, "Игроки", 14, Ui.Muted);
        players.Gravity = GravityFlags.End;
        row.AddView(players, ServerAdapter.Column(this, ServerAdapter.PlayersWidth(this)));
        row.AddView(new View(this), ServerAdapter.Column(this, ServerAdapter.ButtonWidth(this) + 6));
        return row;
    }

    /// <summary>Expanded part of a server row: description, engine support, favorite toggle.</summary>
    private View ServerDetails(ServerItem item)
    {
        var col = Ui.Column(this);
        var info = Ui.Label(this, _serverDetails.GetValueOrDefault(item.Address, "Получение информации о сервере…"), 14);
        col.AddView(info);
        col.AddView(Ui.Label(this, item.Address, 12, Ui.Disabled), Ui.Fill(0, 4, this));
        var isFav = _settings.Favorites.Any(f => f.Address == item.Address);
        var fav = Ui.Button(this, isFav ? "★ Убрать из избранного" : "☆ В избранное", () =>
        {
            if (isFav)
                RemoveFavorite(item.Address);
            else
                AddFavorite(item.Address, item.Name);
            if (_tab == Tab.Servers)
                ShowTab(Tab.Servers);
        });
        fav.Focusable = false;
        col.AddView(fav, Ui.Wrap());

        if (!_serverDetails.ContainsKey(item.Address))
        {
            _serverDetails[item.Address] = "Получение информации о сервере…";
            _ = Task.Run(async () =>
            {
                var text = await DescribeServerAsync(item);
                RunOnUiThread(() =>
                {
                    _serverDetails[item.Address] = text;
                    info.Text = text;
                });
            });
        }

        return col;
    }

    private async Task<string> DescribeServerAsync(ServerItem item)
    {
        try
        {
            var addr = ServerAddress.Parse(item.Address);
            var api = new ServerApi(Http);
            var srvInfo = await api.GetInfoAsync(addr);
            var ver = srvInfo.Build?.EngineVersion ?? "?";
            var engines = new EngineManager(Http, CachePath, _settings.EngineReleaseBase);
            var availability = ver == "?" ? EngineAvailability.None : await engines.CheckAvailabilityAsync(ver);
            var engineText = availability switch
            {
                EngineAvailability.Patched => "Android-сборка",
                EngineAvailability.Official => "режим совместимости",
                _ => "не поддерживается",
            };
            return (string.IsNullOrWhiteSpace(srvInfo.Description) ? "" : srvInfo.Description.Trim() + "\n\n") +
                   $"Движок: {ver} ({engineText}). Вход: {AuthText(srvInfo.Auth?.Mode)}.";
        }
        catch (Exception e)
        {
            return $"Не удалось связаться с сервером: {e.Message}";
        }
    }

    private static ServerItem ToItem(string address, ServerStatus? st, string? fallbackName = null)
    {
        DateTime? start = null;
        if (st?.RoundStartTime is { } rs && DateTime.TryParse(rs, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dt))
            start = dt;
        return new ServerItem(address, st?.Name ?? fallbackName ?? address, st?.Players ?? 0, st?.SoftMaxPlayers ?? 0,
            st?.RunLevel, start);
    }

    // ------------------------------------------------------------------ Home (favorites)

    private View BuildHomeTab()
    {
        var col = Ui.Column(this);

        var top = Ui.Row(this);
        top.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 4));
        top.AddView(Ui.Heading(this, "Избранные Серверы", 19), Ui.Fill(1));
        top.AddView(SmallButton("⟳", () => ShowTab(Tab.Home)), Ui.Wrap());
        top.AddView(SmallButton("+", AskAddFavorite), Ui.Wrap(4, this));
        col.AddView(top);

        if (_settings.Favorites.Count == 0)
        {
            var empty = Ui.Label(this,
                "Здесь будут ваши избранные серверы.\nОткройте сервер на вкладке «Серверы» и нажмите «В избранное».",
                15, Ui.Muted);
            empty.Gravity = GravityFlags.Center;
            empty.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 24), Ui.Dp(this, 16), Ui.Dp(this, 24));
            col.AddView(empty, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        }
        else
        {
            var adapter = NewServerAdapter();
            adapter.SetItems(_settings.Favorites.Select(f => new ServerItem(f.Address, f.Name, -1, 0)).ToList());
            col.AddView(MakeServerList(adapter), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

            // Refresh player counts in the background.
            _ = Task.Run(async () =>
            {
                var api = new ServerApi(Http);
                var items = new List<ServerItem>();
                foreach (var f in _settings.Favorites.ToList())
                {
                    try
                    {
                        items.Add(ToItem(f.Address, await api.GetStatusAsync(ServerAddress.Parse(f.Address)), f.Name));
                    }
                    catch
                    {
                        items.Add(new ServerItem(f.Address, f.Name, -2, 0));
                    }
                }

                RunOnUiThread(() => adapter.SetItems(items));
            });
        }

        var go = Ui.Button(this, "Перейти на вкладку серверов", () => ShowTab(Tab.Servers));
        var goLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.CenterHorizontal, TopMargin = Ui.Dp(this, 6),
        };
        col.AddView(go, goLp);

        var bottom = Ui.Row(this);
        bottom.SetBackgroundColor(Ui.ListRowA);
        bottom.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 6), Ui.Dp(this, 8), Ui.Dp(this, 6));
        bottom.AddView(Ui.Button(this, "Прямое подключение к серверу", AskDirectConnect), Ui.Wrap());
        col.AddView(bottom, Ui.Fill(0, 6, this));
        return col;
    }

    // ------------------------------------------------------------------ Servers (hub)

    private View BuildServersTab()
    {
        var col = Ui.Column(this);
        col.AddView(ServerListHeader());

        var adapter = NewServerAdapter();
        col.AddView(MakeServerList(adapter), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        var bottom = Ui.Row(this);
        bottom.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 6), Ui.Dp(this, 6), Ui.Dp(this, 6));
        var search = Ui.Input(this, "Поиск серверов...", _filter);
        bottom.AddView(search, Ui.Fill(1));
        var filters = Ui.Button(this, "Фильтры", () => { });
        bottom.AddView(filters, Ui.Wrap(6, this));
        bottom.AddView(Ui.Button(this, "⟳", () => _ = LoadHubAsync(true)), Ui.Wrap(4, this));
        col.AddView(bottom);

        void Apply()
        {
            var f = _filter.Trim();
            var items = _hubServers
                .Where(s => f.Length == 0
                            || (s.StatusData?.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                            || s.Address.Contains(f, StringComparison.OrdinalIgnoreCase))
                .Where(s => !_hideEmpty || (s.StatusData?.Players ?? 0) > 0)
                .OrderByDescending(s => s.StatusData?.Players ?? 0)
                .Select(s => ToItem(s.Address, s.StatusData))
                .ToList();
            adapter.SetItems(items);
            filters.Text = _hubServers.Count == 0 ? "Фильтры" : $"Фильтры ({items.Count} / {_hubServers.Count})";
        }

        filters.Click += (_, _) =>
        {
            var box = Ui.Column(this, 16);
            var hide = Ui.Check(this, "Скрыть пустые серверы", _hideEmpty);
            box.AddView(hide);
            Ui.Dialog(this, "Фильтры")
                .SetView(box)!
                .SetPositiveButton("Готово", (_, _) =>
                {
                    _hideEmpty = hide.Checked;
                    Apply();
                })!
                .Show();
        };
        search.TextChanged += (_, _) =>
        {
            _filter = search.Text ?? "";
            Apply();
        };

        _onHubLoaded = Apply;
        Apply();
        if (_hubServers.Count == 0)
            _ = LoadHubAsync(false);
        return col;
    }

    // ------------------------------------------------------------------ News

    private View BuildNewsTab()
    {
        var col = Ui.Column(this, 10);
        col.AddView(Ui.Heading(this, "Последние новости:", 19));
        var scroll = new ScrollView(this);
        var list = Ui.Column(this);
        scroll.AddView(list);
        col.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1) { TopMargin = Ui.Dp(this, 8) });

        void Fill()
        {
            list.RemoveAllViews();
            if (_news == null)
            {
                list.AddView(Ui.Label(this, "Загрузка новостей…", 15, Ui.Muted));
                return;
            }

            if (_news.Count == 0)
                list.AddView(Ui.Label(this, "Не удалось загрузить новости.", 15, Ui.Muted));
            foreach (var (title, link) in _news)
                list.AddView(Ui.Button(this, title, () => OpenUrl(link)), Ui.Fill(0, 4, this));
        }

        Fill();
        if (_news == null)
        {
            _ = Task.Run(async () =>
            {
                var news = new List<(string, string)>();
                try
                {
                    var xml = System.Xml.Linq.XDocument.Parse(await Http.GetStringAsync(_settings.NewsFeedUrl));
                    foreach (var item in xml.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
                    {
                        var title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
                        var linkEl = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
                        var link = linkEl?.Attribute("href")?.Value ?? linkEl?.Value;
                        if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(link))
                            news.Add((title.Trim(), link.Trim()));
                    }
                }
                catch (Exception e)
                {
                    global::Android.Util.Log.Warn("Launcher", $"News: {e.Message}");
                }

                RunOnUiThread(() =>
                {
                    _news = news;
                    if (_tab == Tab.News)
                        Fill();
                });
            });
        }

        return col;
    }

    // ------------------------------------------------------------------ Header helpers

    /// <summary>Text logo in the style of the launcher's: italic white title with a red number.</summary>
    private View Logo()
    {
        var row = Ui.Row(this);
        var narrow = ServerAdapter.Narrow(this);
        var name = Ui.Label(this, "PDA STATION ", narrow ? 16 : 22, Ui.Text, bold: true);
        name.SetTypeface(Ui.Bold(this), global::Android.Graphics.TypefaceStyle.BoldItalic);
        name.SetShadowLayer(0, Ui.Dp(this, 2), Ui.Dp(this, 2), global::Android.Graphics.Color.Black);
        row.AddView(name, Ui.Wrap());
        var num = Ui.Label(this, "14", narrow ? 22 : 30, global::Android.Graphics.Color.ParseColor("#E0262B"), bold: true);
        num.SetTypeface(Ui.Bold(this), global::Android.Graphics.TypefaceStyle.BoldItalic);
        row.AddView(num, Ui.Wrap());
        return row;
    }

    private Button SmallButton(string text, Action onClick)
    {
        var b = Ui.Button(this, text, onClick);
        b.SetTextSize(global::Android.Util.ComplexUnitType.Sp, ServerAdapter.Narrow(this) ? 12 : 14);
        b.SetPadding(Ui.Dp(this, 10), Ui.Dp(this, 2), Ui.Dp(this, 10), Ui.Dp(this, 2));
        b.SetMinHeight(Ui.Dp(this, 32));
        b.SetMinimumHeight(Ui.Dp(this, 32));
        b.SetMinWidth(0);
        b.SetMinimumWidth(Ui.Dp(this, 36));
        return b;
    }

    private void OpenUrl(string url)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
        }
        catch (Exception e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Short)!.Show();
        }
    }

    private void ShowAccountMenu()
    {
        var items = _settings.Login != null
            ? new[] { "Настройки аккаунта", "Сайт аккаунта", "Выйти" }
            : new[] { "Войти в аккаунт", "Сайт аккаунта" };
        Ui.Dialog(this, _settings.Login?.Username ?? "Гость")
            .SetItems(items, (_, e) =>
            {
                switch (items[e.Which])
                {
                    case "Выйти":
                        _ = LogoutAsync();
                        break;
                    case "Сайт аккаунта":
                        OpenAccountSite();
                        break;
                    default:
                        ShowTab(Tab.Settings);
                        break;
                }
            })!
            .Show();
    }

    private Action? _onHubLoaded;

    private async Task LoadHubAsync(bool force)
    {
        try
        {
            if (!force && _hubServers.Count > 0)
                return;
            _hubServers = await new ServerApi(Http).GetHubServersAsync(_settings.HubUrl);
        }
        catch (Exception e)
        {
            RunOnUiThread(() =>
                Toast.MakeText(this, $"Не удалось получить список серверов: {e.Message}", ToastLength.Long)!.Show());
        }

        RunOnUiThread(() =>
        {
            if (_tab == Tab.Servers)
                _onHubLoaded?.Invoke();
        });
    }

    private void AskDirectConnect()
    {
        var input = Ui.Input(this, "ss14://адрес:порт");
        var wrap = Ui.Column(this, 16);
        wrap.AddView(Ui.Label(this, "Введите адрес сервера для подключения:", 14, Ui.Muted));
        wrap.AddView(input, Ui.Fill(0, 8, this));
        Ui.Dialog(this, "Прямое подключение")
            .SetView(wrap)!
            .SetPositiveButton("Подключиться", (_, _) =>
            {
                var text = input.Text ?? "";
                if (ServerAddress.TryParse(text, out var addr))
                    Connect(addr, text);
                else
                    Toast.MakeText(this, "Неправильный адрес", ToastLength.Short)!.Show();
            })!
            .SetNegativeButton("Отмена", (_, _) => { })!
            .Show();
    }

    private void AskAddFavorite()
    {
        var name = Ui.Input(this, "Название");
        var address = Ui.Input(this, "ss14://адрес:порт");
        var wrap = Ui.Column(this, 16);
        wrap.AddView(name);
        wrap.AddView(address, Ui.Fill(0, 8, this));
        Ui.Dialog(this, "Добавить в избранное")
            .SetView(wrap)!
            .SetPositiveButton("Добавить", (_, _) =>
            {
                var text = address.Text ?? "";
                if (!ServerAddress.TryParse(text, out _))
                {
                    Toast.MakeText(this, "Неправильный адрес", ToastLength.Short)!.Show();
                    return;
                }

                AddFavorite(text, string.IsNullOrWhiteSpace(name.Text) ? text : name.Text!);
            })!
            .SetNegativeButton("Отмена", (_, _) => { })!
            .Show();
    }

    private static string AuthText(string? mode) => mode switch
    {
        "Required" => "только с аккаунтом",
        "Disabled" => "без аккаунтов",
        _ => "аккаунт или гость",
    };

    private void AddFavorite(string address, string name)
    {
        if (_settings.Favorites.All(f => f.Address != address))
        {
            _settings.Favorites.Add(new FavoriteServer { Address = address, Name = name });
            _settings.Save(FilesPath);
        }

        Toast.MakeText(this, "Добавлено в избранное", ToastLength.Short)!.Show();
        if (_tab == Tab.Home) ShowTab(Tab.Home);
    }

    private void RemoveFavorite(string address)
    {
        _settings.Favorites.RemoveAll(f => f.Address == address);
        _settings.Save(FilesPath);
        if (_tab == Tab.Home) ShowTab(Tab.Home);
    }

    // ------------------------------------------------------------------ Game settings

    /// <summary>Options section box: gold heading, then rows separated by thin lines.</summary>
    private LinearLayout Section(LinearLayout parent, string title)
    {
        parent.AddView(Ui.Heading(this, title), Ui.Fill(0, 18, this));
        var box = Ui.Column(this, 12);
        box.Background = Ui.Angle(this, Ui.ListRowA, 10);
        parent.AddView(box, Ui.Fill(0, 6, this));
        return box;
    }

    /// <summary>
    /// Game options as in the game's own options window: tab strip (Основные, Графика, Управление, Аудио,
    /// Доступность), gold group headings, checkboxes and drop-downs.
    /// </summary>
    private View BuildGameOptionsPage()
    {
        var page = Ui.Column(this);

        var top = Ui.Row(this);
        top.SetBackgroundColor(Ui.ListRowA);
        top.SetPadding(Ui.Dp(this, 6), Ui.Dp(this, 4), Ui.Dp(this, 10), Ui.Dp(this, 4));
        top.AddView(SmallButton("←", () => ShowTab(Tab.Settings)), Ui.Wrap());
        top.AddView(Ui.Heading(this, "Игровые настройки", 18), Ui.Wrap(10, this));
        page.AddView(top);

        var strip = Ui.Row(this);
        strip.SetBackgroundColor(Ui.ListRowA);
        foreach (var t in GameSettings.Tabs)
        {
            var l = Ui.Label(this, t, 15, t == _gameOptionsTab ? Ui.Text : Ui.Muted);
            l.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 6), Ui.Dp(this, 8), Ui.Dp(this, 6));
            if (t == _gameOptionsTab)
                l.SetBackgroundColor(Ui.ControlMid);
            l.Clickable = true;
            var tab = t;
            l.Click += (_, _) =>
            {
                _gameOptionsTab = tab;
                ShowTab(Tab.GameOptions);
            };
            strip.AddView(l, Ui.Wrap());
        }

        var stripScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        stripScroll.AddView(strip);
        page.AddView(stripScroll);

        var scroll = new ScrollView(this);
        var col = Ui.Column(this, 12);
        scroll.AddView(col);
        page.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        if (_gameOptionsTab == GameSettings.TabControls)
            BuildTouchOptions(col);

        foreach (var group in GameSettings.All.Where(o => o.Tab == _gameOptionsTab).GroupBy(o => o.Group))
        {
            col.AddView(Ui.Heading(this, group.Key, 16), Ui.Fill(0, col.ChildCount == 0 ? 0 : 12, this));
            foreach (var o in group)
                col.AddView(OptionView(o), Ui.Fill(0, 2, this));
        }

        col.AddView(Ui.Label(this,
            "Применяются при входе на любой сервер. Изменённые здесь параметры заменяют настройки из меню игры.",
            12, Ui.Muted), Ui.Fill(0, 16, this));
        col.AddView(Ui.Button(this, "Сбросить игровые настройки", () =>
        {
            _settings.GameCVars.Clear();
            _settings.Save(FilesPath);
            ShowTab(Tab.GameOptions);
        }), Ui.Fill(0, 10, this));
        return page;
    }

    private void BuildTouchOptions(LinearLayout col)
    {
        col.AddView(Ui.Heading(this, "Сенсорное управление", 16));
        var touchOn = Ui.Check(this, "Показывать джойстик и кнопки", _settings.TouchControls);
        touchOn.CheckedChange += (_, e) =>
        {
            _settings.TouchControls = e.IsChecked;
            _settings.Save(FilesPath);
        };
        col.AddView(touchOn);
        var opacityLabel = Ui.Label(this, $"Непрозрачность кнопок: {_settings.TouchOpacity}%", 15);
        col.AddView(opacityLabel, Ui.Fill(0, 8, this));
        var opacity = Ui.Slider(this, 100, _settings.TouchOpacity);
        opacity.ProgressChanged += (_, e) =>
        {
            _settings.TouchOpacity = Math.Max(15, e.Progress);
            opacityLabel.Text = $"Непрозрачность кнопок: {_settings.TouchOpacity}%";
            _settings.Save(FilesPath);
        };
        col.AddView(opacity);
        var sizeLabel = Ui.Label(this, $"Размер кнопок: {_settings.TouchScale:0.0}×", 15);
        col.AddView(sizeLabel, Ui.Fill(0, 8, this));
        var size = Ui.Slider(this, 10, (int)Math.Round((_settings.TouchScale - 0.5f) * 10));
        size.ProgressChanged += (_, e) =>
        {
            _settings.TouchScale = 0.5f + e.Progress / 10f;
            sizeLabel.Text = $"Размер кнопок: {_settings.TouchScale:0.0}×";
            _settings.Save(FilesPath);
        };
        col.AddView(size);
        col.AddView(Ui.Label(this,
            "Джойстик — W/A/S/D. Кнопки: Z — использовать, E — активировать, X — сменить руку, Q — бросить, " +
            "H — отпустить, I — инвентарь. Тап — ЛКМ, долгое нажатие — ПКМ. Физические клавиатура и мышь работают напрямую.",
            12, Ui.Muted), Ui.Fill(0, 6, this));
    }

    private View ChoiceRow(string title, string[] names, int selected, Action<int> changed)
    {
        var row = Ui.Row(this);
        row.SetPadding(0, Ui.Dp(this, 4), 0, Ui.Dp(this, 4));
        row.AddView(Ui.Label(this, title, 15), Ui.Fill(1));
        var spinner = new Spinner(this, SpinnerMode.Dropdown);
        var adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerItem, names);
        adapter.SetDropDownViewResource(global::Android.Resource.Layout.SimpleSpinnerDropDownItem);
        spinner.Adapter = adapter;
        spinner.SetSelection(selected);
        spinner.Background = Ui.Angle(this, Ui.ControlMid, 8);
        spinner.SetPopupBackgroundDrawable(Ui.Angle(this, Ui.PopupBg, 8));
        spinner.ItemSelected += (_, e) =>
        {
            if (e.View is TextView tv)
                tv.SetTextColor(Ui.Text);
            changed(e.Position);
        };
        row.AddView(spinner, Ui.Wrap(8, this));
        return row;
    }

    private View OptionView(GameSettings.Option o)
    {
        var col = Ui.Column(this);
        var value = GameSettings.Get(_settings, o);

        void Save(string v)
        {
            GameSettings.Set(_settings, o, v);
            _settings.Save(FilesPath);
        }

        switch (o.Kind)
        {
            case GameSettings.Kind.Toggle:
            {
                var c = Ui.Check(this, o.Title, value == o.On);
                c.CheckedChange += (_, e) => Save(e.IsChecked ? o.On : o.Off);
                col.AddView(c);
                break;
            }
            case GameSettings.Kind.Choice:
            {
                var choices = o.Choices!;
                var idx = Math.Max(0, Array.FindIndex(choices, c => c.Value == value));
                var labels = choices.Select(c => c.Value == "0" && o.CVar == GameSettings.UiScaleCVar
                    ? $"{c.Label} ({DefaultUiScale() * 100:0} %)"
                    : c.Label).ToArray();
                col.AddView(ChoiceRow(o.Title, labels, idx, i => Save(choices[i].Value)));
                break;
            }
            case GameSettings.Kind.Slider:
            {
                var steps = (int)Math.Round((o.Max - o.Min) / o.Step);
                string Show(float v) => o.Percent ? $"{v * 100:0}%" : GameSettings.Format(v);
                var current = Math.Clamp(GameSettings.ParseFloat(value), o.Min, o.Max);
                var label = Ui.Label(this, $"{o.Title}: {Show(current)}", 15);
                col.AddView(label);
                var slider = Ui.Slider(this, steps, (int)Math.Round((current - o.Min) / o.Step));
                slider.ProgressChanged += (_, e) =>
                {
                    var v = o.Min + e.Progress * o.Step;
                    label.Text = $"{o.Title}: {Show(v)}";
                    if (e.FromUser)
                        Save(GameSettings.Format(v));
                };
                col.AddView(slider);
                break;
            }
        }

        if (o.Description != null)
            col.AddView(Ui.Label(this, o.Description, 12, Ui.Muted));
        return col;
    }

    // ------------------------------------------------------------------ Settings (account + launcher)

    private View BuildSettingsTab()
    {
        var scroll = new ScrollView(this);
        var col = Ui.Column(this, 14);
        scroll.AddView(col);

        BuildAccountSection(Section(col, "Аккаунт"));

        var game = Section(col, "Игра");
        game.AddView(Ui.Label(this,
            "Графика, звук, управление и интерфейс — те же настройки, что в меню игры. Выставьте их до входа на сервер.",
            13, Ui.Muted));
        game.AddView(Ui.Button(this, "Игровые настройки", () => ShowTab(Tab.GameOptions), primary: true),
            Ui.Fill(0, 8, this));

        var links = Section(col, "Ссылки");
        void LinkInput(string hint, string value, Action<string> set)
        {
            links.AddView(Ui.Label(this, hint, 13, Ui.Muted), Ui.Fill(0, links.ChildCount == 0 ? 0 : 8, this));
            var input = Ui.Input(this, hint, value);
            input.TextChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(input.Text))
                {
                    set(input.Text!.Trim());
                    _settings.Save(FilesPath);
                }
            };
            links.AddView(input, Ui.Fill(0, 4, this));
        }

        LinkInput("Discord", _settings.DiscordUrl, v => _settings.DiscordUrl = v);
        LinkInput("Веб-сайт", _settings.WebsiteUrl, v => _settings.WebsiteUrl = v);
        LinkInput("Лента новостей (RSS)", _settings.NewsFeedUrl, v =>
        {
            _settings.NewsFeedUrl = v;
            _news = null;
        });

        var hub = Section(col, "Настройки хаба");
        hub.AddView(Ui.Label(this, "Сервер, с которого загружается список серверов.", 13, Ui.Muted));
        var hubUrl = Ui.Input(this, ServerApi.DefaultHubUrl, _settings.HubUrl);
        hubUrl.TextChanged += (_, _) =>
        {
            _settings.HubUrl = string.IsNullOrWhiteSpace(hubUrl.Text) ? ServerApi.DefaultHubUrl : hubUrl.Text!.Trim();
            _settings.Save(FilesPath);
        };
        hub.AddView(hubUrl, Ui.Fill(0, 6, this));

        var engine = Section(col, "Движок");
        engine.AddView(Ui.Label(this, "Откуда скачивать Android-сборки движка (релизы GitHub).", 13, Ui.Muted));
        var engineUrl = Ui.Input(this, EngineManager.DefaultReleaseBase, _settings.EngineReleaseBase);
        engineUrl.TextChanged += (_, _) =>
        {
            _settings.EngineReleaseBase = string.IsNullOrWhiteSpace(engineUrl.Text)
                ? EngineManager.DefaultReleaseBase
                : engineUrl.Text!.Trim();
            _settings.Save(FilesPath);
        };
        engine.AddView(engineUrl, Ui.Fill(0, 6, this));

        var aot = Section(col, "Предкомпиляция");
        aot.AddView(Ui.Label(this,
            "Заранее скомпилированный код движка: игра запускается намного быстрее. Скачивается один раз для каждой версии движка.",
            13, Ui.Muted));
        aot.AddView(ChoiceRow("При первом входе", new[] { "Спрашивать", "Всегда да", "Всегда нет" }, (int)_settings.AotMode,
            i =>
            {
                if ((int)_settings.AotMode == i)
                    return;
                _settings.AotMode = (AotMode)i;
                _settings.Save(FilesPath);
            }), Ui.Fill(0, 6, this));
        aot.AddView(Ui.Button(this, "Сбросить ответы по движкам", () =>
        {
            var n = _settings.AotDecisions.Count;
            _settings.AotDecisions.Clear();
            _settings.Save(FilesPath);
            Toast.MakeText(this, n > 0 ? $"Сброшено ответов: {n}. Лаунчер спросит снова." : "Ответов не было.",
                ToastLength.Short)!.Show();
        }), Ui.Fill(0, 8, this));

        var data = Section(col, "Данные");
        data.AddView(Ui.Button(this, "Удалить установленные движки", () =>
            ClearDirs("Движки удалены", System.IO.Path.Combine(CachePath, "engines"),
                System.IO.Path.Combine(FilesPath, "engines"),
                System.IO.Path.Combine(FilesPath, "runtimes"))));
        data.AddView(Ui.Button(this, "Удалить контент серверов", () =>
            ClearDirs("Контент удалён", System.IO.Path.Combine(CachePath, "content"))), Ui.Fill(0, 8, this));
        data.AddView(Ui.Button(this, "Лог последней игры", ShowGameLog), Ui.Fill(0, 8, this));

        var info = Ui.Label(this, $"Версия лаунчера: {PackageManager!.GetPackageInfo(PackageName!, 0)!.VersionName}", 12, Ui.Disabled);
        col.AddView(info, Ui.Fill(0, 18, this));
        return scroll;
    }

    private void ClearDirs(string done, params string[] dirs)
    {
        try
        {
            foreach (var p in dirs)
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
            }

            Toast.MakeText(this, done, ToastLength.Short)!.Show();
        }
        catch (Exception e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
        }
    }

    private void BuildAccountSection(LinearLayout box)
    {
        if (_settings.Login is { } login)
        {
            box.AddView(Ui.Label(this, "Вы вошли как", 13, Ui.Muted));
            box.AddView(Ui.Label(this, login.Username, 20, bold: true));
            box.AddView(Ui.Label(this, $"Вход действителен до {login.ExpireTime.LocalDateTime:g}", 12, Ui.Muted));
            var row = Ui.Row(this);
            row.AddView(Ui.Button(this, "Выйти", () => _ = LogoutAsync()), Ui.Fill(1));
            var lp = Ui.Fill(1);
            lp.LeftMargin = Ui.Dp(this, 8);
            row.AddView(Ui.Button(this, "Настройки аккаунта", OpenAccountSite), lp);
            box.AddView(row, Ui.Fill(0, 10, this));
            return;
        }

        box.AddView(Ui.Label(this, "Тот же аккаунт, что и в обычном лаунчере Space Station 14.", 13, Ui.Muted));
        var user = Ui.Input(this, "Имя пользователя или e-mail");
        var pass = Ui.Input(this, "Пароль", password: true);
        var tfa = Ui.Input(this, "Код аутентификации");
        tfa.Visibility = ViewStates.Gone;
        var error = Ui.Label(this, "", 14, Ui.Bad);
        box.AddView(user, Ui.Fill(0, 10, this));
        box.AddView(pass, Ui.Fill(0, 8, this));
        box.AddView(tfa, Ui.Fill(0, 8, this));
        box.AddView(error, Ui.Fill(0, 6, this));

        Button? loginBtn = null;
        loginBtn = Ui.Button(this, "Войти", async () =>
        {
            loginBtn!.Enabled = false;
            error.Text = "";
            try
            {
                var res = await new AuthApi(Http).LoginAsync(user.Text ?? "", pass.Text ?? "",
                    tfa.Visibility == ViewStates.Visible ? tfa.Text : null);
                if (res.Success)
                {
                    _settings.Login = res.Info;
                    _settings.Save(FilesPath);
                    UpdateAccountButton();
                    ShowTab(Tab.Settings);
                    return;
                }

                if (res.Code == AuthDenyCode.TfaRequired)
                {
                    tfa.Visibility = ViewStates.Visible;
                    error.Text = "Требуется двухфакторная аутентификация";
                }
                else
                {
                    error.Text = res.Code switch
                    {
                        AuthDenyCode.InvalidCredentials => "Неправильные данные для входа",
                        AuthDenyCode.TfaInvalid => "Неправильный код двухфакторной аутентификации",
                        AuthDenyCode.AccountUnconfirmed => "E-mail адрес аккаунта ещё не подтверждён",
                        AuthDenyCode.AccountLocked => "Аккаунт заблокирован",
                        _ => string.Join("\n", res.Errors),
                    };
                }
            }
            catch (Exception e)
            {
                error.Text = $"Ошибка: {e.Message}";
            }
            finally
            {
                loginBtn!.Enabled = true;
            }
        }, primary: true);
        box.AddView(loginBtn, Ui.Fill(0, 6, this));
        box.AddView(Ui.Button(this, "Регистрация / забыли пароль", OpenAccountSite), Ui.Fill(0, 8, this));

        box.AddView(Ui.Separator(this), Ui.Fill(0, 14, this));
        box.AddView(Ui.Label(this, "Или играйте гостем — на серверах, где вход не обязателен.", 13, Ui.Muted),
            Ui.Fill(0, 10, this));
        var guest = Ui.Input(this, "Имя гостя (необязательно)", _settings.GuestName);
        guest.TextChanged += (_, _) =>
        {
            _settings.GuestName = guest.Text ?? "";
            _settings.Save(FilesPath);
        };
        box.AddView(guest, Ui.Fill(0, 6, this));
    }

    private void OpenAccountSite()
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(AccountSiteUrl)));
        }
        catch (Exception e)
        {
            Toast.MakeText(this, e.Message, ToastLength.Short)!.Show();
        }
    }

    private async Task LogoutAsync()
    {
        if (_settings.Login is { } login)
        {
            try
            {
                await new AuthApi(Http).LogoutAsync(login.LoginToken);
            }
            catch
            {
                // Logging out locally is what matters.
            }
        }

        _settings.Login = null;
        _settings.Save(FilesPath);
        UpdateAccountButton();
        ShowTab(Tab.Settings);
    }

    /// <summary>Phones have small, dense screens: scale UI so the game is readable at ~720p logical height.</summary>
    private float DefaultUiScale()
    {
        var m = Resources!.DisplayMetrics!;
        var shortSide = Math.Min(m.WidthPixels, m.HeightPixels);
        return Math.Clamp(MathF.Round(shortSide / 720f * 4) / 4, 1f, 2.5f);
    }

    // ------------------------------------------------------------------ Connecting

    private void Connect(ServerAddress address, string name)
    {
        _connectCts?.Cancel();
        var cts = _connectCts = new CancellationTokenSource();

        var body = Ui.Column(this, 20);
        var stage = Ui.Label(this, "Подготовка…", 15);
        var bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            { Indeterminate = true, Max = 1000 };
        body.AddView(stage);
        body.AddView(bar, Ui.Fill(0, 12, this));

        var dialog = new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
            .SetTitle($"Подключение: {name}")!
            .SetView(body)!
            .SetCancelable(false)!
            .SetNegativeButton("Отмена", (_, _) => cts.Cancel())!
            .Show();

        var progress = new Progress<DownloadProgress>(p =>
        {
            stage.Text = p.Total > 0 ? $"{p.Stage}: {FormatAmount(p)}" : p.Stage;
            bar.Indeterminate = p.Total <= 0;
            if (p.Total > 0) bar.Progress = (int)(p.Done * 1000 / p.Total);
        });

        var defaultScale = DefaultUiScale();
        _ = Task.Run(async () =>
        {
            try
            {
                var connector = new Connector(Http, _settings, FilesPath, CachePath);
                var spec = await connector.PrepareAsync(address, defaultScale, ConfirmCompatModeAsync, AskAotAsync,
                    progress, cts.Token);
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    StartGame(spec);
                });
            }
            catch (System.OperationCanceledException) when (cts.IsCancellationRequested)
            {
                RunOnUiThread(dialog.Dismiss);
            }
            catch (Exception e)
            {
                global::Android.Util.Log.Error("Launcher", e.ToString());
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
                        .SetTitle("Не удалось подключиться")!
                        .SetMessage(e is UserFacingException ? e.Message : $"{e.GetType().Name}: {e.Message}")!
                        .SetPositiveButton("OK", (_, _) => { })!
                        .Show();
                });
            }
        });
    }

    private Task<bool> ConfirmCompatModeAsync(string engineVersion)
    {
        var tcs = new TaskCompletionSource<bool>();
        RunOnUiThread(() =>
        {
            new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
                .SetTitle("Режим совместимости")!
                .SetMessage(
                    $"Для движка {engineVersion} нет Android-сборки. Можно запустить официальную сборку этой версии, " +
                    "адаптированную под Android.\n\n" +
                    "В этом режиме песочница отключена: код сервера получит полный доступ к приложению " +
                    "(в том числе к сохранённому входу в аккаунт). Подключайтесь так только к серверам, которым доверяете.\n\n" +
                    "Возможны сбои: такие сборки не проверялись на Android.")!
                .SetCancelable(false)!
                .SetPositiveButton("Доверяю, запустить", (_, _) => tcs.TrySetResult(true))!
                .SetNegativeButton("Отмена", (_, _) => tcs.TrySetResult(false))!
                .Show();
        });
        return tcs.Task;
    }

    private Task<AotChoice> AskAotAsync(string engine, long size)
    {
        var tcs = new TaskCompletionSource<AotChoice>();
        RunOnUiThread(() =>
        {
            var sizeText = size > 0 ? $" (≈{size / 1048576.0:0} МБ)" : "";
            var body = Ui.Column(this);
            var pad = Ui.Dp(this, 20);
            body.SetPadding(pad, Ui.Dp(this, 8), pad, Ui.Dp(this, 8));
            body.AddView(Ui.Label(this,
                $"Сервер работает на {engine}.\n\n" +
                $"Сделать предкомпиляцию{sizeText}? Игра на этом движке будет запускаться намного быстрее.", 15));
            body.AddView(Ui.Label(this, "Изменить выбор можно в настройках.", 13, Ui.Muted), Ui.Fill(0, 6, this));

            AlertDialog? dialog = null;
            void Add(string text, AotChoice choice, bool primary = false) =>
                body.AddView(Ui.Button(this, text, () =>
                {
                    tcs.TrySetResult(choice);
                    dialog?.Dismiss();
                }, primary), Ui.Fill(0, 8, this));
            Add("Да", AotChoice.Yes, true);
            Add("Нет", AotChoice.No);
            Add("Всегда да", AotChoice.AlwaysYes);
            Add("Всегда нет", AotChoice.AlwaysNo);

            var scroll = new ScrollView(this);
            scroll.AddView(body);
            dialog = new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
                .SetTitle("Предкомпиляция")!
                .SetView(scroll)!
                .SetCancelable(false)!
                .Create()!;
            dialog.Show();
        });
        return tcs.Task;
    }

    private static string FormatAmount(DownloadProgress p)
    {
        // Byte counts for engine/zip downloads, file counts for manifest downloads.
        if (p.Total > 100_000)
            return $"{p.Done / 1048576.0:0.0} / {p.Total / 1048576.0:0.0} МБ";
        return $"{p.Done} / {p.Total}";
    }

    private string CrashFile => System.IO.Path.Combine(FilesPath, "last-crash.txt");
    private string GameLogFile => System.IO.Path.Combine(FilesPath, "last-game.log");

    private void ShowGameLog()
    {
        if (!File.Exists(GameLogFile))
        {
            Toast.MakeText(this, "Лога ещё нет: игра не запускалась.", ToastLength.Short)!.Show();
            return;
        }

        var filesDir = FilesPath;
        _ = Task.Run(() =>
        {
            var text = GameLogCollector.Collect(filesDir);
            RunOnUiThread(() => ShowLogDialog(text));
        });
    }

    private void ShowLogDialog(string text)
    {
        // The summary comes first; it is what to send if only a short message fits.
        var cut = text.IndexOf("=== host log ===", StringComparison.Ordinal);
        var summary = cut > 0 ? text[..cut].TrimEnd() : text;

        var shown = text.Split('\n');
        var view = Ui.Label(this, string.Join("\n", shown.Take(500)) + (shown.Length > 500 ? "\n… (полный лог — «Отправить файлом»)" : ""), 11);
        view.SetTextIsSelectable(true);
        view.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 8));
        var scroll = new ScrollView(this);
        scroll.AddView(view);

        new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
            .SetTitle("Лог последней игры")!
            .SetView(scroll)!
            .SetPositiveButton("Закрыть", (_, _) => { })!
            .SetNeutralButton("Копировать итог", (_, _) =>
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
                cm.PrimaryClip = ClipData.NewPlainText("game log", summary);
                Toast.MakeText(this, "Итог скопирован", ToastLength.Short)!.Show();
            })!
            .SetNegativeButton("Отправить файлом", (_, _) =>
            {
                try
                {
                    var uri = GameLogCollector.ExportForSharing(this, text);
                    var send = new Intent(Intent.ActionSend);
                    send.SetType("text/plain");
                    send.PutExtra(Intent.ExtraStream, uri);
                    send.PutExtra(Intent.ExtraSubject, "game-log.txt");
                    send.AddFlags(ActivityFlags.GrantReadUriPermission);
                    StartActivity(Intent.CreateChooser(send, "Отправить лог"));
                }
                catch (Exception e)
                {
                    Toast.MakeText(this, e.Message, ToastLength.Long)!.Show();
                }
            })!
            .Show();
    }

    /// <summary>Extracts the game host assembly from the APK (cheap, done on every launch so updates apply).</summary>
    private string ExtractGameHost()
    {
        var dir = Directory.CreateDirectory(System.IO.Path.Combine(FilesPath, "gamehost")).FullName;
        var path = System.IO.Path.Combine(dir, "Robust.Android.GameHost.dll");
        using var src = Assets!.Open("gamehost/Robust.Android.GameHost.dll");
        using var dst = File.Create(path);
        src.CopyTo(dst);
        return path;
    }

    private string GameStartedMarker => System.IO.Path.Combine(FilesPath, "game-started");

    private bool IsGameProcessRunning()
    {
        var am = (ActivityManager?)GetSystemService(ActivityService);
        var procs = am?.RunningAppProcesses;
        return procs != null && procs.Any(p => p.ProcessName?.EndsWith(":game") == true);
    }

    /// <summary>
    /// The game died without a managed exception (native crash, killed for memory...):
    /// grab logcat right away (it is a ring buffer) and offer the log.
    /// </summary>
    private void CheckUnexpectedGameExit()
    {
        if (!File.Exists(GameStartedMarker) || IsGameProcessRunning())
            return;
        File.Delete(GameStartedMarker);

        string log;
        try
        {
            using var fs = new FileStream(GameLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            log = new StreamReader(fs).ReadToEnd();
        }
        catch
        {
            log = "";
        }

        GameLogCollector.SnapshotLogcat(FilesPath);
        if (log.Contains("Game host exited with 0") || File.Exists(CrashFile))
            return;

        new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
            .SetTitle("Игра неожиданно закрылась")!
            .SetMessage("Похоже, процесс игры аварийно завершился (без ошибки .NET). Лог поможет понять причину — его можно скопировать или отправить.")!
            .SetPositiveButton("Показать лог", (_, _) => ShowGameLog())!
            .SetNegativeButton("Закрыть", (_, _) => { })!
            .Show();
    }

    private void StartGame(GameLaunch launch)
    {
        try
        {
            File.Delete(GameLogCollector.LogcatSnapshot(FilesPath));
            File.WriteAllText(GameStartedMarker, DateTime.UtcNow.ToString("O"));
        }
        catch
        {
            // ignored
        }

        // Must match robust.android.GameActivity (Java/robust/android/GameActivity.java).
        var intent = new Intent();
        intent.SetClassName(this, "robust.android.GameActivity");
        intent.PutExtra("runtime_dir", launch.RuntimeDir);
        intent.PutExtra("host_dll", ExtractGameHost());
        intent.PutExtra("spec", launch.SpecPath);
        intent.PutExtra("crash", CrashFile);
        intent.PutExtra("runtime_config", launch.RuntimeConfig);
        intent.PutExtra("aot", launch.UseAot);
        intent.PutExtra("touch", _settings.TouchControls);
        intent.PutExtra("touch_opacity", _settings.TouchOpacity);
        intent.PutExtra("touch_scale", _settings.TouchScale);
        StartActivity(intent);
    }

    private void ShowLastCrashIfAny()
    {
        var path = CrashFile;
        if (!File.Exists(path))
            return;
        if (File.Exists(GameStartedMarker))
            File.Delete(GameStartedMarker);

        var text = File.ReadAllText(path);
        File.Delete(path);
        new AlertDialog.Builder(this, Resource.Style.LauncherDialog)
            .SetTitle("Игра завершилась с ошибкой")!
            .SetMessage(text.Length > 4000 ? text[..4000] + "…" : text)!
            .SetPositiveButton("OK", (_, _) => { })!
            .SetNeutralButton("Копировать", (_, _) =>
            {
                var cm = (ClipboardManager)GetSystemService(ClipboardService)!;
                cm.PrimaryClip = ClipData.NewPlainText("crash", text);
            })!
            .SetNegativeButton("Лог игры", (_, _) => ShowGameLog())!
            .Show();
    }
}

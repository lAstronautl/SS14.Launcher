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
    private enum Tab { Home, Servers, Game, Settings }

    private const string AccountSiteUrl = "https://account.spacestation14.com/";

    private static readonly HttpClient Http = CreateHttp();

    private LauncherSettings _settings = null!;
    private FrameLayout _content = null!;
    private Button _accountButton = null!;
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

        // --- Header: stripeback band with the title (gold) and the account button.
        var header = Ui.Row(this);
        header.Background = new Ui.StripeDrawable(Ui.Dp(this, 22));
        header.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10));
        var title = Ui.Label(this, GetString(Resource.String.app_name).ToUpperInvariant(), 22, Ui.NanoGold, bold: true);
        title.LetterSpacing = 0.08f;
        header.AddView(title, Ui.Fill(1));
        _accountButton = Ui.Button(this, "", () => ShowTab(Tab.Settings));
        _accountButton.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 14);
        header.AddView(_accountButton, Ui.Wrap(8, this));
        root.AddView(header);
        UpdateAccountButton();

        // --- Tabs: angled items; the selected one is green like the launcher's tab control.
        var tabs = Ui.Row(this);
        tabs.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 8), 0);
        foreach (var (tab, text) in new[]
                 {
                     (Tab.Home, "Главная"), (Tab.Servers, "Серверы"), (Tab.Game, "Игра"), (Tab.Settings, "Настройки")
                 })
        {
            var t = Ui.Label(this, text, 15);
            t.Gravity = GravityFlags.Center;
            t.SetPadding(0, Ui.Dp(this, 10), 0, Ui.Dp(this, 10));
            t.Clickable = true;
            t.Click += (_, _) => ShowTab(tab);
            var lp = Ui.Fill(1);
            lp.SetMargins(Ui.Dp(this, 2), 0, Ui.Dp(this, 2), 0);
            tabs.AddView(t, lp);
            _tabButtons[tab] = t;
        }

        root.AddView(tabs);
        var under = new View(this);
        under.SetBackgroundColor(Ui.ControlHigh);
        root.AddView(under, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 2)));

        _content = new FrameLayout(this);
        root.AddView(_content, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
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
        _accountButton.Text = _settings.Login is { } login ? $"👤 {login.Username}" : "Войти";
    }

    private void ShowTab(Tab tab)
    {
        _tab = tab;
        foreach (var (t, view) in _tabButtons)
        {
            view.Background = t == tab
                ? Ui.Angle(this, Ui.ControlHigh, 8)
                : Ui.AngleStates(this, Ui.Bg, Ui.Hovered, 8);
            view.SetTextColor(t == tab ? Ui.Text : Ui.Muted);
            view.Typeface = t == tab ? Ui.Bold(this) : Ui.Regular(this);
        }

        _content.RemoveAllViews();
        _content.AddView(tab switch
        {
            Tab.Home => BuildHomeTab(),
            Tab.Servers => BuildServersTab(),
            Tab.Game => BuildGameTab(),
            _ => BuildSettingsTab(),
        });
    }

    private ListView MakeServerList(ServerAdapter adapter)
    {
        var list = new ListView(this) { DividerHeight = 0 };
        list.SetBackgroundColor(Ui.ListRowA);
        list.Adapter = adapter;
        list.ItemClick += (_, e) => ShowServerDialog(adapter[e.Position]);
        return list;
    }

    // ------------------------------------------------------------------ Home (favorites)

    private View BuildHomeTab()
    {
        var col = Ui.Column(this, 12);

        var top = Ui.Row(this);
        top.AddView(Ui.Heading(this, "Избранные серверы"), Ui.Fill(1));
        col.AddView(top);

        if (_settings.Favorites.Count == 0)
        {
            var empty = Ui.Label(this,
                "Здесь будут ваши избранные серверы.\nДобавьте сервер со вкладки «Серверы» или по адресу.", 15, Ui.Muted);
            empty.SetPadding(0, Ui.Dp(this, 16), 0, Ui.Dp(this, 16));
            col.AddView(empty);
            var spacer = new View(this);
            col.AddView(spacer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        }
        else
        {
            var adapter = new ServerAdapter(this);
            adapter.SetItems(_settings.Favorites.Select(f => new ServerItem(f.Address, f.Name, -1, 0)).ToList());
            var list = MakeServerList(adapter);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1) { TopMargin = Ui.Dp(this, 8) };
            col.AddView(list, lp);

            // Refresh player counts in the background.
            _ = Task.Run(async () =>
            {
                var api = new ServerApi(Http);
                var items = new List<ServerItem>();
                foreach (var f in _settings.Favorites.ToList())
                {
                    try
                    {
                        var s = await api.GetStatusAsync(ServerAddress.Parse(f.Address));
                        items.Add(new ServerItem(f.Address, s.Name ?? f.Name, s.Players, s.SoftMaxPlayers));
                    }
                    catch
                    {
                        items.Add(new ServerItem(f.Address, f.Name, -2, 0));
                    }
                }

                RunOnUiThread(() => adapter.SetItems(items));
            });
        }

        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Button(this, "Прямое подключение", AskDirectConnect), Ui.Fill(1));
        var addLp = Ui.Fill(1);
        addLp.LeftMargin = Ui.Dp(this, 8);
        buttons.AddView(Ui.Button(this, "Добавить сервер", AskAddFavorite), addLp);
        col.AddView(buttons, Ui.Fill(0, 10, this));
        return col;
    }

    // ------------------------------------------------------------------ Servers (hub)

    private View BuildServersTab()
    {
        var col = Ui.Column(this, 12);

        var top = Ui.Row(this);
        var search = Ui.Input(this, "Поиск серверов", _filter);
        top.AddView(search, Ui.Fill(1));
        top.AddView(Ui.Button(this, "⟳", () => _ = LoadHubAsync(true)), Ui.Wrap(8, this));
        col.AddView(top);

        var filters = Ui.Row(this);
        var hideEmpty = Ui.Check(this, "Скрыть пустые", _hideEmpty);
        filters.AddView(hideEmpty, Ui.Fill(1));
        var status = Ui.Label(this, "", 13, Ui.Muted);
        filters.AddView(status, Ui.Wrap());
        col.AddView(filters, Ui.Fill(0, 4, this));

        var adapter = new ServerAdapter(this);
        var list = MakeServerList(adapter);
        col.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        void Apply()
        {
            var f = _filter.Trim();
            var items = _hubServers
                .Where(s => f.Length == 0
                            || (s.StatusData?.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
                            || s.Address.Contains(f, StringComparison.OrdinalIgnoreCase))
                .Where(s => !_hideEmpty || (s.StatusData?.Players ?? 0) > 0)
                .OrderByDescending(s => s.StatusData?.Players ?? 0)
                .Select(s => new ServerItem(s.Address, s.StatusData?.Name ?? s.Address, s.StatusData?.Players ?? 0,
                    s.StatusData?.SoftMaxPlayers ?? 0))
                .ToList();
            adapter.SetItems(items);
            status.Text = _hubServers.Count == 0
                ? "Получение списка серверов…"
                : $"Серверов: {items.Count} / {_hubServers.Count}";
        }

        search.TextChanged += (_, _) =>
        {
            _filter = search.Text ?? "";
            Apply();
        };
        hideEmpty.CheckedChange += (_, e) =>
        {
            _hideEmpty = e.IsChecked;
            Apply();
        };

        _onHubLoaded = Apply;
        Apply();
        if (_hubServers.Count == 0)
            _ = LoadHubAsync(false);
        return col;
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

    private void ShowServerDialog(ServerItem item)
    {
        var isFav = _settings.Favorites.Any(f => f.Address == item.Address);
        var body = Ui.Column(this, 20);
        body.AddView(Ui.Label(this, item.Address, 13, Ui.Muted));
        var info = Ui.Label(this, "Получение информации о сервере…", 14, Ui.Text);
        body.AddView(info, Ui.Fill(0, 8, this));
        var scroll = new ScrollView(this);
        scroll.AddView(body);

        var dialog = Ui.Dialog(this, item.Name)
            .SetView(scroll)!
            .SetPositiveButton("Подключиться", (_, _) =>
            {
                if (ServerAddress.TryParse(item.Address, out var addr))
                    Connect(addr, item.Name);
            })!
            .SetNeutralButton(isFav ? "Убрать из избранного" : "В избранное", (_, _) =>
            {
                if (isFav)
                    RemoveFavorite(item.Address);
                else
                    AddFavorite(item.Address, item.Name);
            })!
            .SetNegativeButton("Закрыть", (_, _) => { })!
            .Show();

        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var addr = ServerAddress.Parse(item.Address);
                var api = new ServerApi(Http);
                var status = await api.GetStatusAsync(addr);
                var srvInfo = await api.GetInfoAsync(addr);
                var ver = srvInfo.Build?.EngineVersion ?? "?";
                var engines = new EngineManager(Http, CachePath, _settings.EngineReleaseBase);
                var availability = ver == "?" ? EngineAvailability.None : await engines.CheckAvailabilityAsync(ver);
                var engineText = availability switch
                {
                    EngineAvailability.Patched => "✔ Android-сборка",
                    EngineAvailability.Official => "⚠ режим совместимости (без песочницы)",
                    _ => "✘ не поддерживается",
                };
                text = $"Игроков: {status.Players}/{status.SoftMaxPlayers}\n" +
                       (status.Map != null ? $"Карта: {status.Map}\n" : "") +
                       (status.Preset != null ? $"Режим: {status.Preset}\n" : "") +
                       $"Движок: {ver} — {engineText}\n" +
                       $"Вход: {AuthText(srvInfo.Auth?.Mode)}" +
                       (string.IsNullOrWhiteSpace(srvInfo.Description) ? "" : $"\n\n{srvInfo.Description}");
            }
            catch (Exception e)
            {
                text = $"Не удалось связаться с сервером: {e.Message}";
            }

            RunOnUiThread(() =>
            {
                if (dialog.IsShowing)
                    info.Text = text;
            });
        });
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

    private View BuildGameTab()
    {
        var scroll = new ScrollView(this);
        var col = Ui.Column(this, 14);
        scroll.AddView(col);

        col.AddView(Ui.Label(this,
            "Эти настройки применяются при входе на любой сервер. Изменённые здесь параметры заменяют " +
            "настройки из меню игры.", 13, Ui.Muted));

        // UI scale (launcher-specific, always sent).
        var ui = Section(col, "Масштаб интерфейса");
        var scales = new[] { 0f, 1f, 1.25f, 1.5f, 1.75f, 2f, 2.5f };
        var scaleNames = scales.Select(s => s == 0 ? $"Авто ({DefaultUiScale():0.##}×)" : $"{s:0.##}×").ToArray();
        ui.AddView(ChoiceRow("Масштаб", scaleNames, Math.Max(0, Array.IndexOf(scales, _settings.UiScale)), i =>
        {
            _settings.UiScale = scales[i];
            _settings.Save(FilesPath);
        }));

        foreach (var section in GameSettings.All.GroupBy(o => o.Section))
        {
            var box = Section(col, section.Key);
            var first = true;
            foreach (var o in section)
            {
                if (!first)
                    box.AddView(Ui.Separator(this), Ui.Fill(0, 6, this));
                first = false;
                box.AddView(OptionView(o));
            }
        }

        col.AddView(Ui.Button(this, "Сбросить игровые настройки", () =>
        {
            _settings.GameCVars.Clear();
            _settings.UiScale = 0;
            _settings.Save(FilesPath);
            ShowTab(Tab.Game);
        }), Ui.Fill(0, 20, this));
        return scroll;
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
                var c = Ui.Check(this, o.Title, value == "true");
                c.CheckedChange += (_, e) => Save(e.IsChecked ? "true" : "false");
                col.AddView(c);
                break;
            }
            case GameSettings.Kind.Choice:
            {
                var choices = o.Choices!;
                var idx = Math.Max(0, Array.FindIndex(choices, c => c.Value == value));
                col.AddView(ChoiceRow(o.Title, choices.Select(c => c.Label).ToArray(), idx, i => Save(choices[i].Value)));
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

        var touch = Section(col, "Сенсорное управление");
        var touchOn = Ui.Check(this, "Показывать джойстик и кнопки", _settings.TouchControls);
        touchOn.CheckedChange += (_, e) =>
        {
            _settings.TouchControls = e.IsChecked;
            _settings.Save(FilesPath);
        };
        touch.AddView(touchOn);
        var opacityLabel = Ui.Label(this, $"Непрозрачность кнопок: {_settings.TouchOpacity}%", 15);
        touch.AddView(opacityLabel, Ui.Fill(0, 8, this));
        var opacity = Ui.Slider(this, 100, _settings.TouchOpacity);
        opacity.ProgressChanged += (_, e) =>
        {
            _settings.TouchOpacity = Math.Max(15, e.Progress);
            opacityLabel.Text = $"Непрозрачность кнопок: {_settings.TouchOpacity}%";
            _settings.Save(FilesPath);
        };
        touch.AddView(opacity);
        var sizeLabel = Ui.Label(this, $"Размер кнопок: {_settings.TouchScale:0.0}×", 15);
        touch.AddView(sizeLabel, Ui.Fill(0, 8, this));
        var size = Ui.Slider(this, 10, (int)Math.Round((_settings.TouchScale - 0.5f) * 10));
        size.ProgressChanged += (_, e) =>
        {
            _settings.TouchScale = 0.5f + e.Progress / 10f;
            sizeLabel.Text = $"Размер кнопок: {_settings.TouchScale:0.0}×";
            _settings.Save(FilesPath);
        };
        touch.AddView(size);

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

using System.Globalization;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// In-game options exposed in the launcher, so players can set them before joining.
/// Values are passed to the game as <c>--cvar</c>. Only settings the player changed are sent: everything else
/// keeps the game's own value (including what they change in the game's own options menu).
/// CVar names are the engine's (Robust.Shared/CVars.cs) and SS14 content's (Content.Shared/CCVar);
/// a CVar that a server's game doesn't have is simply ignored by the engine.
/// </summary>
internal static class GameSettings
{
    public enum Kind
    {
        Toggle,
        Slider,
        Choice,
    }

    /// <param name="Tab">Tab of the game's options window ("Основные", "Графика", ...).</param>
    /// <param name="Group">Gold sub-heading inside the tab, as in the game ("Дисплей", "Качество", ...).</param>
    /// <param name="CVar">CVar name; names starting with "@" are presets expanded by <see cref="Expand"/>.</param>
    public sealed record Option(
        string Tab,
        string Group,
        string CVar,
        string Title,
        Kind Kind,
        string Default,
        // Slider: value = Min + step * index; shown as Percent (×100) or plain.
        float Min = 0,
        float Max = 1,
        float Step = 0.05f,
        bool Percent = false,
        (string Value, string Label)[]? Choices = null,
        string? Description = null,
        // Toggle: CVar values for checked / unchecked (some game options are inverted or not booleans).
        string On = "true",
        string Off = "false",
        Func<string, IEnumerable<(string CVar, string Value)>>? Expand = null);

    public const string TabGeneral = "Основные";
    public const string TabGraphics = "Графика";
    public const string TabControls = "Управление";
    public const string TabAudio = "Аудио";
    public const string TabAccessibility = "Доступность";

    public static readonly string[] Tabs = { TabGeneral, TabGraphics, TabControls, TabAudio, TabAccessibility };

    public const string UiScaleCVar = "display.uiScale";

    /// <summary>Same presets as the game's "Качество освещения" (Content.Client GraphicsTab.OptionLightingQuality).</summary>
    private static IEnumerable<(string, string)> LightingQuality(string v) => v switch
    {
        "0" => new[] { ("light.resolution_scale", "0.125"), ("light.soft_shadows", "false"), ("light.blur", "false") },
        "1" => new[] { ("light.resolution_scale", "0.5"), ("light.soft_shadows", "false"), ("light.blur", "true") },
        "3" => new[] { ("light.resolution_scale", "1"), ("light.soft_shadows", "true"), ("light.blur", "true") },
        _ => new[] { ("light.resolution_scale", "0.5"), ("light.soft_shadows", "true"), ("light.blur", "true") },
    };

    public static readonly Option[] All =
    {
        // ---- Основные
        new(TabGeneral, "Интерфейс", "ui.layout", "Раскладка интерфейса", Kind.Choice, "Default",
            Choices: new[] { ("Default", "Стандартная"), ("Separated", "Раздельная (чат справа)") }),
        new(TabGeneral, "Интерфейс", "accessibility.chat_window_transparency", "Прозрачность чата", Kind.Slider, "0.85",
            0, 1, 0.05f, Percent: true),

        // ---- Графика: as the game's Graphics tab (Content.Client/Options/UI/Tabs/GraphicsTab.xaml), without bloom.
        new(TabGraphics, "Дисплей", "display.vsync", "Вертикальная синхронизация", Kind.Toggle, "true"),
        new(TabGraphics, "Дисплей", "display.windowmode", "Полный экран", Kind.Toggle, "0", On: "1", Off: "0"),
        new(TabGraphics, "Качество", "@lighting_quality", "Качество освещения", Kind.Choice, "2",
            Choices: new[] { ("0", "Очень низкое"), ("1", "Низкое"), ("2", "Среднее"), ("3", "Высокое") },
            Description: "На телефоне «Низкое» или «Очень низкое» заметно поднимает FPS.",
            Expand: LightingQuality),
        new(TabGraphics, "Качество", "viewport.scale_render", "Изображение низкого разрешения", Kind.Toggle, "true",
            On: "false", Off: "true"),
        new(TabGraphics, "Качество", "parallax.low_quality", "Низкокачественный параллакс (фон)", Kind.Toggle, "false"),
        new(TabGraphics, "Качество", "light.ambient_occlusion", "Показывать затенение окружения", Kind.Toggle, "true"),
        new(TabGraphics, "Интерфейс", UiScaleCVar, "Масштаб UI", Kind.Choice, "0",
            Choices: new[]
            {
                ("0", "Автоматическое"), ("0.75", "75 %"), ("1", "100 %"), ("1.25", "125 %"), ("1.5", "150 %"),
                ("1.75", "175 %"), ("2", "200 %"),
            }),
        new(TabGraphics, "Интерфейс", "viewport.stretch", "Растянуть изображение для соответствия окну игры", Kind.Toggle, "true"),
        new(TabGraphics, "Интерфейс", "viewport.fixed_scale_factor", "Масштаб окна просмотра", Kind.Slider, "2", 1, 5, 1,
            Description: "Когда растягивание выключено."),
        new(TabGraphics, "Интерфейс", "viewport.width", "Ширина окна просмотра", Kind.Slider, "21", 15, 30, 1,
            Description: "Сервер может ограничить диапазон."),
        new(TabGraphics, "Интерфейс", "viewport.snap_tolerance_margin",
            "Использовать целочисленное масштабирование (может вызывать появление чёрных полос/обрезания)", Kind.Toggle,
            "64", On: "64", Off: "0"),
        new(TabGraphics, "Интерфейс", "viewport.scaling_filter", "Фильтр масштабирования", Kind.Choice, "nearest",
            Choices: new[] { ("nearest", "Ближайший (без сглаживания)"), ("bilinear", "Билинейный (сглаживание)") }),
        new(TabGraphics, "Интерфейс", "viewport.vertical_fit", "Подгон окна просмотра по вертикали", Kind.Toggle, "true"),
        new(TabGraphics, "Прочее", "hud.fps_counter_visible", "Показать счётчик FPS", Kind.Toggle, "false"),
        new(TabGraphics, "Прочее", "display.max_fps", "Ограничение FPS", Kind.Choice, "0",
            Choices: new[] { ("0", "Без ограничения"), ("30", "30"), ("45", "45"), ("60", "60") },
            Description: "Ограничение экономит батарею и снижает нагрев."),

        // ---- Аудио
        new(TabAudio, "Громкость", "audio.mastervolume", "Общая громкость", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new(TabAudio, "Громкость", "audio.interface_volume", "Громкость интерфейса", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new(TabAudio, "Громкость", "ambience.volume", "Громкость окружения", Kind.Slider, "1.5", 0, 3, 0.1f, Percent: true),
        new(TabAudio, "Громкость", "ambience.music_volume", "Громкость музыки", Kind.Slider, "1.5", 0, 3, 0.1f, Percent: true),
        new(TabAudio, "Громкость", "ambience.lobby_music_volume", "Громкость музыки в лобби", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new(TabAudio, "Прочее", "ambience.lobby_music_enabled", "Музыка в лобби", Kind.Toggle, "true"),
        new(TabAudio, "Прочее", "ambience.restart_sounds_enabled", "Звуки рестарта раунда", Kind.Toggle, "true"),
        new(TabAudio, "Прочее", "audio.mute_unfocused", "Без звука в фоне", Kind.Toggle, "false"),

        // ---- Доступность
        new(TabAccessibility, "Доступность", "accessibility.screen_shake_intensity", "Сила тряски экрана", Kind.Slider, "1",
            0, 1, 0.05f, Percent: true),
        new(TabAccessibility, "Доступность", "accessibility.reduced_motion", "Уменьшить анимации", Kind.Toggle, "false"),
        new(TabAccessibility, "Доступность", "accessibility.colorblind_friendly", "Режим для дальтоников", Kind.Toggle, "false"),
    };

    public static string Get(LauncherSettings s, Option o) =>
        s.GameCVars.TryGetValue(o.CVar, out var v) ? v : o.Default;

    public static void Set(LauncherSettings s, Option o, string value)
    {
        if (value == o.Default)
            s.GameCVars.Remove(o.CVar);
        else
            s.GameCVars[o.CVar] = value;
    }

    public static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static float ParseFloat(string v) =>
        float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;

    /// <summary>"cvar=value" list for the engine command line: only what the player changed.</summary>
    public static IEnumerable<string> ToCVars(LauncherSettings s)
    {
        foreach (var (key, value) in s.GameCVars)
        {
            if (!key.StartsWith('@'))
            {
                yield return $"{key}={value}";
                continue;
            }

            var preset = All.FirstOrDefault(o => o.CVar == key);
            if (preset?.Expand == null)
                continue;
            foreach (var (cvar, v) in preset.Expand(value))
                yield return $"{cvar}={v}";
        }
    }
}

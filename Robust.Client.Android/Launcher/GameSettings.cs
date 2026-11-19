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

    public sealed record Option(
        string Section,
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
        string? Description = null);

    public static readonly Option[] All =
    {
        // ---- Graphics (engine)
        new("Графика", "display.max_fps", "Ограничение FPS", Kind.Choice, "0",
            Choices: new[] { ("0", "Без ограничения"), ("30", "30"), ("45", "45"), ("60", "60") },
            Description: "Ограничение экономит батарею и снижает нагрев."),
        new("Графика", "display.vsync", "Вертикальная синхронизация", Kind.Toggle, "true"),
        new("Графика", "light.resolution_scale", "Качество освещения", Kind.Choice, "0.5",
            Choices: new[] { ("0.25", "Низкое"), ("0.5", "Среднее"), ("1", "Высокое") }),
        new("Графика", "light.soft_shadows", "Мягкие тени", Kind.Toggle, "true",
            Description: "Сильно нагружают видеочип телефона."),
        new("Графика", "light.blur", "Размытие света", Kind.Toggle, "true"),
        new("Графика", "render.tile_edges", "Края тайлов", Kind.Toggle, "true"),
        new("Графика", "viewport.stretch", "Растягивать игровое окно", Kind.Toggle, "true"),
        new("Графика", "hud.fps_counter_visible", "Показывать FPS", Kind.Toggle, "false"),

        // ---- Audio
        new("Звук", "audio.mastervolume", "Общая громкость", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new("Звук", "audio.interface_volume", "Громкость интерфейса", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new("Звук", "ambience.volume", "Громкость окружения", Kind.Slider, "1.5", 0, 3, 0.1f, Percent: true),
        new("Звук", "ambience.music_volume", "Громкость музыки", Kind.Slider, "1.5", 0, 3, 0.1f, Percent: true),
        new("Звук", "ambience.lobby_music_enabled", "Музыка в лобби", Kind.Toggle, "true"),
        new("Звук", "ambience.lobby_music_volume", "Громкость музыки в лобби", Kind.Slider, "0.5", 0, 1, 0.05f, Percent: true),
        new("Звук", "ambience.restart_sounds_enabled", "Звуки рестарта раунда", Kind.Toggle, "true"),
        new("Звук", "audio.mute_unfocused", "Без звука в фоне", Kind.Toggle, "false"),

        // ---- Interface
        new("Интерфейс", "ui.layout", "Раскладка интерфейса", Kind.Choice, "Default",
            Choices: new[] { ("Default", "Стандартная"), ("Separated", "Раздельная (чат справа)") }),
        new("Интерфейс", "accessibility.chat_window_transparency", "Прозрачность чата", Kind.Slider, "0.85", 0, 1, 0.05f, Percent: true),

        // ---- Accessibility
        new("Доступность", "accessibility.screen_shake_intensity", "Сила тряски экрана", Kind.Slider, "1", 0, 1, 0.05f, Percent: true),
        new("Доступность", "accessibility.reduced_motion", "Уменьшить анимации", Kind.Toggle, "false"),
        new("Доступность", "accessibility.colorblind_friendly", "Режим для дальтоников", Kind.Toggle, "false"),
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
    public static IEnumerable<string> ToCVars(LauncherSettings s) =>
        s.GameCVars.Select(kv => $"{kv.Key}={kv.Value}");
}

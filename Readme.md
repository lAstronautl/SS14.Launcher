# SS14.Android

Ветка **SS14.Android** — Android-версия лаунчера: список серверов, вход в аккаунт, загрузка движка и контента, запуск игры с сенсорным управлением.

| Папка | Что это |
|---|---|
| `Robust.Client.Android` | APK: интерфейс лаунчера, процесс игры (SDL3), нативные библиотеки, AOT |
| `Robust.Launcher.Core` | хаб, авторизация, загрузка движка, контента и среды .NET |
| `Robust.Android.GameHost` | точка входа в процессе игры, запускает движок |
| `Robust.Launcher.Cli` | тот же лаунчер в консоли, для проверки на ПК |

Сборка и устройство описаны в [Robust.Client.Android/README.md](Robust.Client.Android/README.md), CI находится в `.github/workflows/android.yml`. Ниже — исходный README настольного лаунчера.

---

# SS14.Launcher

<a href="https://weblate.spacestation14.com/engage/space-station-14-launcher/">
<img src="https://weblate.spacestation14.com/widget/space-station-14-launcher/main/svg-badge.svg" alt="Translation status" />
</a>

This is the launcher you should be using to connect to SS14 servers. Server browser, content downloads, account management. It's got it all!

# Development

Useful environment variables for development:
* `SS14_LAUNCHER_APPDATA_NAME=launcherTest` to change the user data directories the launcher stores its data in. This can be useful to avoid breaking your "normal" SS14 launcher data while developing something.
* `SS14_LAUNCHER_OVERRIDE_AUTH_URL=https://.../` to change the auth API URL to test against a local dev version of the API.

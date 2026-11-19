# Robust Android (лаунчер + клиент)

Android-приложение, которое показывает серверы SS14, входит в аккаунт, скачивает движок и контент сервера и запускает игру с сенсорным управлением.

## Как это устроено

```
APK
├─ Лаунчер (MainActivity, C#, .NET 10)   серверы, избранное, аккаунт, настройки
├─ GameActivity (Java, процесс :game)    SDLActivity + сенсорное управление
├─ libmain.so (natives/robust_host.c)    SDL_main → загружает нужный Mono → GameHost
├─ assets/gamehost/Robust.Android.GameHost.dll (net6.0)  запуск движка через ILoaderEntryPoint
└─ Нативные .so                          SDL3, OpenAL Soft, FreeType, libsodium, zstd

Скачивается во время работы (кэш приложения):
├─ Движок   наш релиз android-engine-v<версия> ИЛИ официальная сборка Space Wizards этой версии
├─ Среда    Mono-рантайм .NET той версии, под которую собран движок (NuGet: Microsoft.NETCore.App.Runtime.Mono.android-*)
└─ Контент  с самого сервера (ACZ-манифест /manifest.txt + /download или zip)
```

- **Подходит любая версия движка на SDL3 и любая версия .NET (6–10).** Лаунчер берёт `engine_version` из `/info` сервера.
  - Если есть наша Android-сборка этой версии, используется она: Android-поддержка встроена, песочница включена.
  - Если нет, скачивается официальная сборка и адаптируется снаружи (`CompatShims`). Это «режим совместимости»: песочница выключена, поэтому лаунчер спрашивает подтверждение для каждого сервера.
  - Движки на GLFW (без SDL3) на Android не запускаются.
- **Каждый движок работает на своей версии .NET.** Игровой процесс не использует .NET самого приложения: `robust_host.c` загружает скачанный Mono-рантайм версии из `Robust.Client.runtimeconfig.json` движка. Так движок под .NET 9 не ломается на изменениях .NET 10 (например, `BitArray` стал `ISerializable`).
- **Каждая игра — новый процесс `:game`.** Ни движок, ни рантайм нельзя перезапустить в том же процессе. Встроенный загрузчик .NET приложения из этого процесса убран при сборке (`RemoveGameProcessMonoProvider`).
- **Управление.** Слева джойстик (W/A/S/D). Справа кнопки на стандартных клавишах: Z — использовать, E — активировать, X — сменить руку, Q — бросить, H — отпустить, I — инвентарь. Есть залипающие Shift, Ctrl и Alt.
  - Тап — ЛКМ, долгое нажатие — ПКМ.
  - Перетаскивание — ЛКМ с удержанием, два пальца — прокрутка.
  - Сверху кнопки Esc, «Чат» (T), Tab и зум (−/+). Кнопка «◐» прячет всё управление.
  - Кнопка «Назад» работает как Esc.
  - Физические клавиатура и мышь работают напрямую.

## Изменения движка для Android

Это проверки во время выполнения (`OperatingSystem.IsAndroid()`), поэтому один и тот же пакет движка работает и на ПК:

- GLES3/GLES2 и SDL3 без отдельного оконного потока (`Clyde.cs`);
- пути к пользовательским данным (`UserDataDir.cs`) и к исполняемому файлу (`PathHelpers.cs`);
- имена нативных библиотек (`ZStd.cs`, `DllMapHelper.cs`, `ClientDllMap.cs`);
- запасной менеджер системных шрифтов вместо fontconfig;
- каталог фреймворка для песочницы (`AssemblyTypeChecker.cs`, `ROBUST_FRAMEWORK_DIR`): это сборки скачанного рантайма;
- исправление: headless-клиент в релизной сборке больше не обращается к OpenGL (`ResourceCache.Preload.cs`).

## Ускоренный запуск (AOT)

Mono на телефоне по умолчанию компилирует весь код (JIT) при каждом запуске, поэтому старт долгий. Лаунчер скачивает заранее скомпилированный код (`<Сборка>.dll.so` рядом с DLL), и Mono подхватывает его сам:

- `android-runtime-aot-v<версия .NET>/runtime-aot-android-arm64.zip` — фреймворк. Версии среды зафиксированы в `aot/runtime-versions.txt` и `RuntimeManager.PinnedVersions`: образ загружается только в ту версию, под которую собран.
- `android-official-aot-v<версия движка>/official-aot-android-arm64.zip` — официальные сборки движка из списка `aot/official-engines.txt`.

Если пакета нет, игра работает как раньше, на JIT. Отключается в настройках («Ускоренный запуск»).

## Сборка

GitHub Actions, workflow **Android** (`.github/workflows/android.yml`):

- при push собирает APK и публикует недостающие AOT-пакеты;
- ручной запуск с **publish** создаёт релиз `android-launcher-v<версия>` с APK.

Локально (Linux, .NET 10, workload `android`, Android SDK/NDK):

```bash
ANDROID_NDK_HOME=$ANDROID_HOME/ndk/27.2.12479018 Robust.Client.Android/natives/build-natives.sh
dotnet publish Robust.Client.Android -c Release -p:AndroidSdkDirectory=$ANDROID_HOME -o apk-out
ANDROID_NDK_HOME=$ANDROID_HOME/ndk/27.2.12479018 Robust.Client.Android/aot/aot-compile.sh 10.0.15 out -- @framework
```

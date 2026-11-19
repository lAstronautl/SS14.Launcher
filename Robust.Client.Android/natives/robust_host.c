// Native host of the game process (libmain.so).
//
// SDLActivity loads this library and calls SDL_main on the SDL thread. Instead of using the .NET runtime that
// ships with the app (always the newest .NET), we start the Mono runtime of the .NET version the engine was
// built for. That runtime was downloaded by the launcher (official Microsoft.NETCore.App.Runtime.Mono.android-*
// package) and is loaded with dlopen, so one APK can run engines built for any .NET version.
//
// argv (from GameActivity.getArguments):
//   [1] runtime dir   (contains lib/*.dll and native/*.so)
//   [2] game host dll (Robust.Android.GameHost.dll)
//   [3] launch spec   (json)
//   [4] crash file
//   [5] app native library dir (SDL3, OpenAL, ... from the APK)
//   [6] runtime config properties, "key=value\n..." (from the engine's runtimeconfig.json)
//   [7] log file (optional): everything logged by the host and the engine, shown by the launcher

#include <stdarg.h>
#ifdef __ANDROID__
#include <android/log.h>
#include <sys/system_properties.h>
#else
// Desktop build for testing the host logic with the Linux Mono runtime (see ROBUST_HOST_TEST below).
#include <stdarg.h>
#include <stdio.h>
#define ANDROID_LOG_INFO 4
#define ANDROID_LOG_ERROR 6
static int __android_log_print(int prio, const char *tag, const char *fmt, ...)
{
    va_list ap;
    va_start(ap, fmt);
    fprintf(stderr, "[%s/%d] ", tag, prio);
    vfprintf(stderr, fmt, ap);
    fputc('\n', stderr);
    va_end(ap);
    return 0;
}
static int __android_log_write(int prio, const char *tag, const char *text)
{
    fprintf(stderr, "[%s/%d] %s\n", tag, prio, text);
    return 0;
}
#endif
#include <dirent.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define TAG "RobustHost"
#define LOGI(...) host_log(ANDROID_LOG_INFO, __VA_ARGS__)
#define LOGE(...) host_log(ANDROID_LOG_ERROR, __VA_ARGS__)

// ---- log file (so the launcher can show what happened without adb) ----

#define LOG_FILE_MAX (2 * 1024 * 1024)
static FILE *g_logfile;
static long g_logsize;
static pthread_mutex_t g_logmutex = PTHREAD_MUTEX_INITIALIZER;

static void logfile_write(const char *line)
{
    if (!g_logfile)
        return;
    pthread_mutex_lock(&g_logmutex);
    if (g_logsize < LOG_FILE_MAX)
    {
        g_logsize += fprintf(g_logfile, "%s\n", line);
        if (g_logsize >= LOG_FILE_MAX)
            fputs("[log truncated]\n", g_logfile);
        fflush(g_logfile);
    }
    pthread_mutex_unlock(&g_logmutex);
}

static void host_log(int prio, const char *fmt, ...)
{
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    __android_log_write(prio, TAG, buf);
    char line[2100];
    snprintf(line, sizeof(line), "[host] %s", buf);
    logfile_write(line);
}

typedef void MonoDomain;
typedef void MonoAssembly;

typedef int (*monovm_initialize_fn)(int, const char **, const char **);
typedef MonoDomain *(*mono_jit_init_version_fn)(const char *, const char *);
typedef void (*mono_jit_set_aot_mode_fn)(int);
#define MONO_AOT_MODE_NORMAL 1
typedef MonoAssembly *(*mono_domain_assembly_open_fn)(MonoDomain *, const char *);
typedef int (*mono_jit_exec_fn)(MonoDomain *, MonoAssembly *, int, char **);
typedef void (*mono_set_chaining_fn)(int);
typedef void (*mono_jit_cleanup_fn)(MonoDomain *);

// ---- stdout/stderr → logcat ----

static int g_pipe[2];

static void *log_thread(void *arg)
{
    (void)arg;
    char buf[4096];
    size_t len = 0;
    for (;;)
    {
        ssize_t r = read(g_pipe[0], buf + len, sizeof(buf) - 1 - len);
        if (r <= 0)
            break;
        len += (size_t)r;
        buf[len] = 0;
        char *start = buf;
        char *nl;
        while ((nl = strchr(start, '\n')) != NULL)
        {
            *nl = 0;
            __android_log_write(ANDROID_LOG_INFO, "RobustGame", start);
            logfile_write(start);
            start = nl + 1;
        }
        len = strlen(start);
        memmove(buf, start, len);
        if (len == sizeof(buf) - 1)
        {
            buf[len] = 0;
            __android_log_write(ANDROID_LOG_INFO, "RobustGame", buf);
            logfile_write(buf);
            len = 0;
        }
    }
    return NULL;
}

static void redirect_stdio(void)
{
#ifndef __ANDROID__
    return;
#endif
    setvbuf(stdout, NULL, _IOLBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    if (pipe(g_pipe) != 0)
        return;
    dup2(g_pipe[1], STDOUT_FILENO);
    dup2(g_pipe[1], STDERR_FILENO);
    pthread_t t;
    pthread_create(&t, NULL, log_thread, NULL);
    pthread_detach(t);
}

// ---- helpers ----

static char *join_path(const char *a, const char *b)
{
    size_t la = strlen(a), lb = strlen(b);
    char *r = malloc(la + lb + 2);
    memcpy(r, a, la);
    r[la] = '/';
    memcpy(r + la + 1, b, lb + 1);
    return r;
}

// Builds the TRUSTED_PLATFORM_ASSEMBLIES list: every .dll in dir, ':'-separated, plus extra.
static char *build_tpa(const char *dir, const char *extra)
{
    size_t cap = 65536, len = 0;
    char *out = malloc(cap);
    out[0] = 0;
    DIR *d = opendir(dir);
    if (d)
    {
        struct dirent *e;
        while ((e = readdir(d)) != NULL)
        {
            size_t n = strlen(e->d_name);
            if (n < 5 || strcmp(e->d_name + n - 4, ".dll") != 0)
                continue;
            size_t need = strlen(dir) + n + 3;
            if (len + need >= cap)
            {
                cap *= 2;
                out = realloc(out, cap);
            }
            len += (size_t)sprintf(out + len, "%s%s/%s", len ? ":" : "", dir, e->d_name);
        }
        closedir(d);
    }
    size_t ne = strlen(extra);
    if (len + ne + 2 >= cap)
        out = realloc(out, len + ne + 2);
    sprintf(out + len, "%s%s", len ? ":" : "", extra);
    return out;
}

static void write_crash_detail(const char *path, const char *msg, const char *detail)
{
    FILE *f = fopen(path, "w");
    if (!f)
        return;
    fprintf(f, "%s\n\n%s\n", msg, detail ? detail : "");
    fclose(f);
}

static void write_crash(const char *path, const char *msg)
{
    FILE *f = fopen(path, "w");
    if (!f)
        return;
    fputs(msg, f);
    fclose(f);
}

#define LOAD_SYM(var, type, name)                                                                                     \
    type var = (type)dlsym(mono, name);                                                                               \
    if (!var)                                                                                                         \
    {                                                                                                                 \
        LOGE("Missing symbol %s", name);                                                                              \
        write_crash(crash, "Среда .NET повреждена: нет функции " name);                                               \
        return 1;                                                                                                     \
    }

__attribute__((visibility("default"))) int SDL_main(int argc, char **argv)
{
    redirect_stdio();

    if (argc < 7)
    {
        LOGE("Expected 6 arguments, got %d", argc - 1);
        return 1;
    }

    const char *runtime_dir = argv[1];
    const char *host_dll = argv[2];
    const char *spec = argv[3];
    const char *crash = argv[4];
    const char *app_native_dir = argv[5];
    const char *config = argv[6];

    if (argc >= 8 && argv[7][0])
        g_logfile = fopen(argv[7], "w");

#ifdef __ANDROID__
    char model[PROP_VALUE_MAX] = "", release[PROP_VALUE_MAX] = "", abi[PROP_VALUE_MAX] = "";
    __system_property_get("ro.product.model", model);
    __system_property_get("ro.build.version.release", release);
    __system_property_get("ro.product.cpu.abi", abi);
    LOGI("Device: %s, Android %s, %s", model, release, abi);
#endif
    // The launcher uses this to pick this run's lines out of logcat.
    LOGI("PID: %d", (int)getpid());

    // Mono's GC: concurrent mark-sweep and a soft heap limit (~30% of RAM) so major collections happen before
    // Android kills the game for memory. Without it the managed heap grew past 2 GB on a 4 GB phone.
    {
        long mem_kb = 0;
        FILE *mi = fopen("/proc/meminfo", "r");
        if (mi)
        {
            char line[256];
            while (fgets(line, sizeof(line), mi))
                if (sscanf(line, "MemTotal: %ld kB", &mem_kb) == 1)
                    break;
            fclose(mi);
        }
        long limit_mb = mem_kb > 0 ? mem_kb / 1024 * 3 / 10 : 1024;
        if (limit_mb < 512)
            limit_mb = 512;
        char gc[128];
        snprintf(gc, sizeof(gc), "major=marksweep-conc,soft-heap-limit=%ldm", limit_mb);
        setenv("MONO_GC_PARAMS", gc, 0);
        LOGI("MONO_GC_PARAMS=%s (RAM %ld MB)", getenv("MONO_GC_PARAMS"), mem_kb / 1024);
    }

    // SDL reads hints from the environment and SDL_Init has not run yet.
    // Without this SDL picks the orientation from the window size and overrides the manifest.
    setenv("SDL_ORIENTATIONS", "LandscapeLeft LandscapeRight", 1);
    // OpenAL Soft config: no HRTF (its mixer crashed with SIGSEGV on a phone), a cheap resampler and a
    // larger period for slow CPUs. Written next to the log file, i.e. in the app's files dir.
    // Pointing ALSOFT_CONF at it also stops OpenAL Soft from walking "/" for config files.
    if (argc >= 8 && argv[7][0])
    {
        char conf[4096];
        snprintf(conf, sizeof(conf), "%s", argv[7]);
        char *cslash = strrchr(conf, '/');
        if (cslash)
        {
            snprintf(cslash + 1, sizeof(conf) - (size_t)(cslash + 1 - conf), "alsoft.conf");
            FILE *cf = fopen(conf, "w");
            if (cf)
            {
                fputs("[general]\nhrtf = false\nresampler = linear\nperiod_size = 1024\nperiods = 3\n", cf);
                fclose(cf);
                setenv("ALSOFT_CONF", conf, 1);
            }
        }
    }
    if (!getenv("ALSOFT_CONF"))
        setenv("ALSOFT_CONF", "/dev/null", 1);

    LOGI("Runtime: %s", runtime_dir);
    LOGI("Host: %s", host_dll);
    char *native_dir = join_path(runtime_dir, "native");
    char *lib_dir = join_path(runtime_dir, "lib");

    char *mono_path = join_path(native_dir, "libmonosgen-2.0.so");
    void *mono = dlopen(mono_path, RTLD_NOW | RTLD_GLOBAL);
    if (!mono)
    {
        const char *err = dlerror();
        LOGE("dlopen %s: %s", mono_path, err);
        write_crash_detail(crash, "Не удалось загрузить среду .NET (libmonosgen-2.0.so).", err);
        return 1;
    }

    // Runtime components are looked up by name; preload them so the lookup finds them.
    const char *components[] = {"libmono-component-marshal-ilgen.so", "libmono-component-hot_reload.so",
                                "libmono-component-diagnostics_tracing.so", "libmono-component-debugger.so"};
    for (size_t i = 0; i < sizeof(components) / sizeof(components[0]); i++)
    {
        char *p = join_path(native_dir, components[i]);
        if (access(p, R_OK) == 0 && !dlopen(p, RTLD_NOW | RTLD_GLOBAL))
            LOGE("dlopen %s: %s", p, dlerror());
        free(p);
    }

    // The APK also contains libSystem.*.so of the app's own .NET version, and the dynamic linker finds those
    // first when the runtime loads them by name. Preload ours by full path: later loads by name then match
    // the already-loaded library (same soname) instead of the APK copy.
    const char *runtime_natives[] = {"libSystem.Native.so", "libSystem.IO.Compression.Native.so",
                                     "libSystem.Globalization.Native.so",
                                     "libSystem.Security.Cryptography.Native.Android.so"};
    for (size_t i = 0; i < sizeof(runtime_natives) / sizeof(runtime_natives[0]); i++)
    {
        char *p = join_path(native_dir, runtime_natives[i]);
        if (access(p, R_OK) == 0 && !dlopen(p, RTLD_NOW | RTLD_GLOBAL))
            LOGE("dlopen %s: %s", p, dlerror());
        free(p);
    }

    LOAD_SYM(monovm_initialize, monovm_initialize_fn, "monovm_initialize")
    LOAD_SYM(mono_jit_init_version, mono_jit_init_version_fn, "mono_jit_init_version")
    LOAD_SYM(mono_domain_assembly_open, mono_domain_assembly_open_fn, "mono_domain_assembly_open")
    LOAD_SYM(mono_jit_exec, mono_jit_exec_fn, "mono_jit_exec")
    mono_set_chaining_fn set_signal_chaining = (mono_set_chaining_fn)dlsym(mono, "mono_set_signal_chaining");
    mono_set_chaining_fn set_crash_chaining = (mono_set_chaining_fn)dlsym(mono, "mono_set_crash_chaining");

    // ART uses signals too (e.g. SIGSEGV for null checks): chain to its handlers like .NET for Android does.
    if (set_signal_chaining)
        set_signal_chaining(1);
    if (set_crash_chaining)
        set_crash_chaining(1);

    // Runtime properties.
    char host_dir[4096];
    strncpy(host_dir, host_dll, sizeof(host_dir) - 2);
    host_dir[sizeof(host_dir) - 2] = 0;
    char *slash = strrchr(host_dir, '/');
    if (slash)
        slash[1] = 0;

    char *tpa = build_tpa(lib_dir, host_dll);
    size_t nd_len = strlen(native_dir) + strlen(app_native_dir) + 4;
    char *native_search = malloc(nd_len);
    snprintf(native_search, nd_len, "%s:%s", native_dir, app_native_dir);

    const char *keys[64];
    const char *values[64];
    int n = 0;
    keys[n] = "TRUSTED_PLATFORM_ASSEMBLIES"; values[n++] = tpa;
    keys[n] = "NATIVE_DLL_SEARCH_DIRECTORIES"; values[n++] = native_search;
    keys[n] = "APP_CONTEXT_BASE_DIRECTORY"; values[n++] = host_dir;
    keys[n] = "RUNTIME_IDENTIFIER"; values[n++] =
#if defined(__aarch64__)
        "android-arm64";
#elif defined(__x86_64__) && defined(__ANDROID__)
        "android-x64";
#elif defined(__x86_64__)
        "linux-x64";
#else
        "android";
#endif
    // .NET on Android uses the system ICU (the runtime package has none). Use it when the system exposes it;
    // otherwise fall back to invariant mode but still allow creating any culture (e.g. "ru-RU" for content
    // localization), which then simply formats like the invariant culture.
    void *icu = dlopen("libicu.so", RTLD_NOW);
    if (!icu)
        icu = dlopen("libicuuc.so", RTLD_NOW);
    if (icu)
    {
        dlclose(icu);
        LOGI("ICU: system");
    }
    else
    {
        LOGI("ICU: none, invariant globalization mode");
        keys[n] = "System.Globalization.Invariant"; values[n++] = "true";
        keys[n] = "System.Globalization.PredefinedCulturesOnly"; values[n++] = "false";
    }

    // Engine runtimeconfig.json properties ("key=value" lines).
    char *cfg = strdup(config);
    for (char *line = strtok(cfg, "\n"); line && n < 60; line = strtok(NULL, "\n"))
    {
        char *eq = strchr(line, '=');
        if (!eq)
            continue;
        *eq = 0;
        keys[n] = line;
        values[n++] = eq + 1;
    }

    // Precompiled code: Mono loads "<assembly>.dll.so" next to an assembly when AOT mode is on, and JIT-compiles
    // whatever has no (matching) image. argv[8] == "aot" enables it.
    int use_aot = argc >= 9 && strcmp(argv[8], "aot") == 0;
    if (use_aot)
    {
        mono_jit_set_aot_mode_fn set_aot_mode = (mono_jit_set_aot_mode_fn)dlsym(mono, "mono_jit_set_aot_mode");
        if (set_aot_mode)
        {
            setenv("MONO_LOG_MASK", "aot", 1);
            setenv("MONO_LOG_LEVEL", "info", 1);
            set_aot_mode(MONO_AOT_MODE_NORMAL);
            LOGI("AOT: enabled");
        }
        else
            LOGI("AOT: mono_jit_set_aot_mode not exported, JIT only");
    }
    else
        LOGI("AOT: disabled");

    if (monovm_initialize(n, keys, values) != 0)
    {
        LOGE("monovm_initialize failed");
        write_crash(crash, "Не удалось инициализировать среду .NET.");
        return 1;
    }

    MonoDomain *domain = mono_jit_init_version("Robust", "v4.0.30319");
    if (!domain)
    {
        LOGE("mono_jit_init_version failed");
        write_crash(crash, "Не удалось запустить среду .NET.");
        return 1;
    }

    MonoAssembly *asm_ = mono_domain_assembly_open(domain, host_dll);
    if (!asm_)
    {
        LOGE("Cannot open %s", host_dll);
        write_crash_detail(crash, "Не удалось загрузить Robust.Android.GameHost.dll.", host_dll);
        return 1;
    }

    char *exec_argv[] = {(char *)host_dll, (char *)spec, (char *)crash, NULL};
    LOGI("Starting game host");
    int ret = mono_jit_exec(domain, asm_, 3, exec_argv);
    LOGI("Game host exited with %d", ret);

    // Mono cannot be shut down and restarted in-process; GameActivity kills the process when it finishes.
    fflush(stdout);
    fflush(stderr);
    return ret;
}

#ifdef ROBUST_HOST_TEST
int main(int argc, char **argv) { return SDL_main(argc, argv); }
#endif

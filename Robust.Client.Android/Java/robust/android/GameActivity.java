package robust.android;

import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.util.TypedValue;
import android.view.Gravity;
import android.widget.FrameLayout;
import android.widget.TextView;

import org.libsdl.app.SDLActivity;

import java.io.File;
import java.io.RandomAccessFile;

/**
 * Hosts the engine in its own process (":game"). Deliberately plain Java: this process must not start the
 * app's own .NET runtime, because the native host (libmain.so, robust_host.c) starts the Mono runtime of the
 * .NET version the engine was built for.
 */
public class GameActivity extends SDLActivity {
    public static final String EXTRA_RUNTIME_DIR = "runtime_dir";
    public static final String EXTRA_HOST_DLL = "host_dll";
    public static final String EXTRA_SPEC = "spec";
    public static final String EXTRA_CRASH = "crash";
    public static final String EXTRA_RUNTIME_CONFIG = "runtime_config";
    public static final String EXTRA_AOT = "aot";
    public static final String EXTRA_TOUCH = "touch";
    public static final String EXTRA_TOUCH_OPACITY = "touch_opacity";
    public static final String EXTRA_TOUCH_SCALE = "touch_scale";

    private static final String TAG = "RobustGame";

    private TextView status;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private boolean engineStarted;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        Intent intent = getIntent();
        String runtimeDir = intent.getStringExtra(EXTRA_RUNTIME_DIR);

        // .NET's crypto (TLS for account login) is a JNI library; load it from Java so JNI_OnLoad runs
        // with this app's class loader, which contains the net.dot.android.crypto classes.
        if (runtimeDir != null) {
            File crypto = new File(runtimeDir, "native/libSystem.Security.Cryptography.Native.Android.so");
            if (crypto.exists()) {
                try {
                    System.load(crypto.getAbsolutePath());
                } catch (Throwable t) {
                    Log.w(TAG, "Failed to load .NET crypto library", t);
                }
            }
        }

        super.onCreate(savedInstanceState);

        Window window = getWindow();
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        hideSystemBars();

        // Loading status over the (still black) SDL surface, from the game log.
        if (mLayout != null) {
            status = new TextView(this);
            status.setTextColor(Color.argb(220, 232, 238, 246));
            status.setTextSize(TypedValue.COMPLEX_UNIT_SP, 12);
            status.setGravity(Gravity.CENTER_HORIZONTAL | Gravity.BOTTOM);
            status.setPadding(48, 0, 48, 24);
            status.setText("Загрузка движка…");
            mLayout.addView(status, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            handler.postDelayed(this::pollLog, 700);
        }

        if (intent.getBooleanExtra(EXTRA_TOUCH, true) && mLayout != null) {
            TouchControls overlay = new TouchControls(this,
                    intent.getIntExtra(EXTRA_TOUCH_OPACITY, 60),
                    intent.getFloatExtra(EXTRA_TOUCH_SCALE, 1f));
            mLayout.addView(overlay, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        }
    }

    @Override
    protected String[] getLibraries() {
        return new String[]{"SDL3", "main"};
    }

    @Override
    protected String[] getArguments() {
        Intent intent = getIntent();
        return new String[]{
                nonNull(intent.getStringExtra(EXTRA_RUNTIME_DIR)),
                nonNull(intent.getStringExtra(EXTRA_HOST_DLL)),
                nonNull(intent.getStringExtra(EXTRA_SPEC)),
                nonNull(intent.getStringExtra(EXTRA_CRASH)),
                getApplicationInfo().nativeLibraryDir,
                nonNull(intent.getStringExtra(EXTRA_RUNTIME_CONFIG)),
                logFile().getAbsolutePath(),
                intent.getBooleanExtra(EXTRA_AOT, false) ? "aot" : "",
        };
    }

    private File logFile() {
        return new File(getFilesDir(), "last-game.log");
    }

    /** Shows the newest log line until the engine reaches its first run level, and again on fatal errors. */
    private void pollLog() {
        String tail = readTail(logFile(), 4096);
        if (tail != null && status != null) {
            // Once the engine has a GL context it draws its own loading screen.
            if (tail.contains("Runlevel changed") || tail.contains("clyde.ogl") || tail.contains("OpenGL"))
                engineStarted = true;
            boolean fatal = tail.contains("[FATL]") || tail.contains("Fatal:");
            String[] lines = tail.trim().split("\n");
            String last = "";
            for (int i = lines.length - 1; i >= 0; i--) {
                // OpenAL Soft's own warnings are noise here.
                if (!lines[i].startsWith("[ALSOFT]")) {
                    last = lines[i];
                    break;
                }
            }
            if (last.length() > 160)
                last = last.substring(0, 160) + "…";
            if (fatal) {
                status.setVisibility(View.VISIBLE);
                status.setText("Ошибка движка. Подробности: лаунчер → Настройки → Лог последней игры.\n\n" + last);
            } else if (engineStarted) {
                status.setVisibility(View.GONE);
            } else {
                status.setText("Загрузка движка…\n\n" + last);
            }
        }
        handler.postDelayed(this::pollLog, 700);
    }

    private static String readTail(File f, int max) {
        if (!f.exists())
            return null;
        try (RandomAccessFile raf = new RandomAccessFile(f, "r")) {
            long len = raf.length();
            long start = Math.max(0, len - max);
            byte[] buf = new byte[(int) (len - start)];
            raf.seek(start);
            raf.readFully(buf);
            return new String(buf, "UTF-8");
        } catch (Exception e) {
            return null;
        }
    }

    private static String nonNull(String s) {
        return s == null ? "" : s;
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus)
            hideSystemBars();
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent e) {
        // Android "back" behaves like Escape (close windows / game menu) instead of killing the game.
        if (e.getKeyCode() == KeyEvent.KEYCODE_BACK) {
            if (e.getAction() == KeyEvent.ACTION_DOWN)
                onNativeKeyDown(KeyEvent.KEYCODE_ESCAPE);
            else if (e.getAction() == KeyEvent.ACTION_UP)
                onNativeKeyUp(KeyEvent.KEYCODE_ESCAPE);
            return true;
        }
        return super.dispatchKeyEvent(e);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        // The .NET runtime and the engine cannot be restarted in one process: always start clean.
        android.os.Process.killProcess(android.os.Process.myPid());
    }

    @SuppressWarnings("deprecation")
    private void hideSystemBars() {
        Window window = getWindow();
        if (window == null)
            return;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false);
            WindowInsetsController c = window.getInsetsController();
            if (c != null) {
                c.hide(WindowInsets.Type.systemBars());
                c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            window.getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
        }
    }
}

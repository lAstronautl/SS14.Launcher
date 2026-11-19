package robust.android;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.VibrationEffect;
import android.os.Vibrator;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.InputDevice;
import android.view.KeyEvent;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.GridLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.libsdl.app.SDLActivity;

import java.util.ArrayList;
import java.util.List;

/**
 * On-screen controls drawn over the SDL surface. Everything becomes ordinary keyboard and mouse input through
 * SDL's Java entry points, so the engine sees a normal keyboard + mouse and every keybind works.
 * Real mice and keyboards bypass this overlay.
 */
final class TouchControls extends FrameLayout {
    // android.view.MotionEvent constants as used by SDLActivity.onNativeMouse.
    private static final int BUTTON_PRIMARY = 1;
    private static final int BUTTON_SECONDARY = 2;
    private static final int ACTION_DOWN = 0;
    private static final int ACTION_UP = 1;
    private static final int ACTION_MOVE = 2;
    private static final int ACTION_SCROLL = 8;

    private static final long LONG_PRESS_MS = 450;

    private final float slop;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Vibrator vibrator;

    private int pointerId = -1;
    private float downX, downY;
    private boolean dragging, longPressFired, scrolling;
    private float scrollLastY;
    private Runnable longPressAction;

    private boolean rightClickMode;
    private ToggleKey rmbToggle;
    private final List<View> hideable = new ArrayList<>();
    private boolean hidden;
    private final float density;
    private final float scale;

    TouchControls(Context ctx, int opacityPercent, float scale) {
        super(ctx);
        this.scale = scale;
        density = ctx.getResources().getDisplayMetrics().density;
        slop = ViewConfiguration.get(ctx).getScaledTouchSlop() * 1.5f;
        vibrator = (Vibrator) ctx.getSystemService(Context.VIBRATOR_SERVICE);
        float alpha = Math.max(15, Math.min(100, opacityPercent)) / 100f;

        // --- Movement joystick (bottom left) ---
        Joystick joy = new Joystick(ctx);
        joy.setAlpha(alpha);
        LayoutParams joyLp = new LayoutParams(dp(170), dp(170), Gravity.BOTTOM | Gravity.LEFT);
        joyLp.setMargins(dp(24), 0, 0, dp(24));
        addView(joy, joyLp);
        hideable.add(joy);

        // --- Action buttons (bottom right), default SS14 binds ---
        GridLayout grid = new GridLayout(ctx);
        grid.setColumnCount(3);
        grid.setAlpha(alpha);
        addKey(grid, "Исп.", KeyEvent.KEYCODE_Z);     // ActivateItemInHand
        addKey(grid, "Акт.", KeyEvent.KEYCODE_E);     // ActivateItemInWorld
        addKey(grid, "Рука", KeyEvent.KEYCODE_X);     // SwapHands
        addKey(grid, "Брос.", KeyEvent.KEYCODE_Q);    // Drop
        addKey(grid, "Отпуст.", KeyEvent.KEYCODE_H);  // ReleasePulledObject
        addKey(grid, "Инв.", KeyEvent.KEYCODE_I);     // OpenInventoryMenu
        LayoutParams gridLp = new LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.RIGHT);
        gridLp.setMargins(0, 0, dp(16), dp(16));
        addView(grid, gridLp);
        hideable.add(grid);

        // --- Modifiers: a row right above the action grid ---
        LinearLayout side = new LinearLayout(ctx);
        side.setOrientation(LinearLayout.HORIZONTAL);
        side.setAlpha(alpha);
        rmbToggle = new ToggleKey(ctx, "ПКМ", 0, on -> rightClickMode = on);
        addSide(side, rmbToggle);
        addSide(side, new ToggleKey(ctx, "Shift", KeyEvent.KEYCODE_SHIFT_LEFT, null)); // + tap = examine
        addSide(side, new ToggleKey(ctx, "Ctrl", KeyEvent.KEYCODE_CTRL_LEFT, null));   // + tap = pull
        addSide(side, new ToggleKey(ctx, "Alt", KeyEvent.KEYCODE_ALT_LEFT, null));     // + tap = alt-activate
        LayoutParams sideLp = new LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM | Gravity.RIGHT);
        // Action grid: 2 rows of 64dp keys with 4dp margins, 16dp from the bottom.
        sideLp.setMargins(0, 0, dp(16), dp(16 + 2 * 72 + 6));
        addView(side, sideLp);
        hideable.add(side);

        // --- Top bar ---
        LinearLayout top = new LinearLayout(ctx);
        top.setOrientation(LinearLayout.HORIZONTAL);
        top.setAlpha(alpha);
        addTop(top, new KeyButton(ctx, "Esc", KeyEvent.KEYCODE_ESCAPE, null));
        addTop(top, new KeyButton(ctx, "Чат", KeyEvent.KEYCODE_T, null));          // FocusChat
        addTop(top, new KeyButton(ctx, "Tab", KeyEvent.KEYCODE_TAB, null));        // cycle chat channel
        addTop(top, new KeyButton(ctx, "−", KeyEvent.KEYCODE_NUMPAD_4, null));     // ZoomOut
        addTop(top, new KeyButton(ctx, "+", KeyEvent.KEYCODE_NUMPAD_6, null));     // ZoomIn
        LayoutParams topLp = new LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP | Gravity.LEFT);
        topLp.setMargins(dp(8), dp(8), 0, 0);
        addView(top, topLp);
        hideable.add(top);

        // Always visible: hide/show the controls (e.g. with a physical keyboard + mouse).
        KeyButton hide = new KeyButton(ctx, "◐", 0, () -> {
            hidden = !hidden;
            for (View v : hideable)
                v.setVisibility(hidden ? View.GONE : View.VISIBLE);
        });
        hide.setAlpha(alpha);
        LayoutParams hideLp = new LayoutParams(dp(44), dp(44), Gravity.TOP | Gravity.RIGHT);
        hideLp.setMargins(0, dp(8), dp(8), 0);
        addView(hide, hideLp);
    }

    private int dp(float v) {
        return (int) (v * scale * density);
    }

    private void addKey(GridLayout grid, String label, int key) {
        GridLayout.LayoutParams lp = new GridLayout.LayoutParams();
        lp.width = dp(64);
        lp.height = dp(64);
        lp.setMargins(dp(4), dp(4), dp(4), dp(4));
        grid.addView(new KeyButton(getContext(), label, key, null), lp);
    }

    private void addSide(LinearLayout side, View v) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(52), dp(40));
        lp.setMargins(dp(3), dp(3), dp(3), dp(3));
        side.addView(v, lp);
    }

    private void addTop(LinearLayout top, View v) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(dp(56), dp(44));
        lp.setMargins(dp(3), dp(3), dp(3), dp(3));
        top.addView(v, lp);
    }

    @SuppressWarnings("deprecation")
    private void vibrate() {
        try {
            if (vibrator == null)
                return;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                vibrator.vibrate(VibrationEffect.createOneShot(20, VibrationEffect.DEFAULT_AMPLITUDE));
            else
                vibrator.vibrate(20);
        } catch (Throwable ignored) {
        }
    }

    // ------------------------------------------------------------------ free-area gestures

    @Override
    public boolean dispatchTouchEvent(MotionEvent e) {
        // Real mice go straight to SDL.
        if (e.isFromSource(InputDevice.SOURCE_MOUSE) || e.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE)
            return false;

        // Buttons / joystick handle touches that start on them.
        if (super.dispatchTouchEvent(e))
            return true;

        return handleFreeTouch(e);
    }

    private boolean handleFreeTouch(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN: {
                pointerId = e.getPointerId(0);
                downX = e.getX();
                downY = e.getY();
                dragging = false;
                longPressFired = false;
                scrolling = false;
                mouse(0, ACTION_MOVE, downX, downY);
                final float x = downX, y = downY;
                longPressAction = () -> {
                    if (dragging || scrolling || pointerId < 0)
                        return;
                    longPressFired = true;
                    vibrate();
                    click(BUTTON_SECONDARY, x, y);
                };
                handler.postDelayed(longPressAction, LONG_PRESS_MS);
                return true;
            }
            case MotionEvent.ACTION_POINTER_DOWN: {
                // Second finger: two-finger drag scrolls.
                cancelPendingLongPress();
                if (dragging) {
                    mouse(0, ACTION_UP, e.getX(0), e.getY(0));
                    dragging = false;
                }
                scrolling = true;
                scrollLastY = averageY(e);
                return true;
            }
            case MotionEvent.ACTION_MOVE: {
                if (scrolling) {
                    float y = averageY(e);
                    float step = 40 * density;
                    while (Math.abs(y - scrollLastY) >= step) {
                        float dir = y > scrollLastY ? 1f : -1f;
                        mouse(0, ACTION_SCROLL, 0, dir);
                        scrollLastY += dir * step;
                    }
                    return true;
                }
                int idx = e.findPointerIndex(pointerId);
                if (idx < 0)
                    return true;
                float mx = e.getX(idx), my = e.getY(idx);
                if (!dragging && !longPressFired && Math.abs(mx - downX) + Math.abs(my - downY) > slop) {
                    // Drag with the left button held: moving items, selecting, dragging windows.
                    cancelPendingLongPress();
                    dragging = true;
                    mouse(BUTTON_PRIMARY, ACTION_DOWN, downX, downY);
                }
                mouse(0, ACTION_MOVE, mx, my);
                return true;
            }
            case MotionEvent.ACTION_UP: {
                cancelPendingLongPress();
                float x = e.getX(), y = e.getY();
                if (scrolling) {
                    // nothing
                } else if (dragging) {
                    mouse(0, ACTION_UP, x, y);
                } else if (!longPressFired) {
                    click(rightClickMode ? BUTTON_SECONDARY : BUTTON_PRIMARY, x, y);
                    if (rightClickMode && rmbToggle != null)
                        rmbToggle.setOn(false);
                }
                pointerId = -1;
                dragging = false;
                scrolling = false;
                return true;
            }
            case MotionEvent.ACTION_CANCEL: {
                cancelPendingLongPress();
                if (dragging)
                    mouse(0, ACTION_UP, e.getX(), e.getY());
                pointerId = -1;
                dragging = false;
                scrolling = false;
                return true;
            }
        }
        return true;
    }

    private static float averageY(MotionEvent e) {
        float sum = 0;
        for (int i = 0; i < e.getPointerCount(); i++)
            sum += e.getY(i);
        return sum / e.getPointerCount();
    }

    private void cancelPendingLongPress() {
        if (longPressAction != null)
            handler.removeCallbacks(longPressAction);
        longPressAction = null;
    }

    private static void click(int button, float x, float y) {
        mouse(0, ACTION_MOVE, x, y);
        mouse(button, ACTION_DOWN, x, y);
        mouse(0, ACTION_UP, x, y);
    }

    /**
     * SDL's Android_OnMouse takes the mouse *button state* (like MotionEvent.getButtonState):
     * on DOWN the buttons now held, on UP the buttons still held after the release (0 here).
     */
    private static void mouse(int button, int action, float x, float y) {
        SDLActivity.onNativeMouse(button, action, x, y, false);
    }

    private static void key(int keycode, boolean down) {
        if (down)
            SDLActivity.onNativeKeyDown(keycode);
        else
            SDLActivity.onNativeKeyUp(keycode);
    }

    private static GradientDrawable buttonBackground(int color) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setStroke(2, Color.argb(180, 140, 160, 190));
        d.setCornerRadius(18);
        return d;
    }

    // ------------------------------------------------------------------ widgets

    /** Press-and-hold key (keycode 0 = no key), optionally with a tap action. */
    private static final class KeyButton extends TextView {
        private final int keycode;
        private final Runnable onTap;

        KeyButton(Context ctx, String label, int keycode, Runnable onTap) {
            super(ctx);
            this.keycode = keycode;
            this.onTap = onTap;
            setText(label);
            setGravity(Gravity.CENTER);
            setTextColor(Color.WHITE);
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
            setBackground(buttonBackground(Color.argb(150, 20, 28, 40)));
        }

        @Override
        public boolean onTouchEvent(MotionEvent e) {
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    setBackground(buttonBackground(Color.argb(200, 79, 195, 247)));
                    if (keycode != 0) key(keycode, true);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    setBackground(buttonBackground(Color.argb(150, 20, 28, 40)));
                    if (keycode != 0) key(keycode, false);
                    if (e.getActionMasked() == MotionEvent.ACTION_UP && onTap != null) onTap.run();
                    return true;
            }
            return true;
        }
    }

    interface ToggleListener {
        void changed(boolean on);
    }

    /** Sticky modifier: tap to hold the key down, tap again to release. */
    private static final class ToggleKey extends TextView {
        private final int keycode;
        private final ToggleListener listener;
        private boolean on;

        ToggleKey(Context ctx, String label, int keycode, ToggleListener listener) {
            super(ctx);
            this.keycode = keycode;
            this.listener = listener;
            setText(label);
            setGravity(Gravity.CENTER);
            setTextColor(Color.WHITE);
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
            redraw();
        }

        void setOn(boolean value) {
            if (on == value) return;
            on = value;
            if (keycode != 0) key(keycode, on);
            if (listener != null) listener.changed(on);
            redraw();
        }

        private void redraw() {
            setBackground(buttonBackground(on ? Color.argb(220, 255, 183, 77) : Color.argb(150, 20, 28, 40)));
        }

        @Override
        public boolean onTouchEvent(MotionEvent e) {
            if (e.getActionMasked() == MotionEvent.ACTION_UP)
                setOn(!on);
            return true;
        }
    }

    /** 8-way movement stick mapped to W/A/S/D. */
    private static final class Joystick extends View {
        private static final int[] KEYS = {KeyEvent.KEYCODE_W, KeyEvent.KEYCODE_A, KeyEvent.KEYCODE_S, KeyEvent.KEYCODE_D};
        private final boolean[] held = new boolean[4];
        private final Paint base = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint ring = new Paint(Paint.ANTI_ALIAS_FLAG);
        private final Paint knob = new Paint(Paint.ANTI_ALIAS_FLAG);
        private float kx, ky;
        private boolean active;

        Joystick(Context ctx) {
            super(ctx);
            base.setColor(Color.argb(110, 20, 28, 40));
            ring.setColor(Color.argb(170, 140, 160, 190));
            ring.setStyle(Paint.Style.STROKE);
            ring.setStrokeWidth(4);
            knob.setColor(Color.argb(200, 79, 195, 247));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            float cx = getWidth() / 2f, cy = getHeight() / 2f;
            float r = Math.min(cx, cy) - 4;
            canvas.drawCircle(cx, cy, r, base);
            canvas.drawCircle(cx, cy, r, ring);
            canvas.drawCircle(active ? kx : cx, active ? ky : cy, r * 0.38f, knob);
        }

        @Override
        public boolean onTouchEvent(MotionEvent e) {
            float cx = getWidth() / 2f, cy = getHeight() / 2f;
            float r = Math.min(cx, cy);
            switch (e.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                case MotionEvent.ACTION_MOVE: {
                    float dx = e.getX() - cx, dy = e.getY() - cy;
                    float len = (float) Math.sqrt(dx * dx + dy * dy);
                    if (len > r) {
                        dx = dx / len * r;
                        dy = dy / len * r;
                    }
                    active = true;
                    kx = cx + dx;
                    ky = cy + dy;
                    boolean[] want = new boolean[4];
                    if (len > r * 0.3f) {
                        // Each direction covers 135° so diagonals press two keys.
                        double angle = Math.toDegrees(Math.atan2(-dy, dx)); // 0 = right, 90 = up
                        want[0] = angle > 22.5 && angle < 157.5;     // up
                        want[2] = angle < -22.5 && angle > -157.5;   // down
                        want[1] = angle > 112.5 || angle < -112.5;   // left
                        want[3] = angle > -67.5 && angle < 67.5;     // right
                    }
                    apply(want);
                    invalidate();
                    return true;
                }
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    active = false;
                    apply(new boolean[4]);
                    invalidate();
                    return true;
            }
            return true;
        }

        private void apply(boolean[] want) {
            for (int i = 0; i < 4; i++) {
                if (want[i] == held[i]) continue;
                held[i] = want[i];
                key(KEYS[i], want[i]);
            }
        }
    }
}

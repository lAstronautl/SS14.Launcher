using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace Robust.Client.Android.Launcher;

/// <summary>
/// Launcher look, following the official SS14 launcher theme (SS14.Launcher/Theme/ThemeResources.xaml):
/// dark grey background, slate buttons with two cut corners ("AngleBox"), green selection, gold headings,
/// diagonal "stripeback" header, Noto Sans.
/// </summary>
internal static class Ui
{
    // Colors from ThemeResources.xaml.
    public static readonly Color Bg = Color.ParseColor("#25252A");
    public static readonly Color PopupBg = Color.ParseColor("#202025");
    public static readonly Color Text = Color.ParseColor("#EEEEEE");
    public static readonly Color Muted = Color.ParseColor("#AAAAAA");
    public static readonly Color Disabled = Color.ParseColor("#666666");
    public static readonly Color ControlMid = Color.ParseColor("#464966");
    public static readonly Color ControlHigh = Color.ParseColor("#3E6C45");
    public static readonly Color Hovered = Color.ParseColor("#575B7F");
    public static readonly Color NanoGold = Color.ParseColor("#ADA24B");
    public static readonly Color StripeEdge = Color.ParseColor("#525252");
    public static readonly Color ListRowA = Color.ParseColor("#1E1E22");
    public static readonly Color ListRowB = Color.ParseColor("#262626");
    public static readonly Color SeparatorColor = Color.ParseColor("#AA575B7F");
    public static readonly Color Good = Color.ParseColor("#78B85A");
    public static readonly Color Bad = Color.ParseColor("#D9534F");

    // Aliases used across the launcher.
    public static Color Panel => ControlMid;
    public static Color Accent => ControlHigh;

    private static Typeface? _regular, _bold;

    public static Typeface Regular(Context ctx) =>
        _regular ??= TryFont(ctx, "fonts/NotoSans-Regular.ttf") ?? Typeface.Default!;

    public static Typeface Bold(Context ctx) =>
        _bold ??= TryFont(ctx, "fonts/NotoSans-Bold.ttf") ?? Typeface.DefaultBold!;

    private static Typeface? TryFont(Context ctx, string path)
    {
        try
        {
            return Typeface.CreateFromAsset(ctx.Assets, path);
        }
        catch
        {
            return null;
        }
    }

    public static int Dp(Context ctx, float dp) => (int)(dp * ctx.Resources!.DisplayMetrics!.Density + 0.5f);

    // ------------------------------------------------------------------ drawables

    /// <summary>Rectangle with the top-right and bottom-left corners cut, like the launcher's AngleBox.</summary>
    public sealed class AngleDrawable : Drawable
    {
        private readonly Paint _fill = new() { AntiAlias = true };
        private readonly Paint? _stroke;
        private readonly float _corner;
        private readonly bool _openLeft, _openRight;

        public AngleDrawable(Color fill, float corner, bool openLeft = false, bool openRight = false,
            Color? stroke = null, float strokeWidth = 2)
        {
            _fill.Color = fill;
            _corner = corner;
            _openLeft = openLeft;
            _openRight = openRight;
            if (stroke is { } s)
            {
                _stroke = new Paint { AntiAlias = true, Color = s, StrokeWidth = strokeWidth };
                _stroke.SetStyle(Paint.Style.Stroke);
            }
        }

        public override void Draw(Canvas canvas)
        {
            var b = Bounds;
            float w = b.Width(), h = b.Height(), c = Math.Min(_corner, Math.Min(w, h) / 2);
            using var path = new global::Android.Graphics.Path();
            path.MoveTo(b.Left, b.Top);
            if (_openRight)
            {
                path.LineTo(b.Left + w, b.Top);
            }
            else
            {
                path.LineTo(b.Left + w - c, b.Top);
                path.LineTo(b.Left + w, b.Top + c);
            }

            path.LineTo(b.Left + w, b.Top + h);
            if (_openLeft)
            {
                path.LineTo(b.Left, b.Top + h);
            }
            else
            {
                path.LineTo(b.Left + c, b.Top + h);
                path.LineTo(b.Left, b.Top + h - c);
            }

            path.Close();
            canvas.DrawPath(path, _fill);
            if (_stroke != null)
                canvas.DrawPath(path, _stroke);
        }

        public override void SetAlpha(int alpha) => _fill.Alpha = alpha;
        public override void SetColorFilter(ColorFilter? colorFilter) => _fill.SetColorFilter(colorFilter);
#pragma warning disable CS0672
        public override int Opacity => (int)Format.Translucent;
#pragma warning restore CS0672
    }

    /// <summary>Diagonal stripes (#1E1E22 / #262626, 32px tiles) with edge lines: the launcher's "stripeback".</summary>
    public sealed class StripeDrawable : Drawable
    {
        private readonly Paint _bg = new() { Color = ListRowA };
        private readonly Paint _stripe = new() { AntiAlias = true, Color = ListRowB };
        private readonly Paint _edge = new() { Color = StripeEdge, StrokeWidth = 3 };
        private readonly float _tile;

        public StripeDrawable(float tile) => _tile = tile;

        public override void Draw(Canvas canvas)
        {
            var b = Bounds;
            canvas.DrawRect(b, _bg);
            canvas.Save();
            canvas.ClipRect(b);
            var t = _tile;
            // Same shapes as the launcher's 32x32 tile, scaled: two parallelograms per tile.
            for (var x = b.Left - t; x < b.Right + t; x += t)
            {
                for (var y = b.Top - t; y < b.Bottom + t; y += t)
                {
                    using var p1 = new global::Android.Graphics.Path();
                    p1.MoveTo(x, y + t * 0.25f);
                    p1.LineTo(x + t * 0.75f, y + t);
                    p1.LineTo(x + t * 0.25f, y + t);
                    p1.LineTo(x, y + t * 0.75f);
                    p1.Close();
                    canvas.DrawPath(p1, _stripe);
                    using var p2 = new global::Android.Graphics.Path();
                    p2.MoveTo(x + t * 0.25f, y);
                    p2.LineTo(x + t * 0.75f, y);
                    p2.LineTo(x + t, y + t * 0.25f);
                    p2.LineTo(x + t, y + t * 0.75f);
                    p2.Close();
                    canvas.DrawPath(p2, _stripe);
                }
            }

            canvas.Restore();
            canvas.DrawLine(b.Left, b.Top + 1, b.Right, b.Top + 1, _edge);
            canvas.DrawLine(b.Left, b.Bottom - 2, b.Right, b.Bottom - 2, _edge);
        }

        public override void SetAlpha(int alpha) { }
        public override void SetColorFilter(ColorFilter? colorFilter) { }
#pragma warning disable CS0672
        public override int Opacity => (int)Format.Opaque;
#pragma warning restore CS0672
    }

    public static Drawable Angle(Context ctx, Color color, float cornerDp = 10, bool openLeft = false,
        bool openRight = false) =>
        new AngleDrawable(color, Dp(ctx, cornerDp), openLeft, openRight);

    /// <summary>Normal / pressed (hover color) / disabled states for a button-like view.</summary>
    public static Drawable AngleStates(Context ctx, Color normal, Color pressed, float cornerDp = 10,
        bool openLeft = false, bool openRight = false)
    {
        var s = new StateListDrawable();
        s.AddState(new[] { -global::Android.Resource.Attribute.StateEnabled },
            Angle(ctx, Color.ParseColor("#34364A"), cornerDp, openLeft, openRight));
        s.AddState(new[] { global::Android.Resource.Attribute.StatePressed }, Angle(ctx, pressed, cornerDp, openLeft, openRight));
        s.AddState(Array.Empty<int>(), Angle(ctx, normal, cornerDp, openLeft, openRight));
        return s;
    }

    // Kept for code that asks for a "rounded" background; the launcher style uses angled boxes instead.
    public static Drawable Rounded(Context ctx, Color color, float radiusDp = 10) => Angle(ctx, color, radiusDp);

    // ------------------------------------------------------------------ widgets

    public static TextView Label(Context ctx, string text, float sp = 15, Color? color = null, bool bold = false)
    {
        var t = new TextView(ctx) { Text = text };
        t.SetTextSize(global::Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(color ?? Text);
        t.Typeface = bold ? Bold(ctx) : Regular(ctx);
        return t;
    }

    /// <summary>Gold section heading, as used for titles in the launcher.</summary>
    public static TextView Heading(Context ctx, string text, float sp = 17) =>
        Label(ctx, text, sp, NanoGold, bold: true);

    public enum ButtonKind
    {
        Normal,
        Primary,
    }

    public static Button Button(Context ctx, string text, Action onClick, bool primary = false,
        bool openLeft = false, bool openRight = false)
    {
        var b = new Button(ctx) { Text = text };
        b.SetAllCaps(false);
        b.SetTextColor(Text);
        b.Typeface = Regular(ctx);
        b.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 15);
        b.StateListAnimator = null;
        b.Background = AngleStates(ctx, primary ? ControlHigh : ControlMid, Hovered, 10, openLeft, openRight);
        b.SetPadding(Dp(ctx, 16), Dp(ctx, 8), Dp(ctx, 16), Dp(ctx, 8));
        b.SetMinHeight(Dp(ctx, 40));
        b.SetMinimumHeight(Dp(ctx, 40));
        b.Click += (_, _) => onClick();
        return b;
    }

    public static EditText Input(Context ctx, string hint, string value = "", bool password = false)
    {
        var e = new EditText(ctx) { Hint = hint, Text = value };
        e.SetTextColor(Text);
        e.SetHintTextColor(Disabled);
        e.Typeface = Regular(ctx);
        e.SetSingleLine(true);
        e.Background = new AngleDrawable(PopupBg, Dp(ctx, 8), stroke: ControlMid, strokeWidth: Dp(ctx, 1.5f));
        e.SetPadding(Dp(ctx, 12), Dp(ctx, 10), Dp(ctx, 12), Dp(ctx, 10));
        if (password)
            e.InputType = global::Android.Text.InputTypes.ClassText | global::Android.Text.InputTypes.TextVariationPassword;
        return e;
    }

    public static CheckBox Check(Context ctx, string text, bool value)
    {
        var c = new CheckBox(ctx) { Text = text, Checked = value };
        c.SetTextColor(Text);
        c.Typeface = Regular(ctx);
        c.ButtonTintList = ColorStateList.ValueOf(ControlHigh);
        return c;
    }

    public static SeekBar Slider(Context ctx, int max, int value)
    {
        var s = new SeekBar(ctx) { Max = max, Progress = value };
        s.ProgressTintList = ColorStateList.ValueOf(ControlHigh);
        s.ThumbTintList = ColorStateList.ValueOf(NanoGold);
        s.ProgressBackgroundTintList = ColorStateList.ValueOf(ControlMid);
        return s;
    }

    /// <summary>Thin separator line like the launcher's list separators.</summary>
    public static View Separator(Context ctx)
    {
        var v = new View(ctx);
        v.SetBackgroundColor(SeparatorColor);
        v.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(ctx, 1));
        return v;
    }

    public static LinearLayout Column(Context ctx, int paddingDp = 0)
    {
        var l = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Vertical };
        var p = Dp(ctx, paddingDp);
        l.SetPadding(p, p, p, p);
        return l;
    }

    public static LinearLayout Row(Context ctx)
    {
        var l = new LinearLayout(ctx) { Orientation = global::Android.Widget.Orientation.Horizontal };
        l.SetGravity(GravityFlags.CenterVertical);
        return l;
    }

    public static LinearLayout.LayoutParams Fill(float weight = 0, int topMarginDp = 0, Context? ctx = null)
    {
        var lp = new LinearLayout.LayoutParams(
            weight > 0 ? 0 : ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent, weight);
        if (ctx != null) lp.TopMargin = Dp(ctx, topMarginDp);
        return lp;
    }

    public static LinearLayout.LayoutParams Wrap(int leftMarginDp = 0, Context? ctx = null)
    {
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        if (ctx != null) lp.LeftMargin = Dp(ctx, leftMarginDp);
        return lp;
    }

    /// <summary>Dialog in the launcher's popup style.</summary>
    public static AlertDialog.Builder Dialog(Context ctx, string title) =>
        new AlertDialog.Builder(ctx, Resource.Style.LauncherDialog).SetTitle(title)!;
}

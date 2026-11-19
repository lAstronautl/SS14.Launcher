using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace Robust.Client.Android.Launcher;

/// <param name="Players">-1 = unknown yet, -2 = offline.</param>
/// <param name="RunLevel">0 = lobby, 1 = in round, 2 = post round (as reported by the server).</param>
public sealed record ServerItem(string Address, string Name, int Players, int MaxPlayers, int? RunLevel = null,
    DateTime? RoundStart = null, string[]? Tags = null);

/// <summary>
/// Server rows as in the official launcher (ServerEntryView): expander arrow, name, round time, players and a
/// "Подключиться" button; tapping a row expands it with the server's details.
/// </summary>
internal sealed class ServerAdapter(
    Context ctx,
    Action<ServerItem> connect,
    Func<ServerItem, View> details) : BaseAdapter<ServerItem>
{
    private List<ServerItem> _items = new();
    private readonly HashSet<string> _expanded = new();

    public void SetItems(List<ServerItem> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public void Toggle(int position)
    {
        var a = _items[position].Address;
        if (!_expanded.Remove(a))
            _expanded.Add(a);
        NotifyDataSetChanged();
    }

    public override ServerItem this[int position] => _items[position];
    public override int Count => _items.Count;
    public override long GetItemId(int position) => position;

    /// <summary>Round time column: server-entry-round-time / server-entry-status-lobby, as the launcher shows it.</summary>
    public static string RoundTimeText(ServerItem item)
    {
        if (item.Players < 0)
            return "";
        if (item.RunLevel == 0)
            return Loc.Get("server-entry-status-lobby");
        if (item.RoundStart is not { } start)
            return "";
        var t = DateTime.UtcNow - start;
        if (t < TimeSpan.Zero)
            t = TimeSpan.Zero;
        return Loc.Get("server-entry-round-time", ("hours", (int)t.TotalHours), ("mins", t.Minutes)).Trim();
    }

    /// <summary>Phone in portrait: narrower columns and a shorter button so the name stays readable.</summary>
    public static bool Narrow(Context ctx) => ctx.Resources!.Configuration!.ScreenWidthDp < 600;

    public static int TimeWidth(Context ctx) => Narrow(ctx) ? 56 : 80;
    public static int PlayersWidth(Context ctx) => Narrow(ctx) ? 62 : 90;
    public static int ButtonWidth(Context ctx) => Narrow(ctx) ? 86 : 120;

    public static LinearLayout.LayoutParams Column(Context ctx, int widthDp) =>
        new(Ui.Dp(ctx, widthDp), ViewGroup.LayoutParams.WrapContent);

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        var item = _items[position];
        var expanded = _expanded.Contains(item.Address);

        var outer = Ui.Column(ctx);
        outer.SetBackgroundColor(position % 2 == 0 ? Ui.ListRowA : Ui.ListRowB);

        var row = Ui.Row(ctx);
        row.SetPadding(Ui.Dp(ctx, 6), Ui.Dp(ctx, 4), Ui.Dp(ctx, 6), Ui.Dp(ctx, 4));
        row.SetMinimumHeight(Ui.Dp(ctx, 46));

        row.AddView(Ui.Label(ctx, expanded ? "▾" : "▸", 14, Ui.Text), Ui.Wrap());
        var name = Ui.Label(ctx, item.Name, 15);
        name.SetSingleLine(true);
        name.Ellipsize = TextUtils.TruncateAt.End;
        name.SetPadding(Ui.Dp(ctx, 6), 0, Ui.Dp(ctx, 6), 0);
        row.AddView(name, Ui.Fill(1));

        var time = Ui.Label(ctx, RoundTimeText(item), 14);
        time.Gravity = GravityFlags.End;
        row.AddView(time, Column(ctx, TimeWidth(ctx)));

        var players = item.Players switch
        {
            -1 => Ui.Label(ctx, Loc.Get("server-entry-fetching"), 13, Ui.Muted),
            -2 => Ui.Label(ctx, Loc.Get("server-entry-offline"), 13, Ui.Bad),
            _ => Ui.Label(ctx, Loc.Get("server-entry-player-count", ("players", item.Players), ("max", item.MaxPlayers))
                .Replace("\n", "").Trim(), 14),
        };
        players.Gravity = GravityFlags.End;
        row.AddView(players, Column(ctx, PlayersWidth(ctx)));

        var connectButton = Ui.Button(ctx, Loc.Get("server-entry-connect"), () => connect(item));
        connectButton.SetTextSize(global::Android.Util.ComplexUnitType.Sp, Narrow(ctx) ? 12 : 13);
        connectButton.SetSingleLine(true);
        connectButton.Ellipsize = TextUtils.TruncateAt.End;
        connectButton.SetPadding(Ui.Dp(ctx, 4), Ui.Dp(ctx, 4), Ui.Dp(ctx, 4), Ui.Dp(ctx, 4));
        connectButton.SetMinWidth(0);
        connectButton.SetMinimumWidth(0);
        connectButton.SetMinHeight(Ui.Dp(ctx, 34));
        connectButton.SetMinimumHeight(Ui.Dp(ctx, 34));
        connectButton.Enabled = item.Players != -2;
        // Buttons inside list rows steal focus from the row and break row taps.
        connectButton.Focusable = false;
        var blp = Column(ctx, ButtonWidth(ctx));
        blp.LeftMargin = Ui.Dp(ctx, 6);
        row.AddView(connectButton, blp);
        outer.AddView(row);

        if (expanded)
        {
            var d = details(item);
            d.SetPadding(Ui.Dp(ctx, 26), 0, Ui.Dp(ctx, 10), Ui.Dp(ctx, 10));
            outer.AddView(d);
        }

        outer.AddView(Ui.Separator(ctx));
        return outer;
    }
}

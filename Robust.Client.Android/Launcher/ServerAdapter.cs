using Android.Content;
using Android.Views;
using Android.Widget;

namespace Robust.Client.Android.Launcher;

/// <param name="Players">-1 = unknown yet, -2 = offline.</param>
public sealed record ServerItem(string Address, string Name, int Players, int MaxPlayers);

/// <summary>Server rows as in the official launcher: alternating dark rows, separators, player count on the right.</summary>
internal sealed class ServerAdapter(Context ctx) : BaseAdapter<ServerItem>
{
    private List<ServerItem> _items = new();

    public void SetItems(List<ServerItem> items)
    {
        _items = items;
        NotifyDataSetChanged();
    }

    public override ServerItem this[int position] => _items[position];
    public override int Count => _items.Count;
    public override long GetItemId(int position) => position;

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        var item = _items[position];

        var outer = Ui.Column(ctx);
        outer.SetBackgroundColor(position % 2 == 0 ? Ui.ListRowA : Ui.ListRowB);

        var row = Ui.Row(ctx);
        row.SetPadding(Ui.Dp(ctx, 12), Ui.Dp(ctx, 10), Ui.Dp(ctx, 12), Ui.Dp(ctx, 10));

        var texts = Ui.Column(ctx);
        var name = Ui.Label(ctx, item.Name, 16);
        name.SetMaxLines(2);
        texts.AddView(name);
        texts.AddView(Ui.Label(ctx, item.Address, 12, Ui.Disabled));
        row.AddView(texts, Ui.Fill(1));

        var players = item.Players switch
        {
            -1 => Ui.Label(ctx, "…", 14, Ui.Muted),
            -2 => Ui.Label(ctx, "Не в сети", 14, Ui.Bad),
            _ => Ui.Label(ctx, $"{item.Players} / {item.MaxPlayers}", 15, item.Players > 0 ? Ui.NanoGold : Ui.Muted,
                bold: item.Players > 0),
        };
        row.AddView(players, Ui.Wrap(10, ctx));
        outer.AddView(row);
        outer.AddView(Ui.Separator(ctx));
        return outer;
    }
}

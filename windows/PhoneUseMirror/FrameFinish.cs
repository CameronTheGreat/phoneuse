using Windows.UI;

namespace PhoneUseMirror;

/// <summary>
/// The colour of the phone's band and side buttons (Settings › Appearance): real iPhone finishes.
/// Each base colour is AppleDB's for that finish (https://appledb.dev, deviceFiles/iPhone/&lt;identifier&gt;.json
/// in github.com/littlebyteorg/appledb); the band's light and shade are derived from it.
/// </summary>
public sealed record FrameFinish(string Id, string Name, string Group,
    Color Light, Color Dark, Color Mid, Color ButtonEdge, Color ButtonMid, Color Base)
{
    /// <summary>The colour shown on its swatch: the finish itself.</summary>
    public Color Swatch => Base;

    /// <summary>The band tone referenced from Apple's product photos. The AppleDB swatch can differ
    /// from the band, especially when the back is matte and the sides are polished steel.</summary>
    public Color? MeasuredBand { get; init; }

    /// <summary>The band's tone the renderer starts from: measured, or until then estimated from the
    /// swatch (about 40% of its light, the average of the measured ones).</summary>
    public Color Band => MeasuredBand ?? EstimateBand(Base);

    static Color EstimateBand(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        static byte Srgb(double l) { l = Math.Clamp(l, 0, 1); double s = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055; return (byte)Math.Round(s * 255); }
        return Color.FromArgb(0xFF, Srgb(Lin(c.R) * 0.4), Srgb(Lin(c.G) * 0.4), Srgb(Lin(c.B) * 0.4));
    }

    public static readonly string DefaultId = Phones.Str(Phones.Json["defaultFinish"])!;

    /// <summary>A finish from one base colour. Base is exact: the lit band (MainWindow.Frame.cs) shows it
    /// wherever the metal faces you. The others are only for the plain band drawn if that can't be.</summary>
    static FrameFinish From(string id, string name, string group, uint rgb)
    {
        var c = Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var black = Color.FromArgb(0xFF, 0, 0, 0);
        return new FrameFinish(id, name, group,
            Light: Lerp(c, white, 0.28),
            Dark: Lerp(c, black, 0.48),
            Mid: Lerp(c, black, 0.12),
            ButtonEdge: Lerp(c, black, 0.40),
            ButtonMid: Lerp(c, white, 0.18),
            Base: c);
    }

    static Color Hex(uint rgb) => Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    internal static Color Lerp(Color a, Color b, double t) => Color.FromArgb(0xFF,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Every finish, by group, from phoneuse/web/phones.json (Phones.cs). A finish with its
    /// plain-band colours given (Graphite, the frame this window always had) keeps them; the others
    /// derive them from their base colour (From).</summary>
    public static readonly IReadOnlyList<FrameFinish> All = Load();

    static Color Hex(string s) => Hex(Convert.ToUInt32(s.TrimStart('#'), 16));

    static FrameFinish[] Load()
    {
        var all = new List<FrameFinish>();
        foreach (var group in Phones.Json["finishes"]!.AsArray())
        {
            string name = Phones.Str(group!["group"])!;
            foreach (var f in group["finishes"]!.AsArray())
            {
                string id = Phones.Str(f!["id"])!, title = Phones.Str(f["name"])!;
                var finish = f["light"] is null
                    ? From(id, title, name, Convert.ToUInt32(Phones.Str(f["base"])!.TrimStart('#'), 16))
                    : new FrameFinish(id, title, name, Hex(Phones.Str(f["light"])!), Hex(Phones.Str(f["dark"])!),
                        Hex(Phones.Str(f["mid"])!), Hex(Phones.Str(f["buttonEdge"])!), Hex(Phones.Str(f["buttonMid"])!),
                        Hex(Phones.Str(f["base"])!));
                if (Phones.Str(f["band"]) is { } band) finish = finish with { MeasuredBand = Hex(band) };
                all.Add(finish);
            }
        }
        return all.ToArray();
    }

    public static FrameFinish Get(string? id) => All.FirstOrDefault(f => f.Id == id) ?? All[0];
}

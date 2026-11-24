using System.Text.Json.Nodes;

namespace PhoneUseMirror;

/// <summary>What the band is made of: how it takes the light (MainWindow.Frame.cs).</summary>
public enum BandMaterial
{
    /// <summary>Brushed: fine lines along the band, a soft highlight.</summary>
    Titanium,
    /// <summary>Bead-blasted: satin, a broad dim highlight, no lines.</summary>
    Aluminium,
    /// <summary>Polished stainless steel: sharp reflections with a dark rim.</summary>
    Steel,
    /// <summary>Polished titanium, as on iPhone Air and iPhone Duo.</summary>
    PolishedTitanium,
}

/// <summary>One button on an upright phone's side: which side, and its top and bottom as fractions
/// of the phone's length.</summary>
public readonly record struct SideButton(bool Left, double Top, double Bottom);

/// <summary>
/// The frame of one iPhone model, by its model identifier as AirPlay reports it (phoneuse's
/// state message: "model").
///   Glass: the black border between the screen and the band, in units of the screen's width:
///     1.2 mm on the 16 Pro and 1.71 mm on the 15 Pro (reported before launch); the others are
///     estimates from photos.
///   CornerPt: the screen's corner radius in points, iOS's own (UIScreen._displayCornerRadius, as
///     listed by github.com/kylebshr/ScreenCorners), drawn as a continuous corner (ContinuousCorner).
///     The 18 Pro isn't listed there; it has the 17 Pro's body, so its 62 is assumed.
///   FinishGroup: its colours in FrameFinish, if listed.
///   Buttons: the side buttons (Action button or ring/silent switch, volume up, volume down, side
///     button), or null where there's no drawing to take them from (MainWindow's estimate instead).
///   CameraControlAt: Camera Control on the right side, or null for none.
///
/// Where the buttons are comes from Apple's dimensional drawings (developer.apple.com/accessories,
/// "Accessory Design Guidelines"): each button's centre in mm from the phone's top, and its half
/// length (the side views' "2X" figures). Apple places them by distance from the top, not by
/// proportion, so on the bigger phones (Plus, Pro Max, Air) they sit proportionally higher, and
/// Camera Control proportionally lower. Apple has no drawings online for the iPhone 11 and older.
/// </summary>
public sealed record PhoneModel(string Name, double Glass, BandMaterial Material,
    bool ActionButton, SideButton[]? Buttons, SideButton? CameraControlAt, string? FinishGroup, double CornerPt)
{
    // The table itself is phoneuse/web/phones.json (Phones.cs), shared with the browser mirror.

    /// <summary>A model from its entry in phones.json: its layout's buttons placed on a phone `length`
    /// mm long, and its Camera Control (centre in mm, cameraControlHalf either side of it), if any.</summary>
    static PhoneModel FromJson(JsonObject m)
    {
        SideButton[]? buttons = null;
        SideButton? camera = null;
        if (Phones.Str(m["layout"]) is { } layout)
        {
            double length = Phones.Num(m["length"]);
            SideButton At(bool left, double centre, double half) => new(left, (centre - half) / length, (centre + half) / length);
            buttons = Phones.Json["layouts"]![layout]!.AsArray()
                .Select(b => At(b!["left"]!.GetValue<bool>(), Phones.Num(b["centre"]), Phones.Num(b["half"])))
                .ToArray();
            if (m["camera"] is { } c)
                camera = At(false, Phones.Num(c), Phones.Num(Phones.Json["cameraControlHalf"]));
        }
        return new(Phones.Str(m["name"])!, Phones.Num(m["glass"]), Enum.Parse<BandMaterial>(Phones.Str(m["material"])!),
            m["actionButton"]!.GetValue<bool>(), buttons, camera, Phones.Str(m["finish"]), Phones.Num(m["corner"]));
    }

    /// <summary>No phone has mirrored yet, or one this table doesn't know: a current iPhone (the 17 Pro's buttons).</summary>
    public static readonly PhoneModel Default = FromJson(Phones.Json["default"]!.AsObject());

    static readonly Dictionary<string, PhoneModel> Models = Build();

    public static PhoneModel For(string? identifier) =>
        identifier != null && Models.TryGetValue(identifier, out var m) ? m : Default;

    public static string FinishGroupLabel(string group)
    {
        var names = Models.Values.Where(m => m.FinishGroup == group).Select(m => m.Name).Distinct().ToArray();
        return names.Length == 0 ? group : string.Join(" / ", names);
    }

    public static BandMaterial? FinishGroupMaterial(string group) =>
        Models.Values.FirstOrDefault(m => m.FinishGroup == group)?.Material;

    /// <summary>Side buttons of an upright phone for the models Apple has no drawings of (the iPhone 11
    /// and older): (left side?, top, bottom) as fractions of the band's height, estimated from photos.</summary>
    public static readonly (bool left, double top, double bottom)[] EstimatedButtons =
        Phones.Json["estimatedButtons"]!.AsArray()
            .Select(b => (b!["left"]!.GetValue<bool>(), Phones.Num(b["top"]), Phones.Num(b["bottom"])))
            .ToArray();

    static Dictionary<string, PhoneModel> Build()
    {
        var d = new Dictionary<string, PhoneModel>();
        foreach (var node in Phones.Json["models"]!.AsArray())
        {
            var model = FromJson(node!.AsObject());
            foreach (var id in node["ids"]!.AsArray()) d[id!.GetValue<string>()] = model;
        }
        return d;
    }
}

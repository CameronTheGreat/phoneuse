using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;

namespace PhoneUseMirror;

/// <summary>
/// The agent activity rail: a borderless window owned by the mirror window (so it stays just above
/// it and minimizes with it, like the glow), docked by MainWindow.Activity.cs against the tips of the
/// phone's side buttons. It has two sizes:
///   rail    44 DIPs wide, centred on the phone's side: a live dot while an agent runs, then an icon
///           tile per recent action, newest at the top.
///   panel   320 DIPs wide, the straight part of the phone's side tall: the whole log, newest first,
///           drawn as Settings is (the cream page, a bold title, each agent's run as a white card).
/// Hovering the rail opens the panel, which folds back once the pointer has been off it for 360 ms;
/// clicking the rail opens it pinned, until its fold button. The window is always the panel's size and
/// is cut down to the rail with a window region; the rail grows into the panel as a composition clip
/// animation, so the window never resizes mid-animation. The edge against the phone is square and the
/// outer corners round, on a see-through window (the mirror's ClearBackdrop), so the rail reads as part
/// of the device rather than a window beside it.
/// It never takes focus (WS_EX_NOACTIVATE, shown with SW_SHOWNOACTIVATE), so typing still reaches
/// the phone. The text comes from on-screen labels that apps control, so every piece of it goes
/// through TextBlock.Text, never through markup parsing.
/// </summary>
public sealed class ActivitySidebar : Window
{
    const int MaxRows = 200;
    public const double WidthDips = 320;
    const double RailWidth = 44, RailCorner = 16, PanelCorner = 24;
    const double Tile = 28, TileGap = 6;
    static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(220);

    /// <summary>Where it docks, in screen pixels: the x of the band's outer edge, the straight part of the
    /// side (the panel's height), which side, the DPI scale, and the rail's span along the side buttons
    /// (RailHeight 0: none to snap to, so about half the side, centred).</summary>
    public readonly record struct Dock(double EdgeX, double Top, double Height, bool Left, double Scale,
        double RailTop = 0, double RailHeight = 0);

    readonly IntPtr _hwnd;
    readonly Grid _root;
    readonly Border _shape;
    readonly Grid _content;
    readonly Grid _railView;
    readonly StackPanel _railTiles;
    readonly Grid _panelView;
    readonly TranslateTransform _slide = new();
    readonly Compositor _comp;
    readonly CompositionRoundedRectangleGeometry _clip;  // the shape's outline: the rail's, the panel's, or between
    readonly Visual _railVisual, _panelVisual;
    readonly CompositionEasingFunction _openEase, _closeEase, _fadeEase;
    readonly StackPanel _list;
    readonly ScrollViewer _scroll;
    readonly FrameworkElement _empty;
    readonly Button _fold;
    readonly TextBlock _title;
    readonly List<JsonObject> _entries = new();  // oldest first, as the server keeps them
    readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(120) };
    Dock _dock;
    bool _darkMode;
    bool _open, _expanded, _pinned, _railLive;
    int _away;   // hover ticks with the pointer off the open panel
    int _turn;   // bumped on every change of size, so a finished animation from before does nothing

    public event Action? ClearRequested;

    public ActivitySidebar(IntPtr owner, bool darkMode)
    {
        _darkMode = darkMode;
        Title = "Agent activity";
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new MainWindow.ClearBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        const int WS_CAPTION = 0x00C00000;  // WS_BORDER | WS_DLGFRAME
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
        const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        SetWindowLong(_hwnd, GWL_STYLE, GetWindowLong(_hwnd, GWL_STYLE) & ~WS_CAPTION);
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, owner);  // owned: above the mirror, minimizes with it
        int corners = 1;               // DWMWCP_DONOTROUND: the shape draws its own corners
        uint border = 0xFFFFFFFE;      // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, 33, ref corners, sizeof(int));
        DwmSetWindowAttribute(_hwnd, 34, ref border, sizeof(uint));
        // DWM composes the window with its alpha, as the mirror's (blur-behind over an empty region: no blur)
        var region = CreateRectRgn(-2, -2, -1, -1);
        var blur = new DWM_BLURBEHIND { dwFlags = 1 | 2, fEnable = true, hRgnBlur = region };  // ENABLE | BLURREGION
        DwmEnableBlurBehindWindow(_hwnd, ref blur);
        DeleteObject(region);

        // ---- the rail: a live dot, then a tile per recent entry
        _railTiles = new StackPanel { Spacing = TileGap, HorizontalAlignment = HorizontalAlignment.Center };
        _railView = new Grid { Background = Brand.Transparent, Padding = new Thickness(0, 12, 0, 12) };
        _railView.Children.Add(_railTiles);
        _railView.PointerEntered += (_, _) => { if (!_expanded) { _away = -1; _hover.Start(); } };
        _railView.PointerExited += (_, _) => { if (!_expanded) _hover.Stop(); };
        _railView.Tapped += (_, _) => Expand(pinned: true);
        AutomationProperties.SetName(_railView, "Agent activity");
        ToolTipService.SetToolTip(_railView, "Agent activity");

        // ---- the panel: the header as Settings', a big bold title and round cream buttons beside it
        _title = new TextBlock
        {
            Text = "Activity", FontSize = 28, LineHeight = 32, FontWeight = FontWeights.Bold,
            CharacterSpacing = -35, Foreground = Palette("Ink"), VerticalAlignment = VerticalAlignment.Center,
        };
        var clear = new Button
        {
            Content = "Clear", Height = 32, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(16),
            FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        };
        clear.Click += (_, _) => ClearRequested?.Invoke();
        _fold = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(_fold, "Fold into the rail");
        AutomationProperties.SetName(_fold, "Fold into the rail");
        _fold.Click += (_, _) => Collapse();

        var header = Ui.Columns(6, "*", "auto", "auto");
        header.Margin = new Thickness(18, 14, 12, 0);
        _title.AddTo(header, 0);
        clear.AddTo(header, 1);
        _fold.AddTo(header, 2);

        _list = new StackPanel { Padding = new Thickness(14, 0, 14, 20) };
        _scroll = new ScrollViewer
        {
            Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _empty = EmptyState();

        _panelView = new Grid { Visibility = Visibility.Collapsed };
        _panelView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _panelView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panelView.Children.Add(header);
        Grid.SetRow(_scroll, 1);
        _panelView.Children.Add(_scroll);
        Grid.SetRow(_empty, 1);
        _panelView.Children.Add(_empty);

        _content = new Grid { RequestedTheme = darkMode ? ElementTheme.Dark : ElementTheme.Light };
        _content.Children.Add(_railView);
        _content.Children.Add(_panelView);
        _shape = new Border { Background = Palette("Bg"), Child = _content, RenderTransform = _slide };

        _root = new Grid { Background = Brand.Transparent };
        _root.Children.Add(_shape);
        // clipped to the window, so the slide is cut off at the phone's edge
        _root.SizeChanged += (_, e) =>
            _root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        Content = _root;

        // the outline and the cross-fade run on the compositor, off the UI thread
        var shapeVisual = ElementCompositionPreview.GetElementVisual(_shape);
        _comp = shapeVisual.Compositor;
        _clip = _comp.CreateRoundedRectangleGeometry();
        shapeVisual.Clip = _comp.CreateGeometricClip(_clip);
        _railVisual = ElementCompositionPreview.GetElementVisual(_railView);
        _panelVisual = ElementCompositionPreview.GetElementVisual(_panelView);
        ElementCompositionPreview.SetIsTranslationEnabled(_panelView, true);
        _openEase = _comp.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1f));   // quick out, long settle
        _closeEase = _comp.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));
        _fadeEase = _comp.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0f), new Vector2(0.67f, 1f));

        _hover.Tick += (_, _) => HoverTick();
        Render();
    }

    public void ApplyAppearance(bool darkMode)
    {
        if (_darkMode == darkMode) return;
        _darkMode = darkMode;
        _content.RequestedTheme = darkMode ? ElementTheme.Dark : ElementTheme.Light;
        _shape.Background = Palette("Bg");
        _title.Foreground = Palette("Ink");
        UpdateFoldIcon();
        Render();
    }

    Brush Palette(string name) => Brand.AppearanceBrush(name, _darkMode);

    void UpdateFoldIcon() =>
        _fold.Content = Ui.Icon(_dock.Left ? Ui.ChevronIcon : "M15,6 L9,12 L15,18", 14, Palette("Ink2"), 2.2);

    static FrameworkElement EmptyState()
    {
        var col = new StackPanel { Spacing = 4, Margin = new Thickness(32, 0, 32, 60), VerticalAlignment = VerticalAlignment.Center };
        var tile = Ui.IconTile(Icons.Sparkle);
        tile.Width = tile.Height = 44;
        tile.CornerRadius = new CornerRadius(14);
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        tile.Margin = new Thickness(0, 0, 0, 8);
        col.Children.Add(tile);
        col.Children.Add(new TextBlock
        {
            Text = "No agent activity yet", Style = Brand.RowTitle,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        });
        col.Children.Add(new TextBlock
        {
            Text = "When an agent uses the phone, each thing it does shows here, newest first.",
            Style = Brand.RowSub, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        });
        return col;
    }

    // ---------------------------------------------------------------- rows

    public void SetEntries(IEnumerable<JsonObject> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        Trim();
        Render();
    }

    /// <summary>One new entry. At most 200 are kept.</summary>
    public void Add(JsonObject entry)
    {
        _entries.Add(entry);
        Trim();
        Render();
    }

    void Trim()
    {
        if (_entries.Count > MaxRows) _entries.RemoveRange(0, _entries.Count - MaxRows);
    }

    /// <summary>One agent's run: from its start to the next start.</summary>
    sealed class AgentRun
    {
        public readonly List<JsonObject> Entries = new();
        public bool Ended;  // finished, its request ended, or you took over
    }

    /// <summary>Rebuilt whole on every change. Entries arrive seconds apart, and 200 rows build in a few milliseconds.</summary>
    void Render()
    {
        var runs = new List<AgentRun>();
        AgentRun? cur = null;
        foreach (var e in _entries)
        {
            bool session = Kind(e) == "session";
            string words = session ? SessionWords(Text(e)) : "";
            bool starts = words is "Agent started" or "Agent asked to start";
            // "asked to start" then "started" stays one run
            bool continues = cur != null && !cur.Ended && cur.Entries.All(x => Kind(x) == "session");
            if (cur == null || (starts && !continues))
                runs.Add(cur = new AgentRun());
            cur.Entries.Add(e);
            if (session)
                cur.Ended = words is "Agent finished" or "Agent request ended" or "You took over"
                    || (cur.Ended && !(starts || words == "Resumed"));
        }

        _list.Children.Clear();
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var run = runs[i];
            _list.Children.Add(RunLabel(run, i == runs.Count - 1));
            var rows = new StackPanel();
            for (int j = run.Entries.Count - 1; j >= 0; j--)
            {
                var row = MakeRow(run.Entries[j]);
                if (rows.Children.Count > 0)
                {
                    row.BorderBrush = Palette("Line");
                    row.BorderThickness = new Thickness(0, 1, 0, 0);
                }
                rows.Children.Add(row);
            }
            _list.Children.Add(new Border { Style = Brand.CardStyle, Child = rows });
        }
        RenderRail(runs.Count > 0 && !runs[^1].Ended);
        bool none = _entries.Count == 0;
        _empty.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>A card's section label, as Settings' "On this PC": the run's times, or "Now" with a green dot while it runs.</summary>
    FrameworkElement RunLabel(AgentRun run, bool newest)
    {
        string first = Time(run.Entries[0]), last = Time(run.Entries[^1]);
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 18, 4, 8) };
        if (newest && !run.Ended)
        {
            label.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Palette("Green"), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock { Text = "Now", Style = Brand.SectionLabel, Margin = new Thickness(0), Foreground = Palette("GreenText") });
            label.Children.Add(new TextBlock { Text = "· since " + first, Style = Brand.SectionLabel, Margin = new Thickness(0) });
        }
        else
            label.Children.Add(new TextBlock
            {
                Text = first == last ? first : first + " – " + last, Style = Brand.SectionLabel, Margin = new Thickness(0),
            });
        return label;
    }

    static string Kind(JsonObject e) => (string?)e["kind"] ?? "action";
    static string Text(JsonObject e) => (string?)e["text"] ?? "";

    static string Time(JsonObject e) =>
        e["at"] is JsonNode at && (double?)at is double secs
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(secs * 1000)).ToLocalTime().ToString("HH:mm")
            : "";

    /// <summary>A row as a Settings row: its icon tile, the words, and the time on the right. The words come
    /// from on-screen labels that apps control, so they only ever go through TextBlock.Text.</summary>
    Grid MakeRow(JsonObject e)
    {
        string why = (string?)e["why"] ?? "";
        bool session = Kind(e) == "session";
        string title = session ? SessionWords(Text(e)) : Text(e);
        var (icon, tint, stroke) = session ? SessionLook(title) : (ActionIcon(title), null, null);

        var row = Ui.Columns(12, "auto", "*", "auto");
        row.Padding = new Thickness(12, 9, 14, 9);
        row.MinHeight = 46;
        var tile = Ui.IconTile(icon, tint, stroke);
        tile.Width = tile.Height = 28;
        tile.CornerRadius = new CornerRadius(8);
        if (tile.Child is Viewbox v) v.Width = v.Height = 15;
        tile.AddTo(row, 0);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Palette("Ink"),
            FontWeight = session ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (why.Length > 0) words.Children.Add(new TextBlock { Text = Capital(why), Style = Brand.RowSub });
        words.AddTo(row, 1);

        new TextBlock
        {
            Text = Time(e), FontSize = 12, Foreground = Palette("Ink3"), VerticalAlignment = VerticalAlignment.Center,
        }.AddTo(row, 2);
        return row;
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Stroke icons on the site's 24-unit grid (Ui.Icon).</summary>
    static class Icons
    {
        public const string Apps = "M5,5 L10,5 L10,10 L5,10 Z M14,5 L19,5 L19,10 L14,10 Z M5,14 L10,14 L10,19 L5,19 Z M14,14 L19,14 L19,19 L14,19 Z";
        public const string Tap = "M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12 Z M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 Z";
        public const string Scroll = "M12,4 L12,20 M8,8 L12,4 L16,8 M8,16 L12,20 L16,16";
        public const string Swipe = "M4,12 L20,12 M8,8 L4,12 L8,16 M16,8 L20,12 L16,16";
        public const string Type = "M3,7 L21,7 L21,17 L3,17 Z M7,10.5 L7.5,10.5 M11,10.5 L11.5,10.5 M15,10.5 L15.5,10.5 M8,14 L16,14";
        public const string Look = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M14.5,12 A2.5,2.5 0 1 1 9.5,12 A2.5,2.5 0 1 1 14.5,12 Z";
        public const string Back = "M15,6 L9,12 L15,18";
        public const string Home = "M4,11 L12,4 L20,11 M6,9.5 L6,20 L18,20 L18,9.5";
        public const string Clock = "M20,12 A8,8 0 1 1 4,12 A8,8 0 1 1 20,12 Z M12,8 L12,12 L15,14";
        public const string Key = "M15,9 A4,4 0 1 1 7,9 A4,4 0 1 1 15,9 Z M14,12 L20,18 M17,15 L19,13";
        public const string Lock = "M6,11 L18,11 L18,20 L6,20 Z M8.5,11 L8.5,8 A3.5,3.5 0 0 1 15.5,8 L15.5,11";
        public const string Sparkle = "M12,3 L13.8,10.2 L21,12 L13.8,13.8 L12,21 L10.2,13.8 L3,12 L10.2,10.2 Z";
        public const string Play = "M8,5 L19,12 L8,19 Z";
        public const string Check = "M5,12.5 L10,17 L19,7";
        public const string Person = "M16,8 A4,4 0 1 1 8,8 A4,4 0 1 1 16,8 Z M4,20 C5.5,15 18.5,15 20,20";
        public const string Stop = "M7,7 L17,17 M17,7 L7,17";
    }

    /// <summary>The icon for an action, from its verb (cli.py's VERBS). Plum, the agent's colour in Settings.</summary>
    static string ActionIcon(string text)
    {
        string t = text.ToLowerInvariant();
        return t.StartsWith("scrolling") ? Icons.Scroll
            : t.StartsWith("swiping") || t.StartsWith("touching") ? Icons.Swipe
            : t.StartsWith("tapping") || t.StartsWith("long-pressing") ? Icons.Tap
            : t.StartsWith("typing") || t.Contains("keyboard") ? Icons.Type
            : t.StartsWith("pressing") ? Icons.Key
            : t.StartsWith("looking") || t.StartsWith("reading") ? Icons.Look
            : t.StartsWith("going back") || t.StartsWith("closing") ? Icons.Back
            : t.StartsWith("going home") ? Icons.Home
            : t.StartsWith("waiting") ? Icons.Clock
            : t.Contains("unlock") ? Icons.Lock
            : t.StartsWith("opening") ? Icons.Apps
            : Icons.Sparkle;
    }

    /// <summary>Session changes in the status colours: plum starts, green finishes, amber where you took over.</summary>
    (string, Brush?, Brush?) SessionLook(string words) => words switch
    {
        "Agent started" or "Resumed" => (Icons.Play, Palette("PlumTint"), Palette("Plum")),
        "Agent asked to start" => (Icons.Sparkle, Palette("PlumTint"), Palette("Plum")),
        "Agent finished" => (Icons.Check, Palette("GreenTint"), Palette("GreenText")),
        "You took over" => (Icons.Person, Palette("AmberTint"), Palette("AmberIcon")),
        _ => (Icons.Stop, Palette("Cream"), Palette("Ink3")),
    };

    /// <summary>A session change in words: "idle -> driving" is the agent starting.</summary>
    static string SessionWords(string t)
    {
        var parts = t.Split(" -> ");
        if (parts.Length != 2) return t;
        string from = parts[0], to = parts[1];
        if ((from == "idle" || from == "requested") && to == "driving") return "Agent started";
        if (from == "requested" && to == "idle") return "Agent request ended";
        if (from == "driving" && to == "idle") return "Agent finished";
        if (to == "paused") return "You took over";
        if (from == "paused" && to == "idle") return "Resumed";
        if (from == "idle" && to == "requested") return "Agent asked to start";
        return t;
    }

    // ---------------------------------------------------------------- the rail

    /// <summary>The rail's tiles: a dot while an agent runs (green) or not (cream), then the newest entries,
    /// as many as fit the rail's height, the oldest fading.</summary>
    void RenderRail(bool live)
    {
        _railLive = live;
        _railTiles.Children.Clear();
        _railTiles.Children.Add(new Ellipse
        {
            Width = 8, Height = 8, Margin = new Thickness(0, 2, 0, 4), HorizontalAlignment = HorizontalAlignment.Center,
            Fill = live ? Palette("Green") : Palette("LineStrong"),
        });
        int fit = RailFits();
        int shown = 0;
        for (int i = _entries.Count - 1; i >= 0 && shown < fit; i--, shown++)
        {
            var e = _entries[i];
            bool session = Kind(e) == "session";
            string title = session ? SessionWords(Text(e)) : Text(e);
            var (icon, tint, stroke) = session ? SessionLook(title) : (ActionIcon(title), null, null);
            var tile = Ui.IconTile(icon, tint, stroke);
            tile.Width = tile.Height = Tile;
            tile.CornerRadius = new CornerRadius(9);
            if (tile.Child is Viewbox v) v.Width = v.Height = 15;
            if (fit > 3 && shown >= fit - 2) tile.Opacity = shown == fit - 1 ? 0.35 : 0.65;  // the tail fades
            ToolTipService.SetToolTip(tile, title);
            _railTiles.Children.Add(tile);
        }
        if (shown == 0)
        {
            var tile = Ui.IconTile(Icons.Sparkle, Palette("Cream"), Palette("Ink3"));
            tile.Width = tile.Height = Tile;
            tile.CornerRadius = new CornerRadius(9);
            if (tile.Child is Viewbox v) v.Width = v.Height = 15;
            _railTiles.Children.Add(tile);
        }
    }

    /// <summary>How many tiles the rail is tall enough for.</summary>
    int RailFits() => Math.Max(1, (int)((RailHeightDips() - 24 - 14 + TileGap) / (Tile + TileGap)));

    /// <summary>The rail spans the side buttons; with none, about half the side, at most 9 tiles.</summary>
    double RailHeightDips()
    {
        if (_dock.RailHeight > 0 && _dock.Scale > 0) return _dock.RailHeight / _dock.Scale;
        double side = _dock.Scale > 0 ? _dock.Height / _dock.Scale : 400;
        double nine = 24 + 14 + 9 * (Tile + TileGap) - TileGap;
        return Math.Min(side, Math.Max(Math.Min(side, 160), Math.Min(nine, side * 0.55)));
    }

    // ---------------------------------------------------------------- sizes

    /// <summary>The window's rectangle, always the panel's: the rail is the panel's window cut down with a
    /// window region (RailRegion), so opening and closing never resizes or moves the window (a resize shows
    /// a frame of the old content at the new size), and the shape can grow and shrink inside it.</summary>
    RectInt32 WindowRect()
    {
        double s = Math.Max(0.01, _dock.Scale);
        double w = WidthDips * s;
        double x = _dock.Left ? _dock.EdgeX - w : _dock.EdgeX;
        return new RectInt32((int)Math.Round(x), (int)Math.Round(_dock.Top), (int)Math.Round(w), (int)Math.Round(_dock.Height));
    }

    double PanelHeightDips => _dock.Scale > 0 ? _dock.Height / _dock.Scale : 400;

    /// <summary>The rail's or the panel's rectangle inside the window, in DIPs: against the phone's side,
    /// the rail along the side buttons (or centred), the panel the whole straight part.</summary>
    (double x, double y, double w, double h) Rect(bool panel)
    {
        double w = panel ? WidthDips : RailWidth, h = panel ? PanelHeightDips : RailHeightDips();
        double y = !panel && _dock.RailHeight > 0 ? (_dock.RailTop - _dock.Top) / _dock.Scale : (PanelHeightDips - h) / 2;
        return (_dock.Left ? WidthDips - w : 0, y, w, h);
    }

    /// <summary>The clip that gives the shape its outline: a rounded rectangle reaching a corner's width
    /// past the phone-side edge, so only the far corners show round and the near edge stays square.</summary>
    (Vector2 offset, Vector2 size, Vector2 corner) ClipFor(bool panel)
    {
        var (x, y, w, h) = Rect(panel);
        double r = panel ? PanelCorner : RailCorner;
        if (!_dock.Left) x -= r;
        return (new Vector2((float)x, (float)y), new Vector2((float)(w + r), (float)h), new Vector2((float)r));
    }

    void SetClip(bool panel)
    {
        _clip.StopAnimation("Offset");
        _clip.StopAnimation("Size");
        _clip.StopAnimation("CornerRadius");
        (_clip.Offset, _clip.Size, _clip.CornerRadius) = ClipFor(panel);
    }

    /// <summary>The window cut down to the rail (clicks beside it reach the desktop), or whole for the panel.</summary>
    void SetRegion(bool panel)
    {
        if (panel) { SetWindowRgn(_hwnd, IntPtr.Zero, true); return; }
        double s = Math.Max(0.01, _dock.Scale);
        var (x, y, w, h) = Rect(false);
        var rgn = CreateRectRgn((int)Math.Floor(x * s), (int)Math.Floor(y * s), (int)Math.Ceiling((x + w) * s), (int)Math.Ceiling((y + h) * s));
        SetWindowRgn(_hwnd, rgn, true);  // the window owns the region from here
    }

    /// <summary>The visible part in screen pixels, for the hover check.</summary>
    RECT VisibleRect()
    {
        GetWindowRect(_hwnd, out var r);
        if (_expanded) return r;
        double s = Math.Max(0.01, _dock.Scale);
        var (x, y, w, h) = Rect(false);
        return new RECT
        {
            Left = r.Left + (int)(x * s), Top = r.Top + (int)(y * s),
            Right = r.Left + (int)((x + w) * s), Bottom = r.Top + (int)((y + h) * s),
        };
    }

    /// <summary>Settle into the rail or the panel with no animation.</summary>
    void Settle(bool panel)
    {
        _expanded = panel;
        var rail = Rect(false);
        _railView.Width = rail.w;
        _railView.Height = rail.h;
        _railView.VerticalAlignment = VerticalAlignment.Top;
        _railView.Margin = new Thickness(0, rail.y, 0, 0);
        _railView.HorizontalAlignment = _dock.Left ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        UpdateFoldIcon();
        _railView.Visibility = panel ? Visibility.Collapsed : Visibility.Visible;
        _panelView.Visibility = panel ? Visibility.Visible : Visibility.Collapsed;
        _railVisual.Opacity = 1;
        _panelVisual.Opacity = 1;
        _panelVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        SetClip(panel);
        SetRegion(panel);
    }

    // ---------------------------------------------------------------- the morph

    /// <summary>Open the panel from the rail. The rail's outline grows into the panel's (a composition
    /// animation of the clip, so the panel's text lays out once, at its full size, and never reflows),
    /// the rail's tiles fade out under it, and the panel's content fades in drifting from the phone.</summary>
    void Expand(bool pinned)
    {
        _hover.Stop();
        _pinned |= pinned;
        if (_expanded || !_open) return;
        _expanded = true;
        int turn = ++_turn;
        SetRegion(true);
        _panelView.Visibility = Visibility.Visible;
        float drift = _dock.Left ? 18 : -18;
        _panelVisual.Opacity = 0;
        _panelVisual.Properties.InsertVector3("Translation", new Vector3(drift, 0, 0));

        var batch = _comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        Morph(ClipFor(true), OpenTime, _openEase);
        Fade(_railVisual, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(110));
        Fade(_panelVisual, 1, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(240));
        Drift(Vector3.Zero, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(300));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (turn == _turn) _railView.Visibility = Visibility.Collapsed;
        };
        if (!_pinned) { _away = 0; _hover.Start(); }
    }

    /// <summary>Fold the panel back into the rail: the reverse, quicker, the panel's content going first.</summary>
    void Collapse()
    {
        _hover.Stop();
        _pinned = false;
        if (!_expanded) return;
        _expanded = false;
        int turn = ++_turn;
        RenderRail(_railLive);
        _railView.Visibility = Visibility.Visible;
        _railVisual.Opacity = 0;
        float drift = _dock.Left ? 18 : -18;

        var batch = _comp.CreateScopedBatch(CompositionBatchTypes.Animation);
        Fade(_panelVisual, 0, TimeSpan.Zero, TimeSpan.FromMilliseconds(120));
        Drift(new Vector3(drift, 0, 0), TimeSpan.Zero, TimeSpan.FromMilliseconds(180));
        Morph(ClipFor(false), CloseTime, _closeEase);
        Fade(_railVisual, 1, TimeSpan.FromMilliseconds(130), TimeSpan.FromMilliseconds(130));
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (turn == _turn) Settle(false);
        };
    }

    static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(340), CloseTime = TimeSpan.FromMilliseconds(260);

    void Morph((Vector2 offset, Vector2 size, Vector2 corner) to, TimeSpan duration, CompositionEasingFunction ease)
    {
        foreach (var (prop, value) in new[] { ("Offset", to.offset), ("Size", to.size), ("CornerRadius", to.corner) })
        {
            var a = _comp.CreateVector2KeyFrameAnimation();
            a.InsertKeyFrame(1, value, ease);
            a.Duration = duration;
            _clip.StartAnimation(prop, a);
        }
    }

    void Fade(Visual v, float to, TimeSpan delay, TimeSpan duration)
    {
        var a = _comp.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1, to, _fadeEase);
        a.DelayTime = delay;
        a.Duration = duration;
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        v.StartAnimation("Opacity", a);
    }

    void Drift(Vector3 to, TimeSpan delay, TimeSpan duration)
    {
        var a = _comp.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(1, to, _openEase);
        a.DelayTime = delay;
        a.Duration = duration;
        _panelVisual.StartAnimation("Translation", a);
    }

    /// <summary>Every 120 ms while it matters: on the rail, open after one tick (a pass over it doesn't);
    /// on the open, unpinned panel, fold after three ticks with the pointer off it.</summary>
    void HoverTick()
    {
        GetCursorPos(out var pt);
        var r = VisibleRect();
        bool over = pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        if (!_expanded)
        {
            _hover.Stop();
            if (over) Expand(pinned: false);
            return;
        }
        if (_pinned) { _hover.Stop(); return; }
        _away = over ? 0 : _away + 1;
        if (_away >= 3) Collapse();
    }

    // ---------------------------------------------------------------- showing

    /// <summary>Dock and show without activating, as the rail, sliding out from the phone's edge.</summary>
    public void SlideOpen(Dock dock)
    {
        _dock = dock;
        _pinned = false;
        _turn++;
        RenderRail(_railLive);
        AppWindow.MoveAndResize(WindowRect());
        Settle(false);
        bool wasOpen = _open;
        ShowWindow(_hwnd, 4);  // SW_SHOWNOACTIVATE
        _open = true;
        if (!wasOpen || _slide.X != 0) Animate(dock.Left ? RailWidth : -RailWidth, 0, Slide, null);
    }

    public void SlideClosed()
    {
        if (!_open) return;
        _open = false;
        _hover.Stop();
        int turn = ++_turn;
        double w = _expanded ? WidthDips : RailWidth;
        Animate(0, _dock.Left ? w : -w, Slide, () =>
        {
            if (turn != _turn || _open) return;
            ShowWindow(_hwnd, 0);
            _pinned = false;
            _slide.X = 0;
            Settle(false);
        });
    }

    /// <summary>Follow the phone while open, with no animation.</summary>
    public void Place(Dock dock)
    {
        bool changed = dock.Left != _dock.Left || Math.Abs(dock.Height - _dock.Height) > 0.5 || dock.Scale != _dock.Scale
            || Math.Abs(dock.RailTop - dock.Top - (_dock.RailTop - _dock.Top)) > 0.5 || Math.Abs(dock.RailHeight - _dock.RailHeight) > 0.5;
        _dock = dock;
        AppWindow.MoveAndResize(WindowRect());
        if (!changed) return;
        _turn++;
        if (!_expanded) RenderRail(_railLive);
        Settle(_expanded);
    }

    /// <summary>Hide or show with the mirror (tray, minimize), with no animation.</summary>
    public void SetHostVisible(bool visible)
    {
        if (!_open) return;
        if (!visible)
        {
            _hover.Stop();
            if (_expanded && !_pinned) { _turn++; Settle(false); }
        }
        ShowWindow(_hwnd, visible ? 4 : 0);
    }

    /// <summary>Follow the mirror's Always on top: topmost keeps the rail visible when another app
    /// takes focus; off, it is owned, so it still stays just above the mirror. Never activates the rail.</summary>
    public void SetTopmost(bool on) =>
        SetWindowPos(_hwnd, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020);  // HWND_(NO)TOPMOST; NOSIZE|NOMOVE|NOACTIVATE|FRAMECHANGED

    public bool IsOpen => _open;


    void Animate(double from, double to, TimeSpan duration, Action? done)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to, Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(a, _slide);
        Storyboard.SetTargetProperty(a, "X");
        var sb = new Storyboard();
        sb.Children.Add(a);
        if (done != null) sb.Completed += (_, _) => done();
        else sb.Completed += (_, _) => _slide.X = to;
        _slide.X = from;
        sb.Begin();
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blur);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
}

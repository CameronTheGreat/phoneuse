using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace PhoneUseMirror;

/// <summary>
/// The agent activity rail (ActivitySidebar.cs), docked against the tips of the phone's side buttons
/// on the right (or the left, where the work area has no room for the opened panel on the right). It
/// is kept there as the window moves, resizes or turns.
///
/// When it shows: the bar's activity button turns it on and off (AppSettings.ActivityOpen). Off, it is
/// gone, hover and all, except while an agent is connected (driving, or paused by you) and for 4 s
/// after that agent ends, so you see what it does. Pressing the button while an agent has it shown
/// hides it until the next agent connects. The server
/// (phoneuse/activity.py) keeps the entries in memory; this loads them when the sidebar opens and
/// again after the connection comes back, and takes new ones from the "activity" messages.
/// </summary>
public sealed partial class MainWindow
{
    const double ActivityWidth = ActivitySidebar.WidthDips;

    ActivitySidebar? _activity;
    bool _activityLeft;
    bool _activityWant;     // the owner has it on (AppSettings.ActivityOpen), shown or not
    bool _activityAgent;    // an agent is connected: shown even when it's off
    bool _activityDismissed;  // the owner hid it while this agent is connected
    bool _activityLinger;   // the 4 s after an agent ends, while it's off
    DispatcherQueueTimer? _activityLingerTimer;
    long _activityLastId;   // the newest entry shown: a reload and a live message never double a row

    void InitActivity()
    {
        _activityWant = AppSettings.ActivityOpen;
        ShowActivityButton();
        Surface.LayoutUpdated += (_, _) => PlaceActivity();
        AppWindow.Changed += (_, a) =>
        {
            if (a.DidPositionChange || a.DidSizeChange) PlaceActivity();
            if (a.DidVisibilityChange || a.DidPresenterChange || a.DidSizeChange) ShowActivityHost();
        };
        Closed += (_, _) => _activity?.Close();
    }

    bool ActivityHostVisible =>
        AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>Where the rail docks, in screen pixels: on the titanium band's outer edge (the band's outline
    /// as PlaceGlow works it out), over the side buttons, along the straight part of the band's side; the
    /// rail is centred on it.</summary>
    ActivitySidebar.Dock? ActivityDock()
    {
        if (Surface.ActualWidth <= 0 || Root.XamlRoot == null || _glowShape.w <= 0) return null;
        double s = Root.XamlRoot.RasterizationScale;
        var at = Surface.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var origin = new POINT();
        ClientToScreen(WinRT.Interop.WindowNative.GetWindowHandle(this), ref origin);
        var (inset, w, h, r, _, _, _) = _glowShape;
        double phoneX = origin.X + at.X * s, phoneY = origin.Y + at.Y * s;
        double bandLeft = phoneX + inset * s, bandRight = bandLeft + w * s;
        double top = phoneY + (inset + r) * s, height = (h - 2 * r) * s;
        if (height <= 0) return null;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _activityLeft = bandRight + ActivityWidth * s > area.X + area.Width;

        return new ActivitySidebar.Dock(_activityLeft ? bandLeft : bandRight, top, height, _activityLeft, s);
    }

    bool ActivityShown => _activityWant || ((_activityAgent || _activityLinger) && !_activityDismissed);

    /// <summary>Follow the phone; open it once the phone is laid out if it should show.</summary>
    void PlaceActivity()
    {
        if (!ActivityShown || !ActivityHostVisible) return;
        bool wasLeft = _activityLeft;
        if (ActivityDock() is not ActivitySidebar.Dock d) return;
        if (_activity is not { IsOpen: true })
        {
            OpenActivity(d);
            return;
        }
        if (wasLeft != _activityLeft) _activity.SlideOpen(d);  // out again on the other side
        else _activity.Place(d);
    }

    /// <summary>Hidden to the tray or minimized: gone with the mirror; back, without the slide, when it returns.</summary>
    void ShowActivityHost()
    {
        if (_activity is not { IsOpen: true }) return;
        _activity.SetHostVisible(ActivityHostVisible);
        if (ActivityHostVisible) PlaceActivity();
    }

    void OpenActivity(ActivitySidebar.Dock d)
    {
        if (_activity == null)
        {
            _activity = new ActivitySidebar(WinRT.Interop.WindowNative.GetWindowHandle(this), _darkMode);
            _activity.SetTopmost(_alwaysOnTop);
            _activity.ClearRequested += () => _ = ClearActivity();
            _conn.ConnectedChanged += on =>
            {
                if (on) _ui.TryEnqueue(() => { if (_activity is { IsOpen: true }) _ = LoadActivity(); });
            };
        }
        _ = LoadActivity();
        _activity.SlideOpen(d);
    }

    /// <summary>Show it if it should show, slide it away if not.</summary>
    void UpdateActivity()
    {
        if (ActivityShown) PlaceActivity();
        else _activity?.SlideClosed();
        ShowActivityButton();
    }

    /// <summary>The bar's button: on and off, as it looks now (shown for an agent counts as on).</summary>
    void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        bool show = !ActivityShown;
        _activityWant = show;
        AppSettings.ActivityOpen = show;
        _activityDismissed = !show && (_activityAgent || _activityLinger);
        _activityLinger = false;
        UpdateActivity();
    }

    /// <summary>The agent's state from a status message (ShowAgent): connected is driving or paused.</summary>
    void ActivityAgentChanged(string agent)
    {
        bool on = agent is "driving" or "paused";
        ShowActivityButton();  // driving and paused look different on the button
        if (on == _activityAgent) return;
        _activityAgent = on;
        if (on)
        {
            _activityDismissed = false;  // a new agent: shown again
            _activityLinger = false;
            _activityLingerTimer?.Stop();
        }
        else if (!_activityWant && !_activityDismissed)
        {
            _activityLinger = true;
            if (_activityLingerTimer == null)
            {
                _activityLingerTimer = _ui.CreateTimer();
                _activityLingerTimer.Interval = TimeSpan.FromSeconds(4);
                _activityLingerTimer.IsRepeating = false;
                _activityLingerTimer.Tick += (_, _) => { _activityLinger = false; UpdateActivity(); };
            }
            _activityLingerTimer.Start();
        }
        else _activityDismissed = false;
        UpdateActivity();
    }

    async Task LoadActivity()
    {
        var r = await _conn.GetAsync("api/agent/activity");
        if (r == null || r["_failed"] != null || r["entries"] is not JsonArray list || _activity == null) return;
        var entries = list.OfType<JsonObject>().ToList();
        _activity.SetEntries(entries);
        // a reload is authoritative: after a core restart the server's ids start again at 1
        _activityLastId = entries.Select(x => (long?)x["id"] ?? 0).DefaultIfEmpty(0).Max();
    }

    async Task ClearActivity()
    {
        var r = await _conn.PostAsync("api/agent/activity/clear", new { });
        if (r == null || r["_failed"] != null) return;
        _activity?.SetEntries(Array.Empty<JsonObject>());
    }

    /// <summary>A new entry from the server (an "activity" message, on the UI thread like the others).</summary>
    void OnActivity(JsonObject m)
    {
        if (_activity == null || m["entry"] is not JsonObject entry) return;
        long id = (long?)entry["id"] ?? 0;
        if (id <= _activityLastId) return;
        _activityLastId = id;
        _activity.Add(entry);
    }
}

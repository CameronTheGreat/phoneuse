using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PhoneUseMirror;

/// <summary>
/// The mirror window: the phone's picture inside a phone-shaped bezel. Mouse and
/// keys over it go to the phone through phoneuse's browser-mirror server, exactly
/// as the browser page's do; positions are fractions of the screen, and outside
/// 0..1 is the bezel (a press there becomes a touch at the nearest edge).
/// </summary>
public sealed partial class MainWindow : Window
{
    readonly MirrorConnection _conn;
    readonly VideoPipeline _video;
    readonly DispatcherQueue _ui;
    readonly DispatcherQueueTimer _tick;

    // the server's last "state"
    bool _live, _mirroring, _stale, _awake;
    bool? _bluetooth;
    string _agent = "idle", _name = "Phone Use", _agentAction = "";
    double _screenW = 402, _screenH = 874, _bezel = 30.0 / 498, _radius = 60.0 / 498;

    bool _playing;          // a frame of the current stream is on screen
    bool _pressed;          // the left button went down over the phone
    long _troubleSince;     // when the current no-picture problem started (ticks), 0 = none
    CompositionRoundedRectangleGeometry? _clipGeometry;

    public MainWindow(int port)
    {
        InitializeComponent();
        _ui = DispatcherQueue.GetForCurrentThread();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        _video = new VideoPipeline(Video, _ui);
        Video.SizeChanged += (_, _) => ResizeVideo();
        Video.CompositionScaleChanged += (_, _) => ResizeVideo();
        SetupClip();
        SizeToPhone();

        _conn = new MirrorConnection(port);
        _video.KeyframeNeeded += () => _conn.Send(new { t = "keyframe" });
        _video.Playing += () => _ui.TryEnqueue(OnPlaying);
        _conn.Frame += (key, data) => { if (!_hidden) _video.Push(key, data); };  // straight from the socket's thread
        _conn.Message += m => _ui.TryEnqueue(() => OnMessage(m));
        _conn.ConnectedChanged += on => _ui.TryEnqueue(() => OnConnected(on));

        // handledEventsToo: children (the swap chain panel, the bezel) must not swallow hover moves
        Surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Surface_PointerMoved), true);
        Surface.PointerPressed += Surface_PointerPressed;
        Surface.PointerReleased += Surface_PointerReleased;
        Surface.PointerCanceled += (_, _) => Lift();
        Surface.PointerCaptureLost += (_, _) => Lift();
        Surface.PointerWheelChanged += Surface_PointerWheelChanged;
        Surface.PreviewKeyDown += (_, e) => OnKey(e, true);
        Surface.PreviewKeyUp += (_, e) => OnKey(e, false);
        Surface.Loaded += (_, _) => Surface.Focus(FocusState.Programmatic);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                _conn.Send(new { t = "blur" });  // let go of held keys and buttons
            else
                Surface.Focus(FocusState.Programmatic);
        };
        Closed += (_, _) => { _conn.Dispose(); _video.Dispose(); };

        _tick = _ui.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => UpdateNote();
        _tick.Start();
        InitTray(port);
        AssistiveTouchBar.IsOpen = AppSettings.AssistiveTouchReminder;

        UpdateNote();
        _conn.Start();
    }

    // ---------------------------------------------------------------- server events

    void OnConnected(bool on)
    {
        if (on)
        {
            _video.Reset();
            _ = LoadPaired();  // the pairing bar, once phoneuse answers
        }
        else
        {
            _video.Stop();
            _playing = false;
        }
        UpdateNote();
    }

    void OnPlaying()
    {
        if (_playing) return;
        _playing = true;
        _conn.Send(new { t = "playing" });
        UpdateNote();
    }

    void OnMessage(JsonObject m)
    {
        switch ((string?)m["type"])
        {
            case "state": OnState(m); break;
            case "agent-cursor":
                ShowAgentDot((double?)m["x"] ?? 0, (double?)m["y"] ?? 0, (bool?)m["down"] ?? false);
                break;
            case "agent-action":
                _agentAction = (string?)m["action"] ?? "";
                if (_agent == "driving") AgentWhat.Text = _agentAction;
                break;
            case "unlock-request":
                ShowFromTray();
                _ = ShowAsk((string?)m["id"], "unlock", null, null);
                break;
            case "session-request":
                ShowFromTray();
                _ = ShowAsk((string?)m["id"], "session", (string?)m["action"], (double?)m["deadline"]);
                break;
            case "unlock-done": HideAsk("unlock"); break;
            case "session-done": HideAsk("session"); break;
        }
    }

    void OnState(JsonObject m)
    {
        var wasLive = _live;
        _live = (bool?)m["live"] ?? false;
        _mirroring = (bool?)m["mirroring"] ?? false;
        _stale = (bool?)m["stale"] ?? false;
        _awake = (bool?)m["awake"] ?? false;
        _bluetooth = (bool?)m["bluetooth"];
        BluetoothBar.IsOpen = _bluetooth == false;  // not closable: it stays while the link is down
        _agent = (string?)m["agent"] ?? "idle";
        _name = (string?)m["name"] ?? _name;
        if (m["screen"] is JsonArray s && s.Count == 2)
        {
            _screenW = (double?)s[0] ?? _screenW;
            _screenH = (double?)s[1] ?? _screenH;
        }
        _bezel = (double?)m["bezel"] ?? _bezel;
        _radius = (double?)m["radius"] ?? _radius;

        if (!_live && wasLive)
        {
            _playing = false;
            _video.Stop();  // never leave an old picture up
        }
        else if (_live && !wasLive && !_playing)
        {
            _video.Reset();
        }
        Video.Visibility = _live ? Visibility.Visible : Visibility.Collapsed;
        Video.Opacity = _stale ? 0.55 : 1;

        if (AwakeSwitch.IsOn != _awake)
        {
            _settingUp = true;
            AwakeSwitch.IsOn = _awake;
            _settingUp = false;
        }
        AppTitleBar.Subtitle = !_live ? (_mirroring ? "Starting" : "Not mirroring") : _stale ? "Picture is old" : "Live";
        Layout();
        ShowAgent();
        UpdateNote();
    }

    // ---------------------------------------------------------------- notes

    // Notes only appear when there's no picture: short gaps show nothing at all.
    void UpdateNote()
    {
        var connected = _conn?.Connected ?? false;
        if (connected && !_live && !_mirroring)
        {
            SetNote($"Screen Mirroring stopped, or the iPhone is locked.\nUnlock it, then open Control Center › Screen Mirroring › {_name}");
            return;
        }
        if (connected && _live && _stale)
        {
            SetNote("This picture is old.\nThe iPhone's screen is off, or mirroring stalled. Wake the phone, or reconnect Screen Mirroring.");
            return;
        }
        if (_playing) { _troubleSince = 0; SetNote(null); return; }
        if (_troubleSince == 0) _troubleSince = Environment.TickCount64;
        if (Environment.TickCount64 - _troubleSince < 5000) { SetNote(null); return; }
        if (!connected) { SetNote("Waiting for phoneuse up"); return; }
        if (_live) SetNote("The iPhone is mirroring, but no picture has arrived.\nStop and start Screen Mirroring once.");
        else SetNote(null);
    }

    void SetNote(string? text)
    {
        NoteBox.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text != null) NoteText.Text = text;
    }

    // ---------------------------------------------------------------- layout

    void SetupClip()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Screen);
        var comp = visual.Compositor;
        _clipGeometry = comp.CreateRoundedRectangleGeometry();
        visual.Clip = comp.CreateGeometricClip(_clipGeometry);
    }

    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => Layout();

    void ResizeVideo()
    {
        if (Video.ActualWidth > 0 && Video.ActualHeight > 0)
            _video.Resize(Video.ActualWidth, Video.ActualHeight, Video.CompositionScaleX, Video.CompositionScaleY);
    }

    /// <summary>The phone body fits the window; the screen keeps the phone's shape.</summary>
    void Layout()
    {
        double W = Stage.ActualWidth, H = Stage.ActualHeight;
        if (W <= 0 || H <= 0) return;
        double aspect = _screenW / _screenH;
        double dw = 1 + 2 * _bezel, dh = 1 / aspect + 2 * _bezel;
        const double below = 64, above = 8;  // room for the agent button below
        double u = Math.Min((W - 24) / dw, (H - below - above) / dh);
        if (u <= 10) return;

        double pw = dw * u, ph = dh * u, b = _bezel * u, sw = u, sh = u / aspect, sr = _radius * u;
        Surface.Width = pw;
        Surface.Height = ph;
        Surface.Margin = new Thickness(0, Math.Max(above, (H - below - ph) / 2), 0, 0);
        Body.CornerRadius = new CornerRadius((_radius + _bezel) * u);

        Screen.Margin = new Thickness(b, b, 0, 0);
        Screen.Width = sw;
        Screen.Height = sh;
        DotLayer.Width = sw;
        DotLayer.Height = sh;
        if (_clipGeometry != null)
        {
            _clipGeometry.Size = new Vector2((float)sw, (float)sh);
            _clipGeometry.CornerRadius = new Vector2((float)sr, (float)sr);
        }

        Rim.Margin = new Thickness(b - 2, b - 2, 0, 0);
        Rim.Width = sw + 4;
        Rim.Height = sh + 4;
        Rim.CornerRadius = new CornerRadius(sr + 2);
    }

    /// <summary>A phone-shaped window, about 85% of the work area's height, centered.</summary>
    void SizeToPhone()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        double aspect = _screenW / _screenH;
        double dw = 1 + 2 * _bezel, dh = 1 / aspect + 2 * _bezel;
        int h = (int)(area.Height * 0.85);
        double chrome = (48 + 72) * scale;  // title bar + room below the phone
        int w = (int)((h - chrome) * dw / dh + 24 * scale);
        AppWindow.Resize(new SizeInt32(w, h));
        AppWindow.Move(new PointInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---------------------------------------------------------------- mouse

    // While an agent drives the phone, this window sends no input: mixing in clicks
    // and keys would confuse it. "Take over" stops the agent.
    bool Active => _live && _playing && _agent != "driving";

    (double x, double y) At(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Screen).Position;
        return (p.X / Math.Max(1, Screen.ActualWidth), p.Y / Math.Max(1, Screen.ActualHeight));
    }

    void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!Active) return;
        var (x, y) = At(e);
        _conn.Send(new { t = "move", x, y });
    }

    void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Surface.Focus(FocusState.Pointer);
        var props = e.GetCurrentPoint(Surface).Properties;
        if (!props.IsLeftButtonPressed || !Active) return;
        Surface.CapturePointer(e.Pointer);
        _pressed = true;
        var (x, y) = At(e);
        // The server presses where the phone's pointer already hovers: put it here first,
        // or the press lands at the last hover spot and drags over to this one.
        _conn.Send(new { t = "move", x, y });
        _conn.Send(new { t = "down", x, y });
        e.Handled = true;
    }

    void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        Lift();
        Surface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    void Lift()
    {
        if (!_pressed) return;
        _pressed = false;
        _conn.Send(new { t = "up" });
    }

    // Wheel and touchpad scrolling: the server turns it into a drag (iOS ignores the
    // wheel). One notch is 100 px, as in the browser.
    void Surface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!Active) return;
        var props = e.GetCurrentPoint(Screen).Properties;
        double px = props.MouseWheelDelta / 120.0 * 100;
        double w = Math.Max(1, Screen.ActualWidth), h = Math.Max(1, Screen.ActualHeight);
        var (x, y) = At(e);
        double dx = props.IsHorizontalMouseWheel ? -px / w : 0;
        double dy = props.IsHorizontalMouseWheel ? 0 : px / h;
        _conn.Send(new { t = "scroll", x, y, dx, dy });
    }

    // ---------------------------------------------------------------- keyboard

    void OnKey(KeyRoutedEventArgs e, bool down)
    {
        if (!Active) return;
        var st = e.KeyStatus;
        uint scan = st.ScanCode;
        bool ext = st.IsExtendedKey;
        if (KeyMap.Modifier(scan, ext) is int bit)
        {
            e.Handled = true;
            if (down && st.WasKeyDown) return;
            _conn.Send(new { t = "mod", bit, down });
            return;
        }
        if (KeyMap.Consumer(scan, ext) is string key)
        {
            e.Handled = true;
            if (!(down && st.WasKeyDown)) _conn.Send(new { t = "consumer", key, down });
            return;
        }
        if (KeyMap.Usage(scan, ext) is not int usage) return;
        e.Handled = true;
        if (down && st.WasKeyDown) return;  // the phone repeats keys itself
        _conn.Send(new { t = "key", usage, down });
    }

    // ---------------------------------------------------------------- agents

    void ShowAgent()
    {
        bool driving = _agent == "driving";
        if (!driving) AgentDot.Visibility = Visibility.Collapsed;
        Rim.Visibility = driving ? Visibility.Visible : Visibility.Collapsed;
        TakeoverButton.Visibility = !_live && _agent == "idle" ? Visibility.Collapsed : Visibility.Visible;
        TakeoverButton.IsEnabled = _agent != "idle";
        TakeoverLabel.Text = driving ? "Agent in control" : _agent == "paused" ? "You took over" : "You're in control";
        TakeoverAction.Text = driving ? "Take over" : _agent == "paused" ? "Resume agent" : "";
        TakeoverAction.Visibility = _agent == "idle" ? Visibility.Collapsed : Visibility.Visible;
        AgentLight.Fill = new SolidColorBrush(driving
            ? Windows.UI.Color.FromArgb(255, 10, 132, 255)
            : _agent == "paused" ? Windows.UI.Color.FromArgb(255, 255, 159, 10) : Windows.UI.Color.FromArgb(255, 48, 209, 88));
        AgentWhat.Text = driving ? _agentAction : "";
    }

    void ShowAgentDot(double x, double y, bool down)
    {
        Canvas.SetLeft(AgentDot, x * Screen.ActualWidth - AgentDot.Width / 2);
        Canvas.SetTop(AgentDot, y * Screen.ActualHeight - AgentDot.Height / 2);
        AgentDot.Fill = new SolidColorBrush(down
            ? Windows.UI.Color.FromArgb(200, 10, 132, 255) : Windows.UI.Color.FromArgb(100, 255, 255, 255));
        AgentDot.Visibility = Visibility.Visible;
    }

    async void Takeover_Click(object sender, RoutedEventArgs e)
    {
        if (_agent == "idle") return;
        await _conn.PostAsync("api/agent/takeover", new { take = _agent != "paused" });
    }

    // ---------------------------------------------------------------- requests from agents

    ContentDialog? _ask;
    string? _askId;
    string _askKind = "unlock";
    DispatcherQueueTimer? _askTimer;

    async Task ShowAsk(string? id, string kind, string? action, double? deadline)
    {
        if (id == null || id == _askId) return;
        HideAsk(_askKind);
        _askId = id;
        _askKind = kind;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (kind == "session" && deadline is double dl)
        {
            dialog.PrimaryButtonText = "Allow now";
            dialog.CloseButtonText = "Cancel";
            dialog.Content = action ?? "";
            void Tick()
            {
                var left = Math.Max(0, Math.Ceiling(dl - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
                dialog.Title = $"Agent taking control in {left} s";
            }
            Tick();
            _askTimer = _ui.CreateTimer();
            _askTimer.Interval = TimeSpan.FromMilliseconds(250);
            _askTimer.Tick += (_, _) => Tick();
            _askTimer.Start();
        }
        else
        {
            dialog.Title = kind == "session" ? "Agent requests control" : "Agent requests an unlock";
            dialog.Content = kind == "session" ? action ?? "" : "It types your saved passcode once.";
            dialog.PrimaryButtonText = kind == "session" ? "Allow" : "Unlock";
            dialog.CloseButtonText = "Deny";
        }
        _ask = dialog;
        bool answered = false;
        dialog.PrimaryButtonClick += (_, _) => answered = true;
        dialog.CloseButtonClick += (_, _) => answered = true;
        var result = await dialog.ShowAsync();
        if (_ask == dialog)
        {
            _askTimer?.Stop();
            _ask = null;
            if (answered)
                await _conn.PostAsync($"api/{kind}/answer", new { id, approve = result == ContentDialogResult.Primary });
            _askId = null;
        }
    }

    void HideAsk(string kind)
    {
        if (_ask == null || kind != _askKind) return;
        var dialog = _ask;
        _ask = null;
        _askId = null;
        _askTimer?.Stop();
        dialog.Hide();
    }
}

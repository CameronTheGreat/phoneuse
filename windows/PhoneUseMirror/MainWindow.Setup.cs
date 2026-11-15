using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace PhoneUseMirror;

/// <summary>
/// First-time setup: a checklist over the phone that ticks itself off as each step is seen
/// done (GET /api/app "setup", from overview.checklist: the firewall, Screen Mirroring,
/// Bluetooth, AssistiveTouch and the pointer's measurement, the Phone Use app, Claude
/// Code), plus this window's own extras (screen reading, start with Windows). It opens by
/// itself until it's finished or skipped (AppSettings.SetupDone); Settings › Setup guide
/// opens it again. "Show the phone" hides it, and a button over the phone brings it back.
/// </summary>
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _setupPoll;
    JsonArray? _setupItems;
    string? _setupBusy, _setupError;  // the step whose action runs; what went wrong with it
    bool _setupLoading;

    static readonly Brush SetupGreen = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x30, 0xD1, 0x58));

    /// <summary>At launch: the checklist, until setup is finished or skipped.</summary>
    void StartSetup()
    {
        if (!AppSettings.SetupDone) ShowSetup();
    }

    void ShowSetup()
    {
        SetupOverlay.Visibility = Visibility.Visible;
        SetupResume.Visibility = Visibility.Collapsed;
        AssistiveTouchBar.IsOpen = false;  // the checklist says it, as a step
        PairingBar.IsOpen = false;
        _conn.Send(new { t = "blur" });
        if (_setupPoll == null)
        {
            _setupPoll = _ui.CreateTimer();
            _setupPoll.Interval = TimeSpan.FromSeconds(2);
            _setupPoll.Tick += async (_, _) => await LoadSetup();
        }
        _setupPoll.Start();
        DrawSetup();
        _ = LoadSetup();
    }

    void HideSetup(bool resume)
    {
        SetupOverlay.Visibility = Visibility.Collapsed;
        _setupPoll?.Stop();
        int left = SetupLeft();
        SetupResume.Content = left > 0 ? $"Finish setup ({left} left)" : "Finish setup";
        SetupResume.Visibility = resume && !AppSettings.SetupDone ? Visibility.Visible : Visibility.Collapsed;
        Surface.Focus(FocusState.Programmatic);
    }

    void SetupGuide_Click(object sender, RoutedEventArgs e)
    {
        SettingsFlyout.Hide();
        ShowSetup();
    }

    void SetupResume_Click(object sender, RoutedEventArgs e) => ShowSetup();

    int SetupLeft() => _setupItems?.OfType<JsonObject>()
        .Count(i => (bool?)i["done"] != true && (bool?)i["optional"] != true) ?? 0;

    async Task LoadSetup()
    {
        if (_setupLoading) return;
        _setupLoading = true;
        try
        {
            var j = await _conn.CallAsync(System.Net.Http.HttpMethod.Get, "api/app", null, 15);
            if (j?["setup"] is JsonArray items) _setupItems = items;
            else if (j?["_failed"] != null) _setupItems = null;
            if (SetupOverlay.Visibility == Visibility.Visible) DrawSetup(j?["_failed"] != null);
        }
        finally { _setupLoading = false; }
    }

    // ------------------------------------------------------------------ drawing

    string? _setupDrawn;  // what the checklist shows now: it's drawn again only when that changes

    void DrawSetup(bool unreachable = false)
    {
        string key = $"{unreachable}|{_setupItems?.ToJsonString()}|{_setupBusy}|{_setupError}|{VisionInstalled}|{_visionJob}|{_visionStep}";
        if (key == _setupDrawn) return;  // a click mustn't land on a button that is being replaced
        _setupDrawn = key;
        var body = SetupBody;
        body.Children.Clear();

        var head = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(new TextBlock { Text = "Set up Phone Use", Style = Res<Style>("SubtitleTextBlockStyle") });
        var items = _setupItems?.OfType<JsonObject>().ToList() ?? [];
        var required = items.Where(i => (bool?)i["optional"] != true).ToList();
        int done = required.Count(i => (bool?)i["done"] == true);
        titles.Children.Add(new TextBlock
        {
            Text = unreachable ? "Waiting for phoneuse to start…"
                 : items.Count == 0 ? "Checking…"
                 : done == required.Count ? "Everything's set up." : $"{done} of {required.Count} done",
            Opacity = 0.7, Style = Res<Style>("CaptionTextBlockStyle"),
        });
        head.Children.Add(titles);
        var hide = new Button { Content = "Show the phone", VerticalAlignment = VerticalAlignment.Center };
        hide.Click += (_, _) => HideSetup(resume: true);
        Grid.SetColumn(hide, 1);
        head.Children.Add(hide);
        body.Children.Add(head);

        // the first step not done is the current one: it shows its whole text
        var current = required.FirstOrDefault(i => (bool?)i["done"] != true);
        int n = 0;
        foreach (var item in required) body.Children.Add(StepCard(item, ++n, item == current));

        if (items.Count > 0 && current == null)
        {
            var finish = new Button { Content = "Done", Style = Res<Style>("AccentButtonStyle"), HorizontalAlignment = HorizontalAlignment.Stretch };
            finish.Click += (_, _) => { AppSettings.SetupDone = true; HideSetup(resume: false); };
            body.Children.Add(Para("Phone Use is ready. Agents can use the phone, and you can watch and take over here.", 0.8));
            body.Children.Add(finish);
        }

        // extras: optional, any time
        body.Children.Add(new TextBlock { Text = "Extras", Style = Res<Style>("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 10, 0, 0) });
        foreach (var item in items.Where(i => (bool?)i["optional"] == true)) body.Children.Add(StepCard(item, 0, false));
        body.Children.Add(VisionCard());
        body.Children.Add(StartupCard());

        if (current != null)
        {
            var skip = new HyperlinkButton { Content = "Skip setup", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            skip.Click += (_, _) => { AppSettings.SetupDone = true; HideSetup(resume: false); };
            body.Children.Add(skip);
        }
    }

    Border StepCard(JsonObject item, int number, bool current)
    {
        string id = (string?)item["id"] ?? "";
        bool done = (bool?)item["done"] == true;
        string? bold = (string?)item["bold"];
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(Badge(number, done, current));

        var col = new StackPanel { Spacing = 6 };
        col.Children.Add(new TextBlock
        {
            Text = (string?)item["title"] ?? id, Style = Res<Style>("BodyStrongTextBlockStyle"),
            Opacity = done && !current ? 0.6 : 1, TextWrapping = TextWrapping.Wrap,
        });
        if (!done || current)  // done steps fold to their title
        {
            if (item["steps"] is JsonArray steps)
            {
                int k = 0;
                foreach (var s in steps)
                    col.Children.Add(Rich($"{++k}. " + ((string?)s ?? "").Replace("{mirroring} ", ""), bold));
            }
            var detail = (string?)item["detail"];
            if (!string.IsNullOrEmpty(detail)) col.Children.Add(Rich(detail, bold));
            var action = StepAction(id, (string?)item["action"], done);
            if (action != null) col.Children.Add(action);
            if (_setupError != null && _setupBusy == null && id == _setupErrorStep)
                col.Children.Add(new TextBlock { Text = _setupError, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x45, 0x3A)), TextWrapping = TextWrapping.Wrap });
        }
        Grid.SetColumn(col, 1);
        g.Children.Add(col);
        return new Border
        {
            Child = g, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(8),
            Background = Res<Brush>(current ? "CardBackgroundFillColorSecondaryBrush" : "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = current ? Res<Brush>("AccentFillColorDefaultBrush") : Res<Brush>("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(current ? 1.5 : 1),
        };
    }

    string? _setupErrorStep;

    FrameworkElement? StepAction(string id, string? action, bool done)
    {
        if (done || action == null) return null;
        bool busy = _setupBusy == id;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        Button? b = action switch
        {
            "allow_firewall" => new Button { Content = busy ? "Waiting for Windows…" : "Allow", Style = Res<Style>("AccentButtonStyle") },
            "pair_app" => new Button { Content = "Show Pairing Code", Style = Res<Style>("AccentButtonStyle") },
            "claude" => new Button { Content = busy ? "Connecting…" : "Connect" },
            _ => null,
        };
        if (b != null)
        {
            b.IsEnabled = !busy;
            b.Click += async (_, _) => await RunSetupAction(id, action);
            row.Children.Add(b);
        }
        if (action == "setup_bridge")  // a link: the bridge is the other way to be the phone's keyboard
        {
            var link = new HyperlinkButton { Content = "Use a bridge instead", Padding = new Thickness(0) };
            link.Click += (_, _) => BridgeSettings_Click(link, new RoutedEventArgs());
            row.Children.Add(link);
        }
        if (busy) row.Children.Add(new ProgressRing { IsActive = true, Width = 18, Height = 18 });
        return row.Children.Count > 0 ? row : null;
    }

    async Task RunSetupAction(string id, string action)
    {
        if (action == "pair_app")
        {
            Pair_Click(this, new RoutedEventArgs());
            return;
        }
        _setupBusy = id;
        _setupError = null;
        DrawSetup();
        JsonObject? r = action switch
        {
            "allow_firewall" => await _conn.CallAsync(System.Net.Http.HttpMethod.Post, "api/setup/firewall", new { }, 300),
            "claude" => await _conn.PostAsync("api/claude", new { on = true }),
            _ => null,
        };
        _setupBusy = null;
        var err = (string?)r?["error"];
        if (err != null)
        {
            _setupError = err == "cancelled" ? "Windows didn't get permission. Try again and choose Yes." : err;
            _setupErrorStep = id;
        }
        await LoadSetup();
        DrawSetup();
    }

    Border VisionCard()
    {
        bool on = VisionInstalled, busy = _visionJob != VisionJob.None;
        var item = new JsonObject
        {
            ["id"] = "vision", ["title"] = "Screen reading", ["done"] = on, ["optional"] = true,
            ["detail"] = busy ? "Installing (several GB, can take a while)… " + _visionStep
                       : "Local models that let agents read the screen, tap text, type and open apps by name. A few GB.",
        };
        var card = StepCard(item, 0, false);
        if (!on && Backend.Root != null && card.Child is Grid g && g.Children[^1] is StackPanel col)
        {
            var b = new Button { Content = busy ? "Installing…" : "Install", IsEnabled = !busy };
            b.Click += (_, _) => { Vision_Click(b, new RoutedEventArgs()); DrawSetup(); };
            col.Children.Add(b);
        }
        return card;
    }

    Border StartupCard()
    {
        var item = new JsonObject
        {
            ["id"] = "startup", ["title"] = "Start with Windows", ["done"] = false, ["optional"] = true,
            ["detail"] = "phoneuse starts when you sign in, with Phone Use waiting in the tray, so agents can always reach the phone.",
        };
        var card = StepCard(item, 0, false);
        if (card.Child is Grid g && g.Children[^1] is StackPanel col)
        {
            var sw = new ToggleSwitch { IsOn = Startup.Enabled, OnContent = "On", OffContent = "Off" };
            sw.Toggled += (_, _) =>
            {
                try { Startup.Enabled = sw.IsOn; }
                catch (Exception) { sw.IsOn = Startup.Enabled; }
            };
            col.Children.Add(sw);
        }
        return card;
    }

    // ------------------------------------------------------------------ pieces

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    static FrameworkElement Badge(int number, bool done, bool current)
    {
        var circle = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
            Background = done ? SetupGreen : current ? Res<Brush>("AccentFillColorDefaultBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 255, 255, 255)),
        };
        circle.Child = done
            ? new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) }
            : number > 0
                ? new TextBlock
                {
                    Text = number.ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(current ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
                }
                : new FontIcon { Glyph = "", FontSize = 11 };  // an extra: a plus
        return circle;
    }

    static TextBlock Para(string text, double opacity) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

    /// <summary>Text with `bold` (a name: this PC's, the bridge's) in bold wherever it appears.</summary>
    static TextBlock Rich(string text, string? bold)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85, IsTextSelectionEnabled = true };
        if (string.IsNullOrEmpty(bold) || !text.Contains(bold))
        {
            t.Text = text;
            return t;
        }
        var parts = text.Split(bold);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) t.Inlines.Add(new Run { Text = parts[i] });
            if (i < parts.Length - 1) t.Inlines.Add(new Run { Text = bold, FontWeight = FontWeights.SemiBold });
        }
        return t;
    }
}

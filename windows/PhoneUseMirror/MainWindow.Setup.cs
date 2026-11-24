using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using static PhoneUseMirror.Ui;

namespace PhoneUseMirror;

/// <summary>
/// First-time setup, one stage at a time over the phone. It starts by asking what Phone Use is for
/// (AppSettings.SetupMode): the phone from this PC ("desktop"), or that and AI agents ("agent").
///   Connect   the firewall (only when Windows needs a rule) and Screen Mirroring
///   Control   this PC's Bluetooth (or a bridge), AssistiveTouch and the pointer's measurement
///   Agents    (agent path only) the Phone Use app, Claude Code and screen reading: the cards in
///             AgentSetupPanel, which MainWindow.Pairing.cs and MainWindow.Vision.cs keep current
///   Done      Start with Windows, and what to try
/// Each step ticks itself off as it's seen done (GET /api/app "setup", from overview.checklist); the
/// first stage with something left is the one shown, and the ones before it can be looked at again.
/// It opens by itself until it's finished or skipped (AppSettings.SetupDone). Settings › Setup guide
/// starts it over, and Settings › Agent setup opens the Agents stage. "Show the phone" hides it, and a
/// button over the phone brings it back.
/// </summary>
public sealed partial class MainWindow
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _setupPoll;
    JsonArray? _setupItems;
    string? _setupBusy, _setupError;  // the step whose action runs; what went wrong with it
    bool _setupLoading;
    string? _viewStage;    // a stage looked at again (the progress bar), else the current one
    bool _agentContinued;  // Agents stage: Continue pressed (its optional cards are worth a look first)

    /// <summary>At launch: the checklist, until setup is finished or skipped.</summary>
    void StartSetup()
    {
        if (!AppSettings.SetupDone) ShowSetup();
    }

    void ShowSetup(string? stage = null)
    {
        _viewStage = stage;
        SetupOverlay.Visibility = Visibility.Visible;
        SetupResume.Visibility = Visibility.Collapsed;
        CalibrationOverlay.Visibility = Visibility.Collapsed;
        _conn.Send(new { t = "blur" });
        if (_setupPoll == null)
        {
            _setupPoll = _ui.CreateTimer();
            _setupPoll.Interval = TimeSpan.FromSeconds(2);
            _setupPoll.Tick += async (_, _) => await LoadSetup();
        }
        _setupPoll.Start();
        ShowVision();
        DrawSetup();
        _ = LoadSetup();
    }

    void HideSetup(bool resume)
    {
        SetupOverlay.Visibility = Visibility.Collapsed;
        _setupPoll?.Stop();
        ShowCalibration();
        int left = SetupLeft();
        SetupResume.Content = left > 0 ? $"Finish setup ({left} left)" : "Finish setup";
        SetupResume.Visibility = resume && !AppSettings.SetupDone ? Visibility.Visible : Visibility.Collapsed;
        Surface.Focus(FocusState.Programmatic);
    }

    /// <summary>Settings › Setup guide: from the top, asking again how the phone gets its mouse and
    /// keyboard (the saved choice is cleared; a bridge that is set up still counts as chosen).</summary>
    async void SetupGuide_Click(object sender, RoutedEventArgs e)
    {
        HideSettings();
        await _conn.PostAsync("api/setup/input", new { choice = (string?)null });
        AppSettings.SetupMode = null;
        _agentContinued = false;
        _welcomePick = null;
        _welcomeNudge = false;
        ShowSetup();
    }

    /// <summary>Settings › Agent setup: the Agents stage (choosing the agent path, if it wasn't).</summary>
    void AgentSetup_Click(object sender, RoutedEventArgs e)
    {
        HideSettings();
        if (AppSettings.SetupMode != "agent") ChooseMode("agent");
        _agentContinued = false;
        ShowSetup("agent");
    }

    void SetupResume_Click(object sender, RoutedEventArgs e) => ShowSetup();

    int SetupLeft() => Stages().Sum(st => StageItems(st).Count(i => (bool?)i["done"] != true && (bool?)i["optional"] != true));

    async Task LoadSetup()
    {
        if (_setupLoading) return;
        _setupLoading = true;
        try
        {
            var j = await _conn.CallAsync(System.Net.Http.HttpMethod.Get, "api/app", null, 15);
            if (j?["setup"] is JsonArray items)
            {
                _setupItems = items;
                // AssistiveTouch: done once you say so (the pointer's measurement still runs by itself),
                // unless that measurement waits for you (turn the phone upright) or failed
                if (AppSettings.AssistiveTouchDone && CalState is not ("waiting_upright" or "running" or "failed"))
                    foreach (var i in items.OfType<JsonObject>().Where(i => (string?)i["id"] == "pointer"))
                        i["done"] = true;
            }
            else if (j?["_failed"] != null) _setupItems = null;
            if (j != null && j["_failed"] == null) ShowPaired((int?)(double?)j["paired"] ?? 0);
            if (SetupOverlay.Visibility == Visibility.Visible) DrawSetup(j?["_failed"] != null);
        }
        finally { _setupLoading = false; }
    }

    // ------------------------------------------------------------------ stages

    static readonly (string Id, string Label, string Title, string Text)[] StageInfo =
    {
        ("connect", "Connect", "Show the iPhone here", "Screen Mirroring sends the phone's screen to this PC."),
        ("control", "Control", "Control it from this PC", "Phone Use becomes the iPhone's mouse and keyboard, over Bluetooth."),
        ("agent", "Agents", "Let agents use the phone", "Pair the Phone Use app: it's where you allow and stop agents. The rest is optional."),
    };

    /// <summary>The stages of the chosen path.</summary>
    IEnumerable<string> Stages() => AppSettings.SetupMode == "agent"
        ? new[] { "connect", "control", "agent" }
        : new[] { "connect", "control" };

    /// <summary>The checklist's steps in a stage, in order (the Agents stage's own cards aren't steps).</summary>
    List<JsonObject> StageItems(string stage)
    {
        string[] ids = stage switch
        {
            "connect" => new[] { "firewall", "mirroring" },
            "control" => new[] { "input", "bluetooth", "pointer" },
            "agent" => new[] { "app" },
            _ => Array.Empty<string>(),
        };
        var items = _setupItems?.OfType<JsonObject>().ToList() ?? [];
        return ids.Select(id => items.FirstOrDefault(i => (string?)i["id"] == id)).OfType<JsonObject>()
            // the mouse-and-keyboard choice shows only while it's still open (a bridge chosen, not set up yet)
            .Where(i => (string?)i["id"] != "input" || (bool?)i["done"] != true)
            .ToList();
    }

    bool StageDone(string stage) =>
        _setupItems != null && StageItems(stage).All(i => (bool?)i["done"] == true || (bool?)i["optional"] == true)
        && (stage != "agent" || _agentContinued);

    /// <summary>The first stage with something left, or "done".</summary>
    string CurrentStage() => Stages().FirstOrDefault(st => !StageDone(st)) ?? "done";

    /// <summary>The path chosen. The phone's mouse and keyboard default to this PC's Bluetooth (a bridge
    /// set up already counts as chosen), and the agent path starts the screen-reading download now, so
    /// it's ready by the time setup gets there.</summary>
    void ChooseMode(string mode)
    {
        AppSettings.SetupMode = mode;
        var input = _setupItems?.OfType<JsonObject>().FirstOrDefault(i => (string?)i["id"] == "input");
        if (input != null && (bool?)input["done"] != true && (string?)input["choice"] == null) _ = ChooseInput("pc");
        if (mode == "agent" && !VisionInstalled && _visionJob == VisionJob.None && Backend.Root != null)
            Vision_Click(this, new RoutedEventArgs());
        ShowPaired(_paired);  // the pairing reminder over the phone is for the agent path only
    }

    // ------------------------------------------------------------------ drawing

    string? _setupDrawn;  // what the setup shows now: it's drawn again only when that changes

    void DrawSetup(bool unreachable = false)
    {
        // everything the screens below are drawn from: a change to any of it draws them again
        string key = $"{unreachable}|{AppSettings.SetupMode}|{_welcomePick}|{_welcomeNudge}|{_viewStage}|{_agentContinued}|" +
                     $"{VisionInstalled}|{_setupItems?.ToJsonString()}|{_setupBusy}|{_setupError}";
        if (key == _setupDrawn) return;  // a click mustn't land on a button that is being replaced
        _setupDrawn = key;
        SetupBody.Children.Clear();
        SetupFoot.Children.Clear();
        ClaudeSlot.Children.Clear();
        AgentSetupPanel.Visibility = Visibility.Collapsed;

        if (AppSettings.SetupMode == null)
        {
            DrawWelcome();
            return;
        }
        string current = CurrentStage();
        string shown = _viewStage != null && Stages().Contains(_viewStage) ? _viewStage : current;
        SetupBody.Children.Add(SetupHeader(shown, current, unreachable));
        if (shown == "done") DrawDone();
        else DrawStage(shown, shown == current);
    }

    string? _welcomePick;  // the welcome screen's card picked, until Continue
    bool _welcomeNudge;    // Continue pressed with nothing picked

    void SetupScroll_SizeChanged(object sender, SizeChangedEventArgs e) => SetupLayout.MinHeight = e.NewSize.Height;

    // icons on the site's 24-unit grid (BrandIconPathStyle)
    const string IconDesktop = "M4,4 L20,4 A1,1 0 0 1 21,5 L21,15 A1,1 0 0 1 20,16 L4,16 A1,1 0 0 1 3,15 L3,5 A1,1 0 0 1 4,4 Z M8,20 L16,20 M12,16 L12,20";
    const string IconSparkle = "M12,3 L13.8,8.2 L19,10 L13.8,11.8 L12,17 L10.2,11.8 L5,10 L10.2,8.2 Z";

    void DrawWelcome()
    {
        SetupBody.Children.Add(Lockup());
        SetupBody.Children.Add(new TextBlock
        {
            Text = "How will you use your iPhone?", Style = Brand.Title, FontSize = 24, LineHeight = 27,
            TextWrapping = TextWrapping.Wrap, Foreground = Brand.Ink, Margin = new Thickness(0, 14, 0, 0),
        });
        SetupBody.Children.Add(new TextBlock
        {
            Text = "Pick one. You can add agents later in Settings.", Style = Brand.RowSub, FontSize = 13,
            Foreground = Brand.Ink2, Margin = new Thickness(0, 0, 0, 6),
        });
        SetupBody.Children.Add(PathCard("desktop", IconDesktop, false, "From this PC", "Mirror it, mouse and keyboard"));
        SetupBody.Children.Add(PathCard("agent", IconSparkle, true, "With AI agents too", "Adds the app, Claude Code, screen reading"));
        if (_welcomePick == "agent" && !VisionInstalled)
            SetupBody.Children.Add(new TextBlock
            {
                Text = "Screen reading (up to 2 GB) downloads in the background.", Style = Brand.RowSub,
                Foreground = Brand.AmberText, Margin = new Thickness(4, 0, 4, 0),
            });

        // never greyed out (a disabled plum pill all but vanishes on the cream): with nothing picked, it says so
        if (_welcomeNudge && _welcomePick == null)
            SetupFoot.Children.Add(new TextBlock
            {
                Text = "Pick one first.", Style = Brand.RowSub, Foreground = Brand.Brick,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        var go = _welcomePick != null ? PrimaryButton("Continue") : QuietButton("Continue");
        go.Click += (_, _) =>
        {
            if (_welcomePick == null) { _welcomeNudge = true; DrawSetup(); return; }
            ChooseMode(_welcomePick);
            _welcomePick = null;
            _welcomeNudge = false;
            DrawSetup();
        };
        SetupFoot.Children.Add(go);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
        foot.Children.Add(new TextBlock
        {
            Text = "About 5 minutes ·", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = Brand.Ink3,
        });
        var skip = SkipLink();
        skip.Margin = new Thickness(0);
        foot.Children.Add(skip);
        SetupFoot.Children.Add(foot);
    }

    /// <summary>One of the welcome screen's two paths: an icon tile, its name and what it includes, and a
    /// radio circle; picked, it's outlined in plum.</summary>
    Button PathCard(string mode, string icon, bool filledTile, string title, string text)
    {
        bool on = _welcomePick == mode;
        var g = Columns(12, "auto", "*", "auto");
        g.Children.Add(IconTile(icon, filledTile ? Brand.Plum : Brand.Cream, filledTile ? Brand.Bg : null));
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center }.AddTo(g, 1);
        col.Children.Add(new TextBlock { Text = title, Style = Brand.RowTitle });
        col.Children.Add(new TextBlock { Text = text, Style = Brand.RowSub, Foreground = Brand.Ink2 });
        new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center,
            Stroke = on ? Brand.Plum : Brand.Ink3, StrokeThickness = on ? 5 : 1.5,
            Fill = on ? new SolidColorBrush(Microsoft.UI.Colors.White) : null,
        }.AddTo(g, 2);
        var b = new Button
        {
            Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(on ? 13 : 14), CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(on ? 2 : 1),
        };
        // picked: plum outline and tint, a shade deeper under the pointer; not picked: the card, cream under the pointer
        if (on) Paint(b, Brand.PlumTint, Brand.PlumTintHover, Brand.PlumTintPressed, Brand.Plum, Brand.Plum);
        else Paint(b, Brand.Card, Brand.Cream, Brand.AmberTint, Brand.Line, Brand.LineStrong);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, title + (on ? ", selected" : ""));
        b.Click += (_, _) => { _welcomePick = mode; DrawSetup(); };
        return b;
    }

    /// <summary>The title, Show the phone, and the progress bar: a segment per stage (done, current, still
    /// to come); a click opens that stage.</summary>
    FrameworkElement SetupHeader(string shown, string current, bool unreachable)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 4, 0, 4) };
        var head = Columns(0, "*", "auto");
        var stages = Stages().ToList();
        head.Children.Add(Lockup());
        var hide = new Button
        {
            Content = "Show the phone", VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Height = 30,
            Padding = new Thickness(12, 0, 12, 1), CornerRadius = new CornerRadius(15),
        };
        Paint(hide, Brand.Card, Brand.Cream, Brand.AmberTint, Brand.Line, fg: Brand.Ink2);
        hide.Click += (_, _) => HideSetup(resume: true);
        hide.AddTo(head, 1);
        panel.Children.Add(head);

        var rail = new Grid { ColumnSpacing = 6 };
        for (int i = 0; i < stages.Count; i++)
        {
            rail.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string st = stages[i];
            bool done = StageDone(st), here = st == shown;
            var seg = new StackPanel { Spacing = 5 };
            seg.Children.Add(new Border
            {
                Height = 4, CornerRadius = new CornerRadius(2),
                Background = done ? Brand.Green : st == current ? Brand.Plum : Brand.Line,
            });
            seg.Children.Add(new TextBlock
            {
                Text = StageInfo.First(s => s.Id == st).Label, FontSize = 12,
                FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = here ? Brand.Ink : Brand.Ink3,
            });
            var b = new Button
            {
                Content = seg, Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, BorderThickness = new Thickness(0),
            };
            var clear = Brand.Transparent;
            Paint(b, clear, Brand.PlumWash, Brand.PlumWashStrong, clear);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, StageInfo.First(s => s.Id == st).Label +
                (done ? ", done" : st == current ? ", current" : ", still to come"));
            b.Click += (_, _) => { _viewStage = st == current ? null : st; DrawSetup(); };
            b.AddTo(rail, i);
        }
        panel.Children.Add(rail);
        if (unreachable || _setupItems == null)
            panel.Children.Add(new TextBlock
            {
                Text = unreachable ? "Waiting for phoneuse to start…" : "Checking…", Style = Brand.RowSub,
            });
        return panel;
    }

    void DrawStage(string stage, bool isCurrent)
    {
        var info = StageInfo.First(s => s.Id == stage);
        SetupBody.Children.Add(ScreenTitle(info.Title));
        SetupBody.Children.Add(ScreenText(info.Text));

        // the first step not done is the one to do now: it shows its whole text
        var items = StageItems(stage);
        var now = items.FirstOrDefault(i => (bool?)i["done"] != true && (bool?)i["optional"] != true);
        int n = 0;
        foreach (var item in items) SetupBody.Children.Add(StepCard(item, ++n, item == now));
        if (stage == "connect" && now != null)
            SetupBody.Children.Add(new TextBlock
            {
                Text = "This moves on by itself as soon as the phone's picture arrives.", Style = Brand.RowSub,
                Margin = new Thickness(4, 0, 4, 0),
            });
        // the bridge: the other way to be the phone's keyboard, while Bluetooth isn't paired yet
        if (stage == "control" && now != null && (string?)now["action"] == "setup_bridge")
        {
            var bridge = QuietButton("Use a Bluetooth bridge instead");
            bridge.Click += async (_, _) => await ChooseInput("bridge");
            SetupFoot.Children.Add(bridge);
        }

        if (stage == "agent")
        {
            AgentSetupPanel.Visibility = Visibility.Visible;
            ClaudeSlot.Children.Add(ClaudeCard());
            bool paired = items.Any(i => (string?)i["id"] == "app" && (bool?)i["done"] == true);
            if (paired)  // until then, no Continue (a greyed one all but vanishes): the way out is below
            {
                var go = PrimaryButton("Continue");
                go.Click += (_, _) => { _agentContinued = true; _viewStage = null; DrawSetup(); };
                SetupFoot.Children.Add(go);
            }
            else
            {
                SetupFoot.Children.Add(new TextBlock
                {
                    Text = "Agents can use the phone once the Phone Use app is paired.", Style = Brand.RowSub,
                    HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
                });
                var without = new HyperlinkButton { Content = "Finish without agents", HorizontalAlignment = HorizontalAlignment.Center };
                without.Click += (_, _) => { ChooseMode("desktop"); _viewStage = null; DrawSetup(); };
                SetupFoot.Children.Add(without);
            }
        }
        else if (!isCurrent)
        {
            var back = QuietButton("Back to where you were");
            back.Click += (_, _) => { _viewStage = null; DrawSetup(); };
            SetupFoot.Children.Add(back);
        }
        if (!AppSettings.SetupDone) SetupFoot.Children.Add(SkipLink());
    }

    /// <summary>Connect Claude Code: the checklist's step when Claude Code is on this PC, else where to get it.</summary>
    FrameworkElement ClaudeCard()
    {
        var item = _setupItems?.OfType<JsonObject>().FirstOrDefault(i => (string?)i["id"] == "claude");
        if (item != null)
        {
            var copy = (JsonObject)item.DeepClone();
            bool connected = (bool?)item["done"] == true;
            if (connected) copy["title"] = "Claude Code connected";
            return StepCard(copy, 0, !connected);
        }
        var card = StepCard(new JsonObject
        {
            ["id"] = "claude-missing", ["title"] = "Claude Code", ["done"] = false, ["optional"] = true,
            ["detail"] = "Not found on this PC. Install it, then connect it here. Other agents can use the phoneuse command.",
        }, 0, false);
        if (card.Child is Grid g && g.Children[^1] is StackPanel col)
        {
            var get = new HyperlinkButton { Content = "Get Claude Code", Padding = new Thickness(0) };
            get.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://claude.com/claude-code"));
            col.Children.Add(get);
        }
        return card;
    }

    void DrawDone()
    {
        bool agent = AppSettings.SetupMode == "agent";
        // the agent path: phoneuse running whenever you're signed in, so agents can always reach the phone
        // (once: the switch below turns it off for good)
        if (agent && !AppSettings.StartupOffered)
        {
            AppSettings.StartupOffered = true;
            try { Startup.Enabled = true; } catch (Exception) { }
        }
        SetupBody.Children.Add(ScreenTitle("Phone Use is ready"));
        SetupBody.Children.Add(ScreenText(agent
            ? "Your iPhone is connected. Agents can ask to use it, and you can watch and take over here."
            : "Your iPhone is connected. Use it here with your mouse and keyboard."));
        SetupBody.Children.Add(StartupCard());
        bool claude = _setupItems?.OfType<JsonObject>().Any(i => (string?)i["id"] == "claude" && (bool?)i["done"] == true) == true;
        if (agent && claude)
            SetupBody.Children.Add(StepCard(new JsonObject
            {
                ["id"] = "try", ["title"] = "Try it", ["done"] = false, ["optional"] = true,
                ["detail"] = "Ask Claude Code: \"Open Settings on my phone.\" The iPhone asks you to allow it first.",
            }, 0, false));

        var finish = PrimaryButton("Show the phone");
        finish.Click += (_, _) => { AppSettings.SetupDone = true; HideSetup(resume: false); };
        SetupFoot.Children.Add(finish);
        if (!agent)
        {
            var more = new HyperlinkButton { Content = "Let AI agents use it too", HorizontalAlignment = HorizontalAlignment.Center };
            more.Click += (_, _) => { ChooseMode("agent"); _agentContinued = false; _viewStage = null; DrawSetup(); };
            SetupFoot.Children.Add(more);
        }
    }

    HyperlinkButton SkipLink()
    {
        var skip = new HyperlinkButton
        {
            Content = "Skip setup", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12, Padding = new Thickness(4, 2, 4, 2),
        };
        skip.Click += (_, _) => { AppSettings.SetupDone = true; HideSetup(resume: false); };
        return skip;
    }

    Border StepCard(JsonObject item, int number, bool current)
    {
        string id = (string?)item["id"] ?? "";
        bool done = (bool?)item["done"] == true;
        string? bold = (string?)item["bold"];
        var col = new StackPanel { Spacing = 6 };
        string? choice = (string?)item["choice"];
        string title = (string?)item["title"] ?? id;
        if (id == "input" && done)  // folded: say what was chosen
            title = choice == "bridge" ? "Mouse and keyboard: the bridge" : "Mouse and keyboard: this PC's Bluetooth";
        col.Children.Add(new TextBlock
        {
            Text = title, Style = Brand.RowTitle, FontSize = 14.5,
            Foreground = done && !current ? Brand.Ink3 : Brand.Ink,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
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
            var action = id == "input" ? InputChoice(choice, done) : StepAction(id, (string?)item["action"], done);
            if (action != null) col.Children.Add(action);
            if (_setupError != null && _setupBusy == null && id == _setupErrorStep)
                col.Children.Add(new TextBlock { Text = _setupError, Foreground = Brand.Brick, TextWrapping = TextWrapping.Wrap });
        }
        double pad = done && !current ? 10 : 14;
        return Ui.StepCard(Badge(number, done, current), col, new Thickness(14, pad, 14, pad), 16, current);
    }

    string? _setupErrorStep;

    /// <summary>The first step: this PC's Bluetooth LE or a bridge, side by side; once the bridge
    /// is chosen but not set up yet, a way to set it up (or to change your mind).</summary>
    FrameworkElement? InputChoice(string? choice, bool done)
    {
        if (done) return null;
        var panel = new StackPanel { Spacing = 8 };
        if (choice == "bridge")
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var set = new Button { Content = "Set Up Bridge…", Style = Brand.AccentButton };
            set.Click += (_, _) => OpenBridgeSetup();
            row.Children.Add(set);
            var pc = new HyperlinkButton { Content = "Use this PC's Bluetooth instead", Padding = new Thickness(0) };
            pc.Click += async (_, _) => await ChooseInput("pc");
            row.Children.Add(pc);
            panel.Children.Add(row);
            return panel;
        }
        panel.Children.Add(Option("This PC's Bluetooth", "Recommended",
            "Uses the Bluetooth this PC already has: nothing to buy or plug in. The phone takes a report every " +
            "15 ms, so by hand the pointer feels a little slower than with a bridge; agents don't notice.",
            async () => await ChooseInput("pc")));
        panel.Children.Add(Option("A Bluetooth bridge", null,
            "A LilyGO T-Display (an ESP32 board) near the iPhone, on your Wi-Fi, becomes the phone's mouse and " +
            "keyboard over Bluetooth Classic: a quicker pointer (every 8 ms), and it works where this PC's " +
            "Bluetooth can't reach. Takes a few minutes to set up; Phone Use can flash a new board.",
            async () => await ChooseInput("bridge")));
        return panel;
    }

    /// <summary>A choice as a big button: its name, a tag, and what it means.</summary>
    static Button Option(string title, string? tag, string text, Func<Task> chosen)
    {
        var col = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (tag != null)
            head.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
                Background = Brand.Plum,
                Child = new TextBlock { Text = tag, FontSize = 11, Foreground = Brand.Bg },
            });
        col.Children.Add(head);
        col.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 13 });
        var b = new Button
        {
            Content = col, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10),
        };
        b.Click += async (_, _) => await chosen();
        return b;
    }

    async Task ChooseInput(string choice)
    {
        var r = await _conn.PostAsync("api/setup/input", new { choice });
        if (r?["error"] != null)
        {
            _setupError = (string?)r["error"];
            _setupErrorStep = "input";
        }
        await LoadSetup();
        if (choice == "bridge") OpenBridgeSetup();
    }

    /// <summary>The bridge's page over the phone, with its setup dialog open.</summary>
    void OpenBridgeSetup()
    {
        BridgeSettings_Click(this, new RoutedEventArgs());
        var view = _bridgeView!;
        if (view.XamlRoot != null && view.IsLoaded) view.BeginSetup();
        else
        {
            RoutedEventHandler? once = null;
            once = (_, _) => { view.Loaded -= once; view.BeginSetup(); };
            view.Loaded += once;
        }
    }

    FrameworkElement? StepAction(string id, string? action, bool done)
    {
        if (!done && id == "pointer")  // nothing to check from here: a box to tick, and the calibration flow
        {
            var pr = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            if (!AppSettings.AssistiveTouchDone)
            {
                var ok = new Button { Content = "Done", Style = Brand.AccentButton };
                ok.Click += async (_, _) => { AppSettings.AssistiveTouchDone = true; await LoadSetup(); };
                pr.Children.Add(ok);
            }
            var cal = new Button { Content = CalState == "failed" ? "Calibrate again…" : "Calibrate…" };
            if (AppSettings.AssistiveTouchDone) cal.Style = Brand.AccentButton;
            cal.Click += (_, _) => ShowCalibrationFlow();
            pr.Children.Add(cal);
            return pr;
        }
        if (done || action == null) return null;
        bool busy = _setupBusy == id;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        Button? b = action switch
        {
            "allow_firewall" => new Button { Content = busy ? "Waiting for Windows…" : "Allow", Style = Brand.AccentButton },
            "pair_app" => new Button { Content = "Show Pairing Code", Style = Brand.AccentButton },
            "claude" => new Button { Content = busy ? "Connecting…" : "Connect" },
            _ => null,
        };
        if (b != null)
        {
            b.IsEnabled = !busy;
            b.Click += async (_, _) => await RunSetupAction(id, action);
            row.Children.Add(b);
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

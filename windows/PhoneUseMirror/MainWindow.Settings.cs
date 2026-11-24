using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace PhoneUseMirror;

/// <summary>
/// The settings overlay: the browser mirror's settings panel, natively. Every setting
/// goes through the same server routes the page uses:
///   keep awake     /api/awake        {on}
///   agent control  /api/session/ask  {mode | seconds}
///   passcode       /api/passcode     GET / POST {code} / DELETE (never sent back)
///   bridge         /api/bridge/...   (MainWindow.Bridge.cs)
/// </summary>
public sealed partial class MainWindow
{
    bool _darkMode;
    bool _settingUp;  // filling the controls from the server: their change events send nothing
    bool _codexBusy;
    bool? _codexConnected;
    bool _claudeBusy;
    bool? _claudeConnected;

    async void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsVisible)
        {
            HideAbout();
            return;
        }
        ShowOverlay(SettingsPage);
        _settingUp = true;
        AwakeSwitch.IsOn = _awake;
        StartupSwitch.IsOn = Startup.Enabled;
        TopmostSwitch.IsOn = _alwaysOnTop;
        SnapSwitch.IsOn = _snapEnabled;
        UpdateAuto.IsOn = AppSettings.AutoUpdate;
        DarkModeSwitch.IsOn = _darkMode;
        _settingUp = false;
        FillGlowSettings();
        FillFinishSettings();
        ShowUpdate();
        ShowVision();
        PasscodeBox.Password = "";
        await Task.WhenAll(LoadStartMode(), LoadPasscode(), LoadPaired(), LoadBridgeSwitch(), LoadAgentConnectionsIfVisible());
    }

    bool SettingsVisible => AboutOverlay.Visibility == Visibility.Visible && SettingsPage.Visibility == Visibility.Visible;

    void HideSettings()
    {
        if (SettingsVisible) HideAbout();
    }

    // The (i) button next to Settings: opens About, or closes it again.
    void OpenAbout_Click(object sender, RoutedEventArgs e)
    {
        if (AboutOverlay.Visibility == Visibility.Visible && !SettingsVisible && ReferenceEquals(AboutHost.Content, _aboutView)) HideAbout();
        else ShowAbout();
    }

    async void OpenBrowser_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(_conn.PageUri);

    void Snap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUp || SnapSwitch.IsOn == _snapEnabled) return;
        SetSnapEnabled(SnapSwitch.IsOn);
    }

    void Topmost_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUp || TopmostSwitch.IsOn == _alwaysOnTop) return;
        SetAlwaysOnTop(TopmostSwitch.IsOn);
    }

    void DarkMode_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUp || sender is not ToggleSwitch toggle || toggle.IsOn == _darkMode) return;
        AppSettings.DarkMode = toggle.IsOn;
        ApplyAppearance(toggle.IsOn);
        ShowUpdate();
    }

    void ApplyAppearance(bool dark)
    {
        _darkMode = dark;
        AboutOverlay.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        BarBacking.Background = Brand.AppearanceBrush("Bar", dark);
        _activity?.ApplyAppearance(dark);
        ShowActivityButton();
        _aboutView = null;  // rebuild its directly colored setup badges the next time it opens
    }

    // ---------------------------------------------------------------- keep awake

    async void Awake_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        await _conn.PostAsync("api/awake", new { on = AwakeSwitch.IsOn });
    }

    // ---------------------------------------------------------------- start with Windows (this app's own)

    void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingUp) return;
        try { Startup.Enabled = StartupSwitch.IsOn; }
        catch (Exception)
        {
            _settingUp = true;
            StartupSwitch.IsOn = Startup.Enabled;
            _settingUp = false;
        }
    }

    // ---------------------------------------------------------------- agent control

    Task LoadAgentConnectionsIfVisible() => ClaudeButton != null && AgentControlTab.IsChecked == true && SettingsVisible
        ? Task.WhenAll(UpdateAgentConnection(codex: true, null), UpdateAgentConnection(codex: false, null))
        : Task.CompletedTask;

    async void Codex_Click(object sender, RoutedEventArgs e) => await UpdateAgentConnection(codex: true, !_codexConnected);
    async void Claude_Click(object sender, RoutedEventArgs e) => await UpdateAgentConnection(codex: false, !_claudeConnected);

    async Task UpdateAgentConnection(bool codex, bool? on)
    {
        if (codex ? _codexBusy : _claudeBusy) return;
        if (codex) _codexBusy = true;
        else _claudeBusy = true;

        var name = codex ? "Codex" : "Claude Code";
        var row = codex ? CodexRow : ClaudeRow;
        var button = codex ? CodexButton : ClaudeButton;
        button.IsEnabled = false;
        row.Subtitle = on switch
        {
            true => $"Connecting to {name}…",
            false => $"Disconnecting from {name}…",
            null => $"Checking {name} setup…",
        };
        if (codex)
        {
            SetupCodexRow.Subtitle = row.Subtitle;
            SetupCodexButton.IsEnabled = false;
        }
        try
        {
            var path = codex ? "api/codex" : "api/claude";
            var result = on is bool connect
                ? await _conn.PostAsync(path, new { on = connect })
                : await _conn.GetAsync(path);
            var error = (string?)result?["error"];
            bool? available = (bool?)result?[codex ? "available" : "claude"];
            bool? connected = error == null && available == true
                ? codex ? (bool?)result?["connected"] : (bool?)result?["skill"] == true
                    && (bool?)result?["command"] == true && (bool?)result?["mcp"] == true
                : null;
            if (codex) _codexConnected = connected;
            else _claudeConnected = connected;

            string newSession = codex ? "Open a new Codex chat" : "Start a new Claude Code session";
            row.Subtitle = error ?? (connected switch
            {
                true => $"Connected. {newSession} to use Phone Use.",
                false => on == false ? $"Disconnected. {newSession} to apply the change." : $"Let {name} use Phone Use.",
                null => available == false
                    ? codex ? "Install Codex with the Codex CLI to connect." : "Install Claude Code, then refresh."
                    : $"Couldn't check {name} setup.",
            });
            button.Content = connected switch
            {
                true => "Disconnect",
                false => "Connect",
                null => "Refresh",
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{button.Content} {name}");
            if (codex)
            {
                SetupCodexRow.Subtitle = row.Subtitle;
                SetupCodexButton.Visibility = connected == true ? Visibility.Collapsed : Visibility.Visible;
                SetupCodexButton.Content = button.Content;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SetupCodexButton, $"{button.Content} Codex");
            }
        }
        finally
        {
            if (codex)
            {
                _codexBusy = false;
                SetupCodexButton.IsEnabled = true;
            }
            else _claudeBusy = false;
            button.IsEnabled = true;
        }
    }

    async Task LoadStartMode()
    {
        var st = await _conn.GetAsync("api/session/ask");
        if (st != null && st["mode"] != null) ShowStartMode(st);
    }

    void ShowStartMode(JsonObject st)
    {
        _settingUp = true;
        var mode = (string?)st["mode"] ?? "auto";
        var secs = (int?)(double?)st["seconds"] ?? 10;
        foreach (var r in new[] { ModeAuto, ModeCountdown, ModeAsk }) r.IsChecked = (string)r.Tag == mode;
        if (secs is not (5 or 10 or 15 or 30)) secs = 10;
        foreach (var c in new[] { Delay5, Delay10, Delay15, Delay30 }) c.IsChecked = (string)c.Tag == secs.ToString();
        StartSeconds.Visibility = mode == "countdown" ? Visibility.Visible : Visibility.Collapsed;
        _settingUp = false;
    }

    async void StartMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingUp || _conn == null || sender is not RadioButton { Tag: string mode }) return;
        var r = await _conn.PostAsync("api/session/ask", new { mode });
        if (r != null && r["mode"] != null) ShowStartMode(r);
    }

    async void StartSeconds_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingUp || _conn == null || sender is not RadioButton { Tag: string secs }) return;
        var r = await _conn.PostAsync("api/session/ask", new { seconds = int.Parse(secs) });
        if (r != null && r["mode"] != null) ShowStartMode(r);
    }

    // ---------------------------------------------------------------- passcode

    async Task LoadPasscode() => ShowPasscode(await _conn.GetAsync("api/passcode"));

    void ShowPasscode(JsonObject? j)
    {
        // Turned off (or phoneuse unreachable): no passcode settings at all.
        bool on = j != null && j["_failed"] == null && (bool?)j["enabled"] != false;
        PasscodeSection.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var error = (string?)j!["error"];
        PasscodeError.Text = error ?? "";
        PasscodeError.Visibility = error != null ? Visibility.Visible : Visibility.Collapsed;
        if (error == null)
        {
            bool saved = (bool?)j["saved"] ?? false;
            PasscodeRemove.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
            PasscodeBox.PlaceholderText = saved ? "Replace passcode" : "Enter passcode";
        }
    }

    void Passcode_Changed(object sender, RoutedEventArgs e) => PasscodeSave.IsEnabled = PasscodeBox.Password.Length > 0;

    void Passcode_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && PasscodeBox.Password.Length > 0)
        {
            e.Handled = true;
            PasscodeSave_Click(sender, e);
        }
    }

    async void PasscodeSave_Click(object sender, RoutedEventArgs e)
    {
        var code = PasscodeBox.Password;
        PasscodeBox.Password = "";
        PasscodeSave.IsEnabled = false;
        if (code.Length > 0) ShowPasscode(await _conn.PostAsync("api/passcode", new { code }));
    }

    async void PasscodeRemove_Click(object sender, RoutedEventArgs e) =>
        ShowPasscode(await _conn.DeleteAsync("api/passcode"));
}

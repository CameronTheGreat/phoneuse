using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace PhoneUseMirror;

/// <summary>The bar's activity button showing the rail's state: a plum wash and plum icon while the
/// rail is shown, a status dot while an agent is connected, and a tooltip that names the click.</summary>
public sealed partial class MainWindow
{
    void ShowActivityButton()
    {
        // called from init and from the rail/agent handlers, possibly before the XAML has loaded
        if (ActivityButton is null || ActivityIcon is null || ActivityDot is null) return;

        var shown = ActivityShown;
        ActivityButton.Background = shown ? Brand.AppearanceBrush("ActivitySelected", _darkMode) : Brand.Transparent;
        if (shown) ActivityIcon.Foreground = Brand.PlumLight;
        else ActivityIcon.ClearValue(IconElement.ForegroundProperty);  // back to the button's own foreground

        // same colours as AgentLight: plum-light driving, amber paused
        var driving = _agent == "driving";
        var paused = _agent == "paused";
        ActivityDot.Visibility = _activityAgent || driving || paused ? Visibility.Visible : Visibility.Collapsed;
        ActivityDot.Fill = paused ? Brand.Amber : Brand.PlumLight;

        var tip = shown ? "Hide agent activity" : "Show agent activity";
        if (_activityAgent || driving || paused)
            tip += paused ? ", agent paused" : ", agent working";
        ToolTipService.SetToolTip(ActivityButton, tip);
        AutomationProperties.SetName(ActivityButton, tip);
    }
}

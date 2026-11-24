using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace PhoneUseMirror;

/// <summary>The pieces the screens drawn in code share (setup, calibration, About, the bridge), in the
/// site's look (Brand.xaml, App.xaml).</summary>
static class Ui
{
    public const string ChevronIcon = "M9,6 L15,12 L9,18";

    /// <summary>A grid with these columns ("auto", "*", or a width) and this gap between them.</summary>
    public static Grid Columns(double spacing, params string[] widths)
    {
        var g = new Grid { ColumnSpacing = spacing };
        foreach (var w in widths)
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = w == "auto" ? GridLength.Auto : w == "*" ? new GridLength(1, GridUnitType.Star) : new GridLength(double.Parse(w)),
            });
        return g;
    }

    /// <summary>Adds an element to a grid, in that column.</summary>
    public static T AddTo<T>(this T e, Grid g, int column = 0) where T : FrameworkElement
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
        return e;
    }

    /// <summary>A stroke icon drawn on the site's 24-unit grid, shown at `size` (BrandIconPathStyle).</summary>
    public static Viewbox Icon(string data, double size = 18, Brush? stroke = null, double thickness = double.NaN)
    {
        var path = new Path
        {
            Style = Brand.IconPathStyle,
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
        };
        if (stroke != null) path.Stroke = stroke;
        if (!double.IsNaN(thickness)) path.StrokeThickness = thickness;
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    /// <summary>An icon on its rounded tile (BrandIconTileStyle: plum tint unless given a background).</summary>
    public static Border IconTile(string data, Brush? background = null, Brush? stroke = null)
    {
        var tile = new Border { Style = Brand.IconTileStyle, Child = Icon(data, stroke: stroke) };
        if (background != null) tile.Background = background;
        return tile;
    }

    /// <summary>The app's icon and its name, as the top of phoneuse.tech.</summary>
    public static FrameworkElement Lockup()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
            Background = new ImageBrush
            {
                ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/AppIcon.png")),
            },
        });
        row.Children.Add(new TextBlock
        {
            Text = "Phone Use", FontSize = 19, FontWeight = FontWeights.Bold, CharacterSpacing = -35,
            FontFamily = Brand.Font, Foreground = Brand.Ink, VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    /// <summary>A button's colours at rest, under the pointer and pressed. Its template's hover and press states
    /// paint the theme's own fill and border (ButtonBackgroundPointerOver...), over whatever Background and
    /// BorderBrush it was given, so its own versions of those go in its resources.</summary>
    public static void Paint(Button b, Brush rest, Brush hover, Brush pressed, Brush border, Brush? borderHover = null, Brush? fg = null)
    {
        b.Background = rest;
        b.BorderBrush = border;
        b.Resources["ButtonBackground"] = rest;
        b.Resources["ButtonBackgroundPointerOver"] = hover;
        b.Resources["ButtonBackgroundPressed"] = pressed;
        b.Resources["ButtonBorderBrush"] = border;
        b.Resources["ButtonBorderBrushPointerOver"] = borderHover ?? border;
        b.Resources["ButtonBorderBrushPressed"] = borderHover ?? border;
        if (fg == null) return;
        b.Foreground = fg;
        foreach (var state in new[] { "", "PointerOver", "Pressed" }) b.Resources["ButtonForeground" + state] = fg;
    }

    /// <summary>The screen's main action: a full-width plum pill.</summary>
    public static Button PrimaryButton(string text) => new()
    {
        Content = text, Style = Brand.AccentButton, HorizontalAlignment = HorizontalAlignment.Stretch,
        Height = 48, CornerRadius = new CornerRadius(24), FontSize = 15,
    };

    /// <summary>A secondary action at the bottom: a full-width cream pill.</summary>
    public static Button QuietButton(string text)
    {
        var b = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 44, CornerRadius = new CornerRadius(22),
            FontSize = 14, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0),
        };
        Paint(b, Brand.Cream, Brand.AmberTint, Brand.CreamPressed, Brand.Transparent, Brand.Transparent, Brand.PlumDeep);
        return b;
    }

    /// <summary>A screen's heading and its line under it, in the site's voice (heavy, tight).</summary>
    public static TextBlock ScreenTitle(string text) => new()
    {
        Text = text, Style = Brand.Title, FontSize = 22, LineHeight = 26, TextWrapping = TextWrapping.Wrap,
        Foreground = Brand.Ink, Margin = new Thickness(0, 8, 0, 0),
    };

    public static TextBlock ScreenText(string text) => new()
    {
        Text = text, Style = Brand.RowSub, FontSize = 13, LineHeight = 18,
        Foreground = Brand.Ink2, Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock Para(string text, double opacity) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

    /// <summary>A step's number in a circle: green with a tick once done, plum while it's the one to do.</summary>
    public static FrameworkElement Badge(int number, bool done, bool current) => new Border
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top,
        Background = done ? Brand.Green : current ? Brand.Plum : Brand.Cream,
        Child = done
            ? new FontIcon { Glyph = "", FontSize = 12, Foreground = Brand.Bg }
            : number > 0
                ? new TextBlock
                {
                    Text = number.ToString(), FontWeight = FontWeights.SemiBold, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = current ? Brand.Bg : Brand.Ink2,
                }
                : new FontIcon { Glyph = "", FontSize = 11 },  // an extra: a plus
    };

    /// <summary>A step as a card: its badge, and beside it the column of what it says.</summary>
    public static Border StepCard(FrameworkElement badge, StackPanel col, Thickness padding, double corner, bool current)
    {
        var g = Columns(12, "auto", "*");
        g.Children.Add(badge);
        col.AddTo(g, 1);
        return new Border
        {
            Child = g, Padding = padding, CornerRadius = new CornerRadius(corner),
            Background = current ? Brand.Cream : Brand.Card,
            BorderBrush = current ? Brand.Plum : Brand.Line,
            BorderThickness = new Thickness(current ? 1.5 : 1),
        };
    }
}

/// <summary>A stroke icon (Ui.Icon) for XAML: &lt;local:BrandIcon Data="…" /&gt;.</summary>
public sealed partial class BrandIcon : UserControl
{
    public string Data { get; set; } = "";
    public Brush? Stroke { get; set; }
    public double StrokeThickness { get; set; } = double.NaN;
    public double Size { get; set; } = 18;

    public BrandIcon()
    {
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Loading += (_, _) => Content ??= Ui.Icon(Data, Size, Stroke, StrokeThickness);
    }
}

/// <summary>
/// A row of a Settings card: an icon tile, its title (and a line under it), and whatever it holds on the
/// right (a switch; or a chevron, as a button's content). In XAML the right-hand element is its child.
///   Plain    the title alone, at 15 Medium, in the text colour it's in (a button's, in a row button)
/// </summary>
public sealed partial class SettingsRow : Grid
{
    TextBlock? _title, _sub;
    string _titleText = "";
    string? _subText;

    public string? Icon { get; set; }
    public Brush? IconBackground { get; set; }
    public Brush? IconStroke { get; set; }
    public bool Plain { get; set; }
    public bool Chevron { get; set; }

    public string Title
    {
        get => _titleText;
        set { _titleText = value; if (_title != null) _title.Text = value; }
    }

    public string? Subtitle
    {
        get => _subText;
        set { _subText = value; if (_sub != null) _sub.Text = value; }
    }

    public SettingsRow()
    {
        ColumnSpacing = 12;
        Loading += (_, _) => Build();
    }

    void Build()
    {
        if (ColumnDefinitions.Count > 0) return;
        var right = Children.OfType<FrameworkElement>().ToList();  // its XAML children
        if (Chevron) right.Add(Ui.Icon(Ui.ChevronIcon, 16, Brand.Ink3, 2));
        int col = 0;
        if (Icon != null)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Ui.IconTile(Icon, IconBackground, IconStroke).AddTo(this, col++);
        }
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (Plain)
            _title = new TextBlock { Text = _titleText, FontSize = 15, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center }
                .AddTo(this, col);
        else
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center }.AddTo(this, col);
            text.Children.Add(_title = new TextBlock { Text = _titleText, Style = Brand.RowTitle });
            if (_subText != null) text.Children.Add(_sub = new TextBlock { Text = _subText, Style = Brand.RowSub });
        }
        if (right.Count == 0) return;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var e in right)
        {
            Grid.SetColumn(e, col + 1);
            if (!Children.Contains(e)) Children.Add(e);
        }
    }
}

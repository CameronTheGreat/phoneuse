using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PhoneUseMirror;

/// <summary>Brand.xaml's brushes and App.xaml's styles, by name, for screens drawn in code. Each is the
/// app's one shared instance, as {StaticResource} gives XAML.</summary>
static class Brand
{
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];
    static Brush B(string key) => Res<Brush>(key);
    static Style S(string key) => Res<Style>(key);

    public static Brush Bg => B("BrandBgBrush");
    public static Brush Card => B("BrandCardBrush");
    public static Brush Cream => B("BrandCreamBrush");
    public static Brush CreamPressed => B("BrandCreamPressedBrush");
    public static Brush AmberTint => B("BrandAmberTintBrush");
    public static Brush PlumTint => B("BrandPlumTintBrush");
    public static Brush PlumTintHover => B("BrandPlumTintHoverBrush");
    public static Brush PlumTintPressed => B("BrandPlumTintPressedBrush");
    public static Brush PlumWash => B("BrandPlumWashBrush");
    public static Brush PlumWashStrong => B("BrandPlumWashStrongBrush");
    public static Brush Line => B("BrandLineBrush");
    public static Brush LineStrong => B("BrandLineStrongBrush");
    public static Brush Ink => B("BrandInkBrush");
    public static Brush Ink2 => B("BrandInk2Brush");
    public static Brush Ink3 => B("BrandInk3Brush");
    public static Brush Plum => B("BrandPlumBrush");
    public static Brush PlumDeep => B("BrandPlumDeepBrush");
    public static Brush PlumLight => B("BrandPlumLightBrush");
    public static Brush Amber => B("BrandAmberBrush");
    public static Brush AmberText => B("BrandAmberTextBrush");
    public static Brush Brick => B("BrandBrickBrush");
    public static Brush Green => B("BrandGreenBrush");
    public static Brush GreenTint => B("BrandGreenTintBrush");
    public static Brush GreenText => B("BrandGreenTextBrush");
    public static Brush Transparent => new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    public static FontFamily Font => Res<FontFamily>("BrandFontFamily");

    public static Style AccentButton => S("BrandAccentButtonStyle");
    public static Style RowTitle => S("BrandRowTitleStyle");
    public static Style RowSub => S("BrandRowSubStyle");
    public static Style IconTileStyle => S("BrandIconTileStyle");
    public static Style IconPathStyle => S("BrandIconPathStyle");
    public static Style Title => S("TitleTextBlockStyle");
    public static Style TitleLarge => S("TitleLargeTextBlockStyle");
    public static Style Subtitle => S("SubtitleTextBlockStyle");
    public static Style Caption => S("CaptionTextBlockStyle");
    public static Style BodyStrong => S("BodyStrongTextBlockStyle");
}

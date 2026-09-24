using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using WindowSearcher.Core;

namespace WindowSearcher.App.UI;

/// <summary>
/// Attached property that renders an FTS snippet, bolding the parts between the index's
/// highlight markers.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string;
        if (string.IsNullOrEmpty(text)) return;

        var highlight = Application.Current.TryFindResource("HighlightBrush") as Brush;
        var inMatch = false;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var c = atEnd ? '\0' : text[i];
            if (!atEnd && c != SearchIndex.HighlightStart && c != SearchIndex.HighlightEnd) continue;
            if (i > start)
            {
                var run = new Run(text[start..i]);
                if (inMatch)
                {
                    run.FontWeight = FontWeights.Bold;
                    if (highlight is not null) run.Foreground = highlight;
                }
                tb.Inlines.Add(run);
            }
            inMatch = c == SearchIndex.HighlightStart;
            start = i + 1;
        }
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the bound value is null (e.g. show an icon placeholder when there's no preview).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Follows the Windows app light/dark setting.</summary>
internal static class Theme
{
    public static void Apply(ResourceDictionary resources)
    {
        var light = IsLightTheme();
        Set(resources, "BgBrush", light ? "#F9F9F9" : "#202020");
        Set(resources, "PanelBrush", light ? "#FFFFFF" : "#2B2B2B");
        Set(resources, "BorderBrush", light ? "#D6D6D6" : "#3C3C3C");
        Set(resources, "TextBrush", light ? "#1B1B1B" : "#F3F3F3");
        Set(resources, "SubTextBrush", light ? "#555555" : "#B8B8B8");
        Set(resources, "AccentBrush", light ? "#005FB8" : "#60CDFF");
        Set(resources, "SelectionBrush", light ? "#DCEBFA" : "#1F3D5C");
        Set(resources, "HoverBrush", light ? "#EDEDED" : "#2D2D2D");
        Set(resources, "HighlightBrush", light ? "#8A5A00" : "#FFD866");
        Set(resources, "ScrollTrackBrush", light ? "#E6E6E6" : "#2E2E2E");
        Set(resources, "ScrollThumbBrush", light ? "#8C8C8C" : "#A0A0A0");
        Set(resources, "ScrollThumbHoverBrush", light ? "#5C5C5C" : "#D6D6D6");
    }

    private static void Set(ResourceDictionary resources, string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        resources[key] = brush;
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

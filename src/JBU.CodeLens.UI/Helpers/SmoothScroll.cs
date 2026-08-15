using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace JBU.CodeLens.UI.Helpers;

/// <summary>
/// Makes a <see cref="ScrollViewer"/> scroll by the amount the machine is configured to scroll by.
/// </summary>
/// <remarks>
/// <para>
/// WPF moves a fixed three lines per wheel notch and ignores the Windows mouse setting entirely.
/// On a machine set to a higher number, or set to scroll a whole screen at a time, every other
/// application obeys it and this one did not, which reads as the app being sluggish rather than
/// as a setting being missed.
/// </para>
/// <para>
/// Attach from XAML with <c>helpers:SmoothScroll.FollowSystemSetting="True"</c>.
/// </para>
/// </remarks>
public static class SmoothScroll
{
    /// <summary>Roughly one line of the body text this application renders.</summary>
    private const double LineHeight = 18;

    /// <summary>
    /// Windows reports this when the wheel is set to "one screen at a time" rather than to a
    /// number of lines.
    /// </summary>
    private const int ScrollOneScreen = -1;

    public static readonly DependencyProperty FollowSystemSettingProperty =
        DependencyProperty.RegisterAttached(
            "FollowSystemSetting",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnFollowSystemSettingChanged));

    public static void SetFollowSystemSetting(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FollowSystemSettingProperty, value);
    }

    public static bool GetFollowSystemSetting(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(FollowSystemSettingProperty);
    }

    private static void OnFollowSystemSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scrollViewer)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            scrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            scrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        // Nothing to scroll: leave the event alone so it bubbles to a parent that can use it.
        if (scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        // Read every time rather than once at startup, so changing the setting takes effect
        // without restarting the application.
        var lines = SystemParameters.WheelScrollLines;

        var offset = lines == ScrollOneScreen
            ? scrollViewer.ViewportHeight
            : lines * LineHeight;

        // Delta is 120 per notch; a free-spinning wheel or a precision touchpad reports
        // fractions of that, and dividing keeps those proportional instead of snapping them
        // up to a whole notch.
        var distance = offset * (e.Delta / 120.0);

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - distance);
        e.Handled = true;
    }
}

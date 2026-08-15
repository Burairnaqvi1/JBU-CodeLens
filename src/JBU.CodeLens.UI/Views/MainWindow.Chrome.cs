using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JBU.CodeLens.UI.Views;

/// <summary>
/// The window's own presentation: theme switching, the custom title bar, the collapsible
/// sidebar, and the inline notification banner.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml.cs, which had grown past 1900 lines. None of this touches
/// scanning, parsing, or the detail panel, it is the chrome the rest of the window sits in,
/// and keeping it here makes the main file the story of the application rather than of its
/// decoration.
/// </remarks>
public partial class MainWindow
{
    // ── Theme ────────────────────────────────────────────────────────────────
    // Palette values live exclusively in Theme/DarkTheme.xaml and Theme/LightTheme.xaml;
    // ThemeManager retargets the app-level brushes. No color literals appear in code-behind.

    private void LightThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDarkTheme) { ApplyTheme(AppTheme.Light); PersistTheme(); }
    }

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDarkTheme) { ApplyTheme(AppTheme.Dark); PersistTheme(); }
    }

    /// <summary>Remembers the chosen theme so the next launch opens in it.</summary>
    private void PersistTheme()
    {
        _settings.Theme = _isDarkTheme ? "Dark" : "Light";
        _settings.Save();
    }

    private void PlaceholderReopenButton_Click(object sender, RoutedEventArgs e)
    {
        var last = _settings.LastProjectPath;
        if (!IsBusy && !string.IsNullOrEmpty(last) && Directory.Exists(last))
        {
            SelectedFolderPath = last;
            ScanProject();
        }
    }

    private void ApplyTheme(AppTheme theme)
    {
        _isDarkTheme = theme == AppTheme.Dark;
        ThemeManager.Apply(theme);
        UpdateTitleBarTheme();

        var active = _isDarkTheme ? DarkThemeButton : LightThemeButton;
        var inactive = _isDarkTheme ? LightThemeButton : DarkThemeButton;
        active.SetResourceReference(BackgroundProperty, "PrimaryBrush");
        active.SetResourceReference(ForegroundProperty, "SurfaceBrush");
        inactive.Background = Brushes.Transparent;
        inactive.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");

        RefreshCurrentDetailView();
    }

    /// <summary>
    /// Keeps the native Windows title bar in step with the app theme (a white title bar on a
    /// dark app is the single most visible "unfinished" signal). No-ops before the window
    /// handle exists; OnSourceInitialized re-applies once it does.
    /// </summary>
    private void UpdateTitleBarTheme()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var useDark = _isDarkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    // dwmapi.dll is a Windows system library, so restrict the search to System32: the default order
    // would also probe the application and working directories, where a planted dwmapi.dll would be
    // loaded into this process instead.
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTitleBarTheme();
    }

    // ── Custom window chrome ─────────────────────────────────────────────────

    private void MinimizeCaption_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeCaption_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void CloseCaption_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A maximized borderless window overflows the screen by the resize border; compensate
    /// with an equal margin, and keep the maximize glyph in sync (E922 maximize / E923 restore).
    /// </summary>
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        var maximized = WindowState == WindowState.Maximized;
        RootShell.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeCaptionButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeCaptionButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    // ── Sidebar collapse ─────────────────────────────────────────────────────

    private bool _sidebarCollapsed;

    private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = _sidebarCollapsed ? 0 : 280,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
            },
        };
        SidebarPanel.BeginAnimation(WidthProperty, animation);
        SidebarToggleIcon.Text = _sidebarCollapsed ? "" : "";
    }

    // ── Inline notification banner ───────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _notificationTimer;
    private Action? _notificationAction;

    private enum NotificationKind { Info, Success, Error }

    /// <summary>
    /// Shows the inline banner (slides in from the top, auto-dismisses after 4 s). Replaces
    /// modal dialogs for non-critical messages; genuine decisions still use dialogs.
    /// </summary>
    private void ShowNotification(
        string message,
        NotificationKind kind = NotificationKind.Info,
        string? actionLabel = null,
        Action? action = null)
    {
        NotificationText.Text = message;
        NotificationAccent.SetResourceReference(BackgroundProperty, kind switch
        {
            NotificationKind.Success => "SecondaryBrush",
            NotificationKind.Error => "ErrorBrush",
            _ => "PrimaryBrush",
        });

        _notificationAction = action;
        if (actionLabel is not null && action is not null)
        {
            NotificationActionButton.Content = actionLabel;
            NotificationActionButton.Visibility = Visibility.Visible;
        }
        else
        {
            NotificationActionButton.Visibility = Visibility.Collapsed;
        }

        NotificationBanner.Visibility = Visibility.Visible;
        var slide = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = -40,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        NotificationTransform.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty, slide);

        _notificationTimer?.Stop();
        _notificationTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _notificationTimer.Tick += (_, _) => DismissNotification();
        _notificationTimer.Start();
    }

    private void DismissNotification()
    {
        _notificationTimer?.Stop();
        _notificationTimer = null;
        NotificationBanner.Visibility = Visibility.Collapsed;
    }

    private void NotificationClose_Click(object sender, RoutedEventArgs e) => DismissNotification();

    private void NotificationAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _notificationAction;
        DismissNotification();
        action?.Invoke();
    }
}

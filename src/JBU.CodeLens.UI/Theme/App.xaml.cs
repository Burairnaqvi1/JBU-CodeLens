using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JBU.CodeLens.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Guards the failure dialog against reporting a fault raised by the failure dialog itself,
    /// which would recurse until the stack ran out.
    /// </summary>
    private bool _reportingFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        // An unhandled exception on the dispatcher ends the process immediately, with the
        // operating system's own crash box and nothing written down. That is the worst way for
        // this application to fail, mid-demonstration, with no record of what happened. These
        // three cover the places a fault can surface: the UI thread, a background thread, and a
        // Task whose result nobody awaited.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
        Dispatcher.BeginInvoke(ApplyImplicitWindowStyle, DispatcherPriority.Loaded);
    }

    private void ApplyImplicitWindowStyle()
    {
        if (Resources[typeof(Window)] is not Style windowStyle)
        {
            return;
        }

        foreach (Window window in Windows)
        {
            window.Style = windowStyle;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Handled first, so that whatever happens below the application is already spared the
        // process kill. A fault in one panel is not a reason to lose the scan behind it.
        e.Handled = true;
        Log(e.Exception, "dispatcher");
        ShowFailureDialog(e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Cannot be cancelled, the runtime is already tearing down. co this only records what
        // happened, which is the difference between a diagnosable failure and a mystery.
        Log(e.ExceptionObject as Exception, "background thread");
    }

    private static void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Log(e.Exception, "unobserved task");
        e.SetObserved();
    }

    /// <summary>
    /// Appends the fault to <c>%APPDATA%\JBU.CodeLens\error-log.txt</c>. Best effort throughout:
    /// a logger that throws during crash handling would replace the fault being reported.
    /// </summary>
    private static void Log(Exception? exception, string origin)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            var path = AppPaths.InAppData("error-log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entry = new StringBuilder()
                .AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                    $"── {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({origin}) ──")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(path, entry);
        }
        catch (Exception logFailure)
        {
            Debug.WriteLine($"[JBU CodeLens] Could not write the error log: {logFailure.Message}");
        }
    }

    /// <summary>
    /// Reports the fault in the application's own colours, and says where the details were
    /// written, rather than letting Windows show a stock "has stopped working" box.
    /// </summary>
    private void ShowFailureDialog(Exception exception)
    {
        if (_reportingFailure)
        {
            return;
        }

        _reportingFailure = true;
        try
        {
            var message = new TextBlock
            {
                Text = "Something went wrong inside the application. The project you have open "
                    + "is still loaded, so you can carry on; if the same thing keeps happening, "
                    + "rescanning usually clears it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            };
            message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var detail = new TextBox
            {
                Text = exception.Message,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                MaxHeight = 120,
                Margin = new Thickness(0, 0, 0, 14),
            };
            detail.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush");

            var where = new TextBlock
            {
                Text = $"Details were written to {AppPaths.InAppData("error-log.txt")}",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Opacity = 0.75,
            };
            where.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var close = new Button
            {
                Content = "Continue",
                Padding = new Thickness(18, 6, 18, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                IsDefault = true,
            };
            close.SetResourceReference(Button.BackgroundProperty, "PrimaryBrush");
            close.SetResourceReference(Button.ForegroundProperty, "SurfaceBrush");

            var body = new StackPanel { Margin = new Thickness(22) };
            body.Children.Add(message);
            body.Children.Add(detail);
            body.Children.Add(where);
            body.Children.Add(close);

            var dialog = new Window
            {
                Title = "JBU CodeLens",
                Content = body,
                SizeToContent = SizeToContent.Height,
                Width = 470,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Owner = Windows.OfType<Window>().FirstOrDefault(w => w.IsLoaded && w.IsVisible),
            };
            dialog.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush");
            close.Click += (_, _) => dialog.Close();

            dialog.ShowDialog();
        }
        catch (Exception dialogFailure)
        {
            // The themed dialog could not be shown. cay it with the one mechanism that cannot
            // itself depend on the application's resources.
            Debug.WriteLine($"[JBU CodeLens] Could not show the failure dialog: {dialogFailure.Message}");
            MessageBox.Show(
                exception.Message,
                "JBU CodeLens",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _reportingFailure = false;
        }
    }
}

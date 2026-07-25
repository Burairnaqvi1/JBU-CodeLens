using System.Windows;

namespace JBU.CodeLens.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Dispatcher.BeginInvoke(ApplyImplicitWindowStyle, System.Windows.Threading.DispatcherPriority.Loaded);
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
}

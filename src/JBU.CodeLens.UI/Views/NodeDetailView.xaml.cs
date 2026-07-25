using System.Windows;
using System.Windows.Controls;

namespace JBU.CodeLens.UI.Views;

/// <summary>
/// In-app detail page opened from the project-tree visualization: shows the same
/// file/class/method content that <see cref="DetailPanelRenderer"/> produces for the main
/// window's detail panel. One instance lives inside MainWindow — each node click swaps the
/// content in place, and the Back button raises <see cref="BackRequested"/> so MainWindow
/// can return to the tree. Content is rebuilt on theme switches (like the main detail
/// panel) because the renderer resolves brushes with one-time <c>FindResource</c> lookups.
/// </summary>
public partial class NodeDetailView : UserControl
{
    /// <summary>Raised when the user asks to leave this page (Back button).</summary>
    public event EventHandler? BackRequested;

    private Action? _renderAction;

    public NodeDetailView()
    {
        InitializeComponent();
        // This control lives for the application's lifetime, so the static subscription
        // is intentionally never removed.
        ThemeManager.ThemeChanged += (_, _) => _renderAction?.Invoke();
    }

    /// <summary>
    /// Sets the page title and renders new content. The renderer callback is retained so
    /// the content can be rebuilt when the theme changes.
    /// </summary>
    public void ShowDetail(string title, Action<StackPanel> render)
    {
        HeaderTitle.Text = title;
        _renderAction = () => render(DetailHost);
        _renderAction();
        DetailScroll.ScrollToVerticalOffset(0);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}

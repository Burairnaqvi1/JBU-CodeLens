using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace JBU.CodeLens.UI.Views;

/// <summary>
/// In-app page that renders the scanned project as a zoomable, pannable left-to-right tree
/// diagram (project → folders → files → classes → methods) with curved connectors. Hosted
/// inside MainWindow; the Back button raises <see cref="BackRequested"/> and clicking a
/// file/class/method node raises <see cref="NodeClicked"/> — MainWindow owns the navigation.
/// Every element is built in code, so brushes are attached with <c>SetResourceReference</c>
/// and the page survives theme switches without a rebuild.
/// </summary>
/// <summary>
/// Carries the clicked node's payload — a file path, <c>ClassInfo</c>, or <c>MethodInfo</c> —
/// to <see cref="VisualizationView.NodeClicked"/> subscribers.
/// </summary>
public sealed class NodeClickedEventArgs(object payload) : EventArgs
{
    public object Payload { get; } = payload;
}

public partial class VisualizationView : UserControl
{
    private const double RowHeight = 34;
    private const double NodeHeight = 26;
    private const double HorizontalGap = 56;
    private const double CanvasPadding = 24;
    private const double MinScale = 0.08;
    private const double MaxScale = 2.5;

    /// <summary>Fit-to-view never goes below this zoom; smaller and node labels are unreadable.</summary>
    private const double ReadableFitFloor = 0.55;
    private const double ZoomStep = 1.15;
    // Beyond this many nodes a single-canvas render stalls the dispatcher for seconds;
    // the depth selector refuses to switch rather than freeze the app.
    private const int MaxRenderedNodes = 4000;
    private const int MaxLabelLength = 36;

    private enum VizDepth { Files, Classes, Methods }

    private enum NodeKind { Project, Folder, File, Class, Method }

    private sealed class VizNode
    {
        public string Label = "";
        public string? Badge;
        public NodeKind Kind;
        public object? NavTag;
        public string? Tip;
        public bool IsCpp;
        public bool HasError;
        public List<VizNode> Children = [];
        public int Depth;
        public double Y;
        public double W;
    }

    /// <summary>Intermediate directory hierarchy built from the scanned file paths.</summary>
    private sealed class FolderEntry
    {
        public SortedDictionary<string, FolderEntry> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ParseResult> Files { get; } = [];

        public FolderEntry GetOrAdd(string name)
        {
            if (Folders.TryGetValue(name, out var existing))
            {
                return existing;
            }

            var created = new FolderEntry();
            Folders[name] = created;
            return created;
        }
    }

    /// <summary>Raised when the user asks to leave this page (Back button).</summary>
    public event EventHandler? BackRequested;

    /// <summary>Raised with the clicked node's payload: file path, ClassInfo, or MethodInfo.</summary>
    public event EventHandler<NodeClickedEventArgs>? NodeClicked;

    /// <summary>Whether a project has ever been loaded into this page (used to refresh after rescans).</summary>
    public bool HasProject => _results.Count > 0;

    private string _projectName = "";
    private string _rootPath = "";
    private IReadOnlyList<ParseResult> _results = [];
    private VizDepth _depth = VizDepth.Classes;

    private bool _isPanning;
    private Point _panStart;
    private Point _panStartOffsets;

    public VisualizationView()
    {
        InitializeComponent();
        UpdateDepthButtons();
        StatusText.Text = "Nothing to visualize yet.";
    }

    /// <summary>
    /// Replaces the displayed project and re-renders at the current depth (falling back to
    /// Classes when Methods would exceed the node cap), then fits the tree to the viewport.
    /// </summary>
    public void LoadProject(string projectName, string rootPath, IReadOnlyList<ParseResult> results)
    {
        _projectName = projectName;
        _rootPath = rootPath;
        _results = results;

        HeaderTitle.Text = $"Project Tree — {projectName}";

        if (_depth == VizDepth.Methods && CountNodes(BuildTree(VizDepth.Methods)) > MaxRenderedNodes)
        {
            _depth = VizDepth.Classes;
            UpdateDepthButtons();
        }

        Render();
        Dispatcher.InvokeAsync(
            () =>
            {
                FitToView();
                // PreviewKeyDown tunnels along the focus path, so the zoom keys only reach this
                // page once focus is actually inside it.
                Focus();
            },
            DispatcherPriority.Loaded);
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void DepthButton_Click(object sender, RoutedEventArgs e)
    {
        VizDepth target;
        if (ReferenceEquals(sender, DepthFilesButton))
        {
            target = VizDepth.Files;
        }
        else if (ReferenceEquals(sender, DepthMethodsButton))
        {
            target = VizDepth.Methods;
        }
        else
        {
            target = VizDepth.Classes;
        }

        if (target == _depth)
        {
            return;
        }

        var count = CountNodes(BuildTree(target));
        if (count > MaxRenderedNodes)
        {
            StatusText.Text =
                $"The {target} view needs {count:N0} nodes (limit {MaxRenderedNodes:N0}) — keeping the {_depth} view.";
            return;
        }

        _depth = target;
        UpdateDepthButtons();
        Render();
        Dispatcher.InvokeAsync(FitToView, DispatcherPriority.Loaded);
    }

    private void UpdateDepthButtons()
    {
        foreach (var (button, depth) in new[]
        {
            (DepthFilesButton, VizDepth.Files),
            (DepthClassesButton, VizDepth.Classes),
            (DepthMethodsButton, VizDepth.Methods),
        })
        {
            if (depth == _depth)
            {
                button.SetResourceReference(BackgroundProperty, "PrimaryBrush");
                button.SetResourceReference(ForegroundProperty, "SurfaceBrush");
            }
            else
            {
                button.Background = Brushes.Transparent;
                button.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
            }
        }
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(ZoomStep);

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(1 / ZoomStep);

    private void FitButton_Click(object sender, RoutedEventArgs e) => FitToView();

    // ── Save as image ────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the whole diagram to a PNG at its natural size, independent of the current zoom
    /// and of what happens to be scrolled into view.
    /// </summary>
    /// <remarks>
    /// The diagram was previously trapped in the window — a report or slide could only get it
    /// through a screenshot, which captures the visible portion at screen resolution.
    /// </remarks>
    private void SaveImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(TreeCanvas.Width) || TreeCanvas.Width <= 0 || TreeCanvas.Height <= 0)
        {
            return;
        }

        var suggested = string.IsNullOrEmpty(_projectName) ? "diagram" : _projectName;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the diagram",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"{SanitizeFileName(suggested)} - structure.png",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, RenderCanvasToPng());
        }
        catch (IOException ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The image could not be saved.\n\n{ex.Message}",
                "Save diagram",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(
                Window.GetWindow(this),
                $"The image could not be saved.\n\n{ex.Message}",
                "Save diagram",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private byte[] RenderCanvasToPng()
    {
        // The live canvas carries the zoom transform, so it is rendered through a temporary
        // visual at scale 1 — otherwise the file would come out at whatever zoom the reader
        // happened to leave the view on.
        const double dpi = 96;
        const double exportScale = 2; // legible when dropped into a document at half size
        var width = TreeCanvas.Width;
        var height = TreeCanvas.Height;

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(width * exportScale),
            (int)Math.Ceiling(height * exportScale),
            dpi * exportScale,
            dpi * exportScale,
            PixelFormats.Pbgra32);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            // Painted rather than left transparent: the node labels follow the theme, and light
            // text on a transparent background is invisible in most viewers.
            var background = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
            context.DrawRectangle(background, null, new Rect(0, 0, width, height));
            context.DrawRectangle(new VisualBrush(TreeCanvas), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "diagram" : cleaned;
    }

    // ── Zoom & pan ───────────────────────────────────────────────────────────

    /// <summary>
    /// Keyboard zoom and fit. The diagram could only be zoomed with the wheel or the toolbar
    /// buttons, which left it unusable without a mouse.
    /// </summary>
    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            // OemPlus/OemMinus are the main row; Add/Subtract are the numeric keypad. Both are
            // accepted with and without Ctrl — nothing else on this page takes typed input, so
            // requiring the modifier would only be a rule to remember.
            case Key.OemPlus or Key.Add:
                ZoomAtViewportCenter(ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAtViewportCenter(1 / ZoomStep);
                break;
            // Ctrl+0 only. Ctrl+F is the filter shortcut and MainWindow claims it before the
            // event tunnels this far.
            case Key.D0 or Key.NumPad0 when ctrl:
                FitToView();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void CanvasScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        ZoomAt(e.Delta > 0 ? ZoomStep : 1 / ZoomStep, e.GetPosition(CanvasScroll));
    }

    private void ZoomAtViewportCenter(double factor) =>
        ZoomAt(factor, new Point(CanvasScroll.ViewportWidth / 2, CanvasScroll.ViewportHeight / 2));

    /// <summary>Scales the canvas keeping the content under <paramref name="anchor"/> fixed.</summary>
    private void ZoomAt(double factor, Point anchor)
    {
        var oldScale = CanvasScale.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, MinScale, MaxScale);
        if (Math.Abs(newScale - oldScale) < 0.0001)
        {
            return;
        }

        var contentX = (CanvasScroll.HorizontalOffset + anchor.X) / oldScale;
        var contentY = (CanvasScroll.VerticalOffset + anchor.Y) / oldScale;

        CanvasScale.ScaleX = newScale;
        CanvasScale.ScaleY = newScale;
        CanvasScroll.UpdateLayout();
        CanvasScroll.ScrollToHorizontalOffset(contentX * newScale - anchor.X);
        CanvasScroll.ScrollToVerticalOffset(contentY * newScale - anchor.Y);
    }

    private void FitToView()
    {
        if (double.IsNaN(TreeCanvas.Width) || TreeCanvas.Width <= 0 || TreeCanvas.Height <= 0)
        {
            return;
        }

        var viewportWidth = CanvasScroll.ViewportWidth > 0 ? CanvasScroll.ViewportWidth : CanvasScroll.ActualWidth;
        var viewportHeight = CanvasScroll.ViewportHeight > 0 ? CanvasScroll.ViewportHeight : CanvasScroll.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        // Never enlarge past 100% — small projects should not blow up to fill the view — and
        // never shrink below a readable floor: fitting a big tree entirely renders labels as
        // unreadable specks, so past the floor the view fits-as-far-as-legible and pans.
        var scale = Math.Clamp(
            Math.Min(viewportWidth / TreeCanvas.Width, viewportHeight / TreeCanvas.Height) * 0.97,
            Math.Max(MinScale, ReadableFitFloor),
            1.0);
        CanvasScale.ScaleX = scale;
        CanvasScale.ScaleY = scale;
        CanvasScroll.UpdateLayout();
        CanvasScroll.ScrollToHorizontalOffset(0);
        CanvasScroll.ScrollToVerticalOffset(0);
    }

    private void TreeCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPanning = true;
        _panStart = e.GetPosition(CanvasScroll);
        _panStartOffsets = new Point(CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);
        TreeCanvas.CaptureMouse();
        TreeCanvas.Cursor = Cursors.SizeAll;
    }

    private void TreeCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        var pos = e.GetPosition(CanvasScroll);
        CanvasScroll.ScrollToHorizontalOffset(_panStartOffsets.X - (pos.X - _panStart.X));
        CanvasScroll.ScrollToVerticalOffset(_panStartOffsets.Y - (pos.Y - _panStart.Y));
    }

    private void TreeCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        TreeCanvas.ReleaseMouseCapture();
        TreeCanvas.Cursor = Cursors.Arrow;
    }

    // ── Tree construction ────────────────────────────────────────────────────

    private VizNode BuildTree(VizDepth depth)
    {
        var root = new VizNode
        {
            Label = string.IsNullOrEmpty(_projectName) ? "Project" : _projectName,
            Kind = NodeKind.Project,
            Tip = _rootPath,
        };

        var rootFolder = new FolderEntry();
        foreach (var result in _results)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(_rootPath, result.FilePath);
            }
            catch (ArgumentException)
            {
                relative = result.FilePath;
            }

            var folder = rootFolder;
            var directory = Path.GetDirectoryName(relative) ?? "";
            if (directory.Length > 0 && directory != ".")
            {
                foreach (var segment in directory.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment == ".")
                    {
                        continue;
                    }

                    folder = folder.GetOrAdd(segment);
                }
            }

            folder.Files.Add(result);
        }

        AppendFolderContents(root, rootFolder, depth);
        return root;
    }

    private static void AppendFolderContents(VizNode parent, FolderEntry folder, VizDepth depth)
    {
        foreach (var (name, sub) in folder.Folders)
        {
            // Collapse single-child folder chains ("src/JBU.CodeLens.UI") into one node so pure
            // pass-through directories don't waste a whole column each.
            var label = name;
            var current = sub;
            while (current.Files.Count == 0 && current.Folders.Count == 1)
            {
                var (childName, child) = current.Folders.First();
                label = $"{label}/{childName}";
                current = child;
            }

            var folderNode = new VizNode { Label = label, Kind = NodeKind.Folder, Tip = label };
            parent.Children.Add(folderNode);
            AppendFolderContents(folderNode, current, depth);
        }

        foreach (var file in folder.Files.OrderBy(f => Path.GetFileName(f.FilePath), StringComparer.OrdinalIgnoreCase))
        {
            parent.Children.Add(BuildFileNode(file, depth));
        }
    }

    private static VizNode BuildFileNode(ParseResult result, VizDepth depth)
    {
        var node = new VizNode
        {
            Label = Path.GetFileName(result.FilePath),
            Kind = NodeKind.File,
            NavTag = result.FilePath,
            Tip = result.FilePath,
            IsCpp = LanguageFileExtensions.IsCppFile(result.FilePath),
            HasError = result.Errors.Count > 0,
        };

        if (node.HasError)
        {
            node.Badge = "!";
            node.Tip = $"{result.FilePath}\nParse error: {string.Join("; ", result.Errors)}";
            return node;
        }

        if (depth == VizDepth.Files)
        {
            node.Badge = result.Classes.Count.ToString(CultureInfo.InvariantCulture);
            return node;
        }

        foreach (var classInfo in result.Classes)
        {
            var classNode = new VizNode
            {
                Label = classInfo.Name,
                Kind = NodeKind.Class,
                NavTag = classInfo,
                Tip = string.IsNullOrWhiteSpace(classInfo.XmlSummary)
                    ? $"{classInfo.Name} — {classInfo.Methods.Count} methods, {classInfo.Properties.Count} properties"
                    : classInfo.XmlSummary.Trim(),
            };

            if (depth == VizDepth.Methods)
            {
                foreach (var method in classInfo.Methods)
                {
                    classNode.Children.Add(new VizNode
                    {
                        Label = $"{method.Name}()",
                        Kind = NodeKind.Method,
                        NavTag = method,
                        Tip = $"{method.ReturnType} {method.Name}({string.Join(", ", method.Parameters)})",
                    });
                }
            }
            else
            {
                classNode.Badge = classInfo.Methods.Count.ToString(CultureInfo.InvariantCulture);
            }

            node.Children.Add(classNode);
        }

        if (node.Children.Count == 0)
        {
            node.Badge = "0";
        }

        return node;
    }

    private static int CountNodes(VizNode node) => 1 + node.Children.Sum(CountNodes);

    // ── Layout & rendering ───────────────────────────────────────────────────

    private void Render()
    {
        TreeCanvas.Children.Clear();

        if (_results.Count == 0)
        {
            TreeCanvas.Width = 0;
            TreeCanvas.Height = 0;
            StatusText.Text = "Nothing to visualize — scan a project first.";
            return;
        }

        var root = BuildTree(_depth);

        var columnWidths = new List<double>();
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        MeasureNode(root, 0, columnWidths, pixelsPerDip);

        var nextLeafTop = CanvasPadding;
        AssignY(root, ref nextLeafTop);

        var columnX = new double[columnWidths.Count];
        var x = CanvasPadding;
        for (var i = 0; i < columnWidths.Count; i++)
        {
            columnX[i] = x;
            x += columnWidths[i] + HorizontalGap;
        }

        TreeCanvas.Width = x - HorizontalGap + CanvasPadding;
        TreeCanvas.Height = nextLeafTop + CanvasPadding - (RowHeight - NodeHeight) / 2;

        DrawEdges(root, columnX);
        DrawNodes(root, columnX);

        StatusText.Text =
            $"{_results.Count} files · {CountNodes(root):N0} nodes · depth: {_depth}";
    }

    private void MeasureNode(VizNode node, int depth, List<double> columnWidths, double pixelsPerDip)
    {
        node.Depth = depth;

        if (node.Label.Length > MaxLabelLength)
        {
            node.Tip ??= node.Label;
            node.Label = node.Label[..(MaxLabelLength - 1)] + "…";
        }

        var (family, size, weight) = FontFor(node.Kind);
        // Padding 10 + dot 8 + gap 8 + label (+ gap 6 + badge) + padding 10.
        node.W = 10 + 8 + 8 + MeasureText(node.Label, family, size, weight, pixelsPerDip) + 10;
        if (node.Badge is not null)
        {
            node.W += 6 + MeasureText(node.Badge, (FontFamily)FindResource("UiFont"), 10, FontWeights.SemiBold, pixelsPerDip);
        }

        if (columnWidths.Count <= depth)
        {
            columnWidths.Add(0);
        }

        columnWidths[depth] = Math.Max(columnWidths[depth], node.W);

        foreach (var child in node.Children)
        {
            MeasureNode(child, depth + 1, columnWidths, pixelsPerDip);
        }
    }

    private double MeasureText(string text, FontFamily family, double size, FontWeight weight, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            size,
            (Brush)FindResource("TextPrimaryBrush"),
            pixelsPerDip);
        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
    }

    private (FontFamily Family, double Size, FontWeight Weight) FontFor(NodeKind kind) => kind switch
    {
        NodeKind.Project => ((FontFamily)FindResource("UiFont"), 13, FontWeights.SemiBold),
        NodeKind.Folder => ((FontFamily)FindResource("UiFont"), 12, FontWeights.SemiBold),
        NodeKind.Method => ((FontFamily)FindResource("CodeFont"), 11, FontWeights.Normal),
        _ => ((FontFamily)FindResource("UiFont"), 12, FontWeights.Normal),
    };

    private static void AssignY(VizNode node, ref double nextLeafTop)
    {
        if (node.Children.Count == 0)
        {
            node.Y = nextLeafTop + RowHeight / 2;
            nextLeafTop += RowHeight;
            return;
        }

        foreach (var child in node.Children)
        {
            AssignY(child, ref nextLeafTop);
        }

        node.Y = (node.Children[0].Y + node.Children[^1].Y) / 2;
    }

    private void DrawEdges(VizNode node, double[] columnX)
    {
        foreach (var child in node.Children)
        {
            var start = new Point(columnX[node.Depth] + node.W, node.Y);
            var end = new Point(columnX[child.Depth], child.Y);

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new BezierSegment(
                new Point(start.X + HorizontalGap * 0.55, start.Y),
                new Point(end.X - HorizontalGap * 0.55, end.Y),
                end,
                isStroked: true));
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Freeze();

            var path = new Shapes.Path
            {
                Data = geometry,
                StrokeThickness = 1.3,
                Opacity = 0.85,
                IsHitTestVisible = false,
            };
            path.SetResourceReference(Shapes.Shape.StrokeProperty, "BorderBrush");
            TreeCanvas.Children.Add(path);

            DrawEdges(child, columnX);
        }
    }

    private void DrawNodes(VizNode node, double[] columnX)
    {
        var accentKey = AccentKeyFor(node);
        var restBorderKey = node.Kind == NodeKind.Project ? "PrimaryBrush" : "BorderBrush";

        var dot = new Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shapes.Shape.FillProperty, accentKey);

        var (family, size, weight) = FontFor(node.Kind);
        var label = new TextBlock
        {
            Text = node.Label,
            FontFamily = family,
            FontSize = size,
            FontWeight = weight,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(dot);
        content.Children.Add(label);

        if (node.Badge is not null)
        {
            var badge = new TextBlock
            {
                Text = node.Badge,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            badge.SetResourceReference(TextBlock.ForegroundProperty, node.HasError ? "ErrorBrush" : "TextSecondaryBrush");
            content.Children.Add(badge);
        }

        FrameworkElement nodeElement;
        if (node.NavTag is { } navTag)
        {
            // Clickable nodes are real Buttons: the app-wide flat template supplies the
            // rounded chrome and hover overlay, and UIA/keyboard invocation works for free.
            var button = new Button
            {
                Width = node.W,
                Height = NodeHeight,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0),
                Content = content,
                SnapsToDevicePixels = true,
            };
            button.SetResourceReference(BackgroundProperty, "SurfaceBrush");
            button.SetResourceReference(BorderBrushProperty, restBorderKey);
            AutomationProperties.SetName(button, node.Label);
            button.Click += (_, _) => NodeClicked?.Invoke(this, new NodeClickedEventArgs(navTag));
            nodeElement = button;
        }
        else
        {
            var border = new Border
            {
                Width = node.W,
                Height = NodeHeight,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 0, 10, 0),
                Child = content,
                SnapsToDevicePixels = true,
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, restBorderKey);
            // Swallow the press so clicking a node never starts a canvas pan.
            border.MouseLeftButtonDown += (_, e) => e.Handled = true;
            nodeElement = border;
        }

        if (node.Tip is not null)
        {
            nodeElement.ToolTip = node.Tip;
        }

        Canvas.SetLeft(nodeElement, columnX[node.Depth]);
        Canvas.SetTop(nodeElement, node.Y - NodeHeight / 2);
        TreeCanvas.Children.Add(nodeElement);

        foreach (var child in node.Children)
        {
            DrawNodes(child, columnX);
        }
    }

    private static string AccentKeyFor(VizNode node) => node.Kind switch
    {
        NodeKind.Project => "PrimaryBrush",
        NodeKind.File when node.HasError => "ErrorBrush",
        // Language accents match the sidebar badges: C# green, C++ blue.
        NodeKind.File => node.IsCpp ? "PrimaryBrush" : "SecondaryBrush",
        NodeKind.Class => "WarningBrush",
        _ => "TextSecondaryBrush",
    };
}

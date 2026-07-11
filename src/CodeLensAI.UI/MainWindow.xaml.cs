using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeLensAI.Core;
using CodeLensAI.Core.Analysis;
using CodeLensAI.Core.Structural;
using Microsoft.Win32;
using LensMethod = CodeLensAI.Core.MethodInfo;

namespace CodeLensAI.UI;

public partial class MainWindow : Window
{
    // Structural analysis runs through a single ScideEngine parse pass, shared with the file/class
    // tree below (see ScanFolderAsync). AI runs exclusively through the single ExplanationService
    // instance below — there's no second LLM to disable anymore, that path was removed entirely.
    private readonly ScideEngine _scideEngine = new();
    private ExplanationService? _explanationService;
    private readonly Dictionary<string, ParseResult> _parseCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ParseResult> _lastScanResults = [];
    private string? _lastScannedFolder;
    private ProjectIR? _lastProjectIr;
    private MetricsResult? _lastMetrics;
    private Dictionary<string, CodeLensAI.Core.Structural.MethodInfo> _scideMethodIndex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TypeInfo> _scideTypeIndex = new(StringComparer.OrdinalIgnoreCase);

    private string? _selectedFolderPath;
    private bool _hasScanResults;

    // What the detail panel currently shows ("project", "metrics", a file path, ClassInfo, or
    // LensMethod — mirroring tree Tag values). Used to re-render after a theme switch, because
    // detail-panel elements are built in code and would otherwise keep the old theme's brushes.
    private object? _currentDetailContext;
    private int _classCount;
    private int _methodCount;
    private bool _suppressTreeSelectionChanged;
    private bool _isDarkTheme = true;
    private bool _isScanning;

    private string? SelectedFolderPath
    {
        get => _selectedFolderPath;
        set
        {
            _selectedFolderPath = value;
            ScanButton.IsEnabled = !string.IsNullOrEmpty(value);
            ProjectPathText.Text = string.IsNullOrEmpty(value) ? "No project loaded" : value;
            if (!_hasScanResults)
            {
                SetExportButtonsEnabled(false);
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ApplyDarkTheme();

        StatusBarText.Text = "Loading AI model…";
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _explanationService?.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();

        var modelPath = ModelPathResolver.Resolve();
        if (modelPath is null)
        {
            StatusBarText.Text = "Model not found — AI features disabled";
            _explanationService = await Task.Run(() => new ExplanationService(string.Empty));
            return;
        }

        StatusBarText.Text = $"Loading AI model… ({Path.GetFileName(modelPath)})";
        _explanationService = await Task.Run(() => new ExplanationService(modelPath));

        StatusBarText.Text = _explanationService.IsReady
            ? $"AI model ready — {Path.GetFileName(_explanationService.ModelPath)}"
            : "Model not found — AI features disabled";
    }

    /// <summary>
    /// Sizes and positions the window so the title bar and controls stay within the visible work area.
    /// </summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        const double margin = 32;

        var maxWidth = Math.Max(MinWidth, area.Width - margin);
        var maxHeight = Math.Max(MinHeight, area.Height - margin);

        if (Width > maxWidth)
            Width = maxWidth;
        if (Height > maxHeight)
            Height = maxHeight;

        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    private void LightThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDarkTheme) ApplyLightTheme();
    }

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDarkTheme) ApplyDarkTheme();
    }

    private void ApplyDarkTheme()
    {
        _isDarkTheme = true;
        SetBrush("BackgroundBrush",  "#1E1E1E");
        SetBrush("SurfaceBrush",     "#2D2D30");
        SetBrush("BorderBrush",      "#3F3F46");
        SetBrush("AccentBrush",      "#007ACC");
        SetBrush("AccentHoverBrush", "#1C97EA");
        SetBrush("TextBrush",        "#E0E0E0");
        SetBrush("MutedBrush",       "#888888");
        SetBrush("HeaderBrush",      "#1A1A1A");
        SetBrush("StatusBarBrush",   "#1A1A1A");
        SetBrush("SidebarBrush",     "#252526");
        SetBrush("ButtonBgBrush",    "#2D2D30");
        SetBrush("ButtonFgBrush",    "#E0E0E0");
        SetBrush("HoverOverlayBrush",     "#1FFFFFFF");
        SetBrush("PressedOverlayBrush",   "#38FFFFFF");
        SetBrush("ScrollThumbBrush",      "#4D4D55");
        SetBrush("ScrollThumbHoverBrush", "#6A6A75");
        SetBrush("TreeSelectionBrush",    "#094771");

        DarkThemeButton.Background  = (Brush)FindResource("AccentBrush");
        DarkThemeButton.Foreground  = Brushes.White;
        LightThemeButton.Background = Brushes.Transparent;
        LightThemeButton.Foreground = (Brush)FindResource("MutedBrush");

        RefreshCurrentDetailView();
    }

    private void ApplyLightTheme()
    {
        _isDarkTheme = false;
        SetBrush("BackgroundBrush",  "#F5F5F5");
        SetBrush("SurfaceBrush",     "#FFFFFF");
        SetBrush("BorderBrush",      "#CCCCCC");
        SetBrush("AccentBrush",      "#007ACC");
        SetBrush("AccentHoverBrush", "#1C97EA");
        SetBrush("TextBrush",        "#1E1E1E");
        SetBrush("MutedBrush",       "#666666");
        SetBrush("HeaderBrush",      "#E8E8E8");
        SetBrush("StatusBarBrush",   "#E8E8E8");
        SetBrush("SidebarBrush",     "#F0F0F0");
        SetBrush("ButtonBgBrush",    "#E0E0E0");
        SetBrush("ButtonFgBrush",    "#1E1E1E");
        SetBrush("HoverOverlayBrush",     "#14000000");
        SetBrush("PressedOverlayBrush",   "#26000000");
        SetBrush("ScrollThumbBrush",      "#C4C4C4");
        SetBrush("ScrollThumbHoverBrush", "#A8A8A8");
        SetBrush("TreeSelectionBrush",    "#CCE4F7");

        LightThemeButton.Background = (Brush)FindResource("AccentBrush");
        LightThemeButton.Foreground = Brushes.White;
        DarkThemeButton.Background  = Brushes.Transparent;
        DarkThemeButton.Foreground  = (Brush)FindResource("MutedBrush");

        RefreshCurrentDetailView();
    }

    private void SetBrush(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);

        // Mutate the existing brush instead of replacing it: elements built in code resolve
        // these brushes once at creation (FindResource) and hold the instance, so swapping in
        // a new brush would leave every already-rendered panel stuck in the old theme.
        // Look in window resources first, then application resources — the base brushes
        // (BackgroundBrush, TextBrush, …) live in App.xaml, and creating window-level copies
        // instead would leave every app-scope StaticResource reference stuck in the old theme.
        var existing = (Resources[key] ?? Application.Current.Resources[key]) as SolidColorBrush;
        if (existing is { IsFrozen: false })
        {
            existing.Color = color;
        }
        else
        {
            Resources[key] = new SolidColorBrush(color);
        }
    }

    // ── Drag & drop ──────────────────────────────────────────────────────────

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleFolder(e, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (_hasScanResults) return;
        StatusBarText.Text = string.IsNullOrEmpty(SelectedFolderPath)
            ? "AI model ready"
            : $"Folder selected: {SelectedFolderPath}";
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetSingleFolder(e, out var folderPath))
        {
            if (_isScanning)
            {
                StatusBarText.Text = "A scan is already running — drop the folder again when it finishes.";
            }
            else
            {
                SelectedFolderPath = folderPath;
                StatusBarText.Text = $"Folder selected: {folderPath}";
                ScanProject();
            }
        }
        e.Handled = true;
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a project folder",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true)
        {
            SelectedFolderPath = dialog.FolderName;
            StatusBarText.Text = $"Folder selected: {dialog.FolderName}";
            ScanProject();
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e) => _ = ScanProjectAsync();

    private async Task ScanProjectAsync()
    {
        if (string.IsNullOrEmpty(SelectedFolderPath) || _isScanning)
        {
            return;
        }

        _isScanning = true;
        SetScanUiState(scanning: true);
        ProjectTree.Items.Clear();
        _parseCache.Clear();
        _lastScanResults = [];
        _lastScannedFolder = SelectedFolderPath;
        _lastProjectIr = null;
        _lastMetrics = null;
        _scideMethodIndex = new(StringComparer.OrdinalIgnoreCase);
        _scideTypeIndex = new(StringComparer.OrdinalIgnoreCase);
        ShowPlaceholder();
        _hasScanResults = false;
        StatusBarText.Text = "Scanning…";

        var folderPath = SelectedFolderPath;

        try
        {
            // Task.Run keeps the sequential post-parse work (relationships, graph, metrics) off
            // the dispatcher; the parse itself fans out on worker threads inside the engine.
            var scanResult = await Task.Run(() => _scideEngine.AnalyzeProjectAsync(folderPath));

            if (!scanResult.Success)
            {
                StatusBarText.Text = scanResult.Error ?? "Scan failed.";
                return;
            }

            if (scanResult.ParseResults.Count == 0)
            {
                StatusBarText.Text = "Scan complete — no .cs, .cpp, .hpp, or .h source files found.";
                return;
            }

            _lastProjectIr = scanResult.Ir;
            _lastMetrics = scanResult.Metrics;
            _scideMethodIndex = scanResult.ScideMethodIndex;
            _scideTypeIndex = scanResult.ScideTypeIndex;
            _classCount = scanResult.ClassCount;
            _methodCount = scanResult.MethodCount;

            foreach (var parseResult in scanResult.ParseResults)
            {
                _parseCache[parseResult.FilePath] = parseResult;
                _lastScanResults.Add(parseResult);
            }

            var projectName = Path.GetFileName(
                folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var rootItem = new TreeViewItem
            {
                Header = projectName,
                Tag = "project",
                IsExpanded = true,
            };

            if (_lastMetrics is not null)
            {
                rootItem.Items.Add(BuildMetricsNode(_lastMetrics));
            }

            if (_lastProjectIr?.Namespaces.Count > 0)
            {
                rootItem.Items.Add(BuildNamespacesNode(_lastProjectIr));
            }

            foreach (var parseResult in scanResult.ParseResults)
            {
                rootItem.Items.Add(BuildFileNode(parseResult.FilePath, parseResult));
            }

            ProjectTree.Items.Add(rootItem);
            _hasScanResults = true;

            var metricsSuffix = _lastMetrics is { } m
                ? $", MI={m.MaintainabilityIndex:F0}, {_lastProjectIr?.Namespaces.Count ?? 0} namespaces"
                : string.Empty;
            StatusBarText.Text =
                $"Scan complete — {scanResult.ParseResults.Count} files, {_classCount} classes, {_methodCount} functions{metricsSuffix}";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _isScanning = false;
            SetScanUiState(scanning: false);
        }
    }

    private void ScanProject() => _ = ScanProjectAsync();

    private void SetScanUiState(bool scanning)
    {
        BrowseButton.IsEnabled = !scanning;
        ScanButton.IsEnabled = !scanning && !string.IsNullOrEmpty(SelectedFolderPath);
        SetExportButtonsEnabled(!scanning && _hasScanResults);
    }

    private void SetExportButtonsEnabled(bool enabled)
    {
        ExportWordButton.IsEnabled = enabled;
        ExportMarkdownButton.IsEnabled = enabled;
        ExportJsonButton.IsEnabled = enabled;
    }

    private static ProjectMetricsSnapshot? ToMetricsSnapshot(MetricsResult? metrics) =>
        metrics is null
            ? null
            : new ProjectMetricsSnapshot
            {
                TotalClasses = metrics.TotalClasses,
                TotalMethods = metrics.TotalMethods,
                TotalNamespaces = metrics.TotalNamespaces,
                TotalRelationships = metrics.TotalRelationships,
                MaintainabilityIndex = metrics.MaintainabilityIndex,
                AverageComplexity = metrics.AverageComplexity,
                MaxComplexity = metrics.MaxComplexity,
            };

    private bool EnsureScanResultsForExport(string title)
    {
        if (_hasScanResults && _lastScanResults.Count > 0 && !string.IsNullOrEmpty(_lastScannedFolder))
        {
            return true;
        }

        MessageBox.Show(this, "Please scan a project first.", title,
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private TreeViewItem BuildMetricsNode(MetricsResult metrics)
    {
        var item = new TreeViewItem
        {
            Header = CreateMutedHeader(
                $"Metrics — {metrics.TotalClasses} classes, {metrics.TotalMethods} methods, MI={metrics.MaintainabilityIndex:F0}"),
            Tag = "metrics",
            IsExpanded = false,
        };
        return item;
    }

    private TreeViewItem BuildNamespacesNode(ProjectIR ir)
    {
        var nsRoot = new TreeViewItem
        {
            Header = CreateMutedHeader($"Namespaces ({ir.Namespaces.Count})"),
            Tag = "namespaces",
            IsExpanded = false,
        };

        foreach (var ns in ir.Namespaces.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            var nsItem = new TreeViewItem
            {
                Header = ns.Name,
                Tag = $"ns:{ns.Name}",
                IsExpanded = false,
            };

            foreach (var cls in ns.Classes)
            {
                nsItem.Items.Add(new TreeViewItem
                {
                    Header = $"{cls.Name} (class)",
                    IsEnabled = false,
                });
            }

            nsRoot.Items.Add(nsItem);
        }

        return nsRoot;
    }

    private TreeViewItem BuildFileNode(string filePath, ParseResult result)
    {
        var fileName = Path.GetFileName(filePath);
        var isCpp = LanguageFileExtensions.IsCppFile(filePath);
        var fileItem = new TreeViewItem
        {
            Header = CreateFileHeader(fileName, isCpp),
            Tag = filePath,
            IsExpanded = false,
        };

        if (result.Errors.Count > 0)
        {
            fileItem.Items.Add(new TreeViewItem
            {
                Header = CreateMutedHeader($"Parse error: {string.Join("; ", result.Errors)}"),
                IsEnabled = false,
            });
            return fileItem;
        }

        if (result.Classes.Count == 0)
        {
            fileItem.Items.Add(new TreeViewItem
            {
                Header = CreateMutedHeader("(no top-level classes)"),
                IsEnabled = false,
            });
            return fileItem;
        }

        foreach (var classInfo in result.Classes)
        {
            var classItem = new TreeViewItem
            {
                Header = CreateClassHeader(classInfo),
                Tag = classInfo,
                IsExpanded = false,
            };

            foreach (var method in classInfo.Methods)
            {
                classItem.Items.Add(new TreeViewItem
                {
                    Header = CreateMethodHeader(method),
                    Tag = method,
                });
            }

            fileItem.Items.Add(classItem);
        }

        return fileItem;
    }

    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelectionChanged) return;

        if (e.NewValue is not TreeViewItem item)
        {
            ShowPlaceholder();
            return;
        }

        switch (item.Tag)
        {
            case "project":
                ShowProjectSummary();
                break;
            case "metrics":
                ShowMetricsSummary();
                break;
            case ClassInfo classInfo:
                ShowClassDetails(classInfo);
                break;
            case LensMethod methodInfo:
                ShowMethodDetails(methodInfo);
                break;
            case string filePath when !string.IsNullOrEmpty(filePath):
                ShowFileDetails(filePath);
                break;
            default:
                ShowPlaceholder();
                break;
        }
    }

    private async void ExportWordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureScanResultsForExport("Export to Word"))
        {
            return;
        }

        var aiChoice = MessageBox.Show(
            this,
            "Include AI-generated explanations?\n\n" +
            "• Yes — fuller document with brief descriptions, pre/post conditions, design constraints, and error analysis (slower).\n" +
            "• No — parser metadata and documentation only (fast).",
            "Export to Word",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (aiChoice == MessageBoxResult.Cancel)
        {
            return;
        }

        var includeAi = aiChoice == MessageBoxResult.Yes;
        if (includeAi && (_explanationService is null || !_explanationService.IsReady))
        {
            var fallback = MessageBox.Show(
                this,
                $"The AI model is not ready ({_explanationService?.LoadError ?? "still loading"}).\n\nExport without AI instead?",
                "Export to Word",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (fallback != MessageBoxResult.Yes)
            {
                return;
            }

            includeAi = false;
        }

        var projectName = Path.GetFileName(
            _lastScannedFolder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var dialog = new SaveFileDialog
        {
            Title = "Save project documentation",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = $"{projectName}_CodeLensAI_Documentation.docx",
            DefaultExt = ".docx",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var previousStatus = StatusBarText.Text;
        StatusBarText.Text = includeAi ? "Exporting with AI (this may take a while)…" : "Exporting documentation…";
        SetExportButtonsEnabled(false);

        try
        {
            var results = _lastScanResults;
            var folder = _lastScannedFolder!;
            var service = _explanationService;
            var useAi = includeAi;
            var metrics = ToMetricsSnapshot(_lastMetrics);

            await Task.Run(() => WordExporter.Export(
                dialog.FileName,
                folder,
                results,
                service,
                useAi,
                msg => Dispatcher.BeginInvoke(() => StatusBarText.Text = msg),
                metrics));

            var openResult = MessageBox.Show(this,
                "Documentation exported successfully. Open the file?",
                "Export to Word", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (openResult == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });

            StatusBarText.Text = $"Documentation exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = previousStatus;
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export to Word",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExportButtonsEnabled(true);
        }
    }

    private async void ExportMarkdownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureScanResultsForExport("Export to Markdown") || _lastProjectIr is null)
        {
            return;
        }

        var projectName = Path.GetFileName(
            _lastScannedFolder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var dialog = new SaveFileDialog
        {
            Title = "Save Markdown export",
            Filter = "Markdown (*.md)|*.md",
            FileName = $"{projectName}_CodeLensAI_Analysis.md",
            DefaultExt = ".md",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var previousStatus = StatusBarText.Text;
        StatusBarText.Text = "Exporting Markdown…";
        SetExportButtonsEnabled(false);

        try
        {
            var ir = _lastProjectIr;
            var results = _lastScanResults;
            await Task.Run(() => InferenceExportHelper.WriteMarkdownFile(ir, results, dialog.FileName));

            var openResult = MessageBox.Show(this,
                "Markdown exported successfully. Open the file?",
                "Export to Markdown", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (openResult == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }

            StatusBarText.Text = $"Markdown exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = previousStatus;
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export to Markdown",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExportButtonsEnabled(true);
        }
    }

    private async void ExportJsonButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureScanResultsForExport("Export to JSON") || _lastProjectIr is null)
        {
            return;
        }

        var projectName = Path.GetFileName(
            _lastScannedFolder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var dialog = new SaveFileDialog
        {
            Title = "Save JSON export",
            Filter = "JSON (*.json)|*.json",
            FileName = $"{projectName}_CodeLensAI_Analysis.json",
            DefaultExt = ".json",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        var previousStatus = StatusBarText.Text;
        StatusBarText.Text = "Exporting JSON…";
        SetExportButtonsEnabled(false);

        try
        {
            var ir = _lastProjectIr;
            var results = _lastScanResults;
            await Task.Run(() => InferenceExportHelper.WriteJsonFile(ir, results, dialog.FileName));

            var openResult = MessageBox.Show(this,
                "JSON exported successfully. Open the file?",
                "Export to JSON", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (openResult == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
            }

            StatusBarText.Text = $"JSON exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = previousStatus;
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export to JSON",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetExportButtonsEnabled(true);
        }
    }

    // ── Detail panel ─────────────────────────────────────────────────────────

    private void ShowPlaceholder()
    {
        _currentDetailContext = null;
        DetailPlaceholder.Visibility = Visibility.Visible;
        DetailScrollViewer.Visibility = Visibility.Collapsed;
        DetailPanelRenderer.Clear(DetailContentHost);
    }

    /// <summary>
    /// Re-renders whatever the detail panel currently shows. Called after a theme switch so
    /// code-built cards pick up the new theme's brushes instead of keeping the old colors.
    /// </summary>
    private void RefreshCurrentDetailView()
    {
        switch (_currentDetailContext)
        {
            case "project":
                ShowProjectSummary();
                break;
            case "metrics":
                ShowMetricsSummary();
                break;
            case ClassInfo classInfo:
                ShowClassDetails(classInfo);
                break;
            case LensMethod methodInfo:
                ShowMethodDetails(methodInfo);
                break;
            case string filePath when !string.IsNullOrEmpty(filePath):
                ShowFileDetails(filePath);
                break;
        }
    }

    private void ShowDetailContent()
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailScrollViewer.Visibility = Visibility.Visible;
        DetailPanelRenderer.Clear(DetailContentHost);
    }

    private void ShowFileDetails(string filePath)
    {
        ShowDetailContent();
        _currentDetailContext = filePath;
        _parseCache.TryGetValue(filePath, out var parseResult);
        DetailPanelRenderer.RenderFile(DetailContentHost, filePath, parseResult, this);
    }

    private void ShowProjectSummary()
    {
        ShowDetailContent();
        _currentDetailContext = "project";
        DetailPanelRenderer.Clear(DetailContentHost);

        var title = new TextBlock
        {
            Text = _lastProjectIr?.ProjectName ?? "Project",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        };
        DetailContentHost.Children.Add(title);

        if (_lastMetrics is { } m)
        {
            DetailContentHost.Children.Add(new TextBlock
            {
                Text = $"{m.TotalClasses} classes · {m.TotalMethods} methods · {m.TotalNamespaces} namespaces · MI {m.MaintainabilityIndex:F0}",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        if (_lastProjectIr is not null)
        {
            DetailContentHost.Children.Add(new TextBlock
            {
                Text = $"{_lastProjectIr.Relationships.Count} relationships · {_lastProjectIr.FilesAnalyzed} files analyzed",
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16),
            });
        }

        var summaryPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var summaryTitle = new TextBlock
        {
            Text = "AI Project Summary",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var summaryText = new TextBlock
        {
            Text = "Click Generate Summary to produce an architecture overview (Ollama or local GGUF).",
            Foreground = (Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var generateSummaryBtn = new Button
        {
            Content = "Generate Summary",
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
        };

        generateSummaryBtn.Click += async (_, _) =>
        {
            if (_lastProjectIr is null)
            {
                return;
            }

            generateSummaryBtn.IsEnabled = false;
            generateSummaryBtn.Content = "Generating…";
            summaryText.Text = "Generating project summary…";
            summaryText.Foreground = (Brush)FindResource("MutedBrush");

            var ir = _lastProjectIr;
            try
            {
                string summary;
                if (_explanationService is { IsReady: true } svc)
                {
                    var projectContext = BuildProjectContext(ir);
                    summary = await Task.Run(() => svc.GenerateProjectSummary(projectContext));
                }
                else
                {
                    summary = await Task.Run(() => _scideEngine.GetProjectSummaryFallback(ir));
                }

                summaryText.Text = summary;
                summaryText.Foreground = (Brush)FindResource("TextBrush");
            }
            catch (Exception ex)
            {
                summaryText.Text = $"Summary failed: {ex.Message}";
            }
            finally
            {
                generateSummaryBtn.Content = "Regenerate Summary";
                generateSummaryBtn.IsEnabled = true;
            }
        };

        summaryPanel.Children.Add(summaryTitle);
        summaryPanel.Children.Add(generateSummaryBtn);
        summaryPanel.Children.Add(summaryText);
        DetailContentHost.Children.Add(summaryPanel);
    }

    private string BuildProjectContext(ProjectIR ir)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Project: {ir.ProjectName}");

        if (_lastMetrics is { } m)
        {
            sb.AppendLine(
                $"Metrics: {m.TotalClasses} classes, {m.TotalMethods} methods, {m.TotalNamespaces} namespaces.");
            sb.AppendLine(
                $"Average cyclomatic complexity {m.AverageComplexity:F1} (max {m.MaxComplexity}); " +
                $"maintainability index {m.MaintainabilityIndex:F0}.");
        }

        var namespaces = ir.Namespaces
            .Select(n => n.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(8)
            .ToList();
        if (namespaces.Count > 0)
        {
            sb.AppendLine($"Key namespaces: {string.Join(", ", namespaces)}.");
        }

        var typeNames = ir.Classes
            .Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Take(12)
            .ToList();
        if (typeNames.Count > 0)
        {
            sb.AppendLine($"Notable types: {string.Join(", ", typeNames)}.");
        }

        return sb.ToString();
    }

    private void ShowMetricsSummary()
    {
        ShowDetailContent();
        _currentDetailContext = "metrics";
        DetailPanelRenderer.Clear(DetailContentHost);

        if (_lastMetrics is not { } m)
        {
            DetailContentHost.Children.Add(new TextBlock
            {
                Text = "No metrics available.",
                Foreground = (Brush)FindResource("MutedBrush"),
            });
            return;
        }

        DetailContentHost.Children.Add(new TextBlock
        {
            Text = "Project Metrics",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var lines = new[]
        {
            $"Classes: {m.TotalClasses}",
            $"Methods: {m.TotalMethods}",
            $"Properties: {m.TotalProperties}",
            $"Namespaces: {m.TotalNamespaces}",
            $"Relationships: {m.TotalRelationships}",
            $"Avg complexity: {m.AverageComplexity:F2} (max {m.MaxComplexity})",
            $"Maintainability index: {m.MaintainabilityIndex:F0}",
            $"Avg coupling: {m.AverageCoupling:F2}",
            $"Max inheritance depth: {m.MaxInheritanceDepth}",
        };

        foreach (var line in lines)
        {
            DetailContentHost.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
    }

    private void ShowClassDetails(ClassInfo classInfo)
    {
        ShowDetailContent();
        _currentDetailContext = classInfo;
        DetailPanelRenderer.RenderClass(DetailContentHost, classInfo, this,
            method => SelectMethodInTree(method));
    }

    private void ShowMethodDetails(LensMethod methodInfo)
    {
        ShowDetailContent();
        _currentDetailContext = methodInfo;

        // Brief Description resolves as: XML doc → organic inferred sentence → ExplanationService AI.
        var context = ScideMethodIndex.Build(
            methodInfo,
            _lastProjectIr,
            _scideMethodIndex,
            _scideTypeIndex);

        DetailPanelRenderer.RenderMethod(DetailContentHost, context, this, _explanationService);
    }

    private void SelectMethodInTree(LensMethod method)
    {
        if (ProjectTree.Items.Count == 0) { ShowMethodDetails(method); return; }

        var root = ProjectTree.Items[0] as TreeViewItem;
        if (root is null) { ShowMethodDetails(method); return; }

        foreach (var fileObj in root.Items)
        {
            if (fileObj is not TreeViewItem fileItem) continue;
            foreach (var classObj in fileItem.Items)
            {
                if (classObj is not TreeViewItem classItem || classItem.Tag is not ClassInfo classInfo) continue;
                if (!ReferenceEquals(classInfo, method.ParentClass)) continue;
                foreach (var methodObj in classItem.Items)
                {
                    if (methodObj is TreeViewItem methodItem && ReferenceEquals(methodItem.Tag, method))
                    {
                        _suppressTreeSelectionChanged = true;
                        try
                        {
                            fileItem.IsExpanded = true;
                            classItem.IsExpanded = true;
                            methodItem.IsSelected = true;
                            methodItem.BringIntoView();
                        }
                        finally
                        {
                            _suppressTreeSelectionChanged = false;
                        }
                        ShowMethodDetails(method);
                        return;
                    }
                }
            }
        }

        ShowMethodDetails(method);
    }

    // ── Tree header builders ─────────────────────────────────────────────────

    private static string GetCategoryTag(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "[GUI]",
        CodeCategory.Utility  => "[Util]",
        _                     => "[BL]",
    };

    // Tree headers live across theme switches (the tree is not rebuilt like the detail panel is),
    // so brushes must be resource *references* (SetResourceReference), not one-time FindResource
    // lookups — a held instance goes stale if the theme switch ends up replacing the brush.
    private object CreateFileHeader(string fileName, bool isCpp)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = fileName, VerticalAlignment = VerticalAlignment.Center });
        var badge = new TextBlock
        {
            Text = isCpp ? " [C++]" : " [C#]",
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        panel.Children.Add(badge);
        return panel;
    }

    private object CreateClassHeader(ClassInfo classInfo)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = classInfo.Name, VerticalAlignment = VerticalAlignment.Center });
        var tag = new TextBlock
        {
            Text = GetCategoryTag(classInfo.Category),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var pill = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tag,
        };
        pill.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        panel.Children.Add(pill);
        return panel;
    }

    private object CreateMethodHeader(LensMethod method)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var parameters = string.Join(", ", method.Parameters);
        panel.Children.Add(new TextBlock
        {
            Text = $"{method.Name}({parameters})",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static object CreateMutedHeader(string text) => new TextBlock
    {
        Text = text,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
    };

    private static bool TryGetSingleFolder(DragEventArgs e, out string folderPath)
    {
        folderPath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1) return false;
        if (!Directory.Exists(paths[0])) return false;
        folderPath = paths[0];
        return true;
    }
}
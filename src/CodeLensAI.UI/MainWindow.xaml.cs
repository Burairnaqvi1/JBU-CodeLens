using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeLensAI.Core;
using Microsoft.Win32;

namespace CodeLensAI.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly CSharpParser _cSharpParser = new();
    private readonly Dictionary<string, ParseResult> _parseCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ParseResult> _lastScanResults = [];
    private string? _lastScannedFolder;

    private string? _selectedFolderPath;
    private bool _hasScanResults;
    private int _classCount;
    private int _methodCount;
    private bool _suppressTreeSelectionChanged;

    /// <summary>
    /// The project folder the user selected via drag-and-drop or the Browse dialog.
    /// </summary>
    private string? SelectedFolderPath
    {
        get => _selectedFolderPath;
        set
        {
            _selectedFolderPath = value;
            ScanButton.IsEnabled = !string.IsNullOrEmpty(value);
            if (!_hasScanResults)
            {
                ExportButton.IsEnabled = false;
            }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = TryGetSingleFolder(e, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        if (_hasScanResults)
        {
            return;
        }

        StatusBarText.Text = string.IsNullOrEmpty(SelectedFolderPath)
            ? "Ready"
            : $"Folder selected: {SelectedFolderPath}";
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetSingleFolder(e, out var folderPath))
        {
            SelectedFolderPath = folderPath;
            StatusBarText.Text = $"Folder selected: {folderPath}";
        }

        e.Handled = true;
    }

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
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedFolderPath))
        {
            return;
        }

        StatusBarText.Text = "Scanning...";
        ProjectTree.Items.Clear();
        _parseCache.Clear();
        _lastScanResults = [];
        _lastScannedFolder = SelectedFolderPath;
        ShowPlaceholder();
        _hasScanResults = false;
        ExportButton.IsEnabled = false;

        var sourceFiles = DirectoryScanner.ScanForSourceFiles(SelectedFolderPath);
        _classCount = 0;
        _methodCount = 0;

        if (sourceFiles.Count == 0)
        {
            StatusBarText.Text = "Scan complete — no .cs or .cpp source files found.";
            return;
        }

        var projectName = Path.GetFileName(
            SelectedFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var rootItem = new TreeViewItem
        {
            Header = projectName,
            IsExpanded = true,
        };

        foreach (var filePath in sourceFiles)
        {
            rootItem.Items.Add(BuildFileNode(filePath));
        }

        ProjectTree.Items.Add(rootItem);
        _hasScanResults = true;
        ExportButton.IsEnabled = true;
        StatusBarText.Text =
            $"Scan complete — {sourceFiles.Count} files, {_classCount} classes, {_methodCount} functions";
    }

    private TreeViewItem BuildFileNode(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var isCpp = string.Equals(Path.GetExtension(filePath), ".cpp", StringComparison.OrdinalIgnoreCase);
        var fileItem = new TreeViewItem
        {
            Header = CreateFileHeader(fileName, isCpp),
            Tag = filePath,
            IsExpanded = false,
        };

        if (isCpp)
        {
            _lastScanResults.Add(new ParseResult { FilePath = filePath });
            fileItem.Items.Add(new TreeViewItem
            {
                Header = CreateMutedHeader("C++ parsing not yet implemented — coming in next phase"),
                IsEnabled = false,
            });
            return fileItem;
        }

        var result = _cSharpParser.Parse(filePath);
        _parseCache[filePath] = result;
        _lastScanResults.Add(result);

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
            _classCount++;
            _methodCount += classInfo.Methods.Count;

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
        if (_suppressTreeSelectionChanged)
        {
            return;
        }

        if (e.NewValue is not TreeViewItem item)
        {
            ShowPlaceholder();
            return;
        }

        switch (item.Tag)
        {
            case ClassInfo classInfo:
                ShowClassDetails(classInfo);
                break;
            case MethodInfo methodInfo:
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

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasScanResults || _lastScanResults.Count == 0 || string.IsNullOrEmpty(_lastScannedFolder))
        {
            MessageBox.Show(
                this,
                "Please scan a project first.",
                "Export to Word",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var projectName = Path.GetFileName(
            _lastScannedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var defaultFileName = $"{projectName}_CodeLensAI_Documentation.docx";

        var dialog = new SaveFileDialog
        {
            Title = "Save project documentation",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = defaultFileName,
            DefaultExt = ".docx",
            AddExtension = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var previousStatus = StatusBarText.Text;
        StatusBarText.Text = "Exporting documentation...";
        ExportButton.IsEnabled = false;

        try
        {
            WordExporter.Export(dialog.FileName, _lastScannedFolder, _lastScanResults);

            var openResult = MessageBox.Show(
                this,
                "Documentation exported successfully. Open the file?",
                "Export to Word",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (openResult == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(dialog.FileName)
                {
                    UseShellExecute = true,
                });
            }

            StatusBarText.Text = $"Documentation exported to {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusBarText.Text = previousStatus;
            MessageBox.Show(
                this,
                $"Export failed: {ex.Message}",
                "Export to Word",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private void ShowPlaceholder()
    {
        DetailPlaceholder.Visibility = Visibility.Visible;
        DetailContentHost.Visibility = Visibility.Collapsed;
        DetailPanelRenderer.Clear(DetailContentHost);
    }

    private void ShowDetailContent()
    {
        DetailPlaceholder.Visibility = Visibility.Collapsed;
        DetailContentHost.Visibility = Visibility.Visible;
        DetailPanelRenderer.Clear(DetailContentHost);
    }

    private void ShowFileDetails(string filePath)
    {
        ShowDetailContent();
        _parseCache.TryGetValue(filePath, out var parseResult);
        DetailPanelRenderer.RenderFile(DetailContentHost, filePath, parseResult, this);
    }

    private void ShowClassDetails(ClassInfo classInfo)
    {
        ShowDetailContent();
        DetailPanelRenderer.RenderClass(
            DetailContentHost,
            classInfo,
            this,
            method => SelectMethodInTree(method));
    }

    private void ShowMethodDetails(MethodInfo methodInfo)
    {
        ShowDetailContent();
        DetailPanelRenderer.RenderMethod(DetailContentHost, methodInfo, this);
    }

    private void SelectMethodInTree(MethodInfo method)
    {
        if (ProjectTree.Items.Count == 0)
        {
            ShowMethodDetails(method);
            return;
        }

        var root = ProjectTree.Items[0] as TreeViewItem;
        if (root is null)
        {
            ShowMethodDetails(method);
            return;
        }

        foreach (var fileObj in root.Items)
        {
            if (fileObj is not TreeViewItem fileItem)
            {
                continue;
            }

            foreach (var classObj in fileItem.Items)
            {
                if (classObj is not TreeViewItem classItem || classItem.Tag is not ClassInfo classInfo)
                {
                    continue;
                }

                if (!ReferenceEquals(classInfo, method.ParentClass))
                {
                    continue;
                }

                foreach (var methodObj in classItem.Items)
                {
                    if (methodObj is TreeViewItem methodItem
                        && ReferenceEquals(methodItem.Tag, method))
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

    private static string GetCategoryTag(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "[GUI]",
        CodeCategory.Utility => "[Util]",
        _ => "[BL]",
    };

    private object CreateFileHeader(string fileName, bool isCpp)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = fileName,
            VerticalAlignment = VerticalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = isCpp ? "[C++]" : "[C#]",
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private object CreateClassHeader(ClassInfo classInfo)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = classInfo.Name,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var badge = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(6, 1, 6, 1),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)FindResource("BorderBrush"),
            Child = new TextBlock
            {
                Text = GetCategoryTag(classInfo.Category),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush"),
            },
        };

        panel.Children.Add(badge);
        return panel;
    }

    private object CreateMethodHeader(MethodInfo method)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = "f()",
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var parameters = string.Join(", ", method.Parameters);
        panel.Children.Add(new TextBlock
        {
            Text = $"{method.Name}({parameters})",
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private object CreateMutedHeader(string text) => new TextBlock
    {
        Text = text,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
    };

    private static bool TryGetSingleFolder(DragEventArgs e, out string folderPath)
    {
        folderPath = string.Empty;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length != 1)
        {
            return false;
        }

        if (!Directory.Exists(paths[0]))
        {
            return false;
        }

        folderPath = paths[0];
        return true;
    }
}

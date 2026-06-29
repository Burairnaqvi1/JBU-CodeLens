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
    private string? _selectedFolderPath;

    /// <summary>
    /// The project folder the user selected via drag-and-drop or the Browse dialog.
    /// Used by later parsing phases.
    /// </summary>
    public string? SelectedFolderPath
    {
        get => _selectedFolderPath;
        private set
        {
            _selectedFolderPath = value;
            var hasFolder = !string.IsNullOrEmpty(value);
            SelectedPathText.Text = hasFolder ? value : "No folder selected";
            ScanButton.IsEnabled = hasFolder;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (TryGetSingleFolder(e, out _))
        {
            e.Effects = DragDropEffects.Copy;
            DropBorder.Stroke = (Brush)FindResource("AccentBrush");
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetDropBorder();
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        ResetDropBorder();

        if (TryGetSingleFolder(e, out var folderPath))
        {
            SelectedFolderPath = folderPath;
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
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SelectedFolderPath))
        {
            return;
        }

        ResultsList.Items.Clear();

        var sourceFiles = DirectoryScanner.ScanForSourceFiles(SelectedFolderPath);
        if (sourceFiles.Count == 0)
        {
            ResultsList.Items.Add("No .cs or .cpp source files found.");
            return;
        }

        foreach (var filePath in sourceFiles)
        {
            ResultsList.Items.Add(DescribeFile(filePath));
        }
    }

    /// <summary>
    /// Produces a single display line for a source file: C# files are parsed for their
    /// top-level class names, while C++ files are listed with a not-yet-implemented note.
    /// </summary>
    private string DescribeFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var extension = Path.GetExtension(filePath);

        if (string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase))
        {
            return $"{fileName}: (C++ parsing not yet implemented)";
        }

        var result = _cSharpParser.Parse(filePath);

        if (result.Errors.Count > 0)
        {
            return $"{fileName}: error - {string.Join("; ", result.Errors)}";
        }

        if (result.ClassNames.Count == 0)
        {
            return $"{fileName}: (no top-level classes)";
        }

        return $"{fileName}: {string.Join(", ", result.ClassNames)}";
    }

    /// <summary>
    /// Succeeds only when the dragged payload is exactly one existing directory.
    /// </summary>
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

    private void ResetDropBorder()
    {
        DropBorder.Stroke = (Brush)FindResource("BorderBrush");
    }
}

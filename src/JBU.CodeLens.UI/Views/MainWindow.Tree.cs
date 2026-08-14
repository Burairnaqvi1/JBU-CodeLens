using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JBU.CodeLens.Shared.Models;
using LensMethod = JBU.CodeLens.Shared.Models.MethodInfo;

namespace JBU.CodeLens.UI.Views;

/// <summary>
/// The project explorer tree: the filter that narrows it, and the builders that make each row's
/// header content.
/// </summary>
/// <remarks>
/// Split out of MainWindow.xaml.cs, which had grown past 1900 lines. Filtering and row
/// presentation are one concern and were sitting nine hundred lines apart.
/// </remarks>
public partial class MainWindow
{
    // ── Tree filter ──────────────────────────────────────────────────────────

    /// <summary>
    /// Delays the filter until typing pauses. Filtering rebuilds the whole tree, so on a large
    /// project running it per keystroke made the box lag several characters behind the typist.
    /// </summary>
    private readonly DispatcherTimer _filterDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // The hint and clear button track the text itself, not the filter, so they stay
        // immediate — a 150 ms lag on the placeholder would look like a dropped keystroke.
        var empty = string.IsNullOrEmpty(FilterBox.Text);
        FilterHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        FilterClearButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (empty)
        {
            // Clearing restores the full tree; there is nothing to wait for.
            _filterDebounce.Stop();
            ApplyTreeFilter(string.Empty);
            return;
        }

        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void FilterDebounce_Tick(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        ApplyTreeFilter(FilterBox.Text);
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = string.Empty;
        FilterBox.Focus();
    }

    /// <summary>
    /// Shows only the file nodes whose name — or whose classes/methods — match the query,
    /// expanding files whose match is inside them. Matching consults the parse results rather
    /// than tree items, so lazily built children are only materialized for files that match.
    /// Clearing the filter restores every node (collapsed, matching the freshly scanned state).
    /// </summary>
    private void ApplyTreeFilter(string query)
    {
        if (ProjectTree.Items.Count == 0 || ProjectTree.Items[0] is not TreeViewItem root)
        {
            return;
        }

        var q = query.Trim();
        var filtering = q.Length > 0;
        var matchedFiles = 0;

        foreach (var child in root.Items)
        {
            if (child is not TreeViewItem item)
            {
                continue;
            }

            // Metrics and namespace summary nodes: hidden while filtering (they never match).
            if (item.Tag is not string filePath || !_parseCache.ContainsKey(filePath))
            {
                item.Visibility = filtering ? Visibility.Collapsed : Visibility.Visible;
                continue;
            }

            if (!filtering)
            {
                item.Visibility = Visibility.Visible;
                item.IsExpanded = false;
                SetDescendantsVisible(item);
                continue;
            }

            var fileMatch = Path.GetFileName(filePath).Contains(q, StringComparison.OrdinalIgnoreCase);
            _parseCache.TryGetValue(filePath, out var parse);
            var memberMatch = parse?.Classes.Any(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Methods.Any(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) == true;

            item.Visibility = fileMatch || memberMatch ? Visibility.Visible : Visibility.Collapsed;
            if (fileMatch || memberMatch)
            {
                matchedFiles++;
            }

            if (memberMatch)
            {
                PopulateFileChildren(item);
                FilterFileChildren(item, q);
                item.IsExpanded = true;
            }
            else if (fileMatch)
            {
                SetDescendantsVisible(item);
                item.IsExpanded = false;
            }
        }

        ReportFilterResult(q, filtering, matchedFiles);
    }

    /// <summary>
    /// Says how many files the filter matched, or that it matched none.
    /// </summary>
    /// <remarks>
    /// A query matching nothing used to leave an empty tree and no explanation, which is
    /// indistinguishable from the application having lost the project.
    /// </remarks>
    private void ReportFilterResult(string query, bool filtering, int matchedFiles)
    {
        if (!filtering)
        {
            FilterResultText.Visibility = Visibility.Collapsed;
            return;
        }

        FilterResultText.Visibility = Visibility.Visible;
        FilterResultText.Text = matchedFiles switch
        {
            0 => $"No matches for “{query}”",
            1 => "1 file matches",
            _ => $"{matchedFiles} files match",
        };
    }

    private static void FilterFileChildren(TreeViewItem fileItem, string q)
    {
        foreach (var childObj in fileItem.Items)
        {
            if (childObj is not TreeViewItem classItem || classItem.Tag is not ClassInfo classInfo)
            {
                continue;
            }

            var classMatch = classInfo.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            var anyMethodMatch = false;
            foreach (var methodObj in classItem.Items)
            {
                if (methodObj is not TreeViewItem methodItem || methodItem.Tag is not LensMethod method)
                {
                    continue;
                }

                var methodMatch = method.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
                anyMethodMatch |= methodMatch;
                methodItem.Visibility = methodMatch || classMatch ? Visibility.Visible : Visibility.Collapsed;
            }

            classItem.Visibility = classMatch || anyMethodMatch ? Visibility.Visible : Visibility.Collapsed;
            classItem.IsExpanded = anyMethodMatch;
        }
    }

    private static void SetDescendantsVisible(TreeViewItem item)
    {
        foreach (var childObj in item.Items)
        {
            if (childObj is not TreeViewItem child)
            {
                continue;
            }

            child.Visibility = Visibility.Visible;
            SetDescendantsVisible(child);
        }
    }

    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
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
    private static StackPanel CreateFileHeader(string fileName, bool isCpp)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = fileName, VerticalAlignment = VerticalAlignment.Center });
        var badgeText = new TextBlock
        {
            Text = isCpp ? "C++" : "C#",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "SurfaceBrush");
        var badge = new Border
        {
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(8, 1, 8, 1),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = badgeText,
        };
        badge.SetResourceReference(Border.BackgroundProperty, isCpp ? "PrimaryBrush" : "SecondaryBrush");
        panel.Children.Add(badge);
        return panel;
    }

    private static StackPanel CreateClassHeader(ClassInfo classInfo)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = classInfo.Name, VerticalAlignment = VerticalAlignment.Center });
        panel.ToolTip = string.IsNullOrEmpty(classInfo.NamespaceName)
            ? classInfo.Name
            : $"{classInfo.NamespaceName}.{classInfo.Name}";
        var tag = new TextBlock
        {
            Text = GetCategoryTag(classInfo.Category),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
        };
        tag.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
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

    private StackPanel CreateMethodHeader(LensMethod method)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var parameters = string.Join(", ", method.Parameters);
        panel.Children.Add(new TextBlock
        {
            Text = $"{method.Name}({parameters})",
            FontFamily = (FontFamily)FindResource("CodeFont"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.ToolTip = $"{method.ReturnType} {method.Name}({parameters})";
        return panel;
    }

    private static TextBlock CreateMutedHeader(string text) => new TextBlock
    {
        Text = text,
        Opacity = 0.55,
        TextWrapping = TextWrapping.Wrap,
    };
}

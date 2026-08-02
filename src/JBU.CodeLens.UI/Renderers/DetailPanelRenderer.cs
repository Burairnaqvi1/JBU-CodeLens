using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

using MethodDetailContext = JBU.CodeLens.Shared.Structural.MethodDetailContext;

namespace JBU.CodeLens.UI.Renderers;

/// <summary>Which metric category a dashboard tile drills into.</summary>
public enum MetricCategory { Classes, Methods, Properties, Fields, Namespaces, Relationships }

/// <summary>
/// One row in a metric drill-down list: a primary label, an optional detail string, and an
/// optional navigation action (null = an informational row that isn't clickable).
/// </summary>
public sealed record DrillDownItem(string Primary, string? Secondary, Action? OnClick);

internal static class DetailPanelRenderer
{
    public static void Clear(StackPanel host) => host.Children.Clear();

    // ── File ─────────────────────────────────────────────────────────────────

    public static void RenderFile(StackPanel host, string filePath, ParseResult? parseResult, FrameworkElement resourceRoot)
    {
        Clear(host);
        var isCpp = LanguageFileExtensions.IsCppFile(filePath);
        var fileName = System.IO.Path.GetFileName(filePath);

        host.Children.Add(CreateAccentTitle(fileName, resourceRoot));
        host.Children.Add(CreateMutedText(filePath, resourceRoot, marginTop: 6));
        host.Children.Add(CreateLanguageBadge(isCpp ? "[C++]" : "[C#]", resourceRoot, marginTop: 10));
        host.Children.Add(CreateFileActions(filePath, resourceRoot));

        if (parseResult is null || parseResult.Errors.Count > 0)
        {
            AddSection(host, "Overview", resourceRoot);
            host.Children.Add(CreateBodyText($"Parse error: {parseResult?.Errors.FirstOrDefault() ?? "Unable to parse."}", resourceRoot));
            return;
        }

        if (parseResult.Classes.Count == 0)
        {
            AddSection(host, "Overview", resourceRoot);
            host.Children.Add(CreateBodyText(
                isCpp
                    ? "No classes found in this C++ file. The file may be empty or contain only preprocessor directives."
                    : "No classes found in this file.",
                resourceRoot));
            return;
        }

        AddSection(host, "Overview", resourceRoot);
        host.Children.Add(CreateBodyText(
            parseResult.Classes.Count == 1 ? "1 class found in this file." : $"{parseResult.Classes.Count} classes found in this file.",
            resourceRoot));

        if (parseResult.Classes.Count > 0)
        {
            AddSection(host, "Classes", resourceRoot);
            foreach (var classInfo in parseResult.Classes)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
                var pill = CreateCategoryPill(DescribeCategoryLabel(classInfo.Category), resourceRoot, marginLeft: 8);
                DockPanel.SetDock(pill, Dock.Right);
                row.Children.Add(pill);
                row.Children.Add(CreateBodyText(classInfo.Name, resourceRoot, fontWeight: FontWeights.SemiBold));
                host.Children.Add(row);
            }
        }
    }

    // ── Class ─────────────────────────────────────────────────────────────────

    public static void RenderClass(
        StackPanel host,
        ClassInfo classInfo,
        FrameworkElement resourceRoot,
        Action<MethodInfo>? onMethodClicked,
        IExplanationService? explanationService = null)
    {
        Clear(host);

        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        var classCategoryPill = CreateCategoryPill(DescribeCategoryLabel(classInfo.Category), resourceRoot, marginLeft: 12);
        DockPanel.SetDock(classCategoryPill, Dock.Right);
        headerRow.Children.Add(classCategoryPill);
        headerRow.Children.Add(CreateAccentTitle(classInfo.Name, resourceRoot));
        host.Children.Add(headerRow);

        if (!string.IsNullOrEmpty(classInfo.SourceFilePath))
            host.Children.Add(CreateMutedText(classInfo.SourceFilePath, resourceRoot, marginTop: 4));

        AddSection(host, "What This Class Does", resourceRoot);
        AddClassDescription(host, classInfo, resourceRoot, explanationService);

        AddSection(host, "Inheritance & Relationships", resourceRoot);
        host.Children.Add(CreateLabeledRow("Extends",
            string.IsNullOrEmpty(classInfo.BaseClassName) ? "No base class — this is a root class" : classInfo.BaseClassName,
            resourceRoot));
        host.Children.Add(CreateLabeledRow("Implements",
            classInfo.ImplementedInterfaces.Count > 0 ? string.Join(", ", classInfo.ImplementedInterfaces) : "None",
            resourceRoot, marginTop: 6));

        var dependsRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        var dependsLabel = CreateBodyText("Depends on: ", resourceRoot, fontWeight: FontWeights.SemiBold);
        DockPanel.SetDock(dependsLabel, Dock.Left);
        dependsRow.Children.Add(dependsLabel);
        if (classInfo.Dependencies.Count == 0)
        {
            dependsRow.Children.Add(CreateMutedText("None", resourceRoot));
        }
        else
        {
            var chips = new WrapPanel();
            foreach (var dep in classInfo.Dependencies)
                chips.Children.Add(CreateChip(dep, resourceRoot));
            dependsRow.Children.Add(chips);
        }
        host.Children.Add(dependsRow);

        AddSection(host, "Members Summary", resourceRoot);
        var summaryGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mc = new TextBlock { Text = $"{classInfo.Methods.Count} Method{(classInfo.Methods.Count == 1 ? "" : "s")}", FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") };
        var pc = new TextBlock { Text = $"{classInfo.Properties.Count} Propert{(classInfo.Properties.Count == 1 ? "y" : "ies")}", FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") };
        Grid.SetColumn(pc, 1);
        summaryGrid.Children.Add(mc);
        summaryGrid.Children.Add(pc);
        host.Children.Add(summaryGrid);

        if (classInfo.Methods.Count > 0)
        {
            AddSection(host, "Methods", resourceRoot);
            foreach (var method in classInfo.Methods)
                host.Children.Add(CreateMethodRow(method, resourceRoot, () => onMethodClicked?.Invoke(method)));
        }

        if (classInfo.Properties.Count > 0)
        {
            AddSection(host, "Properties", resourceRoot);
            foreach (var property in classInfo.Properties)
                host.Children.Add(CreatePropertyRow(property, resourceRoot));
        }
    }

    /// <summary>
    /// The class description ladder, mirroring the method brief-description card: a developer
    /// XML summary always wins; otherwise the deterministic inferred description shows
    /// immediately and an AI summary is generated lazily (once per class, session-cached) and
    /// added alongside it. The "add a /// summary" advice stays, but as a muted hint under
    /// whichever description is shown — never as the only content.
    /// </summary>
    private static void AddClassDescription(
        StackPanel host,
        ClassInfo classInfo,
        FrameworkElement resourceRoot,
        IExplanationService? explanationService)
    {
        if (!string.IsNullOrWhiteSpace(classInfo.XmlSummary))
        {
            host.Children.Add(CreateCapsLabel("DEVELOPER DESCRIPTION", resourceRoot));
            host.Children.Add(CreateBodyText(classInfo.XmlSummary, resourceRoot, marginTop: 6));
            return;
        }

        if (!string.IsNullOrWhiteSpace(classInfo.InferredDescription))
        {
            host.Children.Add(CreateCapsLabel("INFERRED DESCRIPTION", resourceRoot));
            host.Children.Add(CreateBodyText(classInfo.InferredDescription, resourceRoot, marginTop: 6));
        }

        var aiLabelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        aiLabelRow.Children.Add(CreateCapsLabel("AI DESCRIPTION", resourceRoot));
        aiLabelRow.Children.Add(CreateBadge("AI", "WarningBrush", resourceRoot, marginLeft: 6));
        host.Children.Add(aiLabelRow);

        if (!string.IsNullOrEmpty(classInfo.CachedAiSummary))
        {
            host.Children.Add(CreateBodyText(classInfo.CachedAiSummary, resourceRoot, marginTop: 6));
        }
        else if (explanationService is { IsReady: true })
        {
            var aiText = CreateBodyText("Generating AI description…", resourceRoot, marginTop: 6);
            host.Children.Add(aiText);

            var svc = explanationService;
            var c = classInfo;
            Task.Run(() =>
            {
                var text = svc.GenerateClassSummary(c, partial =>
                    Application.Current.Dispatcher.BeginInvoke(() => aiText.Text = partial));
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    aiText.Text = text;

                    // Only cache real model output — bracketed strings are error/unavailable
                    // messages, and caching those would hide the AI once it becomes ready.
                    if (!text.StartsWith('['))
                    {
                        c.CachedAiSummary = text;
                    }
                });
            });
        }
        else
        {
            host.Children.Add(CreateItalicPlaceholder(GetAiUnavailableMessage(explanationService), resourceRoot, marginTop: 6));
        }

        host.Children.Add(CreateMutedText(
            "Tip: add a /// <summary> comment above the class to provide a developer description.",
            resourceRoot,
            marginTop: 10));
    }

    // ── Metrics dashboard ─────────────────────────────────────────────────────

    /// <summary>
    /// Renders the computed project metrics as a scannable dashboard: stat tiles grouped into
    /// size and quality, a maintainability tile colored by band, and a "most complex methods"
    /// bar list that points straight at refactoring candidates. All values are already computed
    /// during the scan — this is presentation only.
    /// </summary>
    public static void RenderMetricsDashboard(
        StackPanel host,
        JBU.CodeLens.Shared.Structural.MetricsResult metrics,
        JBU.CodeLens.Shared.Structural.ProjectIR? ir,
        FrameworkElement resourceRoot,
        Action<MetricCategory>? onCategoryClick = null)
    {
        // The size tiles drill into the actual items they count; a null handler leaves them
        // as plain stat cards.
        Action? Drill(MetricCategory category) =>
            onCategoryClick is null ? null : () => onCategoryClick(category);

        AddSection(host, "Size", resourceRoot);
        var sizeTiles = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        sizeTiles.Children.Add(CreateStatTile("Classes", metrics.TotalClasses.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Classes)));
        sizeTiles.Children.Add(CreateStatTile("Methods", metrics.TotalMethods.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Methods)));
        sizeTiles.Children.Add(CreateStatTile("Properties", metrics.TotalProperties.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Properties)));
        sizeTiles.Children.Add(CreateStatTile("Fields", metrics.TotalFields.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Fields)));
        sizeTiles.Children.Add(CreateStatTile("Namespaces", metrics.TotalNamespaces.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Namespaces)));
        sizeTiles.Children.Add(CreateStatTile("Relationships", metrics.TotalRelationships.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot, Drill(MetricCategory.Relationships)));
        host.Children.Add(sizeTiles);

        AddSection(host, "Quality", resourceRoot);
        var qualityTiles = new WrapPanel { Margin = new Thickness(0, 2, 0, 4) };
        // Maintainability index: green ≥ 85, amber 65–84, red below (standard MI bands).
        var miBrushKey = metrics.MaintainabilityIndex switch
        {
            >= 85 => "SecondaryBrush",
            >= 65 => "WarningBrush",
            _ => "ErrorBrush",
        };
        qualityTiles.Children.Add(CreateStatTile(
            "Maintainability", metrics.MaintainabilityIndex.ToString("F0", CultureInfo.InvariantCulture), miBrushKey, resourceRoot));
        qualityTiles.Children.Add(CreateStatTile(
            "Avg complexity", metrics.AverageComplexity.ToString("F1", CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot));
        qualityTiles.Children.Add(CreateStatTile(
            "Max complexity", metrics.MaxComplexity.ToString(CultureInfo.InvariantCulture), metrics.MaxComplexity >= 15 ? "WarningBrush" : "PrimaryBrush", resourceRoot));
        qualityTiles.Children.Add(CreateStatTile(
            "Avg coupling", metrics.AverageCoupling.ToString("F1", CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot));
        qualityTiles.Children.Add(CreateStatTile(
            "Max inheritance", metrics.MaxInheritanceDepth.ToString(CultureInfo.InvariantCulture), "PrimaryBrush", resourceRoot));
        host.Children.Add(qualityTiles);

        var topMethods = (ir?.Methods ?? [])
            .OrderByDescending(m => m.CyclomaticComplexity)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (topMethods.Count > 0 && topMethods[0].CyclomaticComplexity > 1)
        {
            AddSection(host, "Most complex methods", resourceRoot);
            var listStack = new StackPanel();
            var max = topMethods[0].CyclomaticComplexity;
            foreach (var method in topMethods)
            {
                listStack.Children.Add(CreateComplexityRow(method, max, resourceRoot));
            }

            host.Children.Add(WrapInCard(listStack, resourceRoot));
        }
    }

    private static FrameworkElement CreateStatTile(
        string label, string value, string valueBrushKey, FrameworkElement resourceRoot, Action? onClick = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(resourceRoot, valueBrushKey),
        });

        // Clickable tiles get a small "view" affordance next to the label so it's clear they
        // drill in, not just a hover flourish.
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        labelRow.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (onClick is not null)
        {
            labelRow.Children.Add(new TextBlock
            {
                Text = "",
                FontFamily = (FontFamily)resourceRoot.FindResource("IconFont"),
                FontSize = 8,
                Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 1, 0, 0),
                Opacity = 0.8,
            });
        }

        stack.Children.Add(labelRow);

        var tile = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 10, 10),
            MinWidth = 116,
            Child = stack,
        };

        AttachHoverLift(tile, resourceRoot);

        if (onClick is null)
        {
            return tile;
        }

        // Clickable tiles are wrapped in a chromeless Button so they are also keyboard-operable
        // (Enter/Space) and exposed to screen readers / UI Automation as invokable — the Border
        // still carries all the visuals and the hover lift.
        tile.Margin = new Thickness(0);
        var button = WrapClickable(tile, onClick, $"{label}: {value}. Show list.");
        button.Margin = new Thickness(0, 0, 10, 10);
        return button;
    }

    // ── Metric drill-down list ────────────────────────────────────────────────

    /// <summary>
    /// A left-aligned "Back" button. Shared by the drill-down list and by class/method detail
    /// views that were reached from a drill-down, so the back trail continues instead of dead-ending.
    /// </summary>
    public static Button CreateBackButton(Action onBack, FrameworkElement resourceRoot)
    {
        var backButton = new Button
        {
            Content = (char)0x2190 + "  Back",
            Padding = new Thickness(12, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(resourceRoot, "SurfaceBrush"),
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14),
        };
        backButton.Click += (_, _) => onBack();
        return backButton;
    }

    /// <summary>
    /// Renders the list behind a metric tile (the classes, methods, etc. it counts): a Back
    /// button to the dashboard, a counted title, and rows that navigate when clicked. Rows with
    /// no action (for example a relationship breakdown) render as plain informational lines.
    /// </summary>
    public static void RenderDrillDown(
        StackPanel host,
        string title,
        IReadOnlyList<DrillDownItem> items,
        Action onBack,
        FrameworkElement resourceRoot)
    {
        host.Children.Add(CreateBackButton(onBack, resourceRoot));

        host.Children.Add(new TextBlock
        {
            Text = $"{title} ({items.Count})",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (items.Count == 0)
        {
            host.Children.Add(CreateItalicPlaceholder("Nothing to show here.", resourceRoot));
            return;
        }

        var listStack = new StackPanel();
        foreach (var item in items)
        {
            listStack.Children.Add(CreateDrillRow(item, resourceRoot));
        }

        host.Children.Add(WrapInCard(listStack, resourceRoot));
    }

    private static FrameworkElement CreateDrillRow(DrillDownItem item, FrameworkElement resourceRoot)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var primary = new TextBlock
        {
            Text = item.Primary,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(primary, 0);
        grid.Children.Add(primary);

        if (!string.IsNullOrEmpty(item.Secondary))
        {
            var secondary = new TextBlock
            {
                Text = item.Secondary,
                Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(secondary, 1);
            grid.Children.Add(secondary);
        }

        var row = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6),
            Background = System.Windows.Media.Brushes.Transparent,
            Child = grid,
        };

                if (item.OnClick is null)
        {
            return row;
        }

        var chevron = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)resourceRoot.FindResource("IconFont"),
            FontSize = 10,
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var hover = Brush(resourceRoot, "HoverOverlayBrush");
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;

        return WrapClickable(
            row, item.OnClick,
            string.IsNullOrEmpty(item.Secondary) ? item.Primary : $"{item.Primary}, {item.Secondary}");
    }

    /// <summary>
    /// Gives a card a subtle lift on hover — it rises a few pixels, gains a soft shadow, and its
    /// border picks up the accent — so the dashboard feels responsive without implying the tile
    /// is a button. Animations are short (≈140 ms) and eased.
    /// </summary>
    private static void AttachHoverLift(Border card, FrameworkElement resourceRoot)
    {
        var lift = new TranslateTransform(0, 0);
        card.RenderTransform = lift;
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0,
            // Not a theme colour. A drop shadow is an absence of light rather than a hue, so it
            // is black in both themes; the strength is carried by Opacity below, which is what
            // the animation varies. A themed colour here would also be misleading, because
            // DropShadowEffect ignores the alpha channel of the colour it is given.
            Color = Colors.Black,
        };
        card.Effect = shadow;

        var ease = new System.Windows.Media.Animation.CubicEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
        };
        var restBorder = Brush(resourceRoot, "BorderBrush");
        var accentBorder = FindAccentBrush(resourceRoot);

        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = accentBorder;
            lift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(-4, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
            shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.22, TimeSpan.FromMilliseconds(140)));
        };
        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = restBorder;
            lift.BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        };
    }

    private static Grid CreateComplexityRow(
        JBU.CodeLens.Shared.Structural.MethodInfo method, int maxComplexity, FrameworkElement resourceRoot)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var declaringType = method.DeclaringType.Contains('.', StringComparison.Ordinal)
            ? method.DeclaringType[(method.DeclaringType.LastIndexOf('.') + 1)..]
            : method.DeclaringType;
        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(declaringType) ? method.Name : $"{declaringType}.{method.Name}",
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // Proportional bar over a faint track.
        var fraction = maxComplexity > 0 ? (double)method.CyclomaticComplexity / maxComplexity : 0;
        var track = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var fill = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = FindAccentBrush(resourceRoot),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(4, 140 * fraction),
        };
        track.Child = fill;
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var count = new TextBlock
        {
            Text = method.CyclomaticComplexity.ToString(CultureInfo.InvariantCulture),
            FontWeight = FontWeights.Bold,
            Foreground = Brush(resourceRoot, method.CyclomaticComplexity >= 15 ? "WarningBrush" : "TextPrimaryBrush"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 24,
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        return grid;
    }

    /// <summary>The accent gradient when defined, otherwise the flat primary brush.</summary>
    private static Brush FindAccentBrush(FrameworkElement resourceRoot) =>
        resourceRoot.TryFindResource("AccentGradientBrush") as Brush ?? Brush(resourceRoot, "PrimaryBrush");

    // ── Method ────────────────────────────────────────────────────────────────

    public static void RenderMethod(
        StackPanel host,
        MethodDetailContext context,
        FrameworkElement resourceRoot,
        IExplanationService? explanationService)
    {
        Clear(host);

        var method = context.Method;
        var organicAnalysis = context.Analysis;
        var parentClass = method.ParentClass;

        // Header
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = true };
        if (parentClass is not null)
        {
            var methodCategoryPill = CreateCategoryPill(DescribeCategoryLabel(parentClass.Category), resourceRoot, marginLeft: 8);
            DockPanel.SetDock(methodCategoryPill, Dock.Right);
            headerRow.Children.Add(methodCategoryPill);
        }
        var accessPill = CreateAccessPill(method.AccessModifier, resourceRoot, marginLeft: 12);
        DockPanel.SetDock(accessPill, Dock.Right);
        headerRow.Children.Add(accessPill);
        var languageBadge = GetMethodLanguageBadge(method);
        if (languageBadge is not null)
        {
            var languagePill = CreateSubtleLanguagePill(languageBadge, resourceRoot, marginLeft: 8);
            DockPanel.SetDock(languagePill, Dock.Right);
            headerRow.Children.Add(languagePill);
        }
        headerRow.Children.Add(CreateAccentTitle(method.Name, resourceRoot, fontSize: 22));
        host.Children.Add(headerRow);

        if (parentClass is not null)
            host.Children.Add(CreateMutedText($"in {parentClass.Name}", resourceRoot, marginTop: 3));

        host.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 16, 0, 16) });

        TextBlock? aiBriefText = null;

        // Row 1: Inputs/Outputs + Brief Description
        var row1 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var inputsCard = BuildInputsOutputsCard(context, resourceRoot);
        var briefCard = BuildBriefDescriptionCard(context, resourceRoot, explanationService, out aiBriefText);
        Grid.SetColumn(inputsCard, 0);
        Grid.SetColumn(briefCard, 2);
        row1.Children.Add(inputsCard);
        row1.Children.Add(briefCard);
        host.Children.Add(row1);

        // Row 2: Variables + Pre&Post Conditions + Design Constraints
        var prePostOrganicHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var prePostAiHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var designOrganicHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var designAiHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        PopulatePrePostConditionsCard(prePostOrganicHost, organicAnalysis, resourceRoot);
        PopulateScideStructuralSection(designOrganicHost, context, resourceRoot);
        PopulateExecutionStepsSection(designOrganicHost, organicAnalysis.ExecutionSteps, resourceRoot);
        PopulateInferenceDesignSection(designOrganicHost, organicAnalysis, resourceRoot);
        prePostAiHost.Children.Add(CreateItalicPlaceholder("Click Generate Analysis to add an AI review of the pre & post conditions.", resourceRoot));
        designAiHost.Children.Add(CreateItalicPlaceholder("Click Generate Analysis to add an AI review of the design requirements.", resourceRoot));

        var prePostHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        prePostHost.Children.Add(prePostOrganicHost);
        prePostHost.Children.Add(prePostAiHost);

        var designHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        designHost.Children.Add(designOrganicHost);
        designHost.Children.Add(designAiHost);

        var row2 = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var variablesCard = BuildVariablesCard(context, resourceRoot);
        var conditionsCard = BuildConditionsCard(prePostHost, resourceRoot);
        var designCard = BuildDesignConstraintsCard(designHost, resourceRoot);
        Grid.SetColumn(variablesCard, 0);
        Grid.SetColumn(conditionsCard, 2);
        Grid.SetColumn(designCard, 4);
        row2.Children.Add(variablesCard);
        row2.Children.Add(conditionsCard);
        row2.Children.Add(designCard);
        host.Children.Add(row2);

        // Full width rather than a fourth column: each row names a variable, its permitted range
        // and the line of code that range was read from, and that does not fit a third of a row.
        host.Children.Add(BuildVariableLimitsCard(context, resourceRoot));

        // Generate Analysis button
        var analysisRow = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) };
        var generateAnalysisBtn = CreateRegenerateButton("Generate Analysis", () => { }, resourceRoot);
        analysisRow.Children.Add(generateAnalysisBtn);
        host.Children.Add(analysisRow);

        generateAnalysisBtn.Click += (_, _) =>
        {
            if (explanationService is null || !explanationService.IsReady)
            {
                var unavailable = GetAiUnavailableMessage(explanationService);
                ShowAnalysisPlaceholder(prePostAiHost, unavailable);
                ShowAnalysisPlaceholder(designAiHost, unavailable);
                return;
            }

            // Once the button reads "Regenerate", pressing it is a request for a different
            // answer — so the cached one has to go, or the service replays it verbatim.
            if (generateAnalysisBtn.Content is string label && label.StartsWith("Regenerate", StringComparison.Ordinal))
            {
                explanationService.Forget(method);
            }

            generateAnalysisBtn.IsEnabled = false;
            generateAnalysisBtn.Content = "Generating…";
            ShowAnalysisPlaceholder(prePostAiHost, "Generating pre & post conditions…");
            ShowAnalysisPlaceholder(designAiHost, "Generating design requirements…");

            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                // Two sequential model calls on one worker — the service serializes inference
                // anyway, so each card is filled the moment its own result lands rather than
                // both sitting on a placeholder until the slower one finishes.
                var prePost = svc.GeneratePrePostConditions(m);
                Application.Current.Dispatcher.BeginInvoke(() => ShowPrePostAiResult(prePost));

                var design = svc.GenerateDesignConstraints(m);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    designAiHost.Children.Clear();
                    PopulateBulletList(
                        CreateAiEnhancementBlock(designAiHost, "AI DESIGN REQUIREMENTS", resourceRoot),
                        design,
                        resourceRoot);
                    generateAnalysisBtn.Content = "Regenerate Analysis";
                    generateAnalysisBtn.IsEnabled = true;
                });
            });
        };

        void ShowAnalysisPlaceholder(StackPanel aiHost, string message)
        {
            aiHost.Children.Clear();
            aiHost.Children.Add(CreateItalicPlaceholder(message, resourceRoot));
        }

        // Mirrors the deterministic PRECONDITIONS/POSTCONDITIONS labels above so a reader never
        // has to work out which group an AI bullet belongs to. When the model ignored the marker
        // format there is no trustworthy way to tell the two apart, so the bullets stay under one
        // neutral label rather than being guessed into the wrong group.
        void ShowPrePostAiResult(string prePost)
        {
            prePostAiHost.Children.Clear();

            var groups = PrePostConditionText.Split(prePost);
            if (!groups.IsGrouped)
            {
                PopulateBulletList(
                    CreateAiEnhancementBlock(prePostAiHost, "AI ENHANCEMENT", resourceRoot),
                    prePost,
                    resourceRoot);
                return;
            }

            var host = CreateAiEnhancementBlock(prePostAiHost, "AI PRECONDITIONS", resourceRoot);
            PopulateGroupBullets(host, groups.Preconditions);

            prePostAiHost.Children.Add(CreateCapsLabel("AI POSTCONDITIONS", resourceRoot, marginTop: 12));
            var postHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            prePostAiHost.Children.Add(postHost);
            PopulateGroupBullets(postHost, groups.Postconditions);

            if (groups.Ungrouped.Count > 0)
            {
                prePostAiHost.Children.Add(CreateCapsLabel("AI ENHANCEMENT", resourceRoot, marginTop: 12));
                var extraHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
                prePostAiHost.Children.Add(extraHost);
                PopulateGroupBullets(extraHost, groups.Ungrouped);
            }
        }

        void PopulateGroupBullets(StackPanel host, IReadOnlyList<string> bullets)
        {
            if (bullets.Count == 0)
            {
                host.Children.Add(CreateItalicPlaceholder("None identified.", resourceRoot));
                return;
            }

            foreach (var bullet in bullets)
            {
                host.Children.Add(CreateBulletItem(bullet, resourceRoot));
            }
        }

        // Errors / Exceptions
        host.Children.Add(BuildErrorsCard(context, resourceRoot, explanationService));

        host.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 16, 0, 16) });

        // How This Fits In
        host.Children.Add(BuildHowThisFitsInCard(context, resourceRoot));

        host.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 16, 0, 16) });

        // AI Q&A
        host.Children.Add(BuildAiExplanationCard(method, resourceRoot, explanationService, method.XmlSummary, aiBriefText));
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    private static Border BuildInputsOutputsCard(MethodDetailContext context, FrameworkElement resourceRoot)
    {
        var method = context.Method;
        var stack = new StackPanel();
        AddCardHeader(stack, "Inputs / Outputs", resourceRoot);

        if (context.ScideModifiers.Count > 0)
        {
            stack.Children.Add(CreateMutedText(
                $"Modifiers: {string.Join(" ", context.ScideModifiers)}",
                resourceRoot,
                marginTop: 4));
        }

        stack.Children.Add(CreateCapsLabel("INPUT PARAMETERS", resourceRoot));
        if (method.Parameters.Count == 0)
        {
            stack.Children.Add(CreateMutedText("No parameters.", resourceRoot, marginTop: 6));
        }
        else
        {
            var scideParams = context.ScideMethod?.Parameters ?? [];
            for (var i = 0; i < method.Parameters.Count; i++)
            {
                var param = method.Parameters[i];
                var (type, name) = SplitParameter(param);
                if (i < scideParams.Count && !string.IsNullOrWhiteSpace(scideParams[i].TypeName))
                    type = scideParams[i].TypeName;

                var paramText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 6, 0, 0) };
                paramText.Inlines.Add(new Run(type)
                {
                    FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush(resourceRoot, "PrimaryBrush"),
                });
                paramText.Inlines.Add(new Run($"  {name}") { Foreground = Brush(resourceRoot, "TextPrimaryBrush") });
                stack.Children.Add(paramText);

                // The permitted range belongs beside the parameter it constrains — that is where
                // the reader is already looking when asking "what may I pass in here?". The card
                // lower down repeats it with the originating code for anyone who wants to check.
                var limit = context.Analysis.VariableLimits
                    .FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.Ordinal));
                if (limit is not null)
                {
                    stack.Children.Add(CreateInlineLimit(limit, resourceRoot));
                }

                if (method.XmlDocTags.TryGetValue($"param:{name}", out var paramDoc))
                    stack.Children.Add(CreateBodyText(paramDoc, resourceRoot, marginTop: 4, marginLeft: 12));
            }
        }

        stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 12, 0, 10) });
        stack.Children.Add(CreateCapsLabel("RETURN VALUE", resourceRoot));

        var returnType = method.ReturnType;
        if (context.ScideMethod is not null && !string.IsNullOrWhiteSpace(context.ScideMethod.ReturnType))
            returnType = context.ScideMethod.ReturnType;

        if (string.Equals(returnType, "void", StringComparison.OrdinalIgnoreCase))
        {
            stack.Children.Add(CreateMutedText("void — no value returned", resourceRoot, marginTop: 6));
        }
        else
        {
            stack.Children.Add(new Border
            {
                Background = Brush(resourceRoot, "PrimaryBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = returnType,
                    FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
                    FontWeight = FontWeights.Bold,
                    Foreground = Brush(resourceRoot, "SurfaceBrush"),
                    FontSize = 13,
                },
            });

            if (method.XmlDocTags.TryGetValue("returns", out var returnsDoc))
                stack.Children.Add(CreateBodyText(returnsDoc, resourceRoot, marginTop: 8));
        }

        return WrapInCard(stack, resourceRoot);
    }

    private static Border BuildBriefDescriptionCard(
        MethodDetailContext context,
        FrameworkElement resourceRoot,
        IExplanationService? explanationService,
        out TextBlock? aiBriefText)
    {
        var method = context.Method;
        aiBriefText = null;
        var stack = new StackPanel();
        AddCardHeader(stack, "Brief Description", resourceRoot);

        var xmlSummary = context.MergedXmlSummary;
        if (!string.IsNullOrWhiteSpace(xmlSummary))
        {
            stack.Children.Add(CreateCapsLabel("DEVELOPER DESCRIPTION", resourceRoot));
            stack.Children.Add(CreateBodyText(xmlSummary, resourceRoot, marginTop: 8));
        }
        else
        {
            stack.Children.Add(CreateItalicPlaceholder("No developer description provided.", resourceRoot));

            var inferred = context.InferredDescription;
            if (!string.IsNullOrWhiteSpace(inferred))
            {
                stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 10, 0, 10) });
                stack.Children.Add(CreateCapsLabel("INFERRED DESCRIPTION", resourceRoot));
                stack.Children.Add(CreateBodyText(inferred, resourceRoot, marginTop: 8));
            }
        }

        // The AI description always runs — even when a developer XML summary exists — so the
        // model's independent read of the method is shown alongside the documentation. This lets
        // the reader cross-check the two and improves overall accuracy/confidence.
        stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 10, 0, 10) });
        var aiLabelRow = new StackPanel { Orientation = Orientation.Horizontal };
        aiLabelRow.Children.Add(CreateCapsLabel("AI DESCRIPTION", resourceRoot));
        aiLabelRow.Children.Add(CreateBadge("AI", "WarningBrush", resourceRoot, marginLeft: 6));
        stack.Children.Add(aiLabelRow);

        aiBriefText = CreateBodyText(
            method.CachedAiBriefDescription ?? "Generating AI description…", resourceRoot, marginTop: 8);
        stack.Children.Add(aiBriefText);

        var briefTextBlock = aiBriefText;
        var svc = explanationService;
        var m = method;

        Button? regenerate = null;

        void Generate()
        {
            if (regenerate is not null) regenerate.IsEnabled = false;
            briefTextBlock.Text = "Generating AI description…";

            Task.Run(() =>
            {
                // The partial callback streams the model's words onto the panel as they are
                // produced. Both the partials and the returned text are shaped by the same
                // function inside the service, so the last partial already equals the final
                // assignment below — the line settles instead of visibly snapping shorter.
                var text = svc is { IsReady: true }
                    ? svc.GenerateBriefDescription(m, partial =>
                        Application.Current.Dispatcher.BeginInvoke(() => briefTextBlock.Text = partial))
                    : GetAiUnavailableMessage(svc);

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    briefTextBlock.Text = text;
                    if (regenerate is not null) regenerate.IsEnabled = true;

                    // Only cache real model output — bracketed strings are error/unavailable
                    // messages, and caching those would hide the AI once it becomes ready.
                    if (svc is { IsReady: true } && !text.StartsWith('['))
                    {
                        m.CachedAiBriefDescription = text;
                    }
                });
            });
        }

        // The description is cached for the session, so without this the only way to get a second
        // opinion on a wrong or unhelpful line was to restart the application.
        regenerate = CreateSmallAction("Regenerate", "Ask the AI for a fresh description", resourceRoot, () =>
        {
            m.CachedAiBriefDescription = null;
            // The panel's own copy is only half of it — the service caches per (method, file
            // timestamp) too, and would return the same text without calling the model.
            svc?.Forget(m);
            Generate();
        });
        regenerate.Margin = new Thickness(8, 0, 0, 0);
        aiLabelRow.Children.Add(regenerate);

        if (string.IsNullOrEmpty(method.CachedAiBriefDescription))
        {
            Generate();
        }

        return WrapInCard(stack, resourceRoot);
    }

    /// <summary>
    /// The one-line form of a variable's operation limit, shown beneath the parameter it applies
    /// to. Carries no evidence column — the reader wanting to check the claim has the full table
    /// further down; here the answer itself is what matters.
    /// </summary>
    private static TextBlock CreateInlineLimit(VariableLimit limit, FrameworkElement resourceRoot)
    {
        var line = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(12, 3, 0, 0),
        };

        line.Inlines.Add(new Run("limit: ")
        {
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            FontWeight = FontWeights.SemiBold,
        });
        line.Inlines.Add(new Run(limit.Limit)
        {
            Foreground = Brush(resourceRoot, LimitBrushKey(limit.Confidence)),
            FontWeight = FontWeights.SemiBold,
        });

        line.ToolTip = $"{limit.Evidence}\n\n{DescribeLimitSource(limit)}";
        return line;
    }

    /// <summary>
    /// Lists the range of values each variable may hold, with the code the range was read from.
    /// </summary>
    /// <remarks>
    /// The evidence column is the point of this card. A stated range the reader cannot check is
    /// worse than no range at all, because they have no way to tell a certainty from a guess —
    /// so the confidence is named and the originating line is quoted beside every row.
    /// </remarks>
    private static Border BuildVariableLimitsCard(MethodDetailContext context, FrameworkElement resourceRoot)
    {
        var stack = new StackPanel();
        AddCardHeader(stack, "Variable Operation Limits", resourceRoot);

        var limits = context.Analysis.VariableLimits;
        if (limits.Count > 0)
        {
            // The table is the part of this panel most likely to end up in a report.
            var copyRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            copyRow.Children.Add(CreateCopyButton(
                "Copy the limits table",
                () => string.Join(
                    Environment.NewLine,
                    limits.Select(l => $"{l.Name}\t{l.Limit}\t{l.Evidence}")),
                resourceRoot));
            stack.Children.Add(copyRow);
        }

        if (limits.Count == 0)
        {
            stack.Children.Add(CreateMutedText(
                "No value limits could be read from this method. Limits are found where the code " +
                "checks a value, forces it into a range, or counts through a fixed loop.",
                resourceRoot,
                marginTop: 4));
            return WrapInCard(stack, resourceRoot);
        }

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // name
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // range
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // evidence

        AddLimitHeaderRow(grid, resourceRoot);

        for (var i = 0; i < limits.Count; i++)
        {
            AddLimitRow(grid, limits[i], i + 1, resourceRoot);
        }

        stack.Children.Add(grid);
        return WrapInCard(stack, resourceRoot);
    }

    private static void AddLimitHeaderRow(Grid grid, FrameworkElement resourceRoot)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddToGrid(grid, CreateCapsLabel("VARIABLE", resourceRoot), 0, 0);
        AddToGrid(grid, CreateCapsLabel("ALLOWED VALUES", resourceRoot), 0, 2);
        AddToGrid(grid, CreateCapsLabel("READ FROM", resourceRoot), 0, 4);
    }

    private static void AddLimitRow(Grid grid, VariableLimit limit, int row, FrameworkElement resourceRoot)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock
        {
            Text = limit.Name,
            FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
            FontSize = 12,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        name.ToolTip = $"{limit.Type} ({DescribeScope(limit.Scope)})";
        AddToGrid(grid, name, row, 0);

        var value = new TextBlock
        {
            Text = limit.Limit,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, LimitBrushKey(limit.Confidence)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        AddToGrid(grid, value, row, 2);

        var evidence = new TextBlock
        {
            Text = limit.Evidence,
            FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
            FontSize = 11,
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.85,
        };
        evidence.ToolTip = $"{limit.Evidence}\n\n{DescribeLimitSource(limit)}";
        AddToGrid(grid, evidence, row, 4);
    }

    private static void AddToGrid(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    /// <summary>
    /// A range only a type implies is dimmer than one the code enforces, so the eye goes to the
    /// limits that were actually checked.
    /// </summary>
    private static string LimitBrushKey(AnalysisConfidence confidence) => confidence switch
    {
        AnalysisConfidence.High => "SecondaryBrush",
        AnalysisConfidence.Medium => "PrimaryBrush",
        _ => "TextSecondaryBrush",
    };

    private static string DescribeScope(VariableScopeKind scope) => scope switch
    {
        VariableScopeKind.Parameter => "parameter",
        VariableScopeKind.Local => "local variable",
        VariableScopeKind.Field => "field",
        _ => "property",
    };

    private static string DescribeLimitSource(VariableLimit limit) => limit.Source switch
    {
        VariableLimitSource.Guard => "The method rejects values outside this range.",
        VariableLimitSource.Clamp => "The method forces the value into this range.",
        VariableLimitSource.Comparison => "Implied by comparisons the method relies on.",
        VariableLimitSource.LoopBound => "The counter's range in a fixed loop.",
        _ => "The natural range of the declared type; no narrower check was found.",
    };

    private static Border BuildVariablesCard(MethodDetailContext context, FrameworkElement resourceRoot)
    {
        var method = context.Method;
        var parentClass = method.ParentClass;
        var stack = new StackPanel();
        AddCardHeader(stack, "Local & Global Variables", resourceRoot);
        stack.Children.Add(CreateCapsLabel("FROM CODE", resourceRoot));

        var globalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globals = parentClass?.Fields ?? [];
        stack.Children.Add(CreateCapsLabel("GLOBAL", resourceRoot, marginTop: 10));
        if (globals.Count == 0 && context.ScideType?.Fields.Count is not > 0)
        {
            stack.Children.Add(CreateMutedText("No class-level fields detected.", resourceRoot, marginTop: 4));
        }
        else
        {
            foreach (var field in globals)
            {
                globalNames.Add(field.Name);
                stack.Children.Add(CreateVariableChip(field.Name, field.Type, "GLOBAL", resourceRoot));
            }

            foreach (var field in context.ScideType?.Fields ?? [])
            {
                if (!globalNames.Add(field.Name)) continue;
                stack.Children.Add(CreateVariableChip(field.Name, field.TypeName, "GLOBAL", resourceRoot));
            }
        }

        stack.Children.Add(CreateCapsLabel("LOCAL", resourceRoot, marginTop: 10));
        if (method.LocalVariables.Count == 0)
            stack.Children.Add(CreateMutedText("No local variables detected.", resourceRoot, marginTop: 4));
        else
            foreach (var local in method.LocalVariables)
            {
                if (!string.IsNullOrEmpty(local.InitialValue))
                    stack.Children.Add(CreateVariableChipWithInitial(local.Name, local.Type, local.InitialValue, resourceRoot));
                else
                    stack.Children.Add(CreateVariableChip(local.Name, local.Type, "LOCAL", resourceRoot));
            }

        if (context.ScideType?.Properties.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 12, 0, 10) });
            stack.Children.Add(CreateCapsLabel("CLASS PROPERTIES", resourceRoot));
            foreach (var prop in context.ScideType.Properties)
                stack.Children.Add(CreateVariableChip(prop.Name, prop.TypeName, "PROPERTY", resourceRoot));
        }

        if (context.FormattedOperationalLimits.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 12, 0, 10) });
            stack.Children.Add(CreateCapsLabel("OPERATIONAL LIMITS", resourceRoot));
            foreach (var limit in context.FormattedOperationalLimits)
                stack.Children.Add(CreateBulletItem(limit, resourceRoot));
        }

        if (context.Analysis.Variables.Count > 0)
        {
            stack.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 12, 0, 10) });
            stack.Children.Add(CreateCapsLabel("USAGE ANALYSIS", resourceRoot));
            foreach (var variable in context.Analysis.Variables.Where(v => v.Usage != VariableUsageKind.Unused))
                stack.Children.Add(CreateBulletItem($"{variable.Name} ({variable.Type}): {variable.Usage}", resourceRoot));
        }

        return WrapInCard(stack, resourceRoot);
    }

    private static Border BuildConditionsCard(StackPanel contentHost, FrameworkElement resourceRoot)
    {
        var stack = new StackPanel();
        AddCardHeader(stack, "Pre & Post Conditions", resourceRoot);
        stack.Children.Add(contentHost);
        return WrapInCard(stack, resourceRoot);
    }

    private static void PopulatePrePostConditionsCard(
        StackPanel host,
        MethodAnalysis analysis,
        FrameworkElement resourceRoot)
    {
        host.Children.Clear();

        host.Children.Add(CreateCapsLabel("PRECONDITIONS", resourceRoot));
        if (analysis.Preconditions.Count == 0)
        {
            host.Children.Add(CreateItalicPlaceholder("No preconditions detected.", resourceRoot, marginTop: 8));
        }
        else
        {
            foreach (var precondition in analysis.Preconditions)
            {
                host.Children.Add(CreateBulletItem(precondition.Description, resourceRoot));
            }
        }

        host.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 10, 0, 10),
        });

        var hasPostconditions = analysis.Postconditions.Count > 0 || analysis.StateChanges.Count > 0;
        host.Children.Add(CreateCapsLabel("POSTCONDITIONS", resourceRoot));
        if (!hasPostconditions)
        {
            host.Children.Add(CreateItalicPlaceholder("No postconditions detected.", resourceRoot, marginTop: 8));
        }
        else
        {
            foreach (var postcondition in analysis.Postconditions)
            {
                host.Children.Add(CreateBulletItem(postcondition.Description, resourceRoot));
            }

            foreach (var stateChange in analysis.StateChanges)
            {
                host.Children.Add(CreateBulletItem(stateChange.Description, resourceRoot));
            }
        }
    }

    private static Border BuildDesignConstraintsCard(StackPanel contentHost, FrameworkElement resourceRoot)
    {
        var stack = new StackPanel();
        AddCardHeader(stack, "Design Requirements", resourceRoot);
        stack.Children.Add(contentHost);
        return WrapInCard(stack, resourceRoot);
    }

    private static void PopulateExecutionStepsSection(
        StackPanel host,
        List<ExecutionStep> steps,
        FrameworkElement resourceRoot)
    {
        host.Children.Clear();
        host.Children.Add(CreateCapsLabel("EXECUTION FLOW", resourceRoot));

        if (steps.Count == 0)
        {
            host.Children.Add(CreateItalicPlaceholder(
                "No execution steps detected from source analysis.",
                resourceRoot,
                marginTop: 8));
            return;
        }

        foreach (var step in steps)
        {
            var row = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 10),
                LastChildFill = true,
            };

            var stepNumber = new TextBlock
            {
                Text = $"{step.StepNumber}.",
                FontWeight = FontWeights.Bold,
                Foreground = Brush(resourceRoot, "PrimaryBrush"),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(stepNumber, Dock.Left);
            row.Children.Add(stepNumber);

            row.Children.Add(new TextBlock
            {
                Text = step.Description,
                Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Top,
            });

            host.Children.Add(row);
        }
    }

    private static void PopulateScideStructuralSection(
        StackPanel host,
        MethodDetailContext context,
        FrameworkElement resourceRoot)
    {
        if (context.ScideMethod is null && context.ScideComplexity <= 0)
        {
            return;
        }

        host.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 12, 0, 10),
        });
        host.Children.Add(CreateCapsLabel("STRUCTURAL ANALYSIS", resourceRoot));

        if (context.ScideComplexity > 0)
        {
            host.Children.Add(CreateBulletItem(
                $"Cyclomatic complexity: {context.ScideComplexity}",
                resourceRoot));
        }

        if (context.ScideCallTargets.Count > 0)
        {
            host.Children.Add(CreateMutedText("Calls detected:", resourceRoot, marginTop: 6));
            foreach (var call in context.ScideCallTargets.Take(8))
                host.Children.Add(CreateBulletItem(call, resourceRoot));
            if (context.ScideCallTargets.Count > 8)
            {
                host.Children.Add(CreateMutedText(
                    $"…and {context.ScideCallTargets.Count - 8} more",
                    resourceRoot,
                    marginTop: 4));
            }
        }
    }

    private static void PopulateInferenceDesignSection(
        StackPanel host,
        MethodAnalysis analysis,
        FrameworkElement resourceRoot)
    {
        if (analysis.DesignConstraints.Count == 0 && analysis.Dependencies.Count == 0)
        {
            return;
        }

        host.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 12, 0, 10),
        });
        host.Children.Add(CreateCapsLabel("DESIGN CONSTRAINTS (INFERENCE)", resourceRoot));

        foreach (var constraint in analysis.DesignConstraints)
            host.Children.Add(CreateBulletItem(constraint.Description, resourceRoot));

        if (analysis.Dependencies.Count > 0)
        {
            host.Children.Add(CreateMutedText("Dependencies:", resourceRoot, marginTop: 8));
            foreach (var dep in analysis.Dependencies)
                host.Children.Add(CreateBulletItem(dep.Name, resourceRoot));
        }
    }

    private static Border BuildErrorsCard(MethodDetailContext context, FrameworkElement resourceRoot, IExplanationService? explanationService)
    {
        var method = context.Method;
        var stack = new StackPanel();
        AddCardHeader(stack, "Errors / Exceptions", resourceRoot);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var organicExceptions = GetOrganicExceptions(method);
        var runtimeRisks = context.Analysis.RuntimeRisks;

        // Left: from code + inference
        var left = new StackPanel();
        left.Children.Add(CreateCapsLabel("EXCEPTIONS FROM CODE", resourceRoot));

        if (organicExceptions.Count > 0)
        {
            foreach (var ex in organicExceptions)
                left.Children.Add(CreateBulletItem($"{ex.Type}: {ex.Description}", resourceRoot));
        }
        else
        {
            left.Children.Add(CreateItalicPlaceholder("No exceptions detected in source code.", resourceRoot, marginTop: 8));
        }

        if (runtimeRisks.Count > 0)
        {
            left.Children.Add(new Border { Height = 1, Background = Brush(resourceRoot, "BorderBrush"), Margin = new Thickness(0, 10, 0, 10) });
            left.Children.Add(CreateCapsLabel("RUNTIME RISKS (INFERENCE)", resourceRoot));
            foreach (var risk in runtimeRisks)
            {
                var label = string.IsNullOrEmpty(risk.ExceptionType)
                    ? risk.Description
                    : $"{risk.ExceptionType}: {risk.Description}";
                left.Children.Add(CreateBulletItem(label, resourceRoot));
            }
        }

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right: AI error analysis
        var right = new StackPanel();
        right.Children.Add(CreateCapsLabel("AI ERROR DETECTION", resourceRoot));
        var aiErrorHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        right.Children.Add(aiErrorHost);

        var generateErrorBtn = CreateRegenerateButton("Generate AI Error Analysis", () => { }, resourceRoot, marginTop: 8);
        right.Children.Add(generateErrorBtn);

        void RunErrorAnalysis(bool isAuto)
        {
            if (explanationService is null || !explanationService.IsReady)
            {
                SetPlaceholder(aiErrorHost, GetAiUnavailableMessage(explanationService));
                return;
            }

            if (!isAuto)
            {
                // A deliberate press of "Regenerate" must reach the model; the automatic first
                // run is happy with whatever the cache already holds.
                if (generateErrorBtn.Content is string label && label.StartsWith("Regenerate", StringComparison.Ordinal))
                {
                    explanationService.Forget(method);
                }

                generateErrorBtn.IsEnabled = false;
                generateErrorBtn.Content = "Generating…";
            }

            SetPlaceholder(aiErrorHost, "Generating error analysis…");
            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                var analysis = svc.GenerateErrorAnalysis(m);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    PopulateBulletList(aiErrorHost, analysis, resourceRoot);
                    generateErrorBtn.Content = "Regenerate AI Error Analysis";
                    generateErrorBtn.IsEnabled = true;
                });
            });
        }

        generateErrorBtn.Click += (_, _) => RunErrorAnalysis(isAuto: false);

        if (organicExceptions.Count == 0 && runtimeRisks.Count == 0 && explanationService is { IsReady: true })
            RunErrorAnalysis(isAuto: true);
        else if (organicExceptions.Count == 0 && runtimeRisks.Count == 0)
            SetPlaceholder(aiErrorHost, GetAiUnavailableMessage(explanationService));
        else
            aiErrorHost.Children.Add(CreateItalicPlaceholder("Click Generate AI Error Analysis for additional runtime risks.", resourceRoot));

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        stack.Children.Add(grid);
        return WrapInCard(stack, resourceRoot);
    }

    private static Border BuildHowThisFitsInCard(MethodDetailContext context, FrameworkElement resourceRoot)
    {
        var method = context.Method;
        var parentClass = method.ParentClass;
        var stack = new StackPanel();
        AddCardHeader(stack, "How This Fits In", resourceRoot);

        if (parentClass is null)
        {
            stack.Children.Add(CreateBodyText("Parent class context is not available.", resourceRoot));
        }
        else
        {
            stack.Children.Add(CreateLabeledRow("Lives in", $"{parentClass.Name} ({DescribeCategoryLabel(parentClass.Category)})", resourceRoot));
            stack.Children.Add(CreateLabeledRow("That class depends on",
                parentClass.Dependencies.Count > 0 ? string.Join(", ", parentClass.Dependencies) : "None",
                resourceRoot, marginTop: 6));
            stack.Children.Add(CreateLabeledRow("That class extends",
                string.IsNullOrEmpty(parentClass.BaseClassName) ? "No base class" : parentClass.BaseClassName,
                resourceRoot, marginTop: 6));
        }

        if (context.ScideCallTargets.Count > 0)
        {
            stack.Children.Add(CreateLabeledRow("This method calls",
                string.Join(", ", context.ScideCallTargets.Take(6)),
                resourceRoot, marginTop: 10));
        }

        if (context.ScideType is not null && context.ProjectIr is not null)
        {
            var inherits = context.ProjectIr.Relationships
                .Where(r => r.Kind == "INHERITS" && r.SourceId == context.ScideType.FullName)
                .Select(r => r.TargetId)
                .ToList();
            if (inherits.Count > 0)
            {
                stack.Children.Add(CreateLabeledRow("Inheritance",
                    string.Join(", ", inherits),
                    resourceRoot, marginTop: 6));
            }
        }

        return WrapInCard(stack, resourceRoot);
    }

    /// <summary>
    /// A method's chat thread, kept so navigating away and back restores it rather than silently
    /// dropping the conversation. Everything shown in the transcript is rebuilt from
    /// <see cref="IMethodConversationSession.History"/>, so this holds no view state.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "False positive. Instances are created by ConditionalWeakTable's " +
                        "GetOrCreateValue (see ChatStates below), which constructs the value type " +
                        "through reflection rather than a visible 'new', so the analyser cannot see " +
                        "the instantiation. Removing the type would break the method chat threads.")]
    private sealed class MethodChatState
    {
        public IMethodConversationSession? Session;

        /// <summary>
        /// The generated explanation shown as the thread's opening message, or <c>null</c> when the
        /// user went straight to a question. In that case the session is seeded implicitly from the
        /// brief description, which already appears in its own card — repeating it as a chat
        /// message would just be noise.
        /// </summary>
        public string? OpeningExplanation;
    }

    /// <summary>
    /// Live chat threads, keyed by method. Weak keys on purpose: a rescan builds a whole new set of
    /// <see cref="MethodInfo"/> objects, and the threads belonging to the discarded ones should go
    /// with them instead of pinning stale conversations for the life of the process.
    /// </summary>
    private static readonly ConditionalWeakTable<MethodInfo, MethodChatState> ChatStates = new();

    private static Border BuildAiExplanationCard(
        MethodInfo method,
        FrameworkElement resourceRoot,
        IExplanationService? explanationService,
        string? existingSummary,
        TextBlock? aiBriefText)
    {
        var state = ChatStates.GetOrCreateValue(method);

        var stack = new StackPanel();
        AddCardHeader(stack, "AI Explanation", resourceRoot)
            .Children.Add(CreateBadge("AI", "WarningBrush", resourceRoot, marginLeft: 8));

        var transcript = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        stack.Children.Add(transcript);

        var startersHost = new StackPanel();
        stack.Children.Add(startersHost);

        var statusText = CreateItalicPlaceholder(string.Empty, resourceRoot, marginTop: 10);
        statusText.Visibility = Visibility.Collapsed;
        stack.Children.Add(statusText);

        // Composer
        var composer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 14, 0, 0) };
        var sendBtn = new Button
        {
            Content = "Send",
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brush(resourceRoot, "PrimaryBrush"),
            Foreground = Brush(resourceRoot, "SurfaceBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var input = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 7, 10, 7),
            Background = Brush(resourceRoot, "SurfaceBrush"),
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(sendBtn, Dock.Right);
        composer.Children.Add(sendBtn);
        composer.Children.Add(input);
        stack.Children.Add(composer);

        var generateBtn = CreateRegenerateButton("Generate AI Explanation", () => { }, resourceRoot, marginTop: 10);
        stack.Children.Add(generateBtn);

        // Sits beside the generate button and appears only while the model is running. Every
        // other slow operation in the application can be stopped; generation could not, leaving
        // the reader with nothing to do but wait out a long answer.
        Action? stopHandler = null;
        var stopBtn = CreateRegenerateButton("Stop", () => stopHandler?.Invoke(), resourceRoot, marginTop: 6);
        stopBtn.Visibility = Visibility.Collapsed;
        stopBtn.Background = Brush(resourceRoot, "SurfaceBrush");
        stopBtn.Foreground = Brush(resourceRoot, "TextPrimaryBrush");
        stopBtn.BorderBrush = Brush(resourceRoot, "BorderBrush");
        stopBtn.BorderThickness = new Thickness(1);
        System.Windows.Automation.AutomationProperties.SetName(stopBtn, "Stop generating");
        stack.Children.Add(stopBtn);

        // The whole conversation, so an explanation and the answers that followed it can be
        // taken away together rather than a paragraph at a time.
        var copyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };
        copyRow.Children.Add(CreateCopyButton(
            "Copy the explanation and answers",
            () => string.Join(
                Environment.NewLine + Environment.NewLine,
                transcript.Children.OfType<FrameworkElement>()
                    .Select(FindMessageText)
                    .Where(t => !string.IsNullOrWhiteSpace(t))),
            resourceRoot));
        stack.Children.Add(copyRow);

        var isBusy = false;

        string GetSeedExplanation()
        {
            if (!string.IsNullOrWhiteSpace(state.OpeningExplanation))
                return state.OpeningExplanation;

            if (!string.IsNullOrWhiteSpace(existingSummary))
                return existingSummary;

            // The cache holds the complete text the moment generation finishes; the TextBlock
            // may still be mid-reveal animation.
            if (!string.IsNullOrWhiteSpace(method.CachedAiBriefDescription))
                return method.CachedAiBriefDescription;

            if (aiBriefText is not null && !string.IsNullOrWhiteSpace(aiBriefText.Text) &&
                !aiBriefText.Text.StartsWith("Generating", StringComparison.Ordinal))
                return aiBriefText.Text;

            return "No prior explanation available.";
        }

        // Returns the body block so a streaming answer can keep writing into the message already
        // on screen, the way a chat client fills in a reply in place.
        //
        // Position carries the speaker, as it does in a normal chat client: the user's turns sit
        // right in a filled bubble, the model's run flush left across the full width. A small
        // "YOU"/"AI" caption did not survive being read at a glance in a dense panel.
        TextBlock AppendMessage(string text, bool isUser)
        {
            var body = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(resourceRoot, isUser ? "SurfaceBrush" : "TextPrimaryBrush"),
                FontSize = 13,
                LineHeight = 22,
            };

            // The narrow first column caps a user bubble at roughly four fifths of the card, so it
            // reads as a bubble instead of a full-width block; answers span both columns.
            var row = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });

            if (isUser)
            {
                var bubble = new Border
                {
                    Background = Brush(resourceRoot, "PrimaryBrush"),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(14, 10, 14, 10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = body,
                };
                Grid.SetColumn(bubble, 1);
                row.Children.Add(bubble);
            }
            else
            {
                Grid.SetColumnSpan(body, 2);
                row.Children.Add(body);
            }

            transcript.Children.Add(row);
            transcript.Visibility = Visibility.Visible;
            return body;
        }

        void RebuildTranscript()
        {
            transcript.Children.Clear();

            if (!string.IsNullOrWhiteSpace(state.OpeningExplanation))
                AppendMessage(state.OpeningExplanation, isUser: false);

            var history = state.Session?.History ?? (IReadOnlyList<ConversationTurn>)Array.Empty<ConversationTurn>();
            foreach (var turn in history)
            {
                AppendMessage(turn.Question, isUser: true);
                AppendMessage(turn.Answer, isUser: false);
            }

            transcript.Visibility = transcript.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Starter prompts are scaffolding for an empty thread only. Once the conversation is under
        // way they disappear, so the card reads as a chat rather than a menu of canned questions.
        void RefreshStarters()
        {
            startersHost.Children.Clear();

            if (transcript.Children.Count > 0)
            {
                startersHost.Visibility = Visibility.Collapsed;
                return;
            }

            startersHost.Visibility = Visibility.Visible;
            startersHost.Children.Add(CreateCapsLabel("ASK ABOUT THIS METHOD", resourceRoot, marginTop: 12));

            foreach (var question in GetFollowUpQuestions(method).Concat(CustomFaqStore.Load()))
            {
                var chip = new Button
                {
                    Content = question,
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 6, 0, 0),
                    Background = Brush(resourceRoot, "SurfaceBrush"),
                    Foreground = Brush(resourceRoot, "PrimaryBrush"),
                    BorderBrush = Brush(resourceRoot, "BorderBrush"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FontSize = 12,
                };
                var captured = question;
                chip.Click += (_, _) => Send(captured);
                startersHost.Children.Add(chip);
            }
        }

        void SetBusy(bool busy)
        {
            isBusy = busy;
            sendBtn.IsEnabled = !busy;
            input.IsEnabled = !busy;
            generateBtn.IsEnabled = !busy;
            foreach (var child in startersHost.Children)
            {
                if (child is Button chip) chip.IsEnabled = !busy;
            }
        }

        void ShowStatus(string message)
        {
            statusText.Text = message;
            statusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        void Send(string rawQuestion)
        {
            if (isBusy) return;

            var question = rawQuestion.Trim();
            if (question.Length == 0) return;

            if (explanationService is null || !explanationService.IsReady)
            {
                ShowStatus(GetAiUnavailableMessage(explanationService));
                return;
            }

            ShowStatus(string.Empty);
            state.Session ??= explanationService.StartMethodConversation(method, GetSeedExplanation());

            input.Text = string.Empty;
            AppendMessage(question, isUser: true);
            var answerBlock = AppendMessage("Thinking…", isUser: false);
            answerBlock.Opacity = 0.6;

            // Hides the starter chips now that the thread has content.
            RefreshStarters();
            SetBusy(true);

            var activeSession = state.Session;
            Task.Run(() =>
            {
                string answer;
                try
                {
                    answer = activeSession.Ask(question, partial =>
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            answerBlock.Text = partial;
                            answerBlock.Opacity = 1.0;
                        }));
                }
                catch (Exception ex)
                {
                    answer = $"[Failed to get an answer: {ex.Message}]";
                }

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    answerBlock.Text = answer;
                    answerBlock.Opacity = 1.0;
                    SetBusy(false);
                });
            });
        }

        sendBtn.Click += (_, _) => Send(input.Text);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                Send(input.Text);
                e.Handled = true;
            }
        };

        generateBtn.Click += (_, _) =>
        {
            if (isBusy) return;

            if (explanationService is null || !explanationService.IsReady)
            {
                ShowStatus(GetAiUnavailableMessage(explanationService));
                return;
            }

            ShowStatus(string.Empty);

            // "Regenerate Explanation" means the reader wants a different explanation, so the
            // service's cached one is dropped first — otherwise it hands back the same text and
            // the transcript is cleared for nothing.
            if (generateBtn.Content is string label && label.StartsWith("Regenerate", StringComparison.Ordinal))
            {
                explanationService.Forget(method);
            }

            // A new explanation reseeds the conversation, so the existing thread — whose answers
            // were produced against the previous seed — is cleared rather than left on screen
            // looking like it still applies. The transcript is visible, so the reset is too.
            state.Session = null;
            state.OpeningExplanation = null;
            transcript.Children.Clear();
            RefreshStarters();

            var answerBlock = AppendMessage("Generating explanation…", isUser: false);
            answerBlock.Opacity = 0.6;
            generateBtn.Content = "Generating…";
            SetBusy(true);

            // A long explanation used to leave the reader with nothing to do but wait, while
            // every other slow operation in the application could be stopped. The button that
            // started the generation becomes the one that stops it, so the control is where the
            // reader is already looking.
            var generation = new CancellationTokenSource();
            var token = generation.Token;
            var finished = false;
            stopBtn.Visibility = Visibility.Visible;
            stopBtn.IsEnabled = true;

            // Guarded because stopping and finishing can both arrive: the reader presses Stop,
            // and the answer that was already in flight completes a moment later. Whichever
            // happens first owns the outcome.
            void EndGeneration()
            {
                if (finished) return;
                finished = true;

                stopBtn.Visibility = Visibility.Collapsed;
                generateBtn.Content = "Regenerate Explanation";
                SetBusy(false);
                RefreshStarters();
            }

            stopHandler = () =>
            {
                if (finished) return;

                // The token is cancelled so the model stops as soon as it reaches a point where
                // it can, but the panel does not wait for that. The library that drives the
                // model decides when to look at the token, and on a long answer it can be many
                // seconds — long enough that a reader who pressed Stop would reasonably conclude
                // nothing had happened. Releasing the panel now is what they asked for; the
                // half-finished answer is discarded whenever the model gets round to stopping.
                generation.Cancel();

                answerBlock.Text = answerBlock.Text is "Generating explanation…" or ""
                    ? "Stopped before anything was generated."
                    : answerBlock.Text.TrimEnd() + " …stopped.";
                answerBlock.Opacity = 1.0;
                state.OpeningExplanation = null;
                EndGeneration();
            };

            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                try
                {
                    var text = svc.ExplainMethod(m, partial =>
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            // A stopped generation must not keep writing to the panel, which the
                            // reader has been told is finished with.
                            if (finished) return;
                            answerBlock.Text = partial;
                            answerBlock.Opacity = 1.0;
                        }),
                        token);

                    Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (finished) return;

                        answerBlock.Text = text;
                        answerBlock.Opacity = 1.0;

                        // Only a genuine explanation seeds the thread; a bracketed message is an
                        // error string and must not become the model's idea of the method.
                        state.OpeningExplanation = text.StartsWith('[') ? null : text;
                        EndGeneration();
                    });
                }
                catch (OperationCanceledException)
                {
                    // The panel was already released when Stop was pressed; nothing to undo.
                }
                finally
                {
                    // Disposed here rather than when the panel is released: the token has to stay
                    // alive until the model actually stops reading it, which can be well after
                    // the reader has moved on.
                    generation.Dispose();
                }
            }, token);
        };

        RebuildTranscript();
        RefreshStarters();
        if (!string.IsNullOrWhiteSpace(state.OpeningExplanation))
        {
            generateBtn.Content = "Regenerate Explanation";
        }

        return WrapInCard(stack, resourceRoot);
    }

    // ── AI helpers ────────────────────────────────────────────────────────────

    private static List<(string Type, string Description)> GetOrganicExceptions(MethodInfo method)
    {
        var results = new List<(string Type, string Description)>();

        foreach (var ex in method.ThrownExceptions)
        {
            var doc = FindExceptionDescription(method, ex);
            results.Add((ex, doc ?? "Thrown in method body"));
        }

        foreach (var tag in method.XmlDocTags)
        {
            if (!tag.Key.StartsWith("exception:", StringComparison.OrdinalIgnoreCase)) continue;
            var type = tag.Key["exception:".Length..];
            if (results.Any(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))) continue;
            results.Add((type, tag.Value));
        }

        return results;
    }

    private static void SetPlaceholder(StackPanel host, string message)
    {
        host.Children.Clear();
        host.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = host.TryFindResource("TextSecondaryBrush") as Brush ?? SystemColors.GrayTextBrush,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
    }

    /// <summary>
    /// Appends an AI sub-block — divider, caps label, bullet host — under an organic card section,
    /// and returns the host the bullets belong in. <paramref name="label"/> names what the block
    /// contains ("AI DESIGN REQUIREMENTS", "AI PRECONDITIONS") so the bullets are never left for
    /// the reader to classify. The divider and label deliberately live in <paramref name="parent"/>
    /// rather than the returned host, because <see cref="PopulateBulletList"/> clears whatever
    /// host it fills.
    /// </summary>
    private static StackPanel CreateAiEnhancementBlock(StackPanel parent, string label, FrameworkElement resourceRoot)
    {
        parent.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 12, 0, 10),
        });
        parent.Children.Add(CreateCapsLabel(label, resourceRoot));

        var bulletHost = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        parent.Children.Add(bulletHost);
        return bulletHost;
    }

    private static void PopulateBulletList(StackPanel host, string text, FrameworkElement resourceRoot)
    {
        host.Children.Clear();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasBullets = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart('-', '•', '*', ' ');
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            hasBullets = true;
            host.Children.Add(CreateBulletItem(trimmed, resourceRoot));
        }

        if (!hasBullets)
            host.Children.Add(CreateBodyText(text, resourceRoot));
    }

    private static Grid CreateBulletItem(string text, FrameworkElement resourceRoot)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = new TextBlock
        {
            Text = "•",
            Foreground = Brush(resourceRoot, "PrimaryBrush"),
            Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(bullet, 0);
        grid.Children.Add(bullet);

        var body = new TextBlock
        {
            Text = text,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        };
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        return grid;
    }

    // ── Cards (class/file views) ──────────────────────────────────────────────

    private static IEnumerable<string> GetFollowUpQuestions(MethodInfo method)
    {
        yield return $"What happens if the inputs to {method.Name} are null or out of range?";
        yield return $"How is {method.Name} called in the rest of the codebase?";
        if (method.ThrownExceptions.Count > 0)
            yield return $"When exactly is {method.ThrownExceptions[0]} thrown?";
        yield return $"Can {method.Name} be made asynchronous?";
    }

    // ── UI primitives ─────────────────────────────────────────────────────────

    private static Border WrapInCard(UIElement content, FrameworkElement resourceRoot)
    {
        // The border sits on its own brush so it can fade to the accent on hover — the card
        // "lights up" a little without moving. Colors are captured per theme; cards rebuild on
        // a theme switch, so the captured values stay correct.
        var restColor = ((SolidColorBrush)Brush(resourceRoot, "BorderBrush")).Color;
        var hoverColor = ((SolidColorBrush)Brush(resourceRoot, "PrimaryBrush")).Color;
        var borderBrush = new SolidColorBrush(restColor);

        var card = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                Opacity = 0.18,
                ShadowDepth = 2,
            },
            Child = content,
        };

        card.MouseEnter += (_, _) => borderBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(hoverColor, TimeSpan.FromMilliseconds(140)));
        card.MouseLeave += (_, _) => borderBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(restColor, TimeSpan.FromMilliseconds(180)));

        return card;
    }

    private static StackPanel AddCardHeader(StackPanel stack, string title, FrameworkElement resourceRoot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        row.Children.Add(new Ellipse
        {
            Width = 8, Height = 8,
            Fill = Brush(resourceRoot, "PrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(row);
        return row;
    }

    private static void AddSection(StackPanel host, string title, FrameworkElement resourceRoot)
    {
        host.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            Margin = new Thickness(0, 20, 0, 0),
        });
        host.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 6, 0, 8),
        });
    }

    private static TextBlock CreateCapsLabel(string text, FrameworkElement resourceRoot, double marginTop = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            Margin = new Thickness(0, marginTop, 0, 0),
        };
    }

    private static Button CreateRegenerateButton(string label, Action onClick, FrameworkElement resourceRoot, double marginTop = 0)
    {
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(16, 8, 16, 8),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Background = Brush(resourceRoot, "PrimaryBrush"),
            Foreground = Brush(resourceRoot, "SurfaceBrush"),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, marginTop, 0, 0),
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    /// <summary>
    /// The text of a transcript entry, whatever it is wrapped in.
    /// </summary>
    /// <remarks>
    /// Entries are bubbles rather than bare text blocks, and the shape differs between a question
    /// and an answer, so the text is found by looking rather than by assuming a structure.
    /// </remarks>
    private static string FindMessageText(FrameworkElement element)
    {
        if (element is TextBlock direct)
        {
            return direct.Text;
        }

        if (element is Border { Child: TextBlock inBorder })
        {
            return inBorder.Text;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                var text = FindMessageText(child);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        if (element is Border { Child: FrameworkElement nested })
        {
            return FindMessageText(nested);
        }

        return string.Empty;
    }

    /// <summary>
    /// A small button that puts the given text on the clipboard and says so briefly.
    /// </summary>
    /// <remarks>
    /// Generated explanations and the limits table are exactly the things a reader wants to paste
    /// into a report or a message, and selecting text across a wrapped panel by dragging is
    /// awkward and easy to get wrong. The label changes to "Copied" for a moment because a copy
    /// that gives no sign of having happened invites a second press.
    /// </remarks>
    private static Button CreateCopyButton(string toolTip, Func<string> getText, FrameworkElement resourceRoot)
    {
        var button = new Button
        {
            Content = "Copy",
            Padding = new Thickness(8, 2, 8, 2),
            FontSize = 10,
            Background = Brush(resourceRoot, "SurfaceBrush"),
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = toolTip,
        };

        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);

        button.Click += (_, _) =>
        {
            var text = getText();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another process can hold the clipboard open. Nothing here is worth interrupting
                // the reader over; the unchanged label already says it did not happen.
                return;
            }

            button.Content = "Copied";
            var revert = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            revert.Tick += (_, _) =>
            {
                button.Content = "Copy";
                revert.Stop();
            };
            revert.Start();
        };

        return button;
    }

    /// <summary>
    /// A link that opens the given path, or shows it in the file manager.
    /// </summary>
    /// <remarks>
    /// The panel names the file a class or method came from, but the name was inert text: reading
    /// it and then finding the file by hand is a step the application can simply take.
    /// </remarks>
    internal static StackPanel CreateFileActions(string filePath, FrameworkElement resourceRoot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        row.Children.Add(CreateSmallAction(
            "Open file",
            $"Open {System.IO.Path.GetFileName(filePath)}",
            resourceRoot,
            () => Launch(new ProcessStartInfo(filePath) { UseShellExecute = true })));

        // Named absolutely so the command cannot resolve to something else that happens to be
        // earlier on the path.
        var explorer = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

        row.Children.Add(CreateSmallAction(
            "Show in folder",
            "Show this file in the file manager",
            resourceRoot,
            () => Launch(new ProcessStartInfo(explorer, $"/select,\"{filePath}\""))));

        return row;
    }

    private static Button CreateSmallAction(
        string label, string toolTip, FrameworkElement resourceRoot, Action onClick)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 11,
            Background = Brush(resourceRoot, "SurfaceBrush"),
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = toolTip,
        };

        System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// Starts a shell action, ignoring the failures that are the user's environment rather than
    /// a fault here — a file deleted since the scan, or no program associated with it.
    /// </summary>
    private static void Launch(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // No program associated with the file, or the shell refused. Nothing here is the
            // application's to fix, and interrupting the reader over it would be worse than the
            // button appearing to do nothing.
            Debug.WriteLine($"[JBU CodeLens] Could not open '{startInfo.FileName}': {ex.Message}");
        }
        catch (IOException ex)
        {
            // The file has been moved or deleted since the scan.
            Debug.WriteLine($"[JBU CodeLens] Could not open '{startInfo.FileName}': {ex.Message}");
        }
    }

    private static Border CreateVariableChip(string name, string type, string tag, FrameworkElement resourceRoot)
    {
        var row = new Border
        {
            Background = Brush(resourceRoot, "BackgroundBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var inner = new DockPanel { LastChildFill = true };
        var tagPill = new Border
        {
            Background = Brush(resourceRoot, "PrimaryBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = tag, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brush(resourceRoot, "SurfaceBrush") },
        };
        DockPanel.SetDock(tagPill, Dock.Right);
        inner.Children.Add(tagPill);
        inner.Children.Add(CreateNameTypeText(name, type, resourceRoot));
        row.Child = inner;
        return row;
    }

    private static Border CreateVariableChipWithInitial(string name, string type, string initial, FrameworkElement resourceRoot)
    {
        var row = new Border
        {
            Background = Brush(resourceRoot, "BackgroundBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var stack = new StackPanel();
        stack.Children.Add(CreateNameTypeText(name, type, resourceRoot));
        stack.Children.Add(new TextBlock
        {
            Text = $"Initial value: {initial}",
            FontSize = 11,
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        row.Child = stack;
        return row;
    }

    /// <summary>
    /// Single wrapping TextBlock combining a variable/property name and its type, using Runs so
    /// the whole line reflows within the actual available width instead of overflowing a
    /// horizontal StackPanel (which measures children against infinite width).
    /// </summary>
    private static TextBlock CreateNameTypeText(string name, string type, FrameworkElement resourceRoot)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run(name)
        {
            FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
        });
        text.Inlines.Add(new Run($" ({type})")
        {
            Foreground = Brush(resourceRoot, "TextSecondaryBrush"),
            FontSize = 12,
        });
        return text;
    }

    private static Button CreateMethodRow(MethodInfo method, FrameworkElement resourceRoot, Action onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse { Width = 8, Height = 8, Fill = AccessBrush(method.AccessModifier, resourceRoot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(dot, 0);

        var namePanel = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        namePanel.Inlines.Add(new Run(method.Name) { FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") });
        namePanel.Inlines.Add(new Run($"  {method.ReturnType}") { Foreground = Brush(resourceRoot, "TextSecondaryBrush") });
        Grid.SetColumn(namePanel, 1);

        var paramCount = CreateMutedText($"{method.Parameters.Count} param{(method.Parameters.Count == 1 ? "" : "s")}", resourceRoot);
        Grid.SetColumn(paramCount, 2);

        grid.Children.Add(dot); grid.Children.Add(namePanel); grid.Children.Add(paramCount);

        // A 1px transparent-until-hover border keeps the accent hover from shifting the layout.
        var row = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = grid,
        };
        var restBorder = Brush(resourceRoot, "BorderBrush");
        var accentBorder = FindAccentBrush(resourceRoot);
        row.MouseEnter += (_, _) => row.BorderBrush = accentBorder;
        row.MouseLeave += (_, _) => row.BorderBrush = restBorder;

        return WrapClickable(row, onClick, $"{method.Name}, returns {method.ReturnType}, {method.Parameters.Count} parameters");
    }

    /// <summary>
    /// Wraps a visual in a chromeless <see cref="Button"/> so it is clickable, keyboard-operable
    /// (Enter/Space), and exposed to screen readers / UI Automation as invokable — while the
    /// wrapped element keeps its own appearance and hover behavior.
    /// </summary>
    private static Button WrapClickable(FrameworkElement visual, Action onClick, string automationName)
    {
        var button = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = visual,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        button.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(button, automationName);
        return button;
    }

    private static Border CreatePropertyRow(PropertyInfo property, FrameworkElement resourceRoot)
    {
        var row = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };
        var panel = new DockPanel { LastChildFill = true };
        var dot = new Ellipse { Width = 8, Height = 8, Fill = AccessBrush(property.AccessModifier, resourceRoot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(dot, Dock.Left);
        panel.Children.Add(dot);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.Inlines.Add(new Run(property.Type)
        {
            FontFamily = (FontFamily)resourceRoot.FindResource("CodeFont"),
            Foreground = Brush(resourceRoot, "PrimaryBrush"),
        });
        text.Inlines.Add(new Run($"  {property.Name}") { FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") });
        text.Inlines.Add(new Run($"  ({property.AccessModifier})") { Foreground = Brush(resourceRoot, "TextSecondaryBrush") });
        panel.Children.Add(text);
        row.Child = panel;
        return row;
    }

    private static Border CreateChip(string text, FrameworkElement resourceRoot)
    {
        return new Border
        {
            Background = Brush(resourceRoot, "BorderBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 4, 6, 4),
            Child = new TextBlock { Text = text, Foreground = Brush(resourceRoot, "PrimaryBrush"), FontSize = 11 },
        };
    }

    private static TextBlock CreateAccentTitle(string text, FrameworkElement resourceRoot, double fontSize = 20)
    {
        return new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeights.Bold, Foreground = Brush(resourceRoot, "PrimaryBrush"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    }

    private static TextBlock CreateMutedText(string text, FrameworkElement resourceRoot, double marginTop = 0, double marginLeft = 0, double opacity = 0.55)
    {
        return new TextBlock { Text = text, Foreground = Brush(resourceRoot, "TextPrimaryBrush"), Opacity = opacity, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(marginLeft, marginTop, 0, 0) };
    }

    private static TextBlock CreateBodyText(string text, FrameworkElement resourceRoot, FontWeight? fontWeight = null, double marginTop = 0, double marginLeft = 0)
    {
        return new TextBlock { Text = text, Foreground = Brush(resourceRoot, "TextPrimaryBrush"), TextWrapping = TextWrapping.Wrap, FontWeight = fontWeight ?? FontWeights.Normal, Margin = new Thickness(marginLeft, marginTop, 0, 0), FontSize = 13 };
    }

    private static TextBlock CreateItalicPlaceholder(string text, FrameworkElement resourceRoot, double marginTop = 0)
    {
        return new TextBlock { Text = text, Foreground = Brush(resourceRoot, "TextPrimaryBrush"), Opacity = 0.55, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, marginTop, 0, 0), FontSize = 13 };
    }

    private static StackPanel CreateLabeledRow(string label, string value, FrameworkElement resourceRoot, double marginTop = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, marginTop, 0, 0) };
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush(resourceRoot, "TextPrimaryBrush"), FontSize = 13 };
        line.Inlines.Add(new Run($"{label}: ") { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new Run(value));
        panel.Children.Add(line);
        return panel;
    }

    private static Border CreateAccessPill(string accessModifier, FrameworkElement resourceRoot, double marginLeft = 0)
    {
        return new Border
        {
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(10),
            Background = Brush(resourceRoot, "SurfaceBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = accessModifier, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") },
        };
    }

    private static Border CreateCategoryPill(string text, FrameworkElement resourceRoot, double marginLeft = 0)
    {
        return new Border
        {
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(10),
            Background = Brush(resourceRoot, "SurfaceBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "PrimaryBrush") },
        };
    }

    /// <summary>
    /// Rounded badge pill per the design spec: C# = Secondary, C++ = Primary, AI = Warning
    /// background, all with Surface text, 11px font, 4px vertical / 8px horizontal padding.
    /// </summary>
    internal static Border CreateBadge(string label, string backgroundKey, FrameworkElement resourceRoot, double marginLeft = 0)
    {
        return new Border
        {
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(10),
            Background = Brush(resourceRoot, backgroundKey),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(resourceRoot, "SurfaceBrush"),
            },
        };
    }

    private static Border CreateSubtleLanguagePill(string text, FrameworkElement resourceRoot, double marginLeft = 0)
    {
        var isCpp = text.Contains("C++", StringComparison.Ordinal);
        return CreateBadge(
            text.Trim('[', ']'),
            isCpp ? "PrimaryBrush" : "SecondaryBrush",
            resourceRoot,
            marginLeft);
    }

    private static string? GetMethodLanguageBadge(MethodInfo method)
    {
        var sourcePath = method.ParentClass?.SourceFilePath;
        if (string.IsNullOrEmpty(sourcePath))
        {
            return null;
        }

        if (LanguageFileExtensions.IsCppFile(sourcePath))
        {
            return "[C++]";
        }

        if (LanguageFileExtensions.IsCSharpFile(sourcePath))
        {
            return "[C#]";
        }

        return null;
    }

    private static string GetAiUnavailableMessage(IExplanationService? explanationService)
    {
        if (explanationService is { IsReady: true })
        {
            return string.Empty;
        }

        if (explanationService?.LoadError?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
        {
            return AiGuidance.ModelNotFoundMessage;
        }

        return "AI model not loaded. Place a .gguf model file in the models/ folder.";
    }

    private static Border CreateLanguageBadge(string text, FrameworkElement resourceRoot, double marginTop = 0)
    {
        var isCpp = text.Contains("C++", StringComparison.Ordinal);
        var badge = CreateBadge(text.Trim('[', ']'), isCpp ? "PrimaryBrush" : "SecondaryBrush", resourceRoot);
        badge.Margin = new Thickness(0, marginTop, 0, 0);
        badge.HorizontalAlignment = HorizontalAlignment.Left;
        return badge;
    }

    private static Brush AccessBrush(string accessModifier, FrameworkElement resourceRoot) => accessModifier switch
    {
        "public"    => Brush(resourceRoot, "PrimaryBrush"),
        "protected" => Brush(resourceRoot, "PrimaryHoverBrush"),
        _           => Brush(resourceRoot, "BorderBrush"),
    };

    private static Brush Brush(FrameworkElement resourceRoot, string key) =>
        (Brush)resourceRoot.FindResource(key);

    private static (string Type, string Name) SplitParameter(string parameter)
    {
        var lastSpace = parameter.LastIndexOf(' ');
        if (lastSpace <= 0 || lastSpace >= parameter.Length - 1)
            return (parameter, "value");
        return (parameter[..lastSpace].Trim(), parameter[(lastSpace + 1)..].Trim());
    }

    private static string? FindExceptionDescription(MethodInfo method, string exceptionType) =>
        MethodDocumentation.FindExceptionDescription(method, exceptionType);

    private static string DescribeCategoryLabel(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "GUI Logic",
        CodeCategory.Utility  => "Utility",
        _                     => "Business Logic",
    };
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CodeLensAI.Core;

namespace CodeLensAI.UI;

/// <summary>
/// Builds the rich master-detail content panels for file, class, and method selections.
/// </summary>
internal static class DetailPanelRenderer
{
    private static readonly string[] RiskyMethodKeywords =
    [
        "Read", "Write", "Parse", "Load", "Save", "Connect", "Send", "Fetch",
    ];

    public static void Clear(StackPanel host)
    {
        host.Children.Clear();
    }

    public static void RenderFile(StackPanel host, string filePath, ParseResult? parseResult, FrameworkElement resourceRoot)
    {
        Clear(host);
        var isCpp = string.Equals(System.IO.Path.GetExtension(filePath), ".cpp", StringComparison.OrdinalIgnoreCase);
        var fileName = System.IO.Path.GetFileName(filePath);

        host.Children.Add(CreateAccentTitle(fileName, resourceRoot));
        host.Children.Add(CreateMutedText(filePath, resourceRoot, marginTop: 6));
        host.Children.Add(CreateLanguageBadge(isCpp ? "[C++]" : "[C#]", resourceRoot, marginTop: 10));

        if (isCpp)
        {
            AddSection(host, "Overview", resourceRoot);
            host.Children.Add(CreateBodyText(
                "C++ parsing not yet implemented. This file will be analyzed in a future phase.",
                resourceRoot));
            return;
        }

        if (parseResult is null || parseResult.Errors.Count > 0)
        {
            var error = parseResult?.Errors.FirstOrDefault() ?? "Unable to parse this file.";
            AddSection(host, "Overview", resourceRoot);
            host.Children.Add(CreateBodyText($"Parse error: {error}", resourceRoot));
            return;
        }

        var classCount = parseResult.Classes.Count;
        AddSection(host, "Overview", resourceRoot);
        host.Children.Add(CreateBodyText(
            classCount == 1 ? "1 class found in this file." : $"{classCount} classes found in this file.",
            resourceRoot));

        if (classCount > 0)
        {
            AddSection(host, "Classes", resourceRoot);
            foreach (var classInfo in parseResult.Classes)
            {
                host.Children.Add(CreateClassChipRow(classInfo, resourceRoot));
            }
        }
    }

    public static void RenderClass(
        StackPanel host,
        ClassInfo classInfo,
        FrameworkElement resourceRoot,
        Action<MethodInfo>? onMethodClicked)
    {
        Clear(host);

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(CreateAccentTitle(classInfo.Name, resourceRoot));
        headerRow.Children.Add(CreateCategoryPill(DescribeCategoryLabel(classInfo.Category), resourceRoot, marginLeft: 12));
        host.Children.Add(headerRow);

        if (!string.IsNullOrEmpty(classInfo.SourceFilePath))
        {
            host.Children.Add(CreateMutedText(classInfo.SourceFilePath, resourceRoot, marginTop: 4));
        }

        AddSection(host, "What This Class Does", resourceRoot);
        host.Children.Add(CreateSummaryOrPlaceholder(classInfo.XmlSummary, isMethod: false, resourceRoot));

        AddSection(host, "Inheritance & Relationships", resourceRoot);
        host.Children.Add(CreateLabeledRow(
            "Extends",
            string.IsNullOrEmpty(classInfo.BaseClassName)
                ? "No base class — this is a root class"
                : classInfo.BaseClassName,
            resourceRoot));
        host.Children.Add(CreateLabeledRow(
            "Implements",
            classInfo.ImplementedInterfaces.Count > 0
                ? string.Join(", ", classInfo.ImplementedInterfaces)
                : "None",
            resourceRoot,
            marginTop: 6));

        var dependsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        dependsRow.Children.Add(CreateBodyText("Depends on: ", resourceRoot, fontWeight: FontWeights.SemiBold));
        if (classInfo.Dependencies.Count == 0)
        {
            dependsRow.Children.Add(CreateMutedText("None", resourceRoot));
        }
        else
        {
            var chips = new WrapPanel();
            foreach (var dependency in classInfo.Dependencies)
            {
                chips.Children.Add(CreateChip(dependency, resourceRoot));
            }

            dependsRow.Children.Add(chips);
        }

        host.Children.Add(dependsRow);
        host.Children.Add(CreateMutedText("Used by / depended on by: (coming soon)", resourceRoot, marginTop: 10, opacity: 0.4));

        AddSection(host, "Members Summary", resourceRoot);
        var summaryGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var methodCount = new TextBlock
        {
            Text = $"{classInfo.Methods.Count} Method{(classInfo.Methods.Count == 1 ? "" : "s")}",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextBrush"),
        };
        var propertyCount = new TextBlock
        {
            Text = $"{classInfo.Properties.Count} Propert{(classInfo.Properties.Count == 1 ? "y" : "ies")}",
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextBrush"),
        };
        Grid.SetColumn(propertyCount, 1);
        summaryGrid.Children.Add(methodCount);
        summaryGrid.Children.Add(propertyCount);
        host.Children.Add(summaryGrid);

        if (classInfo.Methods.Count > 0)
        {
            AddSection(host, "Methods", resourceRoot);
            foreach (var method in classInfo.Methods)
            {
                host.Children.Add(CreateMethodRow(method, resourceRoot, () => onMethodClicked?.Invoke(method)));
            }
        }

        if (classInfo.Properties.Count > 0)
        {
            AddSection(host, "Properties", resourceRoot);
            foreach (var property in classInfo.Properties)
            {
                host.Children.Add(CreatePropertyRow(property, resourceRoot));
            }
        }
    }

    public static void RenderMethod(StackPanel host, MethodInfo method, FrameworkElement resourceRoot)
    {
        Clear(host);
        var parentClass = method.ParentClass;

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        headerRow.Children.Add(CreateAccentTitle(method.Name, resourceRoot, fontSize: 18));
        headerRow.Children.Add(CreateAccessPill(method.AccessModifier, resourceRoot, marginLeft: 10));
        if (parentClass is not null)
        {
            headerRow.Children.Add(CreateCategoryPill(DescribeCategoryLabel(parentClass.Category), resourceRoot, marginLeft: 8));
        }

        host.Children.Add(headerRow);

        if (parentClass is not null)
        {
            host.Children.Add(CreateMutedText($"in {parentClass.Name}", resourceRoot, marginTop: 2));
        }

        AddSection(host, "What This Function Does", resourceRoot);
        host.Children.Add(CreateSummaryOrPlaceholder(method.XmlSummary, isMethod: true, resourceRoot));

        AddSection(host, "Parameters / Inputs", resourceRoot);
        if (method.Parameters.Count == 0)
        {
            host.Children.Add(CreateBodyText("This function takes no inputs.", resourceRoot));
        }
        else
        {
            foreach (var parameter in method.Parameters)
            {
                host.Children.Add(CreateParameterCard(parameter, method, resourceRoot));
            }
        }

        AddSection(host, "Return Value / Output", resourceRoot);
        host.Children.Add(CreateReturnSection(method, resourceRoot));

        AddSection(host, "Error Situations", resourceRoot);
        RenderErrorSituations(host, method, resourceRoot);

        AddSection(host, "How This Fits In", resourceRoot);
        RenderRelationships(host, method, parentClass, resourceRoot);

        AddSection(host, "AI Explanation", resourceRoot);
        host.Children.Add(CreateAiPlaceholder(resourceRoot));
    }

    private static void RenderErrorSituations(StackPanel host, MethodInfo method, FrameworkElement resourceRoot)
    {
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasContent = false;

        foreach (var exceptionType in method.ThrownExceptions)
        {
            hasContent = true;
            shown.Add(exceptionType);
            var description = FindExceptionDescription(method, exceptionType);
            host.Children.Add(CreateWarningCard(exceptionType, description, resourceRoot));
        }

        foreach (var tag in method.XmlDocTags.Where(t => t.Key.StartsWith("exception:", StringComparison.OrdinalIgnoreCase)))
        {
            var exceptionType = tag.Key["exception:".Length..];
            if (!shown.Add(exceptionType))
            {
                continue;
            }

            hasContent = true;
            host.Children.Add(CreateWarningCard(exceptionType, tag.Value, resourceRoot));
        }

        if (!hasContent && SuggestsRiskyOperations(method.Name))
        {
            host.Children.Add(CreateWarningCard(
                "Advisory",
                "This function name suggests it may throw exceptions in error conditions. Consider wrapping calls to it in a try-catch block.",
                resourceRoot));
            return;
        }

        if (!hasContent)
        {
            host.Children.Add(CreateBodyText("No exceptions detected in this function.", resourceRoot));
        }
    }

    private static void RenderRelationships(
        StackPanel host,
        MethodInfo method,
        ClassInfo? parentClass,
        FrameworkElement resourceRoot)
    {
        if (parentClass is null)
        {
            host.Children.Add(CreateBodyText("Parent class context is not available.", resourceRoot));
            return;
        }

        host.Children.Add(CreateLabeledRow(
            "Lives in",
            $"{parentClass.Name} [{DescribeCategoryLabel(parentClass.Category)}]",
            resourceRoot));
        host.Children.Add(CreateLabeledRow(
            "That class depends on",
            parentClass.Dependencies.Count > 0 ? string.Join(", ", parentClass.Dependencies) : "None",
            resourceRoot,
            marginTop: 6));

        if (!string.IsNullOrEmpty(parentClass.BaseClassName))
        {
            host.Children.Add(CreateLabeledRow(
                "That class extends",
                parentClass.BaseClassName,
                resourceRoot,
                marginTop: 6));
        }
        else
        {
            host.Children.Add(CreateLabeledRow(
                "That class extends",
                "No base class",
                resourceRoot,
                marginTop: 6));
        }
    }

    private static UIElement CreateReturnSection(MethodInfo method, FrameworkElement resourceRoot)
    {
        var panel = new StackPanel();

        if (string.Equals(method.ReturnType, "void", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(CreateBodyText(
                "This function does not return a value — it performs an action.",
                resourceRoot));
            return panel;
        }

        var typeRow = new StackPanel { Orientation = Orientation.Horizontal };
        typeRow.Children.Add(CreateMonospaceText(method.ReturnType, resourceRoot, fontWeight: FontWeights.SemiBold));
        panel.Children.Add(typeRow);

        var hint = GetTypeHint(method.ReturnType);
        if (!string.IsNullOrEmpty(hint))
        {
            panel.Children.Add(CreateMutedText(hint, resourceRoot, marginTop: 4));
        }

        if (method.XmlDocTags.TryGetValue("returns", out var returnsDoc))
        {
            panel.Children.Add(CreateBodyText(returnsDoc, resourceRoot, marginTop: 8));
        }
        else
        {
            panel.Children.Add(CreateItalicPlaceholder(
                "No return description available — add a <returns> XML tag to document what this function gives back.",
                resourceRoot,
                marginTop: 8));
        }

        return panel;
    }

    private static Border CreateParameterCard(string parameter, MethodInfo method, FrameworkElement resourceRoot)
    {
        var (type, name) = SplitParameter(parameter);
        var card = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush(resourceRoot, "AccentBrush"),
        };

        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(CreateMonospaceText(type, resourceRoot, fontWeight: FontWeights.SemiBold));
        header.Children.Add(CreateBodyText($"  {name}", resourceRoot, fontWeight: FontWeights.Bold, marginLeft: 0));
        stack.Children.Add(header);

        var hint = GetTypeHint(type);
        if (!string.IsNullOrEmpty(hint))
        {
            stack.Children.Add(CreateMutedText(hint, resourceRoot, marginTop: 4));
        }

        if (method.XmlDocTags.TryGetValue($"param:{name}", out var paramDoc))
        {
            stack.Children.Add(CreateBodyText(paramDoc, resourceRoot, marginTop: 6));
        }
        else
        {
            stack.Children.Add(CreateItalicPlaceholder(
                "No description available — add a <param> XML tag to document this parameter.",
                resourceRoot,
                marginTop: 6));
        }

        card.Child = stack;
        return card;
    }

    private static Border CreateWarningCard(string title, string? description, FrameworkElement resourceRoot)
    {
        var card = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brush(resourceRoot, "AccentHoverBrush"),
        };

        var stack = new StackPanel();
        stack.Children.Add(CreateBodyText(title, resourceRoot, fontWeight: FontWeights.SemiBold));
        if (!string.IsNullOrWhiteSpace(description))
        {
            stack.Children.Add(CreateBodyText(description, resourceRoot, marginTop: 4));
        }

        card.Child = stack;
        return card;
    }

    private static Border CreateMethodRow(MethodInfo method, FrameworkElement resourceRoot, Action onClick)
    {
        var row = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        row.MouseLeftButtonUp += (_, _) => onClick();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = AccessBrush(method.AccessModifier, resourceRoot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(dot, 0);

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(CreateBodyText(method.Name, resourceRoot, fontWeight: FontWeights.SemiBold));
        namePanel.Children.Add(CreateMutedText(
            $"  {method.ReturnType}",
            resourceRoot,
            marginLeft: 0));
        Grid.SetColumn(namePanel, 1);

        var paramCount = CreateMutedText(
            $"{method.Parameters.Count} param{(method.Parameters.Count == 1 ? "" : "s")}",
            resourceRoot);
        Grid.SetColumn(paramCount, 2);

        grid.Children.Add(dot);
        grid.Children.Add(namePanel);
        grid.Children.Add(paramCount);
        row.Child = grid;
        return row;
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

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = AccessBrush(property.AccessModifier, resourceRoot),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        panel.Children.Add(CreateMonospaceText(property.Type, resourceRoot));
        panel.Children.Add(CreateBodyText($"  {property.Name}", resourceRoot, fontWeight: FontWeights.SemiBold, marginLeft: 0));
        panel.Children.Add(CreateMutedText($"  ({property.AccessModifier})", resourceRoot, marginLeft: 4));
        row.Child = panel;
        return row;
    }

    private static Border CreateAiPlaceholder(FrameworkElement resourceRoot)
    {
        var border = new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var indicator = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(resourceRoot, "AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Opacity = 0.4,
        };

        var animation = new DoubleAnimation
        {
            From = 0.3,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(1.2),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        indicator.BeginAnimation(UIElement.OpacityProperty, animation);

        stack.Children.Add(indicator);
        stack.Children.Add(CreateItalicPlaceholder(
            "AI explanation will appear here once the local model is loaded. This will provide a plain-English walkthrough of what this function does step by step.",
            resourceRoot));

        border.Child = stack;
        return border;
    }

    private static StackPanel CreateClassChipRow(ClassInfo classInfo, FrameworkElement resourceRoot)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6),
        };
        row.Children.Add(CreateBodyText(classInfo.Name, resourceRoot, fontWeight: FontWeights.SemiBold));
        row.Children.Add(CreateCategoryPill(DescribeCategoryLabel(classInfo.Category), resourceRoot, marginLeft: 8));
        return row;
    }

    private static Border CreateChip(string text, FrameworkElement resourceRoot)
    {
        return new Border
        {
            Background = Brush(resourceRoot, "BorderBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brush(resourceRoot, "AccentBrush"),
                FontSize = 11,
            },
        };
    }

    private static void AddSection(StackPanel host, string title, FrameworkElement resourceRoot)
    {
        host.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(resourceRoot, "TextBrush"),
            Margin = new Thickness(0, 20, 0, 0),
        });
        host.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(resourceRoot, "BorderBrush"),
            Margin = new Thickness(0, 6, 0, 8),
        });
    }

    private static TextBlock CreateAccentTitle(string text, FrameworkElement resourceRoot, double fontSize = 20)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(resourceRoot, "AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static TextBlock CreateMutedText(
        string text,
        FrameworkElement resourceRoot,
        double marginTop = 0,
        double marginLeft = 0,
        double opacity = 0.55)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Brush(resourceRoot, "TextBrush"),
            Opacity = opacity,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(marginLeft, marginTop, 0, 0),
        };
    }

    private static TextBlock CreateBodyText(
        string text,
        FrameworkElement resourceRoot,
        FontWeight? fontWeight = null,
        double marginTop = 0,
        double marginLeft = 0)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Brush(resourceRoot, "TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            FontWeight = fontWeight ?? FontWeights.Normal,
            Margin = new Thickness(marginLeft, marginTop, 0, 0),
        };
    }

    private static TextBlock CreateMonospaceText(
        string text,
        FrameworkElement resourceRoot,
        FontWeight? fontWeight = null,
        double marginLeft = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Foreground = Brush(resourceRoot, "TextBrush"),
            FontWeight = fontWeight ?? FontWeights.Normal,
            Margin = new Thickness(marginLeft, 0, 0, 0),
        };
    }

    private static TextBlock CreateItalicPlaceholder(string text, FrameworkElement resourceRoot, double marginTop = 0)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = Brush(resourceRoot, "TextBrush"),
            Opacity = 0.55,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, marginTop, 0, 0),
        };
    }

    private static UIElement CreateSummaryOrPlaceholder(string? summary, bool isMethod, FrameworkElement resourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            return CreateBodyText(summary, resourceRoot);
        }

        return CreateItalicPlaceholder(
            isMethod
                ? "No documentation comment found. Add a /// <summary> comment above this method to improve documentation quality."
                : "No documentation comment found. Add a /// <summary> comment above this class to improve documentation quality.",
            resourceRoot);
    }

    private static StackPanel CreateLabeledRow(string label, string value, FrameworkElement resourceRoot, double marginTop = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(0, marginTop, 0, 0) };
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush(resourceRoot, "TextBrush") };
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
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(10),
            Background = AccessBrush(accessModifier, resourceRoot),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = accessModifier,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(resourceRoot, "TextBrush"),
            },
        };
    }

    private static Border CreateCategoryPill(string text, FrameworkElement resourceRoot, double marginLeft = 0)
    {
        return new Border
        {
            Margin = new Thickness(marginLeft, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(10),
            Background = Brush(resourceRoot, "BorderBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(resourceRoot, "AccentBrush"),
            },
        };
    }

    private static TextBlock CreateLanguageBadge(string text, FrameworkElement resourceRoot, double marginTop = 0)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "AccentBrush"),
            Margin = new Thickness(0, marginTop, 0, 0),
        };
    }

    private static Brush AccessBrush(string accessModifier, FrameworkElement resourceRoot) => accessModifier switch
    {
        "public" => Brush(resourceRoot, "AccentBrush"),
        "protected" => Brush(resourceRoot, "AccentHoverBrush"),
        _ => Brush(resourceRoot, "BorderBrush"),
    };

    private static Brush Brush(FrameworkElement resourceRoot, string key) =>
        (Brush)resourceRoot.FindResource(key);

    private static (string Type, string Name) SplitParameter(string parameter)
    {
        var lastSpace = parameter.LastIndexOf(' ');
        if (lastSpace <= 0 || lastSpace >= parameter.Length - 1)
        {
            return (parameter, "value");
        }

        return (parameter[..lastSpace].Trim(), parameter[(lastSpace + 1)..].Trim());
    }

    private static string? FindExceptionDescription(MethodInfo method, string exceptionType)
    {
        foreach (var tag in method.XmlDocTags)
        {
            if (!tag.Key.StartsWith("exception:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keyType = tag.Key["exception:".Length..];
            if (string.Equals(keyType, exceptionType, StringComparison.OrdinalIgnoreCase)
                || keyType.EndsWith(exceptionType, StringComparison.OrdinalIgnoreCase))
            {
                return tag.Value;
            }
        }

        return null;
    }

    private static bool SuggestsRiskyOperations(string methodName) =>
        RiskyMethodKeywords.Any(keyword =>
            methodName.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static string? GetTypeHint(string type)
    {
        var simple = type.Trim();
        if (simple.EndsWith('?'))
        {
            simple = simple[..^1];
        }

        if (string.Equals(simple, "void", StringComparison.OrdinalIgnoreCase))
        {
            return "No value";
        }

        if (string.Equals(simple, "string", StringComparison.OrdinalIgnoreCase)
            || string.Equals(simple, "String", StringComparison.Ordinal))
        {
            return "Text value";
        }

        if (simple is "int" or "long" or "Int32" or "Int64" or "short" or "Int16"
            or "uint" or "UInt32" or "ulong" or "UInt64")
        {
            return "Whole number";
        }

        if (string.Equals(simple, "bool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(simple, "Boolean", StringComparison.Ordinal))
        {
            return "True or False";
        }

        if (simple.StartsWith("List<", StringComparison.Ordinal)
            || simple.StartsWith("IEnumerable<", StringComparison.Ordinal)
            || simple.StartsWith("ICollection<", StringComparison.Ordinal)
            || simple.StartsWith("IList<", StringComparison.Ordinal))
        {
            return "A collection of items";
        }

        if (PrimitiveTypeNames.Contains(simple))
        {
            return null;
        }

        return "Custom type — see class definition";
    }

    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "string", "int", "bool", "void", "object", "char", "byte", "sbyte",
        "short", "ushort", "uint", "long", "ulong", "float", "double", "decimal",
        "nint", "nuint", "dynamic", "var",
        "String", "Int32", "Boolean", "Object", "Char", "Byte", "Int16", "Int64",
        "Double", "Single", "Decimal", "Void",
    };

    private static string DescribeCategoryLabel(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "[GUI Logic]",
        CodeCategory.Utility => "[Utility]",
        _ => "[Business Logic]",
    };
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

using MethodDetailContext = CodeLensAI.Shared.Structural.MethodDetailContext;

namespace CodeLensAI.UI.Renderers;

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

    public static void RenderClass(StackPanel host, ClassInfo classInfo, FrameworkElement resourceRoot, Action<MethodInfo>? onMethodClicked)
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
        host.Children.Add(CreateSummaryOrPlaceholder(classInfo.XmlSummary, isMethod: false, resourceRoot));

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
        var prePostHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var designOrganicHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        var designAiHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        PopulatePrePostConditionsCard(prePostHost, organicAnalysis, resourceRoot);
        PopulateScideStructuralSection(designOrganicHost, context, resourceRoot);
        PopulateExecutionStepsSection(designOrganicHost, organicAnalysis.ExecutionSteps, resourceRoot);
        PopulateInferenceDesignSection(designOrganicHost, organicAnalysis, resourceRoot);
        designAiHost.Children.Add(CreateItalicPlaceholder("Click Generate Analysis to populate.", resourceRoot));

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

        // Generate Analysis button
        var analysisRow = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20) };
        var generateAnalysisBtn = CreateRegenerateButton("Generate Analysis", () => { }, resourceRoot);
        analysisRow.Children.Add(generateAnalysisBtn);
        host.Children.Add(analysisRow);

        generateAnalysisBtn.Click += (_, _) =>
        {
            if (explanationService is null || !explanationService.IsReady)
            {
                designAiHost.Children.Clear();
                designAiHost.Children.Add(CreateItalicPlaceholder(GetAiUnavailableMessage(explanationService), resourceRoot));
                return;
            }

            generateAnalysisBtn.IsEnabled = false;
            generateAnalysisBtn.Content = "Generating…";
            designAiHost.Children.Clear();
            designAiHost.Children.Add(CreateItalicPlaceholder("Generating design requirements…", resourceRoot));

            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                var design = svc.GenerateDesignConstraints(m);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    designAiHost.Children.Clear();
                    designAiHost.Children.Add(new Border
                    {
                        Height = 1,
                        Background = Brush(resourceRoot, "BorderBrush"),
                        Margin = new Thickness(0, 12, 0, 10),
                    });
                    designAiHost.Children.Add(CreateCapsLabel("AI ENHANCEMENT", resourceRoot));
                    PopulateBulletList(designAiHost, design, resourceRoot);
                    generateAnalysisBtn.Content = "Regenerate Analysis";
                    generateAnalysisBtn.IsEnabled = true;
                });
            });
        };

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
                $"Signature: {string.Join(" ", context.ScideModifiers)}",
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

        if (!string.IsNullOrEmpty(method.CachedAiBriefDescription))
        {
            aiBriefText = CreateBodyText(method.CachedAiBriefDescription, resourceRoot, marginTop: 8);
            stack.Children.Add(aiBriefText);
        }
        else
        {
            aiBriefText = CreateBodyText("Resolving best available summary…", resourceRoot, marginTop: 8);
            stack.Children.Add(aiBriefText);

            var briefTextBlock = aiBriefText;
            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                var text = svc is { IsReady: true }
                    ? svc.GenerateBriefDescription(m)
                    : GetAiUnavailableMessage(svc);

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    briefTextBlock.Text = text;

                    // Only cache real model output — bracketed strings are error/unavailable
                    // messages, and caching those would hide the AI once it becomes ready.
                    if (svc is { IsReady: true } && !text.StartsWith('['))
                    {
                        m.CachedAiBriefDescription = text;
                    }
                });
            });
        }

        return WrapInCard(stack, resourceRoot);
    }

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
            stack.Children.Add(CreateMutedText("No locals detected.", resourceRoot, marginTop: 4));
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
            stack.Children.Add(CreateCapsLabel("CLASS PROPERTIES (SCIDE)", resourceRoot));
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
        IReadOnlyList<ExecutionStep> steps,
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
        host.Children.Add(CreateCapsLabel("STRUCTURAL ANALYSIS (SCIDE)", resourceRoot));

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
                stack.Children.Add(CreateLabeledRow("SCIDE inheritance",
                    string.Join(", ", inherits),
                    resourceRoot, marginTop: 6));
            }
        }

        return WrapInCard(stack, resourceRoot);
    }

    private static Border BuildAiExplanationCard(
        MethodInfo method,
        FrameworkElement resourceRoot,
        IExplanationService? explanationService,
        string? existingSummary,
        TextBlock? aiBriefText)
    {
        var stack = new StackPanel();
        AddCardHeader(stack, "AI Explanation", resourceRoot)
            .Children.Add(CreateBadge("AI", "WarningBrush", resourceRoot, marginLeft: 8));

        var explanationText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            FontSize = 13,
            LineHeight = 22,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 12, 0, 0),
        };
        stack.Children.Add(explanationText);

        var followUpHeader = new TextBlock
        {
            Text = "Follow-up Questions",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            Margin = new Thickness(0, 16, 0, 8),
        };
        stack.Children.Add(followUpHeader);

        var questionButtonsPanel = new StackPanel();
        stack.Children.Add(questionButtonsPanel);

        stack.Children.Add(new TextBlock
        {
            Text = "Add your own question",
            FontSize = 11,
            Opacity = 0.55,
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            Margin = new Thickness(0, 10, 0, 4),
        });

        var addQuestionRow = new DockPanel { LastChildFill = true };
        var addQuestionBox = new TextBox
        {
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(resourceRoot, "SurfaceBrush"),
            Foreground = Brush(resourceRoot, "TextPrimaryBrush"),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
        };
        var addQuestionBtn = new Button
        {
            Content = "Add",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brush(resourceRoot, "PrimaryBrush"),
            Foreground = Brush(resourceRoot, "SurfaceBrush"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        DockPanel.SetDock(addQuestionBtn, Dock.Right);
        addQuestionRow.Children.Add(addQuestionBtn);
        addQuestionRow.Children.Add(addQuestionBox);
        stack.Children.Add(addQuestionRow);

        var generateBtn = CreateRegenerateButton("Generate AI Explanation", () => { }, resourceRoot, marginTop: 12);
        stack.Children.Add(generateBtn);

        IMethodConversationSession? session = null;

        string GetSeedExplanation()
        {
            if (!string.IsNullOrWhiteSpace(existingSummary))
                return existingSummary;

            if (aiBriefText is not null && !string.IsNullOrWhiteSpace(aiBriefText.Text) &&
                !aiBriefText.Text.StartsWith("Generating", StringComparison.Ordinal))
                return aiBriefText.Text;

            return "No prior explanation available.";
        }

        var isAsking = false;

        void SetQuestionButtonsEnabled(bool enabled)
        {
            foreach (var child in questionButtonsPanel.Children)
            {
                if (child is Button btn)
                {
                    btn.IsEnabled = enabled;
                }
            }
        }

        void AskQuestion(string question, Button clickedButton)
        {
            // Guard against double-clicks/rapid re-clicks firing overlapping requests at the LLM.
            if (isAsking) return;

            if (explanationService is null || !explanationService.IsReady)
            {
                explanationText.Text = GetAiUnavailableMessage(explanationService);
                explanationText.Visibility = Visibility.Visible;
                return;
            }

            session ??= explanationService.StartMethodConversation(method, GetSeedExplanation());

            isAsking = true;
            SetQuestionButtonsEnabled(false);
            generateBtn.IsEnabled = false;
            var originalContent = clickedButton.Content;
            clickedButton.Content = "Thinking…";
            explanationText.Text = "Generating answer…";
            explanationText.Foreground = Brush(resourceRoot, "TextSecondaryBrush");
            explanationText.Visibility = Visibility.Visible;

            var activeSession = session;
            Task.Run(() =>
            {
                string answer;
                try
                {
                    answer = activeSession.Ask(question);
                }
                catch (Exception ex)
                {
                    answer = $"[Failed to get an answer: {ex.Message}]";
                }

                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    explanationText.Text = answer;
                    explanationText.Foreground = Brush(resourceRoot, "TextPrimaryBrush");
                    clickedButton.Content = originalContent;
                    generateBtn.IsEnabled = true;
                    isAsking = false;
                    SetQuestionButtonsEnabled(true);
                });
            });
        }

        void RefreshQuestionButtons()
        {
            questionButtonsPanel.Children.Clear();

            var allQuestions = GetFollowUpQuestions(method).Concat(CustomFaqStore.Load());
            foreach (var q in allQuestions)
            {
                var qBtn = new Button
                {
                    Content = q,
                    Padding = new Thickness(14, 8, 14, 8),
                    Margin = new Thickness(0, 4, 0, 0),
                    Background = Brush(resourceRoot, "SurfaceBrush"),
                    Foreground = Brush(resourceRoot, "PrimaryBrush"),
                    BorderBrush = Brush(resourceRoot, "BorderBrush"),
                    BorderThickness = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    FontSize = 12,
                };
                var capturedQ = q;
                qBtn.Click += (_, _) => AskQuestion(capturedQ, qBtn);
                questionButtonsPanel.Children.Add(qBtn);
            }
        }

        RefreshQuestionButtons();

        void AddCustomQuestion()
        {
            var question = addQuestionBox.Text.Trim();
            if (question.Length == 0) return;

            CustomFaqStore.Add(question);
            addQuestionBox.Text = string.Empty;
            RefreshQuestionButtons();
        }

        addQuestionBtn.Click += (_, _) => AddCustomQuestion();
        addQuestionBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                AddCustomQuestion();
            }
        };

        generateBtn.Click += (_, _) =>
        {
            if (isAsking) return;

            if (explanationService is null || !explanationService.IsReady)
            {
                explanationText.Text = GetAiUnavailableMessage(explanationService);
                explanationText.Visibility = Visibility.Visible;
                return;
            }

            isAsking = true;
            SetQuestionButtonsEnabled(false);
            generateBtn.IsEnabled = false;
            generateBtn.Content = "Generating…";
            var svc = explanationService;
            var m = method;
            Task.Run(() =>
            {
                var text = svc.ExplainMethod(m);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    explanationText.Text = text;
                    explanationText.Foreground = Brush(resourceRoot, "TextPrimaryBrush");
                    explanationText.Visibility = Visibility.Visible;
                    generateBtn.Content = "Regenerate";
                    generateBtn.IsEnabled = true;
                    session = null;
                    isAsking = false;
                    SetQuestionButtonsEnabled(true);
                });
            });
        };

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
        yield return $"What happens if the inputs to `{method.Name}` are null or out of range?";
        yield return $"How is `{method.Name}` called in the rest of the codebase?";
        if (method.ThrownExceptions.Count > 0)
            yield return $"When exactly is {method.ThrownExceptions[0]} thrown?";
        yield return $"Can `{method.Name}` be made asynchronous?";
    }

    // ── UI primitives ─────────────────────────────────────────────────────────

    private static Border WrapInCard(UIElement content, FrameworkElement resourceRoot)
    {
        return new Border
        {
            Background = Brush(resourceRoot, "SurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            BorderBrush = Brush(resourceRoot, "BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                Opacity = 0.15,
                ShadowDepth = 2,
            },
            Child = content,
        };
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

        var dot = new Ellipse { Width = 8, Height = 8, Fill = AccessBrush(method.AccessModifier, resourceRoot), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(dot, 0);

        var namePanel = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        namePanel.Inlines.Add(new Run(method.Name) { FontWeight = FontWeights.SemiBold, Foreground = Brush(resourceRoot, "TextPrimaryBrush") });
        namePanel.Inlines.Add(new Run($"  {method.ReturnType}") { Foreground = Brush(resourceRoot, "TextSecondaryBrush") });
        Grid.SetColumn(namePanel, 1);

        var paramCount = CreateMutedText($"{method.Parameters.Count} param{(method.Parameters.Count == 1 ? "" : "s")}", resourceRoot);
        Grid.SetColumn(paramCount, 2);

        grid.Children.Add(dot); grid.Children.Add(namePanel); grid.Children.Add(paramCount);
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

    private static UIElement CreateSummaryOrPlaceholder(string? summary, bool isMethod, FrameworkElement resourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            return CreateBodyText(summary, resourceRoot);
        return CreateItalicPlaceholder(
            isMethod
                ? "No documentation comment found. Add a /// <summary> comment above this method."
                : "No documentation comment found. Add a /// <summary> comment above this class.",
            resourceRoot);
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

    private static string? FindExceptionDescription(MethodInfo method, string exceptionType)
    {
        foreach (var tag in method.XmlDocTags)
        {
            if (!tag.Key.StartsWith("exception:", StringComparison.OrdinalIgnoreCase)) continue;
            var keyType = tag.Key["exception:".Length..];
            if (string.Equals(keyType, exceptionType, StringComparison.OrdinalIgnoreCase)
                || keyType.EndsWith(exceptionType, StringComparison.OrdinalIgnoreCase))
                return tag.Value;
        }
        return null;
    }

    private static string DescribeCategoryLabel(CodeCategory category) => category switch
    {
        CodeCategory.GuiLogic => "GUI Logic",
        CodeCategory.Utility  => "Utility",
        _                     => "Business Logic",
    };
}
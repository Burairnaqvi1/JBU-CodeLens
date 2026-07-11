using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeLensAI.Core.Analysis;

/// <summary>
/// Builds numbered execution steps from Roslyn AST analysis (C#) or MethodInfo fields (C++).
/// </summary>
public sealed class ExecutionFlowAnalyzer
{
    private static readonly ExecutionStepKind[] PhaseOrder =
    [
        ExecutionStepKind.Validation,
        ExecutionStepKind.Initialization,
        ExecutionStepKind.LoopProcessing,
        ExecutionStepKind.Calculation,
        ExecutionStepKind.StateUpdate,
        ExecutionStepKind.ExternalCall,
        ExecutionStepKind.Delegation,
        ExecutionStepKind.FileOperation,
        ExecutionStepKind.DatabaseOperation,
        ExecutionStepKind.ConsoleOutput,
        ExecutionStepKind.ExceptionHandling,
        ExecutionStepKind.ReturnResult,
    ];

    private static readonly HashSet<string> PersistMethodNames = new(StringComparer.Ordinal)
    {
        "Save", "SaveAsync", "Submit", "SubmitAsync", "Persist",
    };

    private static readonly HashSet<string> DispatchMethodNames = new(StringComparer.Ordinal)
    {
        "Send", "SendAsync", "Publish", "PublishAsync", "Dispatch",
    };

    private static readonly HashSet<string> MathMethodNames = new(StringComparer.Ordinal)
    {
        "Sqrt", "Pow", "Abs", "Round", "Floor", "Ceiling", "Log", "Sin", "Cos", "Tan", "Exp",
    };

    private static readonly HashSet<string> AccumulatorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "result", "output", "total", "sum", "value",
    };

    public IReadOnlyList<ExecutionStep> Analyze(MethodAnalysisContext context)
    {
        var rawSteps = AnalyzeRawSteps(context);
        var merged = MergeConsecutiveSteps(rawSteps);
        var sorted = SortByPhase(merged);
        var capped = CapSteps(sorted, context.Method);
        return NumberSteps(capped);
    }

    private static List<RawStep> AnalyzeRawSteps(MethodAnalysisContext context)
    {
        var method = context.Method;
        if (method.SyntaxNode is MethodDeclarationSyntax methodDeclaration)
        {
            return AnalyzeCSharpMethod(methodDeclaration, method);
        }

        if (LanguageFileExtensions.IsCSharpFile(method.ParentClass?.SourceFilePath ?? string.Empty))
        {
            var parsed = TryParseCSharpMethod(method);
            if (parsed is not null)
            {
                return AnalyzeCSharpMethod(parsed, method);
            }
        }

        if (method.XmlDocTags.TryGetValue("sourceCode", out var cppSource) &&
            !string.IsNullOrWhiteSpace(cppSource))
        {
            var sourceSteps = AnalyzeCppFromSource(method, cppSource);
            if (sourceSteps.Count > 0)
            {
                return sourceSteps;
            }
        }

        return AnalyzeCppFallback(method);
    }

    /// <summary>
    /// Builds execution steps for C/C++ (or any method whose body is available as raw source but
    /// cannot be parsed as a C# AST) by scanning the source text for the same constructs the
    /// C# analyzer recognizes: guards, loops, calculations, calls, side effects, and returns.
    /// </summary>
    private static List<RawStep> AnalyzeCppFromSource(MethodInfo method, string source)
    {
        var steps = new List<RawStep>();

        foreach (Match match in Regex.Matches(source, @"if\s*\(([^)]*)\)"))
        {
            var window = source.Substring(match.Index, Math.Min(160, source.Length - match.Index));
            if (!window.Contains("throw", StringComparison.Ordinal))
            {
                continue;
            }

            steps.Add(new RawStep(match.Index, ExecutionStepKind.Validation, DescribeCppGuard(match.Groups[1].Value)));
        }

        foreach (Match match in Regex.Matches(source, @"\bfor\s*\(|\bwhile\s*\("))
        {
            steps.Add(new RawStep(
                match.Index,
                ExecutionStepKind.LoopProcessing,
                "Iterate over the elements to process each item sequentially"));
        }

        foreach (var local in method.LocalVariables.Where(v => !string.IsNullOrWhiteSpace(v.InitialValue)))
        {
            var index = source.IndexOf(local.Name, StringComparison.Ordinal);
            var description = AccumulatorNames.Contains(local.Name)
                ? $"Initialize {local.Name} to accumulate the computed result"
                : $"Initialize {local.Name} for use in the processing logic";
            steps.Add(new RawStep(index < 0 ? 0 : index, ExecutionStepKind.Initialization, description));
        }

        foreach (Match match in Regex.Matches(source, @"([A-Za-z_]\w*)\s*(?:\+=|\-=|\*=|/=)"))
        {
            steps.Add(new RawStep(
                match.Index,
                ExecutionStepKind.Calculation,
                $"Accumulate the running total into {match.Groups[1].Value}"));
        }

        foreach (Match match in Regex.Matches(source, @"([A-Za-z_]\w*)\s*=\s*(?![=])[^;]*[-+*/](?!>)[^;]*;"))
        {
            steps.Add(new RawStep(
                match.Index,
                ExecutionStepKind.Calculation,
                $"Calculate the result and store it in {match.Groups[1].Value}"));
        }

        foreach (var field in method.ParentClass?.Fields ?? [])
        {
            if (!SourcePatternHelpers.IsWrittenInSource(source, field.Name))
            {
                continue;
            }

            var index = source.IndexOf(field.Name, StringComparison.Ordinal);
            steps.Add(new RawStep(
                index < 0 ? 0 : index,
                ExecutionStepKind.StateUpdate,
                $"Update the {field.Name} field to reflect this operation"));
            break;
        }

        foreach (Match match in Regex.Matches(source, @"std::cout|std::cerr|\bprintf\s*\("))
        {
            steps.Add(new RawStep(
                match.Index,
                ExecutionStepKind.ConsoleOutput,
                "Output information to the console during execution"));
        }

        foreach (Match match in Regex.Matches(source, @"\b(?:ifstream|ofstream|fstream)\b|\bfopen\s*\("))
        {
            steps.Add(new RawStep(
                match.Index,
                ExecutionStepKind.FileOperation,
                "Perform the required file system operation"));
        }

        foreach (Match match in Regex.Matches(source, @"([A-Za-z_]\w*)\s*(?:\.|->)\s*([A-Za-z_]\w*)\s*\("))
        {
            var invokedName = match.Groups[2].Value;
            if (PersistMethodNames.Contains(invokedName) ||
                invokedName is "SaveChanges" or "ExecuteNonQuery" or "ExecuteScalar" or "ExecuteReader")
            {
                steps.Add(new RawStep(match.Index, ExecutionStepKind.DatabaseOperation,
                    "Persist the processed data to the storage layer"));
            }
            else if (DispatchMethodNames.Contains(invokedName))
            {
                steps.Add(new RawStep(match.Index, ExecutionStepKind.Delegation,
                    "Dispatch the result to the target destination"));
            }
            else
            {
                steps.Add(new RawStep(match.Index, ExecutionStepKind.Delegation,
                    $"Delegate the {invokedName} processing to the responsible component"));
            }
        }

        var tryMatch = Regex.Match(source, @"\btry\b\s*\{");
        if (tryMatch.Success && Regex.IsMatch(source, @"\bcatch\b\s*\("))
        {
            steps.Add(new RawStep(
                tryMatch.Index,
                ExecutionStepKind.ExceptionHandling,
                "Handle potential exceptions internally to ensure graceful recovery"));
        }

        if (!IsVoidReturn(method.ReturnType))
        {
            var returnMatch = Regex.Match(source, @"\breturn\b\s*([^;]*);");
            var expressionName = returnMatch.Success ? ExtractFirstIdentifier(returnMatch.Groups[1].Value) : null;
            var position = returnMatch.Success ? returnMatch.Index : source.Length;
            steps.Add(new RawStep(
                position,
                ExecutionStepKind.ReturnResult,
                BuildReturnDescription(method.ReturnType, method.Name, expressionName)));
        }

        return steps;
    }

    private static string DescribeCppGuard(string condition)
    {
        var name = ExtractFirstIdentifier(condition) ?? "input";

        if (condition.Contains("nullptr", StringComparison.Ordinal) ||
            condition.Contains("NULL", StringComparison.Ordinal))
        {
            return $"Validate that {name} is not null before proceeding";
        }

        if (condition.Contains(".empty()", StringComparison.Ordinal))
        {
            return $"Validate that {name} is not empty before proceeding";
        }

        if (Regex.IsMatch(condition, @"<=\s*0") || Regex.IsMatch(condition, @"==\s*0"))
        {
            return $"Verify that {name} is non-zero to prevent invalid operations";
        }

        if (Regex.IsMatch(condition, @"<\s*0"))
        {
            return $"Ensure that {name} is a positive value as required";
        }

        return "Validate the input conditions before executing the core logic";
    }

    private static string? ExtractFirstIdentifier(string text)
    {
        var match = Regex.Match(text, @"[A-Za-z_]\w*");
        return match.Success ? match.Value : null;
    }

    private static MethodDeclarationSyntax? TryParseCSharpMethod(MethodInfo method)
    {
        if (!method.XmlDocTags.TryGetValue("sourceCode", out var source) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault();
    }

    private static List<RawStep> AnalyzeCSharpMethod(MethodDeclarationSyntax methodDeclaration, MethodInfo method)
    {
        var steps = new List<RawStep>();
        var fieldNames = method.ParentClass?.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal) ?? [];

        foreach (var node in methodDeclaration.DescendantNodes())
        {
            switch (node)
            {
                case IfStatementSyntax ifStatement when ContainsThrow(ifStatement.Statement):
                    AddValidationStep(steps, ifStatement);
                    break;
                case LocalDeclarationStatementSyntax localDeclaration:
                    AddInitializationStep(steps, localDeclaration);
                    break;
                case ForEachStatementSyntax forEach:
                    AddLoopStep(steps, forEach, isNested: HasNestedLoop(forEach));
                    break;
                case ForStatementSyntax forStatement:
                    AddForLoopStep(steps, forStatement, isNested: HasNestedLoop(forStatement));
                    break;
                case WhileStatementSyntax whileStatement:
                    AddWhileLoopStep(steps, whileStatement, isNested: HasNestedLoop(whileStatement));
                    break;
                case AssignmentExpressionSyntax assignment:
                    AddCalculationOrStateStep(steps, assignment, fieldNames);
                    break;
                case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.PostIncrementExpression) ||
                                                              postfix.IsKind(SyntaxKind.PostDecrementExpression):
                    AddPostfixStateStep(steps, postfix, fieldNames);
                    break;
                case PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                                                             prefix.IsKind(SyntaxKind.PreDecrementExpression):
                    AddPrefixStateStep(steps, prefix, fieldNames);
                    break;
                case TryStatementSyntax tryStatement:
                    AddExceptionHandlingStep(steps, tryStatement);
                    break;
                case InvocationExpressionSyntax invocation:
                    AddInvocationStep(steps, invocation);
                    break;
                case ReturnStatementSyntax returnStatement:
                    AddReturnStep(steps, returnStatement, method);
                    break;
            }
        }

        return steps;
    }

    private static List<RawStep> AnalyzeCppFallback(MethodInfo method)
    {
        var steps = new List<RawStep>();
        var issues = method.OperationalLimits;

        if (method.ThrownExceptions.Count > 0)
        {
            steps.Add(new RawStep(
                0,
                ExecutionStepKind.Validation,
                $"Validate the input preconditions — throws {method.ThrownExceptions[0]} if violated"));
        }

        if (issues.Any(i => i.Contains("null", StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(new RawStep(
                1,
                ExecutionStepKind.Validation,
                "Validate that all reference inputs are non-null before proceeding"));
        }

        if (issues.Any(i => i.Contains("zero", StringComparison.OrdinalIgnoreCase) ||
                             i.Contains("division", StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(new RawStep(
                2,
                ExecutionStepKind.Validation,
                "Verify the divisor is non-zero to prevent arithmetic exceptions"));
        }

        if (issues.Any(i => i.Contains("bounds", StringComparison.OrdinalIgnoreCase) ||
                             i.Contains("index", StringComparison.OrdinalIgnoreCase)))
        {
            steps.Add(new RawStep(
                3,
                ExecutionStepKind.Validation,
                "Validate the index is within the bounds of the target collection"));
        }

        var initializedLocal = method.LocalVariables.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.InitialValue));
        if (initializedLocal is not null)
        {
            steps.Add(new RawStep(
                4,
                ExecutionStepKind.Initialization,
                $"Initialize {initializedLocal.Name} for use in the processing logic"));
        }

        var field = method.ParentClass?.Fields.FirstOrDefault();
        if (field is not null && method.XmlDocTags.TryGetValue("sourceCode", out var source) &&
            !string.IsNullOrEmpty(source) &&
            SourcePatternHelpers.IsWrittenInSource(source, field.Name))
        {
            steps.Add(new RawStep(
                5,
                ExecutionStepKind.StateUpdate,
                $"Update the {field.Name} field to reflect this operation"));
        }

        if (!IsVoidReturn(method.ReturnType))
        {
            steps.Add(new RawStep(
                6,
                ExecutionStepKind.ReturnResult,
                BuildReturnDescription(method.ReturnType, method.Name, null)));
        }

        return steps;
    }

    private static void AddValidationStep(List<RawStep> steps, IfStatementSyntax ifStatement)
    {
        var position = ifStatement.SpanStart;
        var condition = ifStatement.Condition;

        if (condition is InvocationExpressionSyntax invocation &&
            invocation.Expression.ToString().Contains("IsNullOrEmpty", StringComparison.Ordinal))
        {
            var argumentName = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? "input";
            steps.Add(new RawStep(
                position,
                ExecutionStepKind.Validation,
                $"Validate that {argumentName} is not empty or whitespace"));
            return;
        }

        if (condition is InvocationExpressionSyntax whiteSpaceInvocation &&
            whiteSpaceInvocation.Expression.ToString().Contains("IsNullOrWhiteSpace", StringComparison.Ordinal))
        {
            var argumentName = whiteSpaceInvocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.ToString() ?? "input";
            steps.Add(new RawStep(
                position,
                ExecutionStepKind.Validation,
                $"Validate that {argumentName} is not empty or whitespace"));
            return;
        }

        if (condition is BinaryExpressionSyntax binary)
        {
            if (binary.Right is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression })
            {
                var name = ExtractIdentifierName(binary.Left) ?? "input";
                steps.Add(new RawStep(
                    position,
                    ExecutionStepKind.Validation,
                    $"Validate that {name} is not null before proceeding"));
                return;
            }

            if (IsZeroLiteral(binary.Right) &&
                (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.LessThanOrEqualExpression)))
            {
                var name = ExtractIdentifierName(binary.Left) ?? "input";
                steps.Add(new RawStep(
                    position,
                    ExecutionStepKind.Validation,
                    $"Verify that {name} is non-zero to prevent invalid operations"));
                return;
            }

            if (binary.IsKind(SyntaxKind.LessThanExpression) &&
                binary.Right is LiteralExpressionSyntax { Token.ValueText: "0" })
            {
                var name = ExtractIdentifierName(binary.Left) ?? "input";
                steps.Add(new RawStep(
                    position,
                    ExecutionStepKind.Validation,
                    $"Ensure that {name} is a positive value as required"));
                return;
            }
        }

        steps.Add(new RawStep(
            position,
            ExecutionStepKind.Validation,
            "Validate the input conditions before executing the core logic"));
    }

    private static void AddInitializationStep(List<RawStep> steps, LocalDeclarationStatementSyntax localDeclaration)
    {
        foreach (var variable in localDeclaration.Declaration.Variables)
        {
            var variableName = variable.Identifier.Text;
            var description = AccumulatorNames.Contains(variableName)
                ? $"Initialize {variableName} to accumulate the computed result"
                : $"Initialize {variableName} for use in the processing logic";

            steps.Add(new RawStep(localDeclaration.SpanStart, ExecutionStepKind.Initialization, description));
        }
    }

    private static void AddLoopStep(List<RawStep> steps, ForEachStatementSyntax forEach, bool isNested)
    {
        var collectionName = forEach.Expression.ToString();
        var description = isNested
            ? "Traverse the nested structure to process all contained elements"
            : $"Process each element in {collectionName} to produce the result";

        steps.Add(new RawStep(forEach.SpanStart, ExecutionStepKind.LoopProcessing, description));
    }

    private static void AddForLoopStep(List<RawStep> steps, ForStatementSyntax forStatement, bool isNested)
    {
        var description = isNested
            ? "Traverse the nested structure to process all contained elements"
            : "Iterate over the index range to process each element sequentially";

        steps.Add(new RawStep(forStatement.SpanStart, ExecutionStepKind.LoopProcessing, description));
    }

    private static void AddWhileLoopStep(List<RawStep> steps, WhileStatementSyntax whileStatement, bool isNested)
    {
        var description = isNested
            ? "Traverse the nested structure to process all contained elements"
            : "Continue processing until the exit condition is satisfied";

        steps.Add(new RawStep(whileStatement.SpanStart, ExecutionStepKind.LoopProcessing, description));
    }

    private static void AddCalculationOrStateStep(
        List<RawStep> steps,
        AssignmentExpressionSyntax assignment,
        HashSet<string> fieldNames)
    {
        var targetName = ExtractAssignmentTargetName(assignment.Left);
        if (!string.IsNullOrEmpty(targetName) && fieldNames.Contains(targetName))
        {
            steps.Add(new RawStep(
                assignment.SpanStart,
                ExecutionStepKind.StateUpdate,
                $"Update the {targetName} field with the new computed value"));
            return;
        }

        if (assignment.Right is BinaryExpressionSyntax or InvocationExpressionSyntax)
        {
            var variableName = targetName ?? "result";
            var description = assignment.IsKind(SyntaxKind.AddAssignmentExpression) ||
                              assignment.IsKind(SyntaxKind.SubtractAssignmentExpression) ||
                              assignment.IsKind(SyntaxKind.MultiplyAssignmentExpression)
                ? $"Accumulate the running total into {variableName}"
                : $"Calculate the result and store it in {variableName}";

            steps.Add(new RawStep(assignment.SpanStart, ExecutionStepKind.Calculation, description));
        }
    }

    private static void AddPostfixStateStep(
        List<RawStep> steps,
        PostfixUnaryExpressionSyntax postfix,
        HashSet<string> fieldNames)
    {
        var fieldName = ExtractIdentifierName(postfix.Operand);
        if (fieldName is null || !fieldNames.Contains(fieldName))
        {
            return;
        }

        steps.Add(new RawStep(
            postfix.SpanStart,
            ExecutionStepKind.StateUpdate,
            $"Increment the {fieldName} counter to record this operation"));
    }

    private static void AddPrefixStateStep(
        List<RawStep> steps,
        PrefixUnaryExpressionSyntax prefix,
        HashSet<string> fieldNames)
    {
        var fieldName = ExtractIdentifierName(prefix.Operand);
        if (fieldName is null || !fieldNames.Contains(fieldName))
        {
            return;
        }

        steps.Add(new RawStep(
            prefix.SpanStart,
            ExecutionStepKind.StateUpdate,
            $"Increment the {fieldName} counter to record this operation"));
    }

    private static void AddExceptionHandlingStep(List<RawStep> steps, TryStatementSyntax tryStatement)
    {
        var hasRethrow = tryStatement.Catches.Any(c => ContainsThrow(c.Block));
        var description = hasRethrow
            ? "Catch and re-throw exceptions after performing any necessary cleanup"
            : "Handle potential exceptions internally to ensure graceful recovery";

        steps.Add(new RawStep(tryStatement.SpanStart, ExecutionStepKind.ExceptionHandling, description));
    }

    private static void AddInvocationStep(List<RawStep> steps, InvocationExpressionSyntax invocation)
    {
        var classified = ClassifyInvocation(invocation);
        if (classified is null)
        {
            return;
        }

        steps.Add(new RawStep(invocation.SpanStart, classified.Value.Kind, classified.Value.Description));
    }

    private static void AddReturnStep(List<RawStep> steps, ReturnStatementSyntax returnStatement, MethodInfo method)
    {
        if (IsVoidReturn(method.ReturnType))
        {
            return;
        }

        var expressionName = ExtractIdentifierName(returnStatement.Expression);
        steps.Add(new RawStep(
            returnStatement.SpanStart,
            ExecutionStepKind.ReturnResult,
            BuildReturnDescription(method.ReturnType, method.Name, expressionName)));
    }

    private static (ExecutionStepKind Kind, string Description)? ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        string receiver;
        string methodName;

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            receiver = memberAccess.Expression.ToString();
            methodName = memberAccess.Name.Identifier.Text;
        }
        else if (invocation.Expression is IdentifierNameSyntax identifierName)
        {
            receiver = identifierName.Identifier.Text;
            methodName = identifierName.Identifier.Text;
        }
        else
        {
            return null;
        }

        if (receiver == "Console")
        {
            return (ExecutionStepKind.ConsoleOutput, "Output information to the console during execution");
        }

        if (receiver is "File" or "Directory" or "Path" or "StreamReader" or "StreamWriter" or
            "FileStream" or "BinaryReader" or "BinaryWriter")
        {
            return (ExecutionStepKind.FileOperation, "Perform the required file system operation");
        }

        if (receiver.Contains("DbContext", StringComparison.Ordinal) ||
            receiver.Contains("Repository", StringComparison.Ordinal) ||
            receiver.Contains("Connection", StringComparison.Ordinal) ||
            receiver.Contains("Command", StringComparison.Ordinal) ||
            methodName is "SaveChanges" or "ExecuteNonQuery" or "ExecuteScalar" or "ExecuteReader")
        {
            return (ExecutionStepKind.DatabaseOperation, "Execute the database operation to persist the changes");
        }

        if (receiver == "Math" || MathMethodNames.Contains(methodName))
        {
            return (ExecutionStepKind.Calculation, "Apply the mathematical function to compute the precise result");
        }

        if (receiver.Contains("Logger", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
            receiver.Contains("_logger", StringComparison.OrdinalIgnoreCase) ||
            methodName is "LogInformation" or "LogError" or "LogWarning" or "LogDebug")
        {
            return (ExecutionStepKind.ExternalCall, "Record the operation details for diagnostic and audit purposes");
        }

        if (PersistMethodNames.Contains(methodName))
        {
            return (ExecutionStepKind.DatabaseOperation, "Persist the processed data to the storage layer");
        }

        if (DispatchMethodNames.Contains(methodName))
        {
            return (ExecutionStepKind.Delegation, "Dispatch the result to the target destination");
        }

        if (invocation.Expression is MemberAccessExpressionSyntax delegationAccess)
        {
            var actualMethodName = delegationAccess.Name.Identifier.Text;
            return (ExecutionStepKind.Delegation,
                $"Delegate the {actualMethodName} processing to the responsible component");
        }

        return null;
    }

    private static string BuildReturnDescription(string returnType, string methodName, string? expressionName)
    {
        if (returnType.Equals("bool", StringComparison.OrdinalIgnoreCase))
        {
            return "Return true if the operation succeeded, false otherwise";
        }

        if (returnType is "double" or "float" or "decimal")
        {
            return $"Return the computed {returnType} result to the caller";
        }

        if (returnType is "int" or "long" or "short" or "byte")
        {
            var valueName = string.IsNullOrWhiteSpace(expressionName) ? "resulting" : expressionName;
            return $"Return the {valueName} value to the caller";
        }

        if (returnType.Equals("string", StringComparison.OrdinalIgnoreCase) ||
            returnType.Contains("string", StringComparison.OrdinalIgnoreCase))
        {
            return "Return the resulting text value to the caller";
        }

        if (returnType.Contains("List", StringComparison.Ordinal) ||
            returnType.Contains("IEnumerable", StringComparison.Ordinal))
        {
            return "Return the processed collection of results to the caller";
        }

        return $"Return the {returnType} result to the caller";
    }

    private static List<RawStep> MergeConsecutiveSteps(List<RawStep> steps)
    {
        var ordered = steps.OrderBy(s => s.Position).ToList();
        var merged = new List<RawStep>();

        foreach (var step in ordered)
        {
            if (merged.Count > 0 && merged[^1].Kind == step.Kind)
            {
                continue;
            }

            merged.Add(step);
        }

        return merged;
    }

    private static List<RawStep> SortByPhase(List<RawStep> steps)
    {
        return steps
            .OrderBy(s => Array.IndexOf(PhaseOrder, s.Kind))
            .ThenBy(s => s.Position)
            .ToList();
    }

    private static List<RawStep> CapSteps(List<RawStep> steps, MethodInfo method)
    {
        if (steps.Count == 0)
        {
            return
            [
                new RawStep(
                    0,
                    ExecutionStepKind.Calculation,
                    $"Execute the {method.Name} operation using the provided inputs"),
            ];
        }

        if (steps.Count <= 8)
        {
            return steps;
        }

        var merged = new List<RawStep>();
        var coreInserted = false;

        foreach (var step in steps)
        {
            if (step.Kind is ExecutionStepKind.Calculation or ExecutionStepKind.Delegation)
            {
                if (!coreInserted)
                {
                    merged.Add(new RawStep(
                        step.Position,
                        ExecutionStepKind.Calculation,
                        "Perform the core processing logic on the provided inputs"));
                    coreInserted = true;
                }

                continue;
            }

            merged.Add(step);
        }

        if (merged.Count > 8)
        {
            var returnSteps = merged.Where(s => s.Kind == ExecutionStepKind.ReturnResult).ToList();
            var nonReturn = merged.Where(s => s.Kind != ExecutionStepKind.ReturnResult).Take(7).ToList();
            merged = nonReturn.Concat(returnSteps).ToList();
        }

        return merged;
    }

    private static IReadOnlyList<ExecutionStep> NumberSteps(List<RawStep> steps)
    {
        var numbered = new List<ExecutionStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            numbered.Add(new ExecutionStep
            {
                StepNumber = i + 1,
                Description = steps[i].Description,
                Kind = steps[i].Kind,
            });
        }

        return numbered;
    }

    private static bool ContainsThrow(StatementSyntax? statement)
    {
        if (statement is null)
        {
            return false;
        }

        return statement.DescendantNodesAndSelf().Any(n => n is ThrowStatementSyntax);
    }

    private static bool ContainsThrow(BlockSyntax block) =>
        block.DescendantNodes().Any(n => n is ThrowStatementSyntax);

    private static bool HasNestedLoop(SyntaxNode loopNode) =>
        loopNode.DescendantNodes().Any(n => n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax && n != loopNode);

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal &&
        literal.Token.ValueText == "0";

    private static string? ExtractIdentifierName(ExpressionSyntax? expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null,
        };

    private static string? ExtractAssignmentTargetName(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            _ => null,
        };

    private static bool IsVoidReturn(string returnType) =>
        returnType.Equals("void", StringComparison.OrdinalIgnoreCase);

    private sealed record RawStep(int Position, ExecutionStepKind Kind, string Description);
}

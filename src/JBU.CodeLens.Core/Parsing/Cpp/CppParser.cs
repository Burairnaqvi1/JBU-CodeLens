using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// Constrains where the runtime may load native dependencies from when it falls back to the OS
// loader. Without this, the default search order includes the current working directory, so a
// libclang.dll planted in whatever folder the user happened to launch the app from would be loaded
// in preference to the real one — arbitrary code execution in this process.
//
// SafeDirectories (LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) covers the application directory and System32,
// which is where the genuine libclang.dll lives: the UI build copies it next to the executable (see
// JBU.CodeLens.UI.csproj, CopyLibClangToAppRoot). AssemblyDirectory would also work but CA5393
// rejects it as unsafe, and SafeDirectories is the stricter option that still resolves correctly —
// verified by the CppParserTests suite, which performs real native parses through these imports.
// Applied assembly-wide because every P/Invoke in this assembly targets that app-local libclang.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace JBU.CodeLens.Core.Parsing.Cpp;

/// <summary>
/// C++ parser backed by libclang. Walks the AST to extract classes, methods, fields,
/// and Doxygen documentation into the shared <see cref="ClassInfo"/> / <see cref="MethodInfo"/> models.
/// </summary>
public class CppParser : ILanguageParser
{
    // Must be 0 (not SkipFunctionBodies) so method body AST nodes are available for source extraction and variable analysis.
    private const uint CXTranslationUnit_None = 0;

    private static readonly HashSet<string> CppNumericTypeNames = new(StringComparer.Ordinal)
    {
        "int", "long", "short", "float", "double", "size_t",
        "uint32_t", "uint64_t", "int32_t", "int64_t",
        "unsigned int", "unsigned long", "ptrdiff_t",
        "uint8_t", "uint16_t", "int8_t", "int16_t",
    };

    private static readonly HashSet<string> PrimitiveTypeNames = new(StringComparer.Ordinal)
    {
        "void", "bool", "char", "wchar_t", "char16_t", "char32_t",
        "int", "short", "long", "float", "double",
        "unsigned", "signed", "size_t", "nullptr_t",
        "int8_t", "int16_t", "int32_t", "int64_t",
        "uint8_t", "uint16_t", "uint32_t", "uint64_t",
        "string", "object", "decimal",
    };

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr clang_createIndex(int excludeDeclarationsFromPCH, int displayDiagnostics);

    // Path and argument strings are marshaled manually as UTF-8 (see ParseTranslationUnitUtf8):
    // libclang expects UTF-8, while DllImport's default string marshaling is the system ANSI
    // code page, which silently breaks any non-ASCII file path.
    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr clang_parseTranslationUnit(
        IntPtr index,
        IntPtr sourceFilename,
        IntPtr[]? commandLineArgs,
        int numCommandLineArgs,
        IntPtr unsavedFiles,
        uint numUnsavedFiles,
        uint options);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern void clang_disposeTranslationUnit(IntPtr tu);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern void clang_disposeIndex(IntPtr index);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXCursorKind clang_getCursorKind(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXString clang_getCursorSpelling(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXCursor clang_getTranslationUnitCursor(IntPtr tu);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint clang_visitChildren(
        CXCursor parent,
        CXCursorVisitor visitor,
        IntPtr clientData);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr clang_getCString(CXString cxString);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern void clang_disposeString(CXString cxString);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXType clang_getCursorType(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXString clang_getTypeSpelling(CXType type);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXType clang_getResultType(CXType type);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clang_Cursor_getNumArguments(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXCursor clang_Cursor_getArgument(CXCursor cursor, uint index);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CX_CXXAccessSpecifier clang_getCXXAccessSpecifier(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXString clang_Cursor_getRawCommentText(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXSourceLocation clang_getCursorLocation(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern int clang_Location_isFromMainFile(CXSourceLocation location);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXSourceRange clang_getCursorExtent(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXSourceLocation clang_getRangeStart(CXSourceRange range);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXSourceLocation clang_getRangeEnd(CXSourceRange range);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern void clang_getSpellingLocation(
        CXSourceLocation location,
        out IntPtr file,
        out uint line,
        out uint column,
        out uint offset);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXCursor clang_getCursorSemanticParent(CXCursor cursor);

    [DllImport("libclang", CallingConvention = CallingConvention.Cdecl)]
    private static extern CXString clang_getCursorDisplayName(CXCursor cursor);

    public ParseResult Parse(string filePath)
    {
        if (!TryValidatePath(filePath, out var invalid))
        {
            return invalid;
        }

        byte[] fileBytes;
        try
        {
            fileBytes = File.ReadAllBytes(filePath);
        }
        catch
        {
            // Parsing can still proceed with an empty buffer for source extraction.
            fileBytes = [];
        }

        return ParseWithClang(filePath, fileBytes);
    }

    // Generous per-file budget: a normal libclang parse takes well under a second; only a
    // pathological input (or a libclang hang) exceeds this.
    private static readonly TimeSpan NativeParseTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<ParseResult> ParseAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!TryValidatePath(filePath, out var invalid))
        {
            return invalid;
        }

        byte[] fileBytes;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            fileBytes = [];
        }

        return await RunNativeParseWithWatchdogAsync(filePath, fileBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the native libclang parse on a dedicated background thread with a watchdog. A call
    /// into native code cannot be aborted, so on timeout (or cancellation mid-parse) the thread
    /// is abandoned — it keeps running detached and its index/TU leak if it never returns — and
    /// the file is reported as failed instead of hanging or stalling the whole scan. That leak
    /// is bounded to genuinely pathological files and is the price of keeping the app alive.
    /// </summary>
    private static async Task<ParseResult> RunNativeParseWithWatchdogAsync(
        string filePath,
        byte[] fileBytes,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ParseResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ParseWithClang(filePath, fileBytes));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "libclang-parse",
        };
        thread.Start();

        // The watchdog timer is tied to its own source so it can be stopped the moment the parse
        // wins. Without that, every parsed file leaves a timer armed for the full timeout: a scan
        // of a few hundred C++ files would hold hundreds of them alive at once for no reason.
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var finished = await Task.WhenAny(
            completion.Task,
            Task.Delay(NativeParseTimeout, watchdogCts.Token)).ConfigureAwait(false);

        if (finished == completion.Task)
        {
            await watchdogCts.CancelAsync().ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var result = new ParseResult { FilePath = filePath };
        result.Errors.Add(
            $"C++ parse timed out after {NativeParseTimeout.TotalSeconds:F0} seconds — the file was skipped.");
        return result;
    }

    private static bool TryValidatePath(string filePath, out ParseResult invalid)
    {
        invalid = new ParseResult { FilePath = filePath };
        if (string.IsNullOrWhiteSpace(filePath))
        {
            invalid.Errors.Add("File path is empty.");
            return false;
        }

        if (!File.Exists(filePath))
        {
            invalid.Errors.Add($"File not found: {filePath}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Runs the actual libclang parse. One <c>CXIndex</c> and one <c>CXTranslationUnit</c> are
    /// created per file and disposed when done; the per-file index is what makes concurrent
    /// parsing of different files safe (libclang is thread-safe across separate indexes).
    /// </summary>
    private static ParseResult ParseWithClang(string filePath, byte[] fileBytes)
    {
        var result = new ParseResult { FilePath = filePath };

        try
        {
            var index = clang_createIndex(0, 0);
            if (index == IntPtr.Zero)
            {
                result.Errors.Add("Failed to create libclang index.");
                return result;
            }

            try
            {
                var compileArgs = GetClangCommandLineArgs(filePath);
                var translationUnit = ParseTranslationUnitUtf8(index, filePath, compileArgs);

                if (translationUnit == IntPtr.Zero)
                {
                    result.Errors.Add($"Failed to parse C++ file: {filePath}");
                    return result;
                }

                try
                {
                    WalkAst(translationUnit, filePath, fileBytes, result);
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"C++ AST walk error: {ex.Message}");
                }
                finally
                {
                    clang_disposeTranslationUnit(translationUnit);
                }
            }
            finally
            {
                clang_disposeIndex(index);
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"C++ parse error: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Calls <c>clang_parseTranslationUnit</c> with the path and arguments marshaled as
    /// null-terminated UTF-8 buffers, matching what libclang expects on every platform.
    /// </summary>
    private static IntPtr ParseTranslationUnitUtf8(IntPtr index, string filePath, string[]? compileArgs)
    {
        var nativeStrings = new List<IntPtr>();
        try
        {
            var pathPtr = AllocUtf8(filePath);
            nativeStrings.Add(pathPtr);

            IntPtr[]? argPtrs = null;
            if (compileArgs is { Length: > 0 })
            {
                argPtrs = new IntPtr[compileArgs.Length];
                for (var i = 0; i < compileArgs.Length; i++)
                {
                    argPtrs[i] = AllocUtf8(compileArgs[i]);
                    nativeStrings.Add(argPtrs[i]);
                }
            }

            return clang_parseTranslationUnit(
                index,
                pathPtr,
                argPtrs,
                argPtrs?.Length ?? 0,
                IntPtr.Zero,
                0,
                CXTranslationUnit_None);
        }
        finally
        {
            foreach (var ptr in nativeStrings)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    private static IntPtr AllocUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    private static void WalkAst(IntPtr translationUnit, string filePath, byte[] fileBytes, ParseResult result)
    {
        var context = new AstVisitorContext
        {
            FilePath = filePath,
            FileBytes = fileBytes,
            Classes = result.Classes,
            FreeFunctions = new List<MethodInfo>(),
        };

        var handle = GCHandle.Alloc(context);
        try
        {
            var tuCursor = clang_getTranslationUnitCursor(translationUnit);
            CXCursorVisitor visitor = VisitTranslationUnitChild;
            // Non-zero only when a visitor returns CXChildVisit_Break; none of ours ever does,
            // so the traversal always runs to completion and the result carries no information.
            _ = clang_visitChildren(tuCursor, visitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (context.FreeFunctions.Count > 0)
        {
            var globalClass = new ClassInfo
            {
                Name = "(global)",
                SourceFilePath = filePath,
            };

            foreach (var method in context.FreeFunctions)
            {
                method.ParentClass = globalClass;
            }

            globalClass.Methods = DeduplicateMethods(context.FreeFunctions);
            globalClass.Category = CategoryClassifier.Classify(globalClass);
            result.Classes.Add(globalClass);
        }

        foreach (var classInfo in result.Classes)
        {
            classInfo.Methods = DeduplicateMethods(classInfo.Methods);

            // File-scope constants are attached to every type in the file, because that is the
            // scope they are visible from. Analysis only surfaces a field a method actually
            // mentions, so a type that never touches one is not padded with it.
            foreach (var constant in context.FileConstants)
            {
                if (!classInfo.Fields.Exists(f => string.Equals(f.Name, constant.Name, StringComparison.Ordinal)))
                {
                    classInfo.Fields.Add(constant);
                }
            }
        }

        result.Errors.AddRange(context.WalkErrors);
    }

    private static string[]? GetClangCommandLineArgs(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        var sourceDirectory = Path.GetDirectoryName(filePath);
        var includeDirectory = string.IsNullOrEmpty(sourceDirectory) ? null : sourceDirectory;

        if (string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase))
        {
            return includeDirectory is null
                ? ["-std=c++17"]
                : ["-std=c++17", "-I", includeDirectory];
        }

        if (LanguageFileExtensions.IsCppFile(filePath) &&
            !string.Equals(extension, ".cpp", StringComparison.OrdinalIgnoreCase))
        {
            return ["-x", "c++", "-std=c++17"];
        }

        return null;
    }

    /// <summary>
    /// Collapses duplicate entries produced when a method appears twice (in-class declaration
    /// plus out-of-class definition), keying on name <b>and</b> parameter types so genuine
    /// overloads survive. Prefers the entry that carries the body source (the definition) and
    /// backfills its doc summary from the header declaration when only that one is documented.
    /// </summary>
    private static List<MethodInfo> DeduplicateMethods(List<MethodInfo> methods) =>
        methods
            .GroupBy(
                // Key on parameter *types* only — the declaration and definition of the same
                // method may name their parameters differently.
                method => $"{method.Name}({string.Join(",", method.Parameters.Select(ExtractParameterType))})",
                StringComparer.Ordinal)
            .Select(group =>
            {
                var preferred = group.FirstOrDefault(m => m.XmlDocTags.ContainsKey("sourceCode"))
                                ?? group.First();
                if (string.IsNullOrEmpty(preferred.XmlSummary))
                {
                    preferred.XmlSummary = group
                        .Select(m => m.XmlSummary)
                        .FirstOrDefault(s => !string.IsNullOrEmpty(s));
                }

                return preferred;
            })
            .ToList();

    private static CXChildVisitResult VisitTranslationUnitChild(CXCursor cursor, CXCursor parent, IntPtr clientData)
    {
        var context = (AstVisitorContext)GCHandle.FromIntPtr(clientData).Target!;

        try
        {
            if (!IsFromMainFile(cursor))
            {
                return CXChildVisitResult.CXChildVisit_Continue;
            }

            var kind = clang_getCursorKind(cursor);
            var parentKind = clang_getCursorKind(parent);

            switch (kind)
            {
                case CXCursorKind.CXCursor_ClassDecl:
                case CXCursorKind.CXCursor_StructDecl:
                    if (!IsTopLevelTypeParent(parentKind))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    var classInfo = BuildClassInfo(cursor, context.FilePath);
                    ProcessClassMembers(cursor, classInfo, context.FileBytes);
                    classInfo.Category = CategoryClassifier.Classify(classInfo);
                    context.Classes.Add(classInfo);
                    return CXChildVisitResult.CXChildVisit_Continue;

                case CXCursorKind.CXCursor_CXXMethod:
                case CXCursorKind.CXCursor_Constructor:
                case CXCursorKind.CXCursor_Destructor:
                    if (IsOutOfClassDefinition(cursor))
                    {
                        ProcessOutOfClassMethod(cursor, context);
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;

                case CXCursorKind.CXCursor_VarDecl:
                    if (IsTopLevelTypeParent(parentKind))
                    {
                        CollectFileConstant(cursor, context);
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;

                case CXCursorKind.CXCursor_FunctionDecl:
                    if (!IsTopLevelTypeParent(parentKind) && !IsOutOfClassDefinition(cursor))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    if (IsOutOfClassDefinition(cursor))
                    {
                        ProcessOutOfClassMethod(cursor, context);
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    if (!IsTopLevelTypeParent(parentKind))
                    {
                        return CXChildVisitResult.CXChildVisit_Continue;
                    }

                    var freeMethod = BuildMethodInfo(cursor, parentClass: null, isFreeFunction: true, context.FileBytes);
                    if (!string.IsNullOrEmpty(freeMethod.Name))
                    {
                        context.FreeFunctions.Add(freeMethod);
                    }

                    return CXChildVisitResult.CXChildVisit_Continue;
            }
        }
        catch (Exception ex)
        {
            context.WalkErrors.Add(ex.Message);
        }

        return CXChildVisitResult.CXChildVisit_Recurse;
    }

    private static CXChildVisitResult VisitClassMember(CXCursor cursor, CXCursor parent, IntPtr clientData)
    {
        var memberContext = (ClassMemberVisitorContext)GCHandle.FromIntPtr(clientData).Target!;
        var classInfo = memberContext.ClassInfo;

        try
        {
            if (!IsFromMainFile(cursor))
            {
                return CXChildVisitResult.CXChildVisit_Continue;
            }

            switch (clang_getCursorKind(cursor))
            {
                case CXCursorKind.CXCursor_CXXBaseSpecifier:
                    ProcessBaseSpecifier(cursor, classInfo);
                    break;

                // A class member (FieldDecl) and a static or namespace-scope variable (VarDecl)
                // both become a field on the enclosing class, and are recorded identically.
                case CXCursorKind.CXCursor_FieldDecl:
                case CXCursorKind.CXCursor_VarDecl:
                    ProcessVariableDecl(cursor, classInfo);
                    break;

                case CXCursorKind.CXCursor_CXXMethod:
                case CXCursorKind.CXCursor_Constructor:
                case CXCursorKind.CXCursor_Destructor:
                    var method = BuildMethodInfo(cursor, classInfo, isFreeFunction: false, memberContext.FileBytes);
                    if (!string.IsNullOrEmpty(method.Name))
                    {
                        classInfo.Methods.Add(method);
                    }

                    break;
            }
        }
        catch
        {
            // Member-level failures should not abort the class walk.
        }

        return CXChildVisitResult.CXChildVisit_Continue;
    }

    private static bool IsOutOfClassDefinition(CXCursor cursor)
    {
        try
        {
            if (!IsFromMainFile(cursor))
            {
                return false;
            }

            var className = GetEnclosingClassName(cursor) ?? GetQualifiedClassNameFromSpelling(cursor);
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            var semanticParent = clang_getCursorSemanticParent(cursor);
            var parentKind = clang_getCursorKind(semanticParent);
            if (parentKind is CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl)
            {
                return !IsFromMainFile(semanticParent);
            }

            return IsFileScopeDeclaration(cursor);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileScopeDeclaration(CXCursor cursor)
    {
        try
        {
            var current = clang_getCursorSemanticParent(cursor);
            for (var depth = 0; depth < 16; depth++)
            {
                var kind = clang_getCursorKind(current);
                if (kind is CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl)
                {
                    return false;
                }

                if (IsTopLevelTypeParent(kind))
                {
                    return true;
                }

                current = clang_getCursorSemanticParent(current);
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void ProcessOutOfClassMethod(CXCursor cursor, AstVisitorContext context, string? className = null)
    {
        className ??= GetEnclosingClassName(cursor);
        if (string.IsNullOrEmpty(className))
        {
            className = GetQualifiedClassNameFromSpelling(cursor);
        }

        if (string.IsNullOrEmpty(className))
        {
            return;
        }

        var classInfo = FindOrCreateClass(context, className, context.FilePath);
        var method = BuildMethodInfo(cursor, classInfo, isFreeFunction: false, context.FileBytes);
        if (string.IsNullOrEmpty(method.Name))
        {
            return;
        }

        classInfo.Methods.Add(method);
    }

    private static string? GetQualifiedClassNameFromSpelling(CXCursor cursor)
    {
        try
        {
            foreach (var candidate in new[] { GetDisplayName(cursor), GetSpelling(cursor) })
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }

                // Only the part before the parameter list can carry the owning type. A parameter
                // type contains scope markers of its own, and searching the whole display name
                // found the "::" inside std::vector, so a free function taking one was filed
                // under a phantom class called "computeAverage(const std".
                var parameters = candidate.IndexOf('(', StringComparison.Ordinal);
                var qualifiedName = parameters >= 0 ? candidate[..parameters] : candidate;

                var scopeIndex = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
                if (scopeIndex > 0)
                {
                    return qualifiedName[..scopeIndex];
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string GetDisplayName(CXCursor cursor)
    {
        try
        {
            var cxString = clang_getCursorDisplayName(cursor);
            return MarshalCxString(cxString);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ClassInfo FindOrCreateClass(AstVisitorContext context, string className, string filePath)
    {
        var existing = context.Classes.FirstOrDefault(classInfo =>
            string.Equals(classInfo.Name, className, StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing;
        }

        var classInfo = new ClassInfo
        {
            Name = className,
            SourceFilePath = filePath,
        };
        classInfo.Category = CategoryClassifier.Classify(classInfo);
        context.Classes.Add(classInfo);
        return classInfo;
    }

    private static string? GetEnclosingClassName(CXCursor cursor)
    {
        try
        {
            var current = clang_getCursorSemanticParent(cursor);
            for (var depth = 0; depth < 16; depth++)
            {
                var kind = clang_getCursorKind(current);
                if (kind is CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl)
                {
                    var name = GetSpelling(current);
                    return string.IsNullOrEmpty(name) ? null : name;
                }

                if (kind == CXCursorKind.CXCursor_TranslationUnit)
                {
                    return null;
                }

                current = clang_getCursorSemanticParent(current);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static void ProcessClassMembers(CXCursor classCursor, ClassInfo classInfo, byte[] fileBytes)
    {
        var memberContext = new ClassMemberVisitorContext
        {
            ClassInfo = classInfo,
            FileBytes = fileBytes,
        };
        var handle = GCHandle.Alloc(memberContext);
        try
        {
            CXCursorVisitor visitor = VisitClassMember;
            // See VisitTranslationUnitChild: no visitor breaks, so the result carries no information.
            _ = clang_visitChildren(classCursor, visitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
    }

    private static ClassInfo BuildClassInfo(CXCursor cursor, string filePath)
    {
        var rawComment = GetRawComment(cursor);
        var xmlDoc = ParseDoxygenComment(rawComment);

        return new ClassInfo
        {
            Name = GetSpelling(cursor),
            XmlSummary = xmlDoc.GetValueOrDefault("summary"),
            SourceFilePath = filePath,
        };
    }

    private static void ProcessBaseSpecifier(CXCursor cursor, ClassInfo classInfo)
    {
        var name = GetSpelling(cursor);
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        name = GetSimpleTypeName(name);
        if (LooksLikeInterface(name))
        {
            classInfo.ImplementedInterfaces.Add(name);
        }
        else
        {
            classInfo.BaseClassName ??= name;
        }
    }

    /// <summary>
    /// Records a declared variable as a field of <paramref name="classInfo"/>. Serves both
    /// class members and static or namespace-scope variables, which are treated identically.
    /// </summary>
    private static void ProcessVariableDecl(CXCursor cursor, ClassInfo classInfo)
    {
        var typeSpelling = GetTypeSpellingStr(clang_getCursorType(cursor));
        classInfo.Fields.Add(new VariableInfo
        {
            Name = GetSpelling(cursor),
            Type = typeSpelling,
            IsField = true,
            AccessModifier = MapAccessSpecifier(clang_getCXXAccessSpecifier(cursor)),
        });

        AddDependency(typeSpelling, classInfo.Dependencies);
    }

    private static MethodInfo BuildMethodInfo(
        CXCursor cursor,
        ClassInfo? parentClass,
        bool isFreeFunction,
        byte[] fileBytes)
    {
        var name = GetSpelling(cursor);
        if (string.IsNullOrEmpty(name))
        {
            name = GetDisplayName(cursor);
        }

        var scopeIndex = name.LastIndexOf("::", StringComparison.Ordinal);
        if (scopeIndex >= 0 && scopeIndex < name.Length - 2)
        {
            name = name[(scopeIndex + 2)..];
        }

        var parenIndex = name.IndexOf('(', StringComparison.Ordinal);
        if (parenIndex > 0)
        {
            name = name[..parenIndex];
        }

        if (string.IsNullOrEmpty(name))
        {
            return new MethodInfo();
        }

        var kind = clang_getCursorKind(cursor);
        var xmlDoc = ParseDoxygenComment(GetRawComment(cursor));

        var methodInfo = new MethodInfo
        {
            Name = name,
            AccessModifier = isFreeFunction
                ? "public"
                : MapAccessSpecifier(clang_getCXXAccessSpecifier(cursor)),
            XmlSummary = xmlDoc.GetValueOrDefault("summary"),
            XmlDocTags = xmlDoc,
            ParentClass = parentClass,
            ThrownExceptions = ExtractThrownExceptions(xmlDoc),
        };

        methodInfo.ReturnType = kind is CXCursorKind.CXCursor_Constructor or CXCursorKind.CXCursor_Destructor
            ? "void"
            : GetTypeSpellingStr(clang_getResultType(clang_getCursorType(cursor)));

        var argCount = clang_Cursor_getNumArguments(cursor);
        for (uint i = 0; i < argCount; i++)
        {
            var argCursor = clang_Cursor_getArgument(cursor, i);
            var paramName = GetSpelling(argCursor);
            var paramType = GetTypeSpellingStr(clang_getCursorType(argCursor));
            methodInfo.Parameters.Add($"{paramType} {paramName}");
        }

        var sourceCode = ExtractSourceText(cursor, fileBytes);
        if (!string.IsNullOrEmpty(sourceCode))
        {
            methodInfo.XmlDocTags["sourceCode"] = sourceCode;
        }

        methodInfo.LocalVariables = ExtractLocalVariables(cursor, sourceCode);
        methodInfo.CyclomaticComplexity = CountCyclomaticComplexity(sourceCode);
        // Merged, not replaced: the documented list comes from Doxygen @throws tags and the two
        // do not necessarily agree — a method may document an exception it no longer throws, or
        // throw one nobody documented, and the reader is served by seeing both.
        foreach (var thrown in ExtractThrownExceptionsFromBody(sourceCode))
        {
            if (!methodInfo.ThrownExceptions.Contains(thrown, StringComparer.Ordinal))
            {
                methodInfo.ThrownExceptions.Add(thrown);
            }
        }
        ApplyVariableOperationalLimits(methodInfo, sourceCode);
        methodInfo.OperationalLimits.AddRange(DetectPotentialRuntimeIssuesCpp(methodInfo, sourceCode));

        return methodInfo;
    }

    /// <summary>
    /// Records a namespace or file scope constant along with the literal it is set to.
    /// </summary>
    /// <remarks>
    /// The value is read from the declaration text rather than the AST, because libclang exposes
    /// an initialiser as a child expression tree that would have to be walked and reassembled;
    /// the source line already says what is wanted. Only a literal is kept — a constant computed
    /// from something else has no fixed value to quote, and quoting a guess would be worse than
    /// leaving the bound unresolved.
    /// </remarks>
    private static void CollectFileConstant(CXCursor cursor, AstVisitorContext context)
    {
        try
        {
            var name = GetSpelling(cursor);
            if (string.IsNullOrEmpty(name)) return;
            if (context.FileConstants.Exists(c => string.Equals(c.Name, name, StringComparison.Ordinal))) return;

            var declaration = ExtractSourceText(cursor, context.FileBytes);
            if (string.IsNullOrEmpty(declaration)) return;

            var match = SafeRegex.Match(
                declaration,
                @"\b" + Regex.Escape(name) + @"\s*=\s*(-?\d+(?:\.\d+)?)");
            if (!match.Success) return;

            context.FileConstants.Add(new VariableInfo
            {
                Name = name,
                Type = GetTypeSpellingStr(clang_getCursorType(cursor)),
                InitialValue = match.Groups[1].Value,
                IsField = true,
            });
        }
        catch
        {
            // Best-effort: a constant that cannot be read simply stays unresolved.
        }
    }

    /// <summary>
    /// Collects the exception types a C++ method throws, in the order they first appear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the C# side listed a method's exceptions while the C++ side always reported
    /// none, so the "Errors / Exceptions" section told the reader that a function throwing three
    /// different types threw nothing at all — worse than silence, because it reads as a checked
    /// finding. Comments and strings are blanked first so the word "throw" inside a message does
    /// not register.
    /// </para>
    /// <para>
    /// Only a type constructed at the throw is recognised, which is how nearly all C++ is
    /// written. Three shapes are deliberately left out rather than guessed at:
    /// <c>throw makeError(...)</c>, where the type is whatever the function returns and the name
    /// at the throw is not it; <c>throw caught;</c>, where the type belongs to the declaration of
    /// the variable rather than the statement; and a bare <c>throw;</c>, which re-raises whatever
    /// is already in flight. Naming the function or the variable in those cases would put a
    /// wrong type in front of the reader, and a wrong entry in a list headed "exceptions" is
    /// worse than a short list.
    /// </para>
    /// </remarks>
    private static List<string> ExtractThrownExceptionsFromBody(string methodSource)
    {
        var thrown = new List<string>();
        if (string.IsNullOrEmpty(methodSource))
        {
            return thrown;
        }

        try
        {
            var code = SourceText.StripCommentsAndStrings(methodSource, keepCharacterLiterals: false);

            foreach (Match match in SafeRegex.Matches(code, @"\bthrow\s+([A-Za-z_][\w:]*)\s*[({]"))
            {
                var type = match.Groups[1].Value;
                if (!thrown.Contains(type, StringComparer.Ordinal))
                {
                    thrown.Add(type);
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological body is not worth failing the parse for.
        }

        return thrown;
    }

    /// <summary>
    /// Counts the decision points in a C++ method body, giving McCabe's cyclomatic complexity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this every C++ method was left at the default of 1, so a C++ project reported an
    /// average complexity of 1.0 however involved its code was, and no C++ method could ever
    /// appear in the "most complex methods" list. The measurement was silently C#-only.
    /// </para>
    /// <para>
    /// The same constructs are counted as on the C# side — one for the method itself, then one
    /// for each branch, loop, case, catch and conditional expression — so the two languages
    /// produce comparable figures and the project-wide average means something.
    /// </para>
    /// <para>
    /// Comments and string literals are blanked first, so the word "if" inside a comment or a
    /// message does not raise the count.
    /// </para>
    /// </remarks>
    private static int CountCyclomaticComplexity(string methodSource)
    {
        if (string.IsNullOrEmpty(methodSource))
        {
            return 1;
        }

        try
        {
            var code = SourceText.StripCommentsAndStrings(methodSource, keepCharacterLiterals: false);

            var complexity = 1;
            complexity += SafeRegex.Matches(code, @"\b(?:if|while|for|case|catch)\b").Count;

            // Short-circuit operators are decisions in their own right, counted here so a C++
            // figure means the same as a C# one.
            complexity += SafeRegex.Matches(code, @"&&|\|\|").Count;

            // Every conditional expression contains exactly one question mark, and C++ has no
            // other use for the character once comments and strings are blanked. Counting the
            // marks is therefore both simpler and steadier than the pattern used before, which
            // tried to match "? ... :" and miscounted as soon as two were nested.
            complexity += code.Count(character => character == '?');

            return complexity;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological body is not worth failing the parse for; the default stands.
            return 1;
        }
    }

    private static void ApplyVariableOperationalLimits(MethodInfo methodInfo, string methodSource)
    {
        try
        {
            foreach (var local in methodInfo.LocalVariables)
            {
                var limit = InferOperationalLimitCpp(local.Name, local.Type, methodSource);
                if (!string.IsNullOrEmpty(limit))
                {
                    AddOperationalLimit(methodInfo.OperationalLimits, $"{local.Name}: {limit}");
                }
            }

            if (methodInfo.ParentClass is null || string.IsNullOrEmpty(methodSource))
            {
                return;
            }

            foreach (var field in methodInfo.ParentClass.Fields)
            {
                if (!methodSource.Contains(field.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var limit = InferOperationalLimitCpp(field.Name, field.Type, methodSource);
                if (!string.IsNullOrEmpty(limit))
                {
                    AddOperationalLimit(methodInfo.OperationalLimits, $"{field.Name}: {limit}");
                }
            }
        }
        catch
        {
            // Best-effort heuristics only.
        }
    }

    private static List<VariableInfo> ExtractLocalVariables(CXCursor methodCursor, string methodSource)
    {
        var result = new List<VariableInfo>();
        try
        {
            var context = new LocalVariableVisitorContext
            {
                Variables = result,
                Seen = new HashSet<string>(StringComparer.Ordinal),
                MethodSource = methodSource,
            };

            var handle = GCHandle.Alloc(context);
            try
            {
                CXCursorVisitor visitor = VisitLocalVariable;
                // See VisitTranslationUnitChild: no visitor breaks, so the result carries no information.
                _ = clang_visitChildren(methodCursor, visitor, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return new List<VariableInfo>();
        }

        return result;
    }

    private static CXChildVisitResult VisitLocalVariable(CXCursor cursor, CXCursor parent, IntPtr clientData)
    {
        var context = (LocalVariableVisitorContext)GCHandle.FromIntPtr(clientData).Target!;

        try
        {
            if (clang_getCursorKind(cursor) != CXCursorKind.CXCursor_VarDecl)
            {
                return CXChildVisitResult.CXChildVisit_Recurse;
            }

            var name = GetSpelling(cursor);
            if (string.IsNullOrEmpty(name) || !context.Seen.Add(name))
            {
                return CXChildVisitResult.CXChildVisit_Recurse;
            }

            var parentKind = clang_getCursorKind(parent);
            if (parentKind is CXCursorKind.CXCursor_ParmDecl or CXCursorKind.CXCursor_CXXMethod
                or CXCursorKind.CXCursor_Constructor or CXCursorKind.CXCursor_Destructor
                or CXCursorKind.CXCursor_FunctionDecl)
            {
                // ParmDecl is not VarDecl; this branch handles direct children only.
            }

            if (parentKind is CXCursorKind.CXCursor_ClassDecl or CXCursorKind.CXCursor_StructDecl
                or CXCursorKind.CXCursor_FieldDecl)
            {
                return CXChildVisitResult.CXChildVisit_Recurse;
            }

            context.Variables.Add(new VariableInfo
            {
                Name = name,
                Type = GetTypeSpellingStr(clang_getCursorType(cursor)),
                IsField = false,
            });
        }
        catch
        {
            // Continue walking on individual variable failures.
        }

        return CXChildVisitResult.CXChildVisit_Recurse;
    }

    /// <summary>
    /// Extracts the cursor's source text by slicing the raw file bytes. libclang reports
    /// <b>byte</b> offsets into the file it read from disk, so the slice must happen on the same
    /// bytes — indexing a decoded .NET string shifts every offset after the first non-ASCII
    /// character (or a UTF-8 BOM) and returns garbled snippets.
    /// </summary>
    private static string ExtractSourceText(CXCursor cursor, byte[] fileBytes)
    {
        try
        {
            if (fileBytes.Length == 0)
            {
                return string.Empty;
            }

            var range = clang_getCursorExtent(cursor);
            var startLocation = clang_getRangeStart(range);
            var endLocation = clang_getRangeEnd(range);

            clang_getSpellingLocation(startLocation, out _, out _, out _, out var startOffset);
            clang_getSpellingLocation(endLocation, out _, out _, out _, out var endOffset);

            if (startOffset >= endOffset || startOffset >= fileBytes.Length)
            {
                return string.Empty;
            }

            var length = (int)Math.Min(endOffset - startOffset, (uint)(fileBytes.Length - startOffset));
            if (length <= 0)
            {
                return string.Empty;
            }

            // Returned whole. This text is what every deterministic analyser reads, so cutting it
            // short silently truncated the analysis rather than the display: a method longer than
            // the old 800-character cap had its later branches uncounted, so its complexity came
            // out too low, and any guard or division past that point was invisible — one function
            // here divides by a count on its last line and was never reported as needing it
            // non-zero. The C# parser has always stored the whole body, which is why only C++ was
            // affected. The language model path caps the snippet separately when building a
            // prompt, so nothing downstream depends on the cap being applied here.
            return Encoding.UTF8.GetString(fileBytes, (int)startOffset, length);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? InferOperationalLimitCpp(string name, string type, string methodSource)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(methodSource))
            {
                return null;
            }

            if (methodSource.Contains(name + "[", StringComparison.Ordinal))
            {
                return "Used as an index; must stay within bounds of the collection it accesses";
            }

            var normalizedType = type.Trim();
            if (IsCppNumericType(normalizedType))
            {
                if (IsUsedAsDivisor(name, methodSource))
                {
                    return "Must not be zero (used as a divisor)";
                }

                if (methodSource.Contains(name + " > 0", StringComparison.Ordinal) ||
                    methodSource.Contains(name + " >= 0", StringComparison.Ordinal) ||
                    methodSource.Contains("0 < " + name, StringComparison.Ordinal))
                {
                    return "Should remain non-negative based on surrounding logic";
                }
            }

            if (IsStringLikeType(normalizedType) &&
                (methodSource.Contains("fopen(" + name, StringComparison.Ordinal) ||
                 methodSource.Contains("open(" + name, StringComparison.Ordinal) ||
                 methodSource.Contains("ifstream" + name, StringComparison.Ordinal) ||
                 methodSource.Contains("ofstream" + name, StringComparison.Ordinal)))
            {
                return "Expected to be a valid file path";
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static bool IsCppNumericType(string type)
    {
        var simple = GetSimpleTypeName(type);
        return CppNumericTypeNames.Contains(simple) ||
               CppNumericTypeNames.Contains(type) ||
               simple.Contains("unsigned", StringComparison.Ordinal) ||
               simple.Contains("int", StringComparison.Ordinal);
    }

    private static bool IsStringLikeType(string type) =>
        type.Contains("char*", StringComparison.Ordinal) ||
        type.Contains("std::string", StringComparison.Ordinal) ||
        string.Equals(type, "string", StringComparison.Ordinal);

    private static bool IsUsedAsDivisor(string name, string methodSource)
    {
        if (!methodSource.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        return methodSource.Contains("/ " + name, StringComparison.Ordinal) ||
               methodSource.Contains("/" + name, StringComparison.Ordinal) ||
               methodSource.Contains("% " + name, StringComparison.Ordinal) ||
               methodSource.Contains("%" + name, StringComparison.Ordinal);
    }

    private static List<string> DetectPotentialRuntimeIssuesCpp(MethodInfo method, string sourceCode)
    {
        var issues = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(sourceCode))
            {
                return issues;
            }

            if (sourceCode.Contains('/', StringComparison.Ordinal))
            {
                foreach (var identifier in GetMethodIdentifiers(method))
                {
                    if (!IsUsedAsDivisor(identifier, sourceCode))
                    {
                        continue;
                    }

                    if (!HasVisibleGuard(sourceCode, identifier))
                    {
                        AddOperationalLimit(
                            issues,
                            "Potential division by zero: verify divisor is non-zero before division.");
                        break;
                    }
                }
            }

            var arrowIndex = 0;
            while ((arrowIndex = sourceCode.IndexOf("->", arrowIndex, StringComparison.Ordinal)) >= 0)
            {
                var identifier = ExtractIdentifierBeforeArrow(sourceCode, arrowIndex);
                arrowIndex += 2;
                if (string.IsNullOrEmpty(identifier) || !IsMethodParameter(method, identifier))
                {
                    continue;
                }

                if (!sourceCode.Contains("if (" + identifier, StringComparison.Ordinal) &&
                    !sourceCode.Contains("if(" + identifier, StringComparison.Ordinal) &&
                    !sourceCode.Contains("if (" + identifier + " !=", StringComparison.Ordinal) &&
                    !sourceCode.Contains("if(" + identifier + "!=", StringComparison.Ordinal))
                {
                    AddOperationalLimit(
                        issues,
                        $"Potential null pointer dereference: '{identifier}' is accessed via -> without a visible null check.");
                }
            }

            foreach (var identifier in GetMethodIdentifiers(method))
            {
                if (!sourceCode.Contains(identifier + "[", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!sourceCode.Contains(identifier + " < ", StringComparison.Ordinal))
                {
                    AddOperationalLimit(
                        issues,
                        "Potential out-of-bounds access: array index may exceed buffer size.");
                    break;
                }
            }

            if (sourceCode.Contains("new ", StringComparison.Ordinal) &&
                !sourceCode.Contains("delete ", StringComparison.Ordinal) &&
                !sourceCode.Contains("unique_ptr", StringComparison.Ordinal) &&
                !sourceCode.Contains("shared_ptr", StringComparison.Ordinal))
            {
                AddOperationalLimit(
                    issues,
                    "Potential memory leak: 'new' used without visible 'delete' or smart pointer.");
            }

            if (sourceCode.Contains("while(true)", StringComparison.Ordinal) ||
                sourceCode.Contains("while (true)", StringComparison.Ordinal) ||
                sourceCode.Contains("for(;;)", StringComparison.Ordinal) ||
                sourceCode.Contains("for (;;)", StringComparison.Ordinal))
            {
                AddOperationalLimit(
                    issues,
                    "Potential infinite loop: loop has no visible termination condition.");
            }
        }
        catch
        {
            return new List<string>();
        }

        return issues
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static IEnumerable<string> GetMethodIdentifiers(MethodInfo method)
    {
        foreach (var parameter in method.Parameters)
        {
            var name = ExtractParameterName(parameter);
            if (!string.IsNullOrEmpty(name))
            {
                yield return name;
            }
        }

        foreach (var local in method.LocalVariables)
        {
            if (!string.IsNullOrEmpty(local.Name))
            {
                yield return local.Name;
            }
        }
    }

    private static string ExtractParameterName(string parameter)
    {
        var trimmed = parameter.Trim();
        var space = trimmed.LastIndexOf(' ');
        return space >= 0 ? trimmed[(space + 1)..].Trim() : trimmed;
    }

    private static string ExtractParameterType(string parameter)
    {
        var trimmed = parameter.Trim();
        var space = trimmed.LastIndexOf(' ');
        return space > 0 ? trimmed[..space].Trim() : trimmed;
    }

    private static bool IsMethodParameter(MethodInfo method, string identifier) =>
        method.Parameters.Any(parameter =>
            string.Equals(ExtractParameterName(parameter), identifier, StringComparison.Ordinal));

    private static bool HasVisibleGuard(string sourceCode, string identifier) =>
        sourceCode.Contains("if (" + identifier, StringComparison.Ordinal) ||
        sourceCode.Contains("if(" + identifier, StringComparison.Ordinal) ||
        sourceCode.Contains(identifier + " != 0", StringComparison.Ordinal) ||
        sourceCode.Contains(identifier + " > 0", StringComparison.Ordinal);

    private static string ExtractIdentifierBeforeArrow(string sourceCode, int arrowIndex)
    {
        var index = arrowIndex - 1;
        while (index >= 0 && char.IsWhiteSpace(sourceCode[index]))
        {
            index--;
        }

        var end = index;
        while (index >= 0 && (char.IsLetterOrDigit(sourceCode[index]) || sourceCode[index] == '_'))
        {
            index--;
        }

        if (end <= index)
        {
            return string.Empty;
        }

        return sourceCode[(index + 1)..(end + 1)];
    }

    private static void AddOperationalLimit(List<string> limits, string description)
    {
        if (!limits.Contains(description, StringComparer.OrdinalIgnoreCase))
        {
            limits.Add(description);
        }
    }

    private sealed class ClassMemberVisitorContext
    {
        public required ClassInfo ClassInfo { get; init; }
        public required byte[] FileBytes { get; init; }
    }

    private sealed class LocalVariableVisitorContext
    {
        public required List<VariableInfo> Variables { get; init; }
        public required HashSet<string> Seen { get; init; }
        public required string MethodSource { get; init; }
    }

    private static List<string> ExtractThrownExceptions(Dictionary<string, string> xmlDocTags)
    {
        var exceptions = new List<string>();
        foreach (var key in xmlDocTags.Keys)
        {
            if (key.StartsWith("exception:", StringComparison.OrdinalIgnoreCase))
            {
                exceptions.Add(key["exception:".Length..]);
            }
        }

        return exceptions;
    }

    private static bool IsTopLevelTypeParent(CXCursorKind parentKind) =>
        parentKind is CXCursorKind.CXCursor_TranslationUnit
            or CXCursorKind.CXCursor_Namespace
            or CXCursorKind.CXCursor_LinkageSpec;

    private static bool LooksLikeInterface(string name) =>
        name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

    private static string GetSpelling(CXCursor cursor)
    {
        try
        {
            var cxString = clang_getCursorSpelling(cursor);
            return MarshalCxString(cxString);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetTypeSpellingStr(CXType type)
    {
        try
        {
            var cxString = clang_getTypeSpelling(type);
            return MarshalCxString(cxString);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetRawComment(CXCursor cursor)
    {
        try
        {
            var cxString = clang_Cursor_getRawCommentText(cursor);
            return MarshalCxString(cxString);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string MarshalCxString(CXString cxString)
    {
        try
        {
            var ptr = clang_getCString(cxString);
            var text = ptr == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
            return text;
        }
        finally
        {
            clang_disposeString(cxString);
        }
    }

    private static bool IsFromMainFile(CXCursor cursor)
    {
        try
        {
            var location = clang_getCursorLocation(cursor);
            return clang_Location_isFromMainFile(location) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string MapAccessSpecifier(CX_CXXAccessSpecifier access) =>
        access switch
        {
            CX_CXXAccessSpecifier.CX_CXXPublic => "public",
            CX_CXXAccessSpecifier.CX_CXXProtected => "protected",
            CX_CXXAccessSpecifier.CX_CXXPrivate => "private",
            _ => "private",
        };

    private static Dictionary<string, string> ParseDoxygenComment(string? rawComment)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(rawComment))
        {
            return tags;
        }

        var lines = CleanDoxygenLines(rawComment);
        var preamble = new List<string>();
        var i = 0;

        while (i < lines.Count)
        {
            var line = lines[i];
            if (TryParseTagLine(line, out var tagName, out var tagValue))
            {
                var value = tagValue;
                i++;
                while (i < lines.Count && !TryParseTagLine(lines[i], out _, out _))
                {
                    value = string.IsNullOrEmpty(value)
                        ? lines[i]
                        : $"{value} {lines[i]}";
                    i++;
                }

                switch (tagName)
                {
                    case "brief":
                        tags["summary"] = NormalizeWhitespace(value);
                        break;
                    case "param":
                        var space = value.IndexOf(' ', StringComparison.Ordinal);
                        if (space > 0)
                        {
                            tags[$"param:{value[..space].Trim()}"] = NormalizeWhitespace(value[(space + 1)..]);
                        }

                        break;
                    case "return":
                    case "returns":
                        tags["returns"] = NormalizeWhitespace(value);
                        break;
                    case "throws":
                    case "exception":
                        var throwSpace = value.IndexOf(' ', StringComparison.Ordinal);
                        var exceptionType = throwSpace > 0 ? value[..throwSpace].Trim() : value.Trim();
                        var exceptionDesc = throwSpace > 0 ? value[(throwSpace + 1)..] : string.Empty;
                        if (!string.IsNullOrEmpty(exceptionType))
                        {
                            tags[$"exception:{exceptionType}"] = NormalizeWhitespace(exceptionDesc);
                        }

                        break;
                }
            }
            else
            {
                preamble.Add(line);
                i++;
            }
        }

        if (!tags.ContainsKey("summary") && preamble.Count > 0)
        {
            tags["summary"] = NormalizeWhitespace(string.Join(' ', preamble));
        }

        return tags;
    }

    private static List<string> CleanDoxygenLines(string rawComment)
    {
        var lines = new List<string>();
        foreach (var rawLine in rawComment.Split('\n'))
        {
            var line = rawLine.Trim();
            // All three Doxygen comment openers are exactly three characters, so they are
            // stripped identically.
            if (line.StartsWith("/**", StringComparison.Ordinal) ||
                line.StartsWith("/*!", StringComparison.Ordinal) ||
                line.StartsWith("///", StringComparison.Ordinal))
            {
                line = line[3..].TrimStart();
            }

            if (line.EndsWith("*/", StringComparison.Ordinal))
            {
                line = line[..^2].TrimEnd();
            }

            if (line.StartsWith('*'))
            {
                line = line[1..].TrimStart();
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static bool TryParseTagLine(string line, out string tagName, out string tagValue)
    {
        tagName = string.Empty;
        tagValue = string.Empty;

        var match = TagLineRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        tagName = match.Groups["tag"].Value;
        tagValue = match.Groups["value"].Value.Trim();
        return true;
    }

    // Matched against Doxygen comment lines from source files the user did not write; the timeout
    // bounds a pathological input instead of letting it hang the parse. See SafeRegex.
    private static readonly Regex TagLineRegex = new(
        @"^[@\\](?<tag>brief|param|return|returns|throws|exception)\b\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static string NormalizeWhitespace(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static void AddDependency(string typeSpelling, List<string> dependencies)
    {
        var name = GetSimpleTypeName(typeSpelling);
        if (string.IsNullOrEmpty(name) || PrimitiveTypeNames.Contains(name))
        {
            return;
        }

        if (!dependencies.Contains(name))
        {
            dependencies.Add(name);
        }
    }

    private static string GetSimpleTypeName(string typeSpelling)
    {
        var name = typeSpelling.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        name = name.Replace("const", "", StringComparison.Ordinal)
            .Replace("volatile", "", StringComparison.Ordinal)
            .Replace("static", "", StringComparison.Ordinal)
            .Trim();

        while (name.Length > 0 && (name[^1] is '&' or '*'))
        {
            name = name[..^1].Trim();
        }

        var angle = name.IndexOf('<', StringComparison.Ordinal);
        if (angle > 0)
        {
            var inner = name[(angle + 1)..].TrimEnd('>').Trim();
            var comma = inner.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0)
            {
                inner = inner[..comma].Trim();
            }

            if (!string.IsNullOrEmpty(inner))
            {
                var innerSimple = GetSimpleTypeName(inner);
                if (!string.IsNullOrEmpty(innerSimple) && !PrimitiveTypeNames.Contains(innerSimple))
                {
                    return innerSimple;
                }
            }

            name = name[..angle].Trim();
        }

        var lastColon = name.LastIndexOf("::", StringComparison.Ordinal);
        if (lastColon >= 0)
        {
            name = name[(lastColon + 2)..];
        }

        return name.Trim();
    }

    private sealed class AstVisitorContext
    {
        public required string FilePath { get; init; }
        public required byte[] FileBytes { get; init; }
        public required List<ClassInfo> Classes { get; init; }
        public required List<MethodInfo> FreeFunctions { get; init; }

        /// <summary>
        /// Constants declared at namespace or file scope. C++ commonly writes a bound as one of
        /// these, and without collecting them a guard such as <c>window &gt; MaxWindow</c> has no
        /// value to resolve to, so the reported limit stops at the end that happens to be a
        /// literal.
        /// </summary>
        public List<VariableInfo> FileConstants { get; } = new();

        public List<string> WalkErrors { get; } = new();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CXCursor
    {
        public int kind;
        public int xdata;
        public IntPtr data0;
        public IntPtr data1;
        public IntPtr data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CXString
    {
        public IntPtr data;
        public uint private_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CXType
    {
        public int kind;
        public IntPtr data0;
        public IntPtr data1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CXSourceLocation
    {
        public IntPtr ptr_data0;
        public IntPtr ptr_data1;
        public uint int_data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CXSourceRange
    {
        public IntPtr ptr_data0;
        public IntPtr ptr_data1;
        public uint begin_int_data;
        public uint end_int_data;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate CXChildVisitResult CXCursorVisitor(CXCursor cursor, CXCursor parent, IntPtr clientData);

    // Values must match clang-c/Index.h exactly. Namespace and LinkageSpec were previously
    // wrong (33/36 — actually NamespaceAlias and TypeAliasDecl), which made the visitor treat
    // real namespace cursors as unknown parents and silently drop every class declared inside
    // a namespace block.
    private enum CXCursorKind : uint
    {
        CXCursor_StructDecl = 2,
        CXCursor_ClassDecl = 4,
        CXCursor_FieldDecl = 6,
        CXCursor_FunctionDecl = 8,
        CXCursor_VarDecl = 9,
        CXCursor_ParmDecl = 10,
        CXCursor_CXXMethod = 21,
        CXCursor_Namespace = 22,
        CXCursor_LinkageSpec = 23,
        CXCursor_Constructor = 24,
        CXCursor_Destructor = 25,
        CXCursor_CXXBaseSpecifier = 44,
        CXCursor_TranslationUnit = 350,
    }

    private enum CXChildVisitResult : uint
    {
        CXChildVisit_Break = 0,
        CXChildVisit_Continue = 1,
        CXChildVisit_Recurse = 2,
    }

    private enum CX_CXXAccessSpecifier : uint
    {
        CX_CXXInvalidAccessSpecifier = 0,
        CX_CXXPublic = 1,
        CX_CXXProtected = 2,
        CX_CXXPrivate = 3,
    }
}

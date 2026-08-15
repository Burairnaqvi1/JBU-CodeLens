using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace JBU.CodeLens.UI.Renderers;

/// <summary>
/// Colours a block of C# or C++ the way an editor would, so the source panel reads like code
/// rather than like a paragraph.
/// </summary>
/// <remarks>
/// <para>
/// The palette is Visual Studio Code's default pair, Dark+ and Light+, because that is what a
/// reader of this panel almost certainly has open on the other monitor, and a familiar colouring
/// is read faster than a handsome one. Both are provided: the panel follows the application
/// theme, and dark tokens on a light background would be unreadable.
/// </para>
/// <para>
/// This is a lexical colouriser, not a parser. It classifies by token shape, a word from the
/// keyword set, a run inside quotes, a comment to end of line, which is what an editor does for
/// its own highlighting and is enough to make structure visible. It deliberately does not resolve
/// types or bind identifiers: this panel exists to show what the file says, and a colouring that
/// pretended to semantic knowledge would be another claim needing verification.
/// </para>
/// </remarks>
internal static class SourceColouriser
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        // C# and C++ declaration and modifier words that share a colour in both editors.
        "abstract", "as", "async", "await", "base", "bool", "byte", "char", "class", "const",
        "constexpr", "decimal", "default", "delegate", "double", "enum", "explicit", "extern",
        "false", "final", "float", "friend", "get", "in", "inline", "int", "interface",
        "internal", "is", "let", "long", "mutable", "namespace", "new", "noexcept", "null",
        "nullptr", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "record", "ref", "sealed", "set", "short", "signed", "sizeof",
        "static", "string", "struct", "template", "this", "true", "typedef", "typename", "uint",
        "ulong", "unsigned", "ushort", "using", "var", "virtual", "void", "volatile", "where",
    };

    private static readonly HashSet<string> ControlKeywords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "continue", "do", "else", "finally", "for", "foreach", "goto",
        "if", "return", "switch", "throw", "try", "while", "yield",
    };

    private sealed record Palette(
        string Keyword, string Control, string Type, string Method, string String,
        string Comment, string Number, string Plain);

    // Visual Studio Code, Dark+ and Light+ defaults.
    private static readonly Palette Dark = new(
        "#569CD6", "#C586C0", "#4EC9B0", "#DCDCAA", "#CE9178", "#6A9955", "#B5CEA8", "#D4D4D4");

    private static readonly Palette Light = new(
        "#0000FF", "#AF00DB", "#267F99", "#795E26", "#A31515", "#008000", "#098658", "#000000");

    /// <summary>
    /// Splits <paramref name="code"/> into coloured runs. Order matters: comments and strings are
    /// matched before anything else, so a keyword inside either keeps the surrounding colour
    /// rather than being lit up in the middle of a sentence.
    /// </summary>
    public static IEnumerable<Run> Colourise(string code, bool isDarkTheme)
    {
        var palette = isDarkTheme ? Dark : Light;
        if (string.IsNullOrEmpty(code))
        {
            yield break;
        }

        // One pass, alternatives tried left to right.
        const string Pattern =
            @"(?<comment>//[^\n]*|/\*.*?\*/)" +
            @"|(?<string>@?""(?:\\.|""""|[^""\\])*""|'(?:\\.|[^'\\])*')" +
            @"|(?<number>\b\d+(?:\.\d+)?[fFdDmMuUlL]*\b|\b0[xX][0-9a-fA-F]+\b)" +
            @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)";

        var last = 0;
        foreach (Match m in Regex.Matches(code, Pattern, RegexOptions.Singleline, TimeSpan.FromSeconds(2)))
        {
            if (m.Index > last)
            {
                yield return Coloured(code[last..m.Index], palette.Plain);
            }

            var text = m.Value;
            if (m.Groups["comment"].Success)
            {
                yield return Coloured(text, palette.Comment);
            }
            else if (m.Groups["string"].Success)
            {
                yield return Coloured(text, palette.String);
            }
            else if (m.Groups["number"].Success)
            {
                yield return Coloured(text, palette.Number);
            }
            else if (ControlKeywords.Contains(text))
            {
                yield return Coloured(text, palette.Control);
            }
            else if (Keywords.Contains(text))
            {
                yield return Coloured(text, palette.Keyword);
            }
            else
            {
                // A word immediately followed by "(" is being called or declared; one followed by
                // "<" or starting upper-case reads as a type. Both are how the editors guess too.
                var after = m.Index + m.Length;
                var next = after < code.Length ? code[after] : '\0';

                string colour;
                if (next == '(')
                {
                    colour = palette.Method;
                }
                else if (next == '<' || char.IsUpper(text[0]))
                {
                    colour = palette.Type;
                }
                else
                {
                    colour = palette.Plain;
                }

                yield return Coloured(text, colour);
            }

            last = m.Index + m.Length;
        }

        if (last < code.Length)
        {
            yield return Coloured(code[last..], palette.Plain);
        }
    }

    private static Run Coloured(string text, string hex) =>
        new(text) { Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) };
}

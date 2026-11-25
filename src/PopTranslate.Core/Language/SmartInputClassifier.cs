using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using PopTranslate.Core.Contracts;
using PopTranslate.Core.Models;

namespace PopTranslate.Core.Language;

/// <summary>
/// High-precision local classifier that identifies URLs, file paths, structured JSON/XML/XAML,
/// exception stack traces, and programming code snippets so ordinary translation does not garble
/// non-prose inputs. Deliberately leaves single code identifiers (camelCase / snake_case) and normal
/// sentences mentioning technical terms as <see cref="SmartInputKind.Prose"/>.
/// </summary>
public sealed partial class SmartInputClassifier : ISmartInputClassifier
{
    private static readonly SmartInputClassification ProseResult = new(SmartInputKind.Prose, string.Empty);

    private static readonly FrozenSet<string> CliTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "git", "dotnet", "npm", "npx", "pnpm", "yarn", "pip", "pip3", "uv",
        "python", "python3", "node", "docker", "kubectl", "helm", "curl", "wget",
        "ssh", "scp", "cargo", "go", "rustc", "gcc", "clang", "cmake", "make",
        "winget", "choco", "scoop", "az", "aws", "gcloud", "terraform", "pwsh", "powershell",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> ProseSecondWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "were", "be", "has", "have", "had", "can", "could",
        "will", "would", "should", "may", "might", "must", "to", "for", "in",
        "on", "at", "with", "of", "and", "or", "makes", "allows", "helps",
        "provides", "supports", "runs", "uses", "means", "works", "becomes",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] StrongCodeLinePrefixes =
    [
        "public ", "private ", "protected ", "internal ", "const ", "let ", "var ",
        "function ", "async function ", "def ", "class ", "interface ", "struct ",
        "record ", "enum ", "namespace ", "import ", "export ", "#include ", "#define ",
        "fn ", "pub fn ", "impl ", "package ", "func ", "type ", "typedef ",
    ];

    public SmartInputClassification Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ProseResult;
        }

        var trimmed = text.Trim();

        if (IsUrlOrEmailBlock(trimmed))
        {
            return new SmartInputClassification(SmartInputKind.UrlOrEmail, "网址或邮箱");
        }

        if (IsFilePath(trimmed))
        {
            return new SmartInputClassification(SmartInputKind.FilePath, "文件路径");
        }

        if (IsJsonOrMarkup(trimmed))
        {
            return new SmartInputClassification(SmartInputKind.JsonOrMarkup, "结构化数据 (JSON / XML)");
        }

        if (IsStackTrace(trimmed))
        {
            return new SmartInputClassification(SmartInputKind.StackTrace, "报错日志 / 堆栈");
        }

        if (IsCodeSnippet(trimmed))
        {
            return new SmartInputClassification(SmartInputKind.CodeSnippet, "代码片段 / 命令");
        }

        return ProseResult;
    }

    private static bool IsUrlOrEmailBlock(string trimmed)
    {
        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 20)
        {
            return false;
        }

        foreach (var line in lines)
        {
            if (!IsSingleUrlOrEmail(line))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSingleUrlOrEmail(string raw)
    {
        var token = UnwrapDelimiters(raw);
        if (token.Length < 4 || ContainsWhitespace(token))
        {
            return false;
        }

        if (SchemePrefixRegex().IsMatch(token))
        {
            return true;
        }

        if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) &&
            token.IndexOf('.', 4) > 4)
        {
            return true;
        }

        if (EmailRegex().IsMatch(token))
        {
            return true;
        }

        if (DomainWithPathRegex().IsMatch(token))
        {
            return true;
        }

        return false;
    }

    private static bool IsFilePath(string trimmed)
    {
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Length is < 3 or > 400)
        {
            return false;
        }

        var candidate = UnwrapDelimiters(trimmed);
        if (candidate.Length < 3 || ContainsSentencePunctuation(candidate))
        {
            return false;
        }

        // Windows drive path (e.g., C:\Users\foo\bar.txt or D:/work/src/App.cs)
        if (WindowsDrivePathRegex().IsMatch(candidate))
        {
            return !ContainsEnglishSentenceConnectors(candidate);
        }

        // Windows UNC path (e.g., \\server\share\file.exe)
        if (UncPathRegex().IsMatch(candidate))
        {
            return !ContainsEnglishSentenceConnectors(candidate);
        }

        // Unix absolute or relative/home path with at least two segments and no spaces
        if (!ContainsWhitespace(candidate) && UnixPathRegex().IsMatch(candidate))
        {
            return true;
        }

        return false;
    }

    private static bool IsJsonOrMarkup(string trimmed)
    {
        if (trimmed.Length < 6)
        {
            return false;
        }

        // Pure JSON object or array
        if ((trimmed[0] == '{' && trimmed[^1] == '}') ||
            (trimmed[0] == '[' && trimmed[^1] == ']'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    using var enumerator = root.EnumerateObject();
                    if (enumerator.MoveNext())
                    {
                        return true;
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    var count = 0;
                    var hasStructuredElement = false;
                    foreach (var elem in root.EnumerateArray())
                    {
                        count++;
                        if (elem.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        {
                            hasStructuredElement = true;
                        }

                        if (hasStructuredElement || count >= 2)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not valid JSON; continue to other classifiers.
            }
        }

        // Pure XML / HTML / XAML markup block
        if (trimmed.Length >= 8 &&
            trimmed[0] == '<' &&
            trimmed[^1] == '>' &&
            MarkupStartRegex().IsMatch(trimmed))
        {
            var hasClosingOrSelfClosing = trimmed.Contains("</", StringComparison.Ordinal) ||
                                          trimmed.Contains("/>", StringComparison.Ordinal);
            var hasAttribute = MarkupAttributeRegex().IsMatch(trimmed);
            var tagCount = MarkupOpenTagRegex().Count(trimmed);

            if (hasClosingOrSelfClosing && (hasAttribute || tagCount >= 2 || trimmed.Contains('\n')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsStackTrace(string trimmed)
    {
        if (trimmed.Contains("Traceback (most recent call last):", StringComparison.Ordinal) ||
            PythonFrameRegex().IsMatch(trimmed) ||
            GoGoroutineRegex().IsMatch(trimmed) ||
            RustPanicRegex().IsMatch(trimmed))
        {
            return true;
        }

        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var frameLineCount = 0;
        var hasExceptionHeader = false;

        foreach (var line in lines)
        {
            if (ExceptionHeaderRegex().IsMatch(line))
            {
                hasExceptionHeader = true;
            }

            if (StackFrameLineRegex().IsMatch(line))
            {
                frameLineCount++;
            }
        }

        return (hasExceptionHeader && frameLineCount >= 1) || frameLineCount >= 2;
    }

    private static bool IsCodeSnippet(string trimmed)
    {
        // Never block single identifiers (camelCase, snake_case, PascalCase) — handled by EnableCodeIdentifierSplit!
        if (!ContainsWhitespace(trimmed) && !trimmed.Contains('\n') && !trimmed.Contains('\r'))
        {
            return false;
        }

        // Markdown fenced code block
        if (trimmed.StartsWith("```", StringComparison.Ordinal) &&
            trimmed.EndsWith("```", StringComparison.Ordinal) &&
            trimmed.Length >= 8)
        {
            return true;
        }

        // SQL statement
        if (IsSqlStatement(trimmed))
        {
            return true;
        }

        // Single-line CLI command
        if (!trimmed.Contains('\n') && !trimmed.Contains('\r') && IsCliCommand(trimmed))
        {
            return true;
        }

        // Structural programming code score
        var lines = trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var score = 0;
        var hasDeclarationPrefix = false;
        var hasSemicolonStatement = false;
        var hasChainedCall = false;
        var hasUsingNamespace = false;
        var hasReturnStatement = false;

        foreach (var line in lines)
        {
            if (!hasDeclarationPrefix)
            {
                foreach (var prefix in StrongCodeLinePrefixes)
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        hasDeclarationPrefix = true;
                        score += 3;
                        break;
                    }
                }
            }

            if (!hasUsingNamespace && UsingNamespaceRegex().IsMatch(line))
            {
                hasUsingNamespace = true;
                score += 3;
            }

            if (!hasReturnStatement &&
                line.StartsWith("return ", StringComparison.Ordinal) &&
                (line.EndsWith(';') || line.Contains('(') || line.Contains("await ", StringComparison.Ordinal)))
            {
                hasReturnStatement = true;
                score += 2;
            }

            if (!hasSemicolonStatement &&
                line.EndsWith(';') &&
                (line.Contains('(') || line.Contains('=') || line.StartsWith("return ", StringComparison.Ordinal) ||
                 line.StartsWith("using ", StringComparison.Ordinal) || line.StartsWith("import ", StringComparison.Ordinal)))
            {
                hasSemicolonStatement = true;
                score += 2;
            }

            if (!hasChainedCall && MemberCallRegex().IsMatch(line))
            {
                hasChainedCall = true;
                score += 2;
            }
        }

        if (trimmed.Contains("=>", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (trimmed.Contains("===", StringComparison.Ordinal) ||
            trimmed.Contains("!==", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (trimmed.Contains("::", StringComparison.Ordinal) ||
            trimmed.Contains("->", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (trimmed.Contains("&&", StringComparison.Ordinal) ||
            trimmed.Contains("||", StringComparison.Ordinal))
        {
            score += 2;
        }

        if (AwaitExpressionRegex().IsMatch(trimmed))
        {
            score += 2;
        }

        if (trimmed.Contains('{') && trimmed.Contains('}') &&
            (trimmed.Contains('\n') || trimmed.Contains(';') || trimmed.Contains('(') || trimmed.Contains("=>", StringComparison.Ordinal)))
        {
            score += 2;
        }

        return score >= 4;
    }

    private static bool IsSqlStatement(string trimmed)
    {
        if (SqlMutationRegex().IsMatch(trimmed))
        {
            return true;
        }

        if (!SqlSelectRegex().IsMatch(trimmed))
        {
            return false;
        }

        var isUppercaseSelect = trimmed.StartsWith("SELECT ", StringComparison.Ordinal);
        var hasStrongSqlClause = SqlClauseRegex().IsMatch(trimmed) ||
                                 trimmed.Contains("SELECT *", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.EndsWith(';');

        // Reject natural English sentences like "Select the preferred language from the dropdown menu."
        if (!isUppercaseSelect && !hasStrongSqlClause)
        {
            return false;
        }

        if (!isUppercaseSelect &&
            trimmed.StartsWith("select the ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsCliCommand(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0 || firstSpace >= line.Length - 1)
        {
            return false;
        }

        var tool = line[..firstSpace];
        if (!CliTools.Contains(tool))
        {
            return false;
        }

        var rest = line[(firstSpace + 1)..].TrimStart();
        var secondSpace = rest.IndexOf(' ');
        var secondToken = secondSpace > 0 ? rest[..secondSpace] : rest;

        if (ProseSecondWords.Contains(secondToken))
        {
            return false;
        }

        var hasFlag = CliFlagRegex().IsMatch(line);
        var hasPipeOrChain = line.Contains(" | ", StringComparison.Ordinal) ||
                             line.Contains(" && ", StringComparison.Ordinal) ||
                             line.Contains(" || ", StringComparison.Ordinal);
        var hasQuotedArg = line.Contains('"') || line.Contains('\'');
        var hasPathOrProject = line.Contains('/') || line.Contains('\\') ||
                               CliArtifactExtensionRegex().IsMatch(line);

        return hasFlag || hasPipeOrChain || hasQuotedArg || hasPathOrProject;
    }

    private static string UnwrapDelimiters(string text)
    {
        var s = text.Trim();
        if (s.Length >= 2)
        {
            var first = s[0];
            var last = s[^1];
            if ((first == '"' && last == '"') ||
                (first == '\'' && last == '\'') ||
                (first == '<' && last == '>') ||
                (first == '(' && last == ')') ||
                (first == '[' && last == ']'))
            {
                return s[1..^1].Trim();
            }
        }

        return s;
    }

    private static bool ContainsWhitespace(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSentencePunctuation(string value)
    {
        foreach (var ch in value)
        {
            if (ch is '，' or '。' or '？' or '！' or '；' or '：' or '?' or '!')
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsEnglishSentenceConnectors(string value) =>
        value.Contains(" and ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(" is ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(" are ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(" was ", StringComparison.OrdinalIgnoreCase) ||
        value.Contains(" please ", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^((https?|ftps?|wss?|file|ssh)://\S{2,}|mailto:[^@\s]+@\S{2,})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemePrefixRegex();

    [GeneratedRegex(@"^[^@\s<>()\[\],;:\""]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,12}/\S*$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainWithPathRegex();

    [GeneratedRegex(@"^[A-Za-z]:[\\/][^\r\n<>|*?\""]+$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePathRegex();

    [GeneratedRegex(@"^\\\\[^\\/\s]+\\[^\\/\s]+(\\[^\r\n<>|*?\""]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex UncPathRegex();

    [GeneratedRegex(@"^((~/|\./|\.\./)[^\s<>|*?\""]+|/[^\s<>|*?\""]+/[^\s<>|*?\""]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"^<([A-Za-z_][\w:.-]*|\?xml|!DOCTYPE)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkupStartRegex();

    [GeneratedRegex(@"\s[A-Za-z_][\w:.-]*\s*=\s*""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupAttributeRegex();

    [GeneratedRegex(@"<[A-Za-z_][\w:.-]*[\s>/]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupOpenTagRegex();

    [GeneratedRegex(@"File\s+""[^""]+""\s*,\s*line\s+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex PythonFrameRegex();

    [GeneratedRegex(@"\bgoroutine\s+\d+\s+\[", RegexOptions.CultureInvariant)]
    private static partial Regex GoGoroutineRegex();

    [GeneratedRegex(@"\bpanicked\s+at\s+", RegexOptions.CultureInvariant)]
    private static partial Regex RustPanicRegex();

    [GeneratedRegex(@"(Exception|Error|Throwable|Traceback)\s*(:|\bin\s+thread\b)", RegexOptions.CultureInvariant)]
    private static partial Regex ExceptionHeaderRegex();

    [GeneratedRegex(@"^\s*at\s+[A-Za-z0-9_.$<>`~]+\s*(\([^)]*\)|\S+:\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex StackFrameLineRegex();

    [GeneratedRegex(@"^using\s+[A-Z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*\s*;?$", RegexOptions.CultureInvariant)]
    private static partial Regex UsingNamespaceRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MemberCallRegex();

    [GeneratedRegex(@"\bawait\s+[A-Za-z_][A-Za-z0-9_.]*", RegexOptions.CultureInvariant)]
    private static partial Regex AwaitExpressionRegex();

    [GeneratedRegex(@"^\s*(INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM|CREATE\s+(OR\s+REPLACE\s+)?(TABLE|VIEW|INDEX)|ALTER\s+TABLE|DROP\s+TABLE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlMutationRegex();

    [GeneratedRegex(@"^\s*SELECT\b.+\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SqlSelectRegex();

    [GeneratedRegex(@"\b(WHERE|INNER\s+JOIN|LEFT\s+JOIN|RIGHT\s+JOIN|JOIN|GROUP\s+BY|ORDER\s+BY|HAVING|LIMIT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlClauseRegex();

    [GeneratedRegex(@"(^|\s)(--?[A-Za-z][A-Za-z0-9_:-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex CliFlagRegex();

    [GeneratedRegex(@"\.(sln|csproj|fsproj|json|yaml|yml|xml|ps1|sh|py|js|ts|tsx|cs|go|rs|cpp|h|sql|toml|lock)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CliArtifactExtensionRegex();
}

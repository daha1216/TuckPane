using System.Xml.Linq;

internal static class RepoSourceSnapshot
{
    internal static string RepoRoot()
    {
        foreach (string seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(seed); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "TuckPane", "TuckPane.csproj")))
                    return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Cannot locate TuckPane repository root.");
    }

    internal static string TuckPaneRoot() => Path.Combine(RepoRoot(), "src", "TuckPane");

    internal static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    internal static string[] EnumerateTuckPaneFiles(params string[] extensions)
    {
        HashSet<string> allowed = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(TuckPaneRoot(), "*", SearchOption.AllDirectories)
            .Where(path => allowed.Contains(Path.GetExtension(path)))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string AllTuckPaneCSharp() =>
        string.Join("\n", EnumerateTuckPaneFiles(".cs").Select(File.ReadAllText));

    internal static string AllTuckPaneXaml() =>
        string.Join("\n", EnumerateTuckPaneFiles(".xaml").Select(File.ReadAllText));

    internal static IReadOnlyList<XDocument> ParseAllTuckPaneXaml() =>
        EnumerateTuckPaneFiles(".xaml").Select(path => XDocument.Parse(File.ReadAllText(path))).ToArray();

    internal static XElement? FindNamedElement(IEnumerable<XDocument> documents, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (XDocument doc in documents)
        {
            XElement? match = doc.Descendants().FirstOrDefault(e => (string?)e.Attribute(x + "Name") == name);
            if (match is not null) return match;
        }
        return null;
    }

    internal static string ExtractMethodBody(string source, string signatureFragment)
    {
        int searchFrom = 0;
        while (searchFrom < source.Length)
        {
            int start = source.IndexOf(signatureFragment, searchFrom, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            searchFrom = start + signatureFragment.Length;

            // Require a member declaration prefix on the same declaration (not a ".Method(" or "= Method(" call site).
            int lineStart = start;
            while (lineStart > 0 && source[lineStart - 1] != '\n' && source[lineStart - 1] != ';' && source[lineStart - 1] != '{' && source[lineStart - 1] != '}')
                lineStart--;
            string prefix = source[lineStart..start];
            if (prefix.Contains('=') || prefix.TrimEnd().EndsWith('.'))
                continue;

            int parenDepth = 0;
            int scan = start;
            bool seenOpenParen = false;
            for (; scan < source.Length; scan++)
            {
                char c = source[scan];
                if (c == '(') { parenDepth++; seenOpenParen = true; }
                else if (c == ')' && seenOpenParen && --parenDepth == 0) { scan++; break; }
            }
            if (!seenOpenParen || parenDepth != 0) continue;

            for (int i = scan; i < source.Length; i++)
            {
                char cur = source[i];
                char nxt = i + 1 < source.Length ? source[i + 1] : '\0';
                if (char.IsWhiteSpace(cur)) continue;
                if (cur == '/' && nxt == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    continue;
                }
                if (cur == '/' && nxt == '*')
                {
                    i += 2;
                    while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (cur == ':' || (cur == 'w' && source[i..].StartsWith("where ", StringComparison.Ordinal)))
                {
                    // Constructor initializer (: base(...) / : this(...)) or generic constraint before '{'
                    while (i < source.Length && source[i] != '{' && source[i] != ';') i++;
                    if (i >= source.Length || source[i] == ';') break;
                    cur = source[i];
                    nxt = i + 1 < source.Length ? source[i + 1] : '\0';
                }
                if (cur == '=' && nxt == '>')
                {
                    int exprStart = i + 2;
                    bool inStr = false, inChr = false, esc = false;
                    int depth = 0;
                    for (int j = exprStart; j < source.Length; j++)
                    {
                        char ch = source[j];
                        if (esc) { esc = false; continue; }
                        if ((inStr || inChr) && ch == '\\') { esc = true; continue; }
                        if (!inChr && ch == '"') { inStr = !inStr; continue; }
                        if (!inStr && ch == '\'') { inChr = !inChr; continue; }
                        if (inStr || inChr) continue;
                        if (ch is '(' or '{' or '[') depth++;
                        else if (ch is ')' or '}' or ']') depth--;
                        else if (ch == ';' && depth <= 0) return source[start..(j + 1)];
                    }
                    return source[start..];
                }
                if (cur == '{')
                {
                    int depth = 0;
                    bool inStr = false, inVerbatim = false, inChr = false, esc = false;
                    for (int j = i; j < source.Length; j++)
                    {
                        char ch = source[j];
                        char next = j + 1 < source.Length ? source[j + 1] : '\0';
                        if (!inStr && !inVerbatim && !inChr)
                        {
                            if (ch == '/' && next == '/')
                            {
                                while (j < source.Length && source[j] != '\n') j++;
                                continue;
                            }
                            if (ch == '/' && next == '*')
                            {
                                j += 2;
                                while (j + 1 < source.Length && !(source[j] == '*' && source[j + 1] == '/')) j++;
                                j++;
                                continue;
                            }
                            if (ch == '@' && next == '"') { inVerbatim = true; j++; continue; }
                            if (ch == '"') { inStr = true; continue; }
                            if (ch == '\'') { inChr = true; continue; }
                            if (ch == '{') depth++;
                            else if (ch == '}' && --depth == 0) return source[start..(j + 1)];
                        }
                        else if (inVerbatim)
                        {
                            if (ch == '"' && next == '"') { j++; continue; }
                            if (ch == '"') inVerbatim = false;
                        }
                        else if (inStr)
                        {
                            if (esc) { esc = false; continue; }
                            if (ch == '\\') { esc = true; continue; }
                            if (ch == '"') inStr = false;
                        }
                        else if (inChr)
                        {
                            if (esc) { esc = false; continue; }
                            if (ch == '\\') { esc = true; continue; }
                            if (ch == '\'') inChr = false;
                        }
                    }
                    return source[start..];
                }
                // Hit ';', ',', ')', etc. -> this occurrence was a call site or abstract/interface declaration!
                break;
            }
        }
        return string.Empty;
    }
}

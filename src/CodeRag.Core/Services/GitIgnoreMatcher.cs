namespace CodeRag.Core.Services;

/// <summary>
/// Honors <c>.gitignore</c> rules anywhere in a directory tree, with optional baseline
/// patterns for build outputs that should always be excluded even when a project lacks
/// a gitignore (or has a permissive one).
/// </summary>
/// <remarks>
/// All <c>.gitignore</c> files under <c>rootPath</c> are discovered at construction time;
/// to pick up new gitignores, recreate the matcher. Patterns are scoped to the directory
/// containing each gitignore (per the gitignore spec), and a separate baseline matcher
/// always runs against the full path-relative-to-root.
/// </remarks>
public class GitIgnoreMatcher
{
    private readonly string _root;
    private readonly Ignore.Ignore _baseline;
    private readonly List<(string DirRel, Ignore.Ignore Matcher)> _scoped;

    public GitIgnoreMatcher(string rootPath, IEnumerable<string>? baselinePatterns = null, bool loadGitIgnores = true)
    {
        _root = Path.GetFullPath(rootPath);

        _baseline = new Ignore.Ignore();
        if (baselinePatterns is not null)
        {
            foreach (var p in baselinePatterns)
            {
                var g = ConvertSegmentPatternToGitIgnore(p);
                if (!string.IsNullOrWhiteSpace(g)) _baseline.Add(g);
            }
        }
        // Always ignore .git internals — even when no .gitignore is present.
        _baseline.Add(".git/");

        _scoped = new();
        if (loadGitIgnores && Directory.Exists(_root))
            LoadGitIgnoreFiles();
    }

    /// <summary>True when <paramref name="absolutePath"/> matches any active ignore rule.</summary>
    public bool IsIgnored(string absolutePath, bool isDirectory = false)
    {
        string rel;
        try { rel = Path.GetRelativePath(_root, absolutePath); }
        catch { return false; }

        if (rel.StartsWith("..", StringComparison.Ordinal)) return false; // outside root
        rel = rel.Replace('\\', '/');
        if (isDirectory && !rel.EndsWith('/')) rel += "/";

        if (_baseline.IsIgnored(rel)) return true;

        foreach (var (dirRel, m) in _scoped)
        {
            // A gitignore only applies to its own directory and below.
            if (dirRel.Length > 0)
            {
                if (!rel.StartsWith(dirRel + "/", StringComparison.OrdinalIgnoreCase) &&
                    !rel.Equals(dirRel, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var subRel = dirRel.Length == 0 ? rel : rel[(dirRel.Length + 1)..];
            if (string.IsNullOrEmpty(subRel)) continue;
            if (m.IsIgnored(subRel)) return true;
        }
        return false;
    }

    public int GitIgnoreFileCount => _scoped.Count;

    private void LoadGitIgnoreFiles()
    {
        // Walk top-down so shallower .gitignores are checked first; this preserves
        // gitignore precedence (deeper rules naturally appear after, but evaluated
        // per-scope so they can still override via negation within their own scope).
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_root, ".gitignore", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        var ordered = files
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in ordered)
        {
            // Skip gitignores that live inside an already-ignored directory (e.g. nested submodule
            // inside /bin/). This avoids loading rules from disposable build output.
            var dirAbs = Path.GetDirectoryName(file)!;
            if (IsIgnored(dirAbs, isDirectory: true)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            var ignore = new Ignore.Ignore();
            var added = false;
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith('#')) continue;
                ignore.Add(line);
                added = true;
            }
            if (!added) continue;

            var dirRel = Path.GetRelativePath(_root, dirAbs).Replace('\\', '/');
            if (dirRel == ".") dirRel = "";
            _scoped.Add((dirRel, ignore));
        }
    }

    /// <summary>
    /// Convert a legacy segment-style pattern like <c>/bin/</c> (matches any path containing
    /// a <c>bin</c> segment) into a gitignore pattern (<c>bin/</c> — directory at any depth).
    /// </summary>
    private static string ConvertSegmentPatternToGitIgnore(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "";
        var s = segment.Trim().Replace('\\', '/').Trim('/');
        return s.Length == 0 ? "" : s + "/";
    }
}

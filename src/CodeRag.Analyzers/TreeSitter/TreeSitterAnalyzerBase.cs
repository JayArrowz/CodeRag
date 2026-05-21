using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>
/// Base class for Tree-sitter-based analyzers.
/// Provides syntax-level extraction using the TreeSitter.DotNet library.
/// </summary>
public abstract class TreeSitterAnalyzerBase : ILanguageAnalyzer
{
    // Language objects wrap native handles; shared per name across all analyzer instances.
    private static readonly ConcurrentDictionary<string, Language> _languageCache =
        new(StringComparer.OrdinalIgnoreCase);

    public abstract string[] SupportedExtensions { get; }
    public abstract string LanguageName { get; }
    public bool HasSemanticModel => false;

    /// <summary>Returns the tree-sitter language name for the given file extension.</summary>
    protected abstract string GetTreeSitterLanguageName(string extension);

    public Task<AnalysisResult> AnalyzeFileAsync(string filePath, string content,
        string workspace, string? projectName = null)
    {
        var result = new AnalysisResult();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var langName = GetTreeSitterLanguageName(ext);
        var language = _languageCache.GetOrAdd(langName, n => new Language(n));

        using var parser = new Parser(language);
        using var tree = parser.Parse(content);
        if (tree is null) return Task.FromResult(result);

        ExtractFromRoot(tree.RootNode, filePath, projectName, result);
        ResolveEdgeTargets(result);
        return Task.FromResult(result);
    }

    protected abstract void ExtractFromRoot(Node root, string filePath,
        string? projectName, AnalysisResult result);

    protected static int StartLine(Node node) => node.StartPosition.Row + 1;
    protected static int EndLine(Node node) => node.EndPosition.Row + 1;

    /// <summary>
    /// Returns the leading JSDoc/doc-comment for a node (/** ... */).
    /// Walks backwards through named siblings looking for a block comment.
    /// </summary>
    protected static string? GetPrecedingJsDoc(Node node)
    {
        var prev = node.PreviousNamedSibling;
        while (prev is not null)
        {
            if (prev.Type == "comment")
            {
                var text = prev.Text;
                if (text.StartsWith("/**")) return text;
                // a plain // comment between JSDoc and the declaration — stop
                break;
            }
            // decorators sit between a JSDoc and the declaration
            if (prev.Type != "decorator") break;
            prev = prev.PreviousNamedSibling;
        }
        return null;
    }

    /// <summary>Yields all descendant nodes depth-first.</summary>
    protected static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var desc in Descendants(child))
                yield return desc;
        }
    }

    protected static Guid DeterministicGuid(string seed)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash);
    }

    private static void ResolveEdgeTargets(AnalysisResult result)
    {
        var bySignature = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var chunk in result.Chunks)
            if (!string.IsNullOrEmpty(chunk.Signature))
                bySignature.TryAdd(chunk.Signature, chunk.Id);

        foreach (var edge in result.Edges)
            if (bySignature.TryGetValue(edge.TargetSignature, out var id))
            {
                edge.TargetChunkId = id;
                edge.IsExternal = false;
            }
    }
}

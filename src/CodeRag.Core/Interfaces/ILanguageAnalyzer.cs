using CodeRag.Core.Models;

namespace CodeRag.Core.Interfaces;

/// <summary>
/// Analyzes source files for a specific language and extracts code chunks.
/// Implement this interface to add support for a new language.
/// </summary>
public interface ILanguageAnalyzer
{
    /// <summary>
    /// File extensions this analyzer handles, e.g. [".cs"], [".py"], [".ts", ".tsx"].
    /// </summary>
    string[] SupportedExtensions { get; }

    /// <summary>
    /// Language identifier written into each chunk, e.g. "csharp", "python", "typescript".
    /// </summary>
    string LanguageName { get; }

    /// <summary>
    /// Whether this analyzer can resolve semantic information (types, assemblies)
    /// beyond pure syntax — e.g. Roslyn can, Tree-sitter cannot.
    /// </summary>
    bool HasSemanticModel { get; }

    /// <summary>
    /// Analyze a single file and return extracted chunks plus relationships.
    /// </summary>
    Task<AnalysisResult> AnalyzeFileAsync(string filePath, string content,
        string workspace, string? projectName = null);
}

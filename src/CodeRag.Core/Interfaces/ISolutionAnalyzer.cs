using CodeRag.Core.Models;

namespace CodeRag.Core.Interfaces;

/// <summary>
/// Analyzes an entire solution/project at once. Used by analyzers that need
/// cross-file semantic context (e.g. Roslyn needs to open the whole solution).
/// </summary>
public interface ISolutionAnalyzer : ILanguageAnalyzer
{
    /// <summary>
    /// Analyze an entire solution/project file and return all chunks and edges.
    /// </summary>
    Task<AnalysisResult> AnalyzeSolutionAsync(string solutionOrProjectPath, string workspace);

    /// <summary>
    /// Analyze only the given files within the context of <paramref name="solutionOrProjectPath"/>.
    /// Reuses a cached <c>MSBuildWorkspace</c> so the per-file path keeps the full semantic model
    /// (cross-file edges like calls/inherits/library refs are preserved on incremental reindex).
    /// </summary>
    /// <param name="absoluteFilePaths">Absolute paths to the source files that changed on disk.</param>
    Task<AnalysisResult> AnalyzeFilesInSolutionAsync(
        string solutionOrProjectPath,
        IEnumerable<string> absoluteFilePaths,
        string workspace);
}

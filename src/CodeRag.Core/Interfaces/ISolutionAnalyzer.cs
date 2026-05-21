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
}

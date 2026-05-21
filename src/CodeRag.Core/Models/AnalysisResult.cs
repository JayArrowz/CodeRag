namespace CodeRag.Core.Models;

/// <summary>
/// Output of an analyzer run: extracted chunks (nodes) plus relationships (edges).
/// </summary>
public class AnalysisResult
{
    public List<CodeChunk> Chunks { get; set; } = [];
    public List<CodeEdge> Edges { get; set; } = [];

    public static AnalysisResult Empty => new();
}

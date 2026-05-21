namespace CodeRag.Core.Models;

public class IndexingStats
{
    public string? Workspace { get; set; }
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public int Methods { get; set; }
    public int Classes { get; set; }
    public int Properties { get; set; }
    public int LibraryCalls { get; set; }
    public int Edges { get; set; }
    public int InternalEdges { get; set; }
    public int ExternalEdges { get; set; }
    public Dictionary<string, int> ByLanguage { get; set; } = [];
    public TimeSpan Duration { get; set; }
}

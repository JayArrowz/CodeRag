namespace CodeRag.Core.Interfaces;

public class StoreStats
{
    public long TotalChunks { get; set; }
    public Dictionary<string, long> ByLanguage { get; set; } = [];
    public Dictionary<string, long> ByKind { get; set; } = [];
    public Dictionary<string, long> ByProject { get; set; } = [];
    public Dictionary<string, long> ByWorkspace { get; set; } = [];
}

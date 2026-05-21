namespace CodeRag.Core.Interfaces;

public class WorkspaceInfo
{
    public string Workspace { get; set; } = string.Empty;
    public long Chunks { get; set; }
    public long Edges { get; set; }
    public DateTime? LastIndexedAt { get; set; }
    public Dictionary<string, long> ByLanguage { get; set; } = [];
    public Dictionary<string, long> ByProject { get; set; } = [];
}

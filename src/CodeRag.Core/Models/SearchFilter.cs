namespace CodeRag.Core.Interfaces;

public class SearchFilter
{
    public string? Workspace { get; set; }
    public string? Language { get; set; }
    public string? ProjectName { get; set; }
    public string? Kind { get; set; }
    public string? FilePath { get; set; }
}

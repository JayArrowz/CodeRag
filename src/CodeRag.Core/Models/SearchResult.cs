namespace CodeRag.Core.Models;

public class SearchResult
{
    public required CodeChunk Chunk { get; set; }
    public double Score { get; set; }
}

namespace CodeRag.Core.Models;

public class SearchResult
{
    public required CodeChunk Chunk { get; set; }
    public double Score { get; set; }

    /// <summary>
    /// Outgoing edges from this chunk, optionally hydrated by the caller.
    /// When present, <see cref="ToRetrievalText"/> appends external-library
    /// documentation so the LLM sees what the chunk's callees actually do.
    /// </summary>
    public List<CodeEdge>? OutgoingEdges { get; set; }

    /// <summary>
    /// Full text passed to an LLM as retrieval context. Includes the chunk's
    /// own retrieval text plus, when <see cref="OutgoingEdges"/> is populated,
    /// inline docs for any external (library) calls the chunk makes.
    /// </summary>
    public string ToRetrievalText()
    {
        var text = Chunk.ToRetrievalText();
        if (OutgoingEdges is null || OutgoingEdges.Count == 0)
            return text;

        var externalDocs = OutgoingEdges
            .Where(e => e.IsExternal && !string.IsNullOrWhiteSpace(e.TargetDocumentation))
            .GroupBy(e => e.TargetSignature)
            .Select(g => g.First())
            .ToList();

        if (externalDocs.Count == 0)
            return text;

        var sb = new System.Text.StringBuilder(text);
        sb.AppendLine();
        sb.AppendLine("// --- referenced library APIs ---");
        foreach (var e in externalDocs)
        {
            sb.AppendLine($"// {e.TargetSignature}");
            foreach (var line in e.TargetDocumentation!.Split('\n'))
                sb.AppendLine($"//   {line.TrimEnd()}");
        }
        return sb.ToString();
    }
}

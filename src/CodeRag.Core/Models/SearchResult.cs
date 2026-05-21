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
    /// <param name="skipDocSignatures">
    /// When provided, the docs for any target signatures in this set are emitted as
    /// signature-only lines (no doc body). Used to dedupe library docs across a batch
    /// of <see cref="SearchResult"/>s — see <see cref="BuildLibraryDocIndex"/>.
    /// </param>
    public string ToRetrievalText(ISet<string>? skipDocSignatures = null)
    {
        var text = Chunk.ToRetrievalText();
        if (OutgoingEdges is null || OutgoingEdges.Count == 0)
            return text;

        var documented = OutgoingEdges
            .Where(e => !string.IsNullOrWhiteSpace(e.TargetDocumentation))
            .GroupBy(e => e.TargetSignature)
            .Select(g => g.First())
            .ToList();

        if (documented.Count == 0)
            return text;

        var sb = new System.Text.StringBuilder(text);
        sb.AppendLine();
        sb.AppendLine("// --- referenced APIs ---");
        foreach (var e in documented)
        {
            if (skipDocSignatures is not null && skipDocSignatures.Contains(e.TargetSignature))
            {
                // Doc body lives in the shared docs section; just name the call here.
                sb.AppendLine($"// {e.TargetSignature}  (see referenced docs)");
                continue;
            }
            sb.AppendLine($"// {e.TargetSignature}");
            foreach (var line in e.TargetDocumentation!.Split('\n'))
                sb.AppendLine($"//   {line.TrimEnd()}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a signature → documentation map across a batch of results, deduplicating
    /// shared call targets (both external library APIs and internal project methods).
    /// Pair with <see cref="ToRetrievalText"/> passing the returned dictionary's keys
    /// to avoid repeating the same XML doc per result.
    /// </summary>
    public static Dictionary<string, string> BuildLibraryDocIndex(IEnumerable<SearchResult> results)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (r.OutgoingEdges is null) continue;
            foreach (var e in r.OutgoingEdges)
            {
                if (string.IsNullOrWhiteSpace(e.TargetDocumentation)) continue;
                docs.TryAdd(e.TargetSignature, e.TargetDocumentation!);
            }
        }
        return docs;
    }
}

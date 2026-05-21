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
    /// Incoming edges (who calls / inherits from this chunk). Hydrated by the
    /// retrieval pipeline so an AI consumer can reason about blast radius.
    /// </summary>
    public List<CodeEdge>? IncomingEdges { get; set; }

    /// <summary>
    /// Neighborhood chunks attached during pipeline expansion: the containing class
    /// of a method, sibling members in the same class, etc. Each is paired with a
    /// short relationship tag (e.g. <c>"containing-type"</c>, <c>"sibling"</c>).
    /// </summary>
    public List<RelatedChunk>? RelatedChunks { get; set; }

    /// <summary>
    /// Optional per-source raw scores (for diagnostics / re-tuning). Keys are
    /// <c>"vector"</c>, <c>"lexical"</c>, <c>"symbol"</c>; values are 0..1.
    /// </summary>
    public Dictionary<string, double>? SourceScores { get; set; }

    /// <summary>
    /// Full text returned when this chunk is retrieved as context for an LLM prompt.
    /// </summary>
    /// <param name="skipDocSignatures">
    /// Signatures whose external doc body should be emitted as a back-reference only
    /// (used to dedupe shared library docs across a batch — see <see cref="BuildLibraryDocIndex"/>).
    /// </param>
    /// <param name="tokenBudget">
    /// Approximate per-result token cap (~4 chars/token). When the chunk body would
    /// blow this budget the body is replaced with its summary, and remaining sections
    /// are appended only while space remains. 0 = no cap.
    /// </param>
    public string ToRetrievalText(ISet<string>? skipDocSignatures = null, int tokenBudget = 0)
    {
        var charBudget = tokenBudget > 0 ? tokenBudget * 4 : int.MaxValue;
        var text = Chunk.ToRetrievalText(charBudget);

        var sb = new System.Text.StringBuilder(text);

        // --- referenced APIs (outgoing) ---
        if (OutgoingEdges is { Count: > 0 })
        {
            var documented = OutgoingEdges
                .Where(e => !string.IsNullOrWhiteSpace(e.TargetDocumentation))
                .GroupBy(e => e.TargetSignature)
                .Select(g => g.First())
                .ToList();

            if (documented.Count > 0 && sb.Length < charBudget)
            {
                sb.AppendLine();
                sb.AppendLine("// --- referenced APIs ---");
                foreach (var e in documented)
                {
                    if (sb.Length >= charBudget) break;
                    if (skipDocSignatures is not null && skipDocSignatures.Contains(e.TargetSignature))
                    {
                        sb.AppendLine($"// {e.TargetSignature}  (see referenced docs)");
                        continue;
                    }
                    sb.AppendLine($"// {e.TargetSignature}");
                    foreach (var line in e.TargetDocumentation!.Split('\n'))
                    {
                        if (sb.Length >= charBudget) break;
                        sb.AppendLine($"//   {line.TrimEnd()}");
                    }
                }
            }
        }

        // --- callers (incoming) ---
        if (IncomingEdges is { Count: > 0 } && sb.Length < charBudget)
        {
            sb.AppendLine();
            sb.AppendLine("// --- callers ---");
            foreach (var e in IncomingEdges.Take(8))
            {
                if (sb.Length >= charBudget) break;
                var loc = string.IsNullOrEmpty(e.FilePath) ? "" : $"  ({e.FilePath}:{e.LineNumber})";
                sb.AppendLine($"// {e.SourceSignature}{loc}");
            }
        }

        // --- related chunks (containing type / siblings) ---
        if (RelatedChunks is { Count: > 0 } && sb.Length < charBudget)
        {
            sb.AppendLine();
            sb.AppendLine("// --- related ---");
            foreach (var rel in RelatedChunks)
            {
                if (sb.Length >= charBudget) break;
                sb.AppendLine($"// [{rel.Relation}] {rel.Chunk.Signature ?? rel.Chunk.FunctionName}  ({rel.Chunk.FilePath}:{rel.Chunk.LineNumber})");
                if (!string.IsNullOrEmpty(rel.Chunk.Documentation))
                {
                    foreach (var line in rel.Chunk.Documentation.Split('\n').Take(4))
                    {
                        if (sb.Length >= charBudget) break;
                        sb.AppendLine($"//   {line.TrimEnd()}");
                    }
                }
            }
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

/// <summary>A chunk that was attached during neighborhood expansion.</summary>
public class RelatedChunk
{
    public required CodeChunk Chunk { get; set; }
    /// <summary>"containing-type" | "sibling" | "implementer" | …</summary>
    public required string Relation { get; set; }
}

using CodeRag.Core.Interfaces;

namespace CodeRag.Core.Models;

/// <summary>
/// Controls every stage of the RAG retrieval pipeline:
///   symbol-exact fast path → vector + lexical candidates → RRF fusion →
///   score threshold → MMR diversification → neighborhood expansion → edge hydration.
/// Defaults are tuned for an AI assistant consuming the results as prompt context.
/// </summary>
public class QueryOptions
{
    /// <summary>Final number of results returned to the caller.</summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Per-source candidate pool size = TopK × this multiplier (min +10).
    /// Larger = better recall before fusion, more DB work.
    /// </summary>
    public int CandidateMultiplier { get; set; } = 4;

    public SearchFilter? Filter { get; set; }

    // -------- stage toggles --------
    public bool EnableSymbolMatch { get; set; } = true;
    public bool EnableVector { get; set; } = true;
    public bool EnableLexical { get; set; } = true;

    // -------- fusion --------
    /// <summary>RRF constant k. 60 is the well-known default.</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>Max symbol-exact matches to pin at the top of the result list.</summary>
    public int SymbolMaxHits { get; set; } = 3;

    // -------- pruning --------
    /// <summary>
    /// Drop candidates whose vector score (cosine similarity, 0..1) is below this.
    /// Set to 0 to disable.
    /// </summary>
    public double MinVectorScore { get; set; } = 0.30;

    // -------- diversification --------
    /// <summary>
    /// When true, caps results per file to <see cref="MaxPerFile"/> and per class
    /// to <see cref="MaxPerClass"/> so the prompt isn't dominated by one neighborhood.
    /// </summary>
    public bool DiversifyResults { get; set; } = true;
    public int MaxPerFile { get; set; } = 2;
    public int MaxPerClass { get; set; } = 3;

    // -------- neighborhood expansion --------
    /// <summary>Pull in containing class chunks, sibling methods, and incoming edges.</summary>
    public bool ExpandNeighbors { get; set; } = true;

    /// <summary>Attach the containing class/record/struct chunk as a related chunk.</summary>
    public bool IncludeContainingType { get; set; } = true;

    /// <summary>Attach hydrated incoming edges (who calls this) to each result.</summary>
    public bool IncludeIncomingEdges { get; set; } = true;
    public int MaxIncomingEdges { get; set; } = 8;

    // -------- edge hydration --------
    /// <summary>Attach outgoing edges (with library docs) — needed for ToRetrievalText.</summary>
    public bool HydrateOutgoingEdges { get; set; } = true;

    // -------- output shaping --------
    /// <summary>
    /// Approximate per-result token cap for <c>ToRetrievalText</c>. The chunk's body
    /// is truncated or replaced with its summary when over budget. ~4 chars/token.
    /// 0 = no cap.
    /// </summary>
    public int TokenBudgetPerResult { get; set; } = 800;

    /// <summary>
    /// Optional rewritten query used for the embedding call (e.g. expanded /
    /// re-phrased by an upstream LLM). The original <c>query</c> is still used for
    /// lexical and symbol-exact matching so identifiers aren't lost.
    /// </summary>
    public string? EmbeddingQueryOverride { get; set; }
}

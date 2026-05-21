namespace CodeRag.Core.Models;

/// <summary>
/// A directed relationship between two code elements: who calls what,
/// who inherits from what, who instantiates what. Stored alongside CodeChunks
/// to enable graph-aware retrieval (caller/callee neighborhood expansion,
/// blast-radius / impact queries, disambiguation).
/// </summary>
public class CodeEdge
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // --- Source (the element that contains / makes the reference) ---
    public Guid SourceChunkId { get; set; }
    public string SourceSignature { get; set; } = string.Empty;

    // --- Target ---
    /// <summary>Filled in after resolution when the target is in-solution; null for externals.</summary>
    public Guid? TargetChunkId { get; set; }
    public string TargetSignature { get; set; } = string.Empty;
    public string? TargetNamespace { get; set; }
    public string? TargetClassName { get; set; }
    public string? TargetMemberName { get; set; }
    public string? TargetAssembly { get; set; }

    /// <summary>calls | creates | inherits | implements | references</summary>
    public string EdgeKind { get; set; } = "calls";

    /// <summary>True if target is not part of any analyzed solution project.</summary>
    public bool IsExternal { get; set; }

    // --- Location of the reference site ---
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }

    /// <summary>Logical workspace this edge belongs to. Required.</summary>
    public string Workspace { get; set; } = string.Empty;

    public string? ProjectName { get; set; }
    public string Language { get; set; } = string.Empty;
}

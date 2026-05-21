namespace CodeRag.Analyzers.TypeScript;

public partial class TsCompilerAnalyzer
{
    private sealed class SidecarEdge
    {
        public string? Type { get; set; }
        public string? SourceNodeId { get; set; }
        public string? TargetNodeId { get; set; }
        public string? TargetSignature { get; set; }
        public string? TargetName { get; set; }
        public string? EdgeKind { get; set; }
        public bool IsExternal { get; set; }
        public string? FilePath { get; set; }
        public int LineNumber { get; set; }
    }
}

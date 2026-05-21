namespace CodeRag.Analyzers.TypeScript;

public partial class TsCompilerAnalyzer
{
    private sealed class SidecarChunk
    {
        public string? Type { get; set; }
        public string? NodeId { get; set; }
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Namespace { get; set; }
        public string? ClassName { get; set; }
        public string? FunctionName { get; set; }
        public string? Signature { get; set; }
        public string? FilePath { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string? Body { get; set; }
        public string? Documentation { get; set; }
        public string? ReturnType { get; set; }
        public List<string>? Parameters { get; set; }
        public List<string>? BaseTypes { get; set; }
        public List<string>? Interfaces { get; set; }
        public List<string>? Modifiers { get; set; }
    }
}

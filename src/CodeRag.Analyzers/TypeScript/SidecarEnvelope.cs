namespace CodeRag.Analyzers.TypeScript;

public partial class TsCompilerAnalyzer
{
    private sealed class SidecarEnvelope
    {
        public string? Type { get; set; }
        public string? Message { get; set; }
        public string? Detail { get; set; }
    }
}

namespace CodeRag.Analyzers.TypeScript;

public partial class TsCompilerAnalyzer
{
    private sealed class SidecarRequest
    {
        public string Op { get; set; } = string.Empty;
        public string? Project { get; set; }
        public List<string>? Files { get; set; }
    }
}

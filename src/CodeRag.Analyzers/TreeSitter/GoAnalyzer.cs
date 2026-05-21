using CodeRag.Core.Models;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>Go analyzer stub — implement ExtractFromRoot to activate.</summary>
public class GoAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".go"];
    public override string LanguageName => "go";
    protected override string GetTreeSitterLanguageName(string extension) => "Go";

    protected override void ExtractFromRoot(Node root, string filePath,
        string? projectName, AnalysisResult result)
        => throw new NotImplementedException("Go extraction not yet implemented.");
}

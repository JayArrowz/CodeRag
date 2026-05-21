using CodeRag.Core.Models;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>Python analyzer stub — implement ExtractFromRoot to activate.</summary>
public class PythonAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".py"];
    public override string LanguageName => "python";
    protected override string GetTreeSitterLanguageName(string extension) => "Python";

    protected override void ExtractFromRoot(Node root, string filePath,
        string? projectName, AnalysisResult result)
        => throw new NotImplementedException("Python extraction not yet implemented.");
}

using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>
/// Base class for Tree-sitter-based analyzers.
/// Provides syntax-level extraction for languages not supported by Roslyn.
/// </summary>
public abstract class TreeSitterAnalyzerBase : ILanguageAnalyzer
{
    public abstract string[] SupportedExtensions { get; }
    public abstract string LanguageName { get; }
    public bool HasSemanticModel => false;

    /// <summary>
    /// Node kind strings for function/method definitions in this language's grammar.
    /// e.g. ["function_definition"] for Python, ["function_declaration", "method_definition"] for TypeScript.
    /// </summary>
    protected abstract string[] FunctionNodeKinds { get; }

    /// <summary>
    /// Node kind strings for class definitions.
    /// </summary>
    protected abstract string[] ClassNodeKinds { get; }

    public Task<AnalysisResult> AnalyzeFileAsync(string filePath, string content,
        string workspace, string? projectName = null)
    {
        // TODO: Implement with TreeSitter NuGet package
        // This is a placeholder showing the intended approach.
        //
        // var parser = new Parser();
        // parser.Language = GetLanguage();  // abstract — each subclass provides its grammar
        // var tree = parser.Parse(content);
        // var root = tree.Root;
        // WalkNode(root, content, filePath, projectName, chunks);

        throw new NotImplementedException(
            $"TreeSitter analyzer for {LanguageName} is not yet wired up. " +
            "Add the TreeSitter NuGet packages and implement GetLanguage().");
    }
}

/// <summary>
/// Python analyzer. Function bodies use "function_definition", classes use "class_definition".
/// Docstrings are the first expression_statement child with a string node.
/// </summary>
public class PythonAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".py"];
    public override string LanguageName => "python";
    protected override string[] FunctionNodeKinds => ["function_definition"];
    protected override string[] ClassNodeKinds => ["class_definition"];
}

/// <summary>
/// TypeScript/JavaScript analyzer.
/// </summary>
public class TypeScriptAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".ts", ".tsx", ".js", ".jsx"];
    public override string LanguageName => "typescript";
    protected override string[] FunctionNodeKinds =>
        ["function_declaration", "method_definition", "arrow_function"];
    protected override string[] ClassNodeKinds => ["class_declaration"];
}

/// <summary>
/// Go analyzer.
/// </summary>
public class GoAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".go"];
    public override string LanguageName => "go";
    protected override string[] FunctionNodeKinds => ["function_declaration", "method_declaration"];
    protected override string[] ClassNodeKinds => ["type_declaration"];
}

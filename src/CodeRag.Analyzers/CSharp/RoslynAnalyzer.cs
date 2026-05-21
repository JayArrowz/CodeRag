using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CodeRag.Core.Interfaces;
using CodeRag.Core.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeRag.Analyzers.CSharp;

/// <summary>
/// Roslyn-based C# analyzer with full semantic model.
/// Extracts code chunks (types, methods, properties, ...) AND edges
/// (calls, creates, inherits, implements) for graph-aware RAG retrieval.
/// </summary>
public class RoslynAnalyzer : ISolutionAnalyzer
{
    private static bool _msbuildRegistered;
    private static readonly object _lock = new();

    public string[] SupportedExtensions => [".cs"];
    public string LanguageName => "csharp";
    public bool HasSemanticModel => true;

    /// <summary>
    /// Analyze a single .cs file without semantic context. Extracts structure only;
    /// edges will be unresolved (no symbol info available).
    /// </summary>
    public async Task<AnalysisResult> AnalyzeFileAsync(string filePath, string content,
        string workspace, string? projectName = null)
    {
        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        var root = await tree.GetRootAsync();
        var result = new AnalysisResult();

        ExtractFromRoot(root, filePath, projectName, semanticModel: null, solutionProjects: null, result);
        return result;
    }

    /// <summary>
    /// Analyze a full .sln or .csproj file using MSBuildWorkspace for full semantic resolution.
    /// </summary>
    public async Task<AnalysisResult> AnalyzeSolutionAsync(string solutionOrProjectPath, string workspace)
    {
        EnsureMSBuildRegistered();

        var msbuildWorkspace = MSBuildWorkspace.Create();
        msbuildWorkspace.WorkspaceFailed += (_, e) =>
        {
            if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                Console.Error.WriteLine($"  Workspace: {e.Diagnostic.Message}");
        };

        IEnumerable<Project> projects;
        if (solutionOrProjectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            var solution = await msbuildWorkspace.OpenSolutionAsync(solutionOrProjectPath);
            projects = solution.Projects;
        }
        else if (solutionOrProjectPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            projects = await OpenSlnxProjectsAsync(msbuildWorkspace, solutionOrProjectPath);
        }
        else
        {
            var project = await msbuildWorkspace.OpenProjectAsync(solutionOrProjectPath);
            projects = [project];
        }

        var solutionProjectNames = projects.Select(p => p.AssemblyName).ToHashSet();
        var result = new AnalysisResult();

        foreach (var project in projects)
        {
            Console.WriteLine($"  Analyzing project: {project.Name}");
            var compilation = await project.GetCompilationAsync();
            if (compilation is null) continue;

            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null) continue;

                var tree = await doc.GetSyntaxTreeAsync();
                if (tree is null) continue;

                var root = await tree.GetRootAsync();
                var semanticModel = compilation.GetSemanticModel(tree);

                ExtractFromRoot(root, doc.FilePath, project.Name, semanticModel, solutionProjectNames, result);
            }
        }

        ResolveEdgeTargets(result);
        return result;
    }

    private void ExtractFromRoot(SyntaxNode root, string filePath, string? projectName,
        SemanticModel? semanticModel, HashSet<string>? solutionProjects, AnalysisResult result)
    {
        foreach (var ns in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
        {
            var nsName = ns is NamespaceDeclarationSyntax nds
                ? nds.Name.ToString()
                : (ns as FileScopedNamespaceDeclarationSyntax)?.Name.ToString();

            foreach (var typeDecl in ns.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeSymbol = semanticModel?.GetDeclaredSymbol(typeDecl);
                var className = typeSymbol?.Name ?? typeDecl.Identifier.Text;
                var classKind = typeDecl switch
                {
                    InterfaceDeclarationSyntax => "interface_declaration",
                    RecordDeclarationSyntax => "record_declaration",
                    StructDeclarationSyntax => "struct_declaration",
                    _ => "class_declaration"
                };

                var (baseTypes, interfaces) = GetBaseTypesAndInterfaces(typeDecl, semanticModel);

                var typeChunk = new CodeChunk
                {
                    Kind = classKind,
                    Language = LanguageName,
                    Namespace = nsName,
                    ClassName = className,
                    FunctionName = className,
                    Signature = typeSymbol?.ToDisplayString() ?? typeDecl.Identifier.Text,
                    FilePath = filePath,
                    LineNumber = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    EndLineNumber = typeDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    Documentation = GetXmlDoc(typeDecl),
                    ProjectName = projectName,
                    Modifiers = typeDecl.Modifiers.Select(m => m.Text).ToList(),
                    Attributes = GetAttributes(typeDecl),
                    BaseTypes = baseTypes.Select(b => b.signature).ToList(),
                    Interfaces = interfaces.Select(i => i.signature).ToList(),
                };
                result.Chunks.Add(typeChunk);

                foreach (var (signature, symbol) in baseTypes)
                    result.Edges.Add(MakeTypeEdge(typeChunk, signature, symbol, "inherits",
                        filePath, typeDecl, projectName, solutionProjects));

                foreach (var (signature, symbol) in interfaces)
                    result.Edges.Add(MakeTypeEdge(typeChunk, signature, symbol, "implements",
                        filePath, typeDecl, projectName, solutionProjects));

                foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    ExtractMethod(method, semanticModel, filePath, nsName, className, projectName,
                        solutionProjects, result);
                }

                foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    var ctorSymbol = semanticModel?.GetDeclaredSymbol(ctor);
                    var ctorChunk = new CodeChunk
                    {
                        Kind = "constructor",
                        Language = LanguageName,
                        Namespace = nsName,
                        ClassName = className,
                        // Use the class name so ctors are discoverable by name searches.
                        FunctionName = className,
                        Signature = ctorSymbol?.ToDisplayString() ?? $"{className}()",
                        FilePath = filePath,
                        LineNumber = ctor.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        EndLineNumber = ctor.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                        Documentation = GetXmlDoc(ctor),
                        Body = ctor.Body?.ToFullString() ?? ctor.ExpressionBody?.ToFullString(),
                        ProjectName = projectName,
                        Modifiers = ctor.Modifiers.Select(m => m.Text).ToList(),
                        Parameters = ctorSymbol?.Parameters.Select(FormatParameter).ToList() ?? [],
                        Attributes = GetAttributes(ctor),
                    };
                    result.Chunks.Add(ctorChunk);
                    ExtractBodyEdges(ctor, ctorChunk, semanticModel, filePath, projectName,
                        solutionProjects, result);
                }

                foreach (var prop in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var propSymbol = semanticModel?.GetDeclaredSymbol(prop);
                    var propChunk = new CodeChunk
                    {
                        Kind = "property_declaration",
                        Language = LanguageName,
                        Namespace = nsName,
                        ClassName = className,
                        FunctionName = propSymbol?.Name ?? prop.Identifier.Text,
                        Signature = propSymbol?.ToDisplayString() ?? prop.ToString().Split('{')[0].Trim(),
                        FilePath = filePath,
                        LineNumber = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        EndLineNumber = prop.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                        Documentation = GetXmlDoc(prop),
                        ReturnType = propSymbol?.Type.ToDisplayString(),
                        ProjectName = projectName,
                        Modifiers = prop.Modifiers.Select(m => m.Text).ToList(),
                        Attributes = GetAttributes(prop),
                    };
                    result.Chunks.Add(propChunk);
                    ExtractBodyEdges(prop, propChunk, semanticModel, filePath, projectName,
                        solutionProjects, result);
                }

                foreach (var field in typeDecl.Members.OfType<FieldDeclarationSyntax>())
                {
                    var fieldAttributes = GetAttributes(field);
                    var fieldModifiers = field.Modifiers.Select(m => m.Text).ToList();
                    var fieldDoc = GetXmlDoc(field);
                    var fieldType = field.Declaration.Type.ToString();

                    foreach (var variable in field.Declaration.Variables)
                    {
                        var fieldSymbol = semanticModel?.GetDeclaredSymbol(variable) as IFieldSymbol;
                        var fieldName = fieldSymbol?.Name ?? variable.Identifier.Text;
                        var lineSpan = variable.GetLocation().GetLineSpan();
                        result.Chunks.Add(new CodeChunk
                        {
                            Kind = "field_declaration",
                            Language = LanguageName,
                            Namespace = nsName,
                            ClassName = className,
                            FunctionName = fieldName,
                            Signature = fieldSymbol?.ToDisplayString() ?? $"{fieldType} {fieldName}",
                            FilePath = filePath,
                            LineNumber = lineSpan.StartLinePosition.Line + 1,
                            EndLineNumber = lineSpan.EndLinePosition.Line + 1,
                            Documentation = fieldDoc,
                            Body = variable.Initializer?.ToFullString(),
                            ReturnType = fieldSymbol?.Type.ToDisplayString() ?? fieldType,
                            ProjectName = projectName,
                            Modifiers = fieldModifiers,
                            Attributes = fieldAttributes,
                        });
                    }
                }
            }

            foreach (var enumDecl in ns.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                var enumSymbol = semanticModel?.GetDeclaredSymbol(enumDecl);
                var members = enumDecl.Members.Select(m => m.Identifier.Text).ToList();
                result.Chunks.Add(new CodeChunk
                {
                    Kind = "enum_declaration",
                    Language = LanguageName,
                    Namespace = nsName,
                    ClassName = enumSymbol?.Name ?? enumDecl.Identifier.Text,
                    FunctionName = enumSymbol?.Name ?? enumDecl.Identifier.Text,
                    Signature = $"enum {enumDecl.Identifier.Text} {{ {string.Join(", ", members)} }}",
                    FilePath = filePath,
                    LineNumber = enumDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    EndLineNumber = enumDecl.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
                    Documentation = GetXmlDoc(enumDecl),
                    ProjectName = projectName,
                });
            }
        }
    }

    private void ExtractMethod(MethodDeclarationSyntax method, SemanticModel? semanticModel,
        string filePath, string? nsName, string className, string? projectName,
        HashSet<string>? solutionProjects, AnalysisResult result)
    {
        var methodSymbol = semanticModel?.GetDeclaredSymbol(method);
        var lineSpan = method.GetLocation().GetLineSpan();

        var chunk = new CodeChunk
        {
            Kind = "method_declaration",
            Language = LanguageName,
            Namespace = nsName,
            ClassName = className,
            FunctionName = methodSymbol?.Name ?? method.Identifier.Text,
            Signature = methodSymbol?.ToDisplayString() ?? method.Identifier.Text,
            FilePath = filePath,
            LineNumber = lineSpan.StartLinePosition.Line + 1,
            EndLineNumber = lineSpan.EndLinePosition.Line + 1,
            Documentation = GetXmlDoc(method),
            Body = method.Body?.ToFullString() ?? method.ExpressionBody?.ToFullString(),
            ReturnType = methodSymbol?.ReturnType.ToDisplayString() ?? method.ReturnType.ToString(),
            ProjectName = projectName,
            Modifiers = method.Modifiers.Select(m => m.Text).ToList(),
            Parameters = methodSymbol?.Parameters.Select(FormatParameter).ToList() ?? [],
            Attributes = GetAttributes(method),
        };
        result.Chunks.Add(chunk);

        ExtractBodyEdges(method, chunk, semanticModel, filePath, projectName, solutionProjects, result);
    }

    /// <summary>
    /// Walks the body of a callable (method / ctor / property) and emits edges
    /// for every invocation and object creation. Also denormalizes the callee
    /// signatures onto the owning chunk's `Calls` list so a single retrieval
    /// surfaces who/what the body talks to.
    /// </summary>
    private void ExtractBodyEdges(SyntaxNode container, CodeChunk owner, SemanticModel? semanticModel,
        string filePath, string? projectName, HashSet<string>? solutionProjects, AnalysisResult result)
    {
        if (semanticModel is null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var callsList = new List<string>();

        foreach (var invocation in container.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (semanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol invokedSymbol) continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var edge = MakeMethodEdge(owner, invokedSymbol, "calls", filePath, line, projectName, solutionProjects);
            var key = $"calls|{edge.TargetSignature}|{line}";
            if (seen.Add(key))
            {
                result.Edges.Add(edge);
                callsList.Add(edge.TargetSignature);
            }
        }

        foreach (var creation in container.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var ctorSymbol = semanticModel.GetSymbolInfo(creation).Symbol as IMethodSymbol;
            var typeSymbol = ctorSymbol?.ContainingType
                ?? semanticModel.GetTypeInfo(creation.Type).Type as INamedTypeSymbol;
            if (typeSymbol is null) continue;

            var line = creation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var signature = (ctorSymbol ?? (ISymbol)typeSymbol).OriginalDefinition.ToDisplayString();
            var assembly = typeSymbol.ContainingAssembly?.Name ?? "";
            var isExternal = solutionProjects is null || !solutionProjects.Contains(assembly);

            var edge = new CodeEdge
            {
                SourceChunkId = owner.Id,
                SourceSignature = owner.Signature ?? owner.FunctionName,
                TargetSignature = signature,
                TargetNamespace = typeSymbol.ContainingNamespace?.ToDisplayString(),
                TargetClassName = typeSymbol.Name,
                TargetMemberName = ".ctor",
                TargetAssembly = assembly,
                EdgeKind = "creates",
                IsExternal = isExternal,
                FilePath = filePath,
                LineNumber = line,
                ProjectName = projectName,
                Language = LanguageName,
                TargetDocumentation = GetSymbolXmlDoc((ISymbol?)ctorSymbol ?? typeSymbol),
            };
            edge.Id = DeterministicGuid($"{owner.Id}|creates|{signature}|{line}");

            var key = $"creates|{signature}|{line}";
            if (seen.Add(key))
            {
                result.Edges.Add(edge);
                callsList.Add($"new {typeSymbol.Name}");
            }
        }

        owner.Calls = callsList.Distinct(StringComparer.Ordinal).ToList();
    }

    private CodeEdge MakeMethodEdge(CodeChunk owner, IMethodSymbol target, string edgeKind,
        string filePath, int lineNumber, string? projectName, HashSet<string>? solutionProjects)
    {
        var signature = target.OriginalDefinition.ToDisplayString();
        var assembly = target.ContainingAssembly?.Name ?? "";
        var isExternal = solutionProjects is null || !solutionProjects.Contains(assembly);

        var edge = new CodeEdge
        {
            SourceChunkId = owner.Id,
            SourceSignature = owner.Signature ?? owner.FunctionName,
            TargetSignature = signature,
            TargetNamespace = target.ContainingNamespace?.ToDisplayString(),
            TargetClassName = target.ContainingType?.Name,
            TargetMemberName = target.Name,
            TargetAssembly = assembly,
            EdgeKind = edgeKind,
            IsExternal = isExternal,
            FilePath = filePath,
            LineNumber = lineNumber,
            ProjectName = projectName,
            Language = LanguageName,
            TargetDocumentation = GetSymbolXmlDoc(target),
        };
        edge.Id = DeterministicGuid($"{owner.Id}|{edgeKind}|{signature}|{lineNumber}");
        return edge;
    }

    private CodeEdge MakeTypeEdge(CodeChunk owner, string signature, INamedTypeSymbol? symbol,
        string edgeKind, string filePath, TypeDeclarationSyntax typeDecl, string? projectName,
        HashSet<string>? solutionProjects)
    {
        var line = typeDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var assembly = symbol?.ContainingAssembly?.Name ?? "";
        var isExternal = solutionProjects is null
            || string.IsNullOrEmpty(assembly)
            || !solutionProjects.Contains(assembly);

        var edge = new CodeEdge
        {
            SourceChunkId = owner.Id,
            SourceSignature = owner.Signature ?? owner.FunctionName,
            TargetSignature = signature,
            TargetNamespace = symbol?.ContainingNamespace?.ToDisplayString(),
            TargetClassName = symbol?.Name,
            TargetMemberName = null,
            TargetAssembly = assembly,
            EdgeKind = edgeKind,
            IsExternal = isExternal,
            FilePath = filePath,
            LineNumber = line,
            ProjectName = projectName,
            Language = LanguageName,
            TargetDocumentation = GetSymbolXmlDoc(symbol),
        };
        edge.Id = DeterministicGuid($"{owner.Id}|{edgeKind}|{signature}");
        return edge;
    }

    private static (List<(string signature, INamedTypeSymbol? symbol)> baseTypes,
                    List<(string signature, INamedTypeSymbol? symbol)> interfaces)
        GetBaseTypesAndInterfaces(TypeDeclarationSyntax typeDecl, SemanticModel? semanticModel)
    {
        var baseTypes = new List<(string, INamedTypeSymbol?)>();
        var interfaces = new List<(string, INamedTypeSymbol?)>();

        if (typeDecl.BaseList is null) return (baseTypes, interfaces);

        foreach (var baseType in typeDecl.BaseList.Types)
        {
            var symbol = semanticModel?.GetTypeInfo(baseType.Type).Type as INamedTypeSymbol;
            var signature = symbol?.OriginalDefinition.ToDisplayString() ?? baseType.Type.ToString();

            if (symbol?.TypeKind == TypeKind.Interface)
                interfaces.Add((signature, symbol));
            else
                baseTypes.Add((signature, symbol));
        }

        return (baseTypes, interfaces);
    }

    /// <summary>
    /// After all chunks are collected, link edges to in-solution target chunks by
    /// matching on signature. External targets remain unresolved (TargetChunkId = null).
    /// </summary>
    private static void ResolveEdgeTargets(AnalysisResult result)
    {
        var bySignature = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var c in result.Chunks)
        {
            if (string.IsNullOrEmpty(c.Signature)) continue;
            bySignature.TryAdd(c.Signature, c.Id);
        }

        foreach (var edge in result.Edges)
        {
            if (bySignature.TryGetValue(edge.TargetSignature, out var id))
            {
                edge.TargetChunkId = id;
                edge.IsExternal = false;
            }
        }
    }

    private static Guid DeterministicGuid(string seed)
    {
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash);
    }

    /// <summary>
    /// Pulls the XML documentation comment off a symbol (including external/library symbols
    /// from reference assemblies). Returns null if the symbol has no doc.
    /// </summary>
    private static string? GetSymbolXmlDoc(ISymbol? symbol)
    {
        if (symbol is null) return null;
        var xml = symbol.GetDocumentationCommentXml();
        return string.IsNullOrWhiteSpace(xml) ? null : xml;
    }

    /// <summary>
    /// Format a parameter, including any attributes (e.g. [FromBody], [Required]).
    /// </summary>
    private static string FormatParameter(IParameterSymbol p)
    {
        var attrs = p.GetAttributes();
        var prefix = attrs.Length == 0
            ? string.Empty
            : string.Concat(attrs.Select(a => $"[{a.AttributeClass?.Name ?? a.ToString()}] "));
        return $"{prefix}{p.Type} {p.Name}";
    }

    private static string? GetXmlDoc(MemberDeclarationSyntax member)
    {
        var trivia = member.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

        var xml = string.Join("\n", trivia.Select(t => t.ToFullString().Trim()));
        return string.IsNullOrWhiteSpace(xml) ? null : xml;
    }

    private static List<string> GetAttributes(MemberDeclarationSyntax member)
    {
        return member.AttributeLists
            .SelectMany(al => al.Attributes)
            .Select(a => a.ToString())
            .ToList();
    }

    /// <summary>
    /// Parses a .slnx (XML solution) file and opens each referenced project via MSBuildWorkspace.
    /// MSBuildWorkspace.OpenSolutionAsync does not support the .slnx format.
    /// </summary>
    private static async Task<IEnumerable<Project>> OpenSlnxProjectsAsync(
        MSBuildWorkspace workspace, string slnxPath)
    {
        var slnxDir = Path.GetDirectoryName(slnxPath)!;
        var doc = XDocument.Load(slnxPath);

        var projectPaths = doc.Descendants()
            .Where(e => e.Name.LocalName == "Project")
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(Path.Combine(slnxDir, p!)))
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                     || p.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var projects = new List<Project>();
        foreach (var path in projectPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"  Skipping missing project: {path}");
                continue;
            }

            var alreadyLoaded = workspace.CurrentSolution.Projects
                .Any(p => string.Equals(p.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (alreadyLoaded) continue;

            await workspace.OpenProjectAsync(path);
        }

        // Return all projects in the solution, including those loaded transitively.
        return workspace.CurrentSolution.Projects;
    }

    private static void EnsureMSBuildRegistered()
    {
        lock (_lock)
        {
            if (_msbuildRegistered) return;
            MSBuildLocator.RegisterDefaults();
            _msbuildRegistered = true;
        }
    }
}

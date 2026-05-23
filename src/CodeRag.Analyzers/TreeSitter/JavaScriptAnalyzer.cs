using System.Security.Cryptography;
using System.Text;
using CodeRag.Core.Models;
using TreeSitter;

namespace CodeRag.Analyzers.TreeSitter;

/// <summary>
/// JavaScript analyzer using tree-sitter.
/// Extracts classes, methods, functions, and call/inheritance edges.
/// Handles: .js (JavaScript), .jsx (JavaScript).
///
/// NOTE: TypeScript (.ts/.tsx) is handled by
/// <see cref="CodeRag.Analyzers.TypeScript.TsCompilerAnalyzer"/>, which uses the
/// real TS Compiler API for symbol resolution. Tree-sitter is retained here only
/// for plain JavaScript where we don't have a project-wide type checker.
/// </summary>
public class JavaScriptAnalyzer : TreeSitterAnalyzerBase
{
    public override string[] SupportedExtensions => [".js", ".jsx"];
    public override string LanguageName => "javascript";

    protected override string GetTreeSitterLanguageName(string extension) => "JavaScript";

    protected override void ExtractFromRoot(Node root, string filePath,
        string? projectName, AnalysisResult result)
    {
        // Synthetic namespace = file directory path (slash → dot), so the Explorer's
        // project → namespace → class tree mirrors the on-disk module layout rather
        // than collapsing every TS chunk into a single "(global namespace)" bucket.
        var fileNs = DeriveFileNamespace(filePath);
        ExtractNodes(root.Children, filePath, projectName, ns: fileNs, className: null, result, []);
    }

    /// <summary>
    /// Derives a dotted namespace from the file's directory path. Returns null for
    /// files at the root. Example: <c>src/services/auth.ts</c> → <c>src.services</c>.
    /// </summary>
    private static string? DeriveFileNamespace(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir)) return null;
        return dir.Replace('\\', '/').Trim('/').Replace('/', '.');
    }

    /// <summary>
    /// Synthetic class name for top-level functions/arrows so they're navigable in the
    /// Explorer (which keys the tree on ClassName). Uses the file's base name without
    /// extension; intentionally not added to <see cref="CodeChunk.Signature"/> so that
    /// call-graph edges (which reference functions by bare name) still resolve.
    /// </summary>
    private static string DeriveFileClassName(string filePath)
        => Path.GetFileNameWithoutExtension(filePath);

    private void ExtractNodes(IReadOnlyList<Node> nodes, string filePath, string? projectName,
        string? ns, string? className, AnalysisResult result, List<string> modifiers)
    {
        foreach (var node in nodes)
            ExtractNode(node, filePath, projectName, ns, className, result, modifiers);
    }

    private void ExtractNode(Node node, string filePath, string? projectName,
        string? ns, string? className, AnalysisResult result, List<string> modifiers)
    {
        switch (node.Type)
        {
            case "export_statement":
            {
                var mods = new List<string>(modifiers) { "export" };
                foreach (var child in node.Children)
                {
                    if (child.Text == "default") { mods.Add("default"); continue; }
                    if (IsDeclarationNode(child.Type))
                        ExtractNode(child, filePath, projectName, ns, className, result, mods);
                }
                break;
            }

            case "function_declaration":
            case "generator_function_declaration":
                ExtractFunction(node, filePath, projectName, ns, className, result, modifiers);
                break;

            case "class_declaration":
            case "abstract_class_declaration":
                ExtractClass(node, filePath, projectName, ns, result, modifiers);
                break;

            case "interface_declaration":
                ExtractInterface(node, filePath, projectName, ns, result, modifiers);
                break;

            case "type_alias_declaration":
                ExtractTypeAlias(node, filePath, projectName, ns, result, modifiers);
                break;

            case "lexical_declaration":
            case "variable_declaration":
                ExtractVariableDeclaration(node, filePath, projectName, ns, result, modifiers);
                break;

            // TypeScript module / namespace declarations
            case "module":
            case "internal_module":
            {
                var nameNode = node.GetChildForField("name");
                var newNs = nameNode?.Text?.Trim('"', '\'');
                if (ns is not null && newNs is not null) newNs = $"{ns}.{newNs}";
                var body = node.GetChildForField("body");
                if (body is not null)
                    ExtractNodes(body.Children, filePath, projectName, newNs ?? ns, null, result, []);
                break;
            }
        }
    }

    private static bool IsDeclarationNode(string type) => type is
        "function_declaration" or "generator_function_declaration" or
        "class_declaration" or "abstract_class_declaration" or
        "interface_declaration" or "type_alias_declaration" or
        "lexical_declaration" or "variable_declaration";

    // ── Function / method extraction ──────────────────────────────────────────

    private void ExtractFunction(Node node, string filePath, string? projectName,
        string? ns, string? className, AnalysisResult result, List<string> modifiers)
    {
        var name = node.GetChildForField("name")?.Text ?? "<anonymous>";
        var paramsNode = node.GetChildForField("parameters");
        var returnTypeNode = node.GetChildForField("return_type");
        var bodyNode = node.GetChildForField("body");

        var parameters = paramsNode is not null ? ExtractParameters(paramsNode) : [];
        var returnType = returnTypeNode is not null ? StripLeadingColon(returnTypeNode.Text) : null;
        var allModifiers = CollectKeywords(node, modifiers, "async");
        // Signature stays unprefixed for top-level functions so call sites that reference
        // them by bare name (e.g. `foo()`) resolve to the same signature.
        var sig = BuildSignature(ns, className, name, parameters, returnType);
        var navClassName = className ?? DeriveFileClassName(filePath);

        var chunk = new CodeChunk
        {
            Kind = className is not null ? "method_declaration" : "function_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = navClassName,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            Body = bodyNode?.Text,
            ReturnType = returnType,
            ProjectName = projectName,
            Modifiers = allModifiers,
            Parameters = parameters,
            Attributes = ExtractDecorators(node),
        };
        result.Chunks.Add(chunk);

        if (bodyNode is not null)
            ExtractCallEdges(bodyNode, chunk, filePath, projectName, result);
    }

    private void ExtractClass(Node node, string filePath, string? projectName,
        string? ns, AnalysisResult result, List<string> modifiers)
    {
        var name = node.GetChildForField("name")?.Text ?? "<anonymous>";
        var sig = ns is not null ? $"{ns}.{name}" : name;
        var allModifiers = new List<string>(modifiers);
        if (node.Type == "abstract_class_declaration") allModifiers.Add("abstract");

        var chunk = new CodeChunk
        {
            Kind = "class_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = name,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            ProjectName = projectName,
            Modifiers = allModifiers,
            Attributes = ExtractDecorators(node),
        };
        result.Chunks.Add(chunk);

        // Heritage — both grammar layouts: with or without a class_heritage wrapper
        foreach (var child in node.Children)
        {
            if (child.Type == "class_heritage")
            {
                foreach (var h in child.Children)
                    ProcessHeritageClause(h, chunk, sig, filePath, projectName, result);
            }
            else
            {
                ProcessHeritageClause(child, chunk, sig, filePath, projectName, result);
            }
        }

        // Members
        var body = node.GetChildForField("body");
        if (body is null) return;

        foreach (var member in body.NamedChildren)
        {
            switch (member.Type)
            {
                case "method_definition":
                    ExtractMethod(member, filePath, projectName, ns, name, result);
                    break;
                case "public_field_definition":
                    ExtractClassField(member, filePath, projectName, ns, name, result);
                    break;
            }
        }
    }

    private void ProcessHeritageClause(Node child, CodeChunk ownerChunk, string ownerSig,
        string filePath, string? projectName, AnalysisResult result)
    {
        if (child.Type == "extends_clause")
        {
            var target = child.GetChildForField("value") ?? child.NamedChildren.FirstOrDefault();
            if (target is not null)
                AddEdge(result, ownerChunk, "inherits", target.Text,
                    targetClassName: target.Text, filePath, StartLine(child), projectName);
        }
        else if (child.Type == "implements_clause")
        {
            foreach (var typeNode in child.NamedChildren)
                AddEdge(result, ownerChunk, "implements", typeNode.Text,
                    targetClassName: typeNode.Text, filePath, StartLine(child), projectName);
        }
    }

    private void ExtractMethod(Node node, string filePath, string? projectName,
        string? ns, string className, AnalysisResult result)
    {
        var name = node.GetChildForField("name")?.Text ?? "<anonymous>";
        var paramsNode = node.GetChildForField("parameters");
        var returnTypeNode = node.GetChildForField("return_type");
        var bodyNode = node.GetChildForField("body");

        var parameters = paramsNode is not null ? ExtractParameters(paramsNode) : [];
        var returnType = returnTypeNode is not null ? StripLeadingColon(returnTypeNode.Text) : null;
        var sig = BuildSignature(ns, className, name, parameters, returnType);

        var chunk = new CodeChunk
        {
            Kind = name == "constructor" ? "constructor_declaration" : "method_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = className,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            Body = bodyNode?.Text,
            ReturnType = returnType,
            ProjectName = projectName,
            Modifiers = CollectMemberModifiers(node),
            Parameters = parameters,
            Attributes = ExtractDecorators(node),
        };
        result.Chunks.Add(chunk);

        if (bodyNode is not null)
            ExtractCallEdges(bodyNode, chunk, filePath, projectName, result);
    }

    private void ExtractClassField(Node node, string filePath, string? projectName,
        string? ns, string className, AnalysisResult result)
    {
        var valueNode = node.GetChildForField("value");
        if (valueNode?.Type is not ("arrow_function" or "function_expression")) return;

        var name = node.GetChildForField("name")?.Text;
        if (name is null) return;

        // Arrow function may use "parameter" (single id) instead of "parameters" (formal list)
        var paramsNode = valueNode.GetChildForField("parameters")
            ?? valueNode.GetChildForField("parameter");
        var returnTypeNode = valueNode.GetChildForField("return_type")
            ?? node.GetChildForField("type");
        var bodyNode = valueNode.GetChildForField("body");

        var parameters = paramsNode?.Type == "formal_parameters"
            ? ExtractParameters(paramsNode)
            : paramsNode is not null ? new List<string> { paramsNode.Text } : [];
        var returnType = returnTypeNode is not null ? StripLeadingColon(returnTypeNode.Text) : null;
        var sig = BuildSignature(ns, className, name, parameters, returnType);

        var chunk = new CodeChunk
        {
            Kind = "method_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = className,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            Body = bodyNode?.Text,
            ReturnType = returnType,
            ProjectName = projectName,
            Modifiers = CollectMemberModifiers(node),
            Parameters = parameters,
        };
        result.Chunks.Add(chunk);

        if (bodyNode is not null)
            ExtractCallEdges(bodyNode, chunk, filePath, projectName, result);
    }

    private void ExtractInterface(Node node, string filePath, string? projectName,
        string? ns, AnalysisResult result, List<string> modifiers)
    {
        var name = node.GetChildForField("name")?.Text ?? "<anonymous>";
        var sig = ns is not null ? $"{ns}.{name}" : name;

        result.Chunks.Add(new CodeChunk
        {
            Kind = "interface_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = name,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            ProjectName = projectName,
            Modifiers = new List<string>(modifiers),
        });

        var body = node.GetChildForField("body");
        if (body is null) return;

        foreach (var member in body.NamedChildren)
        {
            if (member.Type is not ("method_signature" or "property_signature" or "call_signature"))
                continue;

            var memberName = member.GetChildForField("name")?.Text;
            if (memberName is null) continue;

            var paramsNode = member.GetChildForField("parameters");
            var returnTypeNode = member.GetChildForField("return_type")
                ?? member.GetChildForField("type");
            var parameters = paramsNode is not null ? ExtractParameters(paramsNode) : [];
            var returnType = returnTypeNode is not null ? StripLeadingColon(returnTypeNode.Text) : null;
            var memberSig = BuildSignature(ns, name, memberName, parameters, returnType);

            result.Chunks.Add(new CodeChunk
            {
                Kind = "method_declaration",
                Language = LanguageName,
                Namespace = ns,
                ClassName = name,
                FunctionName = memberName,
                Signature = memberSig,
                FilePath = filePath,
                LineNumber = StartLine(member),
                EndLineNumber = EndLine(member),
                Documentation = GetPrecedingJsDoc(member),
                ReturnType = returnType,
                ProjectName = projectName,
                Parameters = parameters,
            });
        }
    }

    private void ExtractTypeAlias(Node node, string filePath, string? projectName,
        string? ns, AnalysisResult result, List<string> modifiers)
    {
        var name = node.GetChildForField("name")?.Text ?? "<anonymous>";
        var sig = ns is not null ? $"{ns}.{name}" : name;

        result.Chunks.Add(new CodeChunk
        {
            Kind = "type_alias_declaration",
            Language = LanguageName,
            Namespace = ns,
            ClassName = name,
            FunctionName = name,
            Signature = sig,
            FilePath = filePath,
            LineNumber = StartLine(node),
            EndLineNumber = EndLine(node),
            Documentation = GetPrecedingJsDoc(node),
            Body = node.Text,
            ProjectName = projectName,
            Modifiers = new List<string>(modifiers),
        });
    }

    private void ExtractVariableDeclaration(Node node, string filePath, string? projectName,
        string? ns, AnalysisResult result, List<string> modifiers)
    {
        var kindText = node.Children
            .FirstOrDefault(c => c.Text is "const" or "let" or "var")?.Text ?? "let";
        var allModifiers = new List<string>(modifiers) { kindText };

        foreach (var declarator in node.NamedChildren.Where(c => c.Type == "variable_declarator"))
        {
            var nameNode = declarator.GetChildForField("name");
            var valueNode = declarator.GetChildForField("value");
            if (nameNode is null || valueNode is null) continue;
            if (valueNode.Type is not ("arrow_function" or "function_expression")) continue;

            var name = nameNode.Text;
            var paramsNode = valueNode.GetChildForField("parameters")
                ?? valueNode.GetChildForField("parameter");
            var returnTypeNode = valueNode.GetChildForField("return_type")
                ?? declarator.GetChildForField("type");
            var bodyNode = valueNode.GetChildForField("body");

            var parameters = paramsNode?.Type == "formal_parameters"
                ? ExtractParameters(paramsNode)
                : paramsNode is not null ? new List<string> { paramsNode.Text } : [];
            var returnType = returnTypeNode is not null ? StripLeadingColon(returnTypeNode.Text) : null;
            var sig = BuildSignature(ns, className: null, name, parameters, returnType);

            var chunk = new CodeChunk
            {
                Kind = "function_declaration",
                Language = LanguageName,
                Namespace = ns,
                ClassName = DeriveFileClassName(filePath),
                FunctionName = name,
                Signature = sig,
                FilePath = filePath,
                LineNumber = StartLine(node),
                EndLineNumber = EndLine(declarator),
                Documentation = GetPrecedingJsDoc(node),
                Body = bodyNode?.Text,
                ReturnType = returnType,
                ProjectName = projectName,
                Modifiers = allModifiers,
                Parameters = parameters,
            };
            result.Chunks.Add(chunk);

            if (bodyNode is not null)
                ExtractCallEdges(bodyNode, chunk, filePath, projectName, result);
        }
    }

    private void ExtractCallEdges(Node body, CodeChunk owner, string filePath,
        string? projectName, AnalysisResult result)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var calls = new List<string>();

        // Pre-pass: record (startRow, startCol, endRow, endCol) of every
        // member_expression that is a call or new callee so we can skip those
        // in the reads pass below (they're emitted as "calls"/"creates" instead).
        var calleePositions = new HashSet<(int sr, int sc, int er, int ec)>();
        foreach (var node in Descendants(body))
        {
            if (node.Type == "call_expression")
            {
                var fn = node.GetChildForField("function");
                if (fn is not null && fn.Type == "member_expression")
                    calleePositions.Add((fn.StartPosition.Row, fn.StartPosition.Column,
                                         fn.EndPosition.Row, fn.EndPosition.Column));
            }
            else if (node.Type == "new_expression")
            {
                var ctor = node.GetChildForField("constructor");
                if (ctor is not null && ctor.Type == "member_expression")
                    calleePositions.Add((ctor.StartPosition.Row, ctor.StartPosition.Column,
                                         ctor.EndPosition.Row, ctor.EndPosition.Column));
            }
        }

        foreach (var node in Descendants(body))
        {
            if (node.Type == "call_expression")
            {
                var funcNode = node.GetChildForField("function");
                if (funcNode is null) continue;
                var targetSig = funcNode.Text;
                var line = StartLine(node);
                if (!seen.Add($"calls|{targetSig}|{line}")) continue;

                result.Edges.Add(new CodeEdge
                {
                    Id = DeterministicGuid($"{owner.Id}|calls|{targetSig}|{line}"),
                    SourceChunkId = owner.Id,
                    SourceSignature = owner.Signature ?? owner.FunctionName,
                    TargetSignature = targetSig,
                    TargetMemberName = funcNode.Type == "member_expression"
                        ? funcNode.GetChildForField("property")?.Text
                        : targetSig,
                    EdgeKind = "calls",
                    IsExternal = true,
                    FilePath = filePath,
                    LineNumber = line,
                    ProjectName = projectName,
                    Language = LanguageName,
                });
                calls.Add(targetSig);
            }
            else if (node.Type == "new_expression")
            {
                var ctorNode = node.GetChildForField("constructor");
                if (ctorNode is null) continue;
                var targetSig = ctorNode.Text;
                var line = StartLine(node);
                if (!seen.Add($"creates|{targetSig}|{line}")) continue;

                result.Edges.Add(new CodeEdge
                {
                    Id = DeterministicGuid($"{owner.Id}|creates|{targetSig}|{line}"),
                    SourceChunkId = owner.Id,
                    SourceSignature = owner.Signature ?? owner.FunctionName,
                    TargetSignature = targetSig,
                    TargetClassName = targetSig,
                    TargetMemberName = ".ctor",
                    EdgeKind = "creates",
                    IsExternal = true,
                    FilePath = filePath,
                    LineNumber = line,
                    ProjectName = projectName,
                    Language = LanguageName,
                });
                calls.Add($"new {targetSig}");
            }
            else if (node.Type == "member_expression")
            {
                // Skip member expressions that were already emitted as call/new callees.
                var pos = (node.StartPosition.Row, node.StartPosition.Column,
                           node.EndPosition.Row, node.EndPosition.Column);
                if (calleePositions.Contains(pos)) continue;

                // Without a type checker, restrict to `this.x` accesses to avoid noise.
                var objNode = node.GetChildForField("object");
                var propNode = node.GetChildForField("property");
                if (objNode?.Text != "this" || propNode is null) continue;

                var memberName = propNode.Text;
                var line = StartLine(node);
                if (!seen.Add($"reads|{memberName}|{line}")) continue;

                result.Edges.Add(new CodeEdge
                {
                    Id = DeterministicGuid($"{owner.Id}|reads|{memberName}|{line}"),
                    SourceChunkId = owner.Id,
                    SourceSignature = owner.Signature ?? owner.FunctionName,
                    TargetSignature = memberName,
                    TargetMemberName = memberName,
                    EdgeKind = "reads",
                    IsExternal = true,
                    FilePath = filePath,
                    LineNumber = line,
                    ProjectName = projectName,
                    Language = LanguageName,
                });
                calls.Add(memberName);
            }
        }

        owner.Calls = calls.Distinct(StringComparer.Ordinal).ToList();
    }

    private void AddEdge(AnalysisResult result, CodeChunk owner, string edgeKind,
        string targetSig, string? targetClassName, string filePath, int line, string? projectName)
    {
        result.Edges.Add(new CodeEdge
        {
            Id = DeterministicGuid($"{owner.Id}|{edgeKind}|{targetSig}"),
            SourceChunkId = owner.Id,
            SourceSignature = owner.Signature ?? owner.FunctionName,
            TargetSignature = targetSig,
            TargetClassName = targetClassName,
            EdgeKind = edgeKind,
            IsExternal = true,
            FilePath = filePath,
            LineNumber = line,
            ProjectName = projectName,
            Language = LanguageName,
        });
    }

    private static List<string> ExtractParameters(Node formalParams)
    {
        var result = new List<string>();
        foreach (var param in formalParams.NamedChildren)
        {
            string formatted;
            switch (param.Type)
            {
                case "required_parameter":
                case "optional_parameter":
                {
                    var patternNode = param.GetChildForField("pattern")
                        ?? param.GetChildForField("name");
                    var typeNode = param.GetChildForField("type");
                    var paramName = patternNode?.Text ?? param.Text;
                    var paramType = typeNode is not null ? StripLeadingColon(typeNode.Text) : null;
                    var opt = param.Type == "optional_parameter" ? "?" : "";
                    formatted = paramType is not null ? $"{paramName}{opt}: {paramType}" : paramName;
                    break;
                }
                case "rest_parameter":
                {
                    var patternNode = param.GetChildForField("pattern")
                        ?? param.GetChildForField("name");
                    var typeNode = param.GetChildForField("type");
                    var paramName = patternNode?.Text ?? "rest";
                    var paramType = typeNode is not null ? StripLeadingColon(typeNode.Text) : null;
                    formatted = paramType is not null ? $"...{paramName}: {paramType}" : $"...{paramName}";
                    break;
                }
                default:
                    // assignment_pattern, destructuring_pattern, identifier, etc.
                    formatted = param.Text.Length > 60 ? $"<{param.Type}>" : param.Text;
                    break;
            }
            result.Add(formatted);
        }
        return result;
    }

    private static readonly HashSet<string> MemberKeywords = new(StringComparer.Ordinal)
        { "public", "private", "protected", "static", "async", "readonly",
          "abstract", "override", "declare", "accessor" };

    private static List<string> CollectKeywords(Node node, List<string> inherited, params string[] extra)
    {
        var extraSet = new HashSet<string>(extra, StringComparer.Ordinal);
        var result = new List<string>(inherited);
        foreach (var child in node.Children)
            if (child.Text is not null && extraSet.Contains(child.Text))
                result.Add(child.Text);
        return result;
    }

    private static List<string> CollectMemberModifiers(Node node)
    {
        var result = new List<string>();
        foreach (var child in node.Children)
            if (child.Text is not null && MemberKeywords.Contains(child.Text))
                result.Add(child.Text);
        return result;
    }

    private static List<string> ExtractDecorators(Node node) =>
        node.Children.Where(c => c.Type == "decorator").Select(c => c.Text).ToList();

    private static string BuildSignature(string? ns, string? className, string name,
        List<string> parameters, string? returnType)
    {
        var prefix = (ns, className) switch
        {
            ({ } n, { } c) => $"{n}.{c}.",
            (null, { } c) => $"{c}.",
            ({ } n, null) => $"{n}.",
            _ => "",
        };
        var retStr = returnType is not null ? $": {returnType}" : "";
        return $"{prefix}{name}({string.Join(", ", parameters)}){retStr}";
    }

    private static string StripLeadingColon(string text) => text.TrimStart(':', ' ').Trim();
}

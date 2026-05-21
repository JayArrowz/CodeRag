namespace CodeRag.Core.Models;

/// <summary>
/// Represents a single extracted code element (method, class, property, library call, etc.)
/// that will be stored in the vector database for RAG retrieval.
/// </summary>
public class CodeChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // --- Classification ---
    /// <summary>
    /// The kind of code element: method_declaration, class_declaration, property_declaration,
    /// constructor, library_call, interface_declaration, enum_declaration, etc.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The source language: csharp, python, typescript, go, rust, etc.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    // --- Identity ---
    public string? Namespace { get; set; }
    public string? ClassName { get; set; }
    public string FunctionName { get; set; } = string.Empty;
    public string? Signature { get; set; }

    // --- Location ---
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public int EndLineNumber { get; set; }

    // --- Content ---
    /// <summary>
    /// XML doc or docstring extracted from the source.
    /// </summary>
    public string? Documentation { get; set; }

    /// <summary>
    /// Full source body of the element. Stored for retrieval, not embedded directly.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Compressed/summarized version of the body used for embedding.
    /// Falls back to truncated body if no LLM summary is available.
    /// </summary>
    public string? BodySummary { get; set; }

    // --- Library info (for library_call kind) ---
    public string? LibraryAssembly { get; set; }
    public string? LibraryPackage { get; set; }

    // --- Metadata ---
    /// <summary>
    /// Logical workspace this chunk belongs to (e.g. "CodeRag", "MyCompanyMonorepo").
    /// All chunks/edges indexed in a single run share the same workspace. Required.
    /// </summary>
    public string Workspace { get; set; } = string.Empty;

    /// <summary>
    /// Inner project name within the workspace (e.g. a single .csproj inside a solution).
    /// Optional sub-grouping.
    /// </summary>
    public string? ProjectName { get; set; }
    public List<string> Modifiers { get; set; } = [];
    public string? ReturnType { get; set; }
    public List<string> Parameters { get; set; } = [];
    public List<string> Attributes { get; set; } = [];

    // --- Graph context (denormalized for fast retrieval & embedding) ---
    /// <summary>
    /// Fully-qualified signatures of methods invoked from inside this element's body.
    /// Populated for methods/constructors. Mirrors the data in the edges table
    /// but kept on the chunk so a single retrieval surfaces callee context.
    /// </summary>
    public List<string> Calls { get; set; } = [];

    /// <summary>Base classes (excluding object). Populated for type chunks.</summary>
    public List<string> BaseTypes { get; set; } = [];

    /// <summary>Interfaces implemented. Populated for type chunks.</summary>
    public List<string> Interfaces { get; set; } = [];
    
    /// <summary>
    /// The embedding vector, populated by the embedding service before storage.
    /// </summary>
    public float[]? Embedding { get; set; }

    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Text used to generate the embedding vector. Compact, semantic-rich.
    /// </summary>
    public string ToEmbeddingText()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Namespace))
            parts.Add($"namespace {Namespace}");

        if (!string.IsNullOrEmpty(ClassName))
            parts.Add($"class {ClassName}");

        if (Modifiers.Count > 0)
            parts.Add(string.Join(" ", Modifiers));

        if (Attributes.Count > 0)
            parts.Add(string.Join(" ", Attributes.Select(a => $"[{a}]")));

        if (!string.IsNullOrEmpty(Signature))
            parts.Add(Signature);
        else
            parts.Add(FunctionName);

        if (!string.IsNullOrEmpty(Documentation))
            parts.Add(Documentation);

        if (!string.IsNullOrEmpty(BodySummary))
            parts.Add(BodySummary);
        else if (!string.IsNullOrEmpty(Body))
            parts.Add(TruncateBody(Body, maxLines: 30));

        if (!string.IsNullOrEmpty(LibraryAssembly))
            parts.Add($"[lib: {LibraryAssembly}]");

        if (BaseTypes.Count > 0)
            parts.Add($"inherits: {string.Join(", ", BaseTypes)}");

        if (Interfaces.Count > 0)
            parts.Add($"implements: {string.Join(", ", Interfaces)}");

        if (Calls.Count > 0)
        {
            // Cap to keep embedding input bounded.
            var sample = Calls.Take(25).ToList();
            parts.Add($"calls: {string.Join(", ", sample)}");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Full text returned when this chunk is retrieved as context for an LLM prompt.
    /// Includes all semantic metadata captured at index time so the model sees the
    /// same intent signals (attributes, modifiers, graph relationships) the embedder did.
    /// </summary>
    /// <param name="charBudget">
    /// Soft character cap (~4 chars per LLM token). When the chunk's body would exceed
    /// the remaining budget, <see cref="BodySummary"/> is used instead, or the body is
    /// line-truncated. Pass <see cref="int.MaxValue"/> for no cap.
    /// </param>
    public string ToRetrievalText(int charBudget = int.MaxValue)
    {
        var parts = new List<string>
        {
            $"// {FilePath}:{LineNumber}-{EndLineNumber}  ({Language}{(string.IsNullOrEmpty(ProjectName) ? "" : $", project: {ProjectName}")})",
        };

        if (!string.IsNullOrEmpty(Namespace) || !string.IsNullOrEmpty(ClassName))
        {
            var loc = string.IsNullOrEmpty(Namespace)
                ? ClassName
                : (string.IsNullOrEmpty(ClassName) ? Namespace : $"{Namespace}.{ClassName}");
            parts.Add($"// in {loc}  [{Kind}]");
        }
        else
        {
            parts.Add($"// [{Kind}]");
        }

        if (Attributes.Count > 0)
            parts.Add(string.Join(" ", Attributes.Select(a => $"[{a}]")));

        var header = Modifiers.Count > 0
            ? $"{string.Join(" ", Modifiers)} {Signature ?? FunctionName}"
            : (Signature ?? FunctionName);
        parts.Add(header);

        if (BaseTypes.Count > 0)
            parts.Add($"// inherits: {string.Join(", ", BaseTypes)}");

        if (Interfaces.Count > 0)
            parts.Add($"// implements: {string.Join(", ", Interfaces)}");

        if (!string.IsNullOrEmpty(Documentation))
            parts.Add(Documentation);

        // Cheap running tally; metadata above is small.
        var headerCost = parts.Sum(p => p.Length + 1);
        var bodyRoom = charBudget - headerCost - 200 /* slack for trailing sections */;

        if (!string.IsNullOrEmpty(Body))
        {
            if (bodyRoom >= Body.Length || charBudget == int.MaxValue)
            {
                parts.Add(Body);
            }
            else if (!string.IsNullOrEmpty(BodySummary) && BodySummary.Length <= Math.Max(bodyRoom, 100))
            {
                parts.Add(BodySummary);
                parts.Add("// (body truncated — summary above)");
            }
            else if (bodyRoom > 200)
            {
                // Line-truncate to fit.
                var lines = Body.Split('\n');
                var sb = new System.Text.StringBuilder();
                foreach (var line in lines)
                {
                    if (sb.Length + line.Length + 1 > bodyRoom) break;
                    sb.AppendLine(line);
                }
                sb.Append("// ... truncated");
                parts.Add(sb.ToString());
            }
            else if (!string.IsNullOrEmpty(BodySummary))
            {
                parts.Add(BodySummary);
            }
        }

        if (Calls.Count > 0)
        {
            var sample = Calls.Take(15).ToList();
            var suffix = Calls.Count > sample.Count ? $", … (+{Calls.Count - sample.Count} more)" : "";
            parts.Add($"// calls: {string.Join(", ", sample)}{suffix}");
        }

        return string.Join("\n", parts);
    }

    private static string TruncateBody(string body, int maxLines)
    {
        var lines = body.Split('\n');
        if (lines.Length <= maxLines)
            return body;

        return string.Join("\n", lines.Take(maxLines)) + "\n// ... truncated";
    }
}

namespace CodeRag.Core.Interfaces;

/// <summary>
/// Multi-valued retrieval filter. Each list is an OR-set; lists are AND-combined.
/// String matches are case-insensitive. Empty / null lists are ignored.
/// </summary>
public class SearchFilter
{
    /// <summary>Restrict to these workspaces. Empty = all workspaces.</summary>
    public List<string> Workspaces { get; set; } = [];

    /// <summary>Restrict to these languages (csharp, python, …). Empty = all.</summary>
    public List<string> Languages { get; set; } = [];

    /// <summary>Restrict to these project names. Empty = all projects.</summary>
    public List<string> Projects { get; set; } = [];

    /// <summary>Restrict to these chunk kinds (method_declaration, class_declaration, …).</summary>
    public List<string> Kinds { get; set; } = [];

    /// <summary>File-path substring filters; a chunk passes if any substring matches.</summary>
    public List<string> FilePathContains { get; set; } = [];

    /// <summary>File-path substrings to EXCLUDE (e.g. "tests/", "examples/").</summary>
    public List<string> ExcludeFilePathContains { get; set; } = [];

    // -------- convenience single-value setters (assign-only sugar) --------
    public string? Workspace { set { if (!string.IsNullOrEmpty(value)) Workspaces = [value]; } }
    public string? Language { set { if (!string.IsNullOrEmpty(value)) Languages = [value]; } }
    public string? ProjectName { set { if (!string.IsNullOrEmpty(value)) Projects = [value]; } }
    public string? Kind { set { if (!string.IsNullOrEmpty(value)) Kinds = [value]; } }
    public string? FilePath { set { if (!string.IsNullOrEmpty(value)) FilePathContains = [value]; } }

    // -------- convenience kind groups --------
    public static readonly string[] MethodKinds = ["method_declaration", "constructor_declaration"];
    public static readonly string[] TypeKinds = ["class_declaration", "record_declaration", "struct_declaration", "interface_declaration", "enum_declaration"];
    public static readonly string[] PropertyKinds = ["property_declaration"];
    public static readonly string[] LibraryCallKinds = ["library_call"];

    public SearchFilter IncludeMethods()      { foreach (var k in MethodKinds)       if (!Kinds.Contains(k)) Kinds.Add(k); return this; }
    public SearchFilter IncludeTypes()        { foreach (var k in TypeKinds)         if (!Kinds.Contains(k)) Kinds.Add(k); return this; }
    public SearchFilter IncludeProperties()   { foreach (var k in PropertyKinds)     if (!Kinds.Contains(k)) Kinds.Add(k); return this; }
    public SearchFilter IncludeLibraryCalls() { foreach (var k in LibraryCallKinds)  if (!Kinds.Contains(k)) Kinds.Add(k); return this; }
}

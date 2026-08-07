namespace FlowLens.Core;

/// <summary>
/// One <c>MethodDeclarationSyntax</c> found in the solution.
/// <para>
/// This is syntax-only: no symbol was resolved, so <see cref="Name"/> and
/// <see cref="ContainingType"/> are the identifiers as written in the file. That is exactly
/// what Phase 1 is meant to demonstrate - see <see cref="SemanticModelDemo"/> for what the
/// same method looks like once a SemanticModel binds those names to symbols.
/// </para>
/// </summary>
/// <param name="FilePath">Path relative to the solution directory, forward slashes.</param>
/// <param name="Line">1-based line of the method identifier.</param>
public sealed record MethodRecord(
    string Name,
    string ContainingType,
    string FilePath,
    int Line,
    string Module,
    string? Layer,
    string Project,
    bool IsTestProject)
{
    public string Location => $"{FilePath}:{Line}";
}

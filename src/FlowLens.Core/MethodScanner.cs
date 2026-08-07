using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>
/// Walks every syntax tree in the solution and records method declarations.
/// <para>
/// Deliberately does NOT build compilations. A SyntaxTree is produced by the parser alone, so
/// this stays fast (seconds) where <c>GetCompilationAsync</c> over the whole solution costs
/// minutes. Phase 1's acceptance criteria only need declaration counts.
/// </para>
/// <para>
/// Scope limit: <c>MethodDeclarationSyntax</c> covers ordinary methods only - not lambdas,
/// constructors, local functions, property accessors or record-generated members. For
/// ModularCommerce that omission is significant and is documented in docs/known-limitations.md.
/// </para>
/// </summary>
public static class MethodScanner
{
    public static async Task<ScanResult> ScanAsync(
        Solution solution,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var methods = new List<MethodRecord>();
        var documentCount = 0;
        var skippedProjects = new List<string>();

        foreach (var project in solution.Projects.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Non-C# or otherwise unanalyzable projects would silently contribute zero
            // methods; record them so a zero is never mistaken for "no methods here".
            if (project.Language != LanguageNames.CSharp || !project.SupportsCompilation)
            {
                skippedProjects.Add(project.Name);
                continue;
            }

            var info = ProjectClassifier.Classify(project.FilePath ?? project.Name, project.Name);

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                documentCount++;

                var relativePath = ToRelativePath(document.FilePath, solutionDirectory);

                foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    methods.Add(new MethodRecord(
                        Name: declaration.Identifier.Text,
                        ContainingType: ResolveContainingTypeName(declaration),
                        FilePath: relativePath,
                        // Point at the identifier, not the attribute list or modifiers that
                        // may precede it - that is the line a human wants to open.
                        Line: declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Module: info.Module,
                        Layer: info.Layer,
                        Project: project.Name,
                        IsTestProject: info.IsTest));
                }
            }
        }

        return new ScanResult(methods, documentCount, skippedProjects);
    }

    /// <summary>
    /// Nearest enclosing type, qualified with its outer types for nested declarations
    /// (Outer+Inner), so two same-named nested helpers stay distinguishable.
    /// </summary>
    private static string ResolveContainingTypeName(MethodDeclarationSyntax declaration)
    {
        var names = declaration.Ancestors()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.Text)
            .Reverse()
            .ToArray();

        return names.Length == 0 ? "(global)" : string.Join('+', names);
    }

    private static string ToRelativePath(string? absolutePath, string solutionDirectory)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return "(no file)";
        }

        var relative = Path.GetRelativePath(solutionDirectory, absolutePath);
        return relative.Replace('\\', '/');
    }
}

/// <param name="SkippedProjects">Projects that could not be scanned (non-C# or no compilation support).</param>
public sealed record ScanResult(
    IReadOnlyList<MethodRecord> Methods,
    int DocumentCount,
    IReadOnlyList<string> SkippedProjects);

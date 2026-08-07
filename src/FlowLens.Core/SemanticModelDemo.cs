using Microsoft.CodeAnalysis;
// Brings in CSharpExtensions, whose GetDeclaredSymbol overloads return the precise symbol type
// (IMethodSymbol here) instead of the language-agnostic ISymbol from the base SemanticModel.
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>Which method the demo should dissect. Defaults point at ModularCommerce's richest handler.</summary>
public sealed record SemanticDemoTarget(string ProjectName, string TypeName, string MethodName)
{
    /// <summary>
    /// CheckoutHandler.HandleAsync is the best teaching example in the target repo: its body
    /// calls into four different modules through Contracts interfaces, so the gap between
    /// "what the text says" and "what the symbol is" is at its widest.
    /// </summary>
    public static SemanticDemoTarget Default { get; } =
        new("ModularCommerce.Ordering.Application", "CheckoutHandler", "HandleAsync");
}

/// <param name="SyntaxView">What the SyntaxTree alone can tell us - identifiers as typed.</param>
/// <param name="SemanticView">What the SemanticModel resolves that identifier to.</param>
public sealed record InvocationComparison(
    int Line,
    string SyntaxView,
    string SemanticView,
    bool Resolved,
    bool IsInterfaceMember,
    string? ContainingAssembly);

public sealed record SignatureComparison(
    string SyntaxReturnType,
    string SemanticReturnType,
    IReadOnlyList<string> SyntaxParameters,
    IReadOnlyList<string> SemanticParameters,
    string SemanticContainingType,
    string SemanticAssembly);

public sealed record SemanticDemoResult(
    bool UsedPreferredTarget,
    string FallbackReason,
    string ProjectName,
    string TypeName,
    string MethodName,
    string FilePath,
    int Line,
    SignatureComparison Signature,
    /// <summary>A capped slice of <see cref="TotalInvocationCount"/>, for display.</summary>
    IReadOnlyList<InvocationComparison> Invocations,
    /// <summary>Every invocation in the body, not just the displayed ones.</summary>
    int TotalInvocationCount,
    int ResolvedInvocationCount,
    int InterfaceInvocationCount);

/// <summary>
/// Phase 1's whole point, in one place: the same method, read twice.
/// <para>
/// A <c>SyntaxTree</c> is what the parser produces. It knows the shape of the code and the
/// spelling of every identifier, and nothing else. In <c>orders.GetByIdempotencyKeyAsync(...)</c>
/// the tree holds two strings; it cannot tell you what <c>orders</c> is, which assembly the
/// method lives in, or whether it is even a real method.
/// </para>
/// <para>
/// A <c>SemanticModel</c> is that tree plus the <c>Compilation</c> - all references resolved,
/// all names bound to symbols. Asking it about the same node yields an <c>IMethodSymbol</c>
/// with a fully-qualified name, a declaring type and a containing assembly. That binding is
/// what makes a call graph possible, which is why Phase 2 is built entirely on it.
/// </para>
/// </summary>
public static class SemanticModelDemo
{
    /// <summary>For types: namespace-qualified, no "global::" noise.</summary>
    private static readonly SymbolDisplayFormat QualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>
    /// For methods. FullyQualifiedFormat is tuned for types and omits a member's declaring
    /// type, which would render the interesting part of an invocation as a bare "ValidateAsync".
    /// Including the containing type and the parameter list is what makes the semantic view
    /// actually more informative than the syntax text next to it.
    /// </summary>
    private static readonly SymbolDisplayFormat MemberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private const int MaxInvocationsShown = 12;

    public static async Task<SemanticDemoResult?> RunAsync(
        Solution solution,
        SemanticDemoTarget target,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var located = await LocateAsync(solution, target, cancellationToken);
        if (located is null)
        {
            return null;
        }

        var (project, document, declaration, usedPreferred, fallbackReason) = located.Value;

        // A Compilation for ONE project (plus its project references), not the whole solution.
        // Compiling all 68 projects would cost minutes and buys nothing here.
        var compilation = await project.GetCompilationAsync(cancellationToken)
            ?? throw new InvalidOperationException($"No compilation for project {project.Name}");

        var tree = await document.GetSyntaxTreeAsync(cancellationToken)
            ?? throw new InvalidOperationException($"No syntax tree for {document.FilePath}");

        var semanticModel = compilation.GetSemanticModel(tree);

        var signature = CompareSignature(declaration, semanticModel, cancellationToken);
        var invocations = CompareInvocations(declaration, semanticModel, cancellationToken);

        return new SemanticDemoResult(
            UsedPreferredTarget: usedPreferred,
            FallbackReason: fallbackReason,
            ProjectName: project.Name,
            TypeName: declaration.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "(global)",
            MethodName: declaration.Identifier.Text,
            FilePath: document.FilePath is null
                ? "(no file)"
                : Path.GetRelativePath(solutionDirectory, document.FilePath).Replace('\\', '/'),
            Line: declaration.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            Signature: signature,
            Invocations: [.. invocations.Take(MaxInvocationsShown)],
            TotalInvocationCount: invocations.Count,
            ResolvedInvocationCount: invocations.Count(i => i.Resolved),
            InterfaceInvocationCount: invocations.Count(i => i.IsInterfaceMember));
    }

    private static SignatureComparison CompareSignature(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        // GetDeclaredSymbol is the declaration-side counterpart of GetSymbolInfo: it maps a
        // declaration node to the symbol it introduces, rather than resolving a reference.
        var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);

        var syntaxParameters = declaration.ParameterList.Parameters
            .Select(p => $"{p.Type?.ToString() ?? "?"} {p.Identifier.Text}")
            .ToArray();

        if (symbol is null)
        {
            return new SignatureComparison(
                declaration.ReturnType.ToString(),
                "(unresolved)",
                syntaxParameters,
                [],
                "(unresolved)",
                "(unresolved)");
        }

        return new SignatureComparison(
            // As written in the file - relative, ambiguous, dependent on the using directives
            // at the top of the document.
            SyntaxReturnType: declaration.ReturnType.ToString(),
            // Fully qualified and unambiguous, independent of usings.
            SemanticReturnType: symbol.ReturnType.ToDisplayString(QualifiedFormat),
            SyntaxParameters: syntaxParameters,
            SemanticParameters:
            [
                .. symbol.Parameters.Select(p =>
                    $"{p.Type.ToDisplayString(QualifiedFormat)} {p.Name}")
            ],
            SemanticContainingType: symbol.ContainingType.ToDisplayString(QualifiedFormat),
            SemanticAssembly: symbol.ContainingAssembly?.Name ?? "(none)");
    }

    private static List<InvocationComparison> CompareInvocations(
        MethodDeclarationSyntax declaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var results = new List<InvocationComparison>();

        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Syntax view: the raw callee text. "orders.GetByIdempotencyKeyAsync" is two
            // identifiers glued by a dot - the tree cannot say what "orders" refers to.
            var syntaxView = invocation.Expression.ToString();
            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            // Semantic view: bind that expression to a symbol.
            var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = info.Symbol as IMethodSymbol
                // Symbol is null for overload-resolution failures; the candidates still tell
                // us what the compiler considered. Phase 2 will need this distinction.
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            if (symbol is null)
            {
                results.Add(new InvocationComparison(
                    line,
                    syntaxView,
                    $"(unresolved: {info.CandidateReason})",
                    Resolved: false,
                    IsInterfaceMember: false,
                    ContainingAssembly: null));
                continue;
            }

            // The Phase 2 "interface problem" in miniature: the call binds to the INTERFACE
            // member, not to the concrete implementation that DI will supply at runtime.
            // Static analysis cannot close that gap without SymbolFinder.
            var isInterfaceMember = symbol.ContainingType?.TypeKind == TypeKind.Interface;

            results.Add(new InvocationComparison(
                line,
                syntaxView,
                symbol.ToDisplayString(MemberFormat),
                Resolved: info.Symbol is not null,
                IsInterfaceMember: isInterfaceMember,
                ContainingAssembly: symbol.ContainingAssembly?.Name));
        }

        return results;
    }

    private static async Task<(Project Project, Document Document, MethodDeclarationSyntax Declaration, bool UsedPreferred, string FallbackReason)?> LocateAsync(
        Solution solution,
        SemanticDemoTarget target,
        CancellationToken cancellationToken)
    {
        var candidates = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.SupportsCompilation)
            .ToList();

        // 1. Preferred: exact project, type and method.
        var preferredProject = candidates.FirstOrDefault(p =>
            p.Name.Equals(target.ProjectName, StringComparison.OrdinalIgnoreCase));

        if (preferredProject is not null)
        {
            var hit = await FindMethodAsync(preferredProject, target.TypeName, target.MethodName, cancellationToken);
            if (hit is not null)
            {
                return (preferredProject, hit.Value.Document, hit.Value.Declaration, true, string.Empty);
            }
        }

        // 2. The type may have moved to another project - look for it solution-wide.
        foreach (var project in candidates)
        {
            var hit = await FindMethodAsync(project, target.TypeName, target.MethodName, cancellationToken);
            if (hit is not null)
            {
                return (project, hit.Value.Document, hit.Value.Declaration, false,
                    $"project '{target.ProjectName}' not found; located {target.TypeName} in '{project.Name}' instead");
            }
        }

        // 3. Generic fallback so the demo works against any solution: the method with the
        //    most invocations has the most to show.
        foreach (var project in candidates.Where(p =>
            !ProjectClassifier.Classify(p.FilePath ?? p.Name, p.Name).IsTest))
        {
            var richest = await FindRichestMethodAsync(project, cancellationToken);
            if (richest is not null)
            {
                return (project, richest.Value.Document, richest.Value.Declaration, false,
                    $"neither project '{target.ProjectName}' nor type '{target.TypeName}' was found; " +
                    "fell back to the call-densest method available");
            }
        }

        return null;
    }

    private static async Task<(Document Document, MethodDeclarationSyntax Declaration)?> FindMethodAsync(
        Project project,
        string typeName,
        string methodName,
        CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
            {
                continue;
            }

            var declaration = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m =>
                    m.Identifier.Text.Equals(methodName, StringComparison.Ordinal)
                    && m.Ancestors().OfType<BaseTypeDeclarationSyntax>()
                        .Any(t => t.Identifier.Text.Equals(typeName, StringComparison.Ordinal)));

            if (declaration is not null)
            {
                return (document, declaration);
            }
        }

        return null;
    }

    private static async Task<(Document Document, MethodDeclarationSyntax Declaration)?> FindRichestMethodAsync(
        Project project,
        CancellationToken cancellationToken)
    {
        (Document Document, MethodDeclarationSyntax Declaration)? best = null;
        var bestCount = 0;

        foreach (var document in project.Documents)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            if (root is null)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var count = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>().Count();
                if (count > bestCount)
                {
                    bestCount = count;
                    best = (document, declaration);
                }
            }
        }

        return bestCount > 0 ? best : null;
    }
}

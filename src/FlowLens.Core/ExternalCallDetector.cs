using Microsoft.CodeAnalysis;

// CSharpExtensions, not the language-agnostic overload: without it GetDeclaredSymbol returns
// ISymbol and the INamedTypeSymbol assignment below does not compile.
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <param name="CallerClrTypeName">The type making the call - the node's identity.</param>
/// <param name="Evidence">Call expression and file:line.</param>
public sealed record ExternalCallSite(
    string CallerClrTypeName,
    string DisplayName,
    string Evidence,
    string FilePath,
    int Line);

/// <summary>
/// Finds calls that actually leave the process.
/// <para>
/// The rule is structural: the invoked member's declaring type is <c>HttpClient</c> or
/// <c>HttpMessageInvoker</c>. Names and abstractions are deliberately ignored, and the contrast in
/// this target shows why. <c>FakePspClient</c> looks exactly like an external payment provider -
/// same <c>IPspClient</c> abstraction, same resilience pipeline around it - but its body is
/// <c>Task.Delay</c> and <c>Random.Shared.NextDouble</c>. It reaches nothing, so it gets no node,
/// and "which external service does checkout call?" correctly answers: none.
/// <c>HttpEmbeddingService</c>, which really does call out, gets one.
/// </para>
/// <para>
/// The destination URL is not part of the node. It comes from configuration at runtime, so any
/// hostname recorded here would be invented rather than observed.
/// </para>
/// </summary>
public static class ExternalCallDetector
{
    private static readonly HashSet<string> HttpClientTypes = new(StringComparer.Ordinal)
    {
        "System.Net.Http.HttpClient",
        "System.Net.Http.HttpMessageInvoker",
    };

    public static IReadOnlyList<ExternalCallSite> Find(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var sites = new List<ExternalCallSite>();

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method)
            {
                continue;
            }

            if (!IsHttpClientCall(method))
            {
                continue;
            }

            var caller = EnclosingType(invocation, semanticModel, cancellationToken);
            if (caller is null)
            {
                continue;
            }

            var (file, line) = SourceLocation.For(invocation, solutionDirectory);
            var callerName = NodeId.ForType(caller);

            sites.Add(new ExternalCallSite(
                callerName,
                $"HTTP -> {caller.Name}",
                $"{method.ContainingType?.Name}.{method.Name} at {file}:{line} · url from configuration",
                file,
                line));
        }

        return sites;
    }

    private static bool IsHttpClientCall(IMethodSymbol method)
    {
        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            if (HttpClientTypes.Contains(type.ToDisplayString()))
            {
                return true;
            }
        }

        return false;
    }

    private static INamedTypeSymbol? EnclosingType(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var declaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();

        return declaration is null
            ? null
            : semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
    }
}

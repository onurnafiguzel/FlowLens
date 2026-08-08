using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>
/// Pass 1 of endpoint discovery: find every <c>MapGet</c>/<c>MapPost</c>/... call, work out where
/// its route builder came from, and record every call that looked like one but did not verify.
/// <para>
/// This closes L1. ModularCommerce declares all 24 of its endpoints as Minimal API lambdas, which
/// <c>MethodDeclarationSyntax</c> does not see at all - so none of Phase 1's 399 methods was an
/// entry point.
/// </para>
/// </summary>
public static class EndpointDiscovery
{
    private const int MaxOriginDepth = 32;

    private static readonly Dictionary<string, string> MapVerbs = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    private const string MapGroupName = "MapGroup";
    private const string AspNetCoreNamespacePrefix = "Microsoft.AspNetCore.";
    private const string RouteBuilderInterface = "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder";

    public static async Task<EndpointDiscoveryResult> DiscoverAsync(
        Solution solution,
        IReadOnlyList<Project> projects,
        string solutionDirectory,
        ImplementationResolver implementationResolver,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var mapCalls = new List<MapCallSite>();
        var propagations = new List<PropagationSite>();
        var eliminated = new List<EliminatedCandidate>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = ProjectClassifier.Classify(project.FilePath ?? project.Name, project.Name);

            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken);
                if (root is null)
                {
                    continue;
                }

                // Cheap pre-filter: most documents mention no Map* verb at all, and acquiring a
                // SemanticModel is the expensive part.
                if (!MentionsAnyMapVerb(root))
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                if (semanticModel is null)
                {
                    continue;
                }

                ScanDocument(
                    root, semanticModel, document, info.Module, solutionDirectory,
                    mapCalls, propagations, eliminated, cancellationToken);
            }
        }

        var resolution = await RoutePrefixResolver.ResolveAsync(
            mapCalls, propagations, implementationResolver, cancellationToken);

        stopwatch.Stop();

        return new EndpointDiscoveryResult(
            resolution.Endpoints,
            eliminated,
            resolution.Warnings,
            stopwatch.Elapsed,
            mapCalls.Count,
            propagations.Count,
            resolution.ReachedMethodCount);
    }

    private static bool MentionsAnyMapVerb(SyntaxNode root)
    {
        var text = root.SyntaxTree.GetText().ToString();
        return MapVerbs.Keys.Any(v => text.Contains(v, StringComparison.Ordinal))
            || text.Contains(MapGroupName, StringComparison.Ordinal);
    }

    private static void ScanDocument(
        SyntaxNode root,
        SemanticModel semanticModel,
        Document document,
        string module,
        string solutionDirectory,
        List<MapCallSite> mapCalls,
        List<PropagationSite> propagations,
        List<EliminatedCandidate> eliminated,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = InvokedName(invocation);
            if (name is null)
            {
                continue;
            }

            var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            var (filePath, line) = SourceLocation.For(invocation, solutionDirectory);

            if (MapVerbs.TryGetValue(name, out var httpMethod))
            {
                // A1 step 2: verify by SYMBOL, not by name. A1b: never drop silently.
                if (symbol is null)
                {
                    eliminated.Add(new EliminatedCandidate(
                        Truncate(invocation.Expression.ToString()), filePath, line, "symbol-unresolved"));
                    continue;
                }

                if (!IsFrameworkEndpointExtension(symbol))
                {
                    eliminated.Add(new EliminatedCandidate(
                        Truncate(invocation.Expression.ToString()), filePath, line,
                        $"resolved-to-other-type:{symbol.ContainingType?.ToDisplayString(NodeId.TypeFormat)}"));
                    continue;
                }

                var enclosing = EnclosingMethod(semanticModel, invocation, cancellationToken);
                if (enclosing is null)
                {
                    eliminated.Add(new EliminatedCandidate(
                        Truncate(invocation.Expression.ToString()), filePath, line, "no-enclosing-method"));
                    continue;
                }

                mapCalls.Add(new MapCallSite(
                    EnclosingMethod: NodeId.Canonical(enclosing),
                    Origin: ResolveOrigin(Receiver(invocation), semanticModel, 0, cancellationToken),
                    HttpMethod: httpMethod,
                    RouteSuffix: ConstantStringArgument(invocation, 0, semanticModel, cancellationToken),
                    HandlerBody: invocation.ArgumentList.Arguments.Count > 1
                        ? invocation.ArgumentList.Arguments[1].Expression
                        : null,
                    DocumentId: document.Id,
                    FilePath: filePath,
                    Line: line,
                    Module: module));

                continue;
            }

            // Not a map verb - is it a route builder being handed to another in-source method?
            if (symbol is null || name == MapGroupName || !SourceLocation.IsInSource(symbol))
            {
                continue;
            }

            var builderExpression = FindRouteBuilderArgument(invocation, semanticModel, cancellationToken);
            if (builderExpression is null)
            {
                continue;
            }

            var enclosingMethod = EnclosingMethod(semanticModel, invocation, cancellationToken);
            if (enclosingMethod is null)
            {
                continue;
            }

            propagations.Add(new PropagationSite(
                EnclosingMethod: NodeId.Canonical(enclosingMethod),
                Target: NodeId.Canonical(symbol),
                Origin: ResolveOrigin(builderExpression, semanticModel, 0, cancellationToken),
                FilePath: filePath,
                Line: line));
        }
    }

    /// <summary>
    /// Walks a route-builder expression back to its origin, accumulating MapGroup prefixes.
    /// <para>
    /// Handles the three shapes ModularCommerce actually uses: a bare parameter, a local assigned
    /// from <c>MapGroup</c>, and pass-through builders such as
    /// <c>MapGroup("").RequireAuthorization()</c> where the fluent call returns the same builder.
    /// </para>
    /// </summary>
    private static BuilderOrigin ResolveOrigin(
        ExpressionSyntax? expression,
        SemanticModel semanticModel,
        int depth,
        CancellationToken cancellationToken)
    {
        if (expression is null || depth > MaxOriginDepth)
        {
            return BuilderOrigin.Unknown;
        }

        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized:
                return ResolveOrigin(parenthesized.Expression, semanticModel, depth + 1, cancellationToken);

            // ((RouteGroupBuilder)group).MapGroup("") - the cast carries no routing meaning.
            case CastExpressionSyntax cast:
                return ResolveOrigin(cast.Expression, semanticModel, depth + 1, cancellationToken);

            case InvocationExpressionSyntax invocation:
            {
                var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
                if (symbol is null)
                {
                    return BuilderOrigin.Unknown;
                }

                var receiver = Receiver(invocation);

                if (symbol.Name == MapGroupName && IsFrameworkEndpointExtension(symbol))
                {
                    var inner = ResolveOrigin(receiver, semanticModel, depth + 1, cancellationToken);
                    if (inner.Kind == BuilderOriginKind.Unknown)
                    {
                        return BuilderOrigin.Unknown;
                    }

                    var segment = ConstantStringArgument(invocation, 0, semanticModel, cancellationToken);
                    return segment is null
                        ? BuilderOrigin.Unknown
                        : inner with { RelativePrefix = RouteText.Combine(inner.RelativePrefix, segment) };
                }

                // Fluent pass-through (RequireAuthorization, WithName, ...): the value is still
                // the same route builder, so keep walking the receiver.
                return IsRouteBuilderType(symbol.ReturnType)
                    ? ResolveOrigin(receiver, semanticModel, depth + 1, cancellationToken)
                    : BuilderOrigin.Unknown;
            }
        }

        var referenced = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;

        return referenced switch
        {
            // The enclosing method's own builder parameter - prefix comes from the caller.
            IParameterSymbol => new BuilderOrigin(BuilderOriginKind.Parameter, string.Empty),

            ILocalSymbol local => ResolveLocal(local, semanticModel, depth, cancellationToken),

            // A field/property such as a cached WebApplication: no enclosing group, so it roots
            // an absolute route.
            IFieldSymbol or IPropertySymbol => new BuilderOrigin(BuilderOriginKind.Root, string.Empty),

            _ => BuilderOrigin.Unknown,
        };
    }

    private static BuilderOrigin ResolveLocal(
        ILocalSymbol local,
        SemanticModel semanticModel,
        int depth,
        CancellationToken cancellationToken)
    {
        var declarator = local.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(cancellationToken))
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault();

        var initializer = declarator?.Initializer?.Value;

        // A local with no traceable initializer - `var app = builder.Build()` - sits under no
        // group, so whatever is mapped on it is an absolute route.
        if (initializer is null)
        {
            return new BuilderOrigin(BuilderOriginKind.Root, string.Empty);
        }

        var origin = ResolveOrigin(initializer, semanticModel, depth + 1, cancellationToken);

        return origin.Kind == BuilderOriginKind.Unknown
            ? new BuilderOrigin(BuilderOriginKind.Root, string.Empty)
            : origin;
    }

    /// <summary>First argument that is a route builder: the extension receiver, else any argument.</summary>
    private static ExpressionSyntax? FindRouteBuilderArgument(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var receiver = Receiver(invocation);
        if (receiver is not null && IsRouteBuilderExpression(receiver, semanticModel, cancellationToken))
        {
            return receiver;
        }

        return invocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault(e => IsRouteBuilderExpression(e, semanticModel, cancellationToken));
    }

    private static bool IsRouteBuilderExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken) =>
        IsRouteBuilderType(semanticModel.GetTypeInfo(expression, cancellationToken).Type);

    private static bool IsRouteBuilderType(ITypeSymbol? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type.ToDisplayString(NodeId.TypeFormat) == RouteBuilderInterface)
        {
            return true;
        }

        return type.AllInterfaces.Any(i =>
            i.ToDisplayString(NodeId.TypeFormat) == RouteBuilderInterface);
    }

    /// <summary>
    /// True for the framework's own endpoint extensions. A user-defined MapPost wrapper lives in
    /// source and fails this check - which is precisely the case A1b logs.
    /// </summary>
    private static bool IsFrameworkEndpointExtension(IMethodSymbol symbol)
    {
        if (SourceLocation.IsInSource(symbol))
        {
            return false;
        }

        var ns = symbol.ContainingType?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return ns.StartsWith(AspNetCoreNamespacePrefix, StringComparison.Ordinal);
    }

    private static string? ConstantStringArgument(
        InvocationExpressionSyntax invocation,
        int index,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count <= index)
        {
            return null;
        }

        // GetConstantValue rather than a literal check, so `const string Route = "..."` resolves
        // too. Anything non-constant stays null and is reported as unresolved - never guessed.
        var constant = semanticModel.GetConstantValue(
            invocation.ArgumentList.Arguments[index].Expression, cancellationToken);

        return constant is { HasValue: true, Value: string text } ? text : null;
    }

    private static ExpressionSyntax? Receiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? memberAccess.Expression
            : null;

    private static string? InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => null,
        };

    private static IMethodSymbol? EnclosingMethod(
        SemanticModel semanticModel,
        SyntaxNode node,
        CancellationToken cancellationToken) =>
        semanticModel.GetEnclosingSymbol(node.SpanStart, cancellationToken) as IMethodSymbol;

    private static string Truncate(string value) =>
        value.Length <= 120 ? value : value[..120] + "...";
}

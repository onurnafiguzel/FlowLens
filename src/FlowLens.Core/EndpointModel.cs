using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>Where a route-builder expression ultimately comes from.</summary>
public enum BuilderOriginKind
{
    /// <summary>
    /// Not derived from any enclosing group - e.g. <c>app</c> in Program.cs. Its prefix is
    /// absolute and known immediately.
    /// </summary>
    Root,

    /// <summary>
    /// Derived from the enclosing method's own route-builder parameter. The absolute prefix
    /// depends on who calls that method, so it is filled in by pass 2.
    /// </summary>
    Parameter,

    /// <summary>Could not be traced. Never guessed at - reported instead.</summary>
    Unknown,
}

/// <param name="RelativePrefix">Prefix accumulated between the origin and this expression.</param>
public sealed record BuilderOrigin(BuilderOriginKind Kind, string RelativePrefix)
{
    public static readonly BuilderOrigin Unknown = new(BuilderOriginKind.Unknown, string.Empty);
}

/// <summary>One <c>MapGet</c>/<c>MapPost</c>/... call found in pass 1.</summary>
/// <param name="RouteSuffix">Null when the route argument was not a compile-time constant.</param>
/// <param name="HandlerBody">The delegate argument - usually a lambda. The walker treats it as a body.</param>
public sealed record MapCallSite(
    IMethodSymbol EnclosingMethod,
    BuilderOrigin Origin,
    string HttpMethod,
    string? RouteSuffix,
    SyntaxNode? HandlerBody,
    DocumentId DocumentId,
    string FilePath,
    int Line,
    string Module);

/// <summary>
/// A route builder handed to another in-source method, which therefore inherits the prefix.
/// This is what links <c>group.MapOrderEndpoints()</c> to the endpoints declared inside it.
/// </summary>
public sealed record PropagationSite(
    IMethodSymbol EnclosingMethod,
    IMethodSymbol Target,
    BuilderOrigin Origin,
    string FilePath,
    int Line);

/// <summary>
/// An invocation whose NAME looked like an endpoint mapping but whose SYMBOL did not verify.
/// Recorded rather than dropped: a custom wrapper such as <c>MapSecuredPost</c> would otherwise
/// remove a real endpoint from the graph with no trace of why the count fell.
/// </summary>
public sealed record EliminatedCandidate(
    string CallText,
    string FilePath,
    int Line,
    string Reason);

/// <param name="Route">Absolute route. Meaningless unless <paramref name="RouteResolved"/> is true.</param>
/// <param name="MultiMount">True when the declaring method is mounted under more than one prefix.</param>
public sealed record EndpointRecord(
    string Id,
    string HttpMethod,
    string Route,
    bool RouteResolved,
    SyntaxNode? HandlerBody,
    DocumentId DocumentId,
    IMethodSymbol EnclosingMethod,
    string FilePath,
    int Line,
    string Module,
    bool MultiMount);

/// <param name="MapCallCount">Verified Map* calls found in pass 1.</param>
/// <param name="PropagationCount">Route builders handed to another in-source method.</param>
/// <param name="ReachedMethodCount">Methods that pass 2 managed to give a prefix to.</param>
public sealed record EndpointDiscoveryResult(
    IReadOnlyList<EndpointRecord> Endpoints,
    IReadOnlyList<EliminatedCandidate> Eliminated,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    int MapCallCount,
    int PropagationCount,
    int ReachedMethodCount)
{
    public int UnresolvedRouteCount => Endpoints.Count(e => !e.RouteResolved);

    public IReadOnlyList<EndpointRecord> MultiMounted => [.. Endpoints.Where(e => e.MultiMount)];
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace FlowLens.Core;

/// <summary>
/// What to do when a call binds to an interface member rather than a concrete one.
/// </summary>
public enum ImplementationPolicy
{
    /// <summary>
    /// Add every implementation and mark the result ambiguous when there is more than one.
    /// Sound: never misses a real path. This is the Phase 2 default - see the plan's §B for the
    /// measurements behind it.
    /// </summary>
    AllImplementations,

    /// <summary>
    /// Keep only implementations declared in the interface's own module. Measured to filter
    /// nothing in ModularCommerce (every ambiguous interface has all its implementations in its
    /// own module), kept as a switch for codebases where it does help.
    /// </summary>
    DeclaringModuleOnly,
}

/// <param name="Implementations">Concrete members. Empty when none were found.</param>
/// <param name="Ambiguous">True when more than one implementation survived the policy.</param>
public sealed record ImplementationResult(
    IReadOnlyList<IMethodSymbol> Implementations,
    bool Ambiguous)
{
    public static readonly ImplementationResult None = new([], false);
}

/// <summary>
/// Wraps <see cref="SymbolFinder.FindImplementationsAsync(ISymbol, Solution, System.Collections.Immutable.IImmutableSet{Project}?, System.Threading.CancellationToken)"/>
/// with a cache and a swappable policy.
/// <para>
/// Phase 1 measured this problem rather than guessing at it: 10 of the 49 invocations in
/// CheckoutHandler.HandleAsync bind to an interface member. Interface resolution is the main
/// road here, not an edge case.
/// </para>
/// </summary>
public sealed class ImplementationResolver(
    Solution solution,
    IReadOnlyList<Project> searchScope,
    ImplementationPolicy policy = ImplementationPolicy.AllImplementations)
{
    // Keyed by canonical name, not by symbol. Symbol identity is per-COMPILATION: the same
    // interface member observed from two calling projects is two unequal symbols, so a symbol-keyed
    // cache would miss on nearly every call and re-run a solution-wide search each time.
    private readonly Dictionary<string, ImplementationResult> _cache = new(StringComparer.Ordinal);

    private readonly System.Collections.Immutable.ImmutableHashSet<Project> _scope =
        [.. searchScope];

    public int CacheHits { get; private set; }

    public int SymbolFinderCalls { get; private set; }

    public ImplementationPolicy Policy => policy;

    /// <summary>
    /// True when the call target is an interface member and therefore needs resolving. Concrete
    /// calls skip SymbolFinder entirely - that is what keeps the cost bounded.
    /// </summary>
    public static bool NeedsResolution(IMethodSymbol symbol) =>
        symbol.ContainingType?.TypeKind == TypeKind.Interface;

    public async Task<ImplementationResult> ResolveAsync(
        IMethodSymbol interfaceMember,
        CancellationToken cancellationToken = default)
    {
        var key = NodeId.Canonical(interfaceMember);
        var cacheKey = NodeId.ForMethod(key);

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            CacheHits++;
            return cached;
        }

        SymbolFinderCalls++;

        // FindImplementationsAsync works on interface MEMBERS directly - it returns the
        // implementing members, so there is no need to find the implementing type first and
        // then match the member by signature.
        var found = await SymbolFinder.FindImplementationsAsync(
            key, solution, _scope, cancellationToken);

        var implementations = found
            .OfType<IMethodSymbol>()
            .Where(SourceLocation.IsInSource)
            .ToList();

        if (policy == ImplementationPolicy.DeclaringModuleOnly)
        {
            var declaringModule = ModuleOf(key);
            if (declaringModule is not null)
            {
                implementations = [.. implementations.Where(m => ModuleOf(m) == declaringModule)];
            }
        }

        var result = new ImplementationResult(
            implementations,
            Ambiguous: implementations.Count > 1);

        _cache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Module inferred from the assembly name: ModularCommerce.Ordering.Domain -> Ordering.
    /// Only used by <see cref="ImplementationPolicy.DeclaringModuleOnly"/>.
    /// </summary>
    private static string? ModuleOf(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Name;
        if (assembly is null)
        {
            return null;
        }

        var segments = assembly.Split('.');
        return segments.Length >= 2 ? segments[1] : null;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <summary>How far the domain-event → integration-event bridge got. See the plan's §D4.</summary>
public enum BridgeStatus
{
    /// <summary>Raised and mapped: it leaves the module. A PUBLISHES edge is emitted.</summary>
    Published,

    /// <summary>
    /// Raised but deliberately not mapped. NOT a failure - the aggregate uses it internally and
    /// the outbox interceptor skips it on purpose. Reported, but no edge.
    /// </summary>
    InternalDomainEvent,

    /// <summary>The raise argument's type could not be resolved. A genuine loss.</summary>
    RaiseArgumentUnresolved,
}

/// <param name="DomainEventType">Null when the raise argument could not be typed.</param>
/// <param name="IntegrationEventType">Null unless <see cref="BridgeStatus.Published"/>.</param>
public sealed record RaisedEvent(
    BridgeStatus Status,
    ITypeSymbol? DomainEventType,
    ITypeSymbol? IntegrationEventType,
    string RaiseSite,
    string? MappingSite);

/// <summary>
/// Answers the question Phase 2 actually needs: <em>which event does this handler publish?</em>
/// <para>
/// Knowing the set of events in the system is a different, easier question. The publish path in
/// ModularCommerce runs
/// <c>MarkPaid → Raise(domain event) → outbox row → registry mapping → MassTransit → consumer</c>,
/// and the only two links that carry event-specific information are the raise site and the
/// registry mapping. The outbox dispatcher is generic infrastructure - identical for every event -
/// so it contributes no edge.
/// </para>
/// </summary>
public sealed class DomainEventBridge
{
    private const string DomainEventInterfaceName = "IDomainEvent";
    private const string MapperInterfaceName = "IIntegrationEventMapper";

    // Keyed by fully qualified NAME, not by symbol. Symbol identity is per-COMPILATION, not per
    // solution: the registry reads Ordering.Domain.Orders.OrderPaid through
    // Ordering.Infrastructure's compilation while the raise site sees it through
    // Ordering.Domain's, and SymbolEqualityComparer treats those as different symbols. Keying by
    // symbol silently classified every published event as internal.
    private readonly Dictionary<string, (ITypeSymbol Integration, string Site)> _mapping;

    private DomainEventBridge(Dictionary<string, (ITypeSymbol, string)> mapping, IReadOnlyList<string> warnings)
    {
        _mapping = mapping;
        Warnings = warnings;
    }

    public IReadOnlyList<string> Warnings { get; }

    public int MappingCount => _mapping.Count;

    /// <summary>
    /// Reads every domain→integration pair out of the integration-event registries.
    /// <para>
    /// The registry's dictionary initializer carries the pairing itself: the key is
    /// <c>typeof(DomainEvents.OrderPaid)</c> and the value's factory constructs
    /// <c>new ContractEvents.OrderPaid(...)</c>. Reading both halves gives a true mapping rather
    /// than just a list of event types.
    /// </para>
    /// </summary>
    public static async Task<DomainEventBridge> BuildAsync(
        IReadOnlyList<Project> projects,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var mapping = new Dictionary<string, (ITypeSymbol, string)>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var registriesFound = 0;

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace, cancellationToken))
            {
                if (!type.AllInterfaces.Any(i => i.Name == MapperInterfaceName))
                {
                    continue;
                }

                registriesFound++;
                var before = mapping.Count;

                foreach (var reference in type.DeclaringSyntaxReferences)
                {
                    var syntax = reference.GetSyntax(cancellationToken);
                    var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);

                    ReadRegistry(syntax, semanticModel, solutionDirectory, mapping, cancellationToken);
                }

                if (mapping.Count == before)
                {
                    warnings.Add(
                        $"{type.ToDisplayString(NodeId.TypeFormat)} - registry-shape-unrecognized: " +
                        "no domain→integration pairs could be read");
                }
            }
        }

        if (registriesFound == 0)
        {
            warnings.Add($"no {MapperInterfaceName} implementation found; no publish bridge available");
        }

        return new DomainEventBridge(mapping, warnings);
    }

    /// <summary>
    /// Finds the domain events raised directly in one body and classifies each one.
    /// <para>
    /// Detection is by SYMBOL SHAPE, not by method name: a call taking exactly one argument whose
    /// parameter is an <c>IDomainEvent</c>. That matches <c>Entity.Raise</c> without hard-coding
    /// the target repository's type names.
    /// </para>
    /// </summary>
    public IReadOnlyList<RaisedEvent> FindRaisedEvents(
        SyntaxNode body,
        SemanticModel semanticModel,
        string solutionDirectory,
        CancellationToken cancellationToken = default)
    {
        var results = new List<RaisedEvent>();

        foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol symbol)
            {
                continue;
            }

            if (!IsRaiseMethod(symbol) || invocation.ArgumentList.Arguments.Count != 1)
            {
                continue;
            }

            var (file, line) = SourceLocation.For(invocation, solutionDirectory);
            var raiseSite = $"{file}:{line}";

            // GetTypeInfo, not a `new X(...)` pattern match: this also works when the event comes
            // from a local, a factory call or a conditional expression.
            var argument = invocation.ArgumentList.Arguments[0].Expression;
            var eventType = semanticModel.GetTypeInfo(argument, cancellationToken).Type;

            if (eventType is null or IErrorTypeSymbol)
            {
                results.Add(new RaisedEvent(
                    BridgeStatus.RaiseArgumentUnresolved, null, null, raiseSite, null));
                continue;
            }

            // The argument is still typed as the interface, so this is plumbing rather than a
            // raise - Entity.Raise's own `_domainEvents.Add(domainEvent)` matches the shape rule
            // above and would otherwise be reported as an unknown event on every single trace.
            // A concrete event type is what carries information.
            if (eventType.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            if (_mapping.TryGetValue(NodeId.ForType(eventType), out var mapped))
            {
                results.Add(new RaisedEvent(
                    BridgeStatus.Published, eventType, mapped.Integration, raiseSite, mapped.Site));
                continue;
            }

            // No registry entry. This is the normal case for events that stay inside the module -
            // the outbox interceptor skips them by design. Treating it as a failure would report
            // three false losses in Ordering alone (OrderCreated, OrderStatusChanged, ...).
            results.Add(new RaisedEvent(
                BridgeStatus.InternalDomainEvent, eventType, null, raiseSite, null));
        }

        return results;
    }

    private static bool IsRaiseMethod(IMethodSymbol symbol)
    {
        if (symbol.Parameters.Length != 1)
        {
            return false;
        }

        var parameterType = symbol.Parameters[0].Type;

        return parameterType.Name == DomainEventInterfaceName
            || parameterType.AllInterfaces.Any(i => i.Name == DomainEventInterfaceName);
    }

    private static void ReadRegistry(
        SyntaxNode registrySyntax,
        SemanticModel semanticModel,
        string solutionDirectory,
        Dictionary<string, (ITypeSymbol, string)> mapping,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in registrySyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // [typeof(DomainEvents.OrderPaid)] = (discriminator, factory)
            if (assignment.Left is not ImplicitElementAccessSyntax elementAccess)
            {
                continue;
            }

            var keyExpression = elementAccess.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (keyExpression is not TypeOfExpressionSyntax typeOf)
            {
                continue;
            }

            var domainType = semanticModel.GetTypeInfo(typeOf.Type, cancellationToken).Type;
            if (domainType is null or IErrorTypeSymbol)
            {
                continue;
            }

            // The factory constructs the integration event; that object creation IS the pairing.
            var creation = assignment.Right
                .DescendantNodesAndSelf()
                .OfType<ObjectCreationExpressionSyntax>()
                .FirstOrDefault();

            if (creation is null)
            {
                continue;
            }

            var integrationType = semanticModel.GetTypeInfo(creation, cancellationToken).Type;
            if (integrationType is null or IErrorTypeSymbol)
            {
                continue;
            }

            var (file, line) = SourceLocation.For(assignment, solutionDirectory);
            mapping[NodeId.ForType(domainType)] = (integrationType.OriginalDefinition, $"{file}:{line}");
        }
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(
        INamespaceSymbol root,
        CancellationToken cancellationToken)
    {
        foreach (var member in root.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (member)
            {
                case INamespaceSymbol nested:
                    foreach (var type in AllTypes(nested, cancellationToken))
                    {
                        yield return type;
                    }

                    break;

                case INamedTypeSymbol type:
                    yield return type;

                    foreach (var nestedType in type.GetTypeMembers())
                    {
                        yield return nestedType;
                    }

                    break;
            }
        }
    }
}

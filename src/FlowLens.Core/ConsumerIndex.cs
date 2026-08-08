using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <param name="ConsumeMethod">The overload bound to this specific event type.</param>
public sealed record ConsumerRegistration(
    INamedTypeSymbol ConsumerType,
    IMethodSymbol ConsumeMethod,
    ITypeSymbol EventType);

/// <summary>
/// Index of <c>IConsumer&lt;T&gt;</c> implementations, keyed by event type.
/// <para>
/// Two properties of this codebase shape the design. First, one class may implement
/// <c>IConsumer&lt;T&gt;</c> several times - <c>ProductChangedConsumer</c> handles both
/// <c>ProductCreated</c> and <c>ProductUpdated</c> - so a consumer contributes one edge per
/// interface, not one edge. Second, several consumers may listen to the same event; that is real
/// fan-out, not ambiguity, and every one of them belongs in the chain.
/// </para>
/// </summary>
public sealed class ConsumerIndex
{
    private const string ConsumerInterfaceName = "IConsumer";
    private const string MassTransitNamespace = "MassTransit";
    private const string ConsumeContextName = "ConsumeContext";

    // Keyed by fully qualified name for the same reason as DomainEventBridge: the publisher and
    // the consumer see the event type through different compilations, so symbol equality fails.
    private readonly Dictionary<string, List<ConsumerRegistration>> _byEvent;

    private ConsumerIndex(Dictionary<string, List<ConsumerRegistration>> byEvent) => _byEvent = byEvent;

    public int ConsumerCount => _byEvent.Values.SelectMany(v => v).Select(r => r.ConsumerType).Distinct(SymbolEqualityComparer.Default).Count();

    public int RegistrationCount => _byEvent.Values.Sum(v => v.Count);

    /// <summary>
    /// Builds the index over the supplied projects. Callers pass non-test projects only: test
    /// assemblies declare throwaway consumers (RecordingConsumer) that would otherwise appear as
    /// real subscribers.
    /// </summary>
    public static async Task<ConsumerIndex> BuildAsync(
        IReadOnlyList<Project> projects,
        CancellationToken cancellationToken = default)
    {
        var byEvent = new Dictionary<string, List<ConsumerRegistration>>(StringComparer.Ordinal);

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
                if (type.TypeKind != TypeKind.Class || type.IsAbstract)
                {
                    continue;
                }

                foreach (var consumerInterface in type.AllInterfaces.Where(IsConsumerInterface))
                {
                    var eventType = consumerInterface.TypeArguments[0];
                    var consumeMethod = FindConsumeOverload(type, eventType);

                    if (consumeMethod is null)
                    {
                        continue;
                    }

                    var key = NodeId.ForType(eventType);
                    if (!byEvent.TryGetValue(key, out var list))
                    {
                        list = [];
                        byEvent[key] = list;
                    }

                    list.Add(new ConsumerRegistration(type, consumeMethod, eventType));
                }
            }
        }

        return new ConsumerIndex(byEvent);
    }

    public IReadOnlyList<ConsumerRegistration> ConsumersOf(ITypeSymbol eventType) =>
        _byEvent.TryGetValue(NodeId.ForType(eventType), out var list) ? list : [];

    /// <summary>Every registration in the index, flattened.</summary>
    public IReadOnlyList<ConsumerRegistration> AllRegistrations =>
        [.. _byEvent.Values.SelectMany(v => v)];

    private static bool IsConsumerInterface(INamedTypeSymbol candidate) =>
        candidate.Name == ConsumerInterfaceName
        && candidate.TypeArguments.Length == 1
        && candidate.ContainingNamespace?.Name == MassTransitNamespace;

    /// <summary>
    /// Picks the Consume overload for one event type by matching the
    /// <c>ConsumeContext&lt;T&gt;</c> argument. Selecting by name alone would bind both of
    /// ProductChangedConsumer's overloads to whichever event was seen first.
    /// </summary>
    private static IMethodSymbol? FindConsumeOverload(INamedTypeSymbol type, ITypeSymbol eventType) =>
        type.GetMembers("Consume")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.Parameters.Length == 1
                && m.Parameters[0].Type is INamedTypeSymbol { Name: ConsumeContextName } context
                && context.TypeArguments.Length == 1
                && SymbolEqualityComparer.Default.Equals(
                    context.TypeArguments[0].OriginalDefinition,
                    eventType.OriginalDefinition));

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

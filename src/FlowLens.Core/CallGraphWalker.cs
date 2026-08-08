using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FlowLens.Core;

/// <param name="MaxDepth">
/// Default chosen from measurement, not guessed. The longest chain in ModularCommerce is
/// POST /api/ordering/checkout at 10 levels; 11 is the first limit that reports no truncation.
/// 20 leaves roughly 2x headroom so a moderately deeper flow still completes by default.
/// <para>
/// Note on the old default of 10: it never lost a node. A node sitting exactly at the limit is
/// flagged before we know whether expanding it would add anything, and here it would not have -
/// depth 10 and depth 50 produce the identical 106-node graph. The flag was conservative rather
/// than wrong, but "possibly incomplete" is not a good default to ship.
/// </para>
/// </param>
/// <param name="MaxNodes">
/// Runaway guard. Measured headroom is large: checkout produces 106 nodes at unlimited depth.
/// </param>
public sealed record TraversalOptions(
    int MaxDepth = 20,
    int MaxNodes = 5000,
    ImplementationPolicy ImplementationPolicy = ImplementationPolicy.AllImplementations);

/// <summary>
/// What the walk actually looked at. Node and edge counts describe the result; these describe the
/// work, which is what tells you whether a clean-looking graph was earned or just lucky.
/// </summary>
/// <param name="InvocationsExamined">Every InvocationExpressionSyntax visited.</param>
/// <param name="ResolvedBySymbol">GetSymbolInfo().Symbol was non-null - the compiler bound exactly one.</param>
/// <param name="ResolvedFromCandidates">Symbol was null; a CandidateSymbols entry was used instead.</param>
/// <param name="Unresolved">Neither, excluding the nameof operator.</param>
/// <param name="FrameworkFiltered">Bound successfully but declared outside the solution, so kept out of the graph.</param>
/// <param name="InterfaceCalls">Calls whose target was an interface member and needed SymbolFinder.</param>
/// <param name="CandidateReasons">CandidateReason -> count, for the calls that fell back.</param>
/// <param name="NodesByDepth">Discovery depth -> node count. Phase 3 needs this to predict traversal cost.</param>
public sealed record TraceStats(
    int NodeCount,
    int EdgeCount,
    int MaxDepthReached,
    bool BudgetExhausted,
    int SymbolFinderCalls,
    int ImplementationCacheHits,
    TimeSpan Elapsed,
    int InvocationsExamined = 0,
    int ResolvedBySymbol = 0,
    int ResolvedFromCandidates = 0,
    int Unresolved = 0,
    int FrameworkFiltered = 0,
    int InterfaceCalls = 0,
    int AmbiguousNodes = 0,
    int TruncatedNodes = 0,
    IReadOnlyDictionary<string, int>? CandidateReasons = null,
    IReadOnlyDictionary<int, int>? NodesByDepth = null);

/// <param name="ReachedMethods">
/// Node id -> the method symbol behind it, for every method the walk reached. Phase 3's EF overlay
/// needs to re-open these bodies to look for entity access, and re-deriving the symbol from the id
/// would mean parsing the id - which is exactly the coupling NodeId exists to prevent.
/// </param>
public sealed record TraceResult(
    IReadOnlyList<Node> Nodes,
    IReadOnlyList<Edge> Edges,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RaisedEvent> InternalDomainEvents,
    TraceStats Stats,
    IReadOnlyDictionary<string, IMethodSymbol> ReachedMethods);

/// <summary>
/// Breadth-first walk from an endpoint through the call chain, across the module boundary via
/// published events.
/// <para>
/// BFS rather than DFS on purpose. A method reached by two different paths becomes ONE node with
/// TWO incoming edges - that is what makes the result a graph instead of a tree - and its body is
/// walked only on first visit. Since the recorded depth is therefore the depth of first
/// discovery, breadth-first makes that the shortest path, which is the meaningful number.
/// </para>
/// </summary>
public sealed class CallGraphWalker(
    Solution solution,
    string solutionDirectory,
    ImplementationResolver implementationResolver,
    DomainEventBridge domainEventBridge,
    ConsumerIndex consumerIndex,
    TraversalOptions? options = null)
{
    private readonly TraversalOptions _options = options ?? new TraversalOptions();

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<(string From, string To, EdgeKind Kind)> _edgeKeys = [];
    private readonly List<Edge> _edges = [];
    private readonly List<string> _warnings = [];
    private readonly List<RaisedEvent> _internalDomainEvents = [];
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModels = [];

    // Keyed on the node id, which is derived from OriginalDefinition - so every instantiation of
    // a generic method converges on one entry and the walk terminates.
    private readonly HashSet<string> _visitedBodies = new(StringComparer.Ordinal);

    // Every method the walk touched, for Phase 3's EF overlay to re-analyse. Instance state like
    // everything else here, which is what lets one walker be driven from many roots: the visited
    // set, node table and this dictionary are shared across successive WalkAsync calls, so a
    // method reachable from two endpoints is analysed once and carries edges from both.
    private readonly Dictionary<string, IMethodSymbol> _reachedMethods = new(StringComparer.Ordinal);

    private int _maxDepthReached;
    private bool _budgetExhausted;

    // Work counters. These answer "was the clean result earned?" - a graph with zero unresolved
    // calls means nothing unless you know how many calls were examined.
    private int _invocationsExamined;
    private int _resolvedBySymbol;
    private int _resolvedFromCandidates;
    private int _unresolved;
    private int _frameworkFiltered;
    private int _interfaceCalls;
    private readonly Dictionary<string, int> _candidateReasons = new(StringComparer.Ordinal);

    public Task<TraceResult> WalkAsync(
        EndpointRecord endpoint,
        CancellationToken cancellationToken = default)
    {
        var endpointNode = new Node(
            Id: endpoint.Id,
            Kind: NodeKind.Endpoint,
            DisplayName: $"{endpoint.HttpMethod} {endpoint.Route}",
            Module: endpoint.Module,
            FilePath: endpoint.FilePath,
            Line: endpoint.Line);

        if (endpoint.HandlerBody is null)
        {
            _warnings.Add($"{endpoint.FilePath}:{endpoint.Line} - endpoint has no handler body to walk");
        }

        return WalkAsync(endpointNode, endpoint.HandlerBody, cancellationToken);
    }

    /// <summary>
    /// Walks from an arbitrary root node and body. The endpoint overload is the normal entry
    /// point; this one exists so the traversal can be exercised without constructing a route.
    /// </summary>
    public async Task<TraceResult> WalkAsync(
        Node root,
        SyntaxNode? body,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _nodes[root.Id] = root;

        var queue = new Queue<WorkItem>();

        if (body is not null)
        {
            queue.Enqueue(new WorkItem(root.Id, body, 0));
        }

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = queue.Dequeue();
            _maxDepthReached = Math.Max(_maxDepthReached, item.Depth);

            if (item.Depth >= _options.MaxDepth)
            {
                MarkTruncated(item.NodeId);
                continue;
            }

            if (_nodes.Count >= _options.MaxNodes)
            {
                _budgetExhausted = true;
                break;
            }

            await ExpandAsync(item, queue, cancellationToken);
        }

        if (_budgetExhausted)
        {
            _warnings.Add(
                $"node budget of {_options.MaxNodes} exhausted; the chain below this point is incomplete");
        }

        stopwatch.Stop();

        return new TraceResult(
            [.. _nodes.Values],
            _edges,
            _warnings,
            _internalDomainEvents,
            new TraceStats(
                _nodes.Count,
                _edges.Count,
                _maxDepthReached,
                _budgetExhausted,
                implementationResolver.SymbolFinderCalls,
                implementationResolver.CacheHits,
                stopwatch.Elapsed,
                InvocationsExamined: _invocationsExamined,
                ResolvedBySymbol: _resolvedBySymbol,
                ResolvedFromCandidates: _resolvedFromCandidates,
                Unresolved: _unresolved,
                FrameworkFiltered: _frameworkFiltered,
                InterfaceCalls: _interfaceCalls,
                AmbiguousNodes: _nodes.Values.Count(n => n.Ambiguous),
                TruncatedNodes: _nodes.Values.Count(n => n.Truncated),
                CandidateReasons: _candidateReasons,
                NodesByDepth: _nodes.Values
                    .GroupBy(n => n.Depth)
                    .ToDictionary(g => g.Key, g => g.Count())),
            _reachedMethods);
    }

    private async Task ExpandAsync(WorkItem item, Queue<WorkItem> queue, CancellationToken cancellationToken)
    {
        var semanticModel = await GetSemanticModelAsync(item.Body.SyntaxTree, cancellationToken);
        if (semanticModel is null)
        {
            _warnings.Add($"no semantic model for {item.Body.SyntaxTree.FilePath}");
            return;
        }

        await ExpandInvocationsAsync(item, semanticModel, queue, cancellationToken);
        ExpandPublishedEvents(item, semanticModel, queue, cancellationToken);
    }

    private async Task ExpandInvocationsAsync(
        WorkItem item,
        SemanticModel semanticModel,
        Queue<WorkItem> queue,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in item.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            // nameof looks like an invocation but is a compile-time operator with no symbol.
            // Counted nowhere: it is not a call in any sense.
            if (invocation.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" })
            {
                continue;
            }

            _invocationsExamined++;

            var info = semanticModel.GetSymbolInfo(invocation, cancellationToken);

            var bound = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

            // Canonical form immediately: a call site sees the reduced extension method and the
            // declaration sees the unreduced one, and identity has to agree across both.
            var target = bound is null ? null : NodeId.Canonical(bound);

            if (target is null)
            {
                _unresolved++;
                var reason = info.CandidateReason.ToString();
                _candidateReasons[reason] = _candidateReasons.GetValueOrDefault(reason) + 1;

                // In a solution that compiles cleanly this should not happen; when it does it
                // signals a degraded compilation rather than an exotic call.
                var (file, line) = SourceLocation.For(invocation, solutionDirectory);
                _warnings.Add(
                    $"{file}:{line} - unresolved invocation '{Shorten(invocation.Expression.ToString())}' " +
                    $"({info.CandidateReason})");
                continue;
            }

            if (info.Symbol is not null)
            {
                _resolvedBySymbol++;
            }
            else
            {
                _resolvedFromCandidates++;
                var reason = info.CandidateReason.ToString();
                _candidateReasons[reason] = _candidateReasons.GetValueOrDefault(reason) + 1;
            }

            // Framework and NuGet methods are out of scope: the graph describes project code.
            if (!SourceLocation.IsInSource(target))
            {
                _frameworkFiltered++;
                continue;
            }

            if (ImplementationResolver.NeedsResolution(target))
            {
                _interfaceCalls++;
            }

            var ambiguousCall = info.Symbol is null;
            var targetId = AddMethodNode(target, item.Depth + 1, ambiguous: ambiguousCall);
            AddEdge(item.NodeId, targetId, EdgeKind.Calls, ambiguous: ambiguousCall);

            if (ImplementationResolver.NeedsResolution(target))
            {
                await ExpandInterfaceAsync(target, targetId, item.Depth, queue, cancellationToken);
                continue;
            }

            EnqueueBody(target, targetId, item.Depth + 1, queue);
        }
    }

    /// <summary>
    /// A call bound to an interface member names the contract, not the implementation DI will
    /// supply. Every implementation is added and the edge is marked ambiguous when there is more
    /// than one - missing a real path is worse than carrying an extra one.
    /// </summary>
    private async Task ExpandInterfaceAsync(
        IMethodSymbol interfaceMember,
        string interfaceNodeId,
        int depth,
        Queue<WorkItem> queue,
        CancellationToken cancellationToken)
    {
        var result = await implementationResolver.ResolveAsync(interfaceMember, cancellationToken);

        if (result.Implementations.Count == 0)
        {
            _warnings.Add(
                $"no implementation found for {interfaceMember.ToDisplayString(NodeId.MemberFormat)}");
            return;
        }

        foreach (var implementation in result.Implementations)
        {
            var implementationId = AddMethodNode(implementation, depth + 2, result.Ambiguous);
            AddEdge(interfaceNodeId, implementationId, EdgeKind.Calls, result.Ambiguous);
            EnqueueBody(implementation, implementationId, depth + 2, queue);
        }
    }

    /// <summary>
    /// Emits the cross-module bridge: raised domain event → integration event → consumers.
    /// </summary>
    private void ExpandPublishedEvents(
        WorkItem item,
        SemanticModel semanticModel,
        Queue<WorkItem> queue,
        CancellationToken cancellationToken)
    {
        var raised = domainEventBridge.FindRaisedEvents(
            item.Body, semanticModel, solutionDirectory, cancellationToken);

        foreach (var raise in raised)
        {
            switch (raise.Status)
            {
                case BridgeStatus.InternalDomainEvent:
                    // Not a failure: the aggregate raises it, the outbox interceptor skips it
                    // because the registry has no mapping. Recorded so the report can say so.
                    _internalDomainEvents.Add(raise);
                    continue;

                case BridgeStatus.RaiseArgumentUnresolved:
                    _warnings.Add($"{raise.RaiseSite} - raise-arg-unresolved: event type could not be typed");
                    continue;
            }

            var eventType = raise.IntegrationEventType!;
            var eventId = NodeId.ForEvent(eventType);
            var (file, line) = SourceLocation.For(eventType, solutionDirectory);

            AddNode(new Node(
                Id: eventId,
                Kind: NodeKind.Event,
                DisplayName: eventType.Name,
                Module: ModuleOf(eventType),
                FilePath: file,
                Line: line,
                Depth: item.Depth + 1));

            AddEdge(
                item.NodeId, eventId, EdgeKind.Publishes,
                evidence: $"raise {raise.RaiseSite} · map {raise.MappingSite}",
                mechanism: EdgeMechanism.DomainEventRegistry);

            var consumers = consumerIndex.ConsumersOf(eventType);

            if (consumers.Count == 0)
            {
                _warnings.Add($"{eventType.Name} is published but no consumer subscribes to it");
                continue;
            }

            // Several consumers on one event is genuine fan-out, not ambiguity - a publish
            // reaches every subscriber, so all of them belong in the chain.
            foreach (var consumer in consumers)
            {
                var consumerId = AddMethodNode(consumer.ConsumeMethod, item.Depth + 2, ambiguous: false);

                var (consumerFile, consumerLine) =
                    SourceLocation.For(consumer.ConsumerType, solutionDirectory);

                AddEdge(
                    eventId, consumerId, EdgeKind.Consumes,
                    evidence: $"{consumer.ConsumerType.Name} : IConsumer<{eventType.Name}> " +
                              $"at {consumerFile}:{consumerLine}",
                    mechanism: EdgeMechanism.ConsumerRegistration);
                EnqueueBody(consumer.ConsumeMethod, consumerId, item.Depth + 2, queue);
            }
        }
    }

    private void EnqueueBody(IMethodSymbol method, string nodeId, int depth, Queue<WorkItem> queue)
    {
        // First visit wins: the body is walked once, but every edge into it is still recorded.
        if (!_visitedBodies.Add(nodeId))
        {
            return;
        }

        var declaration = method.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .FirstOrDefault();

        if (declaration is not null)
        {
            queue.Enqueue(new WorkItem(nodeId, declaration, depth));
        }
    }

    private string AddMethodNode(IMethodSymbol method, int depth, bool ambiguous)
    {
        var id = NodeId.ForMethod(method);
        var (file, line) = SourceLocation.For(method, solutionDirectory);
        var module = ModuleOf(method);

        AddNode(new Node(
            Id: id,
            Kind: NodeKindClassifier.Classify(method),
            DisplayName: NodeId.DisplayName(method),
            Module: module,
            FilePath: file,
            Line: line,
            Ambiguous: ambiguous,
            // Shared-kernel plumbing: Result.Success, Error.Validation and friends. Real calls, so
            // they stay in the graph, but they carry no impact-analysis signal. The test is which
            // project declares them, not what they are called.
            Utility: module == ProjectClassifier.SharedModule,
            Depth: depth));

        _reachedMethods.TryAdd(id, method);

        return id;
    }

    private void AddNode(Node node)
    {
        if (_nodes.TryGetValue(node.Id, out var existing))
        {
            // Keep the shallowest depth (BFS discovery order) and never clear an ambiguity flag.
            _nodes[node.Id] = existing with
            {
                Ambiguous = existing.Ambiguous || node.Ambiguous,
                Depth = Math.Min(existing.Depth, node.Depth),
            };
            return;
        }

        _nodes[node.Id] = node;
    }

    private void AddEdge(
        string from,
        string to,
        EdgeKind kind,
        bool ambiguous = false,
        string? evidence = null,
        EdgeMechanism mechanism = EdgeMechanism.None)
    {
        if (!_edgeKeys.Add((from, to, kind)))
        {
            return;
        }

        _edges.Add(new Edge(from, to, kind, evidence, ambiguous, mechanism));
    }

    private void MarkTruncated(string nodeId)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            _nodes[nodeId] = node with { Truncated = true };
        }
    }

    private async Task<SemanticModel?> GetSemanticModelAsync(SyntaxTree tree, CancellationToken cancellationToken)
    {
        if (_semanticModels.TryGetValue(tree, out var cached))
        {
            return cached;
        }

        var document = solution.GetDocument(tree);
        if (document is null)
        {
            return null;
        }

        // Document.GetSemanticModelAsync is cached by the workspace;
        // Compilation.GetSemanticModel would build a fresh one on every call.
        var model = await document.GetSemanticModelAsync(cancellationToken);
        if (model is not null)
        {
            _semanticModels[tree] = model;
        }

        return model;
    }

    private static string ModuleOf(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Name;
        if (assembly is null)
        {
            return ProjectClassifier.UnknownModule;
        }

        var segments = assembly.Split('.');
        return segments.Length >= 2 ? segments[1] : assembly;
    }

    private static string Shorten(string value) => value.Length <= 80 ? value : value[..80] + "...";

    private sealed record WorkItem(string NodeId, SyntaxNode Body, int Depth);
}

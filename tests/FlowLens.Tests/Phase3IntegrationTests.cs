using FlowLens.Core;

namespace FlowLens.Tests;

/// <summary>
/// The Phase 3 acceptance criteria, against the real target.
/// <para>
/// Anchors are route strings, table names and invariants - never counts. ModularCommerce keeps
/// growing, and a test that asserts "16 tables" fails the day someone adds a module, which trains
/// people to edit the number instead of reading the diff. The measured figures live in
/// docs/phase-3-notes.md.
/// </para>
/// </summary>
[Collection(nameof(Phase3Collection))]
public sealed class Phase3IntegrationTests(Phase3Fixture fixture)
{
    private const string Checkout = "endpoint:POST /api/ordering/checkout";
    private const string OrdersTable = "table:ordering.orders";

    /// <summary>
    /// The roadmap's motivating question: which tables does this flow touch? Every anchor here was
    /// confirmed by reading the target's source, and each reaches the table by a different route -
    /// direct repository write, cross-module call, and the published-event bridge.
    /// </summary>
    [Fact]
    public void ForwardFromCheckoutReachesTheTablesItsCodeWrites()
    {
        var reached = Forward(Checkout);

        Assert.Contains("table:ordering.orders", reached);       // OrderRepository.AddAsync
        Assert.Contains("table:ordering.order_lines", reached);  // owned collection, no DbSet of its own
        Assert.Contains("table:inventory.reservations", reached); // another module, via IStockReservationService
        Assert.Contains("table:payment.payments", reached);       // another module, via IPaymentService
        Assert.Contains("table:cart.carts", reached);             // ICartService
    }

    /// <summary>
    /// Notification tables are only reachable through OrderPaid: raise -> outbox mapping ->
    /// IConsumer. This is Phase 2's cross-module bridge paying off in table terms.
    /// </summary>
    [Fact]
    public void ForwardFromCheckoutCrossesThePublishedEventBridge()
    {
        var reached = Forward(Checkout);

        Assert.Contains("table:notification.notification_logs", reached);
        Assert.Contains("event:ModularCommerce.Ordering.Contracts.IntegrationEvents.OrderPaid", reached);
    }

    /// <summary>
    /// Column level. Order changes state only inside the private TransitionTo helper, so reaching
    /// these columns from an endpoint depends on the ordinary CALLS edges - no special propagation.
    /// </summary>
    [Fact]
    public void ForwardFromCheckoutReachesTheColumnsOrderStateWrites()
    {
        var reached = Forward(Checkout);

        Assert.Contains("column:ordering.orders.Status", reached);
        Assert.Contains("column:ordering.orders.UpdatedAtUtc", reached);
    }

    /// <summary>
    /// Creating an aggregate writes most of its columns inside a private constructor, which the
    /// call walker never enters - `new Product(...)` is not an invocation. Sku and StockQuantity
    /// are written ONLY there, so they are the proof that constructor bodies are analysed.
    /// </summary>
    [Fact]
    public void CreatingAnAggregateReachesTheColumnsItsConstructorWrites()
    {
        var reached = Forward("endpoint:POST /api/catalog/products");

        Assert.Contains("column:catalog.products.Sku", reached);
        Assert.Contains("column:catalog.products.StockQuantity", reached);
        Assert.Contains("column:catalog.products.CreatedAtUtc", reached);
    }

    /// <summary>
    /// A read must not claim writes. Columns are reachable only from the methods that write them,
    /// which is why no table -> column edge exists: it would make every reader of a table appear to
    /// touch every column any writer touches.
    /// </summary>
    [Fact]
    public void ReadingATableClaimsNoColumnWrites()
    {
        var reached = Forward("endpoint:GET /api/catalog/products");

        Assert.Contains("table:catalog.products", reached);
        Assert.DoesNotContain(reached, id => id.StartsWith(NodeId.ColumnPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Shared.Kernel.Money is a complex property in Catalog but an OwnsOne entity in Ordering, so
    /// the model maps it as an owned type of OrderLine. Attributing it on identity alone gave every
    /// module that constructs a Money an edge to ordering.order_lines.
    /// </summary>
    [Fact]
    public void SharedValueObjectsAreNotAttributedToOneModulesTable()
    {
        var reached = Forward("endpoint:POST /api/catalog/products");

        Assert.DoesNotContain("table:ordering.order_lines", reached);
        Assert.DoesNotContain("entity:ModularCommerce.Shared.Kernel.Money", reached);
    }

    /// <summary>
    /// A column's table must be an edge, not a substring of its id. Leaving the relationship
    /// implicit would force every consumer to parse ids - the string handling a graph replaces.
    /// </summary>
    [Fact]
    public void EveryColumnHasAnEdgeToItsTable()
    {
        var columns = fixture.Graph.Nodes.Where(n => n.Kind == NodeKind.Column).Select(n => n.Id).ToHashSet();

        var linked = fixture.Build.Document.Edges
            .Where(e => e.Kind == EdgeKind.MapsTo
                && e.FromId.StartsWith(NodeId.ColumnPrefix, StringComparison.Ordinal)
                && e.ToId.StartsWith(NodeId.TablePrefix, StringComparison.Ordinal))
            .Select(e => e.FromId)
            .ToHashSet();

        Assert.NotEmpty(columns);
        Assert.Empty(columns.Except(linked));
    }

    /// <summary>
    /// The outbox row is written by a SaveChanges interceptor, so no handler mentions it and no
    /// call edge leads to it. Without the interceptor rule, "which tables does checkout write?"
    /// omits it - and the roadmap's own worked example expects it.
    /// </summary>
    [Fact]
    public void CheckoutReachesTheOutboxTableWrittenByTheInterceptor()
    {
        Assert.Contains("table:ordering.outbox_messages", Forward(Checkout));
    }

    /// <summary>
    /// Several dev endpoints take a DbContext straight into the lambda and never call a handler.
    /// Analysing only the methods the walk reached left them with no data edges at all, so they
    /// looked like they touched nothing.
    /// </summary>
    [Theory]
    [InlineData("endpoint:POST /api/inventory/dev/reservations/{id:guid}/expire-now", "table:inventory.reservations")]
    [InlineData("endpoint:GET /api/notification/dev/logs/{orderId:guid}", "table:notification.notification_logs")]
    [InlineData("endpoint:GET /api/payment/dev/payments", "table:payment.payments")]
    public void EndpointsThatUseTheDbContextInlineStillReachTheirTables(string endpoint, string table)
    {
        Assert.Contains(table, Forward(endpoint));
    }

    /// <summary>
    /// PUBLISHES and CONSUMES must be as checkable as the data edges: every edge that asserts a
    /// relationship the reader cannot see in one file carries evidence and a mechanism.
    /// </summary>
    [Fact]
    public void MessagingEdgesCarryEvidenceAndMechanism()
    {
        var messaging = fixture.Build.Document.Edges
            .Where(e => e.Kind is EdgeKind.Publishes or EdgeKind.Consumes)
            .ToList();

        Assert.NotEmpty(messaging);
        Assert.All(messaging, e => Assert.False(string.IsNullOrWhiteSpace(e.Evidence)));
        Assert.All(messaging, e => Assert.NotEqual(EdgeMechanism.None, e.Mechanism));
    }

    /// <summary>Backward is the triage direction: who can reach this table?</summary>
    [Fact]
    public void BackwardFromTheOrdersTableFindsOnlyOrderingEndpoints()
    {
        var endpoints = fixture.Graph
            .Backward(OrdersTable, maxDepth: 20)
            .Where(n => n.Kind == NodeKind.Endpoint)
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.Contains(endpoints, e => e.Id == Checkout);

        // Every endpoint that writes orders is an Ordering endpoint. A table reachable from an
        // unrelated module would mean the walk leaked across a boundary.
        Assert.All(endpoints, e => Assert.Equal("Ordering", e.Module));
    }

    /// <summary>
    /// The roadmap makes filePath and line mandatory. Asserted as an invariant over whatever the
    /// build produced rather than as a count, so it keeps holding as the target grows.
    /// </summary>
    [Fact]
    public void EveryNodeCarriesAFileAndLine()
    {
        var unattributed = fixture.Graph.Nodes
            .Where(n => string.IsNullOrWhiteSpace(n.FilePath)
                || n.FilePath == SourceLocation.NoSource
                || n.Line <= 0)
            .ToList();

        Assert.True(
            unattributed.Count == 0,
            "these nodes cannot be checked against the source: " +
            string.Join(", ", unattributed.Take(10).Select(n => n.Id)));
    }

    /// <summary>
    /// A table must be attributed to the configuration that names it, never to a migration.
    /// <para>
    /// Migrations restate the entire model, so they contain a ToTable for every table and will win
    /// any first-match scan. Pointing a reader at a generated snapshot sends them to a file they
    /// must not edit - and the whole promise of filePath+line is that you can go there and change
    /// the thing being described.
    /// </para>
    /// </summary>
    [Fact]
    public void TablesAreAttributedToConfigurationsNotToGeneratedMigrations()
    {
        var generated = fixture.Graph.Nodes
            .Where(n => n.Kind == NodeKind.Table)
            .Where(n => n.FilePath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase)
                || n.FilePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                || n.FilePath.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            generated.Count == 0,
            "these tables point at generated model code: " +
            string.Join(", ", generated.Select(n => $"{n.DisplayName} -> {n.Location}")));
    }

    [Fact]
    public void TableIdsAreSchemaQualified()
    {
        var tables = fixture.Graph.Nodes.Where(n => n.Kind == NodeKind.Table).ToList();

        Assert.NotEmpty(tables);

        // Not cosmetic: this target really does have both catalog.outbox_messages and
        // ordering.outbox_messages, which an unqualified id would collapse into one node.
        Assert.All(tables, t => Assert.Contains(
            ".", t.Id[NodeId.TablePrefix.Length..], StringComparison.Ordinal));
    }

    /// <summary>
    /// The ExternalCall rule is structural - an HttpClient member is invoked - not name-based.
    /// This target contains the perfect control pair: two "external provider" abstractions, one
    /// that really calls out and one that only simulates. Only the real one may produce a node.
    /// </summary>
    [Fact]
    public void ExternalCallsAreFoundByMechanismNotByName()
    {
        var external = fixture.Graph.Nodes
            .Where(n => n.Kind == NodeKind.ExternalCall)
            .Select(n => n.Id)
            .ToList();

        Assert.Contains(external, id => id.Contains("HttpEmbeddingService", StringComparison.Ordinal));

        // FakePspClient looks like a payment provider and is wrapped in a resilience pipeline, but
        // its body is Task.Delay and Random. Nothing leaves the process, so nothing is claimed.
        Assert.DoesNotContain(external, id => id.Contains("FakePspClient", StringComparison.Ordinal));
    }

    /// <summary>
    /// Roslyn knows which DbContexts exist; reflection knows which ones loaded. A gap means a
    /// module's tables are missing, which must never pass unremarked.
    /// </summary>
    [Fact]
    public void EveryDbContextRoslynFoundWasAlsoInstantiated()
    {
        Assert.Empty(fixture.Build.ModelResult.Failures);
        Assert.Empty(fixture.Build.ModelResult.UnresolvedAssemblies);

        // Every context must contribute at least one table; zero tables is a failure, not a result.
        Assert.All(
            fixture.Build.ModelResult.Snapshots,
            s => Assert.Contains(s.Entities, e => e.QualifiedTableName is not null));
    }

    /// <summary>
    /// Second-class mechanisms must stay distinguishable, or Phase 5 cannot measure "right answer,
    /// wrong reason" separately from "right answer".
    /// </summary>
    [Fact]
    public void EveryDataEdgeRecordsHowItWasDerived()
    {
        var dataEdges = fixture.Build.Document.Edges
            .Where(e => e.Kind is EdgeKind.Reads or EdgeKind.Writes or EdgeKind.MapsTo)
            .ToList();

        Assert.NotEmpty(dataEdges);
        Assert.All(dataEdges, e => Assert.NotEqual(EdgeMechanism.None, e.Mechanism));
        Assert.All(dataEdges, e => Assert.False(string.IsNullOrWhiteSpace(e.Evidence)));
    }

    /// <summary>Background work is a root in its own right; the outbox tables have no endpoint.</summary>
    [Fact]
    public void HostedServicesAndConsumersAreRootsToo()
    {
        Assert.Contains(fixture.Build.Roots, r => r.Kind == RootKind.BackgroundService);
        Assert.Contains(fixture.Build.Roots, r => r.Kind == RootKind.Consumer);
        Assert.Contains(fixture.Build.Roots, r => r.Kind == RootKind.Endpoint);
    }

    /// <summary>
    /// Knowing that 32 roots exist is not the same as knowing WHICH nodes they are. Backward's
    /// answer is a list of entry points, so every root must be identifiable from the graph alone -
    /// otherwise a consumer has to re-derive them, and a background job reads as an ordinary method.
    /// </summary>
    [Fact]
    public void EveryRootNodeCarriesItsRootKind()
    {
        var byId = fixture.Build.Document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        Assert.All(fixture.Build.Roots, root =>
        {
            Assert.True(byId.ContainsKey(root.Id), $"root {root.Id} has no node");
            Assert.Equal(root.Kind, byId[root.Id].RootKind);
        });

        // And nothing else claims to be one.
        Assert.Equal(
            fixture.Build.Roots.Count,
            fixture.Build.Document.Nodes.Count(n => n.RootKind != RootKind.None));
    }

    /// <summary>
    /// Measured regression: MigrateAndSeedHostedService is declared in Shared, so the structural
    /// utility rule tagged it as plumbing - but it is a BackgroundService root that seeds
    /// catalog.products and inventory.stock_items. A consumer thinning utility nodes lost it from
    /// four backward answers, i.e. lost an entry point from "who writes this table?". Anchored on
    /// the invariant across the whole population, not on that one node.
    /// </summary>
    [Fact]
    public void NoRootIsTaggedAsUtility() =>
        Assert.Empty(fixture.Build.Document.Nodes
            .Where(n => n.RootKind != RootKind.None && n.Utility)
            .Select(n => n.Id));

    /// <summary>
    /// THE invariant behind the utility tag: thinning it changes the ANSWER, never the REACHABILITY.
    /// <para>
    /// Asserted over the whole population - every endpoint forward and every table backward - and on
    /// the non-utility NODE SET, not on tables and columns. The narrower check is what let the bug
    /// through: dropping utility nodes mid-traversal cost no table and no column, so a table-level
    /// diff read as clean, while four backward answers had silently lost a background-job root
    /// (MigrateAndSeedHostedService, reached only through the Shared-declared IDataSeeder).
    /// </para>
    /// <para>
    /// With the filter applied to the result instead, this holds by construction. The test is here
    /// so it stays that way rather than being rediscovered by hand.
    /// </para>
    /// </summary>
    [Fact]
    public void ThinningUtilityNodesNeverChangesWhatIsReachable()
    {
        var graph = fixture.Build.Graph;

        var starts = graph.Nodes
            .Where(n => n.Kind is NodeKind.Endpoint or NodeKind.Table)
            .Select(n => n.Id)
            .ToList();

        Assert.NotEmpty(starts);

        var differences = new List<string>();

        foreach (var start in starts)
        {
            Compare("forward", start, graph.ForwardSubgraph);
            Compare("backward", start, graph.BackwardSubgraph);
        }

        Assert.Empty(differences);

        void Compare(string direction, string start, Func<string, TraversalQuery, Subgraph> walk)
        {
            var full = NonUtility(walk(start, new TraversalQuery(IncludeUtility: true)));
            var thinned = NonUtility(walk(start, new TraversalQuery(IncludeUtility: false)));

            foreach (var lost in full.Except(thinned, StringComparer.Ordinal))
            {
                differences.Add($"{direction} {start}: lost {lost}");
            }

            foreach (var gained in thinned.Except(full, StringComparer.Ordinal))
            {
                differences.Add($"{direction} {start}: gained {gained}");
            }
        }

        static HashSet<string> NonUtility(Subgraph subgraph) =>
            subgraph.Nodes.Where(n => !n.Utility).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The measured case behind RootKind: the TTL sweeper reads ordering.orders through
    /// OrderReservationReconciler, so "who writes this table" is four endpoints AND a background
    /// job. Anchored on the table and the kind, never on a count.
    /// </summary>
    [Fact]
    public void BackwardFromOrdersFindsBothEndpointsAndABackgroundJob()
    {
        var roots = fixture.Build.Graph
            .BackwardSubgraph(NodeId.ForTable("ordering.orders"), new TraversalQuery())
            .Nodes
            .Where(n => n.RootKind != RootKind.None)
            .ToList();

        Assert.Contains(roots, n => n.RootKind == RootKind.Endpoint);
        Assert.Contains(roots, n => n.RootKind == RootKind.BackgroundService);
    }

    /// <summary>
    /// An INSERT writes every mapped column, including the ones no C# statement names.
    /// <para>
    /// Three shapes, all measured missing before the row-level rule existed and all with a real
    /// source location once found: a surrogate key initialised on a base type in another assembly,
    /// a JSON container column that is a navigation rather than a property, and the shadow key and
    /// foreign key EF invents for an owned collection's table.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("identity.users", "Id", "Shared.Kernel")]
    [InlineData("cart.carts", "Items", "CartRecord.cs")]
    [InlineData("ordering.order_lines", "order_id", "OrderLine.cs")]
    [InlineData("payment.payment_attempts", "id", "PaymentAttempt.cs")]
    public void ColumnsNoAssignmentNamesAreStillReachedOnAnInsert(string table, string column, string locatedIn)
    {
        var node = fixture.Build.Graph.Find(NodeId.ForColumn(table, column));

        Assert.NotNull(node);
        Assert.Contains(locatedIn, node.FilePath, StringComparison.Ordinal);
        Assert.True(node.Line > 0);
    }

    /// <summary>
    /// The row-level rule must fire on inserts only. A read reaching a table and then claiming its
    /// columns is exactly the failure §5.3 removed, and this rule could reintroduce it from the
    /// other side.
    /// </summary>
    [Fact]
    public void AnInsertClaimsEveryColumnButAnUpdateStillClaimsOnlyTheOnesItAssigns()
    {
        // Cancel updates the order and inserts a status-history row: two shapes, one flow.
        var reached = Forward("endpoint:POST /api/ordering/orders/{id:guid}/cancel");

        Assert.Contains(NodeId.ForColumn("ordering.order_status_history", "order_id"), reached);
        Assert.DoesNotContain(NodeId.ForColumn("ordering.orders", "IdempotencyKey"), reached);
        Assert.DoesNotContain(NodeId.ForColumn("ordering.orders", "CreatedAtUtc"), reached);
    }

    /// <summary>
    /// Order-independence on the REAL graph, not a three-node fixture: writing the built document
    /// and writing a shuffled copy of it must produce identical bytes. This is the property a
    /// second build would exercise, without spending another 32 seconds to get one arbitrary
    /// permutation out of many.
    /// </summary>
    [Fact]
    public void TheBuiltGraphSerialisesIdenticallyWhateverOrderItIsIn()
    {
        var document = fixture.Build.Document;

        // A fixed rotation rather than a random shuffle: a test that fails one run in ten is worse
        // than no test, and rotation already breaks any dependence on the discovery order.
        var shuffled = document with
        {
            Nodes = [.. document.Nodes.Skip(7), .. document.Nodes.Take(7)],
            Edges = [.. document.Edges.Reverse()],
        };

        Assert.Equal(Write(document), Write(shuffled));

        static string Write(GraphDocument value)
        {
            var path = Path.Combine(Path.GetTempPath(), $"flowlens-order-{Guid.NewGuid():N}.json");

            try
            {
                GraphJson.Write(path, value);
                return File.ReadAllText(path);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The written graph must satisfy the same invariants the writer enforces.</summary>
    [Fact]
    public void TheBuiltGraphPassesValidation() =>
        GraphJson.Validate(fixture.Build.Document.Nodes, fixture.Build.Document.Edges);

    private IReadOnlyList<string> Forward(string id) =>
        [.. fixture.Graph.Forward(id, maxDepth: 20).Select(n => n.Id)];
}

[CollectionDefinition(nameof(Phase3Collection))]
public sealed class Phase3Collection : ICollectionFixture<Phase3Fixture>;

using System.Text.Json;
using System.Text.Json.Serialization;
using FlowLens.Api;
using FlowLens.Core;
using FlowLens.Core.Answers;

var builder = WebApplication.CreateBuilder(args);

var resolution = GraphPathResolver.Resolve(builder.Configuration["FlowLens:GraphPath"]);
builder.Services.AddSingleton(new GraphSource(resolution));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

var source = app.Services.GetRequiredService<GraphSource>();

// Loaded at startup, but a failure does NOT stop the process: a crashed API hands the operator a
// stack trace and no way to ask what went wrong. The data endpoints answer 503 with the exact
// reason and the exact fix, and /graph/stats stays available to say why.
// Where the search looked, always - not only on failure. The confusion this replaces came from
// CurrentDirectory and ContentRootPath disagreeing while neither was visible anywhere.
app.Logger.LogInformation(
    "graph search: currentDirectory={Current} contentRoot={ContentRoot} baseDirectory={Base}",
    Environment.CurrentDirectory, app.Environment.ContentRootPath, AppContext.BaseDirectory);

app.Logger.LogInformation(
    "graph search: tried {Count} path(s): {Attempted}",
    source.AttemptedPaths.Count, string.Join(" | ", source.AttemptedPaths));

if (source.Refresh() is { } loaded)
{
    app.Logger.LogInformation(
        "graph loaded from {Path}: {Nodes} nodes, {Edges} edges, ~{Bytes} KB heap",
        source.Path, loaded.Graph.Nodes.Count, loaded.Graph.Edges.Count, loaded.ApproximateHeapBytes / 1024);
}
else
{
    app.Logger.LogError("graph could not be loaded: {Error}", source.LoadError);
}

app.MapGet("/endpoints", (GraphSource graphs, string? module) =>
    Answer(graphs, snapshot => Results.Ok(Queries.Endpoints(snapshot, module))));

app.MapGet("/tables", (GraphSource graphs) =>
    Answer(graphs, snapshot => Results.Ok(Queries.Tables(snapshot))));

app.MapGet("/trace", (GraphSource graphs, string? node, int? maxDepth, bool? includeUtility) =>
    Answer(graphs, snapshot =>
        Queries.Trace(snapshot, node, maxDepth, includeUtility, TraversalDirection.Forward)));

app.MapGet("/backward", (GraphSource graphs, string? node, int? maxDepth, bool? includeUtility) =>
    Answer(graphs, snapshot =>
        Queries.Trace(snapshot, node, maxDepth, includeUtility, TraversalDirection.Backward)));

// Always 200, even with no graph. "Why is everything failing?" has to be answerable when
// everything else is 503 - a diagnostic endpoint that goes down with the thing it diagnoses is
// no use at all.
app.MapGet("/graph/stats", (GraphSource graphs) =>
    Results.Ok(Queries.Stats(graphs.Refresh(), graphs)));

app.Run();

/// <summary>
/// Runs a query against the current graph, or explains why it cannot.
/// <para>
/// The 503 body follows EfPreflight's message shape - what, which value, and the exact command that
/// fixes it - because "graph.json not found" without a path and a next step is the kind of error
/// message this project has already been burned by once (MSBL001, known-limitations L14).
/// </para>
/// </summary>
static IResult Answer(GraphSource graphs, Func<GraphSnapshot, IResult> query)
{
    var snapshot = graphs.Refresh();

    if (snapshot is null)
    {
        return Results.Problem(
            type: "https://flowlens/errors/graph-unavailable",
            title: "graph.json okunamadi",
            detail: $"{graphs.LoadError} API solution yuklemez - bir istek asla build calistirmaz.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                // ALL of them, not just the last one. Naming a single path that happened to be
                // wrong tells the reader where we looked but not why there - and "why there" is
                // the whole question when the working directory is not what they expect.
                ["attemptedPaths"] = graphs.AttemptedPaths,
                ["pathDiagnostics"] = graphs.PathDiagnostics,
                ["fix"] = "flowlens build <solution-path> -o graph.json",
                ["orSetGraphPath"] = "--FlowLens:GraphPath=<path to graph.json>",
            });
    }

    return query(snapshot);
}

/// <summary>
/// Entry-point marker for WebApplicationFactory.
/// <para>
/// A named type rather than the usual <c>public partial class Program</c>: the CLI's top-level
/// statements already generate a global <c>Program</c>, and the test project references both
/// assemblies. WebApplicationFactory only needs a public type from the right assembly.
/// </para>
/// </summary>
namespace FlowLens.Api
{
    public sealed class ApiMarker;
}

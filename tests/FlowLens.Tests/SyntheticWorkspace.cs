using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace FlowLens.Tests;

/// <summary>
/// An in-memory single-project solution built from source snippets.
/// <para>
/// Uses <see cref="AdhocWorkspace"/>, so no MSBuild and no project files are involved and these
/// tests run in milliseconds. References come from the test host's own trusted-platform-assemblies
/// list, which includes ASP.NET Core because the test project takes a FrameworkReference - that is
/// what lets a snippet mention <c>IEndpointRouteBuilder</c> and bind against the real framework
/// symbols rather than a stand-in.
/// </para>
/// </summary>
public sealed class SyntheticWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace = new();

    public Solution Solution => _workspace.CurrentSolution;

    public Project Project => Solution.Projects.Single();

    public static SyntheticWorkspace Create(string source, string assemblyName = "Synthetic")
        => Create([("Source.cs", source)], assemblyName);

    public static SyntheticWorkspace Create(
        IReadOnlyList<(string Name, string Source)> documents,
        string assemblyName = "Synthetic")
    {
        var synthetic = new SyntheticWorkspace();

        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            name: assemblyName,
            assemblyName: assemblyName,
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
            metadataReferences: FrameworkReferences());

        var project = synthetic._workspace.AddProject(projectInfo);

        foreach (var (name, source) in documents)
        {
            synthetic._workspace.AddDocument(DocumentInfo.Create(
                DocumentId.CreateNewId(project.Id),
                name,
                filePath: Path.Combine(AppContext.BaseDirectory, name),
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default))));
        }

        return synthetic;
    }

    /// <summary>Fails the test loudly if the snippet does not compile - a silently broken snippet would test nothing.</summary>
    public async Task AssertCompilesAsync()
    {
        var compilation = await Project.GetCompilationAsync();
        Assert.NotNull(compilation);

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(
            errors.Count == 0,
            "Synthetic snippet failed to compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    public async Task<(Document Document, SyntaxNode Root, SemanticModel Model)> OpenAsync(string documentName)
    {
        var document = Project.Documents.Single(d => d.Name == documentName);
        var root = await document.GetSyntaxRootAsync();
        var model = await document.GetSemanticModelAsync();

        Assert.NotNull(root);
        Assert.NotNull(model);

        return (document, root, model);
    }

    public ImplementationResolverFactory Resolver => new(Solution, [Project]);

    private static IReadOnlyList<MetadataReference> FrameworkReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;

        return
        [
            .. trusted
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        ];
    }

    public void Dispose() => _workspace.Dispose();
}

/// <summary>Small helper so tests do not repeat the resolver's constructor arguments.</summary>
public sealed record ImplementationResolverFactory(Solution Solution, IReadOnlyList<Project> Projects)
{
    public FlowLens.Core.ImplementationResolver Build(
        FlowLens.Core.ImplementationPolicy policy = FlowLens.Core.ImplementationPolicy.AllImplementations)
        => new(Solution, Projects, policy);
}

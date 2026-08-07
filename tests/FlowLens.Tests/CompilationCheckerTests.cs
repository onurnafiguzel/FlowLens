using FlowLens.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace FlowLens.Tests;

/// <summary>
/// Verifies that the compilation check really binds code rather than returning an empty
/// diagnostic list.
/// <para>
/// This matters because against a healthy target repo the check reports "0 errors, 0 warnings"
/// very quickly, and that output is indistinguishable from a checker that silently does
/// nothing. These tests force the distinction by feeding in code that must produce an error.
/// </para>
/// <para>
/// Uses <see cref="AdhocWorkspace"/>, not MSBuildWorkspace: no project files, no MSBuild, so
/// they run in milliseconds.
/// </para>
/// </summary>
public sealed class CompilationCheckerTests
{
    [Fact]
    public async Task Reports_errors_for_code_that_does_not_bind()
    {
        // Calls a method that was never declared: syntactically valid, semantically broken.
        // A parser-only implementation would see nothing wrong here.
        using var workspace = CreateWorkspace("class C { void M() { NoSuchMethod(); } }");

        var result = await CompilationChecker.CheckAsync(workspace.CurrentSolution);

        Assert.True(result.TotalErrors > 0, "Expected the checker to bind and find CS0103.");
        Assert.Contains(result.Failing, p => p.SampleErrors.Any(e => e.Contains("CS0103")));
    }

    [Fact]
    public async Task Reports_clean_for_code_that_binds()
    {
        using var workspace = CreateWorkspace("class C { void M() { System.GC.KeepAlive(this); } }");

        var result = await CompilationChecker.CheckAsync(workspace.CurrentSolution);

        Assert.Equal(0, result.TotalErrors);
        Assert.Empty(result.Failing);
    }

    [Fact]
    public async Task Counts_warnings_separately_from_errors()
    {
        // CS0219: assigned but never used - a warning, not an error. Proves the two counters
        // are wired to different severities.
        using var workspace = CreateWorkspace("class C { void M() { int unused = 1; } }");

        var result = await CompilationChecker.CheckAsync(workspace.CurrentSolution);

        Assert.Equal(0, result.TotalErrors);
        Assert.True(result.TotalWarnings > 0, "Expected at least one warning (CS0219).");
    }

    private static AdhocWorkspace CreateWorkspace(string source)
    {
        var workspace = new AdhocWorkspace();

        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Default,
            name: "Sample",
            assemblyName: "Sample",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

        var project = workspace.AddProject(projectInfo);

        workspace.AddDocument(DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            name: "Sample.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(source), VersionStamp.Default))));

        return workspace;
    }
}

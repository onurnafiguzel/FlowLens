using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <summary>
/// Resolves solution-relative file:line for syntax nodes and symbols. Every node carries this -
/// the roadmap makes filePath + line mandatory because attribution is built on it.
/// </summary>
public static class SourceLocation
{
    public const string NoSource = "(no source)";

    public static (string FilePath, int Line) For(SyntaxNode node, string solutionDirectory) =>
        FromLocation(node.GetLocation(), solutionDirectory);

    public static (string FilePath, int Line) For(SyntaxToken token, string solutionDirectory) =>
        FromLocation(token.GetLocation(), solutionDirectory);

    /// <summary>
    /// First source declaration of a symbol. Symbols that come from metadata (framework or NuGet
    /// assemblies) have none - they report <see cref="NoSource"/> and line 0 rather than a
    /// fabricated location.
    /// </summary>
    public static (string FilePath, int Line) For(ISymbol symbol, string solutionDirectory)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return location is null ? (NoSource, 0) : FromLocation(location, solutionDirectory);
    }

    public static bool IsInSource(ISymbol symbol) => symbol.Locations.Any(l => l.IsInSource);

    private static (string FilePath, int Line) FromLocation(Location location, string solutionDirectory)
    {
        if (!location.IsInSource)
        {
            return (NoSource, 0);
        }

        var span = location.GetLineSpan();
        var path = span.Path;

        if (string.IsNullOrEmpty(path))
        {
            return (NoSource, 0);
        }

        var relative = Path.GetRelativePath(solutionDirectory, path).Replace('\\', '/');
        return (relative, span.StartLinePosition.Line + 1);
    }
}

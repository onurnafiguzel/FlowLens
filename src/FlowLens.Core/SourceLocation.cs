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

    /// <summary>
    /// With the column, which is what separates two calls written on one line. Measured need:
    /// CreateProductHandler.cs:21 invokes Product.Create and Money.Create on the same line, so a
    /// file+line key leaves their order to the tie-break that follows rather than to the source.
    /// </summary>
    public static (string FilePath, int Line, int Column) WithColumn(SyntaxNode node, string solutionDirectory)
    {
        var location = node.GetLocation();
        var (path, line) = FromLocation(location, solutionDirectory);

        return line == 0
            ? (path, 0, 0)
            : (path, line, location.GetLineSpan().StartLinePosition.Character + 1);
    }

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

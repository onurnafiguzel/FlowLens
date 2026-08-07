namespace FlowLens.Core;

/// <summary>
/// Route string arithmetic. Small, but it removes a whole class of bug: joining by segments
/// rather than by concatenation makes <c>MapGroup("")</c> vanish on its own, so the
/// <c>/api/ordering//checkout</c> trap from the survey needs no special case.
/// </summary>
public static class RouteText
{
    /// <summary>
    /// Joins route fragments into a single absolute path. Empty and whitespace-only fragments
    /// contribute nothing; duplicate separators collapse.
    /// </summary>
    public static string Combine(params string?[] parts)
    {
        var segments = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .SelectMany(p => p!.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }
}

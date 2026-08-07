using Microsoft.CodeAnalysis;

namespace FlowLens.Core;

/// <summary>
/// A workspace diagnostic raised while loading a solution, deduplicated by (kind, message).
/// MSBuildWorkspace tends to raise the same message once per affected project, so the raw
/// event stream is noisy; <see cref="OccurrenceCount"/> preserves that information without
/// printing the same line dozens of times.
/// </summary>
public sealed record LoadDiagnostic(
    WorkspaceDiagnosticKind Kind,
    string Message,
    int OccurrenceCount);

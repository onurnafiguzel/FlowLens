using System.Reflection;
using System.Runtime.Loader;

namespace FlowLens.Core.Ef;

/// <summary>
/// Loads the target repository's compiled assemblies so their DbContext types can be instantiated
/// and their EF Core model read.
/// <para>
/// <b>Why a custom context at all, and why this exact shape.</b> Three designs were measured
/// before this one. The two obvious ones are both wrong here:
/// </para>
/// <para>
/// <i>Assembly.LoadFrom is dangerous.</i> On .NET Core it is not a separate binding context - it
/// is <c>Default.LoadFromAssemblyPath</c> plus a lazily installed catch-all
/// <c>Default.Resolving</c> probe over the loaded file's directory. The only directory that has a
/// complete dependency closure is the target's host output, and that directory contains
/// Microsoft.Build.Framework 15.1.0.0 and Roslyn 5.0.0.0 (the target references
/// EntityFrameworkCore.Design, which drags Roslyn in). Probing it from Default would reintroduce
/// exactly the MSBL001 topology that Directory.Build.props exists to prevent.
/// </para>
/// <para>
/// <i>Total isolation does not work.</i> AssemblyDependencyResolver deliberately returns null for
/// shared-framework assemblies - they are absent from deps.json so that they unify with Default.
/// EF Core asks for Microsoft.Extensions.Caching.Memory, which lives in the ASP.NET Core shared
/// framework; FlowLens targets Microsoft.NETCore.App only, so Default cannot supply it either and
/// the load fails. A meaningful slice of EF's dependency surface crosses into Default no matter
/// what the resolver does.
/// </para>
/// <para>
/// <b>So: an explicit two-list policy.</b> The names in <see cref="IsUnified"/> resolve from
/// FlowLens's own TPA, which is what makes <c>typeof(DbContext)</c> the same <see cref="Type"/> on
/// both sides of the boundary - without that, every cast would fail. Everything else, meaning the
/// target's own code, loads privately here. The unified set is exactly FlowLens.Core's package
/// list, so version conflicts surface at restore time as NU1605 rather than at runtime. That is
/// the structural difference from MSBL001: no resolver hook is involved in the decision.
/// </para>
/// <para>
/// Never collectible, one instance per run: EF caches its internal service provider in static
/// state and holds references to types loaded here, so unloading would only add lifetime hazards
/// to a batch process that exits anyway.
/// </para>
/// </summary>
/// <remarks>
/// Internal because <see cref="EfProbe"/> is the single entry point to everything EF-related. If
/// the probe is ever moved to its own process, this class moves with it - see the migration steps
/// on <see cref="EfProbe"/>.
/// </remarks>
internal sealed class TargetModelLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _probeDirectory;
    private readonly List<string> _unresolved = [];

    /// <param name="hostAssemblyPath">
    /// Path to the target's composition-root assembly (ModularCommerce.Host.dll). It must be the
    /// application's output, not a class library's: <c>dotnet build</c> does not copy NuGet assets
    /// into a library's bin, so a module's own output directory contains no EF Core at all and its
    /// deps.json cannot be resolved against anything on disk.
    /// </param>
    public TargetModelLoadContext(string hostAssemblyPath)
        : base(name: "FlowLens.TargetModel", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(hostAssemblyPath);
        _probeDirectory = Path.GetDirectoryName(hostAssemblyPath)
            ?? throw new ArgumentException($"No directory in path: {hostAssemblyPath}", nameof(hostAssemblyPath));
    }

    /// <summary>
    /// Names this context was asked for and could not supply. Expected to stay empty; anything
    /// here means a type may have failed to load, and a dropped IEntityTypeConfiguration is a
    /// silently missing table.
    /// </summary>
    public IReadOnlyList<string> UnresolvedAssemblies => _unresolved;

    public string ProbeDirectory => _probeDirectory;

    /// <summary>
    /// Assemblies that MUST come from the Default context, because FlowLens calls their API
    /// directly and a second copy would make <c>typeof(DbContext)</c> mean two different things on
    /// the two sides of the boundary.
    /// </summary>
    internal static bool IsUnified(string? name) =>
        name is not null
        && (name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Npgsql", StringComparison.Ordinal)
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name is "netstandard" or "mscorlib");

    /// <summary>
    /// Assemblies to take from Default when it has them, and from the target otherwise.
    /// <para>
    /// Microsoft.Extensions.* sits in an awkward middle. Sharing it is preferable, since EF's own
    /// dependency graph reaches DI and logging abstractions and two copies could split those types.
    /// But the target is an ASP.NET Core app whose shared framework contains members FlowLens's
    /// framework does not - Hosting.Abstractions among them - so unconditionally deferring to
    /// Default makes those unloadable. Preferring Default and falling back keeps the common types
    /// shared without losing the ones only the target has.
    /// </para>
    /// </summary>
    private static bool PrefersDefault(string? name) =>
        name is not null && name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsUnified(assemblyName.Name))
        {
            // null -> the runtime falls back to Default -> the TPA list.
            return null;
        }

        if (PrefersDefault(assemblyName.Name))
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
            {
                // Not on FlowLens's TPA; the target ships its own copy. Fall through.
            }
        }

        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved is not null)
        {
            return LoadFromAssemblyPath(resolved);
        }

        // deps.json does not describe everything the SDK copies next to the app, so fall back to a
        // sibling lookup in the same directory before giving up.
        var sibling = Path.Combine(_probeDirectory, assemblyName.Name + ".dll");
        if (File.Exists(sibling))
        {
            return LoadFromAssemblyPath(sibling);
        }

        // Record it, then return null so the CLR raises a named exception rather than a mystery.
        var display = assemblyName.Name ?? assemblyName.FullName;
        if (!_unresolved.Contains(display, StringComparer.Ordinal))
        {
            _unresolved.Add(display);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

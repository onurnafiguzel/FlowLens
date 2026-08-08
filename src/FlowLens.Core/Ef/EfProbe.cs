using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlowLens.Core.Ef;

/// <summary>
/// Instantiates the target's DbContext types and reads EF Core's model metadata out of them.
///
/// <para>
/// <b>This is the ONLY class in FlowLens that touches EF Core.</b> Every
/// <c>Microsoft.EntityFrameworkCore</c> and <c>Npgsql</c> type reference in the whole product is in
/// this file; the analyzers compare metadata names as plain strings, and everything downstream
/// consumes <see cref="EfModelSnapshot"/>, which is strings and bools. <c>EfProbeArchitectureTests</c>
/// enforces that rather than trusting it.
/// </para>
///
/// <para>
/// <b>MOVING THIS TO A SEPARATE PROCESS</b> (if it is ever needed - see L14 in
/// docs/known-limitations.md):
/// <list type="number">
/// <item>Move this file plus <c>TargetModelLoadContext</c> and <c>EfVersionGate</c> into a new
/// <c>FlowLens.EfProbe</c> executable.</item>
/// <item>Its Main becomes: <c>EfProbe.Read(...)</c> -&gt; <see cref="EfModelContract.Serialize"/>
/// -&gt; stdout.</item>
/// <item>FlowLens.Core replaces the direct call with <c>Process.Start</c> plus
/// <see cref="EfModelContract.Deserialize"/> on that stdout.</item>
/// <item>The EF and Npgsql package references move from FlowLens.Core to that project.</item>
/// </list>
/// Step 3 already works today, because the only thing crossing this boundary is
/// <see cref="EfModelSnapshot"/> and it is entirely strings and bools - <c>EfProbeContractTests</c>
/// round-trips the real target's snapshot on every run, so the portability claim is a verified
/// fact rather than an intention.
/// </para>
///
/// <para>
/// The roadmap is explicit that table and column names must come from <c>IModel</c> rather than
/// from name conventions or SQL parsing, because a wrong column means a missing migration means a
/// production failure. That is what forces the target's assemblies to be loaded at all - see
/// <see cref="TargetModelLoadContext"/> for why doing so is safe here.
/// </para>
///
/// <para>
/// No database is contacted. Building a model is a pure function of the source: the path is
/// ModelSource -&gt; ModelBuilder -&gt; OnModelCreating -&gt; conventions -&gt; validators, and nothing on it
/// opens a connection. The provider is still required to be Npgsql, because the configurations use
/// provider-specific mappings (jsonb column types, uint xmin row versions, ToJson owned
/// collections) that a different provider would either reject or map to different store types.
/// </para>
/// </summary>
public static class EfProbe
{
    /// <summary>
    /// Well-formed on purpose. NpgsqlConnectionStringBuilder parses this eagerly, so a placeholder
    /// like "fake" would throw before the model is ever built. Nothing connects to it.
    /// </summary>
    private const string ProbeConnectionString =
        "Host=localhost;Database=flowlens_probe;Username=flowlens;Password=flowlens";

    /// <summary>
    /// Pinned so the output is a function of the target's source alone. Left unset, the provider
    /// would fall back to its own default, which could change between provider versions and
    /// silently alter a store type.
    /// </summary>
    private static readonly Version ProbePostgresVersion = new(17, 0);

    /// <summary>
    /// Reads every declared context. Assumes <see cref="EfPreflight.BeforeRead"/> has already run
    /// and did not block - the version gate in particular must pass before any target assembly is
    /// loaded, because a skew detected afterwards surfaces as an exception from inside EF instead
    /// of a sentence naming both versions.
    /// </summary>
    public static EfModelReadResult Read(
        string hostAssemblyPath,
        IReadOnlyList<DbContextDeclaration> declarations,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var snapshots = new List<EfModelSnapshot>();
        var failures = new List<EfModelFailure>();
        var warnings = new List<string>();

        var loadContext = new TargetModelLoadContext(hostAssemblyPath);
        var assemblies = new Dictionary<string, Assembly?>(StringComparer.Ordinal);

        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var assembly = LoadAssembly(loadContext, declaration.AssemblyName, assemblies, warnings);
                if (assembly is null)
                {
                    failures.Add(new EfModelFailure(
                        declaration.ClrTypeName,
                        declaration.Location,
                        $"assembly {declaration.AssemblyName}.dll not found in {loadContext.ProbeDirectory}"));
                    continue;
                }

                var contextType = assembly.GetType(declaration.ClrTypeName, throwOnError: false);
                if (contextType is null)
                {
                    failures.Add(new EfModelFailure(
                        declaration.ClrTypeName,
                        declaration.Location,
                        $"type not present in {assembly.GetName().Name}; the build may be stale"));
                    continue;
                }

                snapshots.Add(ReadOne(contextType));
            }
            catch (Exception ex)
            {
                // One broken context must not hide the other seven. Whether this is fatal is
                // EfPreflight.AfterRead's call - and it is, because a module whose tables are
                // missing makes "touches nothing" indistinguishable from "we could not look".
                failures.Add(new EfModelFailure(
                    declaration.ClrTypeName,
                    declaration.Location,
                    Describe(ex)));
            }
        }

        stopwatch.Stop();

        return new EfModelReadResult(
            snapshots,
            failures,
            loadContext.UnresolvedAssemblies,
            warnings,
            stopwatch.Elapsed);
    }

    private static Assembly? LoadAssembly(
        TargetModelLoadContext loadContext,
        string assemblyName,
        Dictionary<string, Assembly?> cache,
        List<string> warnings)
    {
        if (cache.TryGetValue(assemblyName, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(loadContext.ProbeDirectory, assemblyName + ".dll");
        Assembly? assembly = File.Exists(path) ? loadContext.LoadFromAssemblyPath(path) : null;

        if (assembly is not null)
        {
            ProbeForLoaderFailures(assembly, warnings);
        }

        cache[assemblyName] = assembly;
        return assembly;
    }

    /// <summary>
    /// Forces type loading and surfaces what failed.
    /// <para>
    /// This exists because EF's ApplyConfigurationsFromAssembly swallows ReflectionTypeLoadException
    /// internally and quietly skips the types it could not load. An IEntityTypeConfiguration that
    /// fails to load therefore produces no error and no table - the exact class of silent loss
    /// known-limitations.md exists to prevent. Asking for the types ourselves is what turns it into
    /// something EfPreflight can stop on.
    /// </para>
    /// </summary>
    private static void ProbeForLoaderFailures(Assembly assembly, List<string> warnings)
    {
        try
        {
            assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var reasons = ex.LoaderExceptions
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Distinct(StringComparer.Ordinal)
                .Take(5);

            warnings.Add(
                $"{assembly.GetName().Name}: {ex.Types.Count(t => t is null)} type(s) failed to load - " +
                string.Join(" | ", reasons));
        }
    }

    private static EfModelSnapshot ReadOne(Type contextType)
    {
        using var context = Instantiate(contextType);
        var model = context.Model;

        var entities = new List<EfEntity>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var properties = new List<EfProperty>();
            var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);

            CollectProperties(entityType, storeObject, clrType, properties, null, null);

            // Every mapped entity is a concrete class, so FullName is present. Falling back to the
            // simple name rather than skipping keeps the entity visible if that ever stops holding -
            // an unnamed table is a bug report, a missing one is a wrong answer.
            entities.Add(new EfEntity(
                ClrTypeName: FullName(clrType) ?? clrType.Name,
                Schema: entityType.GetSchema(),
                TableName: entityType.GetTableName(),
                OwnerClrTypeName: FullName(entityType.FindOwnership()?.PrincipalEntityType.ClrType),
                IsMappedToJson: entityType.IsMappedToJson(),
                Properties: properties));
        }

        return new EfModelSnapshot(
            FullName(contextType)!,
            model.GetDefaultSchema(),
            entities);
    }

    /// <summary>
    /// Scalar properties plus the members of complex properties, which are columns of the same
    /// table but are declared by a different CLR type: Product.Price is a Money, so price_amount
    /// is declared by Money and mapped onto catalog.products. Recording the declaring type is what
    /// lets the analyzer connect an assignment to `money.Amount` back to the right column.
    /// </summary>
    private static void CollectProperties(
        ITypeBase typeBase,
        StoreObjectIdentifier? storeObject,
        Type declaringClrType,
        List<EfProperty> properties,
        string? complexPropertyName,
        string? complexPropertyOwner)
    {
        foreach (var property in typeBase.GetProperties())
        {
            properties.Add(new EfProperty(
                Name: property.Name,
                ColumnName: storeObject is { } store
                    ? property.GetColumnName(store)
                    : property.GetColumnName(),
                JsonPropertyName: property.GetJsonPropertyName(),
                DeclaringClrTypeName: FullName(declaringClrType) ?? declaringClrType.Name,
                IsShadow: property.IsShadowProperty(),
                IsRowVersion: property.IsConcurrencyToken && property.IsShadowProperty(),
                StoreType: property.GetColumnType(),
                ComplexPropertyName: complexPropertyName,
                ComplexPropertyOwnerClrTypeName: complexPropertyOwner));
        }

        // Complex-type members are columns of the SAME table, declared by a different CLR type.
        // Both routes to them are recorded: as Money.Amount (how an assignment inside the value
        // object appears) and as Product.Price (how the entity's own code refers to it).
        foreach (var complex in typeBase.GetComplexProperties())
        {
            CollectProperties(
                complex.ComplexType,
                storeObject,
                complex.ComplexType.ClrType,
                properties,
                complex.Name,
                FullName(typeBase.ClrType) ?? typeBase.ClrType.Name);
        }
    }

    /// <summary>
    /// The context's generic parameter is not known at compile time, but no MakeGenericMethod is
    /// needed: DbContextOptionsBuilder&lt;TContext&gt; derives from the non-generic builder, and the
    /// generic one seeds a DbContextOptions&lt;TContext&gt; that survives WithExtension. So the
    /// Options property still has the closed generic runtime type that DbContext's constructor
    /// validates against.
    /// </summary>
    private static DbContext Instantiate(Type contextType)
    {
        var builderType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(builderType)!;

        builder.UseNpgsql(
            ProbeConnectionString,
            npgsql => npgsql.SetPostgresVersion(ProbePostgresVersion));

        return (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }

    private static string? FullName(Type? type) => type?.FullName?.Replace('+', '.');

    private static string Describe(Exception ex)
    {
        var inner = ex is TargetInvocationException { InnerException: { } target } ? target : ex;
        return $"{inner.GetType().Name}: {inner.Message.ReplaceLineEndings(" ")}";
    }
}

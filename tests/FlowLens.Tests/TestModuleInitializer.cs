using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

// Test collections run in parallel by default, and from Phase 3 there are two fixtures that each
// open the target solution with MSBuildWorkspace - Phase2Fixture and Phase3Fixture. Two concurrent
// loads of the same solution contend over MSBuild's build-host processes and the target's obj/
// directory, and the loser reports project load failures. Observed exactly once as 12 spurious
// failures in Phase 2's suite, green again on re-run.
//
// A flaky suite is worse than a slow one: it teaches people that red means "run it again". Loading
// dominates the runtime anyway, so serialising costs little.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FlowLens.Tests;

internal static class TestModuleInitializer
{
    /// <summary>
    /// The test-host equivalent of Program.cs's first statement. MSBuildWorkspace cannot be
    /// touched until MSBuildLocator has installed its assembly resolver, and a module
    /// initializer is guaranteed to run before any method in this assembly executes - so it
    /// beats every test to the punch without each one having to remember.
    /// <para>
    /// This file names no MSBuild type other than the locator itself, for the same JIT reason
    /// documented on <c>SolutionLoader</c>.
    /// </para>
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }
}

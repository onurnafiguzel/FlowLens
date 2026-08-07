using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

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

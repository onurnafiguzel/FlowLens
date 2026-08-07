using FlowLens.Cli;
using Microsoft.Build.Locator;

// ---------------------------------------------------------------------------------------
// Nothing may precede this registration, and this file must not name a single MSBuild or
// Roslyn-workspace type. See SolutionLoader's docs for the full explanation: the JIT resolves
// every type named in a method body when that method is first called, so mentioning
// MSBuildWorkspace *anywhere in this file* would trigger the MSBuild assembly load before the
// resolver below is installed - even if RegisterDefaults is written on the line above it.
//
// Runner is a separate type, so its body is JIT-compiled only when RunAsync is first invoked,
// which is after this line has run.
// ---------------------------------------------------------------------------------------
if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

return await Runner.RunAsync(args);

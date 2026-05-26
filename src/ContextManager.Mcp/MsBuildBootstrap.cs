using Microsoft.Build.Locator;

namespace ContextManager.Mcp;

internal static class MsBuildBootstrap
{
    // Idempotent: safe to call multiple times. Throws if no MSBuild instance is available.
    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
            return;

        MSBuildLocator.RegisterDefaults();
    }
}

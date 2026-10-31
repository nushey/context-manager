using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace ContextManager.Mcp;

internal static class MsBuildBootstrap
{
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (!MSBuildLocator.IsRegistered)
                Register();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register()
    {
        var instance = MSBuildLocator.QueryVisualStudioInstances()
            .OrderByDescending(i => i.Version)
            .FirstOrDefault();

        if (instance is null)
            throw new InvalidOperationException(
                "No MSBuild instance found. Install Visual Studio or the Build Tools.");

        MSBuildLocator.RegisterInstance(instance);
    }
}

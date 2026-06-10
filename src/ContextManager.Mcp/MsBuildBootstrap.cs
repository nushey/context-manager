using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace ContextManager.Mcp;

internal static class MsBuildBootstrap
{
    public const string MsBuildPathVariable = "CONTEXT_MANAGER_MSBUILD_PATH";

    // Idempotent: safe to call multiple times. Throws if no MSBuild instance is available.
    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
            return;

        Register();
    }

    // Kept separate and non-inlined so no Microsoft.Build type is JIT-touched
    // before the locator registers an instance.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Register()
    {
        var pinnedPath = Environment.GetEnvironmentVariable(MsBuildPathVariable);
        if (!string.IsNullOrWhiteSpace(pinnedPath))
        {
            if (!Directory.Exists(pinnedPath))
                throw new InvalidOperationException(
                    $"{MsBuildPathVariable} points to a directory that does not exist: {pinnedPath}");

            MSBuildLocator.RegisterMSBuildPath(pinnedPath);
            return;
        }

        // With multiple installs (e.g. VS 2022 Build Tools 17.x + VS 18.x),
        // RegisterDefaults() picks nondeterministically and can mix assemblies
        // across major versions. Always pick the highest version explicitly.
        var instance = MSBuildLocator.QueryVisualStudioInstances()
            .OrderByDescending(i => i.Version)
            .FirstOrDefault();

        if (instance is null)
            throw new InvalidOperationException(
                "No MSBuild instance found. Install Visual Studio or the Build Tools, " +
                $"or set {MsBuildPathVariable} to an MSBuild bin directory.");

        MSBuildLocator.RegisterInstance(instance);
    }
}

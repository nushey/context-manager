using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace ContextManager.Mcp;

internal static class MsBuildBootstrap
{
    private static string? _selectedIdentity;

    // Idempotent: safe to call multiple times. Throws if no MSBuild instance is available.
    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            _selectedIdentity ??= "pre-registered by the current process";
            return;
        }

        Register();
    }

    // Kept separate and non-inlined so no Microsoft.Build type is JIT-touched
    // before the locator registers an instance.
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
        _selectedIdentity = $"{instance.Name} {instance.Version} at '{instance.MSBuildPath}'";
    }

    public static string DescribeRegistration()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name is { } name && name.StartsWith("Microsoft.Build", StringComparison.Ordinal))
            .Select(a => $"{a.GetName().FullName} from '{a.Location}'")
            .ToList();

        return $"selected={_selectedIdentity ?? "unknown"}; loaded=[{string.Join("; ", loaded)}]";
    }
}

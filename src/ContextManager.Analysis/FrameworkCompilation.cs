using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ContextManager.Analysis;

internal static class FrameworkCompilation
{
    private const string ImplicitGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(() =>
    {
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa
            ? tpa.Split(Path.PathSeparator).Where(File.Exists).ToList()
            : [];

        var assemblyNames = new HashSet<string>(trusted.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
        var paths = trusted.Concat(SharedFrameworkAssemblies().Where(p => assemblyNames.Add(Path.GetFileName(p))));

        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    });

    public static CSharpCompilation Create(IEnumerable<SyntaxTree> trees, CancellationToken ct)
    {
        var implicitUsings = CSharpSyntaxTree.ParseText(
            ImplicitGlobalUsings,
            path: "__ContextManagerImplicitGlobalUsings.g.cs",
            cancellationToken: ct);

        return CSharpCompilation.Create("ContextAnalysis", trees.Append(implicitUsings), References.Value);
    }

    private static IEnumerable<string> SharedFrameworkAssemblies()
    {
        var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var sharedDirectory = runtimeDirectory.Parent?.Parent;
        if (sharedDirectory is null || !sharedDirectory.Exists)
            return [];

        return sharedDirectory.EnumerateDirectories()
            .Where(framework => framework.FullName != runtimeDirectory.Parent!.FullName)
            .Select(framework => Path.Combine(framework.FullName, runtimeDirectory.Name))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll"))
            .Where(IsManagedAssembly);
    }

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}

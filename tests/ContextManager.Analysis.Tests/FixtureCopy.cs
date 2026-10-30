namespace ContextManager.Analysis.Tests;

internal static class FixtureCopy
{
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextManager.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    public static string SourcePath(params string[] relative) =>
        Path.Combine([RepoRoot(), "tests", "ContextManager.Analysis.Tests", "Fixtures", .. relative]);

    public static string CopyToTemp(string source)
    {
        var target = Path.Combine(Path.GetTempPath(), $"context-manager-fixture-{Guid.NewGuid():N}");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var segments = relativePath.Split(Path.DirectorySeparatorChar);
            if (segments.Any(s => s is "bin" or "obj" or ".context-manager"))
                continue;

            var destination = Path.Combine(target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return target;
    }

    public static void Edit(string path, string from, string to)
    {
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (!text.Contains(from, StringComparison.Ordinal))
            throw new InvalidOperationException($"Fixture edit anchor not found in {path}: {from}");

        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }
}

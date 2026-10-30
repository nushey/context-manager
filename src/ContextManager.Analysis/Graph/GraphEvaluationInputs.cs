using System.Runtime.CompilerServices;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Globbing;

namespace ContextManager.Analysis.Graph;

internal static class GraphEvaluationInputs
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (IReadOnlyList<string> Imports, IReadOnlyList<string> SourceRoots) Collect(IReadOnlyList<string> projectFiles)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        using var collection = new ProjectCollection();

        foreach (var projectFile in projectFiles)
        {
            Project project;
            try
            {
                project = collection.LoadProject(projectFile);
            }
            catch (InvalidProjectFileException)
            {
                continue;
            }

            foreach (var import in project.Imports)
                imports.Add(Path.GetFullPath(import.ImportedProject.FullPath));

            foreach (var glob in project.GetAllGlobs("Compile"))
                foreach (var include in glob.IncludeGlobs)
                    roots.Add(Path.GetFullPath(MSBuildGlob.Parse(project.DirectoryPath, include).FixedDirectoryPart));

            collection.UnloadProject(project);
        }

        return (imports.ToList(), roots.ToList());
    }
}

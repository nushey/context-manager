using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ContextManager.Analysis.Graph;

public sealed class GraphWorkspaceSession : IDisposable
{
    private readonly MSBuildWorkspace _workspace;

    private GraphWorkspaceSession(
        MSBuildWorkspace workspace,
        string solutionPath,
        Solution solution,
        IReadOnlyList<GraphBuildDiagnostic> diagnostics,
        IReadOnlyList<GraphTrackedFile> inputs,
        IReadOnlyList<string> candidateFiles,
        IReadOnlyList<string> sourceRoots,
        TimeSpan evaluation)
    {
        _workspace = workspace;
        SolutionPath = solutionPath;
        Solution = solution;
        Diagnostics = diagnostics;
        Inputs = inputs;
        CandidateFiles = candidateFiles;
        SourceRoots = sourceRoots;
        Evaluation = evaluation;
    }

    public string SolutionPath { get; }
    public Solution Solution { get; private set; }
    public IReadOnlyList<GraphBuildDiagnostic> Diagnostics { get; }
    public IReadOnlyList<GraphTrackedFile> Inputs { get; }
    public IReadOnlyList<string> CandidateFiles { get; }
    public IReadOnlyList<string> SourceRoots { get; }
    public TimeSpan Evaluation { get; }
    public string? Revision { get; private set; }

    public static async Task<GraphWorkspaceSession> OpenAsync(string solutionPath, CancellationToken ct)
    {
        var evaluationStartedUtc = DateTime.UtcNow;
        var timer = Stopwatch.StartNew();
        var workspace = MSBuildWorkspace.Create();
        try
        {
            var diagnostics = new List<GraphBuildDiagnostic>();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                lock (diagnostics)
                    diagnostics.Add(new GraphBuildDiagnostic(args.Diagnostic.Kind.ToString(), args.Diagnostic.Message));
            });

            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
            var projectFiles = solution.Projects.Select(p => p.FilePath).OfType<string>().Distinct().ToList();
            var (imports, sourceRoots) = GraphEvaluationInputs.Collect(projectFiles);
            var configs = new Dictionary<string, GraphTrackedFile>(StringComparer.Ordinal);
            foreach (var document in solution.Projects.SelectMany(p => p.AnalyzerConfigDocuments))
            {
                var config = await TrackAsync(document, null, ct);
                configs.TryAdd(config.Path, config);
            }

            var sourceFiles = solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>().ToList();
            var inputs = ScanInputs(solutionPath, projectFiles, imports.Concat(configs.Keys).ToList(), sourceFiles, evaluationStartedUtc)
                .Select(input => configs.GetValueOrDefault(input.Path) ?? input)
                .ToList();
            var candidates = ScanCandidateFiles(projectFiles, sourceRoots, evaluationStartedUtc);
            timer.Stop();

            List<GraphBuildDiagnostic> diagnosticSnapshot;
            lock (diagnostics)
                diagnosticSnapshot = diagnostics.ToList();

            return new GraphWorkspaceSession(workspace, solutionPath, solution, diagnosticSnapshot, inputs, candidates, sourceRoots, timer.Elapsed);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public static string ProjectKey(Project project) =>
        $"{Path.GetFullPath(project.FilePath ?? project.Name)}|{project.Name}";

    public void Advance(Solution solution, string? revision)
    {
        Solution = solution;
        Revision = revision;
    }

    /// <summary>
    /// Compares the persisted baseline with the files on disk without evaluating MSBuild.
    /// <c>EvaluationChanged</c> is true when an evaluation input, the set of candidate source files,
    /// or a tracked file's existence changed; otherwise <c>ChangedFiles</c> lists tracked documents and
    /// additional files whose content differs.
    /// </summary>
    public static (bool EvaluationChanged, IReadOnlyList<string> ChangedFiles) CheckFreshness(GraphBaseline baseline)
    {
        var projectFiles = baseline.Projects.Select(p => p.FilePath).Distinct().ToList();
        var sourceFiles = baseline.Projects.SelectMany(p => p.Documents).Select(d => d.Path).ToList();
        var inputs = ScanInputs(baseline.SolutionPath, projectFiles, baseline.Inputs.Select(i => i.Path).ToList(), sourceFiles, notAfterUtc: null);
        if (!inputs.SequenceEqual(baseline.Inputs))
            return (true, []);

        if (!ScanCandidateFiles(projectFiles, baseline.SourceRoots ?? [], notAfterUtc: null).SequenceEqual(baseline.CandidateFiles, StringComparer.Ordinal))
            return (true, []);

        var changed = new List<string>();
        var checkedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in baseline.Projects.SelectMany(p => p.Documents.Concat(p.AdditionalFiles)))
        {
            if (!checkedPaths.Add(file.Path))
                continue;

            var checksum = HashFile(file.Path);
            if (checksum is null)
                return (true, []);

            if (checksum != file.Checksum)
                changed.Add(file.Path);
        }

        return (false, changed);
    }

    /// <summary>
    /// Applies the current disk content of <paramref name="changedFiles"/> to every document context
    /// that contains them, then decides per project what must be re-extracted: the whole project when
    /// its dependent semantic version or its source-generated output changed (and every project that
    /// transitively depends on a project whose generated output changed), otherwise only its changed
    /// documents. Returns the derived solution and the selection; a null selection means the whole
    /// project.
    /// </summary>
    public async Task<(Solution Solution, IReadOnlyDictionary<ProjectId, IReadOnlySet<DocumentId>?> Selection)> ApplyTextChangesAsync(
        IReadOnlyList<string> changedFiles,
        CancellationToken ct)
    {
        var oldSolution = Solution;
        var newSolution = oldSolution;
        var changedDocuments = new Dictionary<ProjectId, HashSet<DocumentId>>();
        var touchedProjects = new HashSet<ProjectId>();

        foreach (var path in changedFiles)
        {
            SourceText text;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                text = SourceText.From(stream, checksumAlgorithm: SourceHashAlgorithm.Sha256);

            foreach (var id in oldSolution.GetDocumentIdsWithFilePath(path))
            {
                touchedProjects.Add(id.ProjectId);
                if (oldSolution.GetDocument(id) is not null)
                {
                    newSolution = newSolution.WithDocumentText(id, text);
                    if (!changedDocuments.TryGetValue(id.ProjectId, out var ids))
                        changedDocuments[id.ProjectId] = ids = [];
                    ids.Add(id);
                }
                else if (oldSolution.GetAdditionalDocument(id) is not null)
                {
                    newSolution = newSolution.WithAdditionalDocumentText(id, text);
                }
            }
        }

        var selection = new Dictionary<ProjectId, IReadOnlySet<DocumentId>?>();
        var generatedChanged = new List<ProjectId>();
        foreach (var project in newSolution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            ct.ThrowIfCancellationRequested();
            var oldProject = oldSolution.GetProject(project.Id)!;
            if (await oldProject.GetDependentSemanticVersionAsync(ct) != await project.GetDependentSemanticVersionAsync(ct))
            {
                selection[project.Id] = null;
                continue;
            }

            if (!touchedProjects.Contains(project.Id))
                continue;

            if (!(await GeneratedChecksumsAsync(oldProject, ct)).SequenceEqual(await GeneratedChecksumsAsync(project, ct)))
            {
                generatedChanged.Add(project.Id);
                selection[project.Id] = null;
                continue;
            }

            if (changedDocuments.TryGetValue(project.Id, out var documents))
                selection[project.Id] = documents;
        }

        var dependencyGraph = newSolution.GetProjectDependencyGraph();
        foreach (var dependent in generatedChanged.SelectMany(dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject))
        {
            if (newSolution.GetProject(dependent)?.Language == LanguageNames.CSharp)
                selection[dependent] = null;
        }

        return (newSolution, selection);
    }

    /// <summary>
    /// Selects the projects of this freshly evaluated solution that must be fully re-extracted
    /// against a persisted baseline from another evaluation: every C# project when there is no
    /// baseline; otherwise each project that is new or whose evaluated fingerprint, references,
    /// documents, or additional files differ, plus the projects that transitively depend on a changed
    /// or removed project in either the old or the new dependency graph.
    /// </summary>
    public async Task<IReadOnlyDictionary<ProjectId, IReadOnlySet<DocumentId>?>> SelectChangedProjectsAsync(
        GraphBaseline? previous,
        CancellationToken ct)
    {
        var csharpProjects = Solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        if (previous is null)
            return csharpProjects.ToDictionary(p => p.Id, _ => (IReadOnlySet<DocumentId>?)null);

        var previousByKey = previous.Projects.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var changedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in Solution.Projects)
        {
            var state = await DescribeProjectAsync(project, false, null, ct);
            currentKeys.Add(state.Key);
            if (!previousByKey.TryGetValue(state.Key, out var old) || !SameEvaluation(old, state))
                changedKeys.Add(state.Key);
        }

        changedKeys.UnionWith(previousByKey.Keys.Where(k => !currentKeys.Contains(k)));

        var dependentsByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddDependency(string from, string to)
        {
            if (!dependentsByKey.TryGetValue(to, out var dependents))
                dependentsByKey[to] = dependents = new HashSet<string>(StringComparer.Ordinal);
            dependents.Add(from);
        }

        foreach (var old in previous.Projects)
            foreach (var reference in old.ProjectReferences)
                AddDependency(old.Key, reference);

        foreach (var project in Solution.Projects)
            foreach (var reference in project.ProjectReferences)
                if (Solution.GetProject(reference.ProjectId) is { } target)
                    AddDependency(ProjectKey(project), ProjectKey(target));

        var queue = new Queue<string>(changedKeys);
        while (queue.Count > 0)
        {
            if (!dependentsByKey.TryGetValue(queue.Dequeue(), out var dependents))
                continue;

            foreach (var dependent in dependents)
                if (changedKeys.Add(dependent))
                    queue.Enqueue(dependent);
        }

        return csharpProjects
            .Where(p => changedKeys.Contains(ProjectKey(p)))
            .ToDictionary(p => p.Id, _ => (IReadOnlySet<DocumentId>?)null);
    }

    /// <summary>
    /// Describes <paramref name="project"/> for the persisted baseline. Document
    /// checksums come from the loaded texts, so they match exactly what was extracted.
    /// <paramref name="loaded"/> and <paramref name="extracted"/> carry extraction outcomes.
    /// </summary>
    public static async Task<GraphProjectState> DescribeProjectAsync(
        Project project,
        bool loaded,
        Func<string, bool>? extracted,
        CancellationToken ct)
    {
        var documents = new List<GraphTrackedFile>();
        var additionalFiles = new List<GraphTrackedFile>();
        if (project.Language == LanguageNames.CSharp)
        {
            foreach (var document in project.Documents)
                documents.Add(await TrackAsync(document, extracted, ct));

            foreach (var document in project.AdditionalDocuments)
                additionalFiles.Add(await TrackAsync(document, null, ct));
        }

        return new GraphProjectState(
            ProjectKey(project),
            Path.GetFullPath(project.FilePath ?? project.Name),
            project.Language,
            loaded,
            await FingerprintAsync(project, ct),
            project.ProjectReferences
                .Select(r => project.Solution.GetProject(r.ProjectId))
                .OfType<Project>()
                .Select(ProjectKey)
                .ToList(),
            documents,
            additionalFiles);
    }

    public void Dispose() => _workspace.Dispose();

    internal static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static bool SameEvaluation(GraphProjectState old, GraphProjectState current) =>
        old.FilePath == current.FilePath &&
        old.Language == current.Language &&
        old.Fingerprint == current.Fingerprint &&
        old.ProjectReferences.SequenceEqual(current.ProjectReferences, StringComparer.Ordinal) &&
        old.Documents.Select(d => (d.Path, d.Checksum)).SequenceEqual(current.Documents.Select(d => (d.Path, d.Checksum))) &&
        old.AdditionalFiles.SequenceEqual(current.AdditionalFiles);

    private static async Task<GraphTrackedFile> TrackAsync(TextDocument document, Func<string, bool>? extracted, CancellationToken ct)
    {
        var path = Path.GetFullPath(document.FilePath ?? document.Name);
        var text = await document.GetTextAsync(ct);
        return new GraphTrackedFile(path, Convert.ToHexString(text.GetChecksum().AsSpan()), extracted?.Invoke(path) ?? false);
    }

    private static async Task<IReadOnlyList<(string Path, string Checksum)>> GeneratedChecksumsAsync(Project project, CancellationToken ct)
    {
        var generated = await project.GetSourceGeneratedDocumentsAsync(ct);
        var checksums = new List<(string, string)>();
        foreach (var document in generated)
        {
            var text = await document.GetTextAsync(ct);
            checksums.Add((document.HintName, Convert.ToHexString(text.GetChecksum().AsSpan())));
        }

        return checksums;
    }

    private static async Task<string> FingerprintAsync(Project project, CancellationToken ct)
    {
        var builder = new StringBuilder();
        builder.AppendLine(project.Language).AppendLine(project.AssemblyName);

        if (project.ParseOptions is CSharpParseOptions parse)
        {
            builder.AppendLine($"{parse.LanguageVersion}|{parse.DocumentationMode}|{parse.Kind}|{string.Join(";", parse.PreprocessorSymbolNames)}");
            builder.AppendLine(string.Join(";", parse.Features.Select(f => $"{f.Key}={f.Value}")));
        }

        if (project.CompilationOptions is CSharpCompilationOptions options)
        {
            builder.AppendLine($"{options.OutputKind}|{options.NullableContextOptions}|{options.AllowUnsafe}|{options.CheckOverflow}|{options.Platform}|{options.OptimizationLevel}|{string.Join(";", options.Usings)}");
        }

        foreach (var reference in project.MetadataReferences)
        {
            var path = (reference as PortableExecutableReference)?.FilePath ?? reference.Display ?? string.Empty;
            var info = new FileInfo(path);
            builder.AppendLine($"M|{path}|{string.Join(",", reference.Properties.Aliases)}|{(info.Exists ? $"{info.LastWriteTimeUtc.Ticks}|{info.Length}" : "missing")}");
        }

        foreach (var reference in project.ProjectReferences)
            builder.AppendLine($"P|{project.Solution.GetProject(reference.ProjectId)?.FilePath}|{string.Join(",", reference.Aliases)}");

        foreach (var reference in project.AnalyzerReferences)
            builder.AppendLine($"A|{reference.FullPath}");

        foreach (var document in project.AnalyzerConfigDocuments)
        {
            var text = await document.GetTextAsync(ct);
            builder.AppendLine($"C|{document.FilePath}|{Convert.ToHexString(text.GetChecksum().AsSpan())}");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IReadOnlyList<GraphTrackedFile> ScanInputs(
        string solutionPath,
        IReadOnlyList<string> projectFiles,
        IReadOnlyList<string> evaluatedImports,
        IReadOnlyList<string> sourceFiles,
        DateTime? notAfterUtc)
    {
        var paths = new List<string> { Path.GetFullPath(solutionPath) };
        var seen = new HashSet<string>(paths, StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);

        foreach (var projectFile in projectFiles)
        {
            var fullProjectPath = Path.GetFullPath(projectFile);
            if (seen.Add(fullProjectPath))
                paths.Add(fullProjectPath);

            var assets = Path.Combine(Path.GetDirectoryName(fullProjectPath)!, "obj", "project.assets.json");
            if (File.Exists(assets) && seen.Add(assets))
                paths.Add(assets);

            for (var directory = Path.GetDirectoryName(fullProjectPath); directory is not null; directory = Path.GetDirectoryName(directory))
                directories.Add(directory);
        }

        directories.Add(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!);
        var configDirectories = new HashSet<string>(directories, StringComparer.Ordinal);
        foreach (var source in sourceFiles)
            for (var directory = Path.GetDirectoryName(Path.GetFullPath(source)); directory is not null; directory = Path.GetDirectoryName(directory))
                configDirectories.Add(directory);

        foreach (var directory in configDirectories.Order(StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, ".editorconfig").Concat(Directory.EnumerateFiles(directory, "*.globalconfig")).Order(StringComparer.Ordinal))
                if (seen.Add(file))
                    paths.Add(file);
        }

        foreach (var directory in directories.Order(StringComparer.Ordinal))
        {
            if (!Directory.Exists(directory))
                continue;

            var files = Directory.EnumerateFiles(directory, "*.props")
                .Concat(Directory.EnumerateFiles(directory, "*.targets"))
                .Concat(Directory.EnumerateFiles(directory, "global.json"))
                .Order(StringComparer.Ordinal);
            foreach (var file in files)
                if (seen.Add(file))
                    paths.Add(file);
        }

        foreach (var import in evaluatedImports.Where(i => !seen.Contains(i)).Order(StringComparer.Ordinal))
            if (seen.Add(import))
                paths.Add(import);

        return paths
            .Select(path => new GraphTrackedFile(path, IsStable(path, notAfterUtc) ? HashFile(path) ?? string.Empty : string.Empty))
            .ToList();
    }

    private static IReadOnlyList<string> ScanCandidateFiles(IReadOnlyList<string> projectFiles, IReadOnlyList<string> sourceRoots, DateTime? notAfterUtc)
    {
        var directories = projectFiles
            .Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!)
            .Concat(sourceRoots)
            .Where(Directory.Exists)
            .Distinct(StringComparer.Ordinal);

        return directories
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Where(GraphBuilder.IsSourceDocument)
            .Where(path => IsStable(path, notAfterUtc))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsStable(string path, DateTime? notAfterUtc) =>
        notAfterUtc is null || File.GetLastWriteTimeUtc(path) < notAfterUtc.Value;
}

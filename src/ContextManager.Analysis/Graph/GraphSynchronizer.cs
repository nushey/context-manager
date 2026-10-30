using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

public sealed class GraphSynchronizer : IGraphSynchronizer, IDisposable
{
    private readonly GraphBuilder _builder;
    private readonly GraphRegistry _registry;
    private readonly Action _ensureMsBuildRegistered;
    private readonly Func<string, string, CancellationToken, Task> _writeFile;
    private readonly ConcurrentDictionary<string, GraphWorkspaceSession> _sessions = new(StringComparer.Ordinal);

    public GraphSynchronizer(GraphBuilder builder, GraphRegistry registry, Action ensureMsBuildRegistered)
        : this(builder, registry, ensureMsBuildRegistered, GraphRegistry.WriteAtomicallyAsync)
    {
    }

    public GraphSynchronizer(
        GraphBuilder builder,
        GraphRegistry registry,
        Action ensureMsBuildRegistered,
        Func<string, string, CancellationToken, Task> writeFile)
    {
        _builder = builder;
        _registry = registry;
        _ensureMsBuildRegistered = ensureMsBuildRegistered;
        _writeFile = writeFile;
    }

    public async Task<GraphSyncResult?> SynchronizeAsync(string solutionPath, bool reevaluate, CancellationToken ct = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        var graphPath = GraphRegistry.GetGraphPath(solutionPath);
        if (!File.Exists(solutionPath) && !File.Exists(graphPath))
            return null;

        using var writer = await _registry.AcquireWriterAsync(solutionPath, ct);
        while (true)
        {
            var result = await TrySynchronizeAsync(solutionPath, graphPath, reevaluate, ct);
            if (result is not null)
                return result;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();
    }

    private async Task<GraphSyncResult?> TrySynchronizeAsync(string solutionPath, string graphPath, bool reevaluate, CancellationToken ct)
    {
        var phase = "graph_load";
        GraphWorkspaceSession? opened = null;
        try
        {
            var version = GraphRegistry.ReadVersion(graphPath);
            var current = (version.Exists ? _registry.TryLoad(solutionPath)?.Capture() : null) ?? new GraphStore();
            var baseline = current.HasProvenance ? current.Baseline : null;

            phase = "freshness";
            var freshnessTimer = Stopwatch.StartNew();
            var freshness = baseline is null || reevaluate
                ? (EvaluationChanged: true, ChangedFiles: (IReadOnlyList<string>)[])
                : GraphWorkspaceSession.CheckFreshness(baseline);
            freshnessTimer.Stop();

            if (!freshness.EvaluationChanged && freshness.ChangedFiles.Count == 0)
            {
                return new GraphSyncResult(current, Report(baseline!, current), false, [],
                    new GraphSyncTimings(freshnessTimer.Elapsed, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
            }

            phase = "msbuild_registration";
            _ensureMsBuildRegistered();

            phase = "solution_evaluation_and_extraction";
            var evaluationTimer = Stopwatch.StartNew();
            _sessions.TryGetValue(graphPath, out var session);
            var warm = !freshness.EvaluationChanged && session is not null &&
                       session.Revision == current.Revision && session.SolutionPath == solutionPath;

            Solution solution;
            IReadOnlyDictionary<ProjectId, IReadOnlySet<DocumentId>?> selection;
            if (warm)
            {
                (solution, selection) = await session!.ApplyTextChangesAsync(freshness.ChangedFiles, ct);
            }
            else
            {
                opened = await GraphWorkspaceSession.OpenAsync(solutionPath, ct);
                solution = opened.Solution;
                selection = await opened.SelectChangedProjectsAsync(baseline, ct);
            }

            evaluationTimer.Stop();
            var evaluationTime = warm ? TimeSpan.Zero : evaluationTimer.Elapsed;
            var active = opened ?? session!;

            var previousStates = baseline?.Projects.ToDictionary(p => p.Key, StringComparer.Ordinal)
                                 ?? new Dictionary<string, GraphProjectState>(StringComparer.Ordinal);
            var existingByProject = current.HasProvenance
                ? current.Documents.Values
                    .GroupBy(d => d.ProjectKey, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(d => d.Path, StringComparer.Ordinal), StringComparer.Ordinal)
                : new Dictionary<string, Dictionary<string, GraphDocumentRecord>>(StringComparer.Ordinal);

            var records = new List<GraphDocumentRecord>();
            var projectStates = new List<GraphProjectState>();
            var extractedDocuments = new List<string>();
            var compilationTime = warm ? evaluationTimer.Elapsed : TimeSpan.Zero;
            var extractionTime = TimeSpan.Zero;
            var projectOrder = -1;

            foreach (var project in solution.Projects)
            {
                projectOrder++;
                if (project.Language != LanguageNames.CSharp)
                {
                    projectStates.Add(await GraphWorkspaceSession.DescribeProjectAsync(project, false, null, ct));
                    continue;
                }

                var projectKey = GraphWorkspaceSession.ProjectKey(project);
                var isSelected = selection.TryGetValue(project.Id, out var selectedDocuments);
                var extraction = isSelected
                    ? await _builder.ExtractProjectAsync(project, projectKey, projectOrder, selectedDocuments, ct)
                    : null;

                if (extraction is not null)
                {
                    compilationTime += extraction.Compilation;
                    extractionTime += extraction.Extraction;
                }

                var loaded = extraction?.Loaded ?? previousStates.GetValueOrDefault(projectKey)?.Loaded ?? false;
                var existing = existingByProject.GetValueOrDefault(projectKey);
                var extractedPaths = new HashSet<string>(StringComparer.Ordinal);

                for (var documentOrder = 0; loaded && documentOrder < project.DocumentIds.Count; documentOrder++)
                {
                    var documentId = project.DocumentIds[documentOrder];
                    if (extraction is not null && extraction.Documents.TryGetValue(documentId, out var produced))
                    {
                        if (produced is null)
                            continue;

                        records.Add(produced);
                        extractedPaths.Add(produced.Path);
                        extractedDocuments.Add(produced.Path);
                        continue;
                    }

                    if (isSelected && selectedDocuments is null)
                        continue;

                    var path = Path.GetFullPath(project.GetDocument(documentId)!.FilePath!);
                    if (existing is null || !existing.TryGetValue(path, out var reused))
                        continue;

                    records.Add(reused.ProjectOrder == projectOrder && reused.DocumentOrder == documentOrder
                        ? reused
                        : reused with { ProjectOrder = projectOrder, DocumentOrder = documentOrder });
                    extractedPaths.Add(path);
                }

                projectStates.Add(await GraphWorkspaceSession.DescribeProjectAsync(project, loaded, extractedPaths.Contains, ct));
            }

            var newBaseline = new GraphBaseline(solutionPath, projectStates, active.Inputs, active.CandidateFiles, active.Diagnostics, active.SourceRoots);
            var fromEmpty = !current.HasProvenance;
            var finalKeys = records.Select(r => r.Key).ToHashSet();
            var removed = fromEmpty ? [] : current.Documents.Keys.Where(k => !finalKeys.Contains(k)).ToList();
            var upserts = records
                .Where(r => fromEmpty || !current.Documents.TryGetValue(r.Key, out var old) || !ReferenceEquals(old, r))
                .ToList();

            if (!fromEmpty && removed.Count == 0 && upserts.Count == 0 && SameBaseline(baseline!, newBaseline))
            {
                active.Advance(solution, current.Revision);
                Adopt(graphPath, active);
                opened = null;
                return new GraphSyncResult(current, Report(newBaseline, current), false, extractedDocuments,
                    new GraphSyncTimings(freshnessTimer.Elapsed, evaluationTime, compilationTime, extractionTime, TimeSpan.Zero, TimeSpan.Zero));
            }

            phase = "index_update";
            var indexTimer = Stopwatch.StartNew();
            var candidate = current.Stage(removed, upserts, newBaseline, fromEmpty);
            indexTimer.Stop();

            var report = Report(newBaseline, candidate);
            var timings = new GraphSyncTimings(freshnessTimer.Elapsed, evaluationTime, compilationTime, extractionTime, indexTimer.Elapsed, TimeSpan.Zero);
            if (report.Status is "failed" or "empty")
                return new GraphSyncResult(current, report, false, extractedDocuments, timings);

            phase = "persistence";
            var persistenceTimer = Stopwatch.StartNew();
            if (!await _registry.SaveAsync(solutionPath, candidate, version, _writeFile, ct))
            {
                if (_sessions.TryRemove(graphPath, out var stale) && !ReferenceEquals(stale, opened))
                    stale.Dispose();

                return null;
            }

            persistenceTimer.Stop();
            active.Advance(solution, candidate.Revision);
            Adopt(graphPath, active);
            opened = null;
            return new GraphSyncResult(candidate, report, true, extractedDocuments, timings with { Persistence = persistenceTimer.Elapsed });
        }
        catch (Exception ex) when (ex is not GraphSyncException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw new GraphSyncException(phase, ex);
        }
        finally
        {
            opened?.Dispose();
        }
    }

    private void Adopt(string graphPath, GraphWorkspaceSession session)
    {
        if (_sessions.TryGetValue(graphPath, out var previous) && !ReferenceEquals(previous, session))
            previous.Dispose();

        _sessions[graphPath] = session;
    }

    private static bool SameBaseline(GraphBaseline previous, GraphBaseline current) =>
        JsonSerializer.Serialize(previous, AnalysisJson.Options) == JsonSerializer.Serialize(current, AnalysisJson.Options);

    private static GraphBuildResult Report(GraphBaseline baseline, GraphStore graph)
    {
        var csharpProjects = baseline.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        var loadedProjects = csharpProjects.Count(p => p.Loaded);
        var totalDocuments = csharpProjects.Sum(p => p.Documents.Count);
        var loadedDocuments = csharpProjects.Where(p => p.Loaded).Sum(p => p.Documents.Count(d => d.Extracted));
        var unsupportedProjects = baseline.Projects.Count - csharpProjects.Count;
        var skippedDocuments = totalDocuments - loadedDocuments;
        var hasFailures = baseline.Diagnostics.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure.ToString());
        var status = loadedProjects == 0 || loadedDocuments == 0
            ? "failed"
            : graph.NodeCount == 0
                ? "empty"
                : hasFailures || unsupportedProjects > 0 || skippedDocuments > 0
                    ? "partial"
                    : "complete";

        return new GraphBuildResult(
            status,
            baseline.Projects.Count,
            loadedProjects,
            unsupportedProjects,
            csharpProjects.Count - loadedProjects,
            totalDocuments,
            loadedDocuments,
            skippedDocuments,
            graph.NodeCount,
            graph.EdgeCount,
            baseline.Diagnostics);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

public sealed class GraphSynchronizer : IGraphSynchronizer, IDisposable
{
    private readonly GraphBuilder _builder;
    private readonly GraphRegistry _registry;
    private readonly Action _ensureMsBuildRegistered;
    private readonly ConcurrentDictionary<string, GraphWorkspaceSession> _sessions = new(StringComparer.Ordinal);

    public GraphSynchronizer(GraphBuilder builder, GraphRegistry registry, Action ensureMsBuildRegistered)
    {
        _builder = builder;
        _registry = registry;
        _ensureMsBuildRegistered = ensureMsBuildRegistered;
    }

    public async Task<GraphSyncResult?> SynchronizeAsync(string solutionPath, bool reevaluate, CancellationToken ct = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        var graphPath = GraphRegistry.GetGraphPath(solutionPath);
        if (!File.Exists(solutionPath) && !File.Exists(graphPath))
            return null;

        using var writer = await _registry.AcquireWriterAsync(solutionPath, ct);
        return await SynchronizeLockedAsync(solutionPath, graphPath, reevaluate, ct);
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();

        _sessions.Clear();
    }

    private async Task<GraphSyncResult> SynchronizeLockedAsync(string solutionPath, string graphPath, bool reevaluate, CancellationToken ct)
    {
        var phase = "graph_load";
        GraphWorkspaceSession? opened = null;
        try
        {
            var current = _registry.TryLoad(solutionPath)?.Capture() ?? new GraphStore();
            var baseline = current.Baseline;

            phase = "freshness";
            var freshness = baseline is null || reevaluate
                ? (EvaluationChanged: true, ChangedFiles: (IReadOnlyList<string>)[])
                : GraphWorkspaceSession.CheckFreshness(baseline);

            if (!freshness.EvaluationChanged && freshness.ChangedFiles.Count == 0)
            {
                return new GraphSyncResult(current, Report(baseline!, current));
            }

            phase = "msbuild_registration";
            _ensureMsBuildRegistered();

            phase = "solution_evaluation_and_extraction";
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

            var active = opened ?? session!;

            var previousStates = baseline?.Projects.ToDictionary(p => p.Key, StringComparer.Ordinal)
                                 ?? new Dictionary<string, GraphProjectState>(StringComparer.Ordinal);
            var existingByProject = current.Documents.Values
                .GroupBy(d => d.ProjectKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToDictionary(d => d.Path, StringComparer.Ordinal), StringComparer.Ordinal);

            var records = new List<GraphDocumentRecord>();
            var projectStates = new List<GraphProjectState>();
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

            var newBaseline = new GraphBaseline(solutionPath, projectStates, active.Inputs, active.CandidateFiles, active.HasWorkspaceFailures, active.SourceRoots);
            var finalKeys = records.Select(r => r.Key).ToHashSet();
            var removed = current.Documents.Keys.Where(k => !finalKeys.Contains(k)).ToList();
            var upserts = records
                .Where(r => !current.Documents.TryGetValue(r.Key, out var old) || !ReferenceEquals(old, r))
                .ToList();

            if (baseline is not null && removed.Count == 0 && upserts.Count == 0 && SameBaseline(baseline, newBaseline))
            {
                active.Advance(solution, current.Revision);
                Adopt(graphPath, active);
                opened = null;
                return new GraphSyncResult(current, Report(newBaseline, current));
            }

            phase = "index_update";
            var candidate = current.Stage(removed, upserts, newBaseline);

            var report = Report(newBaseline, candidate);
            if (report.Status is "failed" or "empty")
                return new GraphSyncResult(current, report);

            phase = "persistence";
            await _registry.SaveAsync(solutionPath, candidate, ct);

            active.Advance(solution, candidate.Revision);
            Adopt(graphPath, active);
            opened = null;
            return new GraphSyncResult(candidate, report);
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
        var status = loadedProjects == 0 || loadedDocuments == 0
            ? "failed"
            : graph.NodeCount == 0
                ? "empty"
                : baseline.HasWorkspaceFailures || unsupportedProjects > 0 || skippedDocuments > 0
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
            graph.EdgeCount);
    }
}

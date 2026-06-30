namespace ContextManager.Analysis.Models;

public sealed record FileAnalysisResult(FileAnalysis? Analysis, AnalysisError? Error)
{
    public bool IsSuccess => Analysis is not null;
}

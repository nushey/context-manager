namespace ContextManager.Analysis.Models;

public sealed record FileAnalysisResult(FileAnalysis? Analysis, AnalysisError? Error);

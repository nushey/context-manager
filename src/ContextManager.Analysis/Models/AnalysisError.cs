namespace ContextManager.Analysis.Models;

public sealed record AnalysisError(string Code, string Message, string? FilePath);

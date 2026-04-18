namespace ContextManager.Analysis.Models;

// Code ∈ { "file_not_found", "not_a_cs_file", "read_failed", "parse_failed" }
public sealed record AnalysisError(string Code, string Message, string? FilePath);

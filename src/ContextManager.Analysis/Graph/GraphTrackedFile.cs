namespace ContextManager.Analysis.Graph;

public sealed record GraphTrackedFile(string Path, string Checksum, bool Extracted = false);

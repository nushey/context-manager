using Microsoft.CodeAnalysis;

namespace ContextManager.Analysis.Graph;

public sealed record GraphProjectExtraction(
    bool Loaded,
    IReadOnlyDictionary<DocumentId, GraphDocumentRecord?> Documents,
    TimeSpan Compilation,
    TimeSpan Extraction);
